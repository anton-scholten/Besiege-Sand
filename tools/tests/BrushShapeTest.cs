using System;
using System.Diagnostics;
using UnityEngine;
using SandMod;

/// <summary>
/// The brush's row evaluator and sweep against the one-point weight they must agree with, for every
/// style, and the sweep's budget: a brush too big for a frame is covered evenly, in the end.
/// </summary>
class PourGround : IBedGround { public float Height(float x, float z) { return 0f; } }

class BrushShapeTest
{
    static int fails = 0;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) fails++; }

    static readonly string[] Names = { "Circle", "Flat circle", "Square", "Random", "Ring" };

    public static int Main(string[] args)
    {
        float cell = SandBed.Cell;
        foreach (float taper in new float[] { 0f, 1.7f })
        foreach (float radius in new float[] { 0.6f, 3f, 11.3f })
            for (int style = 0; style < 5; style++)
            {
                int seed = 12345 + style;
                int i0 = Mathf.FloorToInt(-radius / cell), i1 = Mathf.FloorToInt(radius / cell);
                int width = i1 - i0 + 1;
                float x0 = (i0 + 0.5f) * cell;
                float[] w = new float[width];
                double worst = 0; int missed = 0, rowsChecked = 0;
                for (int j = i0 - 2; j <= i1 + 2; j++)
                {
                    float dz = (j + 0.5f) * cell;
                    int from, to, hf, ht;
                    BrushShape.Span(style, dz, radius, x0, cell, width, out from, out to, out hf, out ht);
                    if (to > from) BrushShape.Row(style, dz, radius, seed, x0, cell, width, from, to, w, taper);
                    for (int n = 0; n < width; n++)
                    {
                        float want = BrushShape.Weight(style, x0 + n * cell, dz, radius, seed, taper);
                        bool inside = n >= from && n < to && !(n >= hf && n < ht);
                        float got = inside ? w[n] : 0f;
                        double e = Math.Abs(got - want);
                        if (e > worst) worst = e;
                        if (!inside && want > 0f) missed++;
                    }
                    rowsChecked++;
                }
                Check(worst < 1e-5 && missed == 0, Names[style] + " r=" + radius + " taper=" + taper + ": row equals point weight (max error " + worst.ToString("E1") + ", " + missed + " columns outside the span with weight)");
            }

        // Sweep: with no time limit, one call covers every column once; with a tight one, a few calls do,
        // and each column's total over a sweep is about what one full call would give it.
        for (int style = 0; style < 5; style++)
        {
            float radius = 8f; int seed = 7;
            double full = 0; int fullCols = 0;
            BrushSweep a = new BrushSweep();
            a.Run(0f, 0f, radius, style, seed, 0f, 0f, 0f, delegate (float x, float z, float wt) { full += wt; fullCols++; });

            // Every 4th column, a different quarter each frame, each given four frames' worth: four frames paint what four whole frames do.
            BrushSweep b = new BrushSweep(); b.PinnedStride = 4;
            double part = 0; int cols = 0;
            for (int k = 0; k < 4; k++)
                b.Run(0f, 0f, radius, style, seed, 1000f, 0f, 0.01f, delegate (float x, float z, float wt) { part += wt; cols++; });
            Console.WriteLine(Names[style] + ": whole brush " + fullCols + " columns in one call; every 4th column: " + (cols / 4) + " a call");
            Check(fullCols > 0, Names[style] + ": covers columns");
            Check(part > full * 3.6 && part < full * 4.4, Names[style] + ": four frames of a stride of 4 give the brush four whole frames' worth (" + (part / full / 4).ToString("F2") + "x)");
            Check(cols > fullCols * 0.9 && cols < fullCols * 1.1, Names[style] + ": and visit each column once (" + cols + " of " + fullCols + ")");
        }
        Pour();
        DeepRelease();
        Batch();
        Kick();
        Deferred();
        SettledAsItGrows();
        OtherMediaStay();
        Stranded();
        EveryWheelGetsATurn();
        Console.WriteLine(fails == 0 ? "ALL PASSED" : (fails + " FAILED"));
        return fails;
    }

    // Sand poured with PourVolume lands at its angle of repose, loses none, and leaves the bed nothing to slump.
    static void Pour()
    {
        SandMaterials.Params[] table = SandMaterials.Build();
        SandBed bed = new SandBed(); bed.Enabled = true; bed.SetGround(new PourGround()); bed.SetMaterials(table);
        float cell = SandBed.Cell, area = cell * cell, poured = 0f;
        // Nine metres of sand dropped in one spot, 3 cm at a time: a cone.
        for (int n = 0; n < 300; n++)
        {
            bed.PourVolume(0.06f, 0.06f, 0.03f * area, 0, 0xFFFFFFFFu);
            poured += 0.03f * area;
        }
        Console.WriteLine("cone: " + bed.FilledColumns + " columns, awake " + bed.Unsettled);
        float limit0 = bed.Repose(0) * cell;
        CheckSlope(bed, limit0, "a cone poured a little at a time");
        // A thick layer all at once over a square: poured in pieces, and still no volume lost.
        for (int i = -20; i < 20; i++)
            for (int j = -20; j < 20; j++)
            {
                bed.PourVolume((i + 20.5f) * cell, (j + 20.5f) * cell, 0.5f * area, 0, 0xFFFFFFFFu);
                poured += 0.5f * area;
            }
        Check(Math.Abs(bed.Volume - poured) < poured * 0.001f, "poured sand is all there (" + bed.Volume.ToString("F3") + " of " + poured.ToString("F3") + " m^3)");

        bed.Relax(1000000);
        float after = bed.Volume;
        Check(Math.Abs(after - poured) < poured * 0.001f, "relaxing what is left loses nothing");
    }

    static void CheckSlope(SandBed bed, float limit, string what)
    {
        float worst = 0f; int columns = 0;
        for (int i = -150; i < 150; i++)
            for (int j = -150; j < 150; j++)
            {
                float top, bottom; uint colour;
                if (!bed.Thickness(i, j, out top, out bottom, out colour)) continue;
                columns++;
                for (int d = 0; d < 4; d++)
                {
                    int ni = i + (d == 0 ? 1 : d == 1 ? -1 : 0), nj = j + (d == 2 ? 1 : d == 3 ? -1 : 0);
                    float nt, nb; uint nc;
                    float other = bed.Thickness(ni, nj, out nt, out nb, out nc) ? nt : 0f;
                    worst = Mathf.Max(worst, Mathf.Abs(top - other) / limit);
                }
            }
        Console.WriteLine(what + ": " + columns + " columns, steepest step " + worst.ToString("F2") + " of the limit, " + bed.Unsettled + " awake");
        Check(worst < 1.05f, what + " rests at its angle of repose");
        Check(bed.Unsettled == 0, what + " leaves nothing to slump");
    }

    class Sphere : IBedShapes, IBedSink
    {
        public SandBed bed; public float volume; public int grains;
        public int Count { get { return 1; } }
        public void Extent(int i, out Vector3 min, out Vector3 max) { min = new Vector3(0.1f, 0.3f, 0.1f); max = new Vector3(0.7f, 0.9f, 0.7f); }
        public float Distance(int i, Vector3 p) { return (p - new Vector3(0.4f, 0.6f, 0.4f)).magnitude - 0.3f; }
        public void Union(out Vector3 min, out Vector3 max) { Extent(0, out min, out max); }
        public int Room { get { return 1000000; } }
        public void Release(Vector3 p, int m, uint c) { volume += bed.GrainVolume(m); grains++; }
    }

    // A machine at the foot of a tall pile is given back coarse grains, and no sand is lost doing it.
    static void DeepRelease()
    {
        SandMaterials.Params[] table = SandMaterials.Build();
        SandBed bed = new SandBed(); bed.Enabled = true; bed.SetGround(new PourGround()); bed.SetMaterials(table);
        float cell = SandBed.Cell;
        for (int i = 0; i < 8; i++) for (int j = 0; j < 8; j++) bed.AddVolume((i + 0.5f) * cell, (j + 0.5f) * cell, 8f * cell * cell, 0, 0xFFFFFFFFu);
        float before = bed.Volume;
        Sphere s = new Sphere(); s.bed = bed;
        for (int k = 0; k < 10; k++) bed.Release(s, s, 0.09f, 12000, 12000);
        float after = bed.Volume + s.volume;
        int ordinary = Mathf.RoundToInt(s.volume / bed.GrainVolume(0));
        Check(Math.Abs(after - before) < before * 0.002f, "sand released from a pile 8 m deep, in coarse grains, is all accounted for (" + after.ToString("F4") + " of " + before.ToString("F4") + " m^3)");
        Check(s.grains > 0 && s.grains * 5 < ordinary, "and takes far fewer grains than ordinary ones (" + s.grains + " instead of " + ordinary + ")");
    }

    // A brush's columns poured as a batch, on several threads, add up and stand like the same columns poured one by one.
    static void Batch()
    {
        SandMaterials.Params[] table = SandMaterials.Build();
        float cell = SandBed.Cell, area = cell * cell;
        int side = 120;
        for (int mode = 0; mode < 3; mode++)
        {
            SandBed bed = new SandBed(); bed.Enabled = true; bed.SetGround(new PourGround()); bed.SetMaterials(table);
            // a settled bed 0.3 m deep, a brush of dome-shaped layers poured on it ten times, and a few awkward columns
            for (int i = 0; i < side + 20; i++) for (int j = 0; j < side + 20; j++) bed.AddVolume((i - 10 + 0.5f) * cell, (j - 10 + 0.5f) * cell, 0.3f * area, 0, 0xFFFFFFFFu);
            while (bed.Unsettled > 0) bed.Relax(100000);
            float before = bed.Volume;
            int[] ci = new int[side * side], cj = new int[side * side]; float[] dv = new float[side * side]; int n = 0;
            for (int i = 0; i < side; i++) for (int j = 0; j < side; j++)
            {
                float u = (i - side / 2f) / (side / 2f), v = (j - side / 2f) / (side / 2f), d2 = u * u + v * v;
                if (d2 >= 1f) continue;
                ci[n] = i; cj[n] = j; dv[n] = 0.02f * (1f - d2); n++;
            }
            Workers.Parallel = mode != 1;
            float poured = 0f;
            for (int r = 0; r < 10; r++)
            {
                if (mode == 2) { for (int q = 0; q < n; q++) bed.PourVolume((ci[q] + 0.5f) * cell, (cj[q] + 0.5f) * cell, dv[q] * area, 0, 0xFFFFFFFFu); }
                else bed.PourColumns(ci, cj, dv, n, 0, 0xFFFFFFFFu, false);
                for (int q = 0; q < n; q++) poured += dv[q] * area;
            }
            Workers.Parallel = true;
            string what = mode == 0 ? "on several threads" : mode == 1 ? "on one thread" : "column by column";
            Check(Math.Abs(bed.Volume - before - poured) < poured * 0.001f, "a brush's columns poured " + what + " are all there (" + (bed.Volume - before).ToString("F3") + " of " + poured.ToString("F3") + " m^3)");
            float limit = bed.Repose(0) * cell, worst = 0f;
            for (int i = 0; i < side; i++) for (int j = 0; j < side; j++)
            {
                float t, b, t2, b2; uint c;
                if (!bed.Thickness(i, j, out t, out b, out c)) continue;
                if (bed.Thickness(i + 1, j, out t2, out b2, out c)) worst = Mathf.Max(worst, Mathf.Abs(t - t2) / limit);
                if (bed.Thickness(i, j + 1, out t2, out b2, out c)) worst = Mathf.Max(worst, Mathf.Abs(t - t2) / limit);
            }
            Check(worst < 1.05f, "and stand within the angle of repose " + what + " (steepest step " + worst.ToString("F2") + " of the limit)");
        }
    }

    class SpinningWheel : IBedShapes, IBedShapeMotion, IBedKickSink
    {
        public Vector3 c = new Vector3(0.5f, 0.55f, 0.5f), w = new Vector3(0f, 0f, -12f);
        public float r = 0.3f; public int grains, thrown; public float fastest;
        public int Count { get { return 1; } }
        public void Extent(int i, out Vector3 min, out Vector3 max) { Vector3 e = new Vector3(r, r, r); min = c - e; max = c + e; }
        public float Distance(int i, Vector3 p) { return (p - c).magnitude - r; }
        public void Union(out Vector3 min, out Vector3 max) { Extent(0, out min, out max); }
        public Vector3 SurfaceVelocity(int i, Vector3 p) { return Vector3.Cross(w, p - c); }
        public int Room { get { return 1000000; } }
        public void Release(Vector3 p, int m, uint col) { grains++; }
        public void Release(Vector3 p, int m, uint col, Vector3 v) { grains++; if (v.magnitude > 1.5f) thrown++; fastest = Mathf.Max(fastest, v.magnitude); }
    }

    // A wheel spinning in sand throws what it digs, at the speed of its surface; a sticky medium is thrown less.
    static void Kick()
    {
        SandMaterials.Params[] table = SandMaterials.Build();
        float cell = SandBed.Cell;
        int[] media = { SandMaterials.Sand, SandMaterials.Gravel, SandMaterials.Mud, SandMaterials.Glue };
        float[] fastest = new float[media.Length]; int[] thrown = new int[media.Length], grains = new int[media.Length];
        for (int m = 0; m < media.Length; m++)
        {
            SandBed bed = new SandBed(); bed.Enabled = true; bed.SetGround(new PourGround()); bed.SetMaterials(table);
            for (int i = -10; i < 20; i++) for (int j = -10; j < 20; j++) bed.AddVolume((i + 0.5f) * cell, (j + 0.5f) * cell, 0.6f * cell * cell, media[m], 0xFFFFFFFFu);
            while (bed.Unsettled > 0) bed.Relax(100000);
            SpinningWheel wheel = new SpinningWheel();
            for (int k = 0; k < 6; k++) bed.Release(wheel, wheel, 0.09f, 100000, 100000);
            fastest[m] = wheel.fastest; thrown[m] = wheel.thrown; grains[m] = wheel.grains;
        }
        Console.WriteLine("thrown faster than 1.5 m/s of the grains a spinning wheel digs: sand " + thrown[0] + "/" + grains[0] + " (fastest " + fastest[0].ToString("F1")
                          + "), gravel " + thrown[1] + "/" + grains[1] + ", mud " + thrown[2] + "/" + grains[2] + " (" + fastest[2].ToString("F1") + "), glue " + thrown[3] + "/" + grains[3] + " (" + fastest[3].ToString("F1") + ")");
        Check(thrown[0] > grains[0] / 4 && fastest[0] > 2f && fastest[0] < SandBed.KickMost + 0.01f, "a wheel spinning in dry sand throws it, at about the speed of its surface");
        Check(fastest[2] < fastest[0] * 0.6f, "mud is thrown less than sand");
        Check(fastest[3] < 1f, "and glue is not thrown at all");

        // A wheel resting on the sand, as on the bed's collider, and spinning: its rim moves level along the bottom of
        // the rut, and what it throws has to climb out of it -- thrown level, it ran into the sand beside the rut.
        {
            SandBed bed = new SandBed(); bed.Enabled = true; bed.SetGround(new PourGround()); bed.SetMaterials(table);
            for (int i = -10; i < 20; i++) for (int j = -10; j < 20; j++) bed.AddVolume((i + 0.5f) * cell, (j + 0.5f) * cell, 0.3f * cell * cell, SandMaterials.Sand, 0xFFFFFFFFu);
            while (bed.Unsettled > 0) bed.Relax(100000);
            Rim wheel = new Rim();
            wheel.c = new Vector3(0.5f, 0.3f + wheel.r + 0.01f, 0.5f);
            for (int k = 0; k < 6; k++) bed.Release(wheel, wheel, 0.09f, 100000, 100000);
            Console.WriteLine("a spinning wheel resting on dry sand: " + wheel.thrown + " grains thrown, " + wheel.climbing + " of them climbing at least half as fast as they go back");
            Check(wheel.thrown > 10 && wheel.climbing == wheel.thrown, "a wheel resting on the sand throws what it digs up out of its rut, not level into the sand");
            Check(wheel.highest < wheel.c.y, "and nothing it digs from under itself starts on top of it (highest " + wheel.highest.ToString("F2") + " m, the wheel's middle " + wheel.c.y.ToString("F2") + ")");
        }

        // The same wheel standing 15 cm over the sand it presses on, as on a collider not yet cooked again after the
        // sand under it went down: it still digs, and throws from its tread.
        {
            SandBed bed = new SandBed(); bed.Enabled = true; bed.SetGround(new PourGround()); bed.SetMaterials(table);
            for (int i = -10; i < 20; i++) for (int j = -10; j < 20; j++) bed.AddVolume((i + 0.5f) * cell, (j + 0.5f) * cell, 0.3f * cell * cell, SandMaterials.Sand, 0xFFFFFFFFu);
            while (bed.Unsettled > 0) bed.Relax(100000);
            Rim wheel = new Rim();
            wheel.c = new Vector3(0.5f, 0.3f + wheel.r + 0.15f, 0.5f);
            for (int k = 0; k < 6; k++)
            {
                bed.AddLoad(new Vector3(0.5f, 0.3f, 0.5f), Vector3.up, 0.001f, 0f); bed.Press(0.01f);
                bed.Release(wheel, wheel, 0.09f, 100000, 100000);
            }
            Console.WriteLine("a spinning wheel 15 cm over sand it presses on: " + wheel.thrown + " grains thrown, " + wheel.climbing + " climbing");
            Check(wheel.thrown > 5 && wheel.climbing == wheel.thrown, "a wheel standing on a collider behind the sand still digs what it presses on, and throws it up");
        }
    }

    class Rim : SpinningWheel, IBedKickSink
    {
        public int climbing; public float highest = float.MinValue;
        public new void Release(Vector3 p, int m, uint col)
        {
            base.Release(p, m, col);
            highest = Mathf.Max(highest, p.y);
        }
        public new void Release(Vector3 p, int m, uint col, Vector3 v)
        {
            base.Release(p, m, col, v);
            highest = Mathf.Max(highest, p.y);
            if (v.y >= 0.5f * Mathf.Sqrt(v.x * v.x + v.z * v.z)) climbing++;
        }
    }

    // Dust brushed beside a sand pile settles at dust's flat angle without cutting the sand pile down and laying it back
    // round as dust: another medium is fixed ground to the one being settled.
    static void OtherMediaStay()
    {
        SandMaterials.Params[] table = SandMaterials.Build();
        float cell = SandBed.Cell, area = cell * cell;
        SandBed bed = new SandBed(); bed.Enabled = true; bed.SetGround(new PourGround()); bed.SetMaterials(table);
        for (int n = 0; n < 200; n++) bed.PourVolume(0.06f, 0.06f, 0.03f * area, SandMaterials.Sand, 0xFFFFFFFFu);
        float[] before = new float[61 * 61];
        for (int i = -30; i <= 30; i++) for (int j = -30; j <= 30; j++)
        { float t, b; uint c; before[(i + 30) * 61 + j + 30] = bed.Thickness(i, j, out t, out b, out c) ? t : 0f; }
        int[] ci = new int[400], cj = new int[400]; float[] dv = new float[400]; int m = 0;
        for (int i = 6; i < 16; i++) for (int j = -5; j < 5; j++) { ci[m] = i; cj[m] = j; dv[m] = 0.05f; m++; }
        for (int f = 0; f < 30; f++)
        {
            bed.PourColumns(ci, cj, dv, m, SandMaterials.Dust, 0xFFFFFFFFu, true);
            if (bed.HasDeferred) bed.SettleDeferred(bed.SettleMargin());
        }
        int cut = 0, turned = 0;
        for (int i = -30; i <= 30; i++) for (int j = -30; j <= 30; j++)
        {
            float was = before[(i + 30) * 61 + j + 30];
            if (was <= 0f) continue;
            float t, b; uint c;
            float now = bed.Thickness(i, j, out t, out b, out c) ? t : 0f;
            if (now < was - 1e-3f) cut++;
            if (now <= was + 1e-3f && bed.MaterialAt(i, j) != SandMaterials.Sand) turned++;
        }
        Check(cut == 0 && turned == 0, "dust brushed and settled beside a sand pile leaves the pile as it was (" + cut + " columns cut down, " + turned + " turned to dust)");
    }

    class FarShape : IBedShapes
    {
        public int Count { get { return 1; } }
        public void Extent(int i, out Vector3 a, out Vector3 b) { a = new Vector3(-1f, 0f, -1f); b = new Vector3(1f, 2f, 1f); }
        public float Distance(int i, Vector3 p) { return (p - new Vector3(0f, 1f, 0f)).magnitude - 0.5f; }
        public void Union(out Vector3 a, out Vector3 b) { Extent(0, out a, out b); }
    }

    // A grain loose and slow for a long time goes into the column under it instead of being carried by the solver
    // for ever -- unless there is a block under it: one lying on a block a metre up stays on the block, rather than
    // falling through it. A fresh one is left alone either way.
    static void Stranded()
    {
        SandMaterials.Params[] table = SandMaterials.Build();
        SandBed bed = new SandBed(); bed.Enabled = true; bed.SetGround(new PourGround()); bed.SetMaterials(table);
        Grain[] g = new Grain[3];
        float[] x = { 0.05f, 1.5f, 0.35f };
        for (int k = 0; k < 3; k++)
        {
            g[k].pos = new Vector3(x[k], 1.52f, 0.05f); g[k].prevPos = g[k].pos; g[k].material = SandMaterials.Sand;
            g[k].life = 5400f; g[k].age = k < 2 ? SandBed.StrandedSeconds + 1f : 2f;
        }
        int took = bed.Absorb(g, 3, new FarShape(), 12, 600f, 0.09f, 100);
        bool onBlock = (g[0].material & SandFormat.FlagDead) != 0, beside = (g[1].material & SandFormat.FlagDead) != 0,
             young = (g[2].material & SandFormat.FlagDead) != 0;
        Check(took == 1 && !onBlock && beside && !young, "a grain stranded for " + SandBed.StrandedSeconds
            + " s goes into the sand under it, but not through a block it lies on, and a fresh one stays");
    }

    class Wheels : IBedShapes, IBedShapeMotion, IBedKickSink
    {
        public int n = 6; public int[] got = new int[6];
        Vector3 C(int i) { return new Vector3(0.5f + i * 1.2f, 0.3f + 0.3f + 0.01f, 0.5f); }
        public int Count { get { return n; } }
        public void Extent(int i, out Vector3 a, out Vector3 b) { Vector3 e = new Vector3(0.3f, 0.3f, 0.3f); a = C(i) - e; b = C(i) + e; }
        public float Distance(int i, Vector3 p) { return (p - C(i)).magnitude - 0.3f; }
        public void Union(out Vector3 a, out Vector3 b) { a = C(0) - Vector3.one; b = C(n - 1) + Vector3.one; }
        public Vector3 SurfaceVelocity(int i, Vector3 p) { return Vector3.Cross(new Vector3(0f, 0f, -20f), p - C(i)); }
        public int Room { get { return 1000000; } }
        public void Release(Vector3 p, int m, uint col) { Tally(p); }
        public void Release(Vector3 p, int m, uint col, Vector3 v) { Tally(p); }
        void Tally(Vector3 p) { int i = Mathf.Clamp(Mathf.RoundToInt((p.x - 0.5f) / 1.2f), 0, n - 1); got[i]++; }
    }

    // A machine's wheels, more than one step's grains can serve: each gets its turn, not the first few every step.
    static void EveryWheelGetsATurn()
    {
        SandMaterials.Params[] table = SandMaterials.Build();
        float cell = SandBed.Cell;
        SandBed bed = new SandBed(); bed.Enabled = true; bed.SetGround(new PourGround()); bed.SetMaterials(table);
        for (int i = -10; i < 70; i++) for (int j = -10; j < 20; j++) bed.AddVolume((i + 0.5f) * cell, (j + 0.5f) * cell, 0.3f * cell * cell, SandMaterials.Sand, 0xFFFFFFFFu);
        while (bed.Unsettled > 0) bed.Relax(100000);
        Wheels w = new Wheels();
        for (int k = 0; k < 24; k++) bed.Release(w, w, 0.09f, 100000, 8);
        int least = int.MaxValue;
        for (int i = 0; i < w.n; i++) least = Mathf.Min(least, w.got[i]);
        Console.WriteLine("six spinning wheels, eight grains a step between them: " + string.Join(", ", System.Array.ConvertAll(w.got, x => x.ToString())));
        Check(least > 0, "every wheel digs and throws, not only the first in the list");
    }

    // A pile settled every frame while it grows stands at its angle of repose all along, and keeps all its sand.
    static void SettledAsItGrows()
    {
        SandMaterials.Params[] table = SandMaterials.Build();
        float cell = SandBed.Cell, area = cell * cell;
        int radius = 40, frames = 240, n = 0;
        int[] ci = new int[(2 * radius + 1) * (2 * radius + 1)], cj = new int[ci.Length]; float[] dv = new float[ci.Length];
        double poured = 0;
        for (int i = -radius; i <= radius; i++) for (int j = -radius; j <= radius; j++)
        {
            float d = Mathf.Sqrt(i * i + j * j) / radius;
            if (d > 1f) continue;
            ci[n] = i; cj[n] = j; dv[n] = 0.06f * (1f - d * d); poured += dv[n] * area; n++;
        }
        SandBed bed = new SandBed(); bed.Enabled = true; bed.SetGround(new PourGround()); bed.SetMaterials(table);
        Stopwatch sw = new Stopwatch(); double settling = 0, worstFrame = 0;
        float limit = bed.Repose(0) * cell, steepest = 0f;
        for (int f = 0; f < frames; f++)
        {
            bed.PourColumns(ci, cj, dv, n, 0, 0xFFFFFFFFu, true);
            sw.Reset(); sw.Start();
            if (bed.HasDeferred) bed.SettleDeferred(bed.SettleMargin());
            double t = sw.Elapsed.TotalMilliseconds;
            settling += t; if (t > worstFrame) worstFrame = t;
            if (f % 60 == 59)
                for (int i = -radius * 4; i <= radius * 4; i++) for (int j = -radius * 4; j <= radius * 4; j++)
                {
                    float a, b, a2, b2; uint c;
                    if (!bed.Thickness(i, j, out a, out b, out c)) continue;
                    float o = bed.Thickness(i + 1, j, out a2, out b2, out c) ? a2 : 0f; steepest = Mathf.Max(steepest, Mathf.Abs(a - o) / limit);
                    o = bed.Thickness(i, j + 1, out a2, out b2, out c) ? a2 : 0f; steepest = Mathf.Max(steepest, Mathf.Abs(a - o) / limit);
                }
        }
        poured *= frames;
        Console.WriteLine("a circle brush held 4 s, settled every frame: " + (settling / frames).ToString("F1") + " ms a frame on average, " + worstFrame.ToString("F1") + " at worst; steepest step "
                          + steepest.ToString("F2") + " of the limit; " + bed.Volume.ToString("F2") + " of " + poured.ToString("F2") + " m^3");
        Check(Math.Abs(bed.Volume - poured) < poured * 0.001, "settled every frame, the pile keeps its sand");
        Check(steepest < 1.1f, "and stands at its angle of repose whenever it is looked at");
    }

    // A pile grown in one place, left where it lands and settled at the end, rests at its angle of repose with all its sand.
    static void Deferred()
    {
        SandMaterials.Params[] table = SandMaterials.Build();
        float cell = SandBed.Cell, area = cell * cell;
        int radius = 40, frames = 240;
        for (int style = 0; style < 2; style++)       // 0: a disc, 1: a ring
        {
            int[] ci = new int[4 * radius * radius], cj = new int[ci.Length]; float[] dv = new float[ci.Length]; int n = 0;
            for (int i = -radius; i <= radius; i++) for (int j = -radius; j <= radius; j++)
            {
                float d = Mathf.Sqrt(i * i + j * j) / radius;
                bool inside = style == 0 ? d <= 1f : (d > 0.7f && d < 0.95f);
                if (!inside) continue;
                ci[n] = i; cj[n] = j; dv[n] = 0.03f; n++;
            }
            double poured = n * 0.03 * frames * area;
            string what = style == 0 ? "a disc" : "a ring";

            // As the old brush did it: every column rolled to rest, every frame.
            SandBed rolled = new SandBed(); rolled.Enabled = true; rolled.SetGround(new PourGround()); rolled.SetMaterials(table);
            Stopwatch sw = Stopwatch.StartNew();
            for (int f = 0; f < frames; f++) rolled.PourColumns(ci, cj, dv, n, 0, 0xFFFFFFFFu, false);
            double rolling = sw.Elapsed.TotalMilliseconds;

            SandBed bed = new SandBed(); bed.Enabled = true; bed.SetGround(new PourGround()); bed.SetMaterials(table);
            sw.Reset(); sw.Start();
            for (int f = 0; f < frames; f++) bed.PourColumns(ci, cj, dv, n, 0, 0xFFFFFFFFu, true);
            double growing = sw.Elapsed.TotalMilliseconds;
            Check(bed.HasDeferred, what + " grown in place leaves something to settle");
            sw.Reset(); sw.Start();
            int margin = bed.SettleMargin();
            int changed = bed.SettleDeferred(margin);
            double settling = sw.Elapsed.TotalMilliseconds;
            Console.WriteLine(what + " " + (frames / 60f) + " s at " + (0.03f * 60f).ToString("F1") + " m/s: " + rolling.ToString("F0") + " ms rolled every frame; grown in place " + growing.ToString("F0")
                              + " ms and settled in " + settling.ToString("F0") + " ms (margin " + margin + " columns, " + changed + " changed)");
            Check(Math.Abs(bed.Volume - poured) < poured * 0.003, what + ": all the sand is still there after settling (" + bed.Volume.ToString("F2") + " of " + poured.ToString("F2") + " m^3)");
            float limit = bed.Repose(0) * cell, worst = 0f; int cols = 0;
            for (int i = -radius - margin; i <= radius + margin; i++) for (int j = -radius - margin; j <= radius + margin; j++)
            {
                float t, b, t2, b2; uint c;
                bool a = bed.Thickness(i, j, out t, out b, out c);
                if (!a) continue;
                cols++;
                float o; o = bed.Thickness(i + 1, j, out t2, out b2, out c) ? t2 : 0f; worst = Mathf.Max(worst, Mathf.Abs(t - o) / limit);
                o = bed.Thickness(i, j + 1, out t2, out b2, out c) ? t2 : 0f; worst = Mathf.Max(worst, Mathf.Abs(t - o) / limit);
            }
            Check(worst < 1.25f, what + ": the settled pile rests at its angle (steepest step " + worst.ToString("F2") + " of the limit over " + cols + " columns)");
            Check(settling + growing < rolling, what + ": growing and settling is quicker than rolling it all along");
        }
    }
}
