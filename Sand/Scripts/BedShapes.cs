using UnityEngine;

namespace SandMod
{
    /// <summary>
    /// The shape feed, seen the way the bed wants it: a list of boxes that surely
    /// contain each collider, and a distance query. Extents are deliberately loose --
    /// a bounding sphere for a box, a capsule's full length -- because they only decide
    /// which shapes are worth a real distance test.
    /// </summary>
    public class BedShapes : IBedShapes
    {
        private GpuShape[] shapes;
        private int count;
        private Vector3[] mins = new Vector3[0];
        private Vector3[] maxs = new Vector3[0];
        private Vector3 unionMin, unionMax;

        public int Count { get { return count; } }

        /// <summary>Reads the feed's current shapes and works out their extents.</summary>
        public void Prepare(ShapeFeed feed)
        {
            shapes = feed.Shapes;
            count = feed.ShapeCount;
            if (mins.Length < count)
            {
                mins = new Vector3[shapes.Length];
                maxs = new Vector3[shapes.Length];
            }

            for (int i = 0; i < count; i++)
            {
                GpuShape s = shapes[i];
                int kind = (int)(s.kindBlock & 0xFF);

                float reach;
                if (kind == SandFormat.ShapeSphere) reach = s.radius;
                else if (kind == SandFormat.ShapeCapsule) reach = s.halfExtent.y + s.radius;
                else reach = s.halfExtent.magnitude;

                Vector3 r = new Vector3(reach, reach, reach);
                mins[i] = s.centre - r;
                maxs[i] = s.centre + r;

                if (i == 0) { unionMin = mins[i]; unionMax = maxs[i]; }
                else
                {
                    unionMin = Vector3.Min(unionMin, mins[i]);
                    unionMax = Vector3.Max(unionMax, maxs[i]);
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
            ShapeMath.Distance(shapes[i], p, out dist, out normal);
            return dist;
        }

        public void Union(out Vector3 min, out Vector3 max)
        {
            min = unionMin;
            max = unionMax;
        }
    }
}
