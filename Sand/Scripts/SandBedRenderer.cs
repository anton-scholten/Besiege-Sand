using System.Collections.Generic;
using UnityEngine;

namespace SandMod
{
    /// <summary>
    /// The materials sand is drawn with, chosen once from what the game ships.
    ///
    /// Lit and opaque if at all possible: <c>Legacy Shaders/Diffuse</c> (sand is matte: the
    /// Standard shader's specular and fresnel show every 12 cm facet as a square), or failing that
    /// <c>Mobile/Diffuse</c> or <c>Standard</c>. An opaque lit shader has a shadow-caster
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
            // Sand is matte. The Standard shader is a physically based one, with a fresnel rim and specular
            // glints, and on a surface of 12 cm facets lit from a vertex-interpolated normal those show every
            // facet as a square: a grid over the whole of the sand. A plain Lambert diffuse shows none.
            string[] names = { "Legacy Shaders/Diffuse", "Mobile/Diffuse", "Standard" };
            for (int i = 0; i < names.Length; i++)
            {
                Shader s = Shader.Find(names[i]);
                if (s != null && s.isSupported) { litShader = s; lit = true; break; }
            }
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
        private readonly System.Diagnostics.Stopwatch timer = new System.Diagnostics.Stopwatch();
        private const int Verts = ChunkDraw.Verts;
        private const int TileSide = 4;                     // chunks a tile is across
        private const int TexSide = Verts * TileSide;       // texels a tile is across
        private const float EdgeDip = 0.02f;

        private class Tile
        {
            public int tx, tz;
            public Mesh mesh;
            public Texture2D colours;
            public MaterialPropertyBlock block;
            public bool dirty;
            public bool urgent;          // has a chunk that is new or emptied: not held back by RebuildEvery
            public float assembledAt = -1f;
        }

        /// <summary>Seconds a chunk or a tile that keeps changing waits between being drawn again.</summary>
        private const float RebuildEvery = 0.1f;
        private readonly List<SandBed.Chunk> later = new List<SandBed.Chunk>();

        private Material material;
        private bool lit;
        private readonly Dictionary<long, Tile> tiles = new Dictionary<long, Tile>();
        private readonly List<Tile> dirtyTiles = new List<Tile>();

        private readonly ChunkMesherPool pool = new ChunkMesherPool();
        private readonly List<SandBed.Chunk> batch = new List<SandBed.Chunk>();
        private readonly List<bool> batchNew = new List<bool>();

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

        private void MarkTile(Tile t, bool first)
        {
            if (t == null) return;
            if (first) t.urgent = true;
            if (t.dirty)
            {
                // Already waiting: a tile with a chunk that has never been drawn moves up to the front.
                if (first && dirtyTiles.Count > 0 && dirtyTiles[0] != t) { dirtyTiles.Remove(t); dirtyTiles.Insert(0, t); }
                return;
            }
            t.dirty = true;
            if (first) dirtyTiles.Insert(0, t); else dirtyTiles.Add(t);
        }

        public void Draw(SandBed bed, IBedGround ground, int layer)
        {
            if (material == null) return;

            for (SandBed.Chunk gone = bed.TakeRetired(); gone != null; gone = bed.TakeRetired())
            {
                gone.renderHandle = null;
                MarkTile(TileOf(gone, false), false);
                if (OnRetire != null) OnRetire(gone);
            }

            // At least a few a frame, and more while there is time: a big brush dirties dozens. Worked
            // out several at a time on every core there is, and the main thread only puts tiles together.
            // The timer is a field: this runs every frame, and a Stopwatch a frame is an allocation
            // a frame for nothing.
            timer.Reset(); timer.Start();   // Reset first: Start() does nothing to a running watch
            int rebuilt = 0;
            float now = Time.unscaledTime;
            float turn = RebuildMs + Mathf.Min(1f, bed.DirtyCount / 256f);
            int lot = pool.Threads * 4;
            // While anything is waiting, sand or not: a bed emptied by an undo still has its last surface to take down,
            // and asking for sand here left it on screen.
            while (bed.DirtyCount > 0 && (rebuilt < RebuildsPerFrame || timer.Elapsed.TotalMilliseconds < turn))
            {
                batch.Clear(); batchNew.Clear();
                while (batch.Count < lot)
                {
                    SandBed.Chunk c = bed.TakeDirty();
                    if (c == null) break;
                    // Never drawn, or now empty: its tile goes to the front, so what appears or goes does so at once.
                    // Under a wheel pressing it, too: the rut is drawn as it forms, not up to a tenth of a second later.
                    bool urgent = c.renderHandle == null || c.filled == 0 || bed.PressedNow(c);
                    // A chunk changing all the time -- under a wheel -- is drawn again at most every RebuildEvery
                    // seconds: every frame was a mesh a frame for each of them, and the tiles round them with it.
                    ChunkDraw drawn = c.renderHandle as ChunkDraw;
                    if (!urgent && drawn != null && now - drawn.builtAt < RebuildEvery) { later.Add(c); continue; }
                    if (drawn != null) drawn.builtAt = now;
                    batchNew.Add(urgent);
                    batch.Add(c);
                }
                if (batch.Count == 0) break;
                pool.Run(bed, ground, batch, batch.Count, Looks, lit);
                for (int n = 0; n < batch.Count; n++)
                {
                    ChunkDraw built = batch[n].renderHandle as ChunkDraw;
                    if (built != null) built.builtAt = now;
                    MarkTile(TileOf(batch[n], true), batchNew[n]);
                }
                rebuilt += batch.Count;
            }
            // Back in the queue for a later frame.
            for (int n = 0; n < later.Count; n++) bed.Requeue(later[n]);
            later.Clear();

            // Oldest first, as the chunks are, so no tile waits behind the ones being brushed.
            // A tile put together less than RebuildEvery ago waits, unless it has a chunk new or emptied.
            int assembled = 0;
            for (int n = 0; n < dirtyTiles.Count && (assembled < TileRebuildsPerFrame || timer.Elapsed.TotalMilliseconds < turn + 1f); )
            {
                Tile t = dirtyTiles[n];
                if (!t.urgent && now - t.assembledAt < RebuildEvery) { n++; continue; }
                dirtyTiles.RemoveAt(n);
                t.dirty = false;
                t.urgent = false;
                t.assembledAt = now;
                Assemble(bed, t);
                assembled++;
            }

            // Only the tiles in front of the camera: on a big level hundreds of tiles
            // were handed to Unity every frame, a few microseconds each, to be culled
            // there. The view was widened by 30 m for the shadows the bed used to cast;
            // it casts none now (below), so a tile out of sight is one no camera shows.
            Camera cam = Camera.main;
            bool cull = cam != null;
            Plane[] planes = null;
            if (cull) planes = GeometryUtility.CalculateFrustumPlanes(cam);   // Unity 5.4 has only the one that makes an array
            foreach (KeyValuePair<long, Tile> kv in tiles)
            {
                Tile t = kv.Value;
                if (t.mesh == null) continue;
                if (cull && !GeometryUtility.TestPlanesAABB(planes, t.mesh.bounds)) continue;
                Graphics.DrawMesh(t.mesh, Matrix4x4.identity, material, layer, null, 0, t.block, false, lit);
            }
        }

        /// <summary>Draws the sand as it stands for one camera only, now: for a camera rendered by hand, which the
        /// frame's own draws may not have reached yet (the thumbnail of a sand file).</summary>
        public void DrawFor(Camera cam, int layer)
        {
            if (material == null || cam == null) return;
            foreach (KeyValuePair<long, Tile> kv in tiles)
            {
                Tile t = kv.Value;
                if (t.mesh == null) continue;
                Graphics.DrawMesh(t.mesh, Matrix4x4.identity, material, layer, cam, 0, t.block, false, lit);
            }
        }

        // Where each chunk of a tile sits in the tile's colour texture, worked out once.
        private readonly Vector2[][] chunkUvs = new Vector2[TileSide * TileSide][];

        private Vector2[] ChunkUvs(int dx, int dz)
        {
            Vector2[] uv = chunkUvs[dx + dz * TileSide];
            if (uv != null) return uv;
            uv = new Vector2[Verts * Verts];
            for (int vj = 0; vj < Verts; vj++)
                for (int vi = 0; vi < Verts; vi++)
                    uv[vi + vj * Verts] = new Vector2((dx * Verts + vi + 0.5f) / TexSide, (dz * Verts + vj + 0.5f) / TexSide);
            chunkUvs[dx + dz * TileSide] = uv;
            return uv;
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
                    tUvs.AddRange(ChunkUvs(dx, dz));
                    if (lit)
                        for (int vj = 0; vj < Verts; vj++)
                        {
                            int rowAt = dx * Verts + (dz * Verts + vj) * TexSide;
                            System.Array.Copy(d.texels, vj * Verts, tTexels, rowAt, Verts);
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
                    t.colours = new Texture2D(TexSide, TexSide, TextureFormat.RGBA32, true);   // mipmapped: a speckle a column across, seen from afar, shimmers into a grid without them
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
                t.colours.Apply(true);
            }
            t.mesh.Clear();
            t.mesh.SetVertices(tVerts);
            t.mesh.SetNormals(tNormals);
            t.mesh.SetUVs(0, tUvs);
            t.mesh.SetColors(tColours);
            t.mesh.SetTriangles(tTris, 0);
            t.mesh.RecalculateBounds();
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
