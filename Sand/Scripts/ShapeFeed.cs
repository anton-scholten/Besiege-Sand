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
    /// distance is computed by the solver rather than asked of PhysX.
    /// </summary>
    public class ShapeFeed
    {
        private readonly List<Collider> sources = new List<Collider>();
        private readonly List<Rigidbody> bodies = new List<Rigidbody>();

        private SandShape[] shapes = new SandShape[256];
        private int shapeCount;

        // Beside the shapes: each shape's body's angular velocity, and each body's centre of mass by force slot.
        private Vector3[] spin = new Vector3[256];
        private bool[] onBody = new bool[256];
        private bool[] stander = new bool[256];
        private Vector3[] centres = new Vector3[64];

        // Reused rather than rebuilt: this runs every sand step, and a fresh
        // dictionary per step is garbage the collector has to find later, mid-run.
        private readonly Dictionary<int, int> slotOf = new Dictionary<int, int>();

        /// <summary>Rigidbodies in the order their force slots appear in the buffer.</summary>
        public List<Rigidbody> Bodies { get { return bodies; } }

        public SandShape[] Shapes { get { return shapes; } }
        public int ShapeCount { get { return shapeCount; } }

        /// <summary>Angular velocity of the body each shape belongs to, parallel to
        /// <see cref="Shapes"/>; zero for a shape on nothing that moves. A wheel's centre can
        /// stand still while its rim moves fast, and only this lets the sand see the rim.</summary>
        public Vector3[] Spin { get { return spin; } }

        /// <summary>Whether each shape belongs to a rigidbody -- part of a machine, or a level
        /// object that moves -- rather than to the level's fixed geometry.</summary>
        public bool[] OnBody { get { return onBody; } }

        /// <summary>Whether each shape belongs to one of the level's people or animals (see <see cref="IsEntity"/>):
        /// something that stands on the sand and is otherwise left out of it.</summary>
        public bool[] Stander { get { return stander; } }

        private static readonly Dictionary<int, bool> entityOf = new Dictionary<int, bool>();

        /// <summary>
        /// Whether a body is one of the level's knights, peasants or animals (<c>EntityAI</c>). They walk on the
        /// sand's collider like on any ground, and that is all: they do not press it, dig it, or take the
        /// grains' pushes. A walking ragdoll pressed ruts under its feet, the collider was cooked again under
        /// it every step and it staggered on the new one, and its kicks dug sand up -- lag, and people moving
        /// very oddly.
        /// </summary>
        public static bool IsEntity(Rigidbody rb)
        {
            if (rb == null) return false;
            int id = rb.GetInstanceID();
            bool yes;
            if (entityOf.TryGetValue(id, out yes)) return yes;
            if (entityOf.Count > 20000) entityOf.Clear();
            yes = rb.GetComponentInParent<EntityAI>() != null;
            entityOf[id] = yes;
            return yes;
        }

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
        /// on a rigidbody, which is a block and is approximated by the tightest box it fits
        /// (<see cref="Fit"/>) -- a build surface's slab is one. A concave or static mesh is level
        /// scenery and is the ground's business -- boxing one would wall the sand off in mid-air.
        /// Kinematic too when it is a block's: a multiplayer client's machines are kinematic,
        /// and a surface left out there let all the sand through it.</summary>
        private static bool Supported(Collider c)
        {
            if (c is BoxCollider || c is SphereCollider || c is CapsuleCollider) return true;

            MeshCollider mc = c as MeshCollider;
            if (mc == null || !mc.convex || mc.sharedMesh == null) return false;
            Rigidbody rb = mc.attachedRigidbody;
            if (rb == null) return false;
            if (!rb.isKinematic) return true;
            int id = mc.GetInstanceID();
            bool block;
            if (!blockMesh.TryGetValue(id, out block))
            {
                block = mc.GetComponentInParent<BlockBehaviour>() != null;
                blockMesh[id] = block;
            }
            return block;
        }

        private static readonly Dictionary<int, bool> blockMesh = new Dictionary<int, bool>();

        /// <summary>A convex mesh's box in its own frame: where its middle is, how it is turned,
        /// and its half sizes, all at the scale it was fitted at.</summary>
        private class Fitted { public Vector3 centre, half, scale; public Quaternion turn; public int vertices; }
        private static readonly Dictionary<int, Fitted> fits = new Dictionary<int, Fitted>();

        /// <summary>
        /// The tightest box round a convex mesh that this finds: its bounds as they are, or a box
        /// turned to the plane the mesh lies in, whichever is smaller. A build surface's collider
        /// is a slab a few centimetres thick, tilted however its corners were put; its bounds in
        /// its own frame can be a box as tall as the surface is long, sand resting on air over it.
        /// Fitted once a mesh (the surface makes a new one when it changes) and kept.
        /// </summary>
        private static Fitted Fit(Mesh mesh, Vector3 scale)
        {
            int id = mesh.GetInstanceID();
            Fitted f;
            int count = mesh.vertexCount;
            if (fits.TryGetValue(id, out f) && f.vertices == count && f.scale == scale) return f;
            f = new Fitted();
            f.vertices = count;
            f.scale = scale;
            Bounds mb = mesh.bounds;
            f.centre = Vector3.Scale(mb.center, scale);
            f.half = new Vector3(Mathf.Abs(mb.extents.x * scale.x), Mathf.Abs(mb.extents.y * scale.y), Mathf.Abs(mb.extents.z * scale.z));
            f.turn = Quaternion.identity;
            fits[id] = f;

            Vector3[] v = mesh.vertices;
            if (v.Length < 3) return f;
            Vector3 mid = Vector3.zero;
            for (int i = 0; i < v.Length; i++) { v[i] = Vector3.Scale(v[i], scale); mid += v[i]; }
            mid /= v.Length;
            // The plane: the vertex farthest from the middle, and the one that makes the widest
            // triangle with it.
            int a = 0;
            float far = -1f;
            for (int i = 0; i < v.Length; i++) { float d = (v[i] - mid).sqrMagnitude; if (d > far) { far = d; a = i; } }
            Vector3 x = v[a] - mid, n = Vector3.zero;
            float wide = 0f;
            for (int i = 0; i < v.Length; i++)
            {
                Vector3 c = Vector3.Cross(x, v[i] - mid);
                float w = c.sqrMagnitude;
                if (w > wide) { wide = w; n = c; }
            }
            if (wide < 1e-12f || x.sqrMagnitude < 1e-12f) return f;
            n.Normalize();
            x = (x - n * Vector3.Dot(x, n)).normalized;
            Vector3 z = Vector3.Cross(x, n);
            Vector3 lo = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue), hi = -lo;
            for (int i = 0; i < v.Length; i++)
            {
                Vector3 r = v[i] - mid;
                Vector3 q = new Vector3(Vector3.Dot(r, x), Vector3.Dot(r, n), Vector3.Dot(r, z));
                lo = Vector3.Min(lo, q);
                hi = Vector3.Max(hi, q);
            }
            Vector3 half = (hi - lo) * 0.5f, at = (hi + lo) * 0.5f;
            if (half.x * half.y * half.z >= f.half.x * f.half.y * f.half.z) return f;
            f.half = half;
            f.centre = mid + x * at.x + n * at.y + z * at.z;
            f.turn = Quaternion.LookRotation(z, n);
            return f;
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
                if (rb != null && !rb.isKinematic && !IsEntity(rb))
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
            SandShape[] bigger = new SandShape[shapes.Length * 2];
            System.Array.Copy(shapes, bigger, shapes.Length);
            shapes = bigger;
            Vector3[] moreSpin = new Vector3[bigger.Length];
            System.Array.Copy(spin, moreSpin, spin.Length);
            spin = moreSpin;
            bool[] moreOnBody = new bool[bigger.Length];
            System.Array.Copy(onBody, moreOnBody, onBody.Length);
            onBody = moreOnBody;
            bool[] moreStander = new bool[bigger.Length];
            System.Array.Copy(stander, moreStander, stander.Length);
            stander = moreStander;
        }

        private void Write(Collider c, Rigidbody rb, int slot)
        {
            if (shapeCount >= shapes.Length) Grow();

            Transform t = c.transform;
            Vector3 scale = t.lossyScale;
            Quaternion rot = t.rotation;

            SandShape s = new SandShape();
            bool entity = IsEntity(rb);
            s.movable = (rb != null && !rb.isKinematic && !entity) ? 1f : 0f;

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
                // A block with a convex mesh: the tightest box round it that Fit finds.
                Fitted f = Fit(((MeshCollider)c).sharedMesh, scale);
                s.centre = t.position + rot * f.centre;
                rot = rot * f.turn;
                s.halfExtent = f.half;
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
            if (rb != null && rb.isKinematic)
            {
                // Moved by something else -- on a multiplayer client, the network -- so the body's own speed is nought:
                // how it has moved since it was last looked at.
                Vector3 lin, ang;
                Moved(rb, out lin, out ang);
                s.pointVel = lin + Vector3.Cross(ang, s.centre - rb.position);
                spin[shapeCount] = ang;
            }
            else
            {
                s.pointVel = rb != null ? rb.GetPointVelocity(s.centre) : Vector3.zero;
                spin[shapeCount] = rb != null ? rb.angularVelocity : Vector3.zero;
            }
            onBody[shapeCount] = rb != null;
            stander[shapeCount] = entity;

            shapes[shapeCount] = s;
            shapeCount++;
        }

        // ------------------------------------------------------------------ bodies moved from outside

        private class Track { public Vector3 pos, lin, ang; public Quaternion rot; public float at; public int frame; }
        private readonly System.Collections.Generic.Dictionary<int, Track> tracks = new System.Collections.Generic.Dictionary<int, Track>();

        /// <summary>
        /// The speed and spin of a kinematic body, from where it was when last looked at, once a frame: a multiplayer
        /// client's machines are moved by the network, and their rigidbodies say they are still -- the client's own
        /// drawn spray, which goes by how a wheel's surface slips over the sand, saw nothing slip.
        /// </summary>
        private void Moved(Rigidbody rb, out Vector3 lin, out Vector3 ang)
        {
            int id = rb.GetInstanceID();
            Track t;
            float now = Time.time;
            if (!tracks.TryGetValue(id, out t))
            {
                t = new Track(); t.pos = rb.position; t.rot = rb.rotation; t.at = now; t.frame = Time.frameCount;
                tracks[id] = t;
                if (tracks.Count > 20000) tracks.Clear();
            }
            else if (t.frame != Time.frameCount && now - t.at > 1e-4f)
            {
                float dt = now - t.at;
                Vector3 p = rb.position;
                Quaternion q = rb.rotation;
                t.lin = (p - t.pos) / dt;
                // The turn since: q times the inverse of the last, as an axis and an angle, over the time.
                Quaternion d = q * Quaternion.Inverse(t.rot);
                if (d.w < 0f) { d.x = -d.x; d.y = -d.y; d.z = -d.z; d.w = -d.w; }
                float half = Mathf.Acos(Mathf.Clamp(d.w, -1f, 1f)), sh = Mathf.Sin(half);
                t.ang = sh > 1e-5f ? new Vector3(d.x, d.y, d.z) / sh * (2f * half / dt) : Vector3.zero;
                t.pos = p; t.rot = q; t.at = now; t.frame = Time.frameCount;
            }
            lin = t.lin;
            ang = t.ang;
        }
    }
}
