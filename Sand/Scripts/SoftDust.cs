using UnityEngine;

namespace SandMod
{
    /// <summary>
    /// Dust drawn soft: each grain of dust the solver carries is a camera-facing square of a blurred round puff,
    /// alpha-blended, a few times its grain across, so a stream of dust reads as a haze rather than a spray of hard
    /// discs. Moving grains of every other medium stay the lit disc they are (SandRenderer), which is how sand, snow
    /// and gravel look up close. (The spray that is only drawn is Unity's own particles: SandSpray.)
    ///
    /// The material is the native puffs' (<see cref="NativePuffs.Material"/>): the stock <c>Particles/Alpha Blended</c>
    /// with a neutral grey tint, so the puff is the grain's own colour, on a white disc whose alpha falls off smoothly
    /// to nothing at the edge. Four
    /// vertices a grain, where a lit disc is nine, so the cost is the overdraw of the soft edges, which is only where a
    /// cloud is thick and close.
    /// </summary>
    public class SoftDust
    {
        private const int PerMesh = 16000;      // 16-bit indices: 65535 vertices, four a puff
        private const float Across = 2.6f;      // the puff's half-width, in grain radii
        private const byte Opacity = 150;       // at its middle

        private Material material;
        private readonly System.Collections.Generic.List<Mesh> meshes = new System.Collections.Generic.List<Mesh>();
        private readonly System.Collections.Generic.List<int> meshQuads = new System.Collections.Generic.List<int>();

        // What was added this frame.
        private Vector3[] pos = new Vector3[0];
        private float[] radius = new float[0];
        private Color32[] colour = new Color32[0];
        private byte[] turn = new byte[0];
        private int count;

        // The mesh being built, sized to the batch.
        private Vector3[] verts = new Vector3[0];
        private Vector2[] uvs = new Vector2[0];
        private Color32[] cols = new Color32[0];
        private int[] tris = new int[0];
        private int capacity;

        private static readonly float[] TurnCos = new float[16], TurnSin = new float[16];

        public bool Ready { get { return material != null; } }

        public void Init()
        {
            // The same look as the native puffs: one texture and one material for both.
            material = NativePuffs.Material;
            if (material == null) return;
            for (int k = 0; k < 16; k++) { TurnCos[k] = Mathf.Cos(k * Mathf.PI / 16f); TurnSin[k] = Mathf.Sin(k * Mathf.PI / 16f); }
        }

        public void Begin() { count = 0; }

        /// <summary>A grain of dust at <paramref name="p"/>, <paramref name="r"/> its radius, in its colour; <paramref name="look"/>
        /// turns its puff, so neighbours do not line up.</summary>
        public void Add(Vector3 p, float r, byte red, byte green, byte blue, uint look)
        {
            if (count == pos.Length)
            {
                int grow = Mathf.Max(256, pos.Length * 2);
                System.Array.Resize(ref pos, grow);
                System.Array.Resize(ref radius, grow);
                System.Array.Resize(ref colour, grow);
                System.Array.Resize(ref turn, grow);
            }
            pos[count] = p;
            radius[count] = r;
            colour[count] = new Color32(red, green, blue, Opacity);
            turn[count] = (byte)((look >> 6) & 15);
            count++;
        }

        private void Reserve(int quads)
        {
            if (quads <= capacity) return;
            int want = Mathf.Min(PerMesh, Mathf.NextPowerOfTwo(Mathf.Max(quads, 256)));
            verts = new Vector3[want * 4];
            uvs = new Vector2[want * 4];
            cols = new Color32[want * 4];
            tris = new int[want * 6];
            for (int q = 0; q < want; q++)
            {
                int vi = q * 4, ti = q * 6;
                tris[ti] = vi; tris[ti + 1] = vi + 1; tris[ti + 2] = vi + 2;
                tris[ti + 3] = vi; tris[ti + 4] = vi + 2; tris[ti + 5] = vi + 3;
                uvs[vi] = new Vector2(0f, 0f); uvs[vi + 1] = new Vector2(0f, 1f);
                uvs[vi + 2] = new Vector2(1f, 1f); uvs[vi + 3] = new Vector2(1f, 0f);
            }
            capacity = want;
        }

        /// <summary>Draws what was added since <see cref="Begin"/>, facing the camera whose axes are given.</summary>
        public void End(Vector3 right, Vector3 up, int layer)
        {
            if (material == null) return;
            int drawn = 0, meshIndex = 0;
            while (drawn < count)
            {
                int batch = Mathf.Min(PerMesh, count - drawn);
                Reserve(batch);
                for (int q = 0; q < batch; q++)
                {
                    int i = drawn + q, vi = q * 4;
                    int k = turn[i];
                    float s = radius[i] * Across;
                    Vector3 a = (right * TurnCos[k] + up * TurnSin[k]) * s;
                    Vector3 b = (up * TurnCos[k] - right * TurnSin[k]) * s;
                    Vector3 p = pos[i];
                    verts[vi] = p - a - b;
                    verts[vi + 1] = p - a + b;
                    verts[vi + 2] = p + a + b;
                    verts[vi + 3] = p + a - b;
                    Color32 c = colour[i];
                    cols[vi] = c; cols[vi + 1] = c; cols[vi + 2] = c; cols[vi + 3] = c;
                }
                for (int v = batch * 4; v < capacity * 4; v++) verts[v] = Vector3.zero;

                while (meshes.Count <= meshIndex)
                {
                    Mesh m = new Mesh();
                    m.hideFlags = HideFlags.HideAndDontSave;
                    m.MarkDynamic();
                    meshes.Add(m);
                    meshQuads.Add(-1);
                }
                Mesh mesh = meshes[meshIndex];
                if (meshQuads[meshIndex] != capacity) mesh.Clear();
                mesh.vertices = verts;
                mesh.colors32 = cols;
                if (meshQuads[meshIndex] != capacity)
                {
                    mesh.uv = uvs;
                    mesh.triangles = tris;
                    meshQuads[meshIndex] = capacity;
                }
                mesh.RecalculateBounds();
                Graphics.DrawMesh(mesh, Matrix4x4.identity, material, layer, null, 0, null, false, false);
                drawn += batch;
                meshIndex++;
            }
        }

        public void Dispose()
        {
            for (int i = 0; i < meshes.Count; i++) if (meshes[i] != null) Object.Destroy(meshes[i]);
            meshes.Clear();
            meshQuads.Clear();
            capacity = 0;
            material = null;   // shared (NativePuffs), not ours to destroy
        }
    }
}
