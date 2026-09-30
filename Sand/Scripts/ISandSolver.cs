using UnityEngine;

namespace SandMod
{
    /// <summary>
    /// What a granular solver has to do, so the field does not care which one it has.
    ///
    /// Two implementations: <see cref="GpuSolver"/> is the target and keeps its state
    /// in compute buffers; <see cref="CpuSolver"/> keeps it in managed arrays and is
    /// what runs where compute is unavailable -- which today also means everywhere,
    /// until the kernel has been baked into an asset bundle.
    /// </summary>
    public interface ISandSolver
    {
        /// <summary>True when grain state lives on the GPU and never comes back.</summary>
        bool OnGpu { get; }

        /// <summary>The grain array, on the CPU path. Null on the GPU path.</summary>
        Grain[] Grains { get; }

        /// <summary>The grain buffer, on the GPU path, for the renderer to read. Null otherwise.</summary>
        ComputeBuffer GrainBuffer { get; }

        bool Init(int capacity, SandMaterials.Params[] materials);

        /// <summary>Hands over grains the field created or edited this step.</summary>
        void Upload(Grain[] grains, int count);

        void SetShapes(GpuShape[] shapes, int shapeCount, int bodyCount);

        /// <summary>
        /// Re-reads the material table. Called when an emitter with its advanced
        /// settings open changes one of them: those edits become a variant row rather
        /// than editing the medium everywhere, so the table changes shape at runtime.
        /// </summary>
        void SetMaterials(SandMaterials.Params[] table);

        /// <summary>
        /// One sand step. <paramref name="gravity"/> is read from the level every
        /// time and never assumed -- the space levels have their own and the player
        /// can turn it off.
        /// </summary>
        void Step(float dt, int substeps, int iterations, Vector3 gravity,
                  WaterLink water, Bounds bounds, int count);

        /// <summary>
        /// Force and torque per rigidbody, as six floats each, for the field to apply
        /// with AddForceAtPosition. Sized by body count, never by grain count.
        /// </summary>
        void ReadForces(float[] into, int bodyCount);

        void Dispose();
    }
}
