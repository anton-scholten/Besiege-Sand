using System;
using System.Diagnostics;
using UnityEngine;
using SandMod;

class BenchGround : ISandGround, IBedGround
{
    public bool Built { get { return true; } }
    public float Height(Vector3 p) { return 0f; }
    public float Height(float x, float z) { return 0f; }
    public Vector3 Normal(Vector3 p) { return Vector3.up; }
}
class BenchWheels : IBedShapes, IBedShapeMotion
{
    public Vector3[] c = new Vector3[4]; public Vector3 v, w; public float r = 0.3f;
    public int Count { get { return 4; } }
    public void Extent(int i, out Vector3 a, out Vector3 b) { Vector3 e = new Vector3(r, r, r); a = c[i] - e; b = c[i] + e; }
    public float Distance(int i, Vector3 p) { return (p - c[i]).magnitude - r; }
    public void Union(out Vector3 a, out Vector3 b) { a = c[0]; b = c[0]; for (int i = 0; i < 4; i++) { Vector3 x, y; Extent(i, out x, out y); a = Vector3.Min(a, x); b = Vector3.Max(b, y); } }
    public Vector3 SurfaceVelocity(int i, Vector3 p) { return v + Vector3.Cross(w, p - c[i]); }
}
class BenchSink : IBedKickSink
{
    public Grain[] g; public int count, cap;
    public int Room { get { return cap - count; } }
    public void Release(Vector3 p, int m, uint col, Vector3 vel) { if (count >= cap) return; Grain x = new Grain(); x.pos = p; x.prevPos = p - vel * 0.01f; x.vel = vel; x.material = (uint)m; x.colour = col; x.life = 5400f; x.age = 1f; g[count++] = x; }
    public void Release(Vector3 p, int m, uint col) { Release(p, m, col, Vector3.zero); }
    public void Reap() { int i = 0; while (i < count) { if ((g[i].material & SandFormat.FlagDead) != 0) { count--; g[i] = g[count]; } else i++; } }
}
/// <summary>
/// Where the simulation's time goes, headless, at the game's own cadence: a fixed update every 0.01 s (pressing,
/// slumping), a sand step every fourth (absorbing, digging, slumping, the solver), for four seconds of a four-wheeled
/// machine on a 30 m square of sand 30 cm deep -- parked, then driving at 2 m/s with its wheels spinning. Prints
/// milliseconds per second of game time for each part. What it cannot see is the engine's share: drawing, remeshing
/// the bed, cooking its colliders, finding objects in the scene (see docs/PERFORMANCE.md).
/// </summary>
class PerfBench
{
    static double press, relaxFixed, absorb, release, relaxStep, solve;
    public static int Main(string[] a)
    {
        Run("parked", 0f, 0f);
        Run("driving, spinning", 2f, 14f);
        return 0;
    }
    static void Run(string name, float speed, float spin)
    {
        press = relaxFixed = absorb = release = relaxStep = solve = 0;
        SandMaterials.Params[] table = SandMaterials.Build();
        BenchGround ground = new BenchGround();
        SandBed bed = new SandBed(); bed.Enabled = true; bed.SetGround(ground); bed.SetMaterials(table);
        bed.FillShape(SandBed.ShapeBox, new Vector3(0f, 0.15f, 0f), Quaternion.identity, new Vector3(15f, 0.15f, 15f), 0, 0xFFFFFFFFu);
        while (bed.Unsettled > 0) bed.Relax(100000);
        CpuSolver solver = new CpuSolver(); solver.Init(40000, table); solver.Ground = ground; solver.Bed = bed;
        BenchSink sink = new BenchSink(); sink.g = solver.Grains; sink.cap = 40000;
        BenchWheels wh = new BenchWheels(); wh.v = new Vector3(speed, 0, 0); wh.w = new Vector3(0, 0, -spin);
        Vector3[] off = { new Vector3(-0.8f, 0, -0.7f), new Vector3(0.8f, 0, -0.7f), new Vector3(-0.8f, 0, 0.7f), new Vector3(0.8f, 0, 0.7f) };
        Vector3 body = new Vector3(-5f, 0.3f + wh.r, 0f);
        Stopwatch t = new Stopwatch();
        long checks0 = bed.SlumpChecks, moves0 = bed.SlumpMoves;   // the bed's own settling, before the run, left out
        int seconds = 4, maxGrains = 0; float maxUnsettled = 0;
        Vector3 gravity = new Vector3(0f, -32.8f, 0f);
        for (int f = 0; f < seconds * 100; f++)
        {
            body += wh.v * 0.01f;
            for (int i = 0; i < 4; i++) { Vector3 p = body + off[i]; float top; if (!bed.TryTop(p.x, p.z, out top)) top = 0; wh.c[i] = new Vector3(p.x, top + wh.r - 0.02f, p.z); }
            t.Reset(); t.Start();
            for (int i = 0; i < 4; i++) bed.AddLoad(wh.c[i] - new Vector3(0, wh.r, 0), Vector3.up, 400f, 0f);
            bed.Press(0.01f);
            t.Stop(); press += t.Elapsed.TotalMilliseconds;
            t.Reset(); t.Start();
            if (bed.Unsettled > 0) bed.Relax(100, Mathf.Clamp(bed.Unsettled / 2000f, 0.3f, 2f));
            t.Stop(); relaxFixed += t.Elapsed.TotalMilliseconds;
            maxUnsettled = Math.Max(maxUnsettled, bed.Unsettled);
            if (f % 4 != 0) continue;
            sink.Reap();
            if (sink.count > 0)
            {
                t.Reset(); t.Start();
                SandShape[] sh = new SandShape[4];
                for (int i = 0; i < 4; i++) { sh[i].centre = wh.c[i]; sh[i].radius = wh.r; sh[i].rot = new Vector4(0, 0, 0, 1); sh[i].kindBlock = (uint)SandFormat.ShapeSphere; sh[i].movable = 1f; sh[i].pointVel = wh.v; }
                solver.ShapeSpin = new Vector3[] { wh.w, wh.w, wh.w, wh.w }; solver.BodyCentres = wh.c;
                solver.SetShapes(sh, 4, 4);
                Vector3 lo = sink.g[0].pos, hi = lo;
                for (int k = 1; k < sink.count; k++) { lo = Vector3.Min(lo, sink.g[k].pos); hi = Vector3.Max(hi, sink.g[k].pos); }
                Bounds b = new Bounds((lo + hi) * 0.5f, hi - lo); b.Expand(1f);
                solver.Step(0.04f, 2, 3, gravity, null, b, sink.count);
                t.Stop(); solve += t.Elapsed.TotalMilliseconds;
            }
            maxGrains = Math.Max(maxGrains, sink.count);
            t.Reset(); t.Start(); bed.Absorb(sink.g, sink.count, wh, 12, 600f, 0.09f, 3000); t.Stop(); absorb += t.Elapsed.TotalMilliseconds;
            bed.StepSeconds = 0.04f;
            t.Reset(); t.Start(); bed.Release(wh, sink, 0.09f, 40000, 1500); t.Stop(); release += t.Elapsed.TotalMilliseconds;
            t.Reset(); t.Start(); bed.Relax(300, 3f); t.Stop(); relaxStep += t.Elapsed.TotalMilliseconds;
        }
        solver.Dispose();
        Console.WriteLine(name + " (ms per second of game time, " + bed.FilledColumns + " columns, up to " + maxGrains + " grains, up to " + maxUnsettled + " unsettled):");
        Console.WriteLine("  press (every fixed update)      " + (press / seconds).ToString("F1"));
        Console.WriteLine("  slumping (every fixed update)   " + (relaxFixed / seconds).ToString("F1"));
        Console.WriteLine("  absorb (every sand step)        " + (absorb / seconds).ToString("F1"));
        Console.WriteLine("  release/dig (every sand step)   " + (release / seconds).ToString("F1"));
        Console.WriteLine("  slumping (every sand step)      " + (relaxStep / seconds).ToString("F1"));
        Console.WriteLine("    columns looked at for slumping " + (bed.SlumpChecks - checks0) / seconds + " a second, slumps made " + (bed.SlumpMoves - moves0) / seconds + " a second");
        Console.WriteLine("  solver (every sand step)        " + (solve / seconds).ToString("F1"));
    }
}
