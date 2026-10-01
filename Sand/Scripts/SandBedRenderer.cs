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
    /// Draws the bed as one small mesh per chunk. A chunk is sixteen columns square, so
    /// its mesh is 17 by 17 vertices and rebuilding one is cheap; only chunks whose
    /// columns changed are rebuilt, a few per frame, and every chunk is drawn every
    /// frame.
    ///
    /// Vertices sit on the column centres. A column with no sand dips just below the
    /// ground, so a pile's edge runs into the floor rather than ending on a cliff. Lit, a
    /// chunk's colours are a 17 by 17 texture, one texel a vertex, filtered between them as
    /// vertex colours would be; unlit, they are vertex colours with the slope shaded in.
    /// </summary>
    public class SandBedRenderer
    {
        private const int RebuildsPerFrame = 6;
        private const int Verts = SandBed.ChunkSide + 1;
        private const float EdgeDip = 0.02f;

        private class ChunkDraw
        {
            public Mesh mesh;
            public Texture2D colours;
            public MaterialPropertyBlock block;
        }

        private Material material;
        private bool lit;
        private readonly Vector3 light = new Vector3(0.4f, 0.8f, 0.3f).normalized;

        private readonly Vector3[] vertices = new Vector3[Verts * Verts];
        private readonly Vector3[] normals = new Vector3[Verts * Verts];
        private readonly Vector2[] uvs = new Vector2[Verts * Verts];
        private readonly Color32[] colours = new Color32[Verts * Verts];
        private readonly Color32[] texels = new Color32[Verts * Verts];
        private readonly float[] tops = new float[Verts * Verts];
        private readonly uint[] cols = new uint[Verts * Verts];
        private readonly bool[] has = new bool[Verts * Verts];
        private readonly bool[] known = new bool[Verts * Verts];
        private readonly int[] scratchTris = new int[SandBed.ChunkSide * SandBed.ChunkSide * 6];

        /// <summary>Told of every chunk the bed has let go of, after its mesh is gone.</summary>
        public System.Action<SandBed.Chunk> OnRetire;

        /// <summary>Which medium each table row looks like, for its colour variety. Set by the
        /// field; without it there is none.</summary>
        public int[] Looks;

        public void Init()
        {
            material = SandLook.Make();
            lit = SandLook.Lit;
            for (int vj = 0; vj < Verts; vj++)
                for (int vi = 0; vi < Verts; vi++)
                    uvs[vi + vj * Verts] = new Vector2((vi + 0.5f) / Verts, (vj + 0.5f) / Verts);
        }

        public void Draw(SandBed bed, IBedGround ground, int layer)
        {
            if (material == null) return;

            for (SandBed.Chunk gone = bed.TakeRetired(); gone != null; gone = bed.TakeRetired())
            {
                Release(gone);
                if (OnRetire != null) OnRetire(gone);
            }

            if (!bed.Active) return;

            int rebuilt = 0;
            while (rebuilt < RebuildsPerFrame)
            {
                SandBed.Chunk c = bed.TakeDirty();
                if (c == null) break;
                Rebuild(bed, ground, c);
                rebuilt++;
            }

            foreach (SandBed.Chunk c in bed.Chunks)
            {
                ChunkDraw d = c.renderHandle as ChunkDraw;
                if (d == null) continue;
                Graphics.DrawMesh(d.mesh, Matrix4x4.identity, material, layer, null, 0, d.block, lit, lit);
            }
        }

        private void Release(SandBed.Chunk c)
        {
            ChunkDraw d = c.renderHandle as ChunkDraw;
            c.renderHandle = null;
            if (d == null) return;
            if (d.mesh != null) Object.Destroy(d.mesh);
            if (d.colours != null) Object.Destroy(d.colours);
        }

        private void Rebuild(SandBed bed, IBedGround ground, SandBed.Chunk c)
        {
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

            ChunkDraw draw = c.renderHandle as ChunkDraw;
            if (t == 0)
            {
                Release(c);
                return;
            }
            if (draw == null)
            {
                draw = new ChunkDraw();
                draw.mesh = new Mesh();
                draw.mesh.hideFlags = HideFlags.HideAndDontSave;
                draw.mesh.MarkDynamic();
                if (lit)
                {
                    draw.colours = new Texture2D(Verts, Verts, TextureFormat.RGBA32, false);
                    draw.colours.hideFlags = HideFlags.HideAndDontSave;
                    draw.colours.wrapMode = TextureWrapMode.Clamp;
                    draw.colours.filterMode = FilterMode.Bilinear;
                    draw.block = new MaterialPropertyBlock();
                    draw.block.SetTexture("_MainTex", draw.colours);
                }
                c.renderHandle = draw;
            }

            if (lit)
            {
                draw.colours.SetPixels32(texels);
                draw.colours.Apply(false);
            }

            int[] tris = new int[t];
            System.Array.Copy(scratchTris, tris, t);
            draw.mesh.Clear();
            draw.mesh.vertices = vertices;
            draw.mesh.normals = normals;
            draw.mesh.uv = uvs;
            draw.mesh.colors32 = colours;
            draw.mesh.triangles = tris;
            draw.mesh.RecalculateBounds();
        }

        public void Dispose(SandBed bed)
        {
            foreach (SandBed.Chunk c in bed.Chunks) Release(c);
            for (SandBed.Chunk gone = bed.TakeRetired(); gone != null; gone = bed.TakeRetired())
            {
                Release(gone);
                if (OnRetire != null) OnRetire(gone);
            }
            if (material != null) { Object.Destroy(material); material = null; }
        }
    }
}
