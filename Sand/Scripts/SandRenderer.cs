using System.Collections.Generic;
using UnityEngine;

namespace SandMod
{
    /// <summary>
    /// Draws the medium as camera-facing quads.
    ///
    /// No shader of the mod's own is needed for this: <c>Particles/Alpha Blended</c>
    /// ships in the player (checked), takes vertex colours and a main texture, and is
    /// reachable through <c>Shader.Find</c> -- which only finds shaders that shipped
    /// in the player's build, so a stock name is the only kind worth asking for.
    /// That is why colour and texture work with no asset bundle and no Unity licence,
    /// while the compute solver still waits for one.
    ///
    /// A mesh in Unity 5.4 has 16-bit indices, so 65535 vertices and 16383 quads. More
    /// grains than that is more meshes, which is why this keeps a list.
    /// </summary>
    public class SandRenderer
    {
        private const int QuadsPerMesh = 16000;

        private readonly List<Mesh> meshes = new List<Mesh>();
        private readonly List<int> meshQuads = new List<int>();
        private Material material;

        // Sized to what is actually drawn, not to the cap. Uploading a full
        // 16000-quad mesh every frame to show four hundred grains was the single
        // most expensive thing this mod did.
        private Vector3[] verts = new Vector3[0];
        private Vector2[] uvs = new Vector2[0];
        private Color32[] cols = new Color32[0];
        private int[] tris = new int[0];
        private int capacityQuads;

        private Color[] tints;

        public void Init(SandMaterials.Params[] table)
        {
            Shader shader = Shader.Find("Particles/Alpha Blended");
            if (shader == null) shader = Shader.Find("Particles/Additive");
            material = new Material(shader);
            material.hideFlags = HideFlags.HideAndDontSave;

            Texture atlas = null;
            try
            {
                atlas = Modding.ModResource.GetTexture("SandAtlas").Texture;
            }
            catch (System.Exception)
            {
                // No atlas shipped yet: a flat quad tinted by the material still reads
                // as a grain at the sizes these are drawn.
            }
            if (atlas != null) material.mainTexture = atlas;

            tints = SandMaterials.Tints;
        }

        /// <summary>
        /// Grows the scratch arrays to hold a batch. Grows in steps rather than to the
        /// exact count so a sprayer adding a few grains a frame does not reallocate
        /// every frame, and never shrinks -- the peak is what the level needs.
        /// </summary>
        private void Reserve(int quads)
        {
            if (quads <= capacityQuads) return;

            int want = Mathf.Min(QuadsPerMesh, Mathf.NextPowerOfTwo(Mathf.Max(quads, 256)));
            verts = new Vector3[want * 4];
            uvs = new Vector2[want * 4];
            cols = new Color32[want * 4];
            tris = new int[want * 6];
            for (int q = 0; q < want; q++)
            {
                int vi = q * 4, ti = q * 6;
                tris[ti + 0] = vi; tris[ti + 1] = vi + 1; tris[ti + 2] = vi + 2;
                tris[ti + 3] = vi; tris[ti + 4] = vi + 2; tris[ti + 5] = vi + 3;
            }
            capacityQuads = want;
        }

        /// <summary>
        /// Rebuilds the quads from the grains and draws them. Called once per frame,
        /// not once per sand step, so the medium does not judder between steps.
        /// </summary>
        public void Draw(Grain[] grains, int count, SandMaterials.Params[] table,
                         int[] variantBase, int layer)
        {
            if (material == null || grains == null || count <= 0) return;

            // Camera.main is re-resolved rather than cached: Besiege swaps cameras
            // between the build area, a level and the level editor, and anything that
            // latched one in Start stops working the moment it does.
            Camera cam = Camera.main;
            if (cam == null) return;
            Transform ct = cam.transform;
            Vector3 right = ct.right;
            Vector3 up = ct.up;

            int drawn = 0;
            int meshIndex = 0;
            while (drawn < count)
            {
                int batch = Mathf.Min(QuadsPerMesh, count - drawn);
                Reserve(batch);

                for (int i = 0; i < batch; i++)
                {
                    Grain g = grains[drawn + i];
                    int mat = (int)(g.material & SandFormat.MaterialMask);
                    float r = table[mat].radius;
                    // A variant row borrows the look of the medium it came from, so a
                    // customised sand is still drawn as sand.
                    int look = variantBase[mat];

                    Vector3 a = right * r;
                    Vector3 b = up * r;
                    int vi = i * 4;
                    verts[vi + 0] = g.pos - a - b;
                    verts[vi + 1] = g.pos - a + b;
                    verts[vi + 2] = g.pos + a + b;
                    verts[vi + 3] = g.pos + a - b;

                    // Two by two atlas: the tile is chosen by material, so sand is
                    // speckle and dust is a soft blob without a second draw call.
                    int tile = SandMaterials.Tiles[look];
                    float u = (tile & 1) * 0.5f;
                    float v = (tile >> 1) * 0.5f;
                    uvs[vi + 0] = new Vector2(u, v);
                    uvs[vi + 1] = new Vector2(u, v + 0.5f);
                    uvs[vi + 2] = new Vector2(u + 0.5f, v + 0.5f);
                    uvs[vi + 3] = new Vector2(u + 0.5f, v);

                    Color32 c = Tint(g, look);
                    cols[vi + 0] = c; cols[vi + 1] = c; cols[vi + 2] = c; cols[vi + 3] = c;
                }

                // Anything the batch did not fill is collapsed to a point, so the
                // previous frame's grains cannot smear across the level.
                for (int i = batch; i < capacityQuads; i++)
                {
                    int vi = i * 4;
                    verts[vi + 0] = Vector3.zero; verts[vi + 1] = Vector3.zero;
                    verts[vi + 2] = Vector3.zero; verts[vi + 3] = Vector3.zero;
                }

                Mesh mesh = MeshAt(meshIndex);
                mesh.vertices = verts;
                mesh.uv = uvs;
                mesh.colors32 = cols;

                // Triangles never change for a given array size, and assigning them is
                // what forces Unity to rebuild the index buffer. Only write them when
                // the arrays were actually resized.
                if (meshQuads[meshIndex] != capacityQuads)
                {
                    mesh.triangles = tris;
                    meshQuads[meshIndex] = capacityQuads;
                }
                mesh.RecalculateBounds();

                Graphics.DrawMesh(mesh, Matrix4x4.identity, material, layer);

                drawn += batch;
                meshIndex++;
            }
        }

        /// <summary>
        /// The colour of one grain: what the emitter set, darkened while wet. The
        /// medium's own tint is not multiplied in here -- the emitter's colour control
        /// already starts at it and follows it, so doing both would tint twice and a
        /// white snow would come out sand-coloured.
        /// </summary>
        private Color32 Tint(Grain g, int mat)
        {
            uint packed = g.colour;
            float r = (packed & 0xFF) / 255f;
            float gr = ((packed >> 8) & 0xFF) / 255f;
            float b = ((packed >> 16) & 0xFF) / 255f;
            float a = ((packed >> 24) & 0xFF) / 255f;

            float wet = (g.material & SandFormat.FlagWet) != 0 ? 0.65f : 1f;
            return new Color(r * wet, gr * wet, b * wet, a);
        }

        private Mesh MeshAt(int index)
        {
            while (meshes.Count <= index)
            {
                Mesh m = new Mesh();
                m.hideFlags = HideFlags.HideAndDontSave;
                m.MarkDynamic();
                meshes.Add(m);
                meshQuads.Add(-1);
            }
            return meshes[index];
        }

        public void Dispose()
        {
            for (int i = 0; i < meshes.Count; i++)
            {
                if (meshes[i] != null) Object.Destroy(meshes[i]);
            }
            meshes.Clear();
            if (material != null) { Object.Destroy(material); material = null; }
        }
    }
}
