using UnityEngine;

namespace SandMod
{
    /// <summary>
    /// Signed distance to a shape, in world space.
    ///
    /// This exists at all because Besiege is Unity 5.4: there is no
    /// <c>Physics.ComputePenetration</c> (5.6) and no <c>Collider.ClosestPoint</c>
    /// (2017), so the sand cannot ask PhysX how deep inside a collider a point is.
    /// </summary>
    public class ShapeMath
    {
        public static void Distance(SandShape s, Vector3 p, out float dist, out Vector3 normal)
        {
            // The inverse of a unit quaternion is its conjugate. Quaternion.Inverse is a call
            // into the engine, and the bed makes thousands of these tests a step.
            Quaternion rot = new Quaternion(s.rot.x, s.rot.y, s.rot.z, s.rot.w);
            Quaternion inv = new Quaternion(-s.rot.x, -s.rot.y, -s.rot.z, s.rot.w);
            DistanceInv(s, rot, inv, p, out dist, out normal);
        }

        /// <summary>
        /// The same, with the rotation and its inverse supplied: worked out once per shape per
        /// step and used for thousands of tests, where deriving the inverse each time was most
        /// of the cost of a test.
        /// </summary>
        public static void DistanceInv(SandShape s, Quaternion rot, Quaternion inv, Vector3 p,
                                       out float dist, out Vector3 normal)
        {
            Vector3 local = inv * (p - s.centre);
            int kind = (int)(s.kindBlock & 0xFF);

            if (kind == SandFormat.ShapeSphere)
            {
                float len = Mathf.Max(local.magnitude, 1e-6f);
                dist = len - s.radius;
                normal = rot * (local / len);
                return;
            }

            if (kind == SandFormat.ShapeCapsule)
            {
                Vector3 q = local;
                q.y -= Mathf.Clamp(q.y, -s.halfExtent.y, s.halfExtent.y);
                float len = Mathf.Max(q.magnitude, 1e-6f);
                dist = len - s.radius;
                normal = rot * (q / len);
                return;
            }

            // Box. The exterior case is the usual one; the interior case matters
            // because a point that ends up inside has to be pushed out of the nearest
            // face rather than left there to be pushed nowhere.
            Vector3 d = new Vector3(Mathf.Abs(local.x) - s.halfExtent.x,
                                    Mathf.Abs(local.y) - s.halfExtent.y,
                                    Mathf.Abs(local.z) - s.halfExtent.z);
            Vector3 outside = new Vector3(Mathf.Max(d.x, 0f), Mathf.Max(d.y, 0f), Mathf.Max(d.z, 0f));
            float outLen = outside.magnitude;

            if (outLen > 1e-6f)
            {
                dist = outLen;
                Vector3 n = new Vector3(Mathf.Sign(local.x) * outside.x,
                                        Mathf.Sign(local.y) * outside.y,
                                        Mathf.Sign(local.z) * outside.z) / outLen;
                normal = rot * n;
                return;
            }

            float ax = -d.x, ay = -d.y, az = -d.z;
            float m = Mathf.Min(ax, Mathf.Min(ay, az));
            Vector3 axis = new Vector3(ax <= m ? Mathf.Sign(local.x) : 0f,
                                       ay <= m ? Mathf.Sign(local.y) : 0f,
                                       az <= m ? Mathf.Sign(local.z) : 0f);
            if (axis.sqrMagnitude < 1e-9f) axis = Vector3.up;
            dist = -m;
            normal = rot * axis.normalized;
        }
    }
}
