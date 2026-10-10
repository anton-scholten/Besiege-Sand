using UnityEngine;

namespace SandMod
{
    /// <summary>
    /// The shape feed, seen the way the bed wants it: a list of boxes that surely
    /// contain each collider, and a distance query. Extents are deliberately loose --
    /// a bounding sphere for a box, a capsule's full length -- because they only decide
    /// which shapes are worth a real distance test.
    ///
    /// Only shapes on a rigidbody are kept: a machine, or a level object that can move.
    /// The level's fixed geometry is already the bed's floor, by the ground raycasts, and
    /// counted here it was ruinous. Besiege builds its floors out of box colliders, so a pile
    /// lying on one was "touched by a shape" everywhere, and every column was tested against
    /// every collider of the level.
    /// </summary>
    public class BedShapes : IBedShapes, IBedShapeMotion
    {
        private SandShape[] kept = new SandShape[64];
        private Quaternion[] rot = new Quaternion[64];
        private Quaternion[] inv = new Quaternion[64];
        private Vector3[] spin = new Vector3[64];
        private int count;
        private Vector3[] mins = new Vector3[64];
        private Vector3[] maxs = new Vector3[64];
        private Vector3 unionMin, unionMax;

        public int Count { get { return count; } }

        private Vector3[] standMin = new Vector3[16], standMax = new Vector3[16];
        private int standers;

        /// <summary>Shapes that only stand on the sand (the level's people and animals): not dug with or pressed by.</summary>
        public int StanderCount { get { return standers; } }

        public void StanderExtent(int i, out Vector3 min, out Vector3 max) { min = standMin[i]; max = standMax[i]; }

        /// <summary>Reads the feed's current shapes and works out their extents.</summary>
        public void Prepare(ShapeFeed feed)
        {
            SandShape[] shapes = feed.Shapes;
            bool[] onBody = feed.OnBody;
            bool[] stands = feed.Stander;
            Vector3[] feedSpin = feed.Spin;
            int n = feed.ShapeCount;
            if (kept.Length < n)
            {
                kept = new SandShape[shapes.Length];
                rot = new Quaternion[shapes.Length];
                inv = new Quaternion[shapes.Length];
                spin = new Vector3[shapes.Length];
                mins = new Vector3[shapes.Length];
                maxs = new Vector3[shapes.Length];
            }

            count = 0;
            standers = 0;
            unionMin = unionMax = Vector3.zero;
            for (int i = 0; i < n; i++)
            {
                if (!onBody[i]) continue;
                SandShape s = shapes[i];
                if (stands[i])
                {
                    // Only somewhere to stand: the collider manager wants to know where it is.
                    if (standers == standMin.Length)
                    {
                        System.Array.Resize(ref standMin, standers * 2);
                        System.Array.Resize(ref standMax, standers * 2);
                    }
                    float sr = Mathf.Max(s.radius + s.halfExtent.magnitude, 0.1f);
                    standMin[standers] = s.centre - new Vector3(sr, sr, sr);
                    standMax[standers] = s.centre + new Vector3(sr, sr, sr);
                    standers++;
                    continue;
                }
                int kind = (int)(s.kindBlock & 0xFF);

                float reach;
                if (kind == SandFormat.ShapeSphere) reach = s.radius;
                else if (kind == SandFormat.ShapeCapsule) reach = s.halfExtent.y + s.radius;
                else reach = s.halfExtent.magnitude;

                Vector3 r = new Vector3(reach, reach, reach);
                int k = count++;
                kept[k] = s;
                rot[k] = new Quaternion(s.rot.x, s.rot.y, s.rot.z, s.rot.w);
                inv[k] = new Quaternion(-s.rot.x, -s.rot.y, -s.rot.z, s.rot.w);
                spin[k] = feedSpin[i];
                mins[k] = s.centre - r;
                maxs[k] = s.centre + r;

                if (k == 0) { unionMin = mins[k]; unionMax = maxs[k]; }
                else
                {
                    unionMin = Vector3.Min(unionMin, mins[k]);
                    unionMax = Vector3.Max(unionMax, maxs[k]);
                }
            }
        }

        public void Extent(int i, out Vector3 min, out Vector3 max)
        {
            min = mins[i];
            max = maxs[i];
        }

        public float Distance(int i, Vector3 p)
        {
            float dist;
            Vector3 normal;
            ShapeMath.DistanceInv(kept[i], rot[i], inv[i], p, out dist, out normal);
            return dist;
        }

        public Vector3 SurfaceVelocity(int i, Vector3 p)
        {
            return kept[i].pointVel + Vector3.Cross(spin[i], p - kept[i].centre);
        }

        /// <summary>Shape <paramref name="i"/> as the feed gave it.</summary>
        public SandShape Shape(int i) { return kept[i]; }

        public void Union(out Vector3 min, out Vector3 max)
        {
            min = unionMin;
            max = unionMax;
        }
    }
}
