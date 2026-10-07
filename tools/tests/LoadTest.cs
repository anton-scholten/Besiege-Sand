using System;
using UnityEngine;
using SandMod;

class Floor : ISandGround, IBedGround
{
    public bool Built { get { return true; } }
    public Vector3 Normal(Vector3 p) { return Vector3.up; }
    public float Height(Vector3 p) { return 0f; }
    public float Height(float x, float z) { return 0f; }
}

/// <summary>
/// A closed-loop pour: the real solver, bed and governor, with emission that coarsens the
/// way a sprayer does. It checks the three things the governor is for -- that the step cost
/// is brought inside the budget, that the grain count stays bounded, and that no sand is
/// lost or made on the way, whatever grain sizes it passed through.
/// </summary>
class LoadTest
{
    static int fails = 0;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) fails++; }

    public static int Main(string[] args)
    {
        float perSecond = args.Length > 0 ? float.Parse(args[0]) : 4000f;
        SandMaterials.Params[] table = SandMaterials.Build();
        Floor floor = new Floor();
        SandBed bed = new SandBed(); bed.Enabled = true; bed.SetGround(floor); bed.SetMaterials(table);
        int capacity = 40000;
        CpuSolver solver = new CpuSolver(); solver.Init(capacity, table); solver.Ground = floor; solver.Bed = bed;
        solver.SetShapes(new SandShape[1], 0, 0);
        SandGovernor gov = new SandGovernor(); gov.BudgetMs = 20f; gov.Reset(6000);

        Grain[] g = solver.Grains; int count = 0;
        System.Random rnd = new System.Random(11);
        System.Diagnostics.Stopwatch sw = new System.Diagnostics.Stopwatch();
        float dt = 0.04f, carry = 0f, emittedVolume = 0f, baseGv = bed.GrainVolume(0);
        int peak = 0; double over = 0; int overSteps = 0; double worstSlice = 0;
        Vector3 gravity = new Vector3(0, -32.8f, 0);

        for (int s = 0; s < 750; s++)       // 30 s of game time, then 10 s of settling
        {
            bool pouring = s < 500;
            if (pouring)
            {
                // a sprayer: perSecond ordinary grains' worth of sand, as fewer coarser grains
                int level = gov.Lod, row = SandMaterials.LodRow(0, level);
                float stands = table[row].radius / table[0].radius; stands = stands * stands * stands;
                carry += perSecond * dt / stands;
                int n = (int)carry; carry -= n;
                for (int k = 0; k < n && count < gov.SoftCapacity; k++)
                {
                    Grain q = new Grain();
                    q.pos = new Vector3(0.1f + (float)rnd.NextDouble() * 0.5f, 1.5f + (float)rnd.NextDouble() * 0.3f, 0.1f + (float)rnd.NextDouble() * 0.5f);
                    q.prevPos = q.pos; q.vel = new Vector3(0, -8f, 0); q.material = (uint)row; q.life = 5400f; q.age = 1f; q.colour = 0xFFFFFFFFu;
                    g[count++] = q; emittedVolume += bed.GrainVolume(row);
                }
            }
            if (count > 0)
            {
                Vector3 lo = g[0].pos, hi = lo;
                for (int i = 1; i < count; i++) { lo = Vector3.Min(lo, g[i].pos); hi = Vector3.Max(hi, g[i].pos); }
                Bounds b = new Bounds((lo + hi) * 0.5f, hi - lo); b.Expand(1f);
                // Advanced in four slices, one per fixed update, as the game does.
                int iters = gov.Iterations(3);
                int per = (solver.PassesFor(2, iters) + 3) / 4;
                double stepTotal = 0;
                sw.Reset(); sw.Start();
                solver.BeginStep(dt, 2, iters, gravity, null, b, count);
                sw.Stop(); stepTotal += sw.Elapsed.TotalMilliseconds;
                bool done = false;
                while (!done)
                {
                    sw.Reset(); sw.Start();
                    done = solver.Advance(per);
                    sw.Stop();
                    double ms = sw.Elapsed.TotalMilliseconds;
                    stepTotal += ms;
                    if (s > 300 && pouring && ms > worstSlice) worstSlice = ms;
                }
                gov.Record((float)stepTotal, count, capacity, dt);
                if (s > 300 && pouring) { over += stepTotal; overSteps++; }
            }
            bed.Absorb(g, count, null, 12, 600f, 0.09f, 3000);
            bed.Relax(1500);
            int i2 = 0; while (i2 < count) { if ((g[i2].material & SandFormat.FlagDead) != 0) { count--; g[i2] = g[count]; } else i2++; }
            if (count > peak) peak = count;

            if (s % 100 == 0 || s == 749)
                Console.WriteLine("t=" + (s * dt).ToString("F0") + "s pressure " + gov.Pressure + " lod " + gov.Lod + " grains " + count
                    + " (ceiling " + gov.SoftCapacity + ") step " + gov.CostMs.ToString("F1") + " ms, bed " + bed.Volume.ToString("F2") + " m^3, emitted " + emittedVolume.ToString("F2"));
        }

        float held = 0f;
        for (int i = 0; i < count; i++) held += bed.GrainVolume((int)(g[i].material & SandFormat.MaterialMask));
        float total = bed.Volume + held;
        float baseGrains = emittedVolume / baseGv;
        Console.WriteLine("poured " + emittedVolume.ToString("F2") + " m^3 (= " + baseGrains.ToString("F0") + " ordinary grains), peak live grains " + peak
            + ", mean step cost while pouring " + (over / Math.Max(overSteps, 1)).ToString("F1") + " ms, worst single slice " + worstSlice.ToString("F1") + " ms");
        Check(Math.Abs(total - emittedVolume) < emittedVolume * 0.03f, "all the sand poured is accounted for, bed plus grains (3%)");
        Check(peak <= capacity, "live grains stay within the array");
        Check(over / Math.Max(overSteps, 1) < gov.BudgetMs * 1.6, "mean step cost while pouring is held near the budget");
        Check(worstSlice < 25.0, "no single slice is a hitch (worst " + worstSlice.ToString("F1") + " ms)");
        solver.Dispose();
        Console.WriteLine(fails == 0 ? "ALL PASSED" : (fails + " FAILED"));
        return fails;
    }
}
