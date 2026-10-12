using System;
using System.Diagnostics;
using UnityEngine;
using SandMod;

class BenchGround : IBedGround
{
    public float Height(float x, float z) { return 0f; }
}

/// <summary>
/// A machine of wheels (spheres, the shape every block kind reduces to) in a grid,
/// rolling along +x at <see cref="speed"/> with its wheels spinning at <see cref="spin"/>.
/// The wheels are put back on the sand's surface every fixed update, as PhysX would,
/// and each reports its surface velocity, which is what digs.
/// </summary>
class BenchWheels : IBedShapes, IBedShapeMotion
{
    public Vector3[] c;          // where each wheel is, set every fixed update
    public Vector3[] look;       // where each is being looked for, if the bed is sweeping it
    public Vector3[] off;        // each wheel's place on the machine
    public Vector3 v, w;         // the machine's velocity, and its wheels' spin
    public float r = 0.3f;
    public int Count { get { return c.Length; } }
    public void Extent(int i, out Vector3 a, out Vector3 b)
    {
        Vector3 e = new Vector3(r, r, r); a = c[i] - e; b = c[i] + e;
    }
    public float Distance(int i, Vector3 p) { return (p - c[i] - look[i]).magnitude - r; }
    public void Shift(int i, Vector3 delta) { look[i] += delta; }
    public void Union(out Vector3 a, out Vector3 b)
    {
        a = c[0]; b = c[0];
        for (int i = 1; i < c.Length; i++)
        {
            Vector3 x, y; Extent(i, out x, out y);
            a = Vector3.Min(a, x); b = Vector3.Max(b, y);
        }
    }
    public Vector3 SurfaceVelocity(int i, Vector3 p) { return v + Vector3.Cross(w, p - c[i]); }
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
        Run("parked", 4, 0f, 0f);
        Run("driving, spinning", 4, 2f, 14f);
        Run("driving, 64 contacts", 64, 2f, 14f);
        Run("driving, 300 contacts", 300, 2f, 14f);
        return 0;
    }

    /// <summary>Wheels in a grid <paramref name="count"/> across, a hand's breadth apart.</summary>
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
        SandMaterials.Params[] table = SandMaterials.Build();
        BenchGround ground = new BenchGround();
        SandBed bed = new SandBed(); bed.Enabled = true; bed.SetGround(ground); bed.SetMaterials(table);
        bed.FillShape(SandBed.ShapeBox, new Vector3(0f, 0.15f, 0f), Quaternion.identity,
                      new Vector3(15f, 0.15f, 15f), 0, 0xFFFFFFFFu);
        while (bed.Unsettled > 0) bed.Relax(100000);

        BenchWheels wh = new BenchWheels();
        wh.off = Grid(wheels, wheels > 100 ? 0.5f : 0.6f);
        wh.c = new Vector3[wheels];
        wh.look = new Vector3[wheels];
        wh.v = new Vector3(speed, 0, 0);
        wh.w = new Vector3(0, 0, -spin);
        Vector3 body = new Vector3(-5f, 0.3f + wh.r, 0f);

        Stopwatch t = new Stopwatch();
        long checks0 = bed.SlumpChecks, moves0 = bed.SlumpMoves;   // the bed's own settling, before the run, left out
        int seconds = 4;
        for (int f = 0; f < seconds * 100; f++)
        {
            body += wh.v * 0.01f;
            for (int i = 0; i < wheels; i++)
            {
                Vector3 p = body + wh.off[i];
                float top;
                if (!bed.TryTop(p.x, p.z, out top)) top = 0f;
                wh.c[i] = new Vector3(p.x, top + wh.r - 0.02f, p.z);
            }
            t.Reset(); t.Start();
            for (int i = 0; i < wheels; i++) bed.AddLoad(wh.c[i] - new Vector3(0, wh.r, 0), Vector3.up, 400f, 0f);
            bed.Press(0.01f);
            t.Stop(); press += t.Elapsed.TotalMilliseconds;
            t.Reset(); t.Start();
            if (bed.Unsettled > 0) bed.Relax(100, Mathf.Clamp(bed.Unsettled / 2000f, 0.3f, 2f));
            t.Stop(); relaxFixed += t.Elapsed.TotalMilliseconds;
            if (f % 4 != 0) continue;
            t.Reset(); t.Start();
            ploughed += bed.Plough(wh, 0.09f, 40000);
            t.Stop(); plough += t.Elapsed.TotalMilliseconds;
            t.Reset(); t.Start();
            bed.Relax(300, 3f);
            t.Stop(); relaxStep += t.Elapsed.TotalMilliseconds;
        }
        checks = bed.SlumpChecks - checks0; moves = bed.SlumpMoves - moves0;
        Console.WriteLine(name + " (ms per second of game time, " + bed.FilledColumns
                          + " columns, " + wheels + " wheels):");
        Console.WriteLine("  press (every fixed update)      " + (press / seconds).ToString("F1"));
        Console.WriteLine("  slumping (every fixed update)   " + (relaxFixed / seconds).ToString("F1"));
        Console.WriteLine("  ploughing (every sand step)     " + (plough / seconds).ToString("F1"));
        Console.WriteLine("  slumping (every sand step)      " + (relaxStep / seconds).ToString("F1"));
        Console.WriteLine("    columns looked at for slumping " + (checks / seconds) + " a second, slumps made "
                          + (moves / seconds) + " a second, columns ploughed " + (ploughed / seconds) + " a second");
    }
}
