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

    /// <summary>
    /// One yes-or-no answer per engine object, cached so that a raycast or a collider walk does
    /// not pay for a component lookup every time it meets the same body.
    ///
    /// Keyed by Unity's instance ID, and holding the object the answer was measured on, because
    /// **Unity recycles instance IDs after destruction**. Keyed by the ID alone, one object's
    /// answer stands for another's: a floor collider can inherit a destroyed machine block's ID
    /// and be skipped as "a machine being built, not the floor", which is how a 4 m tile of a
    /// scene's ground comes to have a hole in it that nothing re-fills. Here a spent entry is
    /// recognised -- Unity's own comparison says no for a destroyed object -- and dropped rather
    /// than believed.
    ///
    /// Holding the object costs a wrapper per live entry, which the limit bounds; a destroyed one
    /// is dropped the first time its ID is asked about again.
    /// </summary>
    public class SandObjectCache
    {
        private struct Entry { public UnityEngine.Object was; public bool yes; }

        private readonly System.Collections.Generic.Dictionary<int, Entry> entries =
            new System.Collections.Generic.Dictionary<int, Entry>();
        private readonly int limit;

        public SandObjectCache(int limit) { this.limit = limit; }

        public int Count { get { return entries.Count; } }

        /// <summary>The answer already measured for this object, if there is one that is still
        /// about this object.</summary>
        public bool TryGet(UnityEngine.Object o, out bool yes)
        {
            Entry e;
            if (!entries.TryGetValue(o.GetInstanceID(), out e)) { yes = false; return false; }
            if (e.was == o) { yes = e.yes; return true; }
            if (e.was == null) entries.Remove(o.GetInstanceID());     // its object has gone
            yes = false;
            return false;
        }

        /// <summary>The answer for this object, from now on.</summary>
        public void Put(UnityEngine.Object o, bool yes)
        {
            if (entries.Count >= limit) entries.Clear();   // a capacity limit, not an eviction
            Entry e = new Entry();                         // no constructor initialiser: C# 4
            e.was = o; e.yes = yes;
            entries[o.GetInstanceID()] = e;
        }

        public void Clear() { entries.Clear(); }
    }
}
