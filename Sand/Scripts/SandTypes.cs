using UnityEngine;

namespace SandMod
{
    /// <summary>
    /// A collider as the bed sees it: one of the analytic shapes ShapeFeed turns the
    /// machines' colliders into.
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

    /// <summary>The kinds of shape.</summary>
    public class SandFormat
    {
        public const int ShapeBox = 0;
        public const int ShapeSphere = 1;
        public const int ShapeCapsule = 2;
    }
}
