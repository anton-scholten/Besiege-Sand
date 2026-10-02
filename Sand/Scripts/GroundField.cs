using UnityEngine;

namespace SandMod
{
    /// <summary>What the solver needs of the ground, so it can be driven without the engine.</summary>
    public interface ISandGround
    {
        bool Built { get; }
        float Height(Vector3 p);
        Vector3 Normal(Vector3 p);
    }

    /// <summary>
    /// The level's solid ground, as a height field the solver can ask cheaply.
    ///
    /// This exists because of two measured facts. A Besiege level has **no Unity
    /// Terrain** -- `Terrain.activeTerrains` is empty and no `TerrainCollider` appears
    /// in any scene -- so there is no heightmap to sample. And the ground is *concave*
    /// mesh geometry, which the shape feed deliberately does not send to the solver:
    /// a concave mesh is not an analytic shape and a grain cannot be tested against
    /// one in a few instructions.
    ///
    /// So the world is rasterised by raycasting a grid down onto it, and the solver
    /// treats the result as a floor. Besiege's own water does the same thing with
    /// `TerrainHeightRenderer`; this is that idea with raycasts instead of a camera,
    /// because it has to run on the CPU beside the CPU solver.
    ///
    /// The grid is kept in tiles of four metres, a quarter of a metre a sample, and a
    /// tile is cast once, the first time sand needs it, and kept until the level
    /// changes. It used to be one grid over the whole area the sand covered, rebuilt
    /// from scratch -- up to nine thousand raycasts in one frame -- every time sand left
    /// that area: once per region placed when a run started, again for all of them
    /// together, and again whenever a machine scattered sand past the edge. That was
    /// the stall when a run began and when a machine drove into a pile.
    ///
    /// Only static geometry is sampled. Anything with a rigidbody that can move is
    /// skipped, because it is already in the solver as a shape and would otherwise be
    /// baked into the floor where it happened to be standing.
    ///
    /// Tiles are only added on the main thread between solver passes; the solver's
    /// threads only read, and only while the main thread waits for them.
    /// </summary>
    public class GroundField : ISandGround, IBedGround
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
        private readonly System.Collections.Generic.List<long> wanted = new System.Collections.Generic.List<long>();

        /// <summary>Raycasts made since this was last read, for the field's timing report.</summary>
        public int Raycasts;

        // Colliders that are not the level: the bed's own, which stand on the floor.
        private static readonly System.Collections.Generic.HashSet<int> ignored =
            new System.Collections.Generic.HashSet<int>();

        /// <summary>Whether the ground raycasts look through a collider.</summary>
        public static bool IsIgnored(Collider c)
        {
            return ignored.Count > 0 && ignored.Contains(c.GetInstanceID());
        }

        /// <summary>
        /// Raises the sample nearest a point to at least <paramref name="height"/>, for a
        /// surface the raycasts left out on purpose but that a region of sand was placed on --
        /// a level object with physics, which the casts skip as something that moves. Returns
        /// false where the sample's tile has not been cast.
        /// </summary>
        public bool Raise(float x, float z, float height)
        {
            int ix = Mathf.RoundToInt(x / Cell), iz = Mathf.RoundToInt(z / Cell);
            int tx = ix >= 0 ? ix / Side : (ix + 1) / Side - 1;
            int tz = iz >= 0 ? iz / Side : (iz + 1) / Side - 1;
            float[] h;
            if (!tiles.TryGetValue(Key(tx, tz), out h)) return false;
            int k = (ix - tx * Side) + (iz - tz * Side) * Side;
            if (height > h[k]) h[k] = height;
            return true;
        }

        /// <summary>Has the ground raycasts look through a collider, or stop doing so.</summary>
        public static void Ignore(Collider c, bool ignore)
        {
            if (ignore) ignored.Add(c.GetInstanceID());
            else ignored.Remove(c.GetInstanceID());
        }

        /// <summary>Layer of the floor the raycasts last found: the bed's colliders go on it,
        /// so whatever collides with the floor collides with the sand.</summary>
        public int GroundLayer;

        private static readonly System.Collections.Generic.Dictionary<int, bool> blockBodies =
            new System.Collections.Generic.Dictionary<int, bool>();

        private static bool IsBlock(Rigidbody rb)
        {
            int id = rb.GetInstanceID();
            bool yes;
            if (!blockBodies.TryGetValue(id, out yes))
            {
                yes = rb.GetComponentInParent<BlockBehaviour>() != null;
                if (blockBodies.Count > 8192) blockBodies.Clear();
                blockBodies[id] = yes;
            }
            return yes;
        }

        public bool Built { get { return tiles.Count > 0; } }
        public int TileCount { get { return tiles.Count; } }

        public void Forget() { tiles.Clear(); }

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

            System.Diagnostics.Stopwatch timer = System.Diagnostics.Stopwatch.StartNew();
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

        /// <summary>
        /// Casts the tiles under every grain and the ones around them, whatever it costs:
        /// a grain over a tile that is not there would fall through the floor. Grains come
        /// in a few clumps, so this is a lookup a grain and a handful of tiles.
        /// </summary>
        public void EnsureUnder(Grain[] grains, int count)
        {
            wanted.Clear();
            long last = long.MinValue;
            for (int i = 0; i < count; i++)
            {
                long k = Key(TileOf(grains[i].pos.x), TileOf(grains[i].pos.z));
                if (k == last) continue;
                last = k;
                if (!wanted.Contains(k))
                {
                    wanted.Add(k);
                    if (wanted.Count > 512) break;   // sand scattered everywhere: the rest next step
                }
            }
            for (int w = 0; w < wanted.Count; w++)
            {
                int tx = (int)(wanted[w] >> 32), tz = (int)(wanted[w] & 0xFFFFFFFFL);
                for (int dz = -1; dz <= 1; dz++)
                    for (int dx = -1; dx <= 1; dx++)
                        if (!tiles.ContainsKey(Key(tx + dx, tz + dz))) Cast(tx + dx, tz + dz);
            }
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
            Raycasts += Side * Side;
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
                if (ignored.Count > 0 && ignored.Contains(c.GetInstanceID())) continue;   // the sand's own surface

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

        /// <summary>
        /// Ground height under a position, bilinear. Returns a value far below
        /// everything where there is no answer, so a grain over a hole falls rather
        /// than resting on nothing.
        /// </summary>
        public float Height(Vector3 p)
        {
            return Height(p.x, p.z);
        }

        /// <summary>
        /// Surface normal from the height gradient. This is what makes a slope shed
        /// sand instead of holding it in a staircase of flat cells.
        /// </summary>
        public Vector3 Normal(Vector3 p)
        {
            float e = Cell;
            float hl = Height(p.x - e, p.z);
            float hr = Height(p.x + e, p.z);
            float hd = Height(p.x, p.z - e);
            float hu = Height(p.x, p.z + e);
            if (hl <= NoGround || hr <= NoGround || hd <= NoGround || hu <= NoGround)
                return Vector3.up;

            Vector3 n = new Vector3(hl - hr, 2f * e, hd - hu);
            return n.sqrMagnitude > 1e-9f ? n.normalized : Vector3.up;
        }
    }
}
