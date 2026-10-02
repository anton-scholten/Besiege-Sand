using System.Collections.Generic;
using UnityEngine;

namespace SandMod
{
    /// <summary>
    /// The materials sand is drawn with, chosen once from what the game ships.
    ///
    /// Lit and opaque if at all possible: <c>Standard</c>, which Besiege's own scenes use, or
    /// failing that <c>Legacy Shaders/Diffuse</c>. An opaque lit shader has a shadow-caster
    /// pass, and that is what puts an object into the camera's depth texture -- which
    /// Besiege's screen effects (depth of field, ambient occlusion, fog) read. Sand drawn with
    /// an unlit vertex-colour shader was missing from it, so wherever a block stood in sand
    /// those effects saw the block against the floor far below: a bright halo round anything
    /// in the sand, and the machine seeming to show through it. Lit, the sand also takes the
    /// machine's shadow and casts its own. Neither shader reads vertex colours, so colour
    /// comes from a texture instead.
    ///
    /// If neither can be found the old unlit vertex-colour material is used, and the log says
    /// so.
    /// </summary>
    public static class SandLook
    {
        private static bool chosen;
        private static bool lit;
        private static Shader litShader;

        /// <summary>Whether sand is drawn lit, colour from a texture; otherwise unlit, colour
        /// from the vertices.</summary>
        public static bool Lit { get { Choose(); return lit; } }

        private static void Choose()
        {
            if (chosen) return;
            chosen = true;
            string[] names = { "Standard", "Legacy Shaders/Diffuse" };
            for (int i = 0; i < names.Length; i++)
            {
                Shader s = Shader.Find(names[i]);
                if (s != null && s.isSupported) { litShader = s; lit = true; break; }
            }
            Debug.Log("[Sand] drawing sand with " + (lit ? litShader.name + " (lit)" : "Hidden/Internal-Colored (unlit; no lit shader found)"));
        }

        /// <summary>A new material for sand: lit and opaque, or the unlit fallback.</summary>
        public static Material Make()
        {
            Choose();
            if (!lit) return DepthMaterial();
            Material m = new Material(litShader);
            m.hideFlags = HideFlags.HideAndDontSave;
            m.color = Color.white;
            if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", 0.05f);
            if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0f);
            return m;
        }

        /// <summary>
        /// An unlit vertex-colour material that writes depth. <c>Hidden/Internal-Colored</c> is
        /// Unity's own line-drawing shader, in the built-in resources every player carries, and
        /// its depth, culling and blending are all properties.
        /// </summary>
        public static Material DepthMaterial()
        {
            Shader shader = Shader.Find("Hidden/Internal-Colored");
            if (shader == null)
            {
                shader = Shader.Find("Particles/Alpha Blended");
                Material fallback = new Material(shader);
                fallback.hideFlags = HideFlags.HideAndDontSave;
                return fallback;
            }

            Material m = new Material(shader);
            m.hideFlags = HideFlags.HideAndDontSave;
            m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            m.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            m.SetFloat("_ZWrite", 1f);
            m.SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Back);
            m.renderQueue = 2450;
            return m;
        }
    }

    /// <summary>
    /// Draws the bed. Its geometry is worked out a chunk at a time -- sixteen columns square,
    /// 17 by 17 vertices on the column centres -- but drawn in tiles of four by four chunks,
    /// one mesh and one draw a tile: a level holding a hundred and fifty thousand columns of
    /// sand was six hundred draws a frame drawn a chunk at a time, and that alone was 25 ms.
    /// Only chunks whose columns changed are reworked, a few a frame, and a tile is put back
    /// together from its chunks' kept geometry when one of them has been.
    ///
    /// A column with no sand dips just below the ground, so a pile's edge runs into the floor
    /// rather than ending on a cliff. Lit, colour is a texture, 17 texels a chunk, filtered
    /// between them as vertex colours would be; unlit, it is vertex colours with the slope
    /// shaded in.
    /// </summary>
    public class SandBedRenderer
    {
        private const int RebuildsPerFrame = 8;
        private const int TileRebuildsPerFrame = 4;
        /// <summary>Past the minimums, rebuilding goes on until this much of a frame is spent.</summary>
        private const float RebuildMs = 3f;
        private const int Verts = SandBed.ChunkSide + 1;
        private const int TileSide = 4;                     // chunks a tile is across
        private const int TexSide = Verts * TileSide;       // texels a tile is across
        private const float EdgeDip = 0.02f;

        // What a chunk contributes to its tile, kept so the tile can be rebuilt without
        // reworking all sixteen.
        private class ChunkDraw
        {
            public readonly Vector3[] vertices = new Vector3[Verts * Verts];
            public readonly Vector3[] normals = new Vector3[Verts * Verts];
            public readonly Color32[] colours = new Color32[Verts * Verts];
            public readonly Color32[] texels = new Color32[Verts * Verts];
            public int[] tris = new int[0];
        }

        private class Tile
        {
            public int tx, tz;
            public Mesh mesh;
            public Texture2D colours;
            public MaterialPropertyBlock block;
            public bool dirty;
        }

        private Material material;
        private bool lit;
        private readonly Vector3 light = new Vector3(0.4f, 0.8f, 0.3f).normalized;
        private readonly Dictionary<long, Tile> tiles = new Dictionary<long, Tile>();
        private readonly List<Tile> dirtyTiles = new List<Tile>();

        private readonly float[] tops = new float[Verts * Verts];
        private readonly uint[] cols = new uint[Verts * Verts];
        private readonly bool[] has = new bool[Verts * Verts];
        private readonly bool[] known = new bool[Verts * Verts];
        private readonly int[] scratchTris = new int[SandBed.ChunkSide * SandBed.ChunkSide * 6];

        // A tile's mesh, assembled.
        private readonly List<Vector3> tVerts = new List<Vector3>();
        private readonly List<Vector3> tNormals = new List<Vector3>();
        private readonly List<Vector2> tUvs = new List<Vector2>();
        private readonly List<Color32> tColours = new List<Color32>();
        private readonly List<int> tTris = new List<int>();
        private readonly Color32[] tTexels = new Color32[TexSide * TexSide];

        /// <summary>Told of every chunk the bed has let go of, after its mesh is gone.</summary>
        public System.Action<SandBed.Chunk> OnRetire;

        /// <summary>Which medium each table row looks like, for its colour variety. Set by the
        /// field; without it there is none.</summary>
        public int[] Looks;

        public void Init()
        {
            material = SandLook.Make();
            lit = SandLook.Lit;
        }

        private static int Floor(int a, int b) { return a >= 0 ? a / b : (a + 1) / b - 1; }
        private static long Key(int x, int z) { return ((long)x << 32) | (long)(uint)z; }

        private Tile TileOf(SandBed.Chunk c, bool make)
        {
            int tx = Floor(c.cx, TileSide), tz = Floor(c.cz, TileSide);
            Tile t;
            if (tiles.TryGetValue(Key(tx, tz), out t) || !make) return t;
            t = new Tile();
            t.tx = tx; t.tz = tz;
            tiles[Key(tx, tz)] = t;
            return t;
        }

        private void MarkTile(Tile t)
        {
            if (t == null || t.dirty) return;
            t.dirty = true;
            dirtyTiles.Add(t);
        }

        public void Draw(SandBed bed, IBedGround ground, int layer)
        {
            if (material == null) return;

            for (SandBed.Chunk gone = bed.TakeRetired(); gone != null; gone = bed.TakeRetired())
            {
                gone.renderHandle = null;
                MarkTile(TileOf(gone, false));
                if (OnRetire != null) OnRetire(gone);
            }

            // At least a few a frame, and more while there is time: a big brush dirties dozens.
            System.Diagnostics.Stopwatch timer = System.Diagnostics.Stopwatch.StartNew();
            int rebuilt = 0;
            while (bed.Active && (rebuilt < RebuildsPerFrame || timer.Elapsed.TotalMilliseconds < RebuildMs))
            {
                SandBed.Chunk c = bed.TakeDirty();
                if (c == null) break;
                Rebuild(bed, ground, c);
                MarkTile(TileOf(c, true));
                rebuilt++;
            }

            // Oldest first, as the chunks are, so no tile waits behind the ones being brushed.
            int assembled = 0;
            while (dirtyTiles.Count > 0 && (assembled < TileRebuildsPerFrame || timer.Elapsed.TotalMilliseconds < RebuildMs))
            {
                Tile t = dirtyTiles[0];
                dirtyTiles.RemoveAt(0);
                t.dirty = false;
                Assemble(bed, t);
                assembled++;
            }

            foreach (KeyValuePair<long, Tile> kv in tiles)
            {
                Tile t = kv.Value;
                if (t.mesh == null) continue;
                Graphics.DrawMesh(t.mesh, Matrix4x4.identity, material, layer, null, 0, t.block, lit, lit);
            }
        }

        /// <summary>Puts a tile's mesh together from its chunks' kept geometry.</summary>
        private void Assemble(SandBed bed, Tile t)
        {
            tVerts.Clear(); tNormals.Clear(); tUvs.Clear(); tColours.Clear(); tTris.Clear();
            for (int dz = 0; dz < TileSide; dz++)
            {
                for (int dx = 0; dx < TileSide; dx++)
                {
                    SandBed.Chunk c = bed.ChunkAt(t.tx * TileSide + dx, t.tz * TileSide + dz);
                    ChunkDraw d = c != null ? c.renderHandle as ChunkDraw : null;
                    if (d == null || d.tris.Length == 0) continue;

                    int baseV = tVerts.Count;
                    tVerts.AddRange(d.vertices);
                    tNormals.AddRange(d.normals);
                    tColours.AddRange(d.colours);
                    for (int vj = 0; vj < Verts; vj++)
                        for (int vi = 0; vi < Verts; vi++)
                        {
                            int px = dx * Verts + vi, pz = dz * Verts + vj;
                            tUvs.Add(new Vector2((px + 0.5f) / TexSide, (pz + 0.5f) / TexSide));
                            if (lit) tTexels[px + pz * TexSide] = d.texels[vi + vj * Verts];
                        }
                    for (int k = 0; k < d.tris.Length; k++) tTris.Add(d.tris[k] + baseV);
                }
            }

            if (tTris.Count == 0)
            {
                if (t.mesh != null) Object.Destroy(t.mesh);
                if (t.colours != null) Object.Destroy(t.colours);
                tiles.Remove(Key(t.tx, t.tz));
                return;
            }
            if (t.mesh == null)
            {
                t.mesh = new Mesh();
                t.mesh.hideFlags = HideFlags.HideAndDontSave;
                t.mesh.MarkDynamic();
                if (lit)
                {
                    t.colours = new Texture2D(TexSide, TexSide, TextureFormat.RGBA32, false);
                    t.colours.hideFlags = HideFlags.HideAndDontSave;
                    t.colours.wrapMode = TextureWrapMode.Clamp;
                    t.colours.filterMode = FilterMode.Bilinear;
                    t.block = new MaterialPropertyBlock();
                    t.block.SetTexture("_MainTex", t.colours);
                }
            }
            if (lit)
            {
                t.colours.SetPixels32(tTexels);
                t.colours.Apply(false);
            }
            t.mesh.Clear();
            t.mesh.SetVertices(tVerts);
            t.mesh.SetNormals(tNormals);
            t.mesh.SetUVs(0, tUvs);
            t.mesh.SetColors(tColours);
            t.mesh.SetTriangles(tTris, 0);
            t.mesh.RecalculateBounds();
        }

        /// <summary>Works out one chunk's part of its tile.</summary>
        private void Rebuild(SandBed bed, IBedGround ground, SandBed.Chunk c)
        {
            ChunkDraw draw = c.renderHandle as ChunkDraw;
            if (draw == null) { draw = new ChunkDraw(); c.renderHandle = draw; }
            Vector3[] vertices = draw.vertices;
            Vector3[] normals = draw.normals;
            Color32[] colours = draw.colours;
            Color32[] texels = draw.texels;
            int baseI = c.cx * SandBed.ChunkSide, baseJ = c.cz * SandBed.ChunkSide;

            for (int vj = 0; vj < Verts; vj++)
            {
                for (int vi = 0; vi < Verts; vi++)
                {
                    int k = vi + vj * Verts;
                    int i = baseI + vi, j = baseJ + vj;
                    float top, bottom;
                    uint colour;
                    bool sand = bed.Thickness(i, j, out top, out bottom, out colour);
                    if (!sand)
                    {
                        top = ground.Height((i + 0.5f) * SandBed.Cell, (j + 0.5f) * SandBed.Cell);
                        colour = 0;
                    }
                    else if (Looks != null)
                    {
                        // A column's own shade of its medium, fixed by where it is, so a pile
                        // is speckled the way a pour of grains is, and stays the same speckle.
                        int row = bed.MaterialAt(i, j);
                        if (row >= 0 && row < Looks.Length)
                        {
                            uint h = (uint)(i * 73856093) ^ (uint)(j * 19349663);
                            h ^= h >> 13; h *= 0x5bd1e995u; h ^= h >> 15;
                            colour = SandMaterials.Vary(colour, Looks[row], (int)(h & 0xFFFF));
                        }
                    }
                    tops[k] = top;
                    cols[k] = colour;
                    has[k] = sand;
                    known[k] = top > -9000f;
                }
            }

            // An empty vertex borrows its colour from a sand neighbour, so the pile's edge is
            // sand-coloured all the way into the ground and not black.
            for (int vj = 0; vj < Verts; vj++)
            {
                for (int vi = 0; vi < Verts; vi++)
                {
                    int k = vi + vj * Verts;
                    if (has[k]) continue;
                    uint borrowed = 0;
                    for (int dj = -1; dj <= 1 && borrowed == 0; dj++)
                        for (int di = -1; di <= 1 && borrowed == 0; di++)
                        {
                            int ni = vi + di, nj = vj + dj;
                            if (ni < 0 || nj < 0 || ni >= Verts || nj >= Verts) continue;
                            if (has[ni + nj * Verts]) borrowed = cols[ni + nj * Verts];
                        }
                    cols[k] = borrowed;
                }
            }

            float spare = 0f;
            for (int k = 0; k < Verts * Verts; k++) if (known[k]) { spare = tops[k]; break; }

            for (int vj = 0; vj < Verts; vj++)
            {
                for (int vi = 0; vi < Verts; vi++)
                {
                    int k = vi + vj * Verts;
                    float y = known[k] ? (has[k] ? tops[k] + 0.006f : tops[k] - EdgeDip) : spare;
                    vertices[k] = new Vector3((baseI + vi + 0.5f) * SandBed.Cell, y,
                                              (baseJ + vj + 0.5f) * SandBed.Cell);

                    float hl = tops[Mathf.Max(vi - 1, 0) + vj * Verts];
                    float hr = tops[Mathf.Min(vi + 1, Verts - 1) + vj * Verts];
                    float hd = tops[vi + Mathf.Max(vj - 1, 0) * Verts];
                    float hu = tops[vi + Mathf.Min(vj + 1, Verts - 1) * Verts];
                    Vector3 n = new Vector3(hl - hr, 2f * SandBed.Cell, hd - hu);
                    n = n.sqrMagnitude > 1e-9f && hl > -9000f && hr > -9000f && hd > -9000f && hu > -9000f
                        ? n.normalized : Vector3.up;
                    normals[k] = n;

                    uint p = cols[k];
                    byte r = (byte)(p & 0xFF), g = (byte)((p >> 8) & 0xFF), b = (byte)((p >> 16) & 0xFF);
                    texels[k] = new Color32(r, g, b, 255);

                    // Unlit, the slope is shaded into the colour and an empty vertex fades out.
                    float shade = 0.62f + 0.38f * Mathf.Clamp01(Vector3.Dot(n, light));
                    colours[k] = new Color32((byte)(r * shade), (byte)(g * shade), (byte)(b * shade),
                                             (byte)(has[k] || lit ? 255 : 0));
                }
            }

            int t = 0;
            for (int qj = 0; qj < SandBed.ChunkSide; qj++)
            {
                for (int qi = 0; qi < SandBed.ChunkSide; qi++)
                {
                    int a = qi + qj * Verts, b = a + 1, c2 = a + Verts, d = c2 + 1;
                    if (!(has[a] || has[b] || has[c2] || has[d])) continue;
                    if (!(known[a] && known[b] && known[c2] && known[d])) continue;
                    scratchTris[t++] = a; scratchTris[t++] = c2; scratchTris[t++] = d;
                    scratchTris[t++] = a; scratchTris[t++] = d; scratchTris[t++] = b;
                }
            }
            if (draw.tris.Length != t) draw.tris = new int[t];
            System.Array.Copy(scratchTris, draw.tris, t);
        }

        public void Dispose(SandBed bed)
        {
            for (SandBed.Chunk gone = bed.TakeRetired(); gone != null; gone = bed.TakeRetired())
            {
                gone.renderHandle = null;
                if (OnRetire != null) OnRetire(gone);
            }
            foreach (KeyValuePair<long, Tile> kv in tiles)
            {
                if (kv.Value.mesh != null) Object.Destroy(kv.Value.mesh);
                if (kv.Value.colours != null) Object.Destroy(kv.Value.colours);
            }
            tiles.Clear();
            dirtyTiles.Clear();
            if (material != null) { Object.Destroy(material); material = null; }
        }
    }
}
