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

        // Beside the shapes, for the CPU solver only, so the GPU's shape layout is untouched:
        // each shape's body's angular velocity, and each body's centre of mass by force slot.
        private Vector3[] spin = new Vector3[256];
        private bool[] onBody = new bool[256];
        private Vector3[] centres = new Vector3[64];

        // Reused rather than rebuilt: this runs every sand step, and a fresh
        // dictionary per step is garbage the collector has to find later, mid-run.
        private readonly Dictionary<int, int> slotOf = new Dictionary<int, int>();

        /// <summary>Rigidbodies in the order their force slots appear in the buffer.</summary>
        public List<Rigidbody> Bodies { get { return bodies; } }

        public GpuShape[] Shapes { get { return shapes; } }
        public int ShapeCount { get { return shapeCount; } }

        /// <summary>Angular velocity of the body each shape belongs to, parallel to
        /// <see cref="Shapes"/>; zero for a shape on nothing that moves. A wheel's centre can
        /// stand still while its rim moves fast, and only this lets the sand see the rim.</summary>
        public Vector3[] Spin { get { return spin; } }

        /// <summary>Whether each shape belongs to a rigidbody -- part of a machine, or a level
        /// object that moves -- rather than to the level's fixed geometry.</summary>
        public bool[] OnBody { get { return onBody; } }

        /// <summary>Each body's centre of mass, by force slot: the point the torque handed
        /// back is about, since that is where the force is applied.</summary>
        public Vector3[] BodyCentres { get { return centres; } }

        /// <summary>
        /// Re-scans the scene. Not per step: <c>FindObjectsOfType</c> is not cheap
        /// and the set of colliders only changes when a machine is built, a block is
        /// destroyed or a level loads.
        /// </summary>
        public void Rescan()
        {
            sources.Clear();
            bodies.Clear();
            fixedSources.Clear();
            Scans++;

            Collider[] all = Object.FindObjectsOfType<Collider>();
            for (int i = 0; i < all.Length; i++)
            {
                Collider c = all[i];
                if (c == null || c.isTrigger) continue;
                if (!Supported(c)) continue;
                sources.Add(c);
                if (c.attachedRigidbody == null) fixedSources.Add(c);
            }
            // Bodies that are not blocks, from the colliders already found rather than another
            // walk of the scene; and the machines' water cannons, flamethrowers and fire tags from
            // their own block lists. The four extra FindObjectsOfType this used to make doubled
            // the stall at the start of every run.
            looseBodies.Clear();
            looseSeen.Clear();
            for (int i = 0; i < sources.Count; i++)
            {
                Rigidbody rb = sources[i].attachedRigidbody;
                if (rb == null || !looseSeen.Add(rb.GetInstanceID())) continue;
                if (rb.GetComponent<BlockBehaviour>() == null) looseBodies.Add(rb);
            }
            RescanBodies();
        }

        // The level's fixed colliders from the last full scan. They do not come and go during
        // a run, so between full scans only what is on a rigidbody is looked for again.
        /// <summary>Full scans so far, for the tests and the report.</summary>
        public int Scans;

        private readonly List<Collider> fixedSources = new List<Collider>();
        private readonly List<Collider> scratch = new List<Collider>();

        /// <summary>
        /// The cheap rescan: the fixed colliders and the loose bodies the last full scan found,
        /// and the colliders of every machine's blocks now, read from the machines' own block
        /// lists. FindObjectsOfType walks every object in the scene -- four of them, for the
        /// bodies, the water cannons, the flamethrowers and the fire tags, were 30 ms each
        /// rescan on a big level, a stall every quarter of a second -- and the machines already
        /// know their blocks.
        /// </summary>
        public void RescanBodies()
        {
            sources.Clear();
            bodies.Clear();
            sources.AddRange(fixedSources);
            Cannons.Clear(); Flamers.Clear(); Fires.Clear();

            // Bodies that are not blocks -- a level's moving objects, anything loose at the last
            // full scan -- as that scan found them, less any since destroyed.
            for (int i = looseBodies.Count - 1; i >= 0; i--)
            {
                if (looseBodies[i] == null) { looseBodies.RemoveAt(i); continue; }
                AddBody(looseBodies[i]);
            }

            machines.Clear();
            Machines(machines);
            for (int m = 0; m < machines.Count; m++)
            {
                List<BlockBehaviour> blocks = null;
                try { blocks = machines[m].isSimulating ? machines[m].SimulationBlocks : machines[m].BuildingBlocks; }
                catch (System.Exception) { }
                if (blocks == null) continue;
                for (int b = 0; b < blocks.Count; b++)
                {
                    BlockBehaviour block = blocks[b];
                    if (block == null) continue;
                    WaterCannonController cannon = block as WaterCannonController;
                    if (cannon != null) Cannons.Add(cannon);
                    FlamethrowerController flamer = block as FlamethrowerController;
                    if (flamer != null) Flamers.Add(flamer);
                    if (block.fireTag != null) Fires.Add(block.fireTag);
                    block.GetComponentsInChildren<Collider>(false, scratch);
                    for (int k = 0; k < scratch.Count; k++)
                    {
                        Collider c = scratch[k];
                        if (c == null || c.isTrigger || c.attachedRigidbody == null) continue;
                        if (!Supported(c)) continue;
                        sources.Add(c);
                    }
                }
            }
        }

        private readonly List<Rigidbody> looseBodies = new List<Rigidbody>();
        private readonly HashSet<int> looseSeen = new HashSet<int>();
        private readonly List<Machine> machines = new List<Machine>();

        private void AddBody(Rigidbody rb)
        {
            rb.GetComponentsInChildren<Collider>(false, scratch);
            for (int k = 0; k < scratch.Count; k++)
            {
                Collider c = scratch[k];
                if (c == null || c.isTrigger || c.attachedRigidbody != rb) continue;
                if (!Supported(c)) continue;
                sources.Add(c);
            }
        }

        /// <summary>Every machine in the game: each player's in multiplayer, else the local one.</summary>
        private static void Machines(List<Machine> into)
        {
            try
            {
                if (StatMaster.isMP && Playerlist.Players != null)
                    for (int i = 0; i < Playerlist.Players.Count; i++)
                    {
                        PlayerData p = Playerlist.Players[i];
                        if (p != null && p.machine != null) into.Add(p.machine);
                    }
            }
            catch (System.Exception) { }
            if (into.Count > 0) return;
            try
            {
                Machine m = Machine.Active();
                if (m != null) into.Add(m);
            }
            catch (System.Exception) { }
        }

        // ------------------------------------------------------------------ water and fire

        /// <summary>Water cannons in the scene, from the last scan.</summary>
        public readonly List<WaterCannonController> Cannons = new List<WaterCannonController>();
        /// <summary>Flamethrowers in the scene.</summary>
        public readonly List<FlamethrowerController> Flamers = new List<FlamethrowerController>();
        /// <summary>Everything that can burn, which the sand near it dries beside while it does.</summary>
        public readonly List<FireTag> Fires = new List<FireTag>();

        /// <summary>Takes another feed's scan instead of scanning again. The bed and the solver
        /// each keep a feed, gathered over different areas, but the scene they scan is the
        /// same one, and FindObjectsOfType is the expensive part.</summary>
        public void CopySources(ShapeFeed other)
        {
            sources.Clear();
            sources.AddRange(other.sources);
            bodies.Clear();
        }

        /// <summary>The same, keeping only colliders on a rigidbody: all the bed cares about.
        /// The level's own geometry is most of a scan, and gathering it every step for the bed
        /// to throw away was a large part of a slow step on a big level.</summary>
        public void CopyBodySources(ShapeFeed other)
        {
            sources.Clear();
            for (int i = 0; i < other.sources.Count; i++)
            {
                Collider c = other.sources[i];
                if (c != null && c.attachedRigidbody != null) sources.Add(c);
            }
            bodies.Clear();
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
                        if (slot >= centres.Length)
                        {
                            Vector3[] more = new Vector3[centres.Length * 2];
                            System.Array.Copy(centres, more, centres.Length);
                            centres = more;
                        }
                        centres[slot] = rb.worldCenterOfMass;
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
            Vector3[] moreSpin = new Vector3[bigger.Length];
            System.Array.Copy(spin, moreSpin, spin.Length);
            spin = moreSpin;
            bool[] moreOnBody = new bool[bigger.Length];
            System.Array.Copy(onBody, moreOnBody, onBody.Length);
            onBody = moreOnBody;
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
            spin[shapeCount] = rb != null ? rb.angularVelocity : Vector3.zero;
            onBody[shapeCount] = rb != null;

            shapes[shapeCount] = s;
            shapeCount++;
        }
    }
}
