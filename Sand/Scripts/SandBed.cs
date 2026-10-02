using System.Collections.Generic;
using UnityEngine;

namespace SandMod
{
    /// <summary>Ground height for the bed, so the bed does not depend on the engine.
    /// Returns a value at or below -9000 where there is no answer.</summary>
    public interface IBedGround
    {
        float Height(float x, float z);
    }

    /// <summary>The colliders the bed has to get out of the way of.</summary>
    public interface IBedShapes
    {
        int Count { get; }

        /// <summary>A box that surely contains shape <paramref name="i"/>.</summary>
        void Extent(int i, out Vector3 min, out Vector3 max);

        /// <summary>Signed distance from a point to shape <paramref name="i"/>.</summary>
        float Distance(int i, Vector3 p);

        /// <summary>A box that surely contains every shape; lets most grains skip the list.</summary>
        void Union(out Vector3 min, out Vector3 max);
    }

    /// <summary>
    /// How the shapes move, for a bed that should only give sand back where something digs.
    /// Optional: a shape list without it has every shape it reaches dig.
    /// </summary>
    public interface IBedShapeMotion
    {
        /// <summary>Velocity of shape <paramref name="i"/>'s surface at a point on it.</summary>
        Vector3 SurfaceVelocity(int i, Vector3 p);
    }

    /// <summary>Where grains the bed gives back are put.</summary>
    public interface IBedSink
    {
        int Room { get; }
        void Release(Vector3 position, int material, uint colour);
    }

    /// <summary>
    /// Sand that has come to rest, stored as a height field instead of as grains.
    ///
    /// Most of the sand in a level is asleep most of the time, and an asleep grain
    /// still costs a slot, two solver passes and a draw. A pile is a surface, so once a
    /// grain has lain still for a while its volume is added to the column of a grid
    /// beneath it and the grain itself is dropped. That is what lets the same grain
    /// budget describe a far bigger pile: only sand that is moving, or that something is
    /// touching, is ever a grain.
    ///
    /// Three rules keep it honest:
    ///
    /// * <b>Volume is conserved.</b> A grain adds exactly the volume it occupies in a
    ///   pile (its sphere divided by a packing fraction) to its column, and a column
    ///   handing sand back hands back exactly that many grains.
    /// * <b>Moving grains treat the bed as ground.</b> <see cref="TryTop"/> answers the
    ///   surface height, so the solver needs no other change.
    /// * <b>Anything that reaches into the bed wakes it.</b> <see cref="Release"/>
    ///   turns the sand around a shape back into grains, and those grains then push the
    ///   shape the way any others would. The bed holds no force of its own.
    ///
    /// A column stores a bottom and a top. The bottom is the ground when sand first
    /// arrived, the top is what the pile has grown to. The grid is sparse: sixteen by
    /// sixteen columns to a chunk, chunks created when sand first lands on them.
    ///
    /// Not thread safe for writers. The solver's worker threads only read, and only
    /// while the main thread is waiting for them, which is the order the field steps in.
    /// </summary>
    public class SandBed
    {
        public const float Cell = 0.12f;
        public const int ChunkSide = 16;

        /// <summary>A pile of grains fills this fraction of the space it occupies.</summary>
        public const float PackingFraction = 0.62f;

        private const float NoGround = -9000f;
        private const float MinThickness = 0.004f;

        /// <summary>One chunk's worth of columns.</summary>
        public class Chunk
        {
            public int cx, cz;
            public float[] top = new float[ChunkSide * ChunkSide];
            public float[] bottom = new float[ChunkSide * ChunkSide];
            public byte[] material = new byte[ChunkSide * ChunkSide];
            public uint[] colour = new uint[ChunkSide * ChunkSide];
            public int filled;           // columns thicker than MinThickness
            public bool queued;          // already in the renderer's dirty list
            public object renderHandle;  // the renderer keeps its mesh here
            public object colliderHandle;   // the collider manager keeps its collider here
            public bool colliderDirty;      // columns changed since the collider was cooked
            public bool netDirty;           // changed since it was last sent to the other players
            public int wantStamp;           // the collider manager's: last step it was asked for
            public int wantIndex;           // and where in that step's list
            public float[] packed = new float[ChunkSide * ChunkSide];   // how far each column has been pressed down

            // A column holds up to two media, one on the other: from bottom to split the lower
            // layer, from split to top the surface layer that material and colour describe.
            // With one, split is the bottom.
            public float[] split = new float[ChunkSide * ChunkSide];
            public byte[] lowMaterial = new byte[ChunkSide * ChunkSide];
            public uint[] lowColour = new uint[ChunkSide * ChunkSide];
        }

        /// <summary>Whether a column holds two media.</summary>
        private static bool Layered(Chunk c, int k)
        {
            return c.split[k] > c.bottom[k] + MinThickness && c.split[k] < c.top[k];
        }

        /// <summary>
        /// After a column's top has come down: if it has come down through its surface layer,
        /// the lower one is the surface now.
        /// </summary>
        private static void Settle(Chunk c, int k)
        {
            if (c.split[k] <= c.bottom[k]) return;
            if (c.top[k] <= c.split[k] + MinThickness)
            {
                c.material[k] = c.lowMaterial[k];
                c.colour[k] = c.lowColour[k];
                c.split[k] = c.bottom[k];
            }
        }

        private readonly Dictionary<long, Chunk> chunks = new Dictionary<long, Chunk>();
        // Oldest first. It was newest first, and while the brush was held the chunks under it
        // were queued again every frame and always went first, so a chunk that had just been
        // given sand for the first time waited -- drawn as bare ground -- until the button was
        // let go.
        private readonly List<Chunk> dirty = new List<Chunk>();
        private int dirtyHead;
        private readonly List<Chunk> retired = new List<Chunk>();

        private IBedGround ground;
        private float[] slope = new float[SandMaterials.Count];
        private float[] grainVolume = new float[SandMaterials.Count];
        private float[] grainRadius = new float[SandMaterials.Count];
        private float[] firmnessOf = new float[SandMaterials.Count];
        private float[] packLimitOf = new float[SandMaterials.Count];
        private float[] digSpeedOf = new float[SandMaterials.Count];
        private float[] adhesionOf = new float[SandMaterials.Count];

        private int filledColumns;

        // Columns that changed and have not yet been checked against their neighbours.
        private readonly List<long> active = new List<long>();
        private readonly List<long> batch = new List<long>();
        private readonly HashSet<long> activeSet = new HashSet<long>();

        private Vector3 extentMin, extentMax;
        private bool hasExtent;
        private uint rng = 2463534242u;

        /// <summary>Set by the field: the bed is only used with the CPU solver.</summary>
        public bool Enabled;

        public bool Active { get { return Enabled && filledColumns > 0; } }
        public int FilledColumns { get { return filledColumns; } }
        public int ChunkCount { get { return chunks.Count; } }

        /// <summary>Total sand held, in cubic metres.</summary>
        public float Volume
        {
            get
            {
                float sum = 0f;
                foreach (KeyValuePair<long, Chunk> kv in chunks)
                {
                    Chunk c = kv.Value;
                    for (int k = 0; k < c.top.Length; k++)
                    {
                        float t = c.top[k] - c.bottom[k];
                        if (t > MinThickness) sum += t;
                    }
                }
                return sum * Cell * Cell;
            }
        }

        public void SetGround(IBedGround g) { ground = g; }

        /// <summary>Re-reads what the solver's material table says about grain size and
        /// friction. Called whenever the table changes.</summary>
        public void SetMaterials(SandMaterials.Params[] table)
        {
            if (slope.Length != table.Length)
            {
                slope = new float[table.Length];
                grainVolume = new float[table.Length];
                grainRadius = new float[table.Length];
                firmnessOf = new float[table.Length];
                packLimitOf = new float[table.Length];
                digSpeedOf = new float[table.Length];
                adhesionOf = new float[table.Length];
            }
            for (int i = 0; i < table.Length; i++)
            {
                float r = table[i].radius;
                grainRadius[i] = r;
                grainVolume[i] = 4f / 3f * Mathf.PI * r * r * r / PackingFraction;
                // Angle of repose, as a height step per cell. A little generous so that
                // a pile the dynamic solver left at rest is not immediately disturbed.
                slope[i] = Mathf.Max(table[i].friction, 0.2f) * 1.15f;
                // A row that never set them (a table built before they existed) gets sand's.
                firmnessOf[i] = table[i].firmness > 0f ? table[i].firmness : PressStiffness;
                packLimitOf[i] = table[i].packLimit > 0f ? table[i].packLimit : MostPacking;
                digSpeedOf[i] = table[i].digSpeed > 0f ? table[i].digSpeed : DigSpeed;
                adhesionOf[i] = table[i].adhesion;
            }
        }

        public void Clear()
        {
            foreach (KeyValuePair<long, Chunk> kv in chunks) retired.Add(kv.Value);
            chunks.Clear();
            dirty.Clear();
            dirtyHead = 0;
            active.Clear();
            batch.Clear();
            activeSet.Clear();
            filledColumns = 0;
            hasExtent = false;
        }

        /// <summary>Everything the bed ever covered, padded. Only grows between clears.</summary>
        public Bounds Extent
        {
            get
            {
                if (!hasExtent) return new Bounds(Vector3.zero, Vector3.zero);
                Bounds b = new Bounds((extentMin + extentMax) * 0.5f, extentMax - extentMin);
                b.Expand(0.5f);
                return b;
            }
        }

        // ------------------------------------------------------------------- storage

        private static long Key(int cx, int cz)
        {
            return ((long)cx << 32) | (long)(uint)cz;
        }

        private static int FloorDiv(int a, int b)
        {
            int q = a / b;
            if ((a % b != 0) && ((a < 0) != (b < 0))) q--;
            return q;
        }

        private static int Mod(int a, int b)
        {
            int m = a % b;
            return m < 0 ? m + b : m;
        }

        /// <summary>The chunk at chunk coordinates (<paramref name="cx"/>, <paramref name="cz"/>), or null.</summary>
        public Chunk ChunkAt(int cx, int cz)
        {
            Chunk c;
            return chunks.TryGetValue(Key(cx, cz), out c) ? c : null;
        }

        private Chunk Find(int i, int j)
        {
            Chunk c;
            return chunks.TryGetValue(Key(FloorDiv(i, ChunkSide), FloorDiv(j, ChunkSide)), out c) ? c : null;
        }

        private Chunk FindOrCreate(int i, int j)
        {
            int cx = FloorDiv(i, ChunkSide), cz = FloorDiv(j, ChunkSide);
            long key = Key(cx, cz);
            Chunk c;
            if (!chunks.TryGetValue(key, out c))
            {
                c = new Chunk();
                c.cx = cx;
                c.cz = cz;
                chunks[key] = c;
            }
            return c;
        }

        private static int Slot(int i, int j)
        {
            return Mod(i, ChunkSide) + Mod(j, ChunkSide) * ChunkSide;
        }

        private void MarkDirty(Chunk c, int i, int j)
        {
            Queue(c);
            MarkNet(c);
            c.colliderDirty = true;
            int ei = Mod(i, ChunkSide), ej = Mod(j, ChunkSide);
            if (ei == 0) { Chunk n = Find(i - 1, j); if (n != null) n.colliderDirty = true; }
            if (ej == 0) { Chunk n = Find(i, j - 1); if (n != null) n.colliderDirty = true; }
            if (ei == 0 && ej == 0) { Chunk n = Find(i - 1, j - 1); if (n != null) n.colliderDirty = true; }
            // A column on a chunk's edge is also a vertex of the neighbouring chunk's
            // mesh, so that one has to be rebuilt too.
            int li = Mod(i, ChunkSide), lj = Mod(j, ChunkSide);
            if (li == 0) Queue(Find(i - 1, j));
            if (lj == 0) Queue(Find(i, j - 1));
            if (li == 0 && lj == 0) Queue(Find(i - 1, j - 1));
        }

        private void Queue(Chunk c)
        {
            if (c == null || c.queued) return;
            c.queued = true;
            dirty.Add(c);
        }

        private bool quietFill;

        private void Activate(int i, int j)
        {
            if (quietFill) return;
            long key = Key(i, j);
            if (activeSet.Add(key)) active.Add(key);
        }

        private static int KeyI(long key) { return (int)(key >> 32); }
        private static int KeyJ(long key) { return (int)(key & 0xFFFFFFFFL); }

        private void Grow(float x, float y, float z)
        {
            if (!hasExtent)
            {
                extentMin = extentMax = new Vector3(x, y, z);
                hasExtent = true;
                return;
            }
            if (x < extentMin.x) extentMin.x = x; else if (x > extentMax.x) extentMax.x = x;
            if (y < extentMin.y) extentMin.y = y; else if (y > extentMax.y) extentMax.y = y;
            if (z < extentMin.z) extentMin.z = z; else if (z > extentMax.z) extentMax.z = z;
        }

        // ---------------------------------------------------------------- the renderer

        /// <summary>Chunks whose mesh is out of date. The renderer takes them a few
        /// at a time.</summary>
        public Chunk TakeDirty()
        {
            if (dirtyHead >= dirty.Count)
            {
                dirty.Clear();
                dirtyHead = 0;
                return null;
            }
            Chunk c = dirty[dirtyHead++];
            // Taken from the front by an index; the spent front is dropped now and then.
            if (dirtyHead > 256 && dirtyHead * 2 > dirty.Count)
            {
                dirty.RemoveRange(0, dirtyHead);
                dirtyHead = 0;
            }
            c.queued = false;
            return c;
        }

        /// <summary>Chunks that were cleared away, so their meshes can be destroyed.</summary>
        public Chunk TakeRetired()
        {
            if (retired.Count == 0) return null;
            Chunk c = retired[retired.Count - 1];
            retired.RemoveAt(retired.Count - 1);
            return c;
        }

        public IEnumerable<Chunk> Chunks { get { return chunks.Values; } }

        /// <summary>The table row of the sand in a column, or -1 where there is none.</summary>
        public int MaterialAt(int i, int j)
        {
            Chunk c = Find(i, j);
            if (c == null) return -1;
            int k = Slot(i, j);
            return c.top[k] - c.bottom[k] > MinThickness ? c.material[k] : -1;
        }

        public bool Thickness(int i, int j, out float top, out float bottom, out uint colour)
        {
            top = bottom = 0f;
            colour = 0;
            Chunk c = Find(i, j);
            if (c == null) return false;
            int k = Slot(i, j);
            top = c.top[k];
            bottom = c.bottom[k];
            colour = c.colour[k];
            return top - bottom > MinThickness;
        }

        // ------------------------------------------------------------------ surface

        private float EffectiveTop(int i, int j, ref bool sand)
        {
            Chunk c = Find(i, j);
            if (c != null)
            {
                int k = Slot(i, j);
                if (c.top[k] - c.bottom[k] > MinThickness)
                {
                    sand = true;
                    return c.top[k];
                }
            }
            return ground.Height((i + 0.5f) * Cell, (j + 0.5f) * Cell);
        }

        /// <summary>
        /// Height of the pile's surface at a point, when there is pile there. False
        /// over bare ground, where the solver should use the ground itself.
        /// </summary>
        public bool TryTop(float x, float z, out float height)
        {
            height = 0f;
            if (filledColumns <= 0 || ground == null) return false;

            float fx = x / Cell - 0.5f, fz = z / Cell - 0.5f;
            int i0 = Mathf.FloorToInt(fx), j0 = Mathf.FloorToInt(fz);
            float tx = fx - i0, tz = fz - j0;

            bool sand = false;
            float h00 = EffectiveTop(i0, j0, ref sand);
            float h10 = EffectiveTop(i0 + 1, j0, ref sand);
            float h01 = EffectiveTop(i0, j0 + 1, ref sand);
            float h11 = EffectiveTop(i0 + 1, j0 + 1, ref sand);
            if (!sand) return false;
            if (h00 <= NoGround || h10 <= NoGround || h01 <= NoGround || h11 <= NoGround)
                return false;

            height = Mathf.Lerp(Mathf.Lerp(h00, h10, tx), Mathf.Lerp(h01, h11, tx), tz);
            return true;
        }

        /// <summary>Surface normal from the height gradient, so a slope of sand sheds
        /// grains instead of holding them in steps.</summary>
        public Vector3 Normal(float x, float z)
        {
            float hl, hr, hd, hu;
            if (!TryTop(x - Cell, z, out hl) || !TryTop(x + Cell, z, out hr)
                || !TryTop(x, z - Cell, out hd) || !TryTop(x, z + Cell, out hu))
            {
                // A neighbour that is bare ground: fall back to whatever ground says.
                float h0;
                if (!TryTop(x, z, out h0)) return Vector3.up;
                hl = SafeHeight(x - Cell, z, h0);
                hr = SafeHeight(x + Cell, z, h0);
                hd = SafeHeight(x, z - Cell, h0);
                hu = SafeHeight(x, z + Cell, h0);
            }
            Vector3 n = new Vector3(hl - hr, 2f * Cell, hd - hu);
            return n.sqrMagnitude > 1e-9f ? n.normalized : Vector3.up;
        }

        private float SafeHeight(float x, float z, float fallback)
        {
            float h;
            if (TryTop(x, z, out h)) return h;
            h = ground.Height(x, z);
            return h > NoGround ? h : fallback;
        }

        // -------------------------------------------------------------------- adding

        /// <summary>Volume one grain of this material occupies in a pile.</summary>
        public float GrainVolume(int material)
        {
            return grainVolume[Mathf.Clamp(material, 0, grainVolume.Length - 1)];
        }

        /// <summary>
        /// Adds sand to the column under a point. Returns false, changing nothing, when
        /// the ground there is unknown -- sand cannot be stored on nothing.
        /// </summary>
        public bool AddVolume(float x, float z, float volume, int material, uint colour)
        {
            int i = Mathf.FloorToInt(x / Cell), j = Mathf.FloorToInt(z / Cell);
            return Deposit(i, j, volume / (Cell * Cell), material, colour);
        }

        /// <summary>Adds a thickness to a column. The column takes the incoming colour
        /// in proportion to how much of it the incoming sand now is.</summary>
        private bool Deposit(int i, int j, float thickness, int material, uint colour)
        {
            Chunk c = Find(i, j);
            int k = Slot(i, j);
            bool had = c != null && (c.top[k] - c.bottom[k] > MinThickness);

            if (!had)
            {
                float g = ground.Height((i + 0.5f) * Cell, (j + 0.5f) * Cell);
                if (g <= NoGround) return false;
                if (c == null) c = FindOrCreate(i, j);
                c.bottom[k] = g;
                c.top[k] = g + thickness;
                c.split[k] = g;
                c.material[k] = (byte)material;
                c.colour[k] = colour;
                c.packed[k] = 0f;
                if (thickness > MinThickness) { c.filled++; filledColumns++; }
            }
            else if (c.material[k] == material)
            {
                // The same medium thickens the surface layer.
                float surface = c.top[k] - (Layered(c, k) ? c.split[k] : c.bottom[k]);
                float share = thickness / (surface + thickness);
                c.top[k] += thickness;
                c.colour[k] = Blend(c.colour[k], colour, share);
            }
            else
            {
                // Another medium lies on top of it as a layer of its own, so neither turns into
                // the other. The column used to take whichever there was more of: wet sand
                // landing on a dry pile became dry sand, and the other way round. A column holds
                // two; only a third pushes the old surface into the layer below it, as whichever
                // of the two there was more of.
                if (Layered(c, k))
                {
                    float low = c.split[k] - c.bottom[k], up = c.top[k] - c.split[k];
                    if (up > low) c.lowMaterial[k] = c.material[k];
                    c.lowColour[k] = Blend(c.lowColour[k], c.colour[k], up / (low + up));
                }
                else
                {
                    c.lowMaterial[k] = c.material[k];
                    c.lowColour[k] = c.colour[k];
                }
                c.split[k] = c.top[k];
                c.top[k] += thickness;
                c.material[k] = (byte)material;
                c.colour[k] = colour;
            }

            Grow((i + 0.5f) * Cell, c.top[k], (j + 0.5f) * Cell);
            MarkDirty(c, i, j);
            Activate(i, j);
            return true;
        }

        /// <summary>Takes sand off a column. Emptying it frees the column for good.</summary>
        private void Withdraw(Chunk c, int i, int j, float thickness)
        {
            int k = Slot(i, j);
            c.top[k] -= thickness;
            if (c.top[k] - c.bottom[k] <= MinThickness)
            {
                c.top[k] = c.bottom[k];
                c.split[k] = c.bottom[k];
                c.filled--;
                filledColumns--;
            }
            else Settle(c, k);
            MarkDirty(c, i, j);
            Activate(i, j);
        }

        private static uint Blend(uint a, uint b, float t)
        {
            uint r = 0;
            for (int s = 0; s < 32; s += 8)
            {
                float ca = (a >> s) & 0xFF, cb = (b >> s) & 0xFF;
                uint v = (uint)Mathf.Clamp(Mathf.RoundToInt(ca + (cb - ca) * t), 0, 255);
                r |= v << s;
            }
            return r;
        }

        // ------------------------------------------------------------ filling a shape

        public const int ShapeBox = 0;
        public const int ShapeCylinder = 1;
        public const int ShapeEllipsoid = 2;

        /// <summary>Sample points per side across a column when filling a shape.</summary>
        private const int FillSamples = 8;

        /// <summary>The most columns one fill may cover; a bigger shape is left to the caller.</summary>
        public const int MaxFillColumns = 300000;

        /// <summary>
        /// Fills a box, a standing cylinder or an ellipsoid with sand, straight into the
        /// columns. This is how a region placed in the level starts: it is static sand, so
        /// it is stored as static sand. Filled with grains instead, a block a metre tall is
        /// sixteen layers of them, and under Besiege's gravity the solver cannot pass the
        /// weight down fast enough, so the bottom compresses and goes off like a charge.
        ///
        /// Each column takes the length of the shape along its vertical line, so the sand
        /// holds the shape's volume, resting on the ground; one placed above the ground
        /// drops onto it. A column over unknown ground is skipped. Returns how many columns
        /// received sand, or -1 when the shape would need more than
        /// <see cref="MaxFillColumns"/> and was not filled at all.
        /// </summary>
        public int FillShape(int kind, Vector3 centre, Quaternion rotation, Vector3 half,
                             int material, uint colour)
        {
            int row = int.MinValue;
            return FillShape(kind, centre, rotation, half, material, colour, ref row, int.MaxValue);
        }

        /// <summary>
        /// The same, a few rows of columns at a time: starts at <paramref name="row"/>
        /// (<c>int.MinValue</c> for the first call), stops once it has looked at about
        /// <paramref name="maxColumns"/> columns, and leaves <paramref name="row"/> at the next
        /// row to do, or <c>int.MaxValue</c> when the shape is done. A twenty-metre region is
        /// tens of thousands of columns, too many for one frame.
        /// </summary>
        public int FillShape(int kind, Vector3 centre, Quaternion rotation, Vector3 half,
                             int material, uint colour, ref int row, int maxColumns)
        {
            if (!Enabled || ground == null) { row = int.MaxValue; return 0; }

            // World box of the shape, from the rotation's matrix.
            float x = rotation.x, y = rotation.y, z = rotation.z, w = rotation.w;
            float xx = x * x, yy = y * y, zz = z * z;
            float m00 = 1f - 2f * (yy + zz), m01 = 2f * (x * y - z * w), m02 = 2f * (x * z + y * w);
            float m10 = 2f * (x * y + z * w), m11 = 1f - 2f * (xx + zz), m12 = 2f * (y * z - x * w);
            float m20 = 2f * (x * z - y * w), m21 = 2f * (y * z + x * w), m22 = 1f - 2f * (xx + yy);
            Vector3 ext = new Vector3(
                Mathf.Abs(m00) * half.x + Mathf.Abs(m01) * half.y + Mathf.Abs(m02) * half.z,
                Mathf.Abs(m10) * half.x + Mathf.Abs(m11) * half.y + Mathf.Abs(m12) * half.z,
                Mathf.Abs(m20) * half.x + Mathf.Abs(m21) * half.y + Mathf.Abs(m22) * half.z);

            // A world vertical line, in the shape's own frame: where it starts and which way
            // it runs. The inverse of a unit quaternion is its conjugate.
            Quaternion inv = new Quaternion(-x, -y, -z, w);
            Vector3 dir = inv * Vector3.up;

            int i0 = Mathf.FloorToInt((centre.x - ext.x) / Cell), i1 = Mathf.FloorToInt((centre.x + ext.x) / Cell);
            int j0 = Mathf.FloorToInt((centre.z - ext.z) / Cell), j1 = Mathf.FloorToInt((centre.z + ext.z) / Cell);

            if ((long)(i1 - i0 + 1) * (j1 - j0 + 1) > MaxFillColumns) { row = int.MaxValue; return -1; }

            int filled = 0, looked = 0;
            int jStart = row == int.MinValue ? j0 : Mathf.Max(row, j0);
            int jDone = j1;
            row = int.MaxValue;
            quietFill = true;
            for (int j = jStart; j <= j1; j++)
            {
                if (looked >= maxColumns) { row = j; jDone = j - 1; break; }
                looked += i1 - i0 + 1;
                for (int i = i0; i <= i1; i++)
                {
                    // The average length over a few points across the column, not the length
                    // at its centre: a column the shape only partly covers, which is every
                    // column round its edge, would otherwise count as all or nothing and a
                    // small shape would lose a tenth of its sand.
                    //
                    // A column wholly inside the shape's footprint -- most of them, for a big
                    // region -- is a smooth function across the column, and three by three
                    // averages it closely. Only where one of those nine misses the shape is it
                    // an edge column, worth the full grid. Sixty-four samples a column
                    // everywhere was a fifth of a second for a twenty-metre region, all in the
                    // frame the run started.
                    float chord = 0f;
                    bool edge = false;
                    for (int sj = 0; sj < 3 && !edge; sj++)
                    {
                        for (int si = 0; si < 3; si++)
                        {
                            Vector3 world = new Vector3((i + (si + 0.5f) / 3f) * Cell, centre.y,
                                                        (j + (sj + 0.5f) / 3f) * Cell);
                            float l = Chord(kind, inv * (world - centre), dir, half);
                            if (l <= 0f) { edge = true; break; }
                            chord += l;
                        }
                    }
                    if (!edge) chord /= 9f;
                    else
                    {
                        chord = 0f;
                        for (int sj = 0; sj < FillSamples; sj++)
                        {
                            for (int si = 0; si < FillSamples; si++)
                            {
                                Vector3 world = new Vector3((i + (si + 0.5f) / FillSamples) * Cell, centre.y,
                                                            (j + (sj + 0.5f) / FillSamples) * Cell);
                                chord += Chord(kind, inv * (world - centre), dir, half);
                            }
                        }
                        chord /= FillSamples * FillSamples;
                    }
                    if (chord <= MinThickness) continue;
                    if (Deposit(i, j, chord, material, colour)) filled++;
                }
            }
            quietFill = false;

            // Only the columns that can slump need looking at: those with a neighbour
            // lower than the slope allows. Every column of a filled region used to go on the
            // list, and slumping worked through the flat interior first, so a region's walls
            // stood vertical for seconds -- long enough for a machine to drive into them and
            // dig, where it should have met a slope.
            for (int j = jStart - 1; j <= jDone + 1; j++)
                for (int i = i0 - 1; i <= i1 + 1; i++)
                    if (CanSlump(i, j)) Activate(i, j);
            return filled;
        }

        /// <summary>Whether a column with sand has a neighbour lower than its slope allows.</summary>
        private bool CanSlump(int i, int j)
        {
            Chunk c = Find(i, j);
            if (c == null) return false;
            int k = Slot(i, j);
            if (c.top[k] - c.bottom[k] <= MinThickness) return false;
            float limit = slope[Mathf.Clamp(c.material[k], 0, slope.Length - 1)] * Cell;
            float top = c.top[k];
            bool sand = false;
            if (top - EffectiveTop(i + 1, j, ref sand) > limit) return true;
            if (top - EffectiveTop(i - 1, j, ref sand) > limit) return true;
            if (top - EffectiveTop(i, j + 1, ref sand) > limit) return true;
            if (top - EffectiveTop(i, j - 1, ref sand) > limit) return true;
            return false;
        }

        /// <summary>Length of the part of a line inside a shape: origin and a unit direction,
        /// both in the shape's frame, and its half extents.</summary>
        private static float Chord(int kind, Vector3 o, Vector3 d, Vector3 half)
        {
            float t0 = float.NegativeInfinity, t1 = float.PositiveInfinity;

            if (kind == ShapeBox)
            {
                if (!Slab(o.x, d.x, half.x, ref t0, ref t1)) return 0f;
                if (!Slab(o.y, d.y, half.y, ref t0, ref t1)) return 0f;
                if (!Slab(o.z, d.z, half.z, ref t0, ref t1)) return 0f;
                return t1 > t0 ? t1 - t0 : 0f;
            }

            if (kind == ShapeCylinder)
            {
                // Elliptical cross-section in x and z, along local y.
                float ox = o.x / Mathf.Max(half.x, 1e-4f), oz = o.z / Mathf.Max(half.z, 1e-4f);
                float dx = d.x / Mathf.Max(half.x, 1e-4f), dz = d.z / Mathf.Max(half.z, 1e-4f);
                float a = dx * dx + dz * dz, c = ox * ox + oz * oz - 1f;
                if (a < 1e-8f)
                {
                    if (c > 0f) return 0f;          // parallel to the axis and outside it
                }
                else
                {
                    float b = 2f * (ox * dx + oz * dz);
                    float disc = b * b - 4f * a * c;
                    if (disc <= 0f) return 0f;
                    float sq = Mathf.Sqrt(disc);
                    t0 = (-b - sq) / (2f * a);
                    t1 = (-b + sq) / (2f * a);
                }
                if (!Slab(o.y, d.y, half.y, ref t0, ref t1)) return 0f;
                return t1 > t0 ? t1 - t0 : 0f;
            }

            // Ellipsoid: a unit sphere once each axis is divided by its half extent.
            {
                Vector3 op = new Vector3(o.x / Mathf.Max(half.x, 1e-4f), o.y / Mathf.Max(half.y, 1e-4f), o.z / Mathf.Max(half.z, 1e-4f));
                Vector3 dp = new Vector3(d.x / Mathf.Max(half.x, 1e-4f), d.y / Mathf.Max(half.y, 1e-4f), d.z / Mathf.Max(half.z, 1e-4f));
                float a = Vector3.Dot(dp, dp), b = 2f * Vector3.Dot(op, dp), c = Vector3.Dot(op, op) - 1f;
                float disc = b * b - 4f * a * c;
                if (a < 1e-8f || disc <= 0f) return 0f;
                float sq = Mathf.Sqrt(disc);
                return sq / a;      // (t1 - t0), with the direction unit length
            }
        }

        /// <summary>Narrows a line's interval to one slab of a box; false if it misses.</summary>
        private static bool Slab(float o, float d, float h, ref float t0, ref float t1)
        {
            if (Mathf.Abs(d) < 1e-8f) return o >= -h && o <= h;
            float a = (-h - o) / d, b = (h - o) / d;
            if (a > b) { float tmp = a; a = b; b = tmp; }
            if (a > t0) t0 = a;
            if (b < t1) t1 = b;
            return t1 > t0;
        }

        // ------------------------------------------------------------------ absorbing

        /// <summary>
        /// Moves grains that have come to rest into the bed. A grain qualifies when it
        /// has lain still for <paramref name="restSteps"/> solver steps, is not about to
        /// expire, is not near a shape, and is at or below the surface it is resting on.
        /// Returns how many were absorbed; they are flagged dead and the field's next
        /// reap removes them.
        ///
        /// Capped per call: the first call after a big pour would otherwise absorb
        /// thousands at once.
        /// </summary>
        public int Absorb(Grain[] grains, int count, IBedShapes shapes, int restSteps,
                          float minLife, float margin, int maxPerCall)
        {
            if (!Enabled || ground == null) return 0;

            Vector3 umin = Vector3.zero, umax = Vector3.zero;
            int shapeCount = shapes != null ? shapes.Count : 0;
            if (shapeCount > 0) shapes.Union(out umin, out umax);

            int absorbed = 0;
            for (int n = 0; n < count && absorbed < maxPerCall; n++)
            {
                Grain g = grains[n];
                uint flags = g.material;
                if ((flags & SandFormat.FlagDead) != 0) continue;
                // Away from every machine a grain is taken in after half the wait: nothing there
                // will disturb it, and a grain costs a solver slot for as long as it is one.
                int rest = SandFormat.RestSteps(flags);
                if (rest < restSteps)
                {
                    if (rest < restSteps / 2) continue;
                    if (shapeCount > 0 && Near(umin, umax, g.pos, FarFromMachines)) continue;
                }
                if (g.life < minLife) continue;

                int mat = (int)(flags & SandFormat.MaterialMask);
                if (mat >= grainRadius.Length) continue;
                float r = grainRadius[mat];

                if (shapeCount > 0 && NearShape(shapes, shapeCount, umin, umax, g.pos, margin))
                    continue;

                float surface = ground.Height(g.pos.x, g.pos.z);
                if (surface <= NoGround) continue;
                float bed;
                if (TryTop(g.pos.x, g.pos.z, out bed) && bed > surface) surface = bed;

                // The interpolated surface dips towards bare ground at the rim of a
                // pile, which would leave the grains up the rim forever refused. The
                // column's own top is the higher of the two there, so use whichever is
                // higher.
                float own = OwnTop(g.pos.x, g.pos.z);
                if (own > surface) surface = own;

                // At or below the surface it is lying on, allowing for the stack of
                // grains above the last one absorbed: one more diameter and a half.
                if (g.pos.y - r > surface + r * 1.5f) continue;

                float volume = grainVolume[mat];
                uint colour = g.colour;
                if ((flags & SandFormat.FlagWet) != 0) colour = Darken(colour, 0.65f);

                if (!AddVolume(g.pos.x, g.pos.z, volume, mat, colour)) continue;

                grains[n].material |= SandFormat.FlagDead;
                absorbed++;
            }
            return absorbed;
        }

        /// <summary>Top of the sand in the column containing a point, or a very low
        /// value when that column is empty.</summary>
        private float OwnTop(float x, float z)
        {
            int i = Mathf.FloorToInt(x / Cell), j = Mathf.FloorToInt(z / Cell);
            Chunk c = Find(i, j);
            if (c == null) return -1e9f;
            int k = Slot(i, j);
            return c.top[k] - c.bottom[k] > MinThickness ? c.top[k] : -1e9f;
        }

        private static uint Darken(uint c, float f)
        {
            uint r = (uint)((c & 0xFF) * f), g = (uint)(((c >> 8) & 0xFF) * f), b = (uint)(((c >> 16) & 0xFF) * f);
            return r | (g << 8) | (b << 16) | (c & 0xFF000000u);
        }

        /// <summary>How far from every machine a grain must be to settle on the shorter wait.</summary>
        private const float FarFromMachines = 3f;

        private static bool Near(Vector3 umin, Vector3 umax, Vector3 p, float margin)
        {
            return p.x > umin.x - margin && p.x < umax.x + margin
                && p.y > umin.y - margin && p.y < umax.y + margin
                && p.z > umin.z - margin && p.z < umax.z + margin;
        }

        private static bool NearShape(IBedShapes shapes, int count, Vector3 umin, Vector3 umax,
                                      Vector3 p, float margin)
        {
            if (p.x < umin.x - margin || p.x > umax.x + margin) return false;
            if (p.y < umin.y - margin || p.y > umax.y + margin) return false;
            if (p.z < umin.z - margin || p.z > umax.z + margin) return false;

            for (int s = 0; s < count; s++)
            {
                Vector3 lo, hi;
                shapes.Extent(s, out lo, out hi);
                if (p.x < lo.x - margin || p.x > hi.x + margin) continue;
                if (p.y < lo.y - margin || p.y > hi.y + margin) continue;
                if (p.z < lo.z - margin || p.z > hi.z + margin) continue;
                if (shapes.Distance(s, p) < margin) return true;
            }
            return false;
        }

        // ------------------------------------------------------------------ releasing

        private float Rand()
        {
            rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5;
            return (rng & 0xFFFFFF) / (float)0x1000000;
        }

        /// <summary>
        /// Turns the sand around each shape back into grains. For every column a shape
        /// reaches into, everything from just below the shape's lowest point up to the
        /// top of the pile becomes grains, placed on a lattice so they are not born
        /// overlapped. <paramref name="budget"/> caps the number of distance tests per
        /// call; what is not reached this step is reached the next.
        /// Returns the number of grains released.
        /// </summary>
        public int Release(IBedShapes shapes, IBedSink sink, float margin, int budget)
        {
            return Release(shapes, sink, margin, budget, int.MaxValue);
        }

        /// <summary>The same, giving back at most <paramref name="maxGrains"/> grains; what a
        /// shape reaches into beyond that is given back on later calls.</summary>
        public int Release(IBedShapes shapes, IBedSink sink, float margin, int budget, int maxGrains)
        {
            if (!Enabled || filledColumns <= 0 || shapes == null) return 0;

            int released = 0;
            int tests = 0;
            float sampleStep = 0.05f;
            IBedShapeMotion motion = shapes as IBedShapeMotion;

            // Faces something pushed into, as the colliders reported them.
            for (int n = 0; n < digs.Count; n++)
            {
                Vector3 p = digs[n];
                int i = Mathf.FloorToInt(p.x / Cell), j = Mathf.FloorToInt(p.z / Cell);
                Chunk dc = Find(i, j);
                if (dc == null) continue;
                int k = Slot(i, j);
                if (dc.top[k] - dc.bottom[k] <= MinThickness || dc.top[k] < p.y) continue;
                released += Fluidize(dc, i, j, Mathf.Max(dc.bottom[k], p.y - sampleStep), sink, maxGrains - released);
                if (sink.Room <= 0 || released >= maxGrains) { digs.Clear(); return released; }
            }
            digs.Clear();
            int span = ChunkSide;

            for (int s = 0; s < shapes.Count && tests < budget; s++)
            {
                Vector3 lo, hi;
                shapes.Extent(s, out lo, out hi);
                // How fast the shape's underside slides over the ground: a wheel rolling has
                // none however fast the machine goes, a wheel spinning in place or a block
                // dragged along has all of it.
                float slip = 0f, moving = 0f;
                Vector3 middle = (lo + hi) * 0.5f;
                if (motion != null)
                {
                    Vector3 under = motion.SurfaceVelocity(s, new Vector3(middle.x, lo.y, middle.z));
                    slip = Mathf.Sqrt(under.x * under.x + under.z * under.z);
                    moving = motion.SurfaceVelocity(s, middle).magnitude;
                }
                int picked = 0;
                lo -= new Vector3(margin, margin, margin);
                hi += new Vector3(margin, margin, margin);

                int i0 = Mathf.FloorToInt(lo.x / Cell), i1 = Mathf.FloorToInt(hi.x / Cell);
                int j0 = Mathf.FloorToInt(lo.z / Cell), j1 = Mathf.FloorToInt(hi.z / Cell);

                int ci0 = FloorDiv(i0, span), ci1 = FloorDiv(i1, span);
                int cj0 = FloorDiv(j0, span), cj1 = FloorDiv(j1, span);

                for (int cj = cj0; cj <= cj1 && tests < budget; cj++)
                {
                    for (int ci = ci0; ci <= ci1 && tests < budget; ci++)
                    {
                        Chunk c;
                        if (!chunks.TryGetValue(Key(ci, cj), out c) || c.filled <= 0) continue;

                        int ja = Mathf.Max(j0, cj * span), jb = Mathf.Min(j1, cj * span + span - 1);
                        int ia = Mathf.Max(i0, ci * span), ib = Mathf.Min(i1, ci * span + span - 1);

                        for (int j = ja; j <= jb && tests < budget; j++)
                        {
                            for (int i = ia; i <= ib && tests < budget; i++)
                            {
                                int k = Slot(i, j);
                                float bottom = c.bottom[k], top = c.top[k];
                                if (top - bottom <= MinThickness) continue;
                                if (top < lo.y || bottom > hi.y) continue;

                                float cx = (i + 0.5f) * Cell, cz = (j + 0.5f) * Cell;
                                // Up the column: where the shape first comes within the margin,
                                // where it is nearest, and where -- if anywhere -- the column's
                                // line passes inside it.
                                float lowest = float.MaxValue, inside = float.MaxValue;
                                float nearest = float.MaxValue, nearestY = top;
                                for (float y = bottom + sampleStep * 0.5f; y <= top + sampleStep * 0.5f; y += sampleStep)
                                {
                                    tests++;
                                    float yy = Mathf.Min(y, top);
                                    float d = shapes.Distance(s, new Vector3(cx, yy, cz));
                                    if (d < nearest) { nearest = d; nearestY = yy; }
                                    if (lowest == float.MaxValue && d < margin) lowest = y;
                                    if (d < 0f) { inside = yy; break; }
                                    if (motion == null && lowest != float.MaxValue) break;
                                }
                                if (lowest == float.MaxValue) continue;

                                // Only where something digs. A machine resting or rolling on the
                                // pile stands on its collider and leaves it as it is, pressing it
                                // down (see Press); sand is only handed back as grains where a
                                // surface touching it moves through it -- a wheel spinning, a
                                // block landing -- or where a shape is inside the sand, which
                                // is anything the collider was not there in time to hold up.
                                // Being near was not enough: on a slope the column above a wheel
                                // always passes beside it, and the front of a rolling wheel moves
                                // fast without touching anything, so a machine driving at a pile
                                // dug the slope away in front of itself and ploughed through.
                                if (motion != null)
                                {
                                    // A column something stands on is its rut, not a burial.
                                    float buried = inside == float.MaxValue || Held(i, j) ? 0f : top - inside;
                                    // Digging is slip, not speed: a rolling wheel's rim moves fast where it
                                    // meets the front of its rut, but it moves into the sand there and
                                    // presses it, and digging on that dug every rut deeper the faster the
                                    // machine went, until it had sunk. What digs is the underside sliding
                                    // -- a wheel spinning, a block dragged -- or a hard landing.
                                    bool touching = nearest < TouchDistance;
                                    float dig = digSpeedOf[Mathf.Clamp(c.material[k], 0, digSpeedOf.Length - 1)];
                                    bool digging = false;
                                    if (touching)
                                    {
                                        if (slip > dig) digging = true;
                                        else
                                        {
                                            Vector3 v = motion.SurfaceVelocity(s, new Vector3(cx, nearestY, cz));
                                            digging = -v.y > ImpactDigSpeed;
                                        }
                                    }
                                    if (!digging)
                                    {
                                        int mat = Mathf.Clamp(c.material[k], 0, adhesionOf.Length - 1);
                                        if (buried >= BuryDepth)
                                        {
                                            // Inside the sand without sliding: what it is in is pushed
                                            // aside, as a wheel or a block settling into sand does,
                                            // rather than turned into grains -- which cannot hold a
                                            // machine up, and let it sink on through the pile.
                                            Displace(c, i, j, inside, middle, shapes, s, margin);
                                            continue;
                                        }
                                        // Sticky sand comes away on whatever rolls over it: a skin of
                                        // grains from under a moving wheel, which cling to it.
                                        if (touching && adhesionOf[mat] >= PickupAdhesion && moving > PickupSpeed
                                            && picked < PickupPerShape && Rand() < PickupChance)
                                        {
                                            float skin = Mathf.Max(grainRadius[mat] * 2f, MinThickness * 2f);
                                            int got = Fluidize(c, i, j, Mathf.Max(bottom, top - skin), sink, Mathf.Min(maxGrains - released, 6));
                                            released += got;
                                            picked += got;
                                            if (sink.Room <= 0 || released >= maxGrains) return released;
                                        }
                                        continue;
                                    }
                                }

                                released += Fluidize(c, i, j, Mathf.Max(bottom, lowest - sampleStep), sink,
                                                     maxGrains - released);
                                if (sink.Room <= 0 || released >= maxGrains) return released;
                            }
                        }
                    }
                }
            }
            return released;
        }

        /// <summary>Turns a column's sand above <paramref name="cut"/> into grains.</summary>
        private int Fluidize(Chunk c, int i, int j, float cut, IBedSink sink, int most)
        {
            // A layer at a time from the top, each as the grains of its own medium.
            int k = Slot(i, j);
            int released = 0;
            for (int pass = 0; pass < 2 && released < most; pass++)
            {
                if (c.top[k] - c.bottom[k] <= MinThickness || c.top[k] <= cut + MinThickness) break;
                float layerCut = Layered(c, k) ? Mathf.Max(cut, c.split[k]) : cut;
                int n = FluidizeSurface(c, i, j, layerCut, sink, most - released);
                if (n <= 0) break;
                released += n;
            }
            return released;
        }

        /// <summary>Turns the surface layer's sand above <paramref name="cut"/> into grains.</summary>
        private int FluidizeSurface(Chunk c, int i, int j, float cut, IBedSink sink, int most)
        {
            int k = Slot(i, j);
            int material = c.material[k];
            uint colour = c.colour[k];
            float oldTop = c.top[k];

            float gv = grainVolume[Mathf.Clamp(material, 0, grainVolume.Length - 1)];
            float volume = (oldTop - cut) * Cell * Cell;
            int n = Mathf.RoundToInt(volume / gv);
            if (n < 1) return 0;
            n = Mathf.Min(n, Mathf.Min(sink.Room, most));
            if (n < 1) return 0;

            float newTop = oldTop - n * gv / (Cell * Cell);
            if (newTop < c.bottom[k]) newTop = c.bottom[k];

            // Grains per layer, and the lattice they sit on inside the column.
            float side = Mathf.Pow(gv, 1f / 3f);
            int perLayer = Mathf.Max(1, Mathf.RoundToInt(Cell * Cell / (side * side)));
            int g = Mathf.Max(1, Mathf.CeilToInt(Mathf.Sqrt(perLayer)));
            int layers = (n + perLayer - 1) / perLayer;
            float layerHeight = (oldTop - newTop) / layers;

            // Every other layer is shifted half a step, so a grain starts in the pocket between
            // four below it rather than balanced on the top of one. Grains here cannot roll, so
            // friction holds a column of them standing where a real one would topple: a lattice
            // of columns released from the bed stood as a block with vertical walls.
            float x0 = i * Cell, z0 = j * Cell;
            for (int q = 0; q < n; q++)
            {
                int layer = q / perLayer, slot = q % perLayer;
                float shift = (layer & 1) * 0.5f;
                float ux = ((slot % g) + 0.5f + shift + (Rand() - 0.5f) * 0.5f) / g;
                float uz = ((slot / g) + 0.5f + shift + (Rand() - 0.5f) * 0.5f) / g;
                if (ux >= 1f) ux -= 1f;
                if (uz >= 1f) uz -= 1f;
                Vector3 p = new Vector3(x0 + Mathf.Clamp01(ux) * Cell,
                                        newTop + (layer + 0.5f + (Rand() - 0.5f) * 0.3f) * layerHeight,
                                        z0 + Mathf.Clamp01(uz) * Cell);
                sink.Release(p, material, colour);
            }

            Withdraw(c, i, j, oldTop - newTop);
            return n;
        }

        // ------------------------------------------------------------------ multiplayer
        //
        // In multiplayer the host's bed is the bed: clients do not simulate, they draw what the
        // host sends. The host marks every chunk that changes and sends a few of them at a
        // time; a client writes each one it receives straight into its own bed.

        /// <summary>Set on a multiplayer host: chunks are marked for sending as they change.</summary>
        public bool TrackNet;

        private readonly List<Chunk> netQueue = new List<Chunk>();

        private void MarkNet(Chunk c)
        {
            if (!TrackNet || c.netDirty) return;
            c.netDirty = true;
            netQueue.Add(c);
        }

        /// <summary>Marks every chunk for sending again: someone has just joined.</summary>
        public void MarkAllNet()
        {
            if (!TrackNet) return;
            foreach (KeyValuePair<long, Chunk> kv in chunks) MarkNet(kv.Value);
        }

        /// <summary>The next chunk to send, or null.</summary>
        public Chunk TakeNetDirty()
        {
            while (netQueue.Count > 0)
            {
                Chunk c = netQueue[netQueue.Count - 1];
                netQueue.RemoveAt(netQueue.Count - 1);
                c.netDirty = false;
                if (chunks.ContainsKey(Key(c.cx, c.cz))) return c;
            }
            return null;
        }

        /// <summary>A chunk packed small (BedCodec) for the other players: every column, both
        /// layers, to the millimetre.</summary>
        public byte[] Encode(Chunk c)
        {
            return BedCodec.Pack(c, MinThickness);
        }

        /// <summary>Writes a packed chunk -- from the host, a save or a kept run -- into this bed.</summary>
        public void Decode(byte[] b)
        {
            if (b == null || b.Length < 2) return;
            Chunk d = new Chunk();
            if (!BedCodec.Unpack(b, d)) return;
            Adopt(d);
        }

        /// <summary>Puts a whole chunk's columns into the bed in place of whatever was there.</summary>
        private void Adopt(Chunk d)
        {
            Chunk c = ChunkAt(d.cx, d.cz);
            if (c == null)
            {
                c = new Chunk();
                c.cx = d.cx; c.cz = d.cz;
                chunks[Key(d.cx, d.cz)] = c;
            }
            filledColumns -= c.filled;
            System.Array.Copy(d.top, c.top, d.top.Length);
            System.Array.Copy(d.bottom, c.bottom, d.bottom.Length);
            System.Array.Copy(d.material, c.material, d.material.Length);
            System.Array.Copy(d.colour, c.colour, d.colour.Length);
            System.Array.Copy(d.packed, c.packed, d.packed.Length);
            System.Array.Copy(d.split, c.split, d.split.Length);
            System.Array.Copy(d.lowMaterial, c.lowMaterial, d.lowMaterial.Length);
            System.Array.Copy(d.lowColour, c.lowColour, d.lowColour.Length);
            c.filled = 0;
            for (int k = 0; k < c.top.Length; k++)
            {
                if (c.top[k] - c.bottom[k] <= MinThickness) continue;
                c.filled++;
                int i = c.cx * ChunkSide + k % ChunkSide, j = c.cz * ChunkSide + k / ChunkSide;
                Grow((i + 0.5f) * Cell, c.top[k], (j + 0.5f) * Cell);
            }
            filledColumns += c.filled;
            c.colliderDirty = true;
            Queue(c);
            MarkNet(c);
            // The neighbours share this chunk's edge in their meshes.
            Queue(ChunkAt(d.cx - 1, d.cz)); Queue(ChunkAt(d.cx, d.cz - 1)); Queue(ChunkAt(d.cx - 1, d.cz - 1));
        }

        // ------------------------------------------------------------------ keeping

        /// <summary>
        /// The bed as it was, to put back: copied whole when taken, which is quick, then packed a
        /// few chunks at a time over the frames after (<see cref="Compact"/>), which is small.
        /// A big level's sand went from about 6.6 KB a chunk held for the whole run to a few
        /// hundred bytes.
        /// </summary>
        public class Snapshot
        {
            public readonly List<Chunk> Raw = new List<Chunk>();
            public readonly List<byte[]> Packed = new List<byte[]>();
            public int Bytes;
        }

        /// <summary>
        /// A copy of every column, to put back later: a run of the level starts from the sand
        /// as it was and, unless the player keeps it, ends by going back to that.
        /// </summary>
        public object Capture()
        {
            Snapshot snap = new Snapshot();
            foreach (KeyValuePair<long, Chunk> kv in chunks)
            {
                Chunk c = kv.Value;
                if (c.filled <= 0) continue;
                Chunk d = new Chunk();
                d.cx = c.cx; d.cz = c.cz; d.filled = c.filled;
                System.Array.Copy(c.top, d.top, c.top.Length);
                System.Array.Copy(c.bottom, d.bottom, c.bottom.Length);
                System.Array.Copy(c.material, d.material, c.material.Length);
                System.Array.Copy(c.colour, d.colour, c.colour.Length);
                System.Array.Copy(c.packed, d.packed, c.packed.Length);
                System.Array.Copy(c.split, d.split, c.split.Length);
                System.Array.Copy(c.lowMaterial, d.lowMaterial, c.lowMaterial.Length);
                System.Array.Copy(c.lowColour, d.lowColour, c.lowColour.Length);
                snap.Raw.Add(d);
            }
            return snap;
        }

        /// <summary>Packs what is still a plain copy in a snapshot, for up to
        /// <paramref name="budgetMs"/>. True once it is all packed.</summary>
        public static bool Compact(object captured, float budgetMs)
        {
            Snapshot snap = captured as Snapshot;
            if (snap == null) return true;
            System.Diagnostics.Stopwatch timer = System.Diagnostics.Stopwatch.StartNew();
            while (snap.Raw.Count > 0 && timer.Elapsed.TotalMilliseconds < budgetMs)
            {
                int last = snap.Raw.Count - 1;
                byte[] b = BedCodec.Pack(snap.Raw[last], MinThickness);
                snap.Raw.RemoveAt(last);
                snap.Packed.Add(b);
                snap.Bytes += b.Length;
            }
            return snap.Raw.Count == 0;
        }

        /// <summary>Puts back what <see cref="Capture"/> took. Everything since is let go of,
        /// its meshes and colliders with it.</summary>
        public void Restore(object captured)
        {
            Clear();
            Snapshot snap = captured as Snapshot;
            if (snap == null) return;
            for (int n = 0; n < snap.Raw.Count; n++) Adopt(snap.Raw[n]);
            Chunk scratch = new Chunk();
            for (int n = 0; n < snap.Packed.Count; n++)
                if (BedCodec.Unpack(snap.Packed[n], scratch)) Adopt(scratch);
        }

        // ------------------------------------------------------------------ saving

        /// <summary>Renumbers every column's media through <paramref name="map"/>: sand saved
        /// when the table was laid out differently.</summary>
        public void RemapMaterials(int[] map)
        {
            foreach (KeyValuePair<long, Chunk> kv in chunks)
            {
                Chunk c = kv.Value;
                for (int k = 0; k < c.material.Length; k++)
                {
                    if (c.material[k] < map.Length) c.material[k] = (byte)map[c.material[k]];
                    if (c.lowMaterial[k] < map.Length) c.lowMaterial[k] = (byte)map[c.lowMaterial[k]];
                }
                Queue(c);
            }
        }

        private const int SaveMagic = 0x534E4431;   // "SND1"

        /// <summary>The whole bed, packed, for a level file.</summary>
        public byte[] SaveAll()
        {
            BedCodec.Writer w = new BedCodec.Writer(4096);
            w.Int(SaveMagic);
            int count = 0;
            foreach (KeyValuePair<long, Chunk> kv in chunks) if (kv.Value.filled > 0) count++;
            w.Unsigned((uint)count);
            foreach (KeyValuePair<long, Chunk> kv in chunks)
            {
                if (kv.Value.filled <= 0) continue;
                byte[] b = BedCodec.Pack(kv.Value, MinThickness);
                w.Unsigned((uint)b.Length);
                w.Bytes(b, b.Length);
            }
            return w.ToArray();
        }

        /// <summary>Replaces the bed with one <see cref="SaveAll"/> wrote. False if the bytes
        /// are not a saved bed, which leaves the bed empty.</summary>
        public bool LoadAll(byte[] data)
        {
            Clear();
            if (data == null || data.Length < 5) return false;
            BedCodec.Reader r = new BedCodec.Reader(data, 0);
            if (r.Int() != SaveMagic) return false;
            int count = (int)r.Unsigned();
            Chunk scratch = new Chunk();
            for (int n = 0; n < count && !r.Overrun; n++)
            {
                int len = (int)r.Unsigned();
                byte[] b = r.Bytes(len);
                if (r.Overrun) break;
                if (BedCodec.Unpack(b, scratch)) Adopt(scratch);
            }
            return !r.Overrun;
        }

        /// <summary>
        /// Turns the surface layer of the columns within <paramref name="radius"/> of a point into
        /// another medium, row by row through <paramref name="map"/> (-1 for no change), and
        /// scales their colour by <paramref name="shade"/>: wetting sand darkens it, drying it
        /// lightens it back. Returns how many columns changed.
        /// </summary>
        public int ConvertSurface(Vector3 at, float radius, int[] map, float shade)
        {
            int changed = 0;
            int i0 = Mathf.FloorToInt((at.x - radius) / Cell), i1 = Mathf.FloorToInt((at.x + radius) / Cell);
            int j0 = Mathf.FloorToInt((at.z - radius) / Cell), j1 = Mathf.FloorToInt((at.z + radius) / Cell);
            for (int j = j0; j <= j1; j++)
                for (int i = i0; i <= i1; i++)
                {
                    Chunk c = Find(i, j);
                    if (c == null) continue;
                    int k = Slot(i, j);
                    if (c.top[k] - c.bottom[k] <= MinThickness) continue;
                    int from = c.material[k];
                    int to = from < map.Length ? map[from] : -1;
                    if (to < 0 || to == from) continue;
                    // Only the sand near where it lands, not the whole column under it.
                    if (Mathf.Abs(c.top[k] - at.y) > radius + 0.5f) continue;
                    c.material[k] = (byte)to;
                    c.colour[k] = Scale(c.colour[k], shade);
                    if (Layered(c, k) && c.lowMaterial[k] == to)
                    {
                        // The two layers are one medium now.
                        c.split[k] = c.bottom[k];
                    }
                    MarkDirty(c, i, j);
                    changed++;
                }
            return changed;
        }

        private static uint Scale(uint c, float f)
        {
            uint r = (uint)Mathf.Clamp(Mathf.RoundToInt((c & 0xFF) * f), 0, 255);
            uint g = (uint)Mathf.Clamp(Mathf.RoundToInt(((c >> 8) & 0xFF) * f), 0, 255);
            uint b = (uint)Mathf.Clamp(Mathf.RoundToInt(((c >> 16) & 0xFF) * f), 0, 255);
            return r | (g << 8) | (b << 16) | (c & 0xFF000000u);
        }

        /// <summary>Takes up to <paramref name="thickness"/> off the column under a point;
        /// returns how much it took.</summary>
        public float RemoveVolume(float x, float z, float thickness)
        {
            int i = Mathf.FloorToInt(x / Cell), j = Mathf.FloorToInt(z / Cell);
            Chunk c = Find(i, j);
            if (c == null || thickness <= 0f) return 0f;
            int k = Slot(i, j);
            float avail = c.top[k] - c.bottom[k];
            if (avail <= MinThickness) return 0f;
            float take = Mathf.Min(thickness, avail);
            Withdraw(c, i, j, take);
            return take;
        }

        // ------------------------------------------------------------------ pressing

        /// <summary>Speed a surface must move at, through the sand, to dig it up, where the
        /// medium's row does not say (each medium's is its <c>digSpeed</c>).</summary>
        public const float DigSpeed = 0.4f;

        /// <summary>How far inside the sand a shape may be before the column is dug out from
        /// around it whatever it is doing. A body standing on the bed's collider is never more
        /// than PhysX's contact offset into it.</summary>
        public const float BuryDepth = 0.06f;

        /// <summary>How fast something must come down onto the sand to splash it up as grains
        /// rather than press it: a landing, not a wheel rolling into its rut.</summary>
        public const float ImpactDigSpeed = 6f;

        /// <summary>Sand at least this sticky comes away on a wheel rolling over it.</summary>
        public const float PickupAdhesion = 150f;
        /// <summary>How fast a shape must be moving to pick sand up.</summary>
        public const float PickupSpeed = 0.5f;
        /// <summary>Grains one shape may pick up a step, and the chance a touching column gives
        /// some: enough to coat a wheel over a few metres, not to strip the ground bare.</summary>
        public const int PickupPerShape = 12;
        public const float PickupChance = 0.35f;

        /// <summary>
        /// Moves the sand in a column above <paramref name="cut"/> out from under a shape: to the
        /// first column outward from the shape's middle that the shape is clear of, within a
        /// metre and a half, or -- if there is none -- leaves it.
        /// </summary>
        private void Displace(Chunk c, int i, int j, float cut, Vector3 middle, IBedShapes shapes, int s, float margin)
        {
            int k = Slot(i, j);
            float cx = (i + 0.5f) * Cell, cz = (j + 0.5f) * Cell;
            Vector2 away = new Vector2(cx - middle.x, cz - middle.z);
            if (away.sqrMagnitude < 1e-6f) away = new Vector2(1f, 0f);
            away.Normalize();
            for (float d = Cell; d <= 1.5f; d += Cell)
            {
                float tx = cx + away.x * d, tz = cz + away.y * d;
                int ti = Mathf.FloorToInt(tx / Cell), tj = Mathf.FloorToInt(tz / Cell);
                if (ti == i && tj == j) continue;
                float h;
                if (!TryTop(tx, tz, out h)) h = ground.Height(tx, tz);
                if (h <= NoGround) continue;
                if (Held(ti, tj) || shapes.Distance(s, new Vector3(tx, h + 0.02f, tz)) < margin) continue;
                for (int pass = 0; pass < 2; pass++)
                {
                    if (c.top[k] - c.bottom[k] <= MinThickness || c.top[k] <= cut + MinThickness) break;
                    float layerCut = Layered(c, k) ? Mathf.Max(cut, c.split[k]) : cut;
                    float amount = c.top[k] - Mathf.Max(layerCut, c.bottom[k]);
                    if (amount <= 0f) break;
                    byte material = c.material[k];
                    uint colour = c.colour[k];
                    Withdraw(c, i, j, amount);
                    Deposit(ti, tj, amount, material, colour);
                }
                return;
            }
        }

        /// <summary>How near a surface must be to the sand to count as touching it.</summary>
        public const float TouchDistance = 0.03f;

        /// <summary>A contact steeper than this, as the upward part of its normal, is a shape
        /// pushing into the side of the sand rather than standing on it.</summary>
        private const float SideContact = 0.5f;

        private readonly List<Vector3> digs = new List<Vector3>();

        /// <summary>Pressure, in pascals per metre of sinking, that a medium resists being
        /// pressed down with, where its row does not say (each medium's is its
        /// <c>firmness</c>). A machine's wheel on Besiege's gravity bears a few kilopascals.</summary>
        public const float PressStiffness = 50000f;

        /// <summary>Fastest a column sinks under load, in metres a second.</summary>
        private const float PressRate = 0.6f;

        /// <summary>Most a column is pressed down, as a fraction of its depth, where the medium's
        /// row does not say (each medium's is its <c>packLimit</c>).</summary>
        private const float MostPacking = 0.35f;

        private readonly List<Vector3> loads = new List<Vector3>();   // x, z, force

        /// <summary>
        /// Records a force pressing on the bed at a point this fixed update. The bed's
        /// colliders report what PhysX pushes back with; the bed sinks under it in
        /// <see cref="Press"/>.
        /// </summary>
        public void AddLoad(Vector3 point, float force)
        {
            AddLoad(point, Vector3.up, force);
        }

        /// <summary>
        /// The same, with the contact's normal. A contact on the side of the sand -- a blade
        /// pushed into a steep face -- is not a load to sink under but a place to dig: the
        /// collider would otherwise hold a plough back like a wall. The column there is turned
        /// to grains on the next <see cref="Release"/>, for the blade to push.
        /// </summary>
        public void AddLoad(Vector3 point, Vector3 normal, float force)
        {
            AddLoad(point, normal, force, float.MaxValue);
        }

        /// <summary>Speed into a steep face, or push against it, above which a side contact
        /// digs. Below both, the face holds -- a wheel resting against the side of a pile, or
        /// nudging into it, is stopped by it or climbs it, as by any slope.</summary>
        public const float SideDigSpeed = 1f;
        public const float SideDigForce = 3000f;

        /// <summary>
        /// The same, with how fast the body was moving into the sand. A side contact digs only
        /// when it is a real blow or a real shove: digging at every touch of a steep face let a
        /// machine plough through the wall of a freshly filled region it should have hit.
        /// </summary>
        public void AddLoad(Vector3 point, Vector3 normal, float force, float speed)
        {
            if (force <= 0f) return;
            if (Mathf.Abs(normal.y) < SideContact)
            {
                if ((speed > SideDigSpeed || force > SideDigForce) && digs.Count < 256) digs.Add(point);
                return;
            }
            loads.Add(new Vector3(point.x, point.z, force));
        }

        /// <summary>
        /// Sinks the columns under the loads recorded since the last call. A column under a
        /// pressure sinks towards pressure over <see cref="PressStiffness"/> and stays there --
        /// sand does not spring back -- so a wheel leaves a rut as deep as it was heavy. Half of
        /// what it sinks by is pushed out to the columns beside it that are not loaded, so a rut
        /// has a lip; the other half is sand packing tighter. A load is spread over the nine
        /// columns around its point, the size of a contact patch.
        /// </summary>
        public void Press(float dt)
        {
            pressStep++;
            // Forget what has not been stood on for a while, now and then.
            if ((pressStep & 63) == 0 && held.Count > 0)
            {
                expired.Clear();
                foreach (KeyValuePair<long, int> kv in held) if (kv.Value < pressStep) expired.Add(kv.Key);
                for (int n = 0; n < expired.Count; n++) held.Remove(expired[n]);
            }
            if (!Enabled || loads.Count == 0) { loads.Clear(); return; }

            batch.Clear();
            pressure.Clear();
            for (int n = 0; n < loads.Count; n++)
            {
                Vector3 l = loads[n];
                int i0 = Mathf.FloorToInt(l.x / Cell), j0 = Mathf.FloorToInt(l.y / Cell);
                float each = l.z / (9f * Cell * Cell);
                // Held over a wider patch than the load is spread on: a wheel's footprint, which
                // reaches past its contact on both sides.
                for (int dj = -HoldReach; dj <= HoldReach; dj++)
                    for (int di = -HoldReach; di <= HoldReach; di++)
                        held[Key(i0 + di, j0 + dj)] = pressStep + HoldSteps;
                for (int dj = -1; dj <= 1; dj++)
                    for (int di = -1; di <= 1; di++)
                    {
                        long key = Key(i0 + di, j0 + dj);
                        float p;
                        if (pressure.TryGetValue(key, out p)) pressure[key] = p + each;
                        else { pressure[key] = each; batch.Add(key); }
                    }
            }
            loads.Clear();

            for (int n = 0; n < batch.Count; n++)
            {
                int i = KeyI(batch[n]), j = KeyJ(batch[n]);
                Chunk c = Find(i, j);
                if (c == null) continue;
                int k = Slot(i, j);
                float depth = c.top[k] - c.bottom[k];
                if (depth <= MinThickness) continue;

                int mat = Mathf.Clamp(c.material[k], 0, firmnessOf.Length - 1);
                float want = pressure[batch[n]] / firmnessOf[mat];
                float most = (depth + c.packed[k]) * packLimitOf[mat];
                if (want > most) want = most;
                float sink = Mathf.Min(want - c.packed[k], PressRate * dt);
                if (sink <= 1e-4f) continue;
                if (depth - sink < MinThickness) sink = depth - MinThickness;
                if (sink <= 0f) continue;

                c.top[k] -= sink;
                c.packed[k] += sink;
                Settle(c, k);
                MarkDirty(c, i, j);
                Activate(i, j);

                // Half of it out to the sides -- past the footprint, as a lip beside the rut. It
                // went to the next columns over, which were still under the tyre: they rose into
                // it, and a wheel that found itself inside the sand was dug down through it.
                float spill = sink * 0.5f;
                int free = 0;
                for (int n2 = 0; n2 < 8; n2++)
                {
                    int ti = i + LipI[n2] * LipReach, tj = j + LipJ[n2] * LipReach;
                    if (!pressure.ContainsKey(Key(ti, tj)) && !Held(ti, tj)) free++;
                }
                if (free == 0) continue;
                for (int n2 = 0; n2 < 8; n2++)
                {
                    int ti = i + LipI[n2] * LipReach, tj = j + LipJ[n2] * LipReach;
                    if (!pressure.ContainsKey(Key(ti, tj)) && !Held(ti, tj))
                        Deposit(ti, tj, spill / free, c.material[k], c.colour[k]);
                }
            }
        }

        private readonly Dictionary<long, float> pressure = new Dictionary<long, float>();

        // Columns something is standing on: loaded by a collider within the last few presses.
        // Sand cannot flow in under a wheel resting on it, nor out from under it, and a wheel
        // standing in its own rut is not buried. Slumping refilled the rut beneath a wheel, the
        // refilled sand stood above the wheel's bottom, and the buried rule then dug it out from
        // under it as grains -- over and over, until the machine had gone through the pile.
        private readonly Dictionary<long, int> held = new Dictionary<long, int>();
        private readonly List<long> expired = new List<long>();
        private int pressStep;
        private const int HoldSteps = 10;
        private const int HoldReach = 2;
        private const int LipReach = HoldReach + 1;
        private static readonly int[] LipI = { 1, 1, 0, -1, -1, -1, 0, 1 };
        private static readonly int[] LipJ = { 0, 1, 1, 1, 0, -1, -1, -1 };

        /// <summary>Whether something is standing on a column.</summary>
        private bool Held(int i, int j)
        {
            int until;
            return held.Count > 0 && held.TryGetValue(Key(i, j), out until) && until >= pressStep;
        }

        /// <summary>
        /// The surface heights of a chunk's mesh: one per column centre, seventeen a side so
        /// a chunk's mesh meets its neighbour's. A column with no sand takes the ground's
        /// height less <paramref name="below"/>, so the mesh dips under the ground at a pile's
        /// edge instead of standing on it. <paramref name="known"/> is false where the ground
        /// is unknown.
        /// </summary>
        public void SurfaceOf(Chunk c, float below, float[] heights, bool[] sand, bool[] known)
        {
            int side = ChunkSide + 1;
            int baseI = c.cx * ChunkSide, baseJ = c.cz * ChunkSide;
            for (int vj = 0; vj < side; vj++)
            {
                for (int vi = 0; vi < side; vi++)
                {
                    int v = vi + vj * side;
                    int i = baseI + vi, j = baseJ + vj;
                    Chunk o = (vi < ChunkSide && vj < ChunkSide) ? c : Find(i, j);
                    float h = 0f;
                    bool has = false;
                    if (o != null)
                    {
                        int k = Slot(i, j);
                        if (o.top[k] - o.bottom[k] > MinThickness) { h = o.top[k]; has = true; }
                    }
                    if (!has) h = ground.Height((i + 0.5f) * Cell, (j + 0.5f) * Cell) - below;
                    heights[v] = h;
                    sand[v] = has;
                    known[v] = h > NoGround;
                }
            }
        }

        // ------------------------------------------------------------------ relaxing

        /// <summary>
        /// Lets a pile that is steeper than its angle of repose slump. Only columns that
        /// changed, and what they touch, are looked at, and at most
        /// <paramref name="budget"/> of them per call, so a settled bed costs nothing.
        /// Sand moves, it is never made or lost.
        /// </summary>
        public void Relax(int budget, float budgetMs)
        {
            System.Diagnostics.Stopwatch timer = System.Diagnostics.Stopwatch.StartNew();
            Relax(budget);
            while (active.Count > 0 && timer.Elapsed.TotalMilliseconds < budgetMs) Relax(100);
        }

        /// <summary>Columns waiting to be checked for slumping.</summary>
        public int Unsettled { get { return active.Count; } }

        public void Relax(int budget)
        {
            if (!Enabled || ground == null || active.Count == 0) return;

            // Take a batch off the front. Slumping re-activates the columns it touches,
            // which appends to the same list, so what is left over and what is new both
            // wait for the next call.
            int take = Mathf.Min(active.Count, budget);
            batch.Clear();
            for (int n = 0; n < take; n++)
            {
                batch.Add(active[n]);
                activeSet.Remove(active[n]);
            }
            active.RemoveRange(0, take);

            for (int n = 0; n < batch.Count; n++)
            {
                int i = KeyI(batch[n]), j = KeyJ(batch[n]);
                for (int dj = -1; dj <= 1; dj++)
                {
                    for (int di = -1; di <= 1; di++)
                    {
                        if (di == 0 && dj == 0) continue;
                        Slump(i, j, i + di, j + dj, (di != 0 && dj != 0) ? 1.4142f : 1f);
                    }
                }
            }
        }

        /// <summary>Moves sand between two neighbouring columns if they are steeper
        /// than the material allows, from whichever is higher.</summary>
        private void Slump(int ai, int aj, int bi, int bj, float distance)
        {
            bool sa = false, sb = false;
            float ta = EffectiveTop(ai, aj, ref sa);
            float tb = EffectiveTop(bi, bj, ref sb);
            if (!sa && !sb) return;
            if (ta <= NoGround || tb <= NoGround) return;
            if (Held(ai, aj) || Held(bi, bj)) return;   // under a wheel: see held

            int hiI, hiJ, loI, loJ;
            float hiTop, loTop;
            bool hiSand;
            if (ta >= tb) { hiI = ai; hiJ = aj; loI = bi; loJ = bj; hiTop = ta; loTop = tb; hiSand = sa; }
            else { hiI = bi; hiJ = bj; loI = ai; loJ = aj; hiTop = tb; loTop = ta; hiSand = sb; }
            if (!hiSand) return;   // the higher one is bare ground; nothing to shed

            Chunk hc = Find(hiI, hiJ);
            int hk = Slot(hiI, hiJ);
            int material = hc.material[hk];
            float limit = slope[Mathf.Clamp(material, 0, slope.Length - 1)] * Cell * distance;

            float excess = (hiTop - loTop) - limit;
            if (excess <= 0f) return;

            // Move half the excess: both columns then sit at the limit. Never more than
            // the higher column holds above its own ground.
            //
            // Two rules keep the volume exact. A column left with less than the
            // minimum thickness counts as empty and would lose what remained, so when a
            // move would leave that little it takes the lot. And a move smaller than the
            // minimum would land on an empty column as a sliver that counts as nothing
            // and is overwritten by the next arrival, so it is not made at all.
            float avail = hc.top[hk] - hc.bottom[hk];
            float move = Mathf.Min(excess * 0.5f, avail);
            if (avail - move < MinThickness) move = avail;
            if (move < MinThickness) return;

            // In layers: what comes off the surface layer keeps its medium, and if the move
            // reaches into the layer below, that part keeps its own -- the lower first, so the
            // surface's sand ends on top where it was.
            float surface = Layered(hc, hk) ? hc.top[hk] - hc.split[hk] : avail;
            float upper = Mathf.Min(move, surface), lower = move - upper;
            uint colour = hc.colour[hk];
            if (lower > 0f && lower < MinThickness) { upper += lower; lower = 0f; }
            if (upper < MinThickness && lower > 0f) { lower += upper; upper = 0f; }
            if (lower > 0f && !Deposit(loI, loJ, lower, hc.lowMaterial[hk], hc.lowColour[hk])) return;
            if (upper > 0f && !Deposit(loI, loJ, upper, material, colour)) return;
            Withdraw(hc, hiI, hiJ, move);
        }
    }
}
