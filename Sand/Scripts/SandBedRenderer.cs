using System.Collections.Generic;
using UnityEngine;

namespace SandMod
{
    /// <summary>
    /// Draws the bed as one small mesh per chunk. A chunk is sixteen columns square, so
    /// its mesh is 17 by 17 vertices and rebuilding one is cheap; only chunks whose
    /// columns changed are rebuilt, a few per frame, and every chunk is drawn every
    /// frame.
    ///
    /// Vertices sit on the column centres. A column with no sand takes the ground's
    /// height and is fully transparent, so the edge of a pile fades into the ground
    /// instead of ending on a cliff. Shading is faked from the slope in the vertex
    /// colour -- the shader in use, <c>Particles/Alpha Blended</c>, is unlit.
    /// </summary>
    public class SandBedRenderer
    {
        private const int RebuildsPerFrame = 6;
        private const int Verts = SandBed.ChunkSide + 1;

        private Material material;
        private readonly Vector3 light = new Vector3(0.4f, 0.8f, 0.3f).normalized;

        private readonly Vector3[] vertices = new Vector3[Verts * Verts];
        private readonly Color32[] colours = new Color32[Verts * Verts];
        private readonly float[] tops = new float[Verts * Verts];
        private readonly uint[] cols = new uint[Verts * Verts];
        private readonly bool[] has = new bool[Verts * Verts];
        private readonly bool[] known = new bool[Verts * Verts];
        private readonly int[] scratchTris = new int[SandBed.ChunkSide * SandBed.ChunkSide * 6];

        public void Init()
        {
            material = DepthMaterial();
        }

        /// <summary>
        /// An unlit vertex-colour material that writes depth. <c>Particles/Alpha Blended</c>,
        /// which this used to draw with, does not: with no depth to sort by, the far slope of
        /// a pile was drawn over the near one wherever its triangles came later, and a heap
        /// read as a hollow sunk into the ground. <c>Hidden/Internal-Colored</c> is Unity's
        /// own line-drawing shader, in the built-in resources every player carries, and its
        /// depth, culling and blending are all properties.
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
            // After the level's opaque geometry, so the faded rim of a pile blends over the
            // ground it lies on; before anything transparent, so water and glass in front of
            // the sand sort against the depth it wrote.
            m.renderQueue = 2450;
            return m;
        }

        public void Draw(SandBed bed, IBedGround ground, int layer)
        {
            if (material == null) return;

            for (SandBed.Chunk gone = bed.TakeRetired(); gone != null; gone = bed.TakeRetired())
            {
                Mesh old = gone.renderHandle as Mesh;
                if (old != null) Object.Destroy(old);
                gone.renderHandle = null;
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
                Mesh m = c.renderHandle as Mesh;
                if (m != null) Graphics.DrawMesh(m, Matrix4x4.identity, material, layer);
            }
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
                    tops[k] = top;
                    cols[k] = colour;
                    has[k] = sand;
                    known[k] = top > -9000f;
                }
            }

            // An empty vertex borrows its colour from a sand neighbour, so the fade at
            // a pile's edge is sand-coloured and not black.
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

            for (int vj = 0; vj < Verts; vj++)
            {
                for (int vi = 0; vi < Verts; vi++)
                {
                    int k = vi + vj * Verts;
                    float y = known[k] ? tops[k] + 0.006f : 0f;
                    vertices[k] = new Vector3((baseI + vi + 0.5f) * SandBed.Cell, y,
                                              (baseJ + vj + 0.5f) * SandBed.Cell);

                    // Slope shading from the neighbouring tops.
                    float hl = tops[Mathf.Max(vi - 1, 0) + vj * Verts];
                    float hr = tops[Mathf.Min(vi + 1, Verts - 1) + vj * Verts];
                    float hd = tops[vi + Mathf.Max(vj - 1, 0) * Verts];
                    float hu = tops[vi + Mathf.Min(vj + 1, Verts - 1) * Verts];
                    Vector3 n = new Vector3(hl - hr, 2f * SandBed.Cell, hd - hu);
                    float shade = n.sqrMagnitude > 1e-9f
                        ? 0.62f + 0.38f * Mathf.Clamp01(Vector3.Dot(n.normalized, light))
                        : 1f;

                    uint p = cols[k];
                    float a = has[k] ? 1f : 0f;
                    colours[k] = new Color((p & 0xFF) / 255f * shade,
                                           ((p >> 8) & 0xFF) / 255f * shade,
                                           ((p >> 16) & 0xFF) / 255f * shade, a);
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

            Mesh mesh = c.renderHandle as Mesh;
            if (t == 0)
            {
                if (mesh != null) { Object.Destroy(mesh); c.renderHandle = null; }
                return;
            }
            if (mesh == null)
            {
                mesh = new Mesh();
                mesh.hideFlags = HideFlags.HideAndDontSave;
                mesh.MarkDynamic();
                c.renderHandle = mesh;
            }

            int[] tris = new int[t];
            System.Array.Copy(scratchTris, tris, t);
            mesh.Clear();
            mesh.vertices = vertices;
            mesh.colors32 = colours;
            mesh.triangles = tris;
            mesh.RecalculateBounds();
        }

        public void Dispose(SandBed bed)
        {
            foreach (SandBed.Chunk c in bed.Chunks)
            {
                Mesh m = c.renderHandle as Mesh;
                if (m != null) Object.Destroy(m);
                c.renderHandle = null;
            }
            for (SandBed.Chunk gone = bed.TakeRetired(); gone != null; gone = bed.TakeRetired())
            {
                Mesh old = gone.renderHandle as Mesh;
                if (old != null) Object.Destroy(old);
            }
            if (material != null) { Object.Destroy(material); material = null; }
        }
    }
}
