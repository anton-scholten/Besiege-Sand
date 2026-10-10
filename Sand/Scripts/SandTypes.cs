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

    /// <summary>
    /// A guard on numbers that came from outside: another player's message, a file, a typed value.
    ///
    /// It is here because the arithmetic it exists for does not do it. <c>Mathf.Clamp</c> is
    /// <c>if (v &lt; min) … else if (v &gt; max) …</c>, and every comparison against NaN is false,
    /// so NaN passes straight through a clamp -- and one NaN written into a column's height makes
    /// the bed's whole extent NaN, which poisons every ground height query and every brush for the
    /// rest of the session. Seven floats in a brush message are enough, from any player in a public
    /// game.
    ///
    /// So the check is in one place, and applied at every entry point that takes numbers from
    /// outside. The panel already made its own (<c>SandHud.SetNumber</c>); this is that, shared.
    /// </summary>
    public static class SandGuard
    {
        /// <summary>Whether a number is neither NaN nor infinite.</summary>
        public static bool Ok(float v) { return !(float.IsNaN(v) || float.IsInfinity(v)); }

        /// <summary>Whether all three of a point's coordinates are numbers.</summary>
        public static bool Ok(Vector3 v) { return Ok(v.x) && Ok(v.y) && Ok(v.z); }

        /// <summary>The number, or <paramref name="was"/> if it is neither NaN nor infinite.</summary>
        public static float Ok(float v, float was) { return Ok(v) ? v : was; }

        /// <summary>The number within <paramref name="lo"/> and <paramref name="hi"/>, or
        /// <paramref name="was"/> if it is neither: a NaN is out of range whatever the range is.</summary>
        public static float Clamp(float v, float lo, float hi, float was)
        {
            return Ok(v) ? Mathf.Clamp(v, lo, hi) : was;
        }

        /// <summary>The number rounded to a whole one within <paramref name="lo"/> and
        /// <paramref name="hi"/>, or <paramref name="was"/>. Past 2^24 a float has no integers left
        /// in it, so this is a range test as much as a rounding one.</summary>
        public static int Whole(float v, int lo, int hi, int was)
        {
            return Ok(v) ? Mathf.Clamp(Mathf.RoundToInt(v), lo, hi) : was;
        }
    }
}
