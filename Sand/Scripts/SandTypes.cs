using UnityEngine;

namespace SandMod
{
    /// <summary>
    /// One grain. Field order and types must match <c>Particle</c> in Sand.compute:
    /// 52 bytes. Named Grain and not Particle on purpose -- <c>Particle</c> is one of
    /// the four Unity names Besiege shadows in the global namespace, so a type of
    /// ours called that produces errors naming Besiege's own assembly.
    /// </summary>
    public struct Grain
    {
        public Vector3 pos;
        public Vector3 prevPos;
        public Vector3 vel;
        public uint material;   // material index in the low 8 bits, flags above
        public uint colour;     // packed RGBA8
        public float packing;
        public float wetTimer;
        public float life;      // seconds of lifetime left
        public float age;       // seconds since it was emitted
    }

    /// <summary>
    /// A collider as the solver sees it. Must match <c>Shape</c> in Sand.compute:
    /// 64 bytes.
    /// </summary>
    public struct GpuShape
    {
        public Vector3 centre;
        public float radius;
        public Vector4 rot;
        public Vector3 halfExtent;
        public uint kindBlock;   // kind in the low 8 bits, block index above
        public Vector3 pointVel;
        public float movable;    // 0 for level geometry, 1 for something that can be pushed
    }

    /// <summary>Strides and flag bits shared by the C# and the kernel.</summary>
    public class SandFormat
    {
        public const int GrainStride = 60;
        public const int ShapeStride = 64;

        public const uint FlagWet = 0x00000100;
        public const uint FlagAsleep = 0x00000200;
        public const uint FlagDead = 0x00000400;
        public const uint MaterialMask = 0x000000FF;

        /// <summary>
        /// A grain younger than this does not push blocks. A region placed
        /// overlapping the ground, or grains spawned inside each other, would
        /// otherwise resolve a huge overlap on the first step and fire the machine
        /// across the level.
        /// </summary>
        public const float SettleSeconds = 0.35f;

        /// <summary>
        /// Ceiling on what one grain may push a block with, as a multiple of its own
        /// weight. A position solver's reaction force is overlap over dt squared,
        /// which is unbounded as overlap grows; this is what keeps a bad frame from
        /// becoming a launch.
        /// </summary>
        public const float MaxWeightMultiple = 40f;

        public const int ShapeBox = 0;
        public const int ShapeSphere = 1;
        public const int ShapeCapsule = 2;

        /// <summary>Cell capacity; must match CELL_CAPACITY in Sand.compute.</summary>
        public const int CellCapacity = 16;

        /// <summary>Six fixed-point ints per block: force xyz, torque xyz.</summary>
        public const int ForceStride = 6;

        /// <summary>Must match FORCE_SCALE in Sand.compute.</summary>
        public const float ForceScale = 4096f;
    }
}
