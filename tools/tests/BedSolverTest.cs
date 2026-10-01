using System;
using System.Collections.Generic;
using UnityEngine;
using SandMod;

class Flat : ISandGround, IBedGround
{
    public bool Built { get { return true; } }
    public float Height(Vector3 p) { return 0f; }
    public float Height(float x, float z) { return 0f; }
    public Vector3 Normal(Vector3 p) { return Vector3.up; }
}
class NoShapes : IBedShapes
{
    public int Count { get { return 0; } }
    public void Extent(int i, out Vector3 a, out Vector3 b) { a = b = Vector3.zero; }
    public float Distance(int i, Vector3 p) { return 1f; }
    public void Union(out Vector3 a, out Vector3 b) { a = b = Vector3.zero; }
}
class OneSphere : IBedShapes
{
    public Vector3 c; public float r;
    public int Count { get { return 1; } }
    public void Extent(int i, out Vector3 a, out Vector3 b) { Vector3 v = new Vector3(r,r,r); a = c - v; b = c + v; }
    public float Distance(int i, Vector3 p) { return (p - c).magnitude - r; }
    public void Union(out Vector3 a, out Vector3 b) { Extent(0, out a, out b); }
}
class World : IBedSink
{
    public Grain[] grains; public int count; public int cap;
    public int Room { get { return cap - count; } }
    public void Release(Vector3 p, int m, uint col)
    {
        if (count >= cap) return;
        Grain g = new Grain(); g.pos = p; g.prevPos = p; g.material = (uint)m; g.colour = col;
        g.life = 5400f; g.age = 0.5f; grains[count++] = g;
    }
    public void Reap()
    {
        int i = 0;
        while (i < count) { if ((grains[i].material & SandFormat.FlagDead) != 0) { count--; grains[i] = grains[count]; } else i++; }
    }
}

class BedSolver
{
    static int fails = 0;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) fails++; }

    static Bounds GrainBounds(World w)
    {
        Vector3 lo = w.grains[0].pos, hi = lo;
        for (int i = 1; i < w.count; i++) { Vector3 p = w.grains[i].pos;
            lo = Vector3.Min(lo, p); hi = Vector3.Max(hi, p); }
        Bounds b = new Bounds((lo + hi) * 0.5f, hi - lo); b.Expand(1f); return b;
    }

    public static int Main(string[] args)
    {
        SandMaterials.Params[] table = SandMaterials.Build();
        Flat ground = new Flat();
        SandBed bed = new SandBed(); bed.Enabled = true; bed.SetGround(ground); bed.SetMaterials(table);

        int cap = 12000;
        CpuSolver solver = new CpuSolver();
        solver.Init(cap, table);
        solver.Ground = ground; solver.Bed = bed;
        solver.SetShapes(new GpuShape[1], 0, 0);

        World w = new World(); w.grains = solver.Grains; w.cap = cap;
        System.Random rnd = new System.Random(7);
        int N = 4000;
        for (int i = 0; i < N; i++)
        {
            Grain g = new Grain();
            g.pos = new Vector3((float)rnd.NextDouble() * 0.6f, 0.1f + (float)rnd.NextDouble() * 1.2f, (float)rnd.NextDouble() * 0.6f);
            g.prevPos = g.pos; g.material = 0; g.colour = 0xFFA0B0C0u; g.life = 5400f; w.grains[w.count++] = g;
        }
        float gv = bed.GrainVolume(0);
        float expect = N * gv;
        NoShapes none = new NoShapes();

        Vector3 gravity = new Vector3(0f, -32.8f, 0f);
        float dt = 0.04f;
        System.Diagnostics.Stopwatch sw = new System.Diagnostics.Stopwatch();
        long solverMs = 0, bedMs = 0;
        int steps = 500;
        for (int s = 0; s < steps; s++)
        {
            w.Reap();
            if (w.count <= 0 && !bed.Active) break;
            if (w.count > 0)
            {
                sw.Reset(); sw.Start();
                solver.Step(dt, 2, 3, gravity, null, GrainBounds(w), w.count);
                sw.Stop(); solverMs += sw.ElapsedMilliseconds;
            }
            sw.Reset(); sw.Start();
            bed.Absorb(w.grains, w.count, none, 12, 600f, 0.09f, 3000);
            bed.Relax(1500);
            sw.Stop(); bedMs += sw.ElapsedMilliseconds;

            if (s % 50 == 0 || s == steps - 1)
                Console.WriteLine("step " + s + ": grains " + w.count + ", bed columns " + bed.FilledColumns + ", bed volume " + bed.Volume + " (+grains " + (w.count * gv) + ")");
        }
        w.Reap();
        float total = bed.Volume + w.count * gv;
        Console.WriteLine("solver " + solverMs + " ms total, bed " + bedMs + " ms total over " + steps + " steps");
        Console.WriteLine("final: grains " + w.count + " of " + N + ", bed volume " + bed.Volume + ", expected total " + expect + ", got " + total);

        bool finite = true;
        for (int i = 0; i < w.count; i++) { Vector3 p = w.grains[i].pos; if (float.IsNaN(p.x) || float.IsNaN(p.y) || float.IsNaN(p.z) || p.y < -1f) finite = false; }
        Check(finite, "no NaN grains and none fell through the ground");
        Check(Math.Abs(total - expect) < expect * 0.03f, "total volume conserved through settle (3%)");
        Check(w.count < N * 0.15f, "at least 85% of the grains became bed (" + w.count + " left)");
        float top; bed.TryTop(0.3f, 0.3f, out top);
        Console.WriteLine("pile top at centre " + top);
        Check(top > 0.05f && top < 1.0f, "pile height plausible");

        // Round trip: a sphere pushed into the pile wakes it; take it away and it goes back.
        float before = bed.Volume;
        OneSphere sph = new OneSphere(); sph.c = new Vector3(0.3f, top - 0.02f, 0.3f); sph.r = 0.12f;
        int released = bed.Release(sph, w, 0.09f, 100000);
        Console.WriteLine("sphere released " + released + " grains; bed " + before + " -> " + bed.Volume);
        Check(released > 20, "sphere wakes a patch of the pile");
        int steps2 = 400;
        for (int s = 0; s < steps2; s++)
        {
            w.Reap();
            if (w.count > 0) solver.Step(dt, 2, 3, gravity, null, GrainBounds(w), w.count);
            bed.Absorb(w.grains, w.count, none, 12, 600f, 0.09f, 3000);
            bed.Relax(1500);
        }
        w.Reap();
        float after = bed.Volume + w.count * gv;
        Console.WriteLine("after round trip: grains " + w.count + ", bed " + bed.Volume + ", total " + after + " vs " + (before));
        Check(Math.Abs(after - before) < before * 0.03f, "release then re-absorb conserves volume (3%)");
        Check(w.count < released * 0.3f + 10, "released grains settle back into the bed (" + w.count + " left)");

        solver.Dispose();
        Console.WriteLine(fails == 0 ? "ALL PASSED" : (fails + " FAILED"));
        return fails;
    }
}
