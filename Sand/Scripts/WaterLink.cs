using UnityEngine;

namespace SandMod
{
    /// <summary>
    /// The level's water, as the sand solver needs it.
    ///
    /// Besiege's water is CodeAnimo Surface Waves, and it keeps its surface height in a
    /// RenderTexture on the GPU. It is copied down once a sand step and sampled per grain
    /// there: no per-grain physics query, and the two simulations agree because there is
    /// only one height field between them.
    ///
    /// The same object also carries <c>terrainData</c>, the level's ground height
    /// rendered into a texture -- which matters because a Besiege level has no Unity
    /// Terrain at all (measured; see docs/FINDINGS.md), so there is no heightmap to
    /// sample any other way.
    /// </summary>
    public class WaterLink
    {
        private CodeAnimo.SurfaceWaves.BuoyancyManager manager;

        public bool Available
        {
            get
            {
                return manager != null
                    && manager.waveData != null
                    && manager.waveData.isDataAvailable
                    && manager.waveData.outputData != null;
            }
        }

        public RenderTexture WaveTexture
        {
            get { return Available ? manager.waveData.outputData : null; }
        }


        /// <summary>World position of the wave field's first corner.</summary>
        public Vector3 Origin
        {
            get { return manager == null ? Vector3.zero : manager.simPosition.firstCorner; }
        }

        /// <summary>Texels per world unit in X and Z, for turning a position into a texel.</summary>
        public Vector2 Scale
        {
            get
            {
                if (manager == null) return Vector2.one;
                CodeAnimo.SurfaceWaves.Dimensions d = manager.simPosition;
                Vector3 size = d.localSize;
                float sx = size.x > 1e-4f ? d.resolutionX / size.x : 1f;
                float sz = size.z > 1e-4f ? d.resolutionZ / size.z : 1f;
                return new Vector2(sx, sz);
            }
        }

        /// <summary>The world Y that a texture reading of zero stands for.</summary>
        public float BaseLevel
        {
            get { return manager == null ? 0f : manager.simPosition.center.y; }
        }

        /// <summary>
        /// Finds the level's wave simulation, if it has one. Cheap to call again:
        /// a level change destroys the old manager and this notices by the reference
        /// going null, which is how Unity reports a destroyed object.
        /// </summary>
        public void Refresh()
        {
            if (manager != null) return;
            manager = Object.FindObjectOfType<CodeAnimo.SurfaceWaves.BuoyancyManager>();
        }

        public void Forget()
        {
            manager = null;
            if (readback != null) { Object.Destroy(readback); readback = null; }
            heights = null;
        }

        // ---------------------------------------------- reading the surface on the CPU

        private Texture2D readback;
        private Color[] pixels;
        private float[] heights;
        private int rbW, rbH;
        private Vector3 rbOrigin;
        private Vector2 rbScale;
        private float rbBase;

        /// <summary>
        /// Copies the wave height texture down once, for the solver.
        ///
        /// Called once per sand step, not per grain and not per frame. The copy is
        /// downsampled hard -- a metre of wave detail is far finer than a grain cares
        /// about, and the readback is a pipeline stall.
        /// </summary>
        public void CacheForCpu(int maxSide)
        {
            if (!Available) { heights = null; return; }

            RenderTexture src = WaveTexture;
            int w = Mathf.Min(src.width, maxSide);
            int h = Mathf.Min(src.height, maxSide);

            if (readback == null || rbW != w || rbH != h)
            {
                if (readback != null) Object.Destroy(readback);
                readback = new Texture2D(w, h, TextureFormat.RGBAFloat, false);
                pixels = null;
                rbW = w; rbH = h;
            }

            RenderTexture scaled = RenderTexture.GetTemporary(w, h, 0, src.format);
            Graphics.Blit(src, scaled);

            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = scaled;
            readback.ReadPixels(new Rect(0f, 0f, w, h), 0, 0, false);
            readback.Apply(false);
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(scaled);

            pixels = readback.GetPixels();
            if (heights == null || heights.Length != pixels.Length) heights = new float[pixels.Length];
            for (int i = 0; i < pixels.Length; i++) heights[i] = pixels[i].r;

            // The mapping is captured with the samples: the simulation can move, and
            // a height read against last step's mapping lands in the wrong place.
            rbOrigin = Origin;
            Vector2 s = Scale;
            rbScale = new Vector2(s.x * w / Mathf.Max(WaveTexture.width, 1),
                                  s.y * h / Mathf.Max(WaveTexture.height, 1));
            rbBase = BaseLevel;
        }

        /// <summary>World height of the water surface at a position, or far below
        /// everything when this level has no water.</summary>
        public float SampleHeight(Vector3 p)
        {
            if (heights == null) return -1e9f;
            int x = (int)((p.x - rbOrigin.x) * rbScale.x);
            int z = (int)((p.z - rbOrigin.z) * rbScale.y);
            if (x < 0 || z < 0 || x >= rbW || z >= rbH) return -1e9f;
            return rbBase + heights[x + z * rbW];
        }
    }
}
