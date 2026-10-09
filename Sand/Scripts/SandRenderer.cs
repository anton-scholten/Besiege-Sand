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
    /// That is why colour and texture work with no asset bundle and no Unity licence.
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

        // Dust grains, drawn soft instead (SoftDust).
        private readonly SoftDust soft = new SoftDust();
        private bool[] rowSoft = new bool[0];

        // Sized to what is actually drawn, not to the cap. Uploading a full
        // 16000-quad mesh every frame to show four hundred grains was the single
        // most expensive thing this mod did.
        private Vector3[] verts = new Vector3[0];
        private Vector2[] uvs = new Vector2[0];
        private Vector3[] norms = new Vector3[0];

        // Lit: grain colours as texels of a palette, filled as new colours turn up.
        private bool lit;
        private const int PaletteSide = 64;
        private Texture2D palette;
        private readonly Color32[] paletteTexels = new Color32[PaletteSide * PaletteSide];
        private readonly Dictionary<uint, int> paletteIndex = new Dictionary<uint, int>();
        private bool paletteDirty;
        private uint lastColour = 0xFFFFFFFFu;
        private Vector2 lastUv;
        private bool haveLast;

        /// <summary>
        /// The palette texel for a colour, adding it if it is new. Colours are kept to five bits
        /// a channel, which no eye tells apart on a grain, and the palette is emptied at the start
        /// of a frame once it is half full: the grains' mesh is rebuilt every frame, so nothing
        /// still points at the old texels. It used to fill once and for all -- sand that mixes
        /// blends its colours, and every blend is a new one -- after which every new colour was
        /// drawn as the first one ever added, which was sand's: mud splashed sand-coloured.
        /// </summary>
        private Vector2 PaletteUv(uint colour)
        {
            colour &= 0xF8F8F8u;
            if (haveLast && colour == lastColour) return lastUv;
            int index;
            if (!paletteIndex.TryGetValue(colour, out index))
            {
                // Full within a frame (thousands of distinct colours at once): the grain takes
                // the last grain's colour, a neighbour in a pour, rather than a stranger's.
                if (paletteIndex.Count >= PaletteSide * PaletteSide) return haveLast ? lastUv : Vector2.zero;
                index = paletteIndex.Count;
                paletteIndex[colour] = index;
                paletteTexels[index] = new Color32((byte)((colour & 0xFF) | 4), (byte)(((colour >> 8) & 0xFF) | 4),
                                                   (byte)(((colour >> 16) & 0xFF) | 4), 255);
                paletteDirty = true;
            }
            lastColour = colour;
            haveLast = true;
            lastUv = new Vector2(((index % PaletteSide) + 0.5f) / PaletteSide, ((index / PaletteSide) + 0.5f) / PaletteSide);
            return lastUv;
        }
        private Color32[] cols = new Color32[0];
        private int[] tris = new int[0];
        private int capacityQuads;


        public void Init(SandMaterials.Params[] table)
        {
            soft.Init();
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

            if (atlas != null)
            {
                // Sprites need a textured shader, and the stock one that takes vertex
                // colours does not write depth: grains then draw in array order, not
                // nearest first.
                Shader shader = Shader.Find("Particles/Alpha Blended");
                if (shader == null) shader = Shader.Find("Particles/Additive");
                material = new Material(shader);
                material.hideFlags = HideFlags.HideAndDontSave;
                material.mainTexture = atlas;
            }
            else
            {
                // Flat quads with depth, lit like the bed under them when a lit shader is to
                // be had (see SandLook): a grain's colour is a texel of a small palette and its
                // normal points up, so loose sand looks like the pile it came from instead of
                // a brighter, unlit speckle round everything that touches it.
                material = SandLook.Make();
                lit = SandLook.Lit;
                if (lit)
                {
                    palette = new Texture2D(PaletteSide, PaletteSide, TextureFormat.RGBA32, false);
                    palette.hideFlags = HideFlags.HideAndDontSave;
                    palette.filterMode = FilterMode.Point;
                    palette.wrapMode = TextureWrapMode.Clamp;
                    material.mainTexture = palette;
                }
                else material.SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Off);
            }

        }

        // A grain is drawn lit as a little disc that shades like a ball -- a centre facing the camera and a rim of
        // eight leaning out -- and unlit as a flat sprite: nine vertices or four.
        private int VertsPer { get { return lit ? 9 : 4; } }
        private int TrisPer { get { return lit ? 8 : 2; } }
        private int PerMesh { get { return lit ? 7200 : QuadsPerMesh; } }

        /// <summary>
        /// Grows the scratch arrays to hold a batch. Grows in steps rather than to the
        /// exact count so a sprayer adding a few grains a frame does not reallocate
        /// every frame, and never shrinks -- the peak is what the level needs.
        /// </summary>
        // Frames the batch has needed a quarter of the arrays or less: after a big throw the arrays stayed at its size
        // and every frame sent the whole of them, so a handful of grains cost what the throw had.
        private int smallFrames;

        private void Reserve(int quads)
        {
            if (quads <= capacityQuads)
            {
                if (capacityQuads <= 256 || quads * 4 > capacityQuads) { smallFrames = 0; return; }
                if (++smallFrames < 120) return;
                smallFrames = 0;
                capacityQuads = 0;   // made again at the size wanted now; each mesh sees the new size and is cleared
            }

            int want = Mathf.Min(PerMesh, Mathf.NextPowerOfTwo(Mathf.Max(quads, 256)));
            int vp = VertsPer, tp = TrisPer;
            verts = new Vector3[want * vp];
            uvs = new Vector2[want * vp];
            norms = new Vector3[want * vp];
            for (int n = 0; n < norms.Length; n++) norms[n] = Vector3.up;
            cols = new Color32[want * vp];
            tris = new int[want * tp * 3];
            for (int q = 0; q < want; q++)
            {
                int vi = q * vp, ti = q * tp * 3;
                if (lit)
                    for (int k = 0; k < 8; k++)
                    {
                        // Clockwise as the camera sees it, which is Unity's front: the rim runs anticlockwise from
                        // the camera's right towards its up, so each wedge is the centre, the next rim vertex, then
                        // this one. The other way round every grain faced away and the lit material culled them all.
                        tris[ti + k * 3 + 0] = vi;
                        tris[ti + k * 3 + 1] = vi + 1 + ((k + 1) & 7);
                        tris[ti + k * 3 + 2] = vi + 1 + k;
                    }
                else
                {
                    tris[ti + 0] = vi; tris[ti + 1] = vi + 1; tris[ti + 2] = vi + 2;
                    tris[ti + 3] = vi; tris[ti + 4] = vi + 2; tris[ti + 5] = vi + 3;
                }
            }
            capacityQuads = want;
        }

        // Per-row lookups, rebuilt each draw: the table is a few dozen rows and a grain is
        // tens of thousands, so reading a flat array beats chasing a struct per grain.
        private float[] rowRadius = new float[0];
        private float[] rowU = new float[0];
        private float[] rowV = new float[0];

        // A grain's shape, from its look: how much longer and shorter than round it is along two axes (0.75 to 1.3, a
        // pebble rather than a ball), and which way it lies. Sixty-four of them, picked by the low bits.
        private static float[] shapeLong, shapeShort, shapeCos, shapeSin;
        private static readonly float[] RimCos = new float[8], RimSin = new float[8];

        private static void Shapes()
        {
            if (shapeLong != null) return;
            shapeLong = new float[64]; shapeShort = new float[64]; shapeCos = new float[64]; shapeSin = new float[64];
            uint h = 0x9E3779B9u;
            for (int i = 0; i < 64; i++)
            {
                h ^= h << 13; h ^= h >> 17; h ^= h << 5;
                shapeLong[i] = 1f + (h & 0xFF) / 255f * 0.3f;
                shapeShort[i] = 0.75f + ((h >> 8) & 0xFF) / 255f * 0.25f;
                float a = ((h >> 16) & 0xFFFF) / 65535f * Mathf.PI;
                shapeCos[i] = Mathf.Cos(a); shapeSin[i] = Mathf.Sin(a);
            }
            for (int k = 0; k < 8; k++) { RimCos[k] = Mathf.Cos(k * Mathf.PI / 4f); RimSin[k] = Mathf.Sin(k * Mathf.PI / 4f); }
        }

        /// <summary>
        /// Rebuilds the grains' mesh and draws it, with the spray that is only drawn. Called once per frame, not
        /// once per sand step, so the medium does not judder between steps: with <paramref name="interpolate"/> a
        /// grain is drawn between where it was at the end of the last two steps, <paramref name="t"/> of the way.
        ///
        /// This runs in managed code over every grain every frame, so it is written to be
        /// cheap rather than clear: fields are read off the array element instead of
        /// copying the grain, the colour is converted with integer arithmetic, and a grain
        /// behind the camera is not given a quad at all.
        /// </summary>
        public void Draw(Grain[] grains, int count, SandMaterials.Params[] table,
                         int[] variantBase, int layer, bool interpolate, float t)
        {
            SoftMs = 0f;
            const int sprays = 0;   // the drawn spray is Unity's own particles now (SandSpray)
            if (material == null || grains == null || count <= 0) return;
            if (paletteIndex.Count > PaletteSide * PaletteSide / 2) { paletteIndex.Clear(); haveLast = false; }
            Shapes();

            // Camera.main is re-resolved rather than cached: Besiege swaps cameras
            // between the build area, a level and the level editor, and anything that
            // latched one in Start stops working the moment it does.
            Camera cam = Camera.main;
            if (cam == null) return;
            Transform ct = cam.transform;
            Vector3 right = ct.right;
            Vector3 up = ct.up;
            Vector3 forward = ct.forward;
            Vector3 eye = ct.position;
            Vector3 toEye = -forward;
            float far = cam.farClipPlane;

            int rows = table.Length;
            if (rowRadius.Length != rows)
            {
                rowRadius = new float[rows]; rowU = new float[rows]; rowV = new float[rows]; rowSoft = new bool[rows];
            }
            for (int r = 0; r < rows; r++)
            {
                rowRadius[r] = table[r].radius;
                // Two by two atlas: the tile is chosen by the medium the row looks like, so
                // sand is speckle and dust is a soft blob without a second draw call.
                int look = variantBase[r];
                if (look < 0 || look >= SandMaterials.Tiles.Length) look = 0;   // an unclaimed row
                rowSoft[r] = look == SandMaterials.Dust && soft.Ready;
                int tile = SandMaterials.Tiles[look];
                rowU[r] = (tile & 1) * 0.5f;
                rowV[r] = (tile >> 1) * 0.5f;
            }
            if (t < 0f) t = 0f; else if (t > 1f) t = 1f;

            soft.Begin();
            int total = count + sprays, vp = VertsPer, tp = TrisPer;
            int drawn = 0;
            int meshIndex = 0;
            while (drawn < total)
            {
                int batch = Mathf.Min(PerMesh, total - drawn);
                Reserve(batch);

                int q = 0;
                for (int i = 0; i < batch; i++)
                {
                    int gi = drawn + i;
                    Vector3 p;
                    float r;
                    uint packed, shape;
                    bool wet = false, blur;
                    int row = 0;
                    if (gi < count)
                    {
                        p = grains[gi].pos;
                        if (interpolate)
                        {
                            Vector3 to = grains[gi].shown, from = grains[gi].shownFrom;
                            if (to.x == 0f && to.y == 0f && to.z == 0f) { grains[gi].shown = p; grains[gi].shownFrom = p; }
                            else p = from + (to - from) * t;
                        }
                        uint mat = grains[gi].material;
                        row = (int)(mat & SandFormat.MaterialMask);
                        r = rowRadius[row];
                        packed = grains[gi].colour;
                        wet = (mat & SandFormat.FlagWet) != 0;
                        shape = grains[gi].look;
                        blur = rowSoft[row];
                        if (shape == 0)
                        {
                            // Given once, from where it first showed, so it keeps it.
                            shape = ((uint)(p.x * 7919f) * 2654435761u) ^ ((uint)(p.z * 104729f) * 2246822519u) ^ (uint)(p.y * 1301f) | 1u;
                            grains[gi].look = shape;
                        }
                    }
                    else continue;

                    float depth = (p.x - eye.x) * forward.x + (p.y - eye.y) * forward.y
                                + (p.z - eye.z) * forward.z;
                    if (depth < -0.2f || depth > far) continue;

                    if (blur)
                    {
                        uint dr = packed & 0xFF, dg = (packed >> 8) & 0xFF, db = (packed >> 16) & 0xFF;
                        if (wet) { dr = dr * 166 >> 8; dg = dg * 166 >> 8; db = db * 166 >> 8; }
                        soft.Add(p, r, (byte)dr, (byte)dg, (byte)db, shape);
                        continue;
                    }

                    int si = (int)(shape & 63);
                    float sc = shapeCos[si], ss = shapeSin[si];
                    Vector3 ua = right * sc + up * ss, ub = up * sc - right * ss;
                    Vector3 a = ua * (r * shapeLong[si]);
                    Vector3 b = ub * (r * shapeShort[si]);
                    int vi = q * vp;
                    if (lit)
                    {
                        verts[vi] = p;
                        norms[vi] = toEye;
                        for (int k = 0; k < 8; k++)
                        {
                            float cx = RimCos[k], cy = RimSin[k];
                            verts[vi + 1 + k] = p + a * cx + b * cy;
                            Vector3 n = (ua * cx + ub * cy) * 0.9f + toEye * 0.45f;
                            norms[vi + 1 + k] = n;
                        }
                    }
                    else
                    {
                        verts[vi + 0] = p - a - b;
                        verts[vi + 1] = p - a + b;
                        verts[vi + 2] = p + a + b;
                        verts[vi + 3] = p + a - b;
                    }

                    // The grain's own colour, darkened while wet. The medium's tint is not
                    // multiplied in: the emitter's colour control already starts at it and
                    // follows it, so doing both would tint twice and white snow would come
                    // out sand-coloured.
                    uint cr = packed & 0xFF, cg = (packed >> 8) & 0xFF, cb = (packed >> 16) & 0xFF;
                    if (wet) { cr = cr * 166 >> 8; cg = cg * 166 >> 8; cb = cb * 166 >> 8; }

                    if (lit)
                    {
                        Vector2 pu = PaletteUv(cr | (cg << 8) | (cb << 16));
                        for (int k = 0; k < 9; k++) uvs[vi + k] = pu;
                    }
                    else
                    {
                        float u = rowU[row];
                        float v = rowV[row];
                        uvs[vi + 0] = new Vector2(u, v);
                        uvs[vi + 1] = new Vector2(u, v + 0.5f);
                        uvs[vi + 2] = new Vector2(u + 0.5f, v + 0.5f);
                        uvs[vi + 3] = new Vector2(u + 0.5f, v);
                    }

                    Color32 c = new Color32((byte)cr, (byte)cg, (byte)cb, (byte)(packed >> 24));
                    for (int k = 0; k < vp; k++) cols[vi + k] = c;
                    q++;
                }

                // Anything the batch did not fill is collapsed to a point, so the
                // previous frame's grains cannot smear across the level.
                for (int i = q * vp; i < capacityQuads * vp; i++) verts[i] = Vector3.zero;

                Mesh mesh = MeshAt(meshIndex);
                if (meshQuads[meshIndex] != capacityQuads) mesh.Clear();
                mesh.vertices = verts;
                mesh.uv = uvs;
                mesh.colors32 = cols;
                if (lit) mesh.normals = norms;

                // Triangles never change for a given array size, and assigning them is
                // what forces Unity to rebuild the index buffer. Only write them when
                // the arrays were actually resized.
                if (meshQuads[meshIndex] != capacityQuads)
                {
                    mesh.triangles = tris;
                    if (!lit) mesh.normals = norms;
                    meshQuads[meshIndex] = capacityQuads;
                }
                mesh.RecalculateBounds();

                if (paletteDirty)
                {
                    palette.SetPixels32(paletteTexels);
                    palette.Apply(false);
                    paletteDirty = false;
                }
                // Grains take the machine's shadow but cast none: thousands of specks of shadow
                // cost a shadow pass and add nothing the bed's own shadow does not show.
                Graphics.DrawMesh(mesh, Matrix4x4.identity, material, layer, null, 0, null, false, lit);

                drawn += batch;
                meshIndex++;
            }
            softTimer.Reset(); softTimer.Start();
            soft.End(right, up, layer);
            softTimer.Stop();
            SoftMs = (float)softTimer.Elapsed.TotalMilliseconds;
        }

        /// <summary>Milliseconds the last draw spent building and sending the soft puffs (the spray and dust).</summary>
        public float SoftMs;
        private readonly System.Diagnostics.Stopwatch softTimer = new System.Diagnostics.Stopwatch();

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
            meshQuads.Clear();
            if (material != null) { Object.Destroy(material); material = null; }
            if (palette != null) { Object.Destroy(palette); palette = null; }
            soft.Dispose();
        }
    }
}
