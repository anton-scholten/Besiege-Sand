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

    /// <summary>A sink that can be given a grain's speed too: what a wheel digs out of the sand leaves
    /// with the speed of the wheel's surface where it was, which is how a spinning wheel throws sand.</summary>
    public interface IBedKickSink : IBedSink
    {
        void Release(Vector3 position, int material, uint colour, Vector3 velocity);
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
        private const int ChunkShift = 4;                 // ChunkSide is 1 << ChunkShift
        private const int ChunkMask = ChunkSide - 1;

        /// <summary>A pile of grains fills this fraction of the space it occupies.</summary>
        public const float PackingFraction = 0.62f;

        private const float NoGround = -9000f;
        private const float MinThickness = 0.004f;

        /// <summary>One chunk's worth of columns.</summary>
        public class Chunk
        {
            public int cx, cz;
            public float[] top = new float[ChunkSide * ChunkSide];
            public int[] holdUntil;         // per column, the press step it is held until (see Held); made when first held
            public float[] bottom = new float[ChunkSide * ChunkSide];
            public byte[] material = new byte[ChunkSide * ChunkSide];
            public uint[] colour = new uint[ChunkSide * ChunkSide];
            public int filled;           // columns thicker than MinThickness
            public bool queued;          // already in the renderer's dirty list
            public bool[] awake = new bool[ChunkSide * ChunkSide];   // column is in the bed's list to be checked for slumping
            public object renderHandle;  // the renderer keeps its mesh here
            public object colliderHandle;   // the collider manager keeps its collider here
            public bool colliderDirty;      // columns changed since the collider was cooked
            public bool netDirty;           // changed since it was last sent to the other players
            public int wantStamp;           // the collider manager's: last step it was asked for
            public int topStamp;            // and the step it last measured the chunk's highest sand
            public float topCache;
            public int digStamp;            // Release's: the call it last measured the chunk's highest sand in
            public float digTop;
            public int wantIndex;           // and where in that step's list
            public float[] packed = new float[ChunkSide * ChunkSide];   // how far each column has been pressed down

            // A column holds up to two media, one on the other: from bottom to split the lower
            // layer, from split to top the surface layer that material and colour describe.
            // With one, split is the bottom.
            public float[] split = new float[ChunkSide * ChunkSide];
            public byte[] lowMaterial = new byte[ChunkSide * ChunkSide];
            public uint[] lowColour = new uint[ChunkSide * ChunkSide];

            // Under those two, a column that has had more media laid on it holds the rest, lowest
            // first: each with the height it reaches to. Only such columns have an entry at all.
            public Dictionary<int, List<Deep>> deep;

            // While a settle writes into it: what FlushBatch is to be told, and the highest top written.
            public int settleFlags;
            public float settleTop;
        }

        /// <summary>A layer below a column's lower layer: its medium and colour, and the height it goes up to.</summary>
        public struct Deep
        {
            public byte material;
            public uint colour;
            public float upper;
        }

        /// <summary>The most media a column holds at once, one on another. A seventh pushes the lowest into the one above it.</summary>
        public const int MaxLayers = 6;


        private static void ClearDeep(Chunk c, int k)
        {
            if (c.deep != null && c.deep.Count > 0) c.deep.Remove(k);
        }

        /// <summary>A copy of the deep layers of another chunk, for a chunk that takes its place.</summary>
        private static void CopyDeep(Chunk from, Chunk to)
        {
            to.deep = null;
            if (from.deep == null || from.deep.Count == 0) return;
            to.deep = new Dictionary<int, List<Deep>>();
            foreach (KeyValuePair<int, List<Deep>> kv in from.deep) to.deep[kv.Key] = new List<Deep>(kv.Value);
        }

        /// <summary>The column's layers from the top down: how much each holds, in its medium and colour.</summary>
        private static int Layers(Chunk c, int k, float[] amount, byte[] material, uint[] colour)
        {
            int n = 0;
            float lower = c.split[k] > c.bottom[k] ? c.split[k] : c.bottom[k];
            amount[n] = c.top[k] - lower; material[n] = c.material[k]; colour[n] = c.colour[k]; n++;
            if (c.split[k] > c.bottom[k])
            {
                List<Deep> l = null;
                bool any = c.deep != null && c.deep.TryGetValue(k, out l) && l.Count > 0;
                float floor = any ? l[l.Count - 1].upper : c.bottom[k];
                amount[n] = c.split[k] - floor; material[n] = c.lowMaterial[k]; colour[n] = c.lowColour[k]; n++;
                if (any)
                    for (int i = l.Count - 1; i >= 0 && n < amount.Length; i--)
                    {
                        float under = i > 0 ? l[i - 1].upper : c.bottom[k];
                        amount[n] = l[i].upper - under; material[n] = l[i].material; colour[n] = l[i].colour; n++;
                    }
            }
            return n;
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
            // As many times as it takes: a column cut down through several layers is left with the one it is in.
            while (c.split[k] > c.bottom[k] && c.top[k] <= c.split[k] + MinThickness)
            {
                c.material[k] = c.lowMaterial[k];
                c.colour[k] = c.lowColour[k];
                List<Deep> l;
                if (c.deep != null && c.deep.TryGetValue(k, out l) && l.Count > 0)
                {
                    Deep d = l[l.Count - 1];
                    l.RemoveAt(l.Count - 1);
                    if (l.Count == 0) c.deep.Remove(k);
                    c.lowMaterial[k] = d.material;
                    c.lowColour[k] = d.colour;
                    c.split[k] = d.upper;
                }
                else c.split[k] = c.bottom[k];
            }
        }

        private readonly Dictionary<long, Chunk> chunks = new Dictionary<long, Chunk>();
        // Oldest first. It was newest first, and while the brush was held the chunks under it
        // were queued again every frame and always went first, so a chunk that had just been
        // given sand for the first time waited -- drawn as bare ground -- until the button was
        // let go.
        private readonly List<Chunk> dirty = new List<Chunk>();
        private int dirtyHead;
        // Chunks that have never been drawn, taken before the rest: ground that has just had sand
        // laid on it should show at once, however many chunks already drawn are waiting to be
        // redrawn, or a brush paints as a patchwork of squares coming in one at a time.
        private readonly List<Chunk> fresh = new List<Chunk>();
        private int freshHead;
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
        // Taken from the front by index; the spent part is cut off now and then, not on every call.
        private readonly List<long> active = new List<long>();
        private int activeHead;
        private readonly List<long> batch = new List<long>();

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
                // In double: tens of thousands of columns summed in float lost the last few millimetres of
                // each and read a big pile two per cent light.
                double sum = 0.0;
                foreach (KeyValuePair<long, Chunk> kv in chunks)
                {
                    Chunk c = kv.Value;
                    for (int k = 0; k < c.top.Length; k++)
                    {
                        float t = c.top[k] - c.bottom[k];
                        if (t > MinThickness) sum += t;
                    }
                }
                return (float)(sum * Cell * Cell);
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
            float least = float.MaxValue;
            for (int i = 0; i < slope.Length; i++) least = Mathf.Min(least, slope[i]);
            gentlest = least == float.MaxValue ? 0f : least * Cell;
        }

        // The smallest step between neighbouring columns that any medium will slump, so a
        // column whose neighbours are all within it can be skipped without asking each.
        private float gentlest;

        public void Clear()
        {
            journalBefore = null;
            journalTouched = null;
            emptied.Clear();
            foreach (KeyValuePair<long, Chunk> kv in chunks) retired.Add(kv.Value);
            chunks.Clear();
            dirty.Clear();
            dirtyHead = 0;
            fresh.Clear();
            freshHead = 0;
            active.Clear();
            activeHead = 0;
            batch.Clear();
            generation++;
            rollCx = int.MinValue;
            ForgetDeferred();
            filledColumns = 0;
            hasExtent = false;
        }

        /// <summary>The box round every column with sand in it now, ground to top; false when there is none.</summary>
        public bool FilledBounds(out Bounds box)
        {
            box = new Bounds();
            bool any = false;
            Vector3 lo = Vector3.zero, hi = Vector3.zero;
            foreach (KeyValuePair<long, Chunk> kv in chunks)
            {
                Chunk c = kv.Value;
                if (c.filled <= 0) continue;
                for (int k = 0; k < c.top.Length; k++)
                {
                    if (c.top[k] - c.bottom[k] <= MinThickness) continue;
                    float x = (c.cx * ChunkSide + (k & ChunkMask)) * Cell, z = (c.cz * ChunkSide + (k >> ChunkShift)) * Cell;
                    Vector3 a = new Vector3(x, c.bottom[k], z), b = new Vector3(x + Cell, c.top[k], z + Cell);
                    if (!any) { lo = a; hi = b; any = true; }
                    else { lo = Vector3.Min(lo, a); hi = Vector3.Max(hi, b); }
                }
            }
            if (any) box.SetMinMax(lo, hi);
            return any;
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

        // The last chunk looked up. A brush, a slump and a mesh edge all touch long runs of
        // columns in one chunk, and each of those was a dictionary lookup on a 64-bit key.
        //
        // It is per thread: the solver's workers read the bed (TryTop) at the same moment, and one
        // shared (chunk, cx, cz) written by several threads is read torn -- a chunk from one thread
        // with the coordinates of another -- and answered every sixth height wrongly. A thread's
        // entry is good for the bed and the generation it was made in: Clear is the only thing that
        // lets go of a chunk, and it moves the generation on.
        //
        // The entry is one object per thread, read with a single thread-static access: five thread-static
        // fields read one by one cost Mono more than the dictionary lookup they were saving.
        private class LastChunk
        {
            public Chunk chunk;
            public SandBed bed;
            public int cx, cz, generation;
        }
        [System.ThreadStatic] private static LastChunk tsLast;
        private int generation;

        // The main thread's own, used while no other thread is at work (Workers.Running at nought): a thread-static
        // read costs this Mono some tens of nanoseconds, and slumping a column looks up forty-odd chunks -- which was
        // most of what slumping cost.
        private readonly LastChunk mainLast = new LastChunk();

        private LastChunk Last()
        {
            LastChunk l = tsLast;
            if (l == null) { l = new LastChunk(); tsLast = l; }
            return l;
        }

        private Chunk FindChunk(int cx, int cz)
        {
            LastChunk l = Workers.Running > 0 ? Last() : mainLast;
            if (l.chunk != null && l.bed == this && l.generation == generation && cx == l.cx && cz == l.cz) return l.chunk;
            Chunk c;
            if (!chunks.TryGetValue(Key(cx, cz), out c)) return null;
            l.chunk = c; l.bed = this; l.generation = generation; l.cx = cx; l.cz = cz;
            return c;
        }

        /// <summary>The chunk at chunk coordinates (<paramref name="cx"/>, <paramref name="cz"/>), or null.</summary>
        public Chunk ChunkAt(int cx, int cz)
        {
            Chunk c;
            return chunks.TryGetValue(Key(cx, cz), out c) ? c : null;
        }

        private Chunk Find(int i, int j)
        {
            return FindChunk(i >> ChunkShift, j >> ChunkShift);      // an arithmetic shift floors, negatives included
        }

        private Chunk FindOrCreate(int i, int j)
        {
            int cx = i >> ChunkShift, cz = j >> ChunkShift;
            Chunk c = FindChunk(cx, cz);
            if (c == null)
            {
                c = new Chunk();
                c.cx = cx;
                c.cz = cz;
                chunks[Key(cx, cz)] = c;
                LastChunk l = Workers.Running > 0 ? Last() : mainLast;
                l.chunk = c; l.bed = this; l.generation = generation; l.cx = cx; l.cz = cz;
                rollCx = int.MinValue;     // a chunk the roll saw as missing is here now
            }
            return c;
        }

        private static int Slot(int i, int j)
        {
            return (i & ChunkMask) + ((j & ChunkMask) << ChunkShift);
        }

        private void MarkDirty(Chunk c, int i, int j)
        {
            Queue(c);
            MarkNet(c);
            c.colliderDirty = true;
            int ei = i & ChunkMask, ej = j & ChunkMask;
            if (ei == 0) { Chunk n = Find(i - 1, j); if (n != null) n.colliderDirty = true; }
            if (ej == 0) { Chunk n = Find(i, j - 1); if (n != null) n.colliderDirty = true; }
            if (ei == 0 && ej == 0) { Chunk n = Find(i - 1, j - 1); if (n != null) n.colliderDirty = true; }
            // A chunk's mesh is read from the columns from one before its first to one past its last
            // (the extra rim is for the light, see ChunkMesher), so a column near a chunk's edge is
            // in its neighbour's mesh too, and that one has to be rebuilt as well.
            int loX = ei <= 1 ? -1 : 0, hiX = ei == ChunkMask ? 1 : 0;
            int loZ = ej <= 1 ? -1 : 0, hiZ = ej == ChunkMask ? 1 : 0;
            if (loX == 0 && hiX == 0 && loZ == 0 && hiZ == 0) return;
            int cx = i >> ChunkShift, cz = j >> ChunkShift;
            for (int dz = loZ; dz <= hiZ; dz++)
                for (int dx = loX; dx <= hiX; dx++)
                    if (dx != 0 || dz != 0) Queue(FindChunk(cx + dx, cz + dz));
        }

        private void Queue(Chunk c)
        {
            if (c == null || c.queued) return;
            c.queued = true;
            if (c.renderHandle == null) fresh.Add(c); else dirty.Add(c);
        }

        /// <summary>Puts a chunk taken from the queue back in it, to be drawn on a later frame.</summary>
        public void Requeue(Chunk c) { Queue(c); }

        /// <summary>Chunks waiting to be drawn again, and how many of them have never been drawn.</summary>
        public int DirtyCount { get { return dirty.Count - dirtyHead + fresh.Count - freshHead + emptied.Count; } }

        private bool quietFill;

        private void Activate(int i, int j)
        {
            if (quietFill) return;
            Activate(Find(i, j), i, j);
        }

        private void Activate(Chunk c, int i, int j)
        {
            if (quietFill) return;
            if (c != null)
            {
                int k = Slot(i, j);
                if (c.awake[k]) return;
                c.awake[k] = true;
            }
            active.Add(Key(i, j));
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
            // Emptied wholesale first -- an undo of a loaded file, taken back while it was still being drawn: those
            // chunks were behind thousands never drawn yet, and the sand stood on screen with nothing under it.
            if (emptied.Count > 0)
            {
                Chunk e = emptied[emptied.Count - 1];
                emptied.RemoveAt(emptied.Count - 1);
                return e;
            }
            if (freshHead < fresh.Count)
            {
                Chunk f = fresh[freshHead++];
                if (freshHead == fresh.Count) { fresh.Clear(); freshHead = 0; }
                f.queued = false;
                return f;
            }
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

        // Chunks that were drawn and have been emptied wholesale (Adopt): drawn again before anything else.
        private readonly List<Chunk> emptied = new List<Chunk>();

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

        /// <summary>
        /// Reads an <paramref name="n"/> by <paramref name="n"/> block of columns, from column
        /// (<paramref name="i0"/>, <paramref name="j0"/>), into row-major arrays: top, bottom, colour and
        /// the table row, which is -1 where a column holds no sand. One lookup a chunk instead of three
        /// a column, for the renderer, which reads seventeen by seventeen of them for every chunk.
        /// </summary>
        public void ReadBlock(int i0, int j0, int n, float[] top, float[] bottom, uint[] colour, int[] material)
        {
            for (int vj = 0; vj < n; vj++)
            {
                int j = j0 + vj, cz = j >> ChunkShift, rowBase = (j & ChunkMask) << ChunkShift;
                Chunk c = null;
                int have = int.MinValue;
                for (int vi = 0; vi < n; vi++)
                {
                    int i = i0 + vi, cx = i >> ChunkShift;
                    if (cx != have) { c = FindChunk(cx, cz); have = cx; }
                    int k = vi + vj * n;
                    material[k] = -1;
                    if (c == null) continue;
                    int slot = (i & ChunkMask) + rowBase;
                    float t = c.top[slot], b = c.bottom[slot];
                    if (t - b <= MinThickness) continue;
                    top[k] = t; bottom[k] = b; colour[k] = c.colour[slot]; material[k] = c.material[slot];
                }
            }
        }

        /// <summary>
        /// An explosion in the bed: sand within <paramref name="radius"/> of <paramref name="centre"/> is
        /// blown out of the ground, to a depth of <paramref name="depth"/> at the middle and none at the
        /// edge (a bowl). A part of it is thrown as grains, to <paramref name="sink"/>, which gives them
        /// their speed -- as many as <paramref name="maxGrains"/>, coarse enough that they hold that much
        /// of it; the rest is thrown only as far as the ground just outside the hole, and lies there as a
        /// rim. What the bed and the grains hold in all is what it held before, medium by medium, and what
        /// is left steeper than it stands slumps in the usual way. Returns the number of grains.
        /// </summary>
        public int Blast(Vector3 centre, float radius, float depth, IBedSink sink, int maxGrains)
        {
            if (!Enabled || filledColumns <= 0 || radius <= 0f || depth <= 0f || maxGrains <= 0) return 0;
            int i0 = Mathf.FloorToInt((centre.x - radius) / Cell), i1 = Mathf.FloorToInt((centre.x + radius) / Cell);
            int j0 = Mathf.FloorToInt((centre.z - radius) / Cell), j1 = Mathf.FloorToInt((centre.z + radius) / Cell);
            float area = Cell * Cell;

            // How much there is to blow out, to size the grains.
            double total = 0.0;
            for (int j = j0; j <= j1; j++)
                for (int i = i0; i <= i1; i++)
                {
                    float dx = (i + 0.5f) * Cell - centre.x, dz = (j + 0.5f) * Cell - centre.z;
                    Chunk c = Find(i, j);
                    if (c == null) continue;
                    int k = Slot(i, j);
                    float have = c.top[k] - c.bottom[k];
                    if (have <= MinThickness) continue;
                    // How far the blast is from the sand's surface here, in three dimensions: a bomb above
                    // or in the pile digs less the farther it is from it, and one in the air digs nothing.
                    float dy = c.top[k] - centre.y;
                    float d2 = (dx * dx + dy * dy + dz * dz) / (radius * radius);
                    if (d2 >= 1f) continue;
                    total += Mathf.Min(depth * (1f - d2), have) * area;
                }
            if (total <= 0.0) return 0;

            // A share of it is thrown as grains, the coarser the more there is of it.
            const float Thrown = 0.4f;
            int level = 0;
            while (level < SandMaterials.LodLevels && Thrown * total / grainVolume[SandMaterials.LodRow(SandMaterials.Sand, level)] > maxGrains) level++;
            float throwShare = Mathf.Min(Thrown, (float)(maxGrains * 0.9 * grainVolume[SandMaterials.LodRow(SandMaterials.Sand, level)] / total));

            float[] amount = new float[MaxLayers]; byte[] material = new byte[MaxLayers]; uint[] colour = new uint[MaxLayers];
            float[] removed = new float[256]; uint[] shade = new uint[256];
            double owed = 0.0;
            int released = 0;
            float thrownVolume = 0f;
            bool quiet = quietFill;
            for (int j = j0; j <= j1; j++)
                for (int i = i0; i <= i1; i++)
                {
                    float dx = (i + 0.5f) * Cell - centre.x, dz = (j + 0.5f) * Cell - centre.z;
                    Chunk c = Find(i, j);
                    if (c == null) continue;
                    int k = Slot(i, j);
                    float have = c.top[k] - c.bottom[k];
                    if (have <= MinThickness) continue;
                    float dy = c.top[k] - centre.y;
                    float d2 = (dx * dx + dy * dy + dz * dz) / (radius * radius);
                    if (d2 >= 1f) continue;
                    float want = Mathf.Min(depth * (1f - d2), have);
                    if (have - want < MinThickness) want = have;
                    float surface = c.top[k];

                    // What comes out, layer by layer from the top.
                    int layers = Layers(c, k, amount, material, colour);
                    float left = want; int firstLayer = -1;
                    for (int q = 0; q < layers && left > 0f; q++)
                    {
                        float take = Mathf.Min(left, amount[q]);
                        left -= take;
                        if (take <= 0f) continue;
                        if (firstLayer < 0) firstLayer = q;
                        removed[material[q]] += take * area;
                        shade[material[q]] = colour[q];
                    }
                    Withdraw(c, i, j, want);

                    // Some of it leaves as grains, of the medium of the layer that was on top.
                    owed += want * area * throwShare;
                    int row = firstLayer >= 0 ? material[firstLayer] : c.material[k];
                    if (row < SandMaterials.Count) row = SandMaterials.LodRow(row, level);
                    float gv = grainVolume[Mathf.Clamp(row, 0, grainVolume.Length - 1)];
                    while (owed >= gv && released < maxGrains && sink.Room > 0)
                    {
                        float ux = (Rand() - 0.5f) * Cell, uz = (Rand() - 0.5f) * Cell;
                        sink.Release(new Vector3((i + 0.5f) * Cell + ux, surface + 0.05f + Rand() * 0.1f, (j + 0.5f) * Cell + uz), row, firstLayer >= 0 ? colour[firstLayer] : c.colour[k]);
                        owed -= gv; released++;
                        int baseRow = firstLayer >= 0 ? material[firstLayer] : c.material[k];
                        removed[baseRow] -= gv; thrownVolume += gv;
                    }
                }

            // The rest lies in a rim round the hole: a ring between the bowl's edge and half as far again.
            float rimIn = radius, rimOut = radius * 1.5f;
            int ri0 = Mathf.FloorToInt((centre.x - rimOut) / Cell), ri1 = Mathf.FloorToInt((centre.x + rimOut) / Cell);
            int rj0 = Mathf.FloorToInt((centre.z - rimOut) / Cell), rj1 = Mathf.FloorToInt((centre.z + rimOut) / Cell);
            List<int> ringI = new List<int>(), ringJ = new List<int>(); List<float> ringW = new List<float>(); double ringSum = 0.0;
            for (int j = rj0; j <= rj1; j++)
                for (int i = ri0; i <= ri1; i++)
                {
                    float dx = (i + 0.5f) * Cell - centre.x, dz = (j + 0.5f) * Cell - centre.z;
                    float d = Mathf.Sqrt(dx * dx + dz * dz);
                    if (d < rimIn || d >= rimOut) continue;
                    float mid = (rimIn + rimOut) * 0.5f;
                    float w = 1f - Mathf.Abs(d - mid) / (mid - rimIn);          // a ridge, highest half way out
                    if (w <= 0f) continue;
                    ringI.Add(i); ringJ.Add(j); ringW.Add(w); ringSum += w;
                }
            if (ringSum > 0.0)
                for (int m = 0; m < removed.Length; m++)
                {
                    if (removed[m] <= area * 0.01f) continue;
                    for (int q = 0; q < ringI.Count; q++)
                    {
                        float v = (float)(removed[m] * ringW[q] / ringSum);
                        if (v > 1e-9f) PourVolume((ringI[q] + 0.5f) * Cell, (ringJ[q] + 0.5f) * Cell, v, m, shade[m] != 0 ? shade[m] : 0xFFFFFFFFu);
                    }
                }
            quietFill = quiet;
            return released;
        }

        /// <summary>The layers of a column from the top down: how much each holds and its table row. Zero where there is no sand.</summary>
        public int LayersAt(int i, int j, float[] amount, byte[] material)
        {
            Chunk c = Find(i, j);
            if (c == null) return 0;
            int k = Slot(i, j);
            if (c.top[k] - c.bottom[k] <= MinThickness) return 0;
            uint[] colour = new uint[amount.Length];
            return Layers(c, k, amount, material, colour);
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

        /// <summary>
        /// The same, spread over a square of columns round the point: a landing grain is a grain's worth of sand, and a
        /// coarse one -- what wheels throw -- put into one column stood up as a spike, dust's most of all (a coarse grain
        /// of it is over a metre of one column), until slumping took it down. As many columns as leave each at least
        /// <see cref="SpreadEach"/> -- thinner than the bed's thinnest column, a share would count as nothing and be lost
        /// -- so a grain of under four times that goes into one, as before. False if none of it could be put down.
        /// </summary>
        public bool AddVolumeSpread(float x, float z, float volume, int material, uint colour)
        {
            float area = Cell * Cell;
            float h = volume / area;
            int n = Mathf.Min(MaxSpread, Mathf.FloorToInt(Mathf.Sqrt(h / SpreadEach)));
            if (n <= 1) return AddVolume(x, z, volume, material, colour);
            int ci = Mathf.FloorToInt(x / Cell), cj = Mathf.FloorToInt(z / Cell);
            int i0 = ci - (n - 1) / 2, j0 = cj - (n - 1) / 2;
            float each = volume / (n * n), lost = 0f;
            for (int dj = 0; dj < n; dj++)
                for (int di = 0; di < n; di++)
                    if (!Deposit(i0 + di, j0 + dj, each / area, material, colour)) lost += each;
            if (lost >= volume * 0.999f) return false;
            // What a column would not take (over unknown ground) goes into the middle one.
            if (lost > 0f) Deposit(ci, cj, lost / area, material, colour);
            return true;
        }

        /// <summary>The least depth, in metres, a landing grain's share puts on a column when it is spread.</summary>
        public const float SpreadEach = 0.02f;
        private const int MaxSpread = 15;

        // What a piece rolling downhill reads: the height of a column, from the chunk it is in and the
        // eight round it, which are looked up again only when the piece leaves them. Main thread only.
        private readonly Chunk[] rollAround = new Chunk[9];
        private int rollCx = int.MinValue, rollCz, rollTurn;

        private float RollTop(int i, int j)
        {
            int cx = i >> ChunkShift, cz = j >> ChunkShift;
            int dx = cx - rollCx, dz = cz - rollCz;
            Chunk c;
            if (dx < -1 || dx > 1 || dz < -1 || dz > 1 || rollCx == int.MinValue)
            {
                Around(cx, cz, rollAround);
                rollCx = cx; rollCz = cz;
                c = rollAround[4];
            }
            else c = rollAround[(dz + 1) * 3 + dx + 1];
            if (c != null)
            {
                int k = (i & ChunkMask) + ((j & ChunkMask) << ChunkShift);
                float t = c.top[k];
                if (t - c.bottom[k] > MinThickness) return t;
            }
            return ground.Height((i + 0.5f) * Cell, (j + 0.5f) * Cell);
        }

        private static readonly int[] RollDi = { 1, 1, 0, -1, -1, -1, 0, 1 };
        private static readonly int[] RollDj = { 0, 1, 1, 1, 0, -1, -1, -1 };

        /// <summary>The most columns a poured piece of sand rolls downhill before it stays where it is.</summary>
        private const int MostRoll = 256;

        /// <summary>
        /// Pours <paramref name="n"/> columns' worth of sand, the volumes in <paramref name="volumes"/> at
        /// (<paramref name="xs"/>, <paramref name="zs"/>). A column of one medium that stays within the angle
        /// of repose of all eight neighbours (give or take a layer's slack: see <c>FastAdd</c>) is added to
        /// straight in its chunk's arrays, on every core, and the chunk is marked once for the lot. Every
        /// other column is poured with <see cref="PourVolume"/> -- or, with <c>defer</c>, left where it
        /// lands. A brush's columns come in rows, so nearly all of them are the first kind.
        /// </summary>
        /// <param name="defer">Whether what cannot be added to where it stands is left where it lands, to
        /// be settled later (<see cref="SettleDeferred"/>), instead of being rolled to rest now.</param>
        public bool PourBatch(float[] xs, float[] zs, float[] volumes, int n, int material, uint colour, bool defer)
        {
            if (columnI.Length < n) { columnI = new int[n]; columnJ = new int[n]; columnDv = new float[n]; }
            for (int q = 0; q < n; q++)
            {
                columnI[q] = Mathf.FloorToInt(xs[q] / Cell); columnJ[q] = Mathf.FloorToInt(zs[q] / Cell);
                columnDv[q] = volumes[q] / (Cell * Cell);
            }
            return PourColumns(columnI, columnJ, columnDv, n, material, colour, defer);
        }

        private int[] columnI = new int[0], columnJ = new int[0];
        private float[] columnDv = new float[0];

        /// <summary>The same for columns named by their indices, each with the thickness to add.</summary>
        public bool PourColumns(int[] ci, int[] cj, float[] thickness, int n, int material, uint colour, bool defer)
        {
            float limit = slope[Mathf.Clamp(material, 0, slope.Length - 1)] * Cell;
            if (n < ParallelMinimum || !Workers.Parallel || Workers.Helpers == 0)
                return PourSerial(ci, cj, thickness, n, material, colour, limit, defer);

            // Bucket the columns by chunk. A chunk's columns are added to on one thread and no other
            // touches that chunk (the fast path reads nothing outside it), so no two threads share a
            // byte; whatever the fast path cannot do waits, and is poured on this thread afterwards.
            int minX = int.MaxValue, maxX = int.MinValue, minZ = int.MaxValue, maxZ = int.MinValue;
            for (int q = 0; q < n; q++)
            {
                int cx = ci[q] >> ChunkShift, cz = cj[q] >> ChunkShift;
                if (cx < minX) minX = cx; if (cx > maxX) maxX = cx;
                if (cz < minZ) minZ = cz; if (cz > maxZ) maxZ = cz;
            }
            int w = maxX - minX + 1, buckets = w * (maxZ - minZ + 1);
            if (buckets > 16384) return PourSerial(ci, cj, thickness, n, material, colour, limit, defer);
            if (bucketStart.Length < buckets + 1) { bucketStart = new int[buckets + 1]; bucketFlags = new int[buckets]; bucketTop = new float[buckets]; }
            System.Array.Clear(bucketStart, 0, buckets + 1);
            if (bucketOrder.Length < n) bucketOrder = new int[n];
            for (int q = 0; q < n; q++) bucketStart[((cj[q] >> ChunkShift) - minZ) * w + ((ci[q] >> ChunkShift) - minX) + 1]++;
            int busy = 0;
            for (int b = 0; b < buckets; b++) { if (bucketStart[b + 1] > 0) busy++; bucketStart[b + 1] += bucketStart[b]; }
            if (bucketFill.Length < buckets) bucketFill = new int[buckets];
            System.Array.Copy(bucketStart, bucketFill, buckets);
            for (int q = 0; q < n; q++) bucketOrder[bucketFill[((cj[q] >> ChunkShift) - minZ) * w + ((ci[q] >> ChunkShift) - minX)]++] = q;

            // The buckets with anything in them, as a list of work.
            if (busyList.Length < busy) busyList = new int[busy];
            int at = 0;
            for (int b = 0; b < buckets; b++) if (bucketStart[b + 1] > bucketStart[b]) busyList[at++] = b;
            int helpers = Workers.Helpers + 1;
            while (slowLists.Count < helpers) slowLists.Add(new List<int>());
            while (arounds.Count < helpers) arounds.Add(new Chunk[9]);
            for (int h = 0; h < helpers; h++) slowLists[h].Clear();
            float near = limit, far = limit * 1.4142f;
            int[] sortedOrder = bucketOrder, starts = bucketStart;
            bool ranAll = Workers.For(busy, delegate (int item, int worker)
            {
                int b = busyList[item];
                int cx = minX + b % w, cz = minZ + b / w;
                Chunk[] around = arounds[worker];
                Around(cx, cz, around);
                Chunk c = around[4];
                int flags = 0; float highest = 0f;
                List<int> slow = slowLists[worker];
                for (int r = starts[b]; r < starts[b + 1]; r++)
                {
                    int q = sortedOrder[r];
                    if (c != null && FastAdd(c, around, ci[q] & ChunkMask, cj[q] & ChunkMask, thickness[q], material, colour, near, far, ref highest, ref flags)) continue;
                    slow.Add(q);
                }
                bucketFlags[b] = flags; bucketTop[b] = highest;
            });
            bool ok = true;
            if (!ranAll)
            {
                // A thread died partway: some columns were added to and some not, and which is lost.
                // The bed is as consistent as it was (every add is one column), so mark what there is.
            }
            for (int item = 0; item < busy; item++)
            {
                int b = busyList[item];
                if ((bucketFlags[b] & 1) == 0) continue;
                Chunk c;
                if (chunks.TryGetValue(Key(minX + b % w, minZ + b / w), out c)) FlushBatch(c, bucketFlags[b], bucketTop[b]);
            }
            for (int h = 0; h < helpers; h++)
            {
                List<int> slow = slowLists[h];
                for (int r = 0; r < slow.Count; r++)
                {
                    int q = slow[r];
                    if (!SlowAdd(ci[q], cj[q], thickness[q], material, colour, defer)) ok = false;
                }
            }
            return ok;
        }

        private const int ParallelMinimum = 3000;
        private int[] bucketStart = new int[0], bucketOrder = new int[0], bucketFill = new int[0], busyList = new int[0];
        private int[] bucketFlags = new int[0];
        private float[] bucketTop = new float[0];
        private readonly List<List<int>> slowLists = new List<List<int>>();
        private readonly List<Chunk[]> arounds = new List<Chunk[]>();

        // The chunk at (cx, cz) and the eight round it, row by row from the one below and to the left.
        private void Around(int cx, int cz, Chunk[] into)
        {
            for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    Chunk n;
                    chunks.TryGetValue(Key(cx + dx, cz + dz), out n);
                    into[(dz + 1) * 3 + dx + 1] = n;
                }
        }

        /// <summary>
        /// Adds <paramref name="dv"/> to a column if that leaves it standing against all eight neighbours,
        /// all of them with sand and the column with the same medium and no layer under it. It writes
        /// only this chunk's own column and reads its neighbours', which another thread may be adding to
        /// at that moment: the heights read are then a step old, which the stability check allows for
        /// (a layer is a few millimetres and the step it must stay within is seven centimetres).
        /// False if it did not. <paramref name="around"/> is the chunk and its eight neighbours.
        ///
        /// A neighbour is allowed a step more than the angle of repose by <paramref name="dv"/>, what this
        /// column is being given: it is on the way to being given about the same, a brush's layer being
        /// smooth, and a check against the neighbour as it was before made every column on a slope
        /// standing at its angle fail, so each was poured by hand, a column at a time.
        /// </summary>
        private static bool FastAdd(Chunk c, Chunk[] around, int li, int lj, float dv, int material, uint colour, float near, float far,
                                    ref float highest, ref int flags)
        {
            if (dv > near) return false;
            int k = li + (lj << ChunkShift);
            float[] top = c.top, bottom = c.bottom;
            float own = top[k] - bottom[k];
            if (own <= MinThickness || c.material[k] != material || Layered(c, k)) return false;
            float lift = top[k] + dv;
            if (li >= 1 && li <= ChunkMask - 1 && lj >= 1 && lj <= ChunkMask - 1)
            {
                for (int dj = -1; dj <= 1; dj++)
                    for (int di = -1; di <= 1; di++)
                    {
                        if (di == 0 && dj == 0) continue;
                        int nk = k + di + (dj << ChunkShift);
                        if (top[nk] - bottom[nk] <= MinThickness || lift - top[nk] > ((di != 0 && dj != 0) ? far : near) + dv) return false;
                    }
            }
            else
            {
                for (int dj = -1; dj <= 1; dj++)
                    for (int di = -1; di <= 1; di++)
                    {
                        if (di == 0 && dj == 0) continue;
                        int nl = li + di, ml = lj + dj;
                        Chunk n = around[(ml < 0 ? 0 : ml > ChunkMask ? 2 : 1) * 3 + (nl < 0 ? 0 : nl > ChunkMask ? 2 : 1)];
                        if (n == null) return false;
                        int nk = (nl & ChunkMask) + ((ml & ChunkMask) << ChunkShift);
                        if (n.top[nk] - n.bottom[nk] <= MinThickness || lift - n.top[nk] > ((di != 0 && dj != 0) ? far : near) + dv) return false;
                    }
            }
            top[k] = lift;
            if (c.colour[k] != colour) c.colour[k] = Blend(c.colour[k], colour, dv / (own + dv));
            if (lift > highest) highest = lift;
            // What the chunk has to tell its neighbours when it is marked (see MarkDirty): a column
            // within one of the first edge is in the mesh of the chunk before it, the last in the one after.
            flags |= 1;
            if (li <= 1) flags |= 2;
            if (lj <= 1) flags |= 4;
            if (li == ChunkMask) flags |= 8;
            if (lj == ChunkMask) flags |= 16;
            if (li == 0) flags |= 32;
            if (lj == 0) flags |= 64;
            return true;
        }

        /// <summary>One column that could not be added to in place: poured, or, when the pile is being grown
        /// and settled later, put where it lands and the place noted.</summary>
        private bool SlowAdd(int i, int j, float dv, int material, uint colour, bool defer)
        {
            if (!defer) return PourVolume((i + 0.5f) * Cell, (j + 0.5f) * Cell, dv * Cell * Cell, material, colour);
            if (defI1 < defI0) { defMaterial = material; defColour = colour; }
            if (i < defI0) defI0 = i; if (i > defI1) defI1 = i;
            if (j < defJ0) defJ0 = j; if (j > defJ1) defJ1 = j;
            return Deposit(i, j, dv, material, colour, false);
        }

        // ------------------------------------------------- a pile grown in place, settled at the end
        //
        // A brush held in one place makes a pile grow, and every column on its slope has to be told so
        // every frame: sand added at the top moves every column down the slope out by a little, and a
        // grain at a time that is the whole slope's worth of rolling each frame. So the columns that
        // cannot take their share where they stand are given it there, as tall as that makes them, and
        // the pile is let down to its angle of repose once, when the brush is let go (SettleDeferred).

        private int defI0 = int.MaxValue, defJ0 = int.MaxValue, defI1 = int.MinValue, defJ1 = int.MinValue;
        private int defMaterial;
        private uint defColour;
        private readonly List<Chunk> touched = new List<Chunk>();
        private float[] setTop = new float[0], setFloor = new float[0], setEase = new float[0], setDrop = new float[0], setBase = new float[0];

        /// <summary>Whether anything has been left to settle.</summary>
        public bool HasDeferred { get { return defI1 >= defI0; } }

        /// <summary>The medium of what has been left to settle.</summary>
        public int DeferredMaterial { get { return defMaterial; } }

        /// <summary>The columns that have been left to settle, as a box of column indices.</summary>
        public void DeferredBounds(out int i0, out int j0, out int i1, out int j1)
        {
            i0 = defI0; j0 = defJ0; i1 = defI1; j1 = defJ1;
        }

        /// <summary>Forgets what was left to settle, without settling it.</summary>
        public void ForgetDeferred()
        {
            defI0 = defJ0 = int.MaxValue; defI1 = defJ1 = int.MinValue;
        }

        private float SurfaceAt(int i, int j, out float floor)
        {
            Chunk c = FindChunk(i >> ChunkShift, j >> ChunkShift);
            if (c != null)
            {
                int k = Slot(i, j);
                if (c.top[k] - c.bottom[k] > MinThickness) { floor = c.bottom[k]; return c.top[k]; }
            }
            float g = ground.Height((i + 0.5f) * Cell, (j + 0.5f) * Cell);
            floor = g;
            return g;
        }

        /// <summary>
        /// How many columns beyond the box a settled pile may reach, from how high the sand in it stands
        /// over what is round it: a slope this steep falls that far before it is at rest.
        /// </summary>
        public int SettleMargin()
        {
            if (!HasDeferred) return 0;
            float limit = slope[Mathf.Clamp(defMaterial, 0, slope.Length - 1)] * Cell;
            float lo = float.MaxValue, hi = float.MinValue;
            for (int j = defJ0 - 1; j <= defJ1 + 1; j++)
                for (int i = defI0 - 1; i <= defI1 + 1; i++)
                {
                    float f; float t = SurfaceAt(i, j, out f);
                    if (t <= NoGround) continue;
                    if (f < lo) lo = f;
                    if (t > hi) hi = t;
                }
            if (hi < lo) return 8;
            return Mathf.Clamp(Mathf.CeilToInt((hi - lo) / Mathf.Max(limit, 0.01f)) + 4, 8, 300);
        }

        /// <summary>
        /// Lets the sand left to settle down to its angle of repose, keeping every bit of it. What stands
        /// steeper than the medium rests at is first cut down to the steepest surface that fits under it
        /// (a cone from every low point round the pile), and what is cut off is laid back round the
        /// pile as a skirt that falls away from it at the angle -- as high as that much sand makes it.
        /// Returns the number of columns changed.
        /// </summary>
        public int SettleDeferred(int margin)
        {
            if (!HasDeferred || ground == null) { ForgetDeferred(); return 0; }
            float limit = slope[Mathf.Clamp(defMaterial, 0, slope.Length - 1)] * Cell;
            int material = defMaterial;
            uint colour = defColour;
            int i0 = defI0 - margin, j0 = defJ0 - margin, w = (defI1 - defI0 + 1) + 2 * margin, h = (defJ1 - defJ0 + 1) + 2 * margin;
            ForgetDeferred();
            long cells = (long)w * h;
            if (cells > 4000000L || limit <= 1e-4f) return 0;
            int n = (int)cells;
            if (setTop.Length < n) { setTop = new float[n]; setFloor = new float[n]; setEase = new float[n]; setDrop = new float[n]; setBase = new float[n]; }
            float[] top = setTop, floor = setFloor, ease = setEase, drop = setDrop;
            const float Blocked = 1e9f;
            float diag = limit * 1.4142f;

            // What is there: the sand's top, or the ground; and what is under it. Where the ground is not known
            // nothing is done.
            // The chunk is looked up when the row crosses into another, not for every column.
            for (int j = 0; j < h; j++)
            {
                int gj = j0 + j, cz = gj >> ChunkShift;
                Chunk sc = null;
                int scx = int.MinValue;
                for (int i = 0; i < w; i++)
                {
                    int k = i + j * w, gi = i0 + i, cx = gi >> ChunkShift;
                    if (cx != scx) { sc = ChunkAt(cx, cz); scx = cx; }
                    int slot = (gi & ChunkMask) + ((gj & ChunkMask) << ChunkShift);
                    float t, f;
                    if (sc != null && sc.top[slot] - sc.bottom[slot] > MinThickness)
                    {
                        // Only the medium being settled moves. Another medium is fixed ground to it, and so is
                        // what lies under its own surface layer: settled as dust's flat angle, a sand pile
                        // beside a stroke of dust was cut down and laid back round it as dust.
                        t = sc.top[slot];
                        if (sc.material[slot] != material) f = t;
                        else f = Layered(sc, slot) ? sc.split[slot] : sc.bottom[slot];
                    }
                    else { t = ground.Height((gi + 0.5f) * Cell, (gj + 0.5f) * Cell); f = t; }
                    if (t <= NoGround) { top[k] = Blocked; floor[k] = Blocked; }
                    else { top[k] = t; floor[k] = f; }
                    ease[k] = top[k];
                }
            }

            // Cut: the highest surface no steeper than the angle that lies under the sand, and no lower than
            // the ground. Two sweeps do it: every column is held down by the lowest of its neighbours
            // plus a step.
            for (int j = 0; j < h; j++)
                for (int i = 0; i < w; i++) Hold(ease, floor, w, h, i, j, limit, diag, false);
            for (int j = h - 1; j >= 0; j--)
                for (int i = w - 1; i >= 0; i--) Hold(ease, floor, w, h, i, j, limit, diag, true);

            double cut = 0.0;
            for (int k = 0; k < n; k++)
            {
                if (top[k] >= Blocked) continue;
                float d = top[k] - ease[k];
                if (d > MinThickness) { cut += d; drop[k] = 0f; } else drop[k] = float.MaxValue;
            }
            if (cut <= 0.0) return 0;

            // Fall: how far down a skirt from the places that were cut has dropped by each column.
            for (int j = 0; j < h; j++)
                for (int i = 0; i < w; i++) Fall(drop, top, w, h, i, j, limit, diag, false);
            for (int j = h - 1; j >= 0; j--)
                for (int i = w - 1; i >= 0; i--) Fall(drop, top, w, h, i, j, limit, diag, true);

            // The level the skirt starts at, so that it holds what was cut off. What a level holds is the sum of
            // level - drop - ease over the columns it stands above, which grows piecewise linearly and ever more
            // steeply with the level; Newton's method from above lands on it in a few passes and never overshoots,
            // and a column the level has fallen below is out of the reckoning for good. (Forty halvings over the
            // whole box were most of the cost of a settle.)
            float[] under = setBase;
            int live = 0;
            float level = float.MinValue;
            for (int k = 0; k < n; k++)
            {
                if (top[k] >= Blocked) continue;
                if (top[k] > level) level = top[k];
                if (drop[k] < float.MaxValue * 0.5f) under[live++] = ease[k] + drop[k];
            }
            for (int it = 0; it < 64 && live > 0; it++)
            {
                double vol = 0.0;
                int kept = 0;
                for (int q = 0; q < live; q++)
                {
                    float f = level - under[q];
                    if (f > 0f) { vol += f; under[kept++] = under[q]; }
                }
                live = kept;
                double over = vol - cut;
                if (live == 0 || over <= cut * 1e-7) break;
                level -= (float)(over / live);
            }

            // Write it: each column to the greater of its cut surface and the skirt. What is actually taken and
            // given is counted -- bare ground takes no skirt thinner than a layer, and a column cut to under one is
            // emptied -- and the difference is laid back on the places that were cut, so no sand is made or lost.
            // Settled every frame of a stroke, those crumbs were two per cent of a big pile.
            int changed = 0, sources = 0;
            double balance = 0.0;
            bool quiet = quietFill;
            quietFill = true;
            touched.Clear();
            for (int j = 0; j < h; j++)
            {
                int gj = j0 + j, cz = gj >> ChunkShift, lj = gj & ChunkMask;
                Chunk wc = null;
                int wcx = int.MinValue;
                for (int i = 0; i < w; i++)
                {
                    int k = i + j * w;
                    if (top[k] >= Blocked) continue;
                    if (drop[k] == 0f) sources++;
                    float target = ease[k];
                    if (drop[k] < float.MaxValue * 0.5f) target = Mathf.Max(target, level - drop[k]);
                    float delta = target - top[k];
                    float least = top[k] - floor[k] > MinThickness ? 1e-6f : MinThickness;
                    if (delta > least)
                    {
                        // Most of a settle is the skirt thickening a slope of the same medium a little: written
                        // straight into the chunk, which is marked once for the lot (as FastAdd does).
                        int gi = i0 + i, cx = gi >> ChunkShift, li = gi & ChunkMask;
                        if (cx != wcx) { wc = ChunkAt(cx, cz); wcx = cx; }
                        int slot = li + (lj << ChunkShift);
                        if (wc != null && wc.top[slot] - wc.bottom[slot] > MinThickness && wc.material[slot] == material)
                        {
                            float own = wc.top[slot] - (Layered(wc, slot) ? wc.split[slot] : wc.bottom[slot]);
                            wc.top[slot] += delta;
                            if (wc.colour[slot] != colour) wc.colour[slot] = Blend(wc.colour[slot], colour, delta / (own + delta));
                            if (wc.settleFlags == 0) { touched.Add(wc); wc.settleTop = float.MinValue; }
                            int flags = 1;
                            if (li <= 1) flags |= 2;
                            if (lj <= 1) flags |= 4;
                            if (li == ChunkMask) flags |= 8;
                            if (lj == ChunkMask) flags |= 16;
                            if (li == 0) flags |= 32;
                            if (lj == 0) flags |= 64;
                            wc.settleFlags |= flags;
                            if (wc.top[slot] > wc.settleTop) wc.settleTop = wc.top[slot];
                            changed++; balance -= delta;
                        }
                        else if (Deposit(gi, gj, delta, material, colour, false)) { changed++; balance -= delta; }
                    }
                    else if (delta < -least)
                    {
                        Chunk c = FindChunk((i0 + i) >> ChunkShift, (j0 + j) >> ChunkShift);
                        if (c == null) continue;
                        int slot = Slot(i0 + i, j0 + j);
                        float room = c.top[slot] - c.bottom[slot];
                        if (room <= MinThickness) continue;
                        Withdraw(c, i0 + i, j0 + j, Mathf.Min(-delta, room));
                        balance += room - (c.top[slot] - c.bottom[slot]);
                        changed++;
                    }
                }
            }
            for (int t = 0; t < touched.Count; t++)
            {
                Chunk tc = touched[t];
                FlushBatch(tc, tc.settleFlags, tc.settleTop);
                tc.settleFlags = 0;
            }
            if (balance > 1e-6 && sources > 0)
            {
                float each = (float)(balance / sources);
                for (int j = 0; j < h; j++)
                    for (int i = 0; i < w; i++)
                    {
                        int k = i + j * w;
                        if (top[k] < Blocked && drop[k] == 0f) Deposit(i0 + i, j0 + j, each, material, colour, false);
                    }
            }
            quietFill = quiet;
            return changed;
        }

        // One column held down by the lowest neighbour plus a step (the cut), never below its floor.
        private static void Hold(float[] a, float[] floor, int w, int h, int i, int j, float step, float diag, bool back)
        {
            int k = i + j * w;
            float v = a[k];
            if (v >= 1e8f) return;
            int s = back ? 1 : -1;
            int ni = i + s, nj = j + s;
            if (ni >= 0 && ni < w) { float c = a[k + s] + step; if (a[k + s] < 1e8f && c < v) v = c; }
            if (nj >= 0 && nj < h)
            {
                int r = k + s * w;
                if (a[r] < 1e8f) { float c = a[r] + step; if (c < v) v = c; }
                if (ni >= 0 && ni < w && a[r + s] < 1e8f) { float c = a[r + s] + diag; if (c < v) v = c; }
                int oi = i - s;
                if (oi >= 0 && oi < w && a[r - s] < 1e8f) { float c = a[r - s] + diag; if (c < v) v = c; }
            }
            if (v < floor[k]) v = floor[k];
            a[k] = v;
        }

        // The distance, as a drop in height, from the nearest place that was cut.
        private static void Fall(float[] d, float[] top, int w, int h, int i, int j, float step, float diag, bool back)
        {
            int k = i + j * w;
            if (top[k] >= 1e8f) return;
            float v = d[k];
            int s = back ? 1 : -1;
            int ni = i + s, nj = j + s;
            if (ni >= 0 && ni < w) { float c = d[k + s] + step; if (c < v) v = c; }
            if (nj >= 0 && nj < h)
            {
                int r = k + s * w;
                { float c = d[r] + step; if (c < v) v = c; }
                if (ni >= 0 && ni < w) { float c = d[r + s] + diag; if (c < v) v = c; }
                int oi = i - s;
                if (oi >= 0 && oi < w) { float c = d[r - s] + diag; if (c < v) v = c; }
            }
            d[k] = v;
        }

        /// <summary>
        /// Evens out the speckle a big brush leaves (see <see cref="BrushSweep"/>): every pair of
        /// neighbouring columns of the same medium in the box passes a quarter of their difference
        /// between them, so nothing is made or lost.
        /// </summary>
        public void SmoothBox(int i0, int j0, int i1, int j1)
        {
            for (int j = j0; j <= j1; j++)
                for (int i = i0; i <= i1; i++)
                {
                    Chunk a = FindChunk(i >> ChunkShift, j >> ChunkShift);
                    if (a == null) continue;
                    int ka = Slot(i, j);
                    if (a.top[ka] - a.bottom[ka] <= MinThickness || Layered(a, ka)) continue;
                    bool moved = false;
                    for (int d = 0; d < 2; d++)
                    {
                        int ni = d == 0 ? i + 1 : i, nj = d == 0 ? j : j + 1;
                        Chunk b = FindChunk(ni >> ChunkShift, nj >> ChunkShift);
                        if (b == null) continue;
                        int kb = Slot(ni, nj);
                        if (b.top[kb] - b.bottom[kb] <= MinThickness || Layered(b, kb) || b.material[kb] != a.material[ka]) continue;
                        float f = (b.top[kb] - a.top[ka]) * 0.25f;
                        if (f > -1e-5f && f < 1e-5f) continue;
                        // Neither column may be taken below its floor.
                        if (a.top[ka] + f - a.bottom[ka] <= MinThickness || b.top[kb] - f - b.bottom[kb] <= MinThickness) continue;
                        a.top[ka] += f; b.top[kb] -= f;
                        MarkDirty(b, ni, nj);
                        moved = true;
                    }
                    if (moved) MarkDirty(a, i, j);
                }
        }

        private bool PourSerial(int[] ci, int[] cj, float[] thickness, int n, int material, uint colour, float limit, bool defer)
        {
            float near = limit, far = limit * 1.4142f;
            bool ok = true;
            Chunk cc = null;
            int ccx = int.MinValue, ccz = int.MinValue;
            int flags = 0;
            float highest = 0f;
            Chunk[] around = arounds.Count > 0 ? arounds[0] : new Chunk[9];
            if (arounds.Count == 0) arounds.Add(around);
            for (int q = 0; q < n; q++)
            {
                int i = ci[q], j = cj[q];
                int cx = i >> ChunkShift, cz = j >> ChunkShift;
                if (cx != ccx || cz != ccz)
                {
                    if ((flags & 1) != 0) FlushBatch(cc, flags, highest);
                    flags = 0; highest = 0f;
                    Around(cx, cz, around);
                    cc = around[4];
                    ccx = cx; ccz = cz;
                }
                if (cc != null && FastAdd(cc, around, i & ChunkMask, j & ChunkMask, thickness[q], material, colour, near, far, ref highest, ref flags)) continue;
                if (!SlowAdd(i, j, thickness[q], material, colour, defer)) ok = false;
            }
            if ((flags & 1) != 0) FlushBatch(cc, flags, highest);
            return ok;
        }

        private void FlushBatch(Chunk c, int flags, float highest)
        {
            if (c == null) return;
            Queue(c);
            MarkNet(c);
            c.colliderDirty = true;
            bool west = (flags & 2) != 0, south = (flags & 4) != 0, east = (flags & 8) != 0, north = (flags & 16) != 0;
            if (west) Queue(FindChunk(c.cx - 1, c.cz));
            if (south) Queue(FindChunk(c.cx, c.cz - 1));
            if (east) Queue(FindChunk(c.cx + 1, c.cz));
            if (north) Queue(FindChunk(c.cx, c.cz + 1));
            if (west && south) Queue(FindChunk(c.cx - 1, c.cz - 1));
            if (east && north) Queue(FindChunk(c.cx + 1, c.cz + 1));
            if (west && north) Queue(FindChunk(c.cx - 1, c.cz + 1));
            if (east && south) Queue(FindChunk(c.cx + 1, c.cz - 1));
            // A column at a chunk's own edge is a vertex of its collider mesh and of the neighbour's.
            if ((flags & 32) != 0) { Chunk n = FindChunk(c.cx - 1, c.cz); if (n != null) n.colliderDirty = true; }
            if ((flags & 64) != 0) { Chunk n = FindChunk(c.cx, c.cz - 1); if (n != null) n.colliderDirty = true; }
            if ((flags & 96) == 96) { Chunk n = FindChunk(c.cx - 1, c.cz - 1); if (n != null) n.colliderDirty = true; }
            Grow((c.cx * ChunkSide + 0.5f) * Cell, highest, (c.cz * ChunkSide + 0.5f) * Cell);
            Grow((c.cx * ChunkSide + ChunkSide - 0.5f) * Cell, highest, (c.cz * ChunkSide + ChunkSide - 0.5f) * Cell);
        }

        /// <summary>
        /// Whether <paramref name="dv"/> added to a column leaves it standing against all eight
        /// neighbours, read straight from the chunk's arrays: for a column with sand all round it in
        /// the same chunk, which is nearly every column of a brush. False where that cannot be said
        /// quickly (an edge, bare ground, a chunk's border, a step too steep) and the caller looks properly.
        /// </summary>
        private bool Stable(int i, int j, float dv, float limit)
        {
            int li = i & ChunkMask, lj = j & ChunkMask;
            if (li == 0 || lj == 0 || li == ChunkMask || lj == ChunkMask) return false;
            Chunk c = Find(i, j);
            if (c == null) return false;
            int k = li + (lj << ChunkShift);
            if (c.top[k] - c.bottom[k] <= MinThickness) return false;
            float lift = c.top[k] + dv, near = limit, far = limit * 1.4142f;
            for (int dj = -1; dj <= 1; dj++)
                for (int di = -1; di <= 1; di++)
                {
                    if (di == 0 && dj == 0) continue;
                    int nk = k + di + (dj << ChunkShift);
                    if (c.top[nk] - c.bottom[nk] <= MinThickness) return false;
                    if (lift - c.top[nk] > ((di != 0 && dj != 0) ? far : near)) return false;
                }
            return true;
        }

        /// <summary>A medium's slope at rest, height per metre across.</summary>
        public float Repose(int material)
        {
            return slope[Mathf.Clamp(material, 0, slope.Length - 1)];
        }

        /// <summary>How deep the sand is at a point, 0 where there is none.</summary>
        public float DepthAt(float x, float z)
        {
            int i = Mathf.FloorToInt(x / Cell), j = Mathf.FloorToInt(z / Cell);
            Chunk c = Find(i, j);
            if (c == null) return 0f;
            int k = Slot(i, j);
            float d = c.top[k] - c.bottom[k];
            return d > MinThickness ? d : 0f;
        }

        /// <summary>
        /// Adds sand the way it falls: onto a column if the column can hold it at the medium's
        /// angle of repose, else down the steepest way to one that can. A pile made like this is
        /// at its angle as it grows -- a cone, or a flat-topped cone with the brush's edge as its
        /// foot -- and nothing is left to <see cref="Relax"/>. Added with <see cref="AddVolume"/>,
        /// a thick layer stands on a cliff and the slumping that follows moves a column's worth at
        /// a time: a flat brush held for three seconds took a minute of it to finish.
        ///
        /// A big volume is poured as at most eight pieces, each rolled on its own; a piece too
        /// thick for the angle (more than a column can step) is left where it lands, awake.
        /// </summary>
        public bool PourVolume(float x, float z, float volume, int material, uint colour)
        {
            if (volume <= 0f) return true;
            int i = Mathf.FloorToInt(x / Cell), j = Mathf.FloorToInt(z / Cell);
            float thickness = volume / (Cell * Cell);
            float limit = slope[Mathf.Clamp(material, 0, slope.Length - 1)] * Cell;
            float piece = Mathf.Max(limit * 0.5f, thickness * 0.125f);
            bool ok = true;
            while (thickness > 0f)
            {
                float dv = thickness < piece * 1.5f ? thickness : piece;
                thickness -= dv;
                if (Stable(i, j, dv, limit))
                {
                    if (!Deposit(i, j, dv, material, colour, false)) ok = false;
                    continue;
                }
                int ci = i, cj = j;
                float top = RollTop(ci, cj);
                for (int step = 0; step < MostRoll && top > NoGround; step++)
                {
                    rollTurn = (rollTurn + 3) & 7;
                    float lowest = top, reachDv = top + dv;
                    int bi = ci, bj = cj;
                    for (int n = 0; n < 8; n++)
                    {
                        int d = (rollTurn + n) & 7;
                        int ni = ci + RollDi[d], nj = cj + RollDj[d];
                        float nt = RollTop(ni, nj);
                        if (nt <= NoGround || nt >= lowest) continue;
                        if (reachDv - nt > ((d & 1) != 0 ? limit * 1.4142f : limit)) { lowest = nt; bi = ni; bj = nj; }
                    }
                    if (bi == ci && bj == cj) break;
                    ci = bi; cj = bj; top = lowest;
                }
                if (!Deposit(ci, cj, dv, material, colour, dv > limit)) ok = false;
            }
            return ok;
        }

        /// <summary>Adds a thickness to a column. The column takes the incoming colour
        /// in proportion to how much of it the incoming sand now is.</summary>
        private bool Deposit(int i, int j, float thickness, int material, uint colour)
        {
            return Deposit(i, j, thickness, material, colour, true);
        }

        private bool Deposit(int i, int j, float thickness, int material, uint colour, bool wake)
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
                ClearDeep(c, k);
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
                // the other (wet sand landing on a dry pile became dry sand once, and the other way
                // round). The layers below move down: a column holds up to MaxLayers, one on another,
                // and only a seventh medium takes the lowest into the one above it.
                if (Layered(c, k))
                {
                    List<Deep> l;
                    if (c.deep == null) c.deep = new Dictionary<int, List<Deep>>();
                    if (!c.deep.TryGetValue(k, out l)) { l = new List<Deep>(); c.deep[k] = l; }
                    Deep d; d.material = c.lowMaterial[k]; d.colour = c.lowColour[k]; d.upper = c.split[k];
                    l.Add(d);
                    if (l.Count > MaxLayers - 2) l.RemoveAt(0);
                }
                c.lowMaterial[k] = c.material[k];
                c.lowColour[k] = c.colour[k];
                c.split[k] = c.top[k];
                c.top[k] += thickness;
                c.material[k] = (byte)material;
                c.colour[k] = colour;
            }

            Grow((i + 0.5f) * Cell, c.top[k], (j + 0.5f) * Cell);
            MarkDirty(c, i, j);
            if (wake) Activate(c, i, j);
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
                ClearDeep(c, k);
                c.filled--;
                filledColumns--;
            }
            else Settle(c, k);
            MarkDirty(c, i, j);
            Activate(c, i, j);
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

        /// <summary>
        /// Fills a box, a standing cylinder or an ellipsoid with sand, straight into the
        /// columns, resting on the ground: each column takes the length of the shape along its
        /// vertical line, so the sand holds the shape's volume. A column over unknown ground is
        /// skipped. Returns how many columns received sand. (The headless tests lay their sand
        /// down with this.)
        /// </summary>
        public int FillShape(int kind, Vector3 centre, Quaternion rotation, Vector3 half,
                             int material, uint colour)
        {
            if (!Enabled || ground == null) return 0;

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

            int filled = 0;
            quietFill = true;
            for (int j = j0; j <= j1; j++)
            {
                for (int i = i0; i <= i1; i++)
                {
                    // The average length over a few points across the column, not the length
                    // at its centre: a column the shape only partly covers, which is every
                    // column round its edge, would otherwise count as all or nothing and a
                    // small shape would lose a tenth of its sand.
                    //
                    // A column wholly inside the shape's footprint -- most of them, for a big
                    // shape -- is a smooth function across the column, and three by three
                    // averages it closely. Only where one of those nine misses the shape is it
                    // an edge column, worth the full grid.
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
            // lower than the slope allows. Queuing every column had slumping work through the
            // flat interior first while the walls stood vertical for seconds.
            for (int j = j0 - 1; j <= j1 + 1; j++)
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
                if (g.life < minLife) continue;
                int mat = (int)(flags & SandFormat.MaterialMask);
                if (mat >= grainRadius.Length) continue;
                float r = grainRadius[mat];

                // A grain that has been loose and slow a long while is stranded -- lying on a machine,
                // wedged by a wheel, never quite still -- and goes into the column under it wherever it
                // is. Waiting for it to be still, clear of every shape and down on the sand, those were
                // never taken in: over a long run they piled up to the field's ceiling, the solver
                // carried them every step, and nothing new could be sprayed or thrown.
                bool stranded = g.age > StrandedSeconds && g.vel.sqrMagnitude < StrandedSpeed * StrandedSpeed;
                // Landed: down on the sand or the ground and slowed to a walk, a grain becomes sand where it is rather than
                // rolling and sliding there as a grain until it is still. The slope it lands on is the bed's to settle,
                // at its own angle, as everything else the bed holds; what is given up is the last half second of a grain
                // tumbling down a slope, which the bed's slumping does instead. Not beside a machine, which may dig it up
                // again at once, and not while it is still fast.
                bool landed = !stranded && LandOnContact && g.age > LandAge && g.vel.sqrMagnitude < LandSpeed * LandSpeed;
                if (landed)
                {
                    if (shapeCount > 0 && NearShape(shapes, shapeCount, umin, umax, g.pos, margin)) landed = false;
                }
                if (!stranded && !landed)
                {
                    // Away from every machine a grain is taken in after half the wait: nothing there
                    // will disturb it, and a grain costs a solver slot for as long as it is one.
                    int rest = SandFormat.RestSteps(flags);
                    if (rest < restSteps)
                    {
                        if (rest < restSteps / 2) continue;
                        if (shapeCount > 0 && Near(umin, umax, g.pos, FarFromMachines)) continue;
                    }
                    if (shapeCount > 0 && NearShape(shapes, shapeCount, umin, umax, g.pos, margin))
                        continue;
                }

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
                bool high = g.pos.y - r > surface + r * 1.5f;
                if (high && !stranded) continue;
                // A stranded grain goes into the column under it -- but not through a block: one lying on a
                // build surface, or on the sand heaped on one, stays where it is.
                if (high && shapeCount > 0 && ShapeBelow(shapes, shapeCount, umin, umax, g.pos, surface)) continue;

                float volume = grainVolume[mat];
                uint colour = g.colour;
                if ((flags & SandFormat.FlagWet) != 0) colour = Darken(colour, 0.65f);

                if (!AddVolumeSpread(g.pos.x, g.pos.z, volume, mat, colour)) continue;

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

        /// <summary>Seconds a grain may be loose, and the speed under which it counts as not going anywhere, before
        /// it is taken into the column under it wherever it lies (see <see cref="Absorb"/>).</summary>
        public const float StrandedSeconds = 8f, StrandedSpeed = 1f;

        /// <summary>Whether a grain that has come down on the sand or the ground slowly becomes sand at once (see
        /// <see cref="Absorb"/>), rather than after lying still for the rest steps. On by default: fewer grains for
        /// the solver to carry, for a little less rolling.</summary>
        public static bool LandOnContact = true;
        /// <summary>How slow, in m/s, and how old, in s, a grain on the surface must be to become sand at once.</summary>
        public const float LandSpeed = 1.2f, LandAge = 0.3f;

        /// <summary>How far from every machine a grain must be to settle on the shorter wait.</summary>
        private const float FarFromMachines = 3f;

        private static bool Near(Vector3 umin, Vector3 umax, Vector3 p, float margin)
        {
            return p.x > umin.x - margin && p.x < umax.x + margin
                && p.y > umin.y - margin && p.y < umax.y + margin
                && p.z > umin.z - margin && p.z < umax.z + margin;
        }

        /// <summary>Whether a shape's box is between a point and the surface under it.</summary>
        private static bool ShapeBelow(IBedShapes shapes, int count, Vector3 umin, Vector3 umax, Vector3 p, float surface)
        {
            if (p.x < umin.x || p.x > umax.x || p.z < umin.z || p.z > umax.z || umin.y > p.y || umax.y < surface) return false;
            for (int s = 0; s < count; s++)
            {
                Vector3 lo, hi;
                shapes.Extent(s, out lo, out hi);
                if (p.x < lo.x || p.x > hi.x || p.z < lo.z || p.z > hi.z) continue;
                if (lo.y < p.y && hi.y > surface) return true;
            }
            return false;
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

        /// <summary>The same, giving back at most <paramref name="maxGrains"/> grains; what a
        /// shape reaches into beyond that is given back on later calls.</summary>
        public int Release(IBedShapes shapes, IBedSink sink, float margin, int budget, int maxGrains)
        {
            return ReleaseCore(shapes, sink, margin, budget, maxGrains);
        }

        /// <summary>Whether the column under a point holds sand above <paramref name="height"/>: a shape there may be in it.</summary>
        private bool SandAbove(float x, float z, float height)
        {
            int i = Mathf.FloorToInt(x / Cell), j = Mathf.FloorToInt(z / Cell);
            Chunk c = Find(i, j);
            if (c == null) return false;
            int k = Slot(i, j);
            return c.top[k] - c.bottom[k] > MinThickness && c.top[k] > height;
        }

        /// <summary>Surface speed, m/s, under which a shape counts as still for digging.</summary>
        private const float StillSpeed = 0.05f;
        private int releaseCalls;

        /// <summary>The highest sand in a chunk, measured once a call to Release; below everything if it has none.</summary>
        private float DigTop(int cx, int cz)
        {
            Chunk c = FindChunk(cx, cz);
            if (c == null || c.filled <= 0) return float.MinValue;
            if (c.digStamp == releaseCalls) return c.digTop;
            float most = float.MinValue;
            for (int k = 0; k < c.top.Length; k++)
                if (c.top[k] - c.bottom[k] > MinThickness && c.top[k] > most) most = c.top[k];
            c.digStamp = releaseCalls;
            c.digTop = most;
            return most;
        }

        private int ReleaseCore(IBedShapes shapes, IBedSink sink, float margin, int budget, int maxGrains)
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

            // From where the last call's budget ran out, round: with hundreds of shapes over
            // the sand the budget is spent before the end of the list, and starting at the top
            // every step left the same shapes at the end never looked at -- a plough's blade
            // among them, which then neither dug nor ploughed.
            int count = shapes.Count;
            int first = count > 0 ? releaseFrom % count : 0;
            int done = 0;
            releaseCalls++;
            for (int n = 0; n < count && tests < budget; n++, done++)
            {
                int s = (first + n) % count;
                Vector3 lo, hi;
                shapes.Extent(s, out lo, out hi);
                // How fast the shape's underside slides over the ground: a wheel rolling has
                // none however fast the machine goes, a wheel spinning in place or a block
                // dragged along has all of it.
                float slip = 0f, moving = 0f;
                Vector3 middle = (lo + hi) * 0.5f;
                float underside = lo.y;
                if (motion != null)
                {
                    Vector3 under = motion.SurfaceVelocity(s, new Vector3(middle.x, lo.y, middle.z));
                    slip = Mathf.Sqrt(under.x * under.x + under.z * under.z);
                    moving = motion.SurfaceVelocity(s, middle).magnitude;
                }
                // A shape that is all but still -- a block standing on the sand, a wheel parked -- digs nothing
                // unless it is buried, which a fourth of the steps finds out soon enough: on a big machine most
                // shapes over the sand are still relative to it, and each was searched every step.
                if (motion != null && moving < StillSpeed && slip < StillSpeed && ((s + releaseCalls) & 3) != 0
                    && !SandAbove(middle.x, middle.z, underside + HeldTouch)) continue;
                int picked = 0;
                lo -= new Vector3(margin, margin, margin);
                hi += new Vector3(margin, margin, margin);
                // Down to HeldTouch for something moving: it may be standing on a collider over sand that has
                // gone down under it (see touching, below).
                float reachY = lo.y;
                if (motion != null) lo.y -= HeldTouch;

                int i0 = Mathf.FloorToInt(lo.x / Cell), i1 = Mathf.FloorToInt(hi.x / Cell);
                int j0 = Mathf.FloorToInt(lo.z / Cell), j1 = Mathf.FloorToInt(hi.z / Cell);

                int ci0 = FloorDiv(i0, span), ci1 = FloorDiv(i1, span);
                int cj0 = FloorDiv(j0, span), cj1 = FloorDiv(j1, span);

                // Nothing to do if the shape's underside is above every grain of sand under it: the chassis, the
                // arms, whatever rides over the sand. Asked of the chunks first, a number each, not of the columns.
                {
                    float highest = float.MinValue;
                    for (int cj = cj0; cj <= cj1; cj++)
                        for (int ci = ci0; ci <= ci1; ci++)
                        {
                            float t = DigTop(ci, cj);
                            if (t > highest) highest = t;
                        }
                    if (highest < lo.y) continue;
                }

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
                                // From the shape's own bottom, not the sand's: nothing lower comes
                                // within the margin, and a deep pile was dozens of tests a column.
                                float from = Mathf.Max(bottom, reachY - sampleStep);
                                for (float y = from + sampleStep * 0.5f; y <= top + sampleStep * 0.5f; y += sampleStep)
                                {
                                    tests++;
                                    float yy = Mathf.Min(y, top);
                                    float d = shapes.Distance(s, new Vector3(cx, yy, cz));
                                    if (d < nearest) { nearest = d; nearestY = yy; }
                                    if (lowest == float.MaxValue && d < margin) lowest = y;
                                    if (d < 0f) { inside = yy; break; }
                                    if (motion == null && lowest != float.MaxValue) break;
                                }
                                if (lowest == float.MaxValue)
                                {
                                    if (motion == null || !Held(i, j)) continue;
                                    if (nearest == float.MaxValue)
                                    {
                                        // Wholly below where the shape reaches: how far over it the shape stands.
                                        tests++;
                                        nearest = shapes.Distance(s, new Vector3(cx, top, cz));
                                        nearestY = top;
                                    }
                                    if (nearest >= HeldTouch) continue;
                                    lowest = top;
                                }
                                bool bySlip = false;

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
                                    // Or standing on it through the collider: a column something has pressed on in
                                    // the last few presses is under it whatever the heights say. The collider is cooked
                                    // a few steps behind the sand, so a column pressed or dug down under a wheel left
                                    // the wheel standing on the old surface, out of touch, and it dug nothing more.
                                    bool touching = nearest < TouchDistance || (nearest < HeldTouch && Held(i, j));
                                    float dig = digSpeedOf[Mathf.Clamp(c.material[k], 0, digSpeedOf.Length - 1)];

                                    // Ploughing: a face moving sideways into the sand, at the side
                                    // of the shape rather than under it, pushes what is in front of
                                    // it ahead. The bed's collider is a surface, and to the physics a
                                    // pile's edge is a slope: a blade met it as a ramp, rode up and
                                    // over, and nothing could scrape sand off from the side. Now the
                                    // sand is moved before the blade reaches it, into a berm that the
                                    // blade goes on pushing. A rolling wheel's front moves down as
                                    // fast as forward, so a wheel still climbs; a locked one ploughs.
                                    {
                                        Vector3 pv = motion.SurfaceVelocity(s, new Vector3(cx, nearestY, cz));
                                        float ph = Mathf.Sqrt(pv.x * pv.x + pv.z * pv.z);
                                        // Where the face here meets the sand going forward -- its own surface facing the way it
                                        // goes -- not merely ahead of the shape's middle: half the face of a blade set at an
                                        // angle is behind its middle, and there the sand was dug as though under a slipping
                                        // wheel instead of pushed aside.
                                        Vector2 away;
                                        if (ph > Mathf.Max(dig, PlowSpeed) && Mathf.Abs(pv.y) < ph * 0.5f
                                            && top > underside + PlowLift && !Held(i, j)
                                            && PlowAway(shapes, s, new Vector3(cx, nearestY, cz), pv.x / ph, pv.z / ph, out away))
                                        {
                                            Displace(c, i, j, Mathf.Max(bottom, underside), away, shapes, s, margin, 3f);
                                            continue;
                                        }
                                    }
                                    bool digging = false;
                                    if (touching)
                                    {
                                        if (slip > dig) { digging = true; bySlip = true; }
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
                                        // The stickier, the more and the more readily: glue comes
                                        // away on anything that so much as moves in it.
                                        float tack = adhesionOf[mat] / PickupAdhesion;
                                        if (nearest < TouchDistance && tack >= 1f && moving > PickupSpeed / Mathf.Min(tack, 10f)
                                            && picked < PickupPerShape * Mathf.Min(tack, 5f)
                                            && Rand() < Mathf.Min(1f, PickupChance * tack))
                                        {
                                            float skin = Mathf.Max(grainRadius[mat] * 2f, MinThickness * 2f) * Mathf.Min(tack, 3f);
                                            int got = Fluidize(c, i, j, Mathf.Max(bottom, top - skin), sink, Mathf.Min(maxGrains - released, 6 * (int)Mathf.Min(tack, 5f)), shapes, s);
                                            released += got;
                                            picked += got;
                                            if (sink.Room <= 0 || released >= maxGrains) { releaseFrom = (s + 1) % count; return released; }
                                        }
                                        continue;
                                    }
                                }

                                // A wheel slipping on the sand under it takes off a layer as thick as it slips
                                // far, not the column: cut from below where it reaches, a column went at once
                                // from under a wheel standing on the bed's collider, which then stood over the
                                // hole out of touch until the collider caught up -- one bite and nothing, and
                                // most of what it dug too far from it to be thrown. Where the column runs up
                                // into the shape, the shape is in the sand, and all of that goes, as before.
                                float cut = Mathf.Max(bottom, lowest - sampleStep);
                                if (bySlip && inside == float.MaxValue)
                                {
                                    Vector3 lv = motion.SurfaceVelocity(s, new Vector3(cx, nearestY, cz));
                                    float local = Mathf.Sqrt(lv.x * lv.x + lv.z * lv.z);
                                    int lm = Mathf.Clamp(c.material[k], 0, grainVolume.Length - 1);
                                    if (lm < SandMaterials.Count) lm = Mathf.Min(SandMaterials.LodRow(lm, KickLevel), grainVolume.Length - 1);
                                    float least = grainVolume[lm] / (Cell * Cell) * 1.05f;
                                    float skin = Mathf.Clamp(local * StepSeconds * DigBite * BiteFor(i, j), least, Mathf.Max(least, DigSkinMost * BiteFor(i, j)));
                                    cut = Mathf.Max(cut, top - skin);
                                    // And at least one grain, however close the shape: a gravel grain is a column
                                    // 28 cm deep, more than a wheel reaches into, and rounded to none, gravel was never dug.
                                    cut = Mathf.Max(bottom, Mathf.Min(cut, top - least));
                                }
                                // What a slipping surface digs, it picks up: the grains start at it, to be thrown.
                                pickUp = bySlip;
                                // What a slipping surface throws comes back as coarser grains (KickLevel): a third as
                                // many for the solver to carry, the same sand. The look of a spray of fine sand is the
                                // field's business: it adds grains that are only drawn (SandSpray).
                                if (bySlip) forceLevel = KickLevel;
                                released += Fluidize(c, i, j, cut, sink, maxGrains - released, shapes, s);
                                forceLevel = -1;
                                // A column a wheel is spinning in is not a rut it stands in: the sand round it
                                // runs in, to be dug and thrown in turn. Held, the hole's walls stood, the wheel
                                // was through to the ground in half a second and threw almost nothing after.
                                if (pickUp) Unhold(i, j);
                                pickUp = false;
                                if (sink.Room <= 0 || released >= maxGrains) { releaseFrom = (s + 1) % count; return released; }
                            }
                        }
                    }
                }
            }
            // On from where this call stopped -- its budget spent, or its grains -- so every shape has its turn. It
            // stayed where it was, and with a machine over the sand the same few wheels got the whole budget every
            // step: only those dug, threw, and came away with sand on them.
            releaseFrom = count > 0 ? (first + done) % count : 0;
            return released;
        }

        private int releaseFrom;

        // The shape doing the digging, while it digs: grains are not given back inside it.
        private IBedShapes clearOf;
        private int clearShape = -1;
        private IBedShapeMotion kickMotion;
        private bool pickUp;     // the layer being dug is a slipping surface's bite: its grains start at the surface

        private IBedKickSink kickSink;

        /// <summary>How near a shape's surface a grain given back has to be to leave with its speed, in metres.</summary>
        public const float KickReach = 0.15f;
        /// <summary>How far above a column something pressing on it through the collider may stand and still dig
        /// it, in metres. Not further: a machine's body over its wheels' ruts would dig them as it went.</summary>
        public const float HeldTouch = 0.25f;
        /// <summary>The share of the surface's speed a grain of a medium with no stickiness leaves with.</summary>
        public const float KickShare = 0.85f;
        /// <summary>The fastest a grain is thrown, in metres a second.</summary>
        public const float KickMost = 14f;
        /// <summary>How far round a turning shape carries a grain it digs before letting go, in degrees: each
        /// grain somewhere between the two.</summary>
        public const float KickExitLeast = 20f, KickExitMost = 70f;

        // A vector turned about a unit axis, the way the axis turns, by the angle with this cosine and sine
        // (Rodrigues). Not Quaternion.AngleAxis: that is a call into the engine, which the headless tests do not have.
        private static Vector3 Turn(Vector3 v, Vector3 k, float c, float s)
        {
            return v * c + Vector3.Cross(k, v) * s + k * (Vector3.Dot(k, v) * (1f - c));
        }
        /// <summary>The least a thrown grain climbs for each metre a second it goes sideways (tan of the angle).</summary>
        public const float KickLift = 0.8f;

        private int Fluidize(Chunk c, int i, int j, float cut, IBedSink sink, int most, IBedShapes shapes, int s)
        {
            clearOf = shapes; clearShape = s;
            kickMotion = shapes as IBedShapeMotion;
            kickSink = sink as IBedKickSink;
            int n = Fluidize(c, i, j, cut, sink, most);
            clearOf = null; clearShape = -1;
            kickMotion = null; kickSink = null;
            return n;
        }

        /// <summary>Turns a column's sand above <paramref name="cut"/> into grains.</summary>
        private int Fluidize(Chunk c, int i, int j, float cut, IBedSink sink, int most)
        {
            // A layer at a time from the top, each as the grains of its own medium.
            int k = Slot(i, j);
            int released = 0;
            for (int pass = 0; pass < MaxLayers && released < most; pass++)
            {
                if (c.top[k] - c.bottom[k] <= MinThickness || c.top[k] <= cut + MinThickness) break;
                bool layered = Layered(c, k);
                float layerCut = layered ? Mathf.Max(cut, c.split[k]) : cut;
                int n = FluidizeSurface(c, i, j, layerCut, sink, most - released);
                if (n <= 0)
                {
                    // A sliver of the surface layer, less than a grain, over more to dig: it joins the layer under it, and the
                    // digging goes on. It stopped the dig there, and what was under it was never dug.
                    if (layered && layerCut > cut + MinThickness) { c.split[k] = c.top[k]; Settle(c, k); continue; }
                    break;
                }
                released += n;
            }
            return released;
        }

        /// <summary>Turns the surface layer's sand above <paramref name="cut"/> into grains.</summary>
        /// <summary>How much sand above the cut makes it come back as grains one, two and three levels coarser.</summary>
        public const float CoarseDepth1 = 1.5f, CoarseDepth2 = 3f, CoarseDepth3 = 6f;

        /// <summary>How much coarser than the medium's own the grains a slipping surface throws are (see SandMaterials.LodRow):
        /// at least level 1, half again as wide, standing for three and a half ordinary grains -- four wheels throwing
        /// ordinary grains had the solver carrying thousands, and driving on sand lagged -- and coarser still while the
        /// solver is behind (the field sets it from the governor's grain size level): the same sand thrown as fewer
        /// grains, where before the digging threw as many as the sand limit allowed whatever the solver could carry.</summary>
        public int KickLevel = 1;

        // The grain size a blast asks for, whatever the depth (see Blast); -1 for the depth to say.
        private int forceLevel = -1;

        private int FluidizeSurface(Chunk c, int i, int j, float cut, IBedSink sink, int most)
        {
            int k = Slot(i, j);
            int material = c.material[k];
            uint colour = c.colour[k];
            float oldTop = c.top[k];

            // Deep sand is given back as coarse grains. A column of it is hundreds of grains, and a
            // machine at the foot of a pile ten metres high asked for thousands in a few steps -- the
            // solver's whole budget, for sand that is going to fall in a heap. A coarse grain is a
            // bigger sphere of the same medium standing for several ordinary ones (the governor does
            // the same to a sprayer when the solver is short of time).
            float deep = oldTop - cut;
            int level = deep > CoarseDepth3 ? 3 : deep > CoarseDepth2 ? 2 : deep > CoarseDepth1 ? 1 : 0;
            if (forceLevel >= 0)
            {
                // Asked for, but never so coarse that what there is to give back makes less than one grain: a coarse gravel
                // grain is more than a layer of gravel, and rounded to none it stopped the dig at that layer.
                level = forceLevel;
                float have = (oldTop - cut) * Cell * Cell;
                while (level > 0 && material < SandMaterials.Count && SandMaterials.LodRow(material, level) < grainVolume.Length
                       && have < grainVolume[SandMaterials.LodRow(material, level)]) level--;
            }
            if (level > 0 && material < SandMaterials.Count && SandMaterials.LodRow(material, level) < grainVolume.Length)
                material = SandMaterials.LodRow(material, level);

            float gv = grainVolume[Mathf.Clamp(material, 0, grainVolume.Length - 1)];
            float volume = (oldTop - cut) * Cell * Cell;
            // Coarse grains asked for are whole grains of what is there, rounded down: rounded to the nearest, a layer's
            // last coarse grain took the rest of its volume out of the layer under it, and gravel and sand dug from under
            // snow came back as snow.
            int n = level > 0 && forceLevel >= 0 ? Mathf.FloorToInt(volume / gv) : Mathf.RoundToInt(volume / gv);
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
                // A column dug by a wheel runs up through the wheel, and grains given back inside
                // it were thrown out of it at the speed of its rim: mud and wet sand shot straight
                // up behind a wheel. They start on top of it instead.
                if (clearOf != null && clearShape >= 0)
                {
                    float gr = grainRadius[Mathf.Clamp(material, 0, grainRadius.Length - 1)];
                    // Out of the shape the nearest way, along its surface's normal. Lifted straight up a grain at a
                    // time, one started inside the bottom of a wheel came out through its top: sand appeared on top of
                    // the wheel it was dug from under.
                    for (int push = 0; push < 8; push++)
                    {
                        float inside = clearOf.Distance(clearShape, p);
                        if (inside >= gr) break;
                        const float ne = 0.01f;
                        Vector3 grad = new Vector3(
                            clearOf.Distance(clearShape, p + new Vector3(ne, 0f, 0f)) - clearOf.Distance(clearShape, p - new Vector3(ne, 0f, 0f)),
                            clearOf.Distance(clearShape, p + new Vector3(0f, ne, 0f)) - clearOf.Distance(clearShape, p - new Vector3(0f, ne, 0f)),
                            clearOf.Distance(clearShape, p + new Vector3(0f, 0f, ne)) - clearOf.Distance(clearShape, p - new Vector3(0f, 0f, ne)));
                        float gl = grad.magnitude;
                        p += (gl > 1e-6f ? grad / gl : Vector3.down) * (gr - inside + 0.002f);
                    }
                    // Not through the ground under the column, though: a grain under a shape buried to the floor goes up
                    // round it, as it used to.
                    float under = c.bottom[k] + gr;
                    if (p.y < under) p.y = under;
                    for (int up = 0; up < 40 && clearOf.Distance(clearShape, p) < gr; up++) p.y += gr;
                    // Dug from under a shape standing a little above it (see HeldTouch): picked up by it, at its surface.
                    {
                        float gap = clearOf.Distance(clearShape, p);
                        if (pickUp && gap > KickReach && gap < HeldTouch + KickReach) p.y += gap - gr;
                    }
                    // What a spinning wheel digs, it throws: a grain this close to the wheel leaves
                    // with the speed of the wheel's surface where it was, less what runs into the
                    // wheel. (They were thrown from inside it once, at the rim's speed, and mud and wet
                    // sand shot straight up; they started at rest above it after that, and dry sand
                    // stopped flying. Now they start at the surface, outside it, and what a medium
                    // clings by takes the throw out of it.)
                    if (kickMotion != null && kickSink != null && clearOf.Distance(clearShape, p) < KickReach)
                    {
                        int row = Mathf.Clamp(material, 0, adhesionOf.Length - 1);
                        float share = KickShare / (1f + adhesionOf[row] / PickupAdhesion);
                        Vector3 v = kickMotion.SurfaceVelocity(clearShape, p);
                        const float e = 0.02f;
                        float dx = clearOf.Distance(clearShape, p + new Vector3(e, 0f, 0f)) - clearOf.Distance(clearShape, p - new Vector3(e, 0f, 0f));
                        float dy = clearOf.Distance(clearShape, p + new Vector3(0f, e, 0f)) - clearOf.Distance(clearShape, p - new Vector3(0f, e, 0f));
                        float dz = clearOf.Distance(clearShape, p + new Vector3(0f, 0f, e)) - clearOf.Distance(clearShape, p - new Vector3(0f, 0f, e));
                        float nl = Mathf.Sqrt(dx * dx + dy * dy + dz * dz);
                        Vector3 out3 = nl > 1e-6f ? new Vector3(dx / nl, dy / nl, dz / nl) : Vector3.zero;
                        // A turning shape carries what it digs round with it and lets go of it further on:
                        // sand under a spinning tyre rides the tread up the back of the wheel and leaves it
                        // there, at the rim's speed and climbing. Taken where it was dug -- under the wheel,
                        // where the rim moves level -- dry sand left at a metre or two a second, which under
                        // Besiege's gravity is a few centimetres high. So the grain is let go KickExit
                        // degrees further round, with the surface's velocity there. The spin is read off the
                        // surface's velocity field, the centre off the shape's box.
                        Vector3 ax = kickMotion.SurfaceVelocity(clearShape, p + Vector3.right) - v;
                        Vector3 ay = kickMotion.SurfaceVelocity(clearShape, p + Vector3.up) - v;
                        Vector3 spin = new Vector3(ay.z, -ax.z, ax.y);
                        float turning = spin.magnitude;
                        if (turning > 1f)
                        {
                            Vector3 lo, hi;
                            clearOf.Extent(clearShape, out lo, out hi);
                            Vector3 hub = (lo + hi) * 0.5f;
                            Vector3 axis = spin / turning;
                            Vector3 arm = p - hub, rim = Vector3.Cross(spin, arm);
                            // Each grain lets go somewhere along the back of the rim, so what a wheel throws
                            // leaves as a fan rather than a jet from one point.
                            float exit = (KickExitLeast + Rand() * (KickExitMost - KickExitLeast)) * Mathf.Deg2Rad;
                            float ec = Mathf.Cos(exit), es = Mathf.Sin(exit);
                            Vector3 moved = hub + Turn(arm, axis, ec, es);
                            v = v - rim + Turn(rim, axis, ec, es);
                            out3 = Turn(out3, axis, ec, es);
                            // Out at the rim, where it leaves, unless that is inside the sand.
                            float there;
                            if (!TryTop(moved.x, moved.z, out there) || moved.y > there + gr)
                            {
                                p = moved;
                                for (int up = 0; up < 40 && clearOf.Distance(clearShape, p) < gr; up++) p += (out3.sqrMagnitude > 0.5f ? out3 : Vector3.up) * gr;
                            }
                        }
                        float into = Vector3.Dot(v, out3);
                        if (into < 0f) v -= out3 * into;         // what moves into the surface does not come out of it
                        // Up out of the rut. Under a wheel the rim moves level, and a grain thrown level from
                        // the bottom of its own hole ran into the sand beside it and was absorbed again: dry
                        // sand behind a spinning wheel lay still. The sand under the tread is pressed against
                        // what is below and leaves upward, so the throw climbs at least KickLift of its run.
                        float run = Mathf.Sqrt(v.x * v.x + v.z * v.z);
                        if (v.y < run * KickLift) v.y = run * KickLift;
                        v *= share;
                        float sp = v.magnitude;
                        if (sp > KickMost) v *= KickMost / sp;
                        if (sp > 0.5f) { kickSink.Release(p, material, colour, v); continue; }
                    }
                }
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
            // Every change to a chunk's columns comes through here, so the undo journal learns of it here too. A chunk
            // changed that was not copied beforehand (JournalArea) is copied now, as it is after this first change: a
            // column at the far edge of what a stroke set slumping, which is better kept slightly wrong than lost.
            if (journalTouched != null)
            {
                long key = Key(c.cx, c.cz);
                if (!journalTouched.ContainsKey(key))
                {
                    journalTouched[key] = c;
                    if (!journalBefore.ContainsKey(key)) journalBefore[key] = Copy(c);
                }
            }
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
                rollCx = int.MinValue;
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
            CopyDeep(d, c);
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
            if (c.filled == 0 && c.renderHandle != null) emptied.Add(c);
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
                snap.Raw.Add(Copy(c));
            }
            return snap;
        }

        private static Chunk Copy(Chunk c)
        {
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
            CopyDeep(c, d);
            return d;
        }

        // ------------------------------------------------------------------ undo
        //
        // A brush stroke is undone chunk by chunk. While it lasts, the chunks it is about to reach are copied before it
        // reaches them (JournalArea: the brush's circle, and the box its pile settles into, each before it is worked
        // on -- plain array copies, and only once a stroke), every chunk that changes is noted (in MarkNet, which every
        // change passes through), and when it ends the copies of the chunks it changed are packed small and kept as
        // its Edit, the rest let go. Copying the whole bed instead was some fifty megabytes a stroke on a big level.
        // Undoing puts the chunks back as they were and keeps how they are now as the Edit that redoes it.

        /// <summary>Chunks as they were: the cell of each and its packed columns, or null for no sand.</summary>
        public class Edit
        {
            public readonly List<int> Cx = new List<int>(), Cz = new List<int>();
            public readonly List<byte[]> Packed = new List<byte[]>();
            public int Count { get { return Cx.Count; } }
            public void Add(int cx, int cz, byte[] packed) { Cx.Add(cx); Cz.Add(cz); Packed.Add(packed); }
        }

        private Dictionary<long, Chunk> journalBefore;
        private Dictionary<long, Chunk> journalTouched;

        /// <summary>A stroke is being journalled.</summary>
        public bool Journaling { get { return journalTouched != null; } }

        /// <summary>Starts journalling a stroke.</summary>
        public void BeginJournal()
        {
            journalBefore = new Dictionary<long, Chunk>();
            journalTouched = new Dictionary<long, Chunk>();
        }

        /// <summary>Copies, as they are now, the chunks over columns (i0, j0) to (i1, j1) not copied yet this stroke --
        /// those it may be about to change. A chunk with no sand is noted as none.</summary>
        public void JournalArea(int i0, int j0, int i1, int j1)
        {
            if (journalBefore == null) return;
            int cx0 = i0 >> ChunkShift, cz0 = j0 >> ChunkShift, cx1 = i1 >> ChunkShift, cz1 = j1 >> ChunkShift;
            for (int cz = cz0; cz <= cz1; cz++)
                for (int cx = cx0; cx <= cx1; cx++)
                {
                    long key = Key(cx, cz);
                    if (journalBefore.ContainsKey(key)) continue;
                    Chunk c = ChunkAt(cx, cz);
                    journalBefore[key] = c != null && c.filled > 0 ? Copy(c) : null;
                }
        }

        /// <summary>The same around a point, out to <paramref name="reach"/> metres.</summary>
        public void JournalArea(float x, float z, float reach)
        {
            if (journalBefore == null) return;
            JournalArea(Mathf.FloorToInt((x - reach) / Cell), Mathf.FloorToInt((z - reach) / Cell),
                        Mathf.FloorToInt((x + reach) / Cell), Mathf.FloorToInt((z + reach) / Cell));
        }

        /// <summary>Adds to <paramref name="e"/> the chunks with sand now that it does not hold, as none: after a load, the
        /// chunks the file filled, to empty again.</summary>
        public void AddMissing(Edit e)
        {
            HashSet<long> had = new HashSet<long>();
            for (int n = 0; n < e.Count; n++) had.Add(Key(e.Cx[n], e.Cz[n]));
            foreach (KeyValuePair<long, Chunk> kv in chunks)
                if (kv.Value.filled > 0 && !had.Contains(kv.Key)) e.Add(kv.Value.cx, kv.Value.cz, null);
        }

        /// <summary>Every chunk with sand, packed: what clearing the sand takes away, to put back.</summary>
        public Edit PackAll()
        {
            Edit e = new Edit();
            foreach (KeyValuePair<long, Chunk> kv in chunks)
                if (kv.Value.filled > 0) e.Add(kv.Value.cx, kv.Value.cz, BedCodec.Pack(kv.Value, MinThickness));
            return e;
        }

        /// <summary>The chunks the stroke changed, as they were before it; null if it changed nothing.</summary>
        public Edit EndJournal()
        {
            if (journalTouched == null) return null;
            Edit e = new Edit();
            foreach (KeyValuePair<long, Chunk> kv in journalTouched)
            {
                Chunk before;
                journalBefore.TryGetValue(kv.Key, out before);
                e.Add(kv.Value.cx, kv.Value.cz, before != null ? BedCodec.Pack(before, MinThickness) : null);
            }
            journalBefore = null;
            journalTouched = null;
            return e.Count > 0 ? e : null;
        }

        /// <summary>Drops a journal under way without keeping it.</summary>
        public void ForgetJournal()
        {
            journalBefore = null;
            journalTouched = null;
        }

        /// <summary>Puts the chunks of <paramref name="e"/> back as it holds them, and returns how they were just now,
        /// to put back in turn.</summary>
        public Edit Apply(Edit e)
        {
            Edit now = new Edit();
            for (int n = 0; n < e.Count; n++)
            {
                Chunk c = ChunkAt(e.Cx[n], e.Cz[n]);
                now.Add(e.Cx[n], e.Cz[n], c != null && c.filled > 0 ? BedCodec.Pack(c, MinThickness) : null);
                if (e.Packed[n] != null) Decode(e.Packed[n]);
                else if (c != null)
                {
                    Chunk empty = new Chunk();
                    empty.cx = e.Cx[n]; empty.cz = e.Cz[n];
                    Adopt(empty);
                }
            }
            return now;
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


        private const int SaveMagic = 0x534E4431;   // "SND1"

        public byte[] SaveAll(int[] map)
        {
            BedCodec.Writer w = new BedCodec.Writer(4096);
            w.Int(SaveMagic);
            int count = 0;
            foreach (KeyValuePair<long, Chunk> kv in chunks) if (kv.Value.filled > 0) count++;
            w.Unsigned((uint)count);
            foreach (KeyValuePair<long, Chunk> kv in chunks)
            {
                if (kv.Value.filled <= 0) continue;
                byte[] b = BedCodec.Pack(kv.Value, MinThickness, map);
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
        /// Melts the surface layer of the columns near a heat source, where it is one of the
        /// <paramref name="meltable"/> rows: up to <paramref name="depth"/> metres off a column
        /// right at the source, less further off, none at <paramref name="reach"/>, so a pile
        /// goes from the side nearest the flame. Distance is to the column's top, in three
        /// dimensions -- a fire above a pile melts its peak first. Returns columns touched.
        /// </summary>
        public int Melt(Vector3 at, float reach, float depth, bool[] meltable)
        {
            if (!Enabled || depth <= 0f || reach <= 0f) return 0;
            int touched = 0;
            int i0 = Mathf.FloorToInt((at.x - reach) / Cell), i1 = Mathf.FloorToInt((at.x + reach) / Cell);
            int j0 = Mathf.FloorToInt((at.z - reach) / Cell), j1 = Mathf.FloorToInt((at.z + reach) / Cell);
            for (int j = j0; j <= j1; j++)
                for (int i = i0; i <= i1; i++)
                {
                    Chunk c = Find(i, j);
                    if (c == null) continue;
                    int k = Slot(i, j);
                    if (c.top[k] - c.bottom[k] <= MinThickness) continue;
                    int row = c.material[k];
                    if (row >= meltable.Length || !meltable[row]) continue;
                    float dx = (i + 0.5f) * Cell - at.x, dy = c.top[k] - at.y, dz = (j + 0.5f) * Cell - at.z;
                    float d = Mathf.Sqrt(dx * dx + dy * dy + dz * dz);
                    if (d >= reach) continue;
                    float w = 1f - d / reach;
                    float layer = c.top[k] - (Layered(c, k) ? c.split[k] : c.bottom[k]);
                    float take = Mathf.Min(layer, depth * w * w);
                    if (take <= 0f) continue;
                    Withdraw(c, i, j, take);
                    touched++;
                }
            return touched;
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
                        // The two layers are one medium now: what was under the lower one is the lower one.
                        List<Deep> l;
                        if (c.deep != null && c.deep.TryGetValue(k, out l) && l.Count > 0)
                        {
                            Deep d = l[l.Count - 1];
                            l.RemoveAt(l.Count - 1);
                            if (l.Count == 0) c.deep.Remove(k);
                            c.lowMaterial[k] = d.material; c.lowColour[k] = d.colour; c.split[k] = d.upper;
                        }
                        else c.split[k] = c.bottom[k];
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

        /// <summary>
        /// Which way a ploughing face pushes the sand in front of it: off its own surface, as a blade does -- one set
        /// across the way it goes pushes the sand ahead, one set at an angle sends it off to the side, to run off the
        /// end of the blade in a windrow. (It was always the way it went: an angled snow plough pushed its sand
        /// straight ahead, and dug what was behind its middle.)
        /// </summary>
        /// Returns false where the face does not face the way it goes, which is no plough.
        private static bool PlowAway(IBedShapes shapes, int s, Vector3 at, float gx, float gz, out Vector2 away)
        {
            const float e = 0.02f;
            float nx = shapes.Distance(s, at + new Vector3(e, 0f, 0f)) - shapes.Distance(s, at - new Vector3(e, 0f, 0f));
            float nz = shapes.Distance(s, at + new Vector3(0f, 0f, e)) - shapes.Distance(s, at - new Vector3(0f, 0f, e));
            float nl = Mathf.Sqrt(nx * nx + nz * nz);
            away = new Vector2(gx, gz);
            if (nl < 1e-6f) return false;
            nx /= nl; nz /= nl;
            if (nx * gx + nz * gz <= 0.2f) return false;
            if (PlowOffFace) away = new Vector2(nx, nz);
            return true;
        }

        /// <summary>Whether a plough pushes sand off its face (true) or straight the way it goes, as it once did: there
        /// for the test that compares the two.</summary>
        public static bool PlowOffFace = true;

        /// <summary>How fast a face must move sideways into the sand to plough it, at least, and
        /// how far the sand must stand above the shape's underside: less, and the shape is riding
        /// on it. (It asked where the face met the column, which for a layer thinner than the
        /// blade was at the blade's foot -- so a thin layer was never ploughed at all.)</summary>
        public const float PlowSpeed = 0.3f;
        public const float PlowLift = 0.03f;

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
            float cx = (i + 0.5f) * Cell, cz = (j + 0.5f) * Cell;
            Vector2 away = new Vector2(cx - middle.x, cz - middle.z);
            if (away.sqrMagnitude < 1e-6f) away = new Vector2(1f, 0f);
            away.Normalize();
            Displace(c, i, j, cut, away, shapes, s, margin, 1.5f);
        }

        /// <summary>The same, moving the sand along <paramref name="away"/>, up to
        /// <paramref name="reach"/> metres.</summary>
        private void Displace(Chunk c, int i, int j, float cut, Vector2 away, IBedShapes shapes, int s, float margin, float reach)
        {
            int k = Slot(i, j);
            float cx = (i + 0.5f) * Cell, cz = (j + 0.5f) * Cell;
            for (float d = Cell; d <= reach; d += Cell)
            {
                float tx = cx + away.x * d, tz = cz + away.y * d;
                int ti = Mathf.FloorToInt(tx / Cell), tj = Mathf.FloorToInt(tz / Cell);
                if (ti == i && tj == j) continue;
                float h;
                if (!TryTop(tx, tz, out h)) h = ground.Height(tx, tz);
                if (h <= NoGround) continue;
                if (Held(ti, tj) || shapes.Distance(s, new Vector3(tx, h + 0.02f, tz)) < margin) continue;
                for (int pass = 0; pass < MaxLayers; pass++)
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
        public const float TouchDistance = 0.06f;

        /// <summary>Seconds between digs (the field's sand step), for how far a slipping surface goes in one.</summary>
        public float StepSeconds = 0.04f;

        /// <summary>How thick a layer a slipping surface takes off, as a share of how far it slips in a step: a
        /// wheel slipping 3 m/s at 25 steps a second takes 12 cm a step, at least a grain's worth.</summary>
        public const float DigBite = 1.0f;

        /// <summary>The thickest layer a slipping surface takes off a column in one step, in metres.</summary>
        public const float DigSkinMost = 0.15f;

        /// <summary>A contact steeper than this, as the upward part of its normal, is a shape
        /// pushing into the side of the sand rather than standing on it.</summary>
        private const float SideContact = 0.5f;

        private readonly List<Vector3> digs = new List<Vector3>();

        /// <summary>Pressure, in pascals, that presses a medium down a metre, where its row does not
        /// say (each medium's is its <c>firmness</c>); a quarter of it presses half a metre. A
        /// machine's wheel on Besiege's gravity bears a few kilopascals.</summary>
        public const float PressStiffness = 50000f;

        /// <summary>Fastest a column sinks under load, in metres a second. It was 0.6, and a wheel crossing a
        /// column in a few hundredths of a second left a rut of a few centimetres whatever it weighed; at 6 the
        /// sand fell away from under a wheel faster than its collider was cooked again.</summary>
        private const float PressRate = 2f;

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
        public const float SideDigSpeed = 0.5f;
        public const float SideDigForce = 300f;

        /// <summary>
        /// The same, with how fast the body was moving into the sand. A side contact digs only
        /// when it is a real blow or a real shove: digging at every touch of a steep face let a
        /// machine plough through a wall of sand it should have hit.
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
        /// pressure p sinks towards sqrt(p / firmness) and stays there -- sand does not spring
        /// back -- so a wheel leaves a rut as deep as it was heavy. What it sinks by is pushed out
        /// to the columns round it that are not loaded, as a lip, half of it while the column still
        /// packs and all of it after. A load is spread over the nine columns around its point, the
        /// size of a contact patch.
        /// </summary>
        /// <summary>Lets go of the loads and digs recorded since the last step, unused: the sand is paused.</summary>
        public void DropLoads()
        {
            loads.Clear();
            digs.Clear();
        }

        public void Press(float dt)
        {
            pressStep++;
            // Forget what has not been stood on for a while, now and then.
            // A column's pressure goes when it is no longer held -- let go by digging (see Release) or not stood on.
            if ((pressStep & 63) == 0 && pressOf.Count > 0)
            {
                expired.Clear();
                foreach (KeyValuePair<long, float> kv in pressOf) if (!Held(KeyI(kv.Key), KeyJ(kv.Key))) expired.Add(kv.Key);
                for (int n = 0; n < expired.Count; n++) pressOf.Remove(expired[n]);
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
                        Hold(i0 + di, j0 + dj, pressStep + HoldSteps);
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
            for (int n = 0; n < batch.Count; n++) pressOf[batch[n]] = pressure[batch[n]];

            for (int n = 0; n < batch.Count; n++)
            {
                int i = KeyI(batch[n]), j = KeyJ(batch[n]);
                Chunk c = Find(i, j);
                if (c == null) continue;
                int k = Slot(i, j);
                float depth = c.top[k] - c.bottom[k];
                if (depth <= MinThickness) continue;

                int mat = Mathf.Clamp(c.material[k], 0, firmnessOf.Length - 1);
                // How far this pressure presses the column: firmness is the pressure that presses it a metre, and
                // twice as far takes four times as much (Bekker's p = k z^n with n = 2), so a block resting on the
                // sand marks it and a loaded wheel sinks well in without the one being nothing or the other
                // going to the floor.
                float want = Mathf.Sqrt(pressure[batch[n]] / firmnessOf[mat]);
                float sink = Mathf.Min(want - c.packed[k], PressRate * dt);
                if (sink <= 1e-4f) continue;
                if (depth - sink < MinThickness) sink = depth - MinThickness;
                if (sink <= 0f) continue;

                // Where what it sinks by goes: out past the footprint, as a lip beside the rut (it went to the
                // next columns over once, which were still under the tyre: they rose into it, and a wheel that
                // found itself inside the sand was dug down through it). Until the column has been pressed by
                // its medium's packing limit half of it packs tighter and half goes to the lip; past that the
                // sand cannot pack any tighter and all of it is pushed aside. (Pressing used to stop there:
                // a rut was never deeper than a third of the sand, and anything at all pressed it that far.)
                int free = 0;
                for (int n2 = 0; n2 < 8; n2++)
                {
                    int ti = i + LipI[n2] * LipReach, tj = j + LipJ[n2] * LipReach;
                    if (!pressure.ContainsKey(Key(ti, tj)) && !Held(ti, tj)) free++;
                }
                bool packs = c.packed[k] < (depth + c.packed[k]) * packLimitOf[mat];
                if (!packs && free == 0) continue;      // packed as far as it goes, and nowhere to push it
                byte medium = c.material[k];
                uint tint = c.colour[k];

                c.top[k] -= sink;
                c.packed[k] += sink;
                Settle(c, k);
                MarkDirty(c, i, j);
                Activate(i, j);

                if (free == 0) continue;
                float spill = sink * (packs ? 0.5f : 1f);
                for (int n2 = 0; n2 < 8; n2++)
                {
                    int ti = i + LipI[n2] * LipReach, tj = j + LipJ[n2] * LipReach;
                    if (!pressure.ContainsKey(Key(ti, tj)) && !Held(ti, tj))
                        Deposit(ti, tj, spill / free, medium, tint);
                }
            }
        }

        private readonly Dictionary<long, float> pressure = new Dictionary<long, float>();

        // The pressure each column was last pressed with, for as long as it is held: what a wheel weighs on the sand
        // it is spinning in (see BiteFor).
        private readonly Dictionary<long, float> pressOf = new Dictionary<long, float>();

        /// <summary>The pressure, in pascals, at which a slipping wheel bites as deep as <see cref="DigBite"/> says.</summary>
        public const float BitePressure = 800f;

        /// <summary>
        /// How much deeper than its slip alone a surface bites into a column, by what it presses on it with: the square
        /// root of the pressure over <see cref="BitePressure"/>, from 0.6 to 2.5. A heavy machine's wheel spinning in
        /// sand digs itself in and throws more; one barely touching it skims. (Weight only pressed ruts before: a
        /// loaded wheel and a light one threw the same.)
        /// </summary>
        private float BiteFor(int i, int j)
        {
            float p;
            if (pressOf.Count == 0 || !pressOf.TryGetValue(Key(i, j), out p)) return 1f;
            return Mathf.Clamp(Mathf.Sqrt(p / BitePressure), 0.6f, 2.5f);
        }

        // Columns something is standing on: loaded by a collider within the last few presses.
        // Sand cannot flow in under a wheel resting on it, nor out from under it, and a wheel
        // standing in its own rut is not buried. Slumping refilled the rut beneath a wheel, the
        // refilled sand stood above the wheel's bottom, and the buried rule then dug it out from
        // under it as grains -- over and over, until the machine had gone through the pile.
        // Kept per column in its chunk (Chunk.holdUntil), not in a dictionary: slumping asks of both columns of every
        // pair it looks at, eight pairs a column, and two dictionary lookups a pair were a good part of its time.
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
            Chunk c = Find(i, j);
            return c != null && c.holdUntil != null && c.holdUntil[Slot(i, j)] >= pressStep;
        }

        private void Hold(int i, int j, int until)
        {
            Chunk c = Find(i, j);
            if (c == null) return;   // no sand there to hold
            if (c.holdUntil == null) c.holdUntil = new int[ChunkSide * ChunkSide];
            c.holdUntil[Slot(i, j)] = until;
        }

        private void Unhold(int i, int j)
        {
            Chunk c = Find(i, j);
            if (c != null && c.holdUntil != null) c.holdUntil[Slot(i, j)] = 0;
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
            while (active.Count > activeHead && timer.Elapsed.TotalMilliseconds < budgetMs) Relax(100);
        }

        /// <summary>Columns waiting to be checked for slumping.</summary>
        public int Unsettled { get { return active.Count - activeHead; } }

        public void Relax(int budget)
        {
            if (!Enabled || ground == null || active.Count == activeHead) return;

            // Take a batch off the front. Slumping re-activates the columns it touches,
            // which appends to the same list, so what is left over and what is new both
            // wait for the next call.
            int take = Mathf.Min(active.Count - activeHead, budget);
            batch.Clear();
            for (int n = 0; n < take; n++)
            {
                long key = active[activeHead + n];
                batch.Add(key);
                int ki = KeyI(key), kj = KeyJ(key);
                Chunk kc = Find(ki, kj);
                if (kc != null) kc.awake[Slot(ki, kj)] = false;
            }
            activeHead += take;
            if (activeHead == active.Count) { active.Clear(); activeHead = 0; }
            else if (activeHead > 4096 && activeHead * 2 > active.Count)
            {
                active.RemoveRange(0, activeHead);
                activeHead = 0;
            }

            SlumpChecks += batch.Count;
            for (int n = 0; n < batch.Count; n++)
            {
                int i = KeyI(batch[n]), j = KeyJ(batch[n]);
                if (Level(i, j)) continue;
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

        /// <summary>
        /// Whether a column and all eight neighbours hold sand and none is steeper than the
        /// gentlest medium would slump: then <see cref="Slump"/> would find nothing to move
        /// in any of the eight pairs, and they need not be asked. The inside of a painted
        /// plateau is all of this; only its edge has any work.
        /// </summary>
        private bool Level(int i, int j)
        {
            if (gentlest <= 0f) return false;
            Chunk c = Find(i, j);
            if (c == null) return false;
            int k = Slot(i, j);
            if (c.top[k] - c.bottom[k] <= MinThickness) return false;
            float t = c.top[k];
            for (int dj = -1; dj <= 1; dj++)
                for (int di = -1; di <= 1; di++)
                {
                    if (di == 0 && dj == 0) continue;
                    int ni = i + di, nj = j + dj;
                    Chunk n = Find(ni, nj);
                    if (n == null) return false;
                    int nk = Slot(ni, nj);
                    if (n.top[nk] - n.bottom[nk] <= MinThickness) return false;
                    float d = n.top[nk] - t;
                    if (d > gentlest || d < -gentlest) return false;
                }
            return true;
        }

        /// <summary>Moves sand between two neighbouring columns if they are steeper
        /// than the material allows, from whichever is higher.</summary>
        private readonly float[] slumpAmount = new float[MaxLayers];
        private readonly byte[] slumpMaterial = new byte[MaxLayers];
        private readonly uint[] slumpColour = new uint[MaxLayers];

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

            // In layers: what comes off each layer keeps its medium, the surface first and then what is
            // under it as far as the move reaches, and goes down in the order it came, the lowest first, so
            // the surface's sand ends on top where it was.
            float[] amount = slumpAmount; byte[] material2 = slumpMaterial; uint[] colour2 = slumpColour;
            int layers = Layers(hc, hk, amount, material2, colour2);
            float remaining = move;
            for (int q = 0; q < layers; q++)
            {
                float take = Mathf.Min(remaining, amount[q]);
                remaining -= take;
                amount[q] = take;
            }
            if (remaining > 0f && layers > 0) amount[layers - 1] += remaining;
            // A piece smaller than the thinnest column joins the one under it, or the one over it.
            for (int q = 0; q < layers - 1; q++) if (amount[q] > 0f && amount[q] < MinThickness) { amount[q + 1] += amount[q]; amount[q] = 0f; }
            for (int q = layers - 1; q > 0; q--) if (amount[q] > 0f && amount[q] < MinThickness) { amount[q - 1] += amount[q]; amount[q] = 0f; }
            for (int q = layers - 1; q >= 0; q--)
                if (amount[q] > 0f && !Deposit(loI, loJ, amount[q], material2[q], colour2[q])) return;
            Withdraw(hc, hiI, hiJ, move);
            SlumpMoves++;
        }

        /// <summary>Columns looked at for slumping, and slumps made, since the bed was made (for measuring).</summary>
        public long SlumpChecks, SlumpMoves;
    }
}
