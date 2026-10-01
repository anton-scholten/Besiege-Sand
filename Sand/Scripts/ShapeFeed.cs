using System.Collections.Generic;
using UnityEngine;

namespace SandMod
{
    /// <summary>
    /// Turns the level's colliders into the analytic shapes the solver understands,
    /// once per sand step.
    ///
    /// This is cheap because of what Besiege is made of: across all 104 block
    /// prefabs there are 342 box colliders, 138 spheres and 67 capsules, and five
    /// convex meshes in total (measured -- see docs/FINDINGS.md). So the machine
    /// goes to the solver as primitives and no triangle is ever touched. Concave
    /// scenery is not sent at all; it belongs in the ground height texture.
    ///
    /// Besiege has no <c>Physics.ComputePenetration</c> (it arrived in 5.6) and no
    /// <c>Collider.ClosestPoint</c>, which is why the shapes are rebuilt and the
    /// distance is computed in the kernel rather than asked of PhysX.
    /// </summary>
    public class ShapeFeed
    {
        private readonly List<Collider> sources = new List<Collider>();
        private readonly List<Rigidbody> bodies = new List<Rigidbody>();

        private GpuShape[] shapes = new GpuShape[256];
        private int shapeCount;

        // Reused rather than rebuilt: this runs every sand step, and a fresh
        // dictionary per step is garbage the collector has to find later, mid-run.
        private readonly Dictionary<int, int> slotOf = new Dictionary<int, int>();

        /// <summary>Rigidbodies in the order their force slots appear in the buffer.</summary>
        public List<Rigidbody> Bodies { get { return bodies; } }

        public GpuShape[] Shapes { get { return shapes; } }
        public int ShapeCount { get { return shapeCount; } }

        /// <summary>
        /// Re-scans the scene. Not per step: <c>FindObjectsOfType</c> is not cheap
        /// and the set of colliders only changes when a machine is built, a block is
        /// destroyed or a level loads.
        /// </summary>
        public void Rescan()
        {
            sources.Clear();
            bodies.Clear();

            Collider[] all = Object.FindObjectsOfType<Collider>();
            for (int i = 0; i < all.Length; i++)
            {
                Collider c = all[i];
                if (c == null || c.isTrigger) continue;
                if (!Supported(c)) continue;
                sources.Add(c);
            }
        }

        /// <summary>Colliders handed to the solver: the analytic shapes, plus a convex mesh
        /// on a rigidbody, which is a block and is approximated by the oriented box of
        /// its mesh bounds. A concave or static mesh is level scenery and is the ground's
        /// business -- boxing one would wall the sand off in mid-air.</summary>
        private static bool Supported(Collider c)
        {
            if (c is BoxCollider || c is SphereCollider || c is CapsuleCollider) return true;

            MeshCollider mc = c as MeshCollider;
            if (mc == null || !mc.convex || mc.sharedMesh == null) return false;
            Rigidbody rb = mc.attachedRigidbody;
            return rb != null && !rb.isKinematic;
        }

        /// <summary>How many colliders the last scan kept.</summary>
        public int SourceCount { get { return sources.Count; } }

        /// <summary>How many of the shapes written by the last gather can be pushed.</summary>
        public int MovableShapes
        {
            get
            {
                int n = 0;
                for (int i = 0; i < shapeCount; i++) if (shapes[i].movable > 0.5f) n++;
                return n;
            }
        }

        /// <summary>
        /// Colliders overlapping an area that the solver is NOT given, by kind. This is
        /// what answers "why does sand go through that block": either the block's
        /// collider is of a kind the feed does not take, or it is not here at all.
        /// </summary>
        public string DescribeIgnored(Bounds area)
        {
            int triggers = 0, concave = 0, convexStatic = 0, wheels = 0, other = 0;
            Collider[] all = Object.FindObjectsOfType<Collider>();
            for (int i = 0; i < all.Length; i++)
            {
                Collider c = all[i];
                if (c == null || !c.enabled || !area.Intersects(c.bounds)) continue;
                if (c.isTrigger) { triggers++; continue; }
                if (Supported(c)) continue;

                MeshCollider mc = c as MeshCollider;
                if (mc != null) { if (mc.convex) convexStatic++; else concave++; }
                else if (c is WheelCollider) wheels++;
                else other++;
            }
            return "triggers " + triggers + ", concave mesh " + concave
                + ", convex mesh not on a body " + convexStatic
                + ", wheel " + wheels + ", other " + other;
        }

        /// <summary>
        /// Rebuilds the shape array for this step, keeping only what is inside the
        /// field's bounds. Returns the number of shapes written.
        /// </summary>
        public int Gather(Bounds field)
        {
            shapeCount = 0;
            bodies.Clear();

            // A rigidbody gets one force slot however many colliders it has -- the
            // worst block carries 32 of them, so slots keyed by collider would waste
            // most of the readback.
            slotOf.Clear();

            for (int i = 0; i < sources.Count; i++)
            {
                Collider c = sources[i];
                if (c == null || !c.enabled) continue;
                if (!field.Intersects(c.bounds)) continue;

                Rigidbody rb = c.attachedRigidbody;
                int slot = 0;
                if (rb != null && !rb.isKinematic)
                {
                    int key = rb.GetInstanceID();
                    if (!slotOf.TryGetValue(key, out slot))
                    {
                        slot = bodies.Count;
                        slotOf[key] = slot;   // indexer, never Add: a duplicate key throws
                        bodies.Add(rb);
                    }
                }

                Write(c, rb, slot);
            }
            return shapeCount;
        }

        private void Grow()
        {
            GpuShape[] bigger = new GpuShape[shapes.Length * 2];
            System.Array.Copy(shapes, bigger, shapes.Length);
            shapes = bigger;
        }

        private void Write(Collider c, Rigidbody rb, int slot)
        {
            if (shapeCount >= shapes.Length) Grow();

            Transform t = c.transform;
            Vector3 scale = t.lossyScale;
            Quaternion rot = t.rotation;

            GpuShape s = new GpuShape();
            s.movable = (rb != null && !rb.isKinematic) ? 1f : 0f;

            if (c is BoxCollider)
            {
                BoxCollider b = (BoxCollider)c;
                s.centre = t.TransformPoint(b.center);
                s.halfExtent = new Vector3(Mathf.Abs(b.size.x * scale.x) * 0.5f,
                                           Mathf.Abs(b.size.y * scale.y) * 0.5f,
                                           Mathf.Abs(b.size.z * scale.z) * 0.5f);
                s.radius = 0f;
                s.kindBlock = (uint)SandFormat.ShapeBox | ((uint)slot << 8);
            }
            else if (c is SphereCollider)
            {
                SphereCollider sp = (SphereCollider)c;
                s.centre = t.TransformPoint(sp.center);
                float m = Mathf.Max(Mathf.Abs(scale.x), Mathf.Max(Mathf.Abs(scale.y), Mathf.Abs(scale.z)));
                s.radius = sp.radius * m;
                s.kindBlock = (uint)SandFormat.ShapeSphere | ((uint)slot << 8);
            }
            else if (c is MeshCollider)
            {
                // A block with a convex mesh: its mesh bounds as an oriented box. Looser
                // than the mesh, tighter than nothing.
                Bounds mb = ((MeshCollider)c).sharedMesh.bounds;
                s.centre = t.TransformPoint(mb.center);
                s.halfExtent = new Vector3(Mathf.Abs(mb.extents.x * scale.x),
                                           Mathf.Abs(mb.extents.y * scale.y),
                                           Mathf.Abs(mb.extents.z * scale.z));
                s.radius = 0f;
                s.kindBlock = (uint)SandFormat.ShapeBox | ((uint)slot << 8);
            }
            else
            {
                CapsuleCollider cap = (CapsuleCollider)c;
                s.centre = t.TransformPoint(cap.center);

                // The kernel's capsule runs along local Y. Unity's can run along any
                // axis, so the difference is folded into the rotation handed over
                // rather than branched on in the shader.
                if (cap.direction == 0) rot = rot * Quaternion.Euler(0f, 0f, 90f);
                else if (cap.direction == 2) rot = rot * Quaternion.Euler(90f, 0f, 0f);

                float radial = cap.direction == 0
                    ? Mathf.Max(Mathf.Abs(scale.y), Mathf.Abs(scale.z))
                    : (cap.direction == 1 ? Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.z))
                                          : Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.y)));
                float axial = cap.direction == 0 ? Mathf.Abs(scale.x)
                            : (cap.direction == 1 ? Mathf.Abs(scale.y) : Mathf.Abs(scale.z));

                s.radius = cap.radius * radial;
                float half = Mathf.Max(cap.height * axial * 0.5f - s.radius, 0f);
                s.halfExtent = new Vector3(0f, half, 0f);
                s.kindBlock = (uint)SandFormat.ShapeCapsule | ((uint)slot << 8);
            }

            s.rot = new Vector4(rot.x, rot.y, rot.z, rot.w);
            s.pointVel = rb != null ? rb.GetPointVelocity(s.centre) : Vector3.zero;

            shapes[shapeCount] = s;
            shapeCount++;
        }
    }
}
