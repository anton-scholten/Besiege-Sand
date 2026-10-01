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
        }

        private readonly Dictionary<long, Chunk> chunks = new Dictionary<long, Chunk>();
        private readonly List<Chunk> dirty = new List<Chunk>();
        private readonly List<Chunk> retired = new List<Chunk>();

        private IBedGround ground;
        private float[] slope = new float[SandMaterials.Count];
        private float[] grainVolume = new float[SandMaterials.Count];
        private float[] grainRadius = new float[SandMaterials.Count];

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
            }
            for (int i = 0; i < table.Length; i++)
            {
                float r = table[i].radius;
                grainRadius[i] = r;
                grainVolume[i] = 4f / 3f * Mathf.PI * r * r * r / PackingFraction;
                // Angle of repose, as a height step per cell. A little generous so that
                // a pile the dynamic solver left at rest is not immediately disturbed.
                slope[i] = Mathf.Max(table[i].friction, 0.2f) * 1.15f;
            }
        }

        public void Clear()
        {
            foreach (KeyValuePair<long, Chunk> kv in chunks) retired.Add(kv.Value);
            chunks.Clear();
            dirty.Clear();
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

        private void Activate(int i, int j)
        {
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
            if (dirty.Count == 0) return null;
            Chunk c = dirty[dirty.Count - 1];
            dirty.RemoveAt(dirty.Count - 1);
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
                c.material[k] = (byte)material;
                c.colour[k] = colour;
                if (thickness > MinThickness) { c.filled++; filledColumns++; }
            }
            else
            {
                float existing = c.top[k] - c.bottom[k];
                float share = thickness / (existing + thickness);
                c.top[k] += thickness;
                c.colour[k] = Blend(c.colour[k], colour, share);
                if (share > 0.5f) c.material[k] = (byte)material;
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
                c.filled--;
                filledColumns--;
            }
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
            row = int.MaxValue;
            for (int j = jStart; j <= j1; j++)
            {
                if (looked >= maxColumns) { row = j; break; }
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
            return filled;
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
                if (SandFormat.RestSteps(flags) < restSteps) continue;
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
            int span = ChunkSide;

            for (int s = 0; s < shapes.Count && tests < budget; s++)
            {
                Vector3 lo, hi;
                shapes.Extent(s, out lo, out hi);
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
                                float lowest = float.MaxValue;
                                for (float y = bottom + sampleStep * 0.5f; y <= top + sampleStep * 0.5f; y += sampleStep)
                                {
                                    tests++;
                                    if (shapes.Distance(s, new Vector3(cx, Mathf.Min(y, top), cz)) < margin)
                                    {
                                        lowest = y;
                                        break;
                                    }
                                }
                                if (lowest == float.MaxValue) continue;

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

        // ------------------------------------------------------------------ relaxing

        /// <summary>
        /// Lets a pile that is steeper than its angle of repose slump. Only columns that
        /// changed, and what they touch, are looked at, and at most
        /// <paramref name="budget"/> of them per call, so a settled bed costs nothing.
        /// Sand moves, it is never made or lost.
        /// </summary>
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

            uint colour = hc.colour[hk];
            if (!Deposit(loI, loJ, move, material, colour)) return;
            Withdraw(hc, hiI, hiJ, move);
        }
    }
}
