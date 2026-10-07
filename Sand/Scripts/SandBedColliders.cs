using System.Collections.Generic;
using UnityEngine;

namespace SandMod
{
    /// <summary>
    /// Makes the bed solid to machines: a static mesh collider for each chunk of it near
    /// something that moves, so PhysX holds a machine up on the pile the way it holds it up on
    /// the ground, with friction, rolling and all.
    ///
    /// The bed used to have no collider. A machine was held up only by the grains it woke,
    /// which it then shoved aside, so it settled through a pile to the floor beneath and drove
    /// along the bottom with the sand standing round its wheels untouched -- no ruts, and the
    /// machine seen inside the sand. Now it stands on the surface, and the force PhysX pushes
    /// back with is reported to the bed (<see cref="BedContact"/>), which sinks under it
    /// (<see cref="SandBed.Press"/>): a light machine barely marks the sand, a heavy one leaves
    /// ruts. Sand only becomes grains again where something digs (<see cref="SandBed.Release"/>).
    ///
    /// Colliders exist only for chunks within reach of a body, and are cooked a few per sand
    /// step, nearest a body first; a chunk nothing has come near for a couple of seconds loses
    /// its collider again. Cooking a concave mesh is the expensive part, and a big region has
    /// hundreds of chunks no machine will ever touch.
    /// </summary>
    public class SandBedColliders
    {
        private const int Side = SandBed.ChunkSide + 1;

        /// <summary>Metres around a body, beyond how far it will move in half a second, in
        /// which the bed has a collider.</summary>
        private const float Reach = 1.5f;

        /// <summary>Sand steps a collider is kept after nothing wants it any more.</summary>
        private const int KeepSteps = 50;

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
        }

        /// <summary>Layer the colliders go on: the level floor's, so that whatever collides
        /// with the floor collides with the sand. Set from the ground raycasts.</summary>
        public int Layer;
        private int lastLayer = -1;

        private Transform parent;
        private PhysicMaterial physic;
        private SandBed bed;
        private readonly List<SandBed.Chunk> live = new List<SandBed.Chunk>();
        private readonly List<SandBed.Chunk> wantNow = new List<SandBed.Chunk>();
        private readonly List<float> wantDistance = new List<float>();
        private int stamp;
        private static bool logged;

        private readonly float[] heights = new float[Side * Side];
        private readonly bool[] sand = new bool[Side * Side];
        private readonly bool[] known = new bool[Side * Side];
        private readonly Vector3[] vertices = new Vector3[Side * Side];
        private readonly int[] scratch = new int[SandBed.ChunkSide * SandBed.ChunkSide * 6];

        public int Count { get { return live.Count; } }

        public void Init(Transform owner, SandBed sandBed)
        {
            parent = owner;
            bed = sandBed;
            physic = new PhysicMaterial("Sand");
            physic.dynamicFriction = 0.55f;
            physic.staticFriction = 0.65f;
            physic.bounciness = 0f;
            physic.frictionCombine = PhysicMaterialCombine.Average;
            physic.bounceCombine = PhysicMaterialCombine.Minimum;
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
                Debug.Log("[Sand] bed colliders go on layer " + Layer + " (" + LayerMask.LayerToName(Layer) + ")");
            }
            wantNow.Clear();
            wantDistance.Clear();

            float chunkSize = SandBed.Cell * SandBed.ChunkSide;
            for (int s = 0; s < shapes.Count; s++)
            {
                Vector3 lo, hi;
                shapes.Extent(s, out lo, out hi);
                SandShape sh = shapes.Shape(s);
                Vector3 centre = (lo + hi) * 0.5f;
                float ahead = sh.pointVel.magnitude * 0.5f + Reach;

                int cx0 = Mathf.FloorToInt((lo.x - ahead) / chunkSize), cx1 = Mathf.FloorToInt((hi.x + ahead) / chunkSize);
                int cz0 = Mathf.FloorToInt((lo.z - ahead) / chunkSize), cz1 = Mathf.FloorToInt((hi.z + ahead) / chunkSize);
                for (int cz = cz0; cz <= cz1; cz++)
                {
                    for (int cx = cx0; cx <= cx1; cx++)
                    {
                        SandBed.Chunk c = bed.ChunkAt(cx, cz);
                        if (c == null || c.filled <= 0) continue;
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
            System.Diagnostics.Stopwatch timer = System.Diagnostics.Stopwatch.StartNew();
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

        /// <summary>
        /// How far the surface must have moved before a collider is cooked again, in metres: at
        /// once past <see cref="CookTolerance"/>, or past <see cref="CookDrift"/> once it is
        /// <see cref="CookAge"/> seconds old. Every change used to recook it -- under a wheel
        /// pressing soft sand, every step -- and a recooked collider loses the contacts PhysX was
        /// holding against it, the static friction among them: a machine standing or braking on
        /// sand slid on as though on ice, and the cooking was a large part of a slow step besides.
        /// </summary>
        private const float CookTolerance = 0.01f;
        private const float CookDrift = 0.002f;
        private const float CookAge = 0.5f;

        private void Build(SandBed.Chunk c)
        {
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
                float age = Time.time - was.cookedAt;
                if (same && moved < CookTolerance && (moved < CookDrift || age < CookAge))
                {
                    // Close enough: left as it is. Still dirty while it has drifted at all, so it
                    // is looked at again and cooked once the drift has stood long enough.
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
                e.go = new GameObject("SandBedCollider");
                e.go.layer = Layer;
                e.go.transform.SetParent(parent, false);
                e.collider = e.go.AddComponent<MeshCollider>();
                e.collider.sharedMaterial = physic;
                BedContact contact = e.go.AddComponent<BedContact>();
                contact.Bed = bed;
                e.mesh = new Mesh();
                e.mesh.hideFlags = HideFlags.HideAndDontSave;
                // The ground raycasts must see through it: it stands on the floor they measure.
                GroundField.Ignore(e.collider, true);
                if (!logged)
                {
                    logged = true;
                    Debug.Log("[Sand] bed colliders go on layer " + Layer + " (" + LayerMask.LayerToName(Layer) + ")");
                }
                c.colliderHandle = e;
                live.Add(c);
            }
            e.wanted = stamp;
            if (e.go.layer != Layer) e.go.layer = Layer;

            int[] tris = new int[t];
            System.Array.Copy(scratch, tris, t);
            e.mesh.Clear();
            e.mesh.vertices = vertices;
            e.mesh.triangles = tris;
            e.mesh.RecalculateBounds();
            // Assigning the same mesh again does not recook it; clearing first does.
            e.collider.sharedMesh = null;
            e.collider.sharedMesh = e.mesh;
            if (e.cooked == null) { e.cooked = new float[heights.Length]; e.cookedSand = new bool[heights.Length]; }
            System.Array.Copy(heights, e.cooked, heights.Length);
            System.Array.Copy(sand, e.cookedSand, sand.Length);
            e.cookedAt = Time.time;
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
            for (int k = live.Count - 1; k >= 0; k--) Destroy(live[k]);
            if (physic != null) { Object.Destroy(physic); physic = null; }
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

        private void OnCollisionEnter(Collision c) { Report(c); }
        private void OnCollisionStay(Collision c) { Report(c); }

        private void Report(Collision c)
        {
            if (Bed == null) return;
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
            for (int k = 0; k < points.Length; k++)
            {
                Vector3 n = points[k].normal;
                float into = Mathf.Abs(rel.x * n.x + rel.y * n.y + rel.z * n.z);
                Bed.AddLoad(points[k].point, n, each, into);
            }
        }
    }
}
