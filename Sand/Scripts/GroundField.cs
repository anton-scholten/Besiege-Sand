using UnityEngine;

namespace SandMod
{
    /// <summary>
    /// The level's solid ground, as a height field the sand can ask cheaply.
    ///
    /// This exists because of two measured facts. A Besiege level has **no Unity
    /// Terrain** -- `Terrain.activeTerrains` is empty and no `TerrainCollider` appears
    /// in any scene -- so there is no heightmap to sample. And the ground is *concave*
    /// mesh geometry, which is not an analytic shape and cannot be asked for a height in
    /// a few instructions.
    ///
    /// So the world is rasterised by raycasting a grid down onto it, and the bed treats
    /// the result as its floor. Besiege's own water does the same thing with
    /// `TerrainHeightRenderer`; this is that idea with raycasts instead of a camera.
    ///
    /// The grid is kept in tiles of four metres, a quarter of a metre a sample, and a
    /// tile is cast once, the first time sand needs it, and kept until the level
    /// changes. It used to be one grid over the whole area the sand covered, rebuilt
    /// from scratch -- up to nine thousand raycasts in one frame -- every time sand left
    /// that area, and again whenever a machine scattered sand past the edge. That was
    /// the stall when a run began and when a machine drove into a pile.
    ///
    /// Only static geometry is sampled. Anything with a rigidbody that can move is
    /// skipped, because it is a shape of its own and would otherwise be baked into the
    /// floor where it happened to be standing.
    ///
    /// Tiles are only added on the main thread; worker threads only read, and only while
    /// the main thread waits for them.
    /// </summary>
    public class GroundField : IBedGround
    {
        public const float Cell = 0.25f;
        private const int Side = 16;                     // samples a tile is across
        private const float TileSize = Cell * Side;
        private const float SkyTop = 2000f;
        private const float RayLength = 4000f;
        private const float NoGround = -10000f;

        private readonly System.Collections.Generic.Dictionary<long, float[]> tiles =
            new System.Collections.Generic.Dictionary<long, float[]>();
        private readonly RaycastHit[] hits = new RaycastHit[16];
        private readonly System.Diagnostics.Stopwatch timer = new System.Diagnostics.Stopwatch();


        // Colliders that are not the level: the bed's own, which stand on the floor.
        //
        // Keyed by instance ID, holding the collider it was told about: Unity recycles those
        // after destruction, so an ID alone would let one collider's answer stand for another's.
        private static readonly System.Collections.Generic.Dictionary<int, Collider> ignored =
            new System.Collections.Generic.Dictionary<int, Collider>();

        /// <summary>Whether the collider told to look through is still the one that was.</summary>
        private static bool IsIgnored(Collider c)
        {
            Collider was;
            if (!ignored.TryGetValue(c.GetInstanceID(), out was)) return false;
            if (was == c) return true;
            if (was == null) ignored.Remove(c.GetInstanceID());      // its collider has gone
            return false;
        }

        /// <summary>Has the ground raycasts look through a collider, or stop doing so.</summary>
        public static void Ignore(Collider c, bool ignore)
        {
            int id = c.GetInstanceID();
            if (ignore) ignored[id] = c;
            else
            {
                Collider was;
                if (ignored.TryGetValue(id, out was) && was == c) ignored.Remove(id);
            }
        }

        /// <summary>Layer of the floor the raycasts last found: the bed's colliders go on it,
        /// so whatever collides with the floor collides with the sand.</summary>
        public int GroundLayer;

        /// <summary>Whether a body is one of a machine's blocks. Cached, and keyed safely:
        /// see <see cref="SandObjectCache"/>.</summary>
        private static readonly SandObjectCache blockBodies = new SandObjectCache(8192);

        private static bool IsBlock(Rigidbody rb)
        {
            bool yes;
            if (!blockBodies.TryGet(rb, out yes))
            {
                yes = rb.GetComponentInParent<BlockBehaviour>() != null;
                blockBodies.Put(rb, yes);
            }
            return yes;
        }

        /// <summary>Forgets the ground cast so far, and the colliders told to look through: a
        /// bed that has gone leaves nothing behind for the next level's floor to inherit.</summary>
        public void Forget() { tiles.Clear(); ignored.Clear(); blockBodies.Clear(); }

        private static long Key(int tx, int tz) { return ((long)tx << 32) | (long)(uint)tz; }

        private static int TileOf(float v) { return Mathf.FloorToInt(v / TileSize); }

        /// <summary>
        /// Casts every missing tile under an area, plus a tile of margin, until
        /// <paramref name="budgetMs"/> has been spent (at least one tile is always cast).
        /// Returns true when the whole area is ready. Pass a negative budget for no limit.
        /// </summary>
        public bool Ensure(Bounds area, float budgetMs)
        {
            int tx0 = TileOf(area.min.x) - 1, tx1 = TileOf(area.max.x) + 1;
            int tz0 = TileOf(area.min.z) - 1, tz1 = TileOf(area.max.z) + 1;
            if ((long)(tx1 - tx0 + 1) * (tz1 - tz0 + 1) > 4096) return false;   // nonsense bounds

            // The timer is a field: Ensure is called for every brush stroke and every run, and a Stopwatch
            // each time is an allocation each time for nothing.
            timer.Reset(); timer.Start();   // Reset first: Start() does nothing to a running watch
            bool done = true;
            bool castOne = false;
            for (int tz = tz0; tz <= tz1; tz++)
            {
                for (int tx = tx0; tx <= tx1; tx++)
                {
                    if (tiles.ContainsKey(Key(tx, tz))) continue;
                    if (castOne && budgetMs >= 0f && timer.Elapsed.TotalMilliseconds > budgetMs)
                    {
                        done = false;
                        continue;
                    }
                    Cast(tx, tz);
                    castOne = true;
                }
            }
            return done;
        }

        private void Cast(int tx, int tz)
        {
            float[] h = new float[Side * Side];
            for (int z = 0; z < Side; z++)
            {
                for (int x = 0; x < Side; x++)
                {
                    Vector3 from = new Vector3((tx * Side + x) * Cell, SkyTop, (tz * Side + z) * Cell);
                    h[x + z * Side] = CastDown(from);
                }
            }
            tiles[Key(tx, tz)] = h;
        }

        /// <summary>
        /// Highest static surface under a point. RaycastAll rather than Raycast,
        /// because the first thing a ray meets may well be a block of the machine and
        /// the floor is behind it.
        /// </summary>
        private float CastDown(Vector3 from)
        {
            int n = Physics.RaycastNonAlloc(new Ray(from, Vector3.down), hits, RayLength);
            float best = NoGround;
            int layer = -1;
            for (int i = 0; i < n; i++)
            {
                Collider c = hits[i].collider;
                if (c == null || c.isTrigger) continue;

                Rigidbody rb = c.attachedRigidbody;
                if (rb != null && !rb.isKinematic) continue;   // that is a shape, not the floor
                // A machine being built is kinematic, and is not the floor either. Taken for it,
                // the ground under a machine was the machine's top, and the sand's colliders went
                // onto the machine's layer -- one the running machine does not collide with, so
                // wheels went straight through sand brushed while building.
                if (rb != null && IsBlock(rb)) continue;
                if (ignored.Count > 0 && IsIgnored(c)) continue;   // the sand's own surface

                if (hits[i].point.y > best) { best = hits[i].point.y; layer = c.gameObject.layer; }
            }
            if (layer >= 0) GroundLayer = layer;
            return best;
        }

        /// <summary>A sample of the grid, or NoGround where its tile has not been cast.</summary>
        private float Sample(int ix, int iz)
        {
            int tx = ix >= 0 ? ix / Side : (ix + 1) / Side - 1;
            int tz = iz >= 0 ? iz / Side : (iz + 1) / Side - 1;
            float[] h;
            if (!tiles.TryGetValue(Key(tx, tz), out h)) return NoGround;
            return h[(ix - tx * Side) + (iz - tz * Side) * Side];
        }

        /// <summary>The same height, for callers with no Vector3 to hand (the bed).</summary>
        public float Height(float x, float z)
        {
            float fx = x / Cell, fz = z / Cell;
            int x0 = Mathf.FloorToInt(fx), z0 = Mathf.FloorToInt(fz);
            float tx = fx - x0, tz = fz - z0;

            float h00, h10, h01, h11;
            int lx = x0 >= 0 ? x0 % Side : (x0 % Side + Side) % Side;
            int lz = z0 >= 0 ? z0 % Side : (z0 % Side + Side) % Side;
            if (lx < Side - 1 && lz < Side - 1)
            {
                // All four corners in one tile, which is fifteen times in sixteen: one lookup.
                float[] h;
                if (!tiles.TryGetValue(Key((x0 - lx) / Side, (z0 - lz) / Side), out h)) return NoGround;
                int k = lx + lz * Side;
                h00 = h[k]; h10 = h[k + 1]; h01 = h[k + Side]; h11 = h[k + Side + 1];
            }
            else
            {
                h00 = Sample(x0, z0); h10 = Sample(x0 + 1, z0);
                h01 = Sample(x0, z0 + 1); h11 = Sample(x0 + 1, z0 + 1);
            }

            // A cell touching a hole is a hole: interpolating towards -10000 would
            // pull the whole cell down into a funnel.
            if (h00 <= NoGround || h10 <= NoGround || h01 <= NoGround || h11 <= NoGround)
                return NoGround;

            float a = h00 + (h10 - h00) * tx;
            float b = h01 + (h11 - h01) * tx;
            return a + (b - a) * tz;
        }
    }
}
