using UnityEngine;

namespace SandMod
{
    /// <summary>
    /// The granular solver on the GPU. Grain state lives in a compute buffer and never
    /// comes back: the renderer reads the same buffer, and the only thing crossing to
    /// the CPU each step is one force and torque per rigidbody -- sized by blocks, not
    /// by grains, which is the rule Besiege's own BuoyancyManager follows.
    ///
    /// <see cref="Init"/> returns false rather than throwing when the machine has no
    /// compute support or the mod has no shader bundle, so the field can fall back to
    /// <see cref="CpuSolver"/> without knowing why.
    /// </summary>
    public class GpuSolver : ISandSolver
    {
        private ComputeShader shader;
        private int kClearForces, kPredict, kClearGrid, kBuildGrid, kSolve, kFinalize;

        private ComputeBuffer grains, gridCounts, gridItems, materialBuffer, shapeBuffer, forceBuffer;
        private int capacity;
        private int cellsX, cellsY, cellsZ;
        private int shapeCount, bodyCount;
        private int[] forceScratch = new int[SandFormat.ForceStride];

        public bool OnGpu { get { return true; } }
        public Grain[] Grains { get { return null; } }
        public ComputeBuffer GrainBuffer { get { return grains; } }

        /// <summary>The kernel, once something has handed one over.</summary>
        public static ComputeShader Kernel;

        public bool Init(int cap, SandMaterials.Params[] materials)
        {
            if (!SystemInfo.supportsComputeShaders) return false;
            if (Kernel == null) return false;

            shader = Kernel;
            capacity = cap;

            // FindKernel is fatal on a name that is not there, so every lookup is
            // guarded: a bundle built from a newer copy of the shader should degrade
            // to the CPU path, not take the mod down.
            try
            {
                kClearForces = shader.FindKernel("CSClearForces");
                kPredict = shader.FindKernel("CSPredict");
                kClearGrid = shader.FindKernel("CSClearGrid");
                kBuildGrid = shader.FindKernel("CSBuildGrid");
                kSolve = shader.FindKernel("CSSolve");
                kFinalize = shader.FindKernel("CSFinalize");
            }
            catch (System.Exception e)
            {
                Debug.Log("[Sand] compute kernel missing, falling back to the CPU: " + e.Message);
                return false;
            }

            grains = new ComputeBuffer(capacity, SandFormat.GrainStride);
            materialBuffer = new ComputeBuffer(materials.Length, SandMaterials.ParamsStride);
            materialBuffer.SetData(materials);
            shapeBuffer = new ComputeBuffer(1024, SandFormat.ShapeStride);
            forceBuffer = new ComputeBuffer(256 * SandFormat.ForceStride, 4);
            return true;
        }

        public void Upload(Grain[] source, int count)
        {
            if (grains != null && count > 0) grains.SetData(source);
        }

        public void SetMaterials(SandMaterials.Params[] table)
        {
            if (materialBuffer == null) return;
            if (materialBuffer.count < table.Length)
            {
                materialBuffer.Release();
                materialBuffer = new ComputeBuffer(table.Length, SandMaterials.ParamsStride);
            }
            materialBuffer.SetData(table);
        }

        public void SetShapes(GpuShape[] shapes, int count, int bodies)
        {
            shapeCount = count;
            bodyCount = bodies;
            if (count > 0)
            {
                if (shapeBuffer.count < count)
                {
                    shapeBuffer.Release();
                    shapeBuffer = new ComputeBuffer(Mathf.NextPowerOfTwo(count), SandFormat.ShapeStride);
                }
                shapeBuffer.SetData(shapes);
            }
            int want = Mathf.Max(bodies, 1) * SandFormat.ForceStride;
            if (forceBuffer.count < want)
            {
                forceBuffer.Release();
                forceBuffer = new ComputeBuffer(Mathf.NextPowerOfTwo(want), 4);
            }
            if (forceScratch.Length < want) forceScratch = new int[want];
        }

        public void Step(float dt, int substeps, int iterations, Vector3 gravity,
                         WaterLink water, Bounds bounds, int count)
        {
            if (shader == null || count <= 0) return;

            float cellSize = 0.12f;
            Vector3 origin = bounds.min - new Vector3(cellSize, cellSize, cellSize);
            Vector3 span = bounds.size + new Vector3(cellSize * 2f, cellSize * 2f, cellSize * 2f);
            int nx = Mathf.Max(1, Mathf.CeilToInt(span.x / cellSize));
            int ny = Mathf.Max(1, Mathf.CeilToInt(span.y / cellSize));
            int nz = Mathf.Max(1, Mathf.CeilToInt(span.z / cellSize));
            EnsureGrid(nx, ny, nz);

            shader.SetInt("_ParticleCount", count);
            shader.SetInt("_ShapeCount", shapeCount);
            shader.SetVector("_GridOrigin", origin);
            shader.SetFloat("_CellSize", cellSize);
            shader.SetInts("_GridDims", new int[] { cellsX, cellsY, cellsZ });
            shader.SetVector("_Gravity", gravity);
            shader.SetFloat("_SleepSpeed", 0.02f);

            bool haveWater = water != null && water.Available;
            shader.SetFloat("_WaterEnabled", haveWater ? 1f : 0f);
            if (haveWater)
            {
                // Bound straight from the game's own wave simulation. No copy, no
                // readback: both simulations read one height field.
                shader.SetTexture(kPredict, "_WaveTex", water.WaveTexture);
                Vector3 o = water.Origin;
                Vector2 s = water.Scale;
                shader.SetVector("_WaveOrigin", o);
                shader.SetVector("_WaveScale", new Vector4(s.x, s.y, 0f, 0f));
                shader.SetFloat("_WaveLevel", water.BaseLevel);
            }

            int groups = (count + 63) / 64;
            int gridGroups = (cellsX * cellsY * cellsZ + 63) / 64;
            int forceGroups = (Mathf.Max(bodyCount, 1) * SandFormat.ForceStride + 63) / 64;

            Bind(kClearForces); shader.Dispatch(kClearForces, forceGroups, 1, 1);

            float h = dt / Mathf.Max(substeps, 1);
            shader.SetFloat("_Dt", h);

            for (int s = 0; s < substeps; s++)
            {
                Bind(kPredict); shader.Dispatch(kPredict, groups, 1, 1);
                Bind(kClearGrid); shader.Dispatch(kClearGrid, gridGroups, 1, 1);
                Bind(kBuildGrid); shader.Dispatch(kBuildGrid, groups, 1, 1);
                for (int it = 0; it < iterations; it++)
                {
                    Bind(kSolve); shader.Dispatch(kSolve, groups, 1, 1);
                }
                Bind(kFinalize); shader.Dispatch(kFinalize, groups, 1, 1);
            }
        }

        private void Bind(int kernel)
        {
            shader.SetBuffer(kernel, "_Particles", grains);
            shader.SetBuffer(kernel, "_GridCounts", gridCounts);
            shader.SetBuffer(kernel, "_GridItems", gridItems);
            shader.SetBuffer(kernel, "_BlockForces", forceBuffer);
            shader.SetBuffer(kernel, "_Materials", materialBuffer);
            shader.SetBuffer(kernel, "_Shapes", shapeBuffer);
        }

        private void EnsureGrid(int nx, int ny, int nz)
        {
            if (gridCounts != null && nx == cellsX && ny == cellsY && nz == cellsZ) return;
            cellsX = nx; cellsY = ny; cellsZ = nz;
            int cells = nx * ny * nz;
            if (gridCounts != null) gridCounts.Release();
            if (gridItems != null) gridItems.Release();
            gridCounts = new ComputeBuffer(cells, 4);
            gridItems = new ComputeBuffer(cells * SandFormat.CellCapacity, 4);
        }

        public void ReadForces(float[] into, int bodies)
        {
            if (forceBuffer == null || bodies <= 0) return;

            // The one readback per sand step. It is a pipeline stall, which is why it
            // is sized by bodies and why the field does not step every FixedUpdate.
            forceBuffer.GetData(forceScratch);
            int n = Mathf.Min(into.Length, bodies * SandFormat.ForceStride);
            for (int i = 0; i < n; i++) into[i] = forceScratch[i] / SandFormat.ForceScale;
        }

        public void Dispose()
        {
            if (grains != null) { grains.Release(); grains = null; }
            if (gridCounts != null) { gridCounts.Release(); gridCounts = null; }
            if (gridItems != null) { gridItems.Release(); gridItems = null; }
            if (materialBuffer != null) { materialBuffer.Release(); materialBuffer = null; }
            if (shapeBuffer != null) { shapeBuffer.Release(); shapeBuffer = null; }
            if (forceBuffer != null) { forceBuffer.Release(); forceBuffer = null; }
        }
    }
}
