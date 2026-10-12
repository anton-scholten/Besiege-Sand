using System.Collections.Generic;
using UnityEngine;

namespace SandMod
{
    /// <summary>
    /// Makes the bed solid to machines: a static mesh collider for each chunk of it near
    /// something that moves, so PhysX holds a machine up on the pile the way it holds it up on
    /// the ground, with friction, rolling and all.
    ///
    /// A machine stands on the surface, and the force PhysX pushes
    /// back with is reported to the bed (<see cref="BedContact"/>), which sinks under it
    /// (<see cref="SandBed.Press"/>): a light machine barely marks the sand, a heavy one leaves
    /// ruts. A face moving sideways into it ploughs it (<see cref="SandBed.Plough"/>).
    ///
    /// Colliders exist only for chunks within reach of a body, and are cooked a few per sand
    /// step, nearest a body first; a chunk nothing has come near for a couple of seconds loses
    /// its collider again. Cooking a concave mesh is the expensive part, and a big bed has
    /// hundreds of chunks no machine will ever touch.
    /// </summary>
    public class SandBedColliders
    {
        private const int Side = SandBed.ChunkSide + 1;
        private readonly System.Diagnostics.Stopwatch timer = new System.Diagnostics.Stopwatch();

        /// <summary>Metres around a shape, beyond how far it will move in half a second, in
        /// which the bed has a collider. It was 1.5 m, then 1 m, and a big machine still wanted nearly five hundred.</summary>
        private const float Reach = 0.6f;

        /// <summary>Seconds of a shape's travel ahead of it that get colliders: a quarter of a second -- at 12 m/s three
        /// metres, cooked well before it gets there; half a second wanted more colliders than could be cooked.</summary>
        private const float Ahead = 0.25f;

        /// <summary>Sand steps a collider is kept after nothing wants it any more: 0.4 s, under a fast machine's trail.</summary>
        private const int KeepSteps = 10;

        /// <summary>How far below the ground the collider's edge dips where the sand runs out,
        /// so the level's own floor takes over there.</summary>
        private const float EdgeDip = 0.03f;

        private class Entry
        {
            public GameObject go;
            public MeshCollider collider;
            public Mesh mesh;
            public int wanted;
            public float[] cooked;      // the heights it was last cooked with
            public bool[] cookedSand;
            public float cookedAt;      // and when
            public float lastMoved;      // and how far the sand had gone from it when last looked at
        }

        /// <summary>Layer the colliders go on: the level floor's, so that whatever collides
        /// with the floor collides with the sand. Set from the ground raycasts.</summary>
        public int Layer;
        private int lastLayer = -1;

        private Transform parent;
        private PhysicMaterial[] physic;     // one a medium: see SandMaterials.GripStatic
        private SandBed bed;
        private readonly List<SandBed.Chunk> live = new List<SandBed.Chunk>();
        private readonly List<SandBed.Chunk> wantNow = new List<SandBed.Chunk>();
        private readonly List<float> wantDistance = new List<float>();
        private int stamp;

        private readonly float[] heights = new float[Side * Side];
        private readonly bool[] sand = new bool[Side * Side];
        private readonly bool[] known = new bool[Side * Side];
        private readonly Vector3[] vertices = new Vector3[Side * Side];
        private readonly int[] scratch = new int[SandBed.ChunkSide * SandBed.ChunkSide * 6];
        private readonly int[] mediumTally = new int[SandMaterials.Count];

        public int Count { get { return live.Count; } }

        public void Init(Transform owner, SandBed sandBed)
        {
            parent = owner;
            bed = sandBed;
            // Minimum, so the medium's grip is what a tyre gets however grippy the tyre: averaged, a block's
            // own friction lifted mud to most of tarmac's. (A block whose material combines by Multiply or
            // Maximum outranks it.)
            physic = new PhysicMaterial[SandMaterials.Count];
            for (int m = 0; m < physic.Length; m++)
            {
                PhysicMaterial pm = new PhysicMaterial("Sand" + m);
                pm.dynamicFriction = SandMaterials.GripSliding[m];
                pm.staticFriction = SandMaterials.GripStatic[m];
                pm.bounciness = 0f;
                // Glue's grip is the higher of its and the tyre's: Maximum outranks every other combine mode,
                // so no block slips on it.
                pm.frictionCombine = m == SandMaterials.Glue ? PhysicMaterialCombine.Maximum : PhysicMaterialCombine.Minimum;
                pm.bounceCombine = PhysicMaterialCombine.Minimum;
                physic[m] = pm;
            }
        }

        /// <summary>
        /// Gives a collider to every chunk with sand that a body is near, and recooks the ones
        /// whose sand has changed, closest to a body first, until <paramref name="budgetMs"/>
        /// is spent. Called once a sand step.
        /// </summary>
        public void Update(BedShapes shapes, float budgetMs)
        {
            if (bed == null) return;
            stamp++;
            // The layer can change -- the first floor found was not the floor -- and a collider
            // left on the old one is one the machine may not collide with.
            if (Layer != lastLayer)
            {
                lastLayer = Layer;
                for (int k = 0; k < live.Count; k++)
                {
                    Entry le = live[k].colliderHandle as Entry;
                    if (le != null && le.go != null) le.go.layer = Layer;
                }
            }
            wantNow.Clear();
            wantDistance.Clear();

            float chunkSize = SandBed.Cell * SandBed.ChunkSide;
            int machineShapes = shapes.Count;
            for (int s = 0; s < machineShapes + shapes.StanderCount; s++)
            {
                Vector3 lo, hi;
                if (s < machineShapes)
                {
                    shapes.Extent(s, out lo, out hi);
                    // Swept half a second along its velocity, not grown that far every way: a machine at 15 m/s
                    // wanted colliders seven metres round every one of its shapes, behind and above as well, and
                    // kept five hundred of them.
                    Vector3 sweep = shapes.Shape(s).pointVel * Ahead;
                    lo = Vector3.Min(lo, lo + sweep);
                    hi = Vector3.Max(hi, hi + sweep);
                }
                else shapes.StanderExtent(s - machineShapes, out lo, out hi);
                Vector3 centre = (lo + hi) * 0.5f;
                lo -= new Vector3(Reach, Reach, Reach);
                hi += new Vector3(Reach, Reach, Reach);

                int cx0 = Mathf.FloorToInt(lo.x / chunkSize), cx1 = Mathf.FloorToInt(hi.x / chunkSize);
                int cz0 = Mathf.FloorToInt(lo.z / chunkSize), cz1 = Mathf.FloorToInt(hi.z / chunkSize);
                for (int cz = cz0; cz <= cz1; cz++)
                {
                    for (int cx = cx0; cx <= cx1; cx++)
                    {
                        SandBed.Chunk c = bed.ChunkAt(cx, cz);
                        if (c == null || c.filled <= 0) continue;
                        // Only a shape that may reach the chunk's sand -- its underside within reach of the
                        // highest sand there. A machine's chassis, arms and cargo, far above the sand, asked for
                        // colliders round themselves as its wheels did, and a big machine kept hundreds alive.
                        if (lo.y > HighestSand(c)) continue;
                        Entry e = c.colliderHandle as Entry;
                        if (e != null && e.wanted == stamp && !c.colliderDirty) continue;

                        // Distance from the shape to the chunk's middle, for the order.
                        float mx = (cx + 0.5f) * chunkSize - centre.x, mz = (cz + 0.5f) * chunkSize - centre.z;
                        float d = mx * mx + mz * mz;

                        if (e != null)
                        {
                            bool first = e.wanted != stamp;
                            e.wanted = stamp;
                            if (!c.colliderDirty || !first) continue;
                        }
                        // Once per chunk per step, by stamp: a machine is hundreds of shapes,
                        // each near the same few chunks, and looking each one up in the list
                        // was most of a slow step.
                        if (c.wantStamp != stamp)
                        {
                            c.wantStamp = stamp;
                            c.wantIndex = wantNow.Count;
                            wantNow.Add(c);
                            wantDistance.Add(d);
                        }
                        else if (d < wantDistance[c.wantIndex]) wantDistance[c.wantIndex] = d;
                    }
                }
            }

            // Nearest first, as far as the budget goes; at least one a step.
            // The timer is a field: this runs a sand step at a time, and a Stopwatch a step is an
            // allocation a step for nothing.
            timer.Reset(); timer.Start();   // Reset first: Start() does nothing to a running watch
            while (wantNow.Count > 0)
            {
                int best = 0;
                for (int k = 1; k < wantNow.Count; k++) if (wantDistance[k] < wantDistance[best]) best = k;
                SandBed.Chunk c = wantNow[best];
                wantNow.RemoveAt(best);
                wantDistance.RemoveAt(best);
                Build(c);
                if (timer.Elapsed.TotalMilliseconds > budgetMs) break;
            }

            // Colliders nothing has wanted for a while.
            for (int k = live.Count - 1; k >= 0; k--)
            {
                Entry e = live[k].colliderHandle as Entry;
                if (e == null) { live.RemoveAt(k); continue; }
                if (stamp - e.wanted > KeepSteps) { Destroy(live[k]); }
            }
        }

        /// <summary>The top of the highest sand in a chunk, measured once a step.</summary>
        private float HighestSand(SandBed.Chunk c)
        {
            if (c.topStamp == stamp) return c.topCache;
            float most = float.MinValue;
            for (int k = 0; k < c.top.Length; k++)
                if (c.top[k] - c.bottom[k] > 0.004f && c.top[k] > most) most = c.top[k];
            c.topStamp = stamp;
            c.topCache = most;
            return most;
        }

        /// <summary>
        /// How far the surface must have moved before a collider is cooked again, in metres: at
        /// once past <see cref="CookTolerance"/>, or past <see cref="CookDrift"/> more than it had
        /// moved last time it was looked at. Every change used to recook it -- under a wheel
        /// pressing soft sand, every step -- and a recooked collider loses the contacts PhysX was
        /// holding against it, the static friction among them: a machine standing or braking on
        /// sand slid on as though on ice, and the cooking was a large part of a slow step besides.
        /// </summary>
        public static float CookTolerance = 0.01f;
        public static float CookDrift = 0.002f;

        /// <summary>Colliders cooked since this was last read, and the sand steps they were cooked
        /// on. To see what the drifting is worth on a scene, from the console:
        /// <c>SandBedColliders.Cooks</c> and <c>SandBedColliders.CookSteps</c>.</summary>
        public static long Cooks, CookSteps;

        private void Build(SandBed.Chunk c)
        {
            CookSteps++;
            bed.SurfaceOf(c, EdgeDip, heights, sand, known);

            Entry was = c.colliderHandle as Entry;
            if (was != null && was.cooked != null)
            {
                float moved = 0f;
                bool same = true;
                for (int v = 0; v < heights.Length; v++)
                {
                    if (sand[v] != was.cookedSand[v]) { same = false; break; }
                    float d = heights[v] - was.cooked[v];
                    if (d < 0f) d = -d;
                    if (d > moved) moved = d;
                }
                // A collider is left alone until the sand has moved CookDrift further since the
                // last time it was looked at -- a rate, not a birthday. Waiting on an age meant
                // that sand settling slowly under a wheel stood half a second wrong before anyone
                // looked, which is half a second of the machine driving on a surface that is no
                // longer there, and its track appearing late and behind it. Compared with how far
                // it has gone since last time, sand that has stopped moving costs nothing however
                // old the collider is, and sand that is still moving is cooked at once.
                float since = moved - was.lastMoved;
                if (same && moved < CookTolerance && since < CookDrift)
                {
                    was.lastMoved = moved;
                    was.wanted = stamp;
                    c.colliderDirty = moved >= CookDrift;
                    return;
                }
            }

            // A vertex over unknown ground is in no triangle, but it still counts towards the
            // mesh's bounds; parked at a known height it does not stretch them to the abyss.
            float spare = 0f;
            for (int v = 0; v < Side * Side; v++) if (known[v]) { spare = heights[v]; break; }

            int baseI = c.cx * SandBed.ChunkSide, baseJ = c.cz * SandBed.ChunkSide;
            for (int vj = 0; vj < Side; vj++)
                for (int vi = 0; vi < Side; vi++)
                {
                    int v = vi + vj * Side;
                    vertices[v] = new Vector3((baseI + vi + 0.5f) * SandBed.Cell,
                                              known[v] ? heights[v] : spare,
                                              (baseJ + vj + 0.5f) * SandBed.Cell);
                }

            int t = 0;
            for (int qj = 0; qj < SandBed.ChunkSide; qj++)
            {
                for (int qi = 0; qi < SandBed.ChunkSide; qi++)
                {
                    int a = qi + qj * Side, b = a + 1, c2 = a + Side, d = c2 + 1;
                    if (!(sand[a] || sand[b] || sand[c2] || sand[d])) continue;
                    if (!(known[a] && known[b] && known[c2] && known[d])) continue;
                    scratch[t++] = a; scratch[t++] = c2; scratch[t++] = d;
                    scratch[t++] = a; scratch[t++] = d; scratch[t++] = b;
                }
            }
            c.colliderDirty = false;

            Entry e = c.colliderHandle as Entry;
            if (t == 0)
            {
                if (e != null) Destroy(c);
                return;
            }
            if (e == null)
            {
                e = new Entry();
                // "Floor" in the name, and the camera's own tag for things it looks through: Besiege's camera
                // keeps itself out of the environment by sphere-casting from what it orbits, and skips a
                // collider so named or so tagged. Without them, sand between the camera and the machine --
                // a pile, or the collider over a hole a wheel had just dug -- pulled the camera in close.
                e.go = new GameObject("SandFloor");
                try { e.go.tag = "CamIgnored"; } catch (System.Exception) { }
                e.go.layer = Layer;
                e.go.transform.SetParent(parent, false);
                e.collider = e.go.AddComponent<MeshCollider>();

                BedContact contact = e.go.AddComponent<BedContact>();
                contact.Bed = bed;
                e.mesh = new Mesh();
                e.mesh.hideFlags = HideFlags.HideAndDontSave;
                // The ground raycasts must see through it: it stands on the floor they measure.
                GroundField.Ignore(e.collider, true);
                c.colliderHandle = e;
                live.Add(c);
            }
            e.wanted = stamp;
            if (e.go.layer != Layer) e.go.layer = Layer;
            // The grip of what most of the chunk's surface is.
            {
                int[] tally = mediumTally;
                System.Array.Clear(tally, 0, tally.Length);
                for (int k = 0; k < c.top.Length; k++)
                    if (c.top[k] - c.bottom[k] > 0.004f) tally[SandMaterials.MediumOf(c.material[k])]++;
                int most = 0;
                for (int m = 1; m < tally.Length; m++) if (tally[m] > tally[most]) most = m;
                if (e.collider.sharedMaterial != physic[most]) e.collider.sharedMaterial = physic[most];
            }

            int[] tris = new int[t];
            System.Array.Copy(scratch, tris, t);
            e.mesh.Clear();
            e.mesh.vertices = vertices;
            e.mesh.triangles = tris;
            e.mesh.RecalculateBounds();
            // Assigning the same mesh again does not recook it; clearing first does.
            e.collider.sharedMesh = null;
            e.collider.sharedMesh = e.mesh;
            Cooks++;
            if (e.cooked == null) { e.cooked = new float[heights.Length]; e.cookedSand = new bool[heights.Length]; }
            System.Array.Copy(heights, e.cooked, heights.Length);
            System.Array.Copy(sand, e.cookedSand, sand.Length);
            e.cookedAt = Time.time;
            e.lastMoved = 0f;
        }

        /// <summary>
        /// Gives the sand round a point a collider now, for something that will ask PhysX about it this frame -- the god
        /// tool's explode on click, whose raycast went through sand with no collider to the ground under it. The chunk
        /// and its eight neighbours, cooked at once if they need it and kept for as long as this is asked each frame.
        /// </summary>
        public void WantAt(Vector3 p)
        {
            if (bed == null) return;
            float chunkSize = SandBed.Cell * SandBed.ChunkSide;
            int cx = Mathf.FloorToInt(p.x / chunkSize), cz = Mathf.FloorToInt(p.z / chunkSize);
            for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    SandBed.Chunk c = bed.ChunkAt(cx + dx, cz + dz);
                    if (c == null || c.filled <= 0) continue;
                    Entry e = c.colliderHandle as Entry;
                    if (e == null || c.colliderDirty) Build(c);
                    else e.wanted = stamp;
                }
        }

        /// <summary>Drops a chunk's collider, for a chunk the bed has let go of.</summary>
        public void Destroy(SandBed.Chunk c)
        {
            Entry e = c.colliderHandle as Entry;
            c.colliderHandle = null;
            live.Remove(c);
            if (e == null) return;
            if (e.collider != null) GroundField.Ignore(e.collider, false);
            if (e.go != null) Object.Destroy(e.go);
            if (e.mesh != null) Object.Destroy(e.mesh);
        }

        public void Dispose()
        {
            BedContact.UngroundAll();
            for (int k = live.Count - 1; k >= 0; k--) Destroy(live[k]);
            if (physic != null) { for (int m = 0; m < physic.Length; m++) if (physic[m] != null) Object.Destroy(physic[m]); physic = null; }
        }

        /// <summary>
        /// Puts every live collider back into the ground raycasts' list of things to see through.
        ///
        /// A level change calls <see cref="GroundField.Forget"/>, which clears that list so the last
        /// level's floor is not inherited -- and these colliders go on living, because they hang off
        /// the field and a scene load does not destroy it. Only a collider nothing has wanted for
        /// <see cref="KeepSteps"/> steps is destroyed, so for the first fraction of a second of the
        /// new level the bed's own surface is standing there without being in the list, and the
        /// first ground cast measures the sand as the floor underneath it. Sand then settles onto
        /// the bed, and those tiles are never cast again.
        /// </summary>
        public void Reignore()
        {
            for (int k = 0; k < live.Count; k++)
            {
                Entry e = live[k].colliderHandle as Entry;
                if (e != null && e.collider != null) GroundField.Ignore(e.collider, true);
            }
        }
    }

    /// <summary>
    /// On each bed collider: hands what PhysX pushed back with to the bed, so the sand sinks
    /// under a machine in proportion to its weight. The impulse is the pair's for the fixed
    /// step, shared between its contact points.
    /// </summary>
    public class BedContact : MonoBehaviour
    {
        public SandBed Bed;

        // ---- the level's people on the sand. A knight or a peasant walks by its own AI, which asks "am I on the
        // ground" by a raycast against its own groundLayerMask: the sand's collider sits on the level floor's
        // layer, but an entity whose mask did not name that layer thought itself falling on sand -- it glided
        // along, and fell over and died. So a person touching the sand has the sand's layer added to its mask.
        // And it keeps its own grip there: the sand's slippery media are combined by the lower of the two
        // frictions, which made people skate, so while it touches the sand its collider wears a material
        // combined by the higher, 0.6, and gets its own back when it steps off.
        private static readonly Dictionary<int, EntityAI> aiOf = new Dictionary<int, EntityAI>();
        private static readonly Dictionary<int, PhysicMaterial> worn = new Dictionary<int, PhysicMaterial>();
        private static readonly Dictionary<int, Collider> wearers = new Dictionary<int, Collider>();
        private static readonly Dictionary<int, EntityAI> wearerAi = new Dictionary<int, EntityAI>();

        // A person's own slope limits while it is on the sand, to give back when it steps off. Its AI takes a contact
        // whose normal is further from up than MaxAscent (a cosine) for a wall, and walks round it; the sand stands at
        // thirty degrees and its collider's facets steeper here and there, so people would not set foot on a slope of it.
        // On the sand they take anything up to fifty-three degrees (0.6) as ground and climb it (walkUpSlopes).
        // And they do not take the sand for an obstacle to jump or turn back from. Their AI measures a thing it walks into
        // by its collider's bounding box -- the top of the box over its own feet -- and jumps what is up to its jump
        // height and turns away from what is higher. A sand collider is a whole chunk, two metres square, and its box's
        // top is the highest sand in it: at the foot of a pile a person saw the pile's top as a wall, or a jump, every
        // step. With jumping off (canJump) that check is skipped, and they walk up the slope as up any; they have their
        // jump back when they step off. (Their climbing speed is left alone: faster, they ran into the next slope hard
        // enough to die of it.)
        private struct Limits { public float ascent; public bool slopes, jump; public int feet; }
        private static readonly Dictionary<int, Limits> limitsOf = new Dictionary<int, Limits>();
        private const float SandAscent = 0.6f;
        private static PhysicMaterial footing;

        private void Ground(Collision c)
        {
            try
            {
                Rigidbody rb = c.rigidbody;
                if (rb == null) return;
                int id = rb.GetInstanceID();
                EntityAI ai;
                if (!aiOf.TryGetValue(id, out ai))
                {
                    if (aiOf.Count > 5000) aiOf.Clear();
                    ai = rb.GetComponentInParent<EntityAI>();
                    aiOf[id] = ai;
                }
                int bit = 1 << gameObject.layer;
                if (ai != null && (ai.groundLayerMask.value & bit) == 0) ai.groundLayerMask = ai.groundLayerMask.value | bit;

                Collider foot = c.collider;
                if (foot == null) return;
                int fid = foot.GetInstanceID();
                if (worn.ContainsKey(fid)) return;
                if (footing == null)
                {
                    footing = new PhysicMaterial("SandFooting");
                    footing.dynamicFriction = 0.6f;
                    footing.staticFriction = 0.6f;
                    footing.frictionCombine = PhysicMaterialCombine.Maximum;
                    footing.bounceCombine = PhysicMaterialCombine.Minimum;
                }
                worn[fid] = foot.sharedMaterial;
                wearers[fid] = foot;
                foot.sharedMaterial = footing;
                if (ai != null && ai.movement != null)
                {
                    int aid = ai.GetInstanceID();
                    Limits l;
                    if (!limitsOf.TryGetValue(aid, out l))
                    {
                        l.ascent = ai.movement.MaxAscent;
                        l.slopes = ai.movement.walkUpSlopes;
                        l.jump = ai.movement.canJump;
                        l.feet = 0;
                        ai.movement.MaxAscent = Mathf.Min(l.ascent, SandAscent);
                        ai.movement.walkUpSlopes = true;
                        ai.movement.canJump = false;
                        ai.movement.hitHighObject = false;
                    }
                    l.feet++;
                    limitsOf[aid] = l;
                    wearerAi[fid] = ai;
                }
            }
            catch (System.Exception) { }
        }

        private static void Unground(Collider foot)
        {
            if (foot == null) return;
            int fid = foot.GetInstanceID();
            PhysicMaterial was;
            if (!worn.TryGetValue(fid, out was)) return;
            worn.Remove(fid);
            wearers.Remove(fid);
            try { foot.sharedMaterial = was; } catch (System.Exception) { }
            EntityAI ai;
            if (wearerAi.TryGetValue(fid, out ai))
            {
                wearerAi.Remove(fid);
                if (ai == null) return;
                int aid = ai.GetInstanceID();
                Limits l;
                if (!limitsOf.TryGetValue(aid, out l)) return;
                if (--l.feet > 0) { limitsOf[aid] = l; return; }
                limitsOf.Remove(aid);
                try
                {
                    if (ai.movement != null) { ai.movement.MaxAscent = l.ascent; ai.movement.walkUpSlopes = l.slopes; ai.movement.canJump = l.jump; }
                }
                catch (System.Exception) { }
            }
        }

        /// <summary>Gives every person still wearing the sand's footing their own material back (the level is going away,
        /// or the sand is).</summary>
        public static void UngroundAll()
        {
            List<Collider> feet = new List<Collider>(wearers.Values);
            for (int i = 0; i < feet.Count; i++) Unground(feet[i]);
            worn.Clear();
            wearers.Clear();
            wearerAi.Clear();
            limitsOf.Clear();
            aiOf.Clear();
        }

        private void OnCollisionEnter(Collision c) { Report(c); }
        private void OnCollisionStay(Collision c) { Report(c); }
        private void OnCollisionExit(Collision c) { if (c != null) Unground(c.collider); }

        /// <summary>Scales what the sand pushes back with, which is its own bearing pressure over
        /// the patch of the thing touching it and nothing else. One is as it should be; below one
        /// every medium is softer than it is, which is a way of testing a machine's own springs
        /// against it. Zero turns the whole thing off and leaves machines standing on the collider
        /// alone, as they did before any of this.</summary>
        public static float SupportGain = 1f;

        /// <summary>Over how many metres a thing sinks into the sand before the sand is holding
        /// it up at all, in metres. The resistance is a step -- a medium bears what it bears, or it
        /// does not -- and a step would arrive as a bang, so it is eased in over the first few
        /// centimetres of going in.</summary>
        public const float BearingRamp = 0.05f;

        /// <summary>How much of one of its own widths a single contact stands for, in metres,
        /// when there is only the one and so nothing to measure the patch by. A spike touches the
        /// sand in a point; it is not a point, and how big it is not is a matter of how fine the
        /// sand is -- a couple of centimetres, about what a grain field's spacing is worth.</summary>
        public const float ContactWidth = 0.02f;

        /// <summary>The area of the patch of a collider that is touching, in square metres: how
        /// far its contacts reach across the face they are on.
        ///
        /// Not the collider's bounding box. A wheel block two metres across standing on sand bears
        /// on the few square centimetres of it that are touching, and using the box made it eighty
        /// thousand newtons -- a catapult. A blade a metre square and five centimetres thick bore
        /// a square metre and could not cut through anything, which is the whole of what it is for.
        ///
        /// The contacts say the answer themselves, which is also why it works from any direction:
        /// they are on whatever face it is, and there is no case to tell.
        /// </summary>
        private static float Patch(ContactPoint[] points, Vector3 normal)
        {
            Vector3 n = normal.normalized;
            Vector3 side = Mathf.Abs(n.y) > 0.9f ? Vector3.right : Vector3.up;
            Vector3 u = Vector3.Cross(n, side);
            if (u.sqrMagnitude < 1e-8f) u = Vector3.right;
            u = u.normalized;
            Vector3 v = Vector3.Cross(n, u);
            float lo = float.MaxValue, hi = float.MinValue, lo2 = float.MaxValue, hi2 = float.MinValue;
            for (int k = 0; k < points.Length; k++)
            {
                Vector3 d = points[k].point - points[0].point;
                float a = Vector3.Dot(d, u), b = Vector3.Dot(d, v);
                if (a < lo) lo = a;
                if (a > hi) hi = a;
                if (b < lo2) lo2 = b;
                if (b > hi2) hi2 = b;
            }
            return (hi - lo + ContactWidth) * (hi2 - lo2 + ContactWidth);
        }

        private void Report(Collision c)
        {
            if (Bed == null) return;
            // The level's people stand on the sand and leave it be; they are only made to stand on it properly.
            if (ShapeFeed.IsEntity(c.rigidbody)) { Ground(c); return; }
            ContactPoint[] points = c.contacts;
            if (points == null || points.Length == 0) return;
            // Only what pushes into the sand bears on it. The impulse includes the friction
            // that drives a powered wheel along, which pressed ruts as though the machine were
            // many times heavier than it is.
            Vector3 normal = Vector3.zero;
            for (int k = 0; k < points.Length; k++) normal += points[k].normal;
            float len = normal.magnitude;
            float pushed = len > 1e-4f ? Mathf.Abs(Vector3.Dot(c.impulse, normal / len)) : c.impulse.magnitude;
            float force = pushed / Mathf.Max(Time.fixedDeltaTime, 1e-4f);
            float each = force / points.Length;
            Vector3 rel = c.relativeVelocity;
            Rigidbody body = c.rigidbody;
            bool push = SupportGain > 0f && body != null && !body.isKinematic;
            float patch = push ? Patch(points, normal / (len > 1e-4f ? len : 1f)) : 0f;
            for (int k = 0; k < points.Length; k++)
            {
                ContactPoint at = points[k];
                Vector3 n = at.normal;
                float into = Mathf.Abs(rel.x * n.x + rel.y * n.y + rel.z * n.z);
                Bed.AddLoad(at.point, n, each, into);

                // Standing in the sand, not only on a collider cooked before the sand moved.
                //
                // The colliders are deliberately stale -- they are recooked a centimetre of drift
                // at a time, and that is most of the saving -- so a machine is held up at a height
                // the sand has already left, or pushed into a sand that has risen above it. What
                // holds it is a force, not a fresh mesh: the gap between the sand's surface about
                // here and the point bearing on it, scaled by how much sand there is left to give.
                //
                // Scaled by the load, because that is the sand's answer already: <see cref="Press"/>
                // presses a column by sqrt(pressure/firmness), so what resists a wheel is what it is
                // putting in. Scaling by it keeps a heavy machine and a light one on the same footing
                // as the ruts they leave, rather than inventing a second opinion about firmness.
                // What the sand will bear, over whatever cross-section of this thing is going into
                // it. Not a share of the load: that was the old law, and it could never tell a
                // spike from a plate, because both got back exactly what they put in and settled
                // the same depth. The load decides only whether the bearing is enough; the shape
                // decides what there is to bear against, and a spike has almost none of it.
                //
                // Whatever way it is going in, up or down or sideways. A contact on the side of the
                // sand is not a load to press under -- Press must not read it as weight, and does
                // not -- but it is still sand being parted, and the one law covers all of them.
                if (!push || patch <= 0f) continue;
                float top, room, bear;
                if (!Bed.Support(at.point.x, at.point.z, out top, out room, out bear)) continue;
                if (bear <= 0f) continue;
                float gone = top - at.point.y;
                float f;
                if (gone > 0f)
                {
                    // In the sand: what the medium bears over the patch, eased in over the first
                    // few centimetres so that a step is not a bang.
                    f = bear * patch * (gone >= BearingRamp ? 1f : gone / BearingRamp);
                    // Past the point where it can compact at all it is simply ground, and ground
                    // does not give: the sand cannot resist more by being pressed, so the rest of
                    // the way in is taken over the collider's own account rather than this one's.
                    if (room > 1e-4f && gone > room) f += bear * patch * (gone - room) / room;
                }
                else
                {
                    // Above it, on a collider cooked before the sand fell away: the sand is lower
                    // than the thing standing on it, and it should be going in rather than
                    // floating over its own rut. Downwards, and no harder than the bearing.
                    f = bear * patch;
                    if (gone < -BearingRamp) f = bear * patch * -1f;
                }
                body.AddForceAtPosition(n * (f * SupportGain), at.point);
            }
        }
    }
}
