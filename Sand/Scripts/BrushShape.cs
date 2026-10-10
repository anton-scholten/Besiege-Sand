using UnityEngine;

namespace SandMod
{
    /// <summary>
    /// How much of a brush a point gets, 0 to 1, by the brush's shape. Every shape tapers over its
    /// last few columns, so a brushed edge is a slope rather than a step.
    ///
    /// <see cref="Weight"/> answers for one point; <see cref="Row"/> answers for a run of points
    /// along x and gives the same numbers, but works out what the run shares once (the edge taper,
    /// the lattice corners a noise cell is made of) and has no per-point switch.
    /// </summary>
    public static class BrushShape
    {
        public const int Circle = 0, Flat = 1, Square = 2, Random = 3, Ring = 4;

        /// <summary>The width of the taper at the edge, as a share of the radius: a few columns, and
        /// at least a fifth of a small brush.</summary>
        private static float Edge(float radius)
        {
            return Mathf.Clamp(3f * SandBed.Cell / radius, 0.05f, 0.5f);
        }

        /// <summary>
        /// The edge of a flat brush, as a share of its radius: the default few columns, or a slope.
        /// A <paramref name="taper"/> in metres is how far the edge is to reach in from the brush's
        /// outline; the weight then falls off in a straight line, so a layer of the brush's weight
        /// times its thickness has the slope it was asked for instead of a cliff the bed must slump.
        /// </summary>
        private static float Edge(float radius, float taper)
        {
            return taper > 0f ? Mathf.Clamp(Mathf.Max(taper, 3f * SandBed.Cell) / radius, 0.05f, 0.95f) : Edge(radius);
        }

        private static float Fall(float t, float taper)
        {
            return taper > 0f ? Mathf.Clamp01(t) : Smooth(t);
        }

        public static float Weight(int style, float dx, float dz, float radius, int seed, float taper)
        {
            float u = dx / radius, v = dz / radius;
            float d2 = u * u + v * v;
            float edge = Edge(radius, taper);
            switch (style)
            {
                case Flat:
                {
                    float d = Mathf.Sqrt(d2);
                    return d >= 1f ? 0f : Fall((1f - d) / edge, taper);
                }
                case Square:
                {
                    float m = Mathf.Max(Mathf.Abs(u), Mathf.Abs(v));
                    return m >= 1f ? 0f : Fall((1f - m) / edge, taper);
                }
                case Ring:
                {
                    // A band a quarter of the radius wide, round at its middle.
                    float d = Mathf.Sqrt(d2);
                    float t = Mathf.Abs(d - 0.8f) / 0.2f;
                    return t >= 1f ? 0f : 1f - t * t;
                }
                case Random:
                {
                    if (d2 >= 1f) return 0f;
                    // Smooth lumps a third of the brush across, fixed in the world for a stroke
                    // so holding the button grows the same lumps rather than a fuzz.
                    float scale = Mathf.Max(radius / 3f, 0.25f);
                    float n = Noise((radius * u) / scale + seed * 0.37f, (radius * v) / scale - seed * 0.53f, seed);
                    float lump = Mathf.Clamp01((n - 0.45f) * 2.5f);
                    return lump * (1f - d2);
                }
                default:
                    return d2 >= 1f ? 0f : 1f - d2;
            }
        }

        /// <summary>
        /// The columns of a row, at height <paramref name="dz"/> from the brush's centre, that can
        /// have any weight: <c>[from, to)</c> counted in steps of <paramref name="step"/> from
        /// <paramref name="x0"/> (the first column's offset from the centre), and a hole in the
        /// middle of it for a ring. Outside them <see cref="Weight"/> is zero.
        /// </summary>
        public static void Span(int style, float dz, float radius, float x0, float step, int count,
                                out int from, out int to, out int holeFrom, out int holeTo)
        {
            from = 0; to = count; holeFrom = holeTo = 0;
            if (style == Square) return;
            float outer2 = radius * radius - dz * dz;
            if (outer2 <= 0f) { to = 0; return; }
            float half = Mathf.Sqrt(outer2);
            from = Mathf.Max(0, Mathf.CeilToInt((-half - x0) / step) - 1);
            to = Mathf.Min(count, Mathf.FloorToInt((half - x0) / step) + 2);
            if (style == Ring)
            {
                // Nothing inside 0.6 of the radius (the band is 0.8 +- 0.2).
                float inner2 = 0.36f * radius * radius - dz * dz;
                if (inner2 > 0f)
                {
                    float ih = Mathf.Sqrt(inner2);
                    holeFrom = Mathf.Max(from, Mathf.CeilToInt((-ih - x0) / step) + 1);
                    holeTo = Mathf.Min(to, Mathf.FloorToInt((ih - x0) / step));
                }
            }
        }

        /// <summary>
        /// Weights for <paramref name="count"/> points along x at height <paramref name="dz"/> from
        /// the centre, the first at <paramref name="x0"/> from it and the rest <paramref name="step"/>
        /// apart. Only <c>[from, to)</c> of <see cref="Span"/> is written; the caller skips the rest.
        /// </summary>
        public static void Row(int style, float dz, float radius, int seed, float x0, float step,
                               int count, int from, int to, float[] w, float taper)
        {
            if (style == Random) { RandomRow(dz, radius, seed, x0, step, from, to, w); return; }
            for (int n = from; n < to; n++) w[n] = Weight(style, x0 + n * step, dz, radius, seed, taper);
        }

        /// <summary>Value noise along a row: the four lattice corners are looked up when the
        /// point crosses into a new cell, not for every point.</summary>
        private static void RandomRow(float dz, float radius, int seed, float x0, float step,
                                      int from, int to, float[] w)
        {
            float scale = Mathf.Max(radius / 3f, 0.25f);
            float v = dz / radius;
            float zn = (radius * v) / scale - seed * 0.53f;
            int zi = Mathf.FloorToInt(zn);
            float fz = zn - zi;
            fz = fz * fz * (3f - 2f * fz);
            int cachedXi = int.MinValue;
            float a0 = 0f, a1 = 0f, c0 = 0f, c1 = 0f;   // the corners at xi and xi + 1, for zi and zi + 1
            float v2 = v * v;
            for (int n = from; n < to; n++)
            {
                float u = (x0 + n * step) / radius;
                float d2 = u * u + v2;
                if (d2 >= 1f) { w[n] = 0f; continue; }
                float xn = (radius * u) / scale + seed * 0.37f;
                int xi = Mathf.FloorToInt(xn);
                if (xi != cachedXi)
                {
                    if (xi == cachedXi + 1) { a0 = a1; c0 = c1; }
                    else { a0 = Lattice(xi, zi, seed); c0 = Lattice(xi, zi + 1, seed); }
                    a1 = Lattice(xi + 1, zi, seed); c1 = Lattice(xi + 1, zi + 1, seed);
                    cachedXi = xi;
                }
                float fx = xn - xi;
                fx = fx * fx * (3f - 2f * fx);
                float noise = Mathf.Lerp(Mathf.Lerp(a0, a1, fx), Mathf.Lerp(c0, c1, fx), fz);
                float lump = Mathf.Clamp01((noise - 0.45f) * 2.5f);
                w[n] = lump * (1f - d2);
            }
        }

        private static float Smooth(float t)
        {
            t = Mathf.Clamp01(t);
            return t * t * (3f - 2f * t);
        }

        /// <summary>Value noise: a smooth field of 0 to 1 over the plane.</summary>
        private static float Noise(float x, float z, int seed)
        {
            int xi = Mathf.FloorToInt(x), zi = Mathf.FloorToInt(z);
            float fx = x - xi, fz = z - zi;
            fx = fx * fx * (3f - 2f * fx); fz = fz * fz * (3f - 2f * fz);
            float a = Lattice(xi, zi, seed), b = Lattice(xi + 1, zi, seed);
            float c = Lattice(xi, zi + 1, seed), d = Lattice(xi + 1, zi + 1, seed);
            return Mathf.Lerp(Mathf.Lerp(a, b, fx), Mathf.Lerp(c, d, fx), fz);
        }

        private static float Lattice(int x, int z, int seed)
        {
            uint h = (uint)(x * 374761393 + z * 668265263 + seed * 2147483647);
            h = (h ^ (h >> 13)) * 1274126177u;
            h ^= h >> 16;
            return (h & 0xFFFF) / 65535f;
        }
    }

    /// <summary>
    /// Visits the columns a brush covers, with how much each gets. A brush that fits the time it is
    /// given gets every column, every frame. A bigger one gets every <c>stride</c>th column a frame
    /// -- a different set each time, spread evenly over the brush, so every column has had its turn
    /// after <c>stride</c> frames -- each given <c>stride</c> frames' worth, and the stride is kept
    /// small enough that what is left standing on a column is less than a column can hold: past
    /// that the brush paints slower, not rougher. (Taking whole bands of rows instead left strips a
    /// metre thick beside bare strips, each a cliff the brush's length; and a stride that let a
    /// column stand a few tenths of a metre proud left a field of spikes that moved from frame to
    /// frame and stayed where the brush was let go.)
    /// </summary>
    public class BrushSweep
    {
        /// <summary>The most frames' worth a column is given at once, however big the brush.</summary>
        public const int MaxStride = 48;

        /// <summary>The thickest speckle left on a column in one frame, in metres: under the step
        /// between neighbours that sand stands at, so none of it is a spike.</summary>
        public const float MostSpeckle = 0.04f;

        private int stride = 1;
        private int phase;

        private int lastMost = 1;
        private bool lastAbandoned;

        /// <summary>
        /// Tells the sweep what the frame it has just made cost, <paramref name="ms"/> milliseconds in all
        /// -- the sweep and whatever was done with its columns, which is where the time goes -- against
        /// <paramref name="budgetMs"/>: the next frame's set is more columns apart if this one ran over,
        /// one fewer if it left room.
        /// </summary>
        public void Adapt(double ms, float budgetMs)
        {
            if (budgetMs <= 0f || PinnedStride > 0) return;
            int k = stride;
            if (lastAbandoned || ms > budgetMs * 4.0) stride = Mathf.Min(lastMost, k * 2);
            else
            {
                int want = Mathf.CeilToInt((float)(k * ms / budgetMs));
                if (want > k) stride = Mathf.Min(lastMost, want);
                else if (want < k - 1 || (want < k && ms < budgetMs * 0.6)) stride = Mathf.Max(1, k - 1);
            }
        }

        /// <summary>A stride to use whatever the clock says, for tests; 0 to let the clock decide.</summary>
        public int PinnedStride;
        private float[] weights = new float[0];
        private readonly System.Diagnostics.Stopwatch timer = new System.Diagnostics.Stopwatch();

        /// <summary>Columns visited by the last <see cref="Run"/>, and the stride it used.</summary>
        public int LastColumns, LastStride;

        /// <param name="layer">How thick a frame's layer is at full strength, in metres, which sets
        /// how many frames' worth a column may be given at once (<see cref="MostSpeckle"/>).</param>
        public void Run(float cx, float cz, float radius, int style, int seed, float budgetMs, float taper,
                        float layer, System.Action<float, float, float> each)
        {
            int most = layer > 0f ? Mathf.Clamp(Mathf.FloorToInt(MostSpeckle / layer), 1, MaxStride) : 1;
            if (PinnedStride > 0) stride = PinnedStride;
            else if (stride > most) stride = most;
            float cell = SandBed.Cell;
            int i0 = Mathf.FloorToInt((cx - radius) / cell), i1 = Mathf.FloorToInt((cx + radius) / cell);
            int j0 = Mathf.FloorToInt((cz - radius) / cell), j1 = Mathf.FloorToInt((cz + radius) / cell);
            int width = i1 - i0 + 1;
            if (budgetMs <= 0f && PinnedStride <= 0) stride = 1;
            int k = stride;
            phase = (phase + 1) % k;
            float scale = k;
            float x0 = (i0 + 0.5f) * cell - cx;
            if (weights.Length < width) weights = new float[width];
            float[] w = weights;
            // Reset before Start: Start() does nothing to a watch that is already running, and this one is
            // never stopped. Run is called every frame a brush is held.
            timer.Reset(); timer.Start();
            int columns = 0;
            bool abandoned = false;

            for (int j = j0; j <= j1 && !abandoned; j++)
            {
                float z = (j + 0.5f) * cell, dz = z - cz;
                int from, to, holeFrom, holeTo;
                BrushShape.Span(style, dz, radius, x0, cell, width, out from, out to, out holeFrom, out holeTo);
                if (to <= from) continue;
                if (k == 1)
                {
                    BrushShape.Row(style, dz, radius, seed, x0, cell, width, from, to, w, taper);
                    for (int n = from; n < to; n++)
                    {
                        if (n == holeFrom && holeTo > holeFrom) { n = holeTo - 1; continue; }
                        if (w[n] > 0f) { each((i0 + n + 0.5f) * cell, z, w[n]); columns++; }
                    }
                }
                else
                {
                    // The columns of this row that are in this frame's set: (i + 3 j + phase) a
                    // multiple of the stride, which scatters the set rather than lining it up.
                    int off = (((i0 + from + 3 * j + phase) % k) + k) % k;
                    for (int n = from + (off == 0 ? 0 : k - off); n < to; n += k)
                    {
                        if (n >= holeFrom && n < holeTo) continue;
                        float wt = BrushShape.Weight(style, x0 + n * cell, dz, radius, seed, taper);
                        if (wt > 0f) { each((i0 + n + 0.5f) * cell, z, wt * scale); columns++; }
                    }
                }
                // A frame that has run far past its time stops where it is; the next one is thinner.
                if (budgetMs > 0f && timer.Elapsed.TotalMilliseconds > budgetMs * 2f) abandoned = true;
            }

            lastMost = most; lastAbandoned = abandoned;
            LastColumns = columns;
            LastStride = k;
        }
    }
}
