using System;
using System.Diagnostics;
using UnityEngine;
using SandMod;

class BenchGround : IBedGround
{
    public float Height(float x, float z) { return 0f; }
}

/// <summary>
/// A machine of Besiege wheels in a grid, rolling along +x at <see cref="v"/> with its wheels
/// turning at <see cref="w"/>. A wheel is what the game's own Wheel block is (read from its prefab
/// in level0): no sphere and no convex mesh -- the block's convex mesh collider is disabled -- but
/// eight boxes 1.938 x 0.323 x 0.319 m, crossed in pairs and turned every 22.5 degrees about the
/// axle, 0.189 m out along it from the block's centre. Each box is its own shape to the bed, as
/// it is to ShapeFeed, so a wheel is eight shapes. The axle is along z; the wheels sit on the sand
/// with their lowest bar 2 cm into it, put back every fixed update as PhysX would.
/// </summary>
class BenchWheels : IBedShapes, IBedShapeMotion
{
    public const int Bars = 8;
    public const float Radius = 0.969f, HalfWide = 0.1615f, HalfDeep = 0.1595f, Out = 0.189f;
    public Vector3[] c;          // each wheel's centre, set every fixed update
    public Vector3[] off;        // each wheel's place on the machine
    public Vector3 v, w;         // the machine's velocity, and its wheels' spin (about z)
    public float turned;         // how far the wheels have turned, radians
    Vector3[] shift;             // per shape, from Shift
    float[] cos = new float[Bars], sin = new float[Bars];
    /// <summary>The bars' turn, worked out once a fixed update as BedShapes caches each shape's
    /// rotation once a step: not a sine and a cosine in every distance test.</summary>
    public void Turn(float by)
    {
        turned += by;
        for (int k = 0; k < Bars; k++) { float a = turned + k * (Mathf.PI / Bars); cos[k] = Mathf.Cos(a); sin[k] = Mathf.Sin(a); }
    }
    public int Count { get { return c.Length * Bars; } }
    Vector3 Centre(int i)
    {
        if (shift == null || shift.Length != Count) shift = new Vector3[Count];
        return c[i / Bars] + new Vector3(0f, 0f, Out) + shift[i];
    }
    public void Extent(int i, out Vector3 a, out Vector3 b)
    {
        Vector3 m = Centre(i);
        float co = Mathf.Abs(cos[i % Bars]), si = Mathf.Abs(sin[i % Bars]);
        Vector3 e = new Vector3(Radius * co + HalfWide * si, Radius * si + HalfWide * co, HalfDeep);
        a = m - e; b = m + e;
    }
    public float Distance(int i, Vector3 p)
    {
        Vector3 q = p - Centre(i);
        float co = cos[i % Bars], si = sin[i % Bars];
        float x = q.x * co + q.y * si, y = -q.x * si + q.y * co;
        float dx = Mathf.Abs(x) - Radius, dy = Mathf.Abs(y) - HalfWide, dz = Mathf.Abs(q.z) - HalfDeep;
        float ox = Mathf.Max(dx, 0f), oy = Mathf.Max(dy, 0f), oz = Mathf.Max(dz, 0f);
        return Mathf.Sqrt(ox * ox + oy * oy + oz * oz) + Mathf.Min(Mathf.Max(dx, Mathf.Max(dy, dz)), 0f);
    }
    public void Shift(int i, Vector3 delta) { Centre(i); shift[i] += delta; }
    public void Union(out Vector3 a, out Vector3 b)
    {
        a = c[0]; b = c[0];
        for (int i = 0; i < Count; i++)
        {
            Vector3 x, y; Extent(i, out x, out y);
            a = Vector3.Min(a, x); b = Vector3.Max(b, y);
        }
    }
    public Vector3 SurfaceVelocity(int i, Vector3 p) { return v + Vector3.Cross(w, p - c[i / Bars]); }

    /// <summary>The contact points PhysX reports for one wheel standing on the bed's collider: the
    /// corners and middle of the bottom bar's face, across the tread and along it, six in all.</summary>
    public static readonly Vector2[] Contacts =
    {
        new Vector2(-0.12f, Out - 0.14f), new Vector2(0f, Out - 0.14f), new Vector2(0.12f, Out - 0.14f),
        new Vector2(-0.12f, Out + 0.14f), new Vector2(0f, Out + 0.14f), new Vector2(0.12f, Out + 0.14f),
    };
}

/// <summary>
/// Where the simulation's time goes, headless, at the game's own cadence: a fixed
/// update every 0.01 s (pressing, slumping), a sand step every fourth (ploughing,
/// slumping), for four seconds of a machine on a 30 m square of sand 30 cm deep --
/// parked, then driving at 2 m/s with its wheels spinning, then the same with the
/// contact count of a big machine (64) and a huge one (300), the rows of the press
/// table in docs/PERFORMANCE.md. Prints milliseconds per second of game time for
/// each part, and how much sand each part moved. What it cannot see is the engine's
/// share: drawing, remeshing the bed, cooking its colliders, finding objects in the
/// scene.
///
/// The bed runs with its flow unlimited (slumping moves at once, not at the medium's
/// speed): the game advances the bed's clock, which a bed on its own has no reason to.
/// </summary>
class PerfBench
{
    static double press, relaxFixed, plough, relaxStep;
    static long checks, moves, ploughed;

    public static int Main(string[] a)
    {
        Run("a car parked", 4, 0f, 0f);
        Run("a car driving at 4 m/s, wheels rolling", 4, 4f, 4f / BenchWheels.Radius);
        Run("a car at 2 m/s, wheels spinning", 4, 2f, 14f);
        Run("16 wheels driving at 4 m/s", 16, 4f, 4f / BenchWheels.Radius);
        Run("64 wheels driving at 4 m/s", 64, 4f, 4f / BenchWheels.Radius);
        return 0;
    }

    /// <summary>Wheels in a grid <paramref name="count"/> across, <paramref name="gap"/> apart.</summary>
    static Vector3[] Grid(int count, float gap)
    {
        int nx = Mathf.CeilToInt(Mathf.Sqrt(count));
        int nz = Mathf.CeilToInt((float)count / nx);
        Vector3[] off = new Vector3[count];
        int n = 0;
        for (int z = 0; z < nz && n < count; z++)
            for (int x = 0; x < nx && n < count; x++)
                off[n++] = new Vector3((x - (nx - 1) * 0.5f) * gap, 0f, (z - (nz - 1) * 0.5f) * gap);
        return off;
    }

    static void Run(string name, int wheels, float speed, float spin)
    {
        press = relaxFixed = plough = relaxStep = 0;
        ploughed = 0;
        SandMaterials.Params[] table = SandMaterials.Build();
        BenchGround ground = new BenchGround();
        SandBed bed = new SandBed(); bed.Enabled = true; bed.SetGround(ground); bed.SetMaterials(table);
        bed.FillShape(SandBed.ShapeBox, new Vector3(0f, 0.15f, 0f), Quaternion.identity,
                      new Vector3(15f, 0.15f, 15f), 0, 0xFFFFFFFFu);
        while (bed.Unsettled > 0) bed.Relax(100000);

        BenchWheels wh = new BenchWheels();
        wh.Turn(0f);
        wh.off = Grid(wheels, 2.2f);
        wh.c = new Vector3[wheels];
        wh.v = new Vector3(speed, 0, 0);
        wh.w = new Vector3(0, 0, -spin);
        Vector3 body = new Vector3(-8f, 0f, 0f);
        float load = 400f / BenchWheels.Contacts.Length;

        Stopwatch t = new Stopwatch();
        long checks0 = bed.SlumpChecks, moves0 = bed.SlumpMoves;   // the bed's own settling, before the run, left out
        int seconds = 4;
        for (int f = 0; f < seconds * 100; f++)
        {
            body += wh.v * 0.01f;
            wh.Turn(spin * 0.01f);
            for (int i = 0; i < wheels; i++)
            {
                Vector3 p = body + wh.off[i];
                float top;
                if (!bed.TryTop(p.x, p.z + BenchWheels.Out, out top)) top = 0f;
                wh.c[i] = new Vector3(p.x, top + BenchWheels.Radius - 0.02f, p.z);
            }
            t.Reset(); t.Start();
            for (int i = 0; i < wheels; i++)
                for (int k = 0; k < BenchWheels.Contacts.Length; k++)
                {
                    Vector2 o = BenchWheels.Contacts[k];
                    bed.AddLoad(wh.c[i] + new Vector3(o.x, -BenchWheels.Radius + 0.02f, o.y), Vector3.up, load, 0f);
                }
            FixedPress(bed, f);
            t.Stop(); press += t.Elapsed.TotalMilliseconds;
            t.Reset(); t.Start();
            if (bed.Unsettled > 0) bed.Relax(100, Mathf.Clamp(bed.Unsettled / 2000f, 0.3f, 2f));
            t.Stop(); relaxFixed += t.Elapsed.TotalMilliseconds;
            if (f % 4 != 0) continue;
            t.Reset(); t.Start();
            StepPress(bed);
            ploughed += bed.Plough(wh, 0.09f, 40000);
            t.Stop(); plough += t.Elapsed.TotalMilliseconds;
            t.Reset(); t.Start();
            bed.Relax(300, 3f);
            t.Stop(); relaxStep += t.Elapsed.TotalMilliseconds;
        }
        checks = bed.SlumpChecks - checks0; moves = bed.SlumpMoves - moves0;
        Console.WriteLine(name + " (ms per second of game time, " + bed.FilledColumns
                          + " columns, " + wheels + " wheels, " + wheels * BenchWheels.Bars + " shapes, "
                          + wheels * BenchWheels.Contacts.Length + " contacts):");
        Console.WriteLine("  press (every fixed update)      " + (press / seconds).ToString("F1"));
        Console.WriteLine("  slumping (every fixed update)   " + (relaxFixed / seconds).ToString("F1"));
        Console.WriteLine("  ploughing (every sand step)     " + (plough / seconds).ToString("F1"));
        Console.WriteLine("  slumping (every sand step)      " + (relaxStep / seconds).ToString("F1"));
        Console.WriteLine("  in all                          " + ((press + relaxFixed + plough + relaxStep) / seconds).ToString("F1"));
        Console.WriteLine("    columns looked at for slumping " + (checks / seconds) + " a second, slumps made "
                          + (moves / seconds) + " a second, columns ploughed " + (ploughed / seconds) + " a second");
    }

    // Where the field presses: every fixed update, as SandField.FixedUpdate does.
    static void FixedPress(SandBed bed, int f) { bed.Press(0.01f); }
    static void StepPress(SandBed bed) { }
}
