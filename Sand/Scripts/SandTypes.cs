using UnityEngine;

namespace SandMod
{
    /// <summary>
    /// One grain. Named Grain and not Particle on purpose -- <c>Particle</c> is one of
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
        // Drawing only, never the solver: where the grain was at the end of the last two sand steps, which a frame
        // draws it between (SandRenderer), and the seed of its shape. All zero for a grain not yet drawn.
        public Vector3 shown;
        public Vector3 shownFrom;
        public uint look;
    }

    /// <summary>
    /// A collider as the solver sees it.
    /// </summary>
    public struct SandShape
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

        public const uint FlagWet = 0x00000100;
        public const uint FlagAsleep = 0x00000200;
        public const uint FlagDead = 0x00000400;
        public const uint MaterialMask = 0x000000FF;

        /// <summary>
        /// How many solver steps in a row a grain has been asleep, in four bits above
        /// the flags. The CPU solver counts it; the bed absorbs a grain once it has
        /// lain still long enough.
        /// </summary>
        public const int RestShift = 12;
        public const uint RestMask = 0x0000F000;
        public const int RestMax = 15;

        public static int RestSteps(uint material)
        {
            return (int)((material & RestMask) >> RestShift);
        }

        /// <summary>
        /// A grain younger than this does not push blocks. Grains spawned inside
        /// each other, or overlapping the ground, would
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


        /// <summary>Six fixed-point ints per block: force xyz, torque xyz.</summary>
        public const int ForceStride = 6;

    }

    /// <summary>What sand may do to a rigidbody, apart from the solver.</summary>
    public static class SandCoupling
    {
        /// <summary>How many times its own weight's worth of speed a step sand may give a body that is
        /// not moving into it: one carries it, the rest is margin.</summary>
        public const float RestSupport = 1.7f;

        /// <summary>
        /// The share of an impulse sand may give a body, 0 to 1. Sand takes the speed a body has into
        /// it away and does not hand it back: it may add along its push the speed the body has against
        /// that push, plus what holds the body up for a step (gravity times the step, and a margin so
        /// a body at rest is still carried). A core block dropped on a pile used to be thrown off it by
        /// the grains it had pressed together.
        /// </summary>
        public static float Allowed(Vector3 impulse, float mass, Vector3 velocity, float gravity, float stepDt)
        {
            float push = impulse.magnitude / Mathf.Max(mass, 0.01f);
            if (push < 1e-4f) return 1f;
            Vector3 along = impulse / (push * Mathf.Max(mass, 0.01f));
            float into = Mathf.Max(0f, -Vector3.Dot(velocity, along));
            float allowed = into + RestSupport * gravity * Mathf.Max(stepDt, 0.01f);
            return push > allowed ? allowed / push : 1f;
        }
    }
}
