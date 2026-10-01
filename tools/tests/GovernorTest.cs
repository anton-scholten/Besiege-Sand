using System;
using System.Collections.Generic;
using UnityEngine;
using SandMod;

class FlatGround : IBedGround { public float Height(float x, float z) { return 0f; } }
class Sink : IBedSink
{
    public List<int> mat = new List<int>();
    public int Room { get { return 1000000; } }
    public void Release(Vector3 p, int m, uint c) { mat.Add(m); }
}
class OneSphere : IBedShapes
{
    public Vector3 c; public float r;
    public int Count { get { return 1; } }
    public void Extent(int i, out Vector3 a, out Vector3 b) { Vector3 v = new Vector3(r, r, r); a = c - v; b = c + v; }
    public float Distance(int i, Vector3 p) { return (p - c).magnitude - r; }
    public void Union(out Vector3 a, out Vector3 b) { Extent(0, out a, out b); }
}

class GovernorTest
{
    static int fails = 0;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) fails++; }

    static void Run(SandGovernor g, float msFactor, float seconds, int count, int capacity)
    {
        float dt = 0.04f;
        for (float t = 0f; t < seconds; t += dt) g.Record(g.BudgetMs * msFactor, count, capacity, dt);
    }

    public static int Main(string[] a)
    {
        // ---- governor
        {
            // 5000 grains at half the budget: 2 us a grain, so about 10000 are affordable.
            SandGovernor g = new SandGovernor(); g.Reset(6000);
            Run(g, 0.5f, 30f, 5000, 40000);
            Check(g.SoftCapacity > 9000 && g.SoftCapacity <= 10100, "the ceiling is what the measured cost per grain affords (" + g.SoftCapacity + ")");
            Check(g.Pressure == 0, "half full and cheap: full quality, no change of detail");
        }
        {
            // 8000 grains costing 1.4x the budget: only ~5700 are affordable, so the count
            // is over the ceiling and the grain size has to go up.
            SandGovernor g = new SandGovernor(); g.Reset(6000);
            int last = 0; bool monotonic = true; float dt = 0.04f; int firstRise = -1; int tAt = 0;
            for (float t = 0f; t < 20f; t += dt, tAt++)
            {
                g.Record(g.BudgetMs * 1.4f, 8000, 40000, dt);
                if (g.Pressure < last) monotonic = false;
                if (g.Pressure > last && firstRise < 0) firstRise = tAt;
                last = g.Pressure;
            }
            Check(monotonic, "pressure only rises while the count is over what is affordable");
            Check(g.Pressure == SandGovernor.MaxPressure, "and reaches the top (" + g.Pressure + ")");
            Check(firstRise >= 0 && firstRise <= 3, "the first rung comes within a few steps, not seconds (step " + firstRise + ")");
            Check(g.SoftCapacity < 6500, "the ceiling has dropped to what is affordable (" + g.SoftCapacity + ")");
            Check(g.Lod == 3 && g.Iterations(3) == 1, "top rung is the coarsest grain and one iteration");
            Check(g.Iterations(1) == 1, "iterations never exceed what was asked for");
            // the pile has settled: far fewer grains, cheap steps. Must come back down, slowly.
            Run(g, 0.2f, 60f, 1500, 40000);
            Check(g.Pressure == 0, "pressure falls back to full quality once the load has gone (" + g.Pressure + ")");
        }
        {
            // rungs are not climbed faster than the hold allows
            SandGovernor g = new SandGovernor(); g.Reset(6000);
            float dt = 0.04f; int rises = 0, prev = 0; float t = 0f;
            for (; t < 3f; t += dt)
            {
                g.Record(g.BudgetMs * 3f, 20000, 40000, dt);
                if (g.Pressure > prev) { rises++; prev = g.Pressure; }
            }
            Check(rises <= 5, "rungs are held between rises: " + rises + " in 3 s");
        }
        {
            SandGovernor g = new SandGovernor(); g.Reset(40000);
            Run(g, 5f, 10f, 20000, 40000);
            Check(g.SoftCapacity < 6000, "a step far over budget cuts the admission ceiling hard (" + g.SoftCapacity + ")");
            Check(g.SoftCapacity >= SandGovernor.MinCapacity, "but never below the floor");
            Run(g, 0.4f, 120f, 3000, 40000);
            Check(g.SoftCapacity > 6000, "and the ceiling recovers when steps are cheap (" + g.SoftCapacity + ")");
        }
        {
            // a steady, moderate load -- about half of what is affordable -- is left alone
            SandGovernor g = new SandGovernor(); g.Reset(6000);
            Run(g, 0.5f, 120f, 5000, 40000);
            Check(g.Pressure == 0, "a steady half-full load does not change the detail level");
        }

        // ---- the coarse grain table
        SandMaterials.Params[] t2 = SandMaterials.Build();
        Check(t2.Length == SandMaterials.FirstVariant, "table holds the five media and their coarse rows (" + t2.Length + ")");
        bool order = true, capped = true, mass = true;
        for (int m = 0; m < SandMaterials.Count; m++)
        {
            float prev = t2[m].radius;
            for (int l = 1; l <= SandMaterials.LodLevels; l++)
            {
                SandMaterials.Params p = t2[SandMaterials.LodRow(m, l)];
                if (p.radius < prev - 1e-6f) order = false;
                if (p.radius > Math.Max(SandMaterials.MaxLodRadius, t2[m].radius) + 1e-6f) capped = false;
                if (p.invMass > t2[m].invMass + 1e-6f) mass = false;
                prev = p.radius;
            }
        }
        Check(order, "grains get no smaller at a coarser level");
        Check(capped, "no coarse grain is bigger than the cap");
        Check(mass, "a coarse grain is heavier than an ordinary one");
        int wetBase = t2[SandMaterials.Sand].wetMaterial;
        Check(t2[SandMaterials.LodRow(SandMaterials.Sand, 2)].wetMaterial == SandMaterials.LodRow(wetBase, 2),
              "coarse sand that gets wet becomes coarse wet sand, not a fine grain");
        Check(SandMaterials.LodRow(SandMaterials.FirstVariant + 3, 2) == SandMaterials.FirstVariant + 3, "an emitter's own variant is left alone");
        Check(SandMaterials.LodRow(2, 0) == 2, "level zero is the ordinary row");

        // ---- every row of the table, used or not, has a look the renderer can index
        {
            int len = SandMaterials.FirstVariant + 59;
            int[] looks = SandMaterials.InitialLooks(len);
            bool valid = looks.Length == len;
            for (int i = 0; i < looks.Length; i++)
                if (looks[i] < 0 || looks[i] >= SandMaterials.Tiles.Length || looks[i] >= SandMaterials.Tints.Length) valid = false;
            Check(valid, "every row, including the unclaimed emitter rows, has a valid look");
            Check(looks[SandMaterials.LodRow(SandMaterials.Snow, 2)] == SandMaterials.Snow, "a coarse row looks like its medium");
        }

        // ---- the bed keeps volume exact when grains are coarse
        {
            SandBed bed = new SandBed(); bed.Enabled = true; bed.SetGround(new FlatGround()); bed.SetMaterials(t2);
            int row = SandMaterials.LodRow(SandMaterials.Sand, 3);
            float ratio = t2[row].radius / t2[SandMaterials.Sand].radius;
            float expectRatio = ratio * ratio * ratio;
            float gv0 = bed.GrainVolume(SandMaterials.Sand), gv3 = bed.GrainVolume(row);
            Check(Math.Abs(gv3 / gv0 - expectRatio) < 0.001f, "a coarse grain stands for its radius ratio cubed in sand (" + (gv3 / gv0).ToString("F1") + "x)");

            Grain[] gr = new Grain[900];
            for (int i = 0; i < gr.Length; i++)
            {
                gr[i].pos = new Vector3(0.1f + (i % 30) * 0.1f, t2[row].radius, 0.1f + (i / 30) * 0.1f);
                gr[i].material = (uint)row | SandFormat.FlagAsleep | ((uint)SandFormat.RestMax << SandFormat.RestShift);
                gr[i].life = 5400f; gr[i].colour = 0xFFFFFFFFu;
            }
            int got, rounds = 0; do { got = bed.Absorb(gr, gr.Length, null, 12, 600f, 0.09f, 100000); rounds++; } while (got > 0 && rounds < 20);
            Check(Math.Abs(bed.Volume - gr.Length * gv3) < gr.Length * gv3 * 0.001f, "absorbing coarse grains adds exactly their volume");

            OneSphere sph = new OneSphere(); sph.c = new Vector3(1.5f, 0.1f, 1.5f); sph.r = 0.4f;
            Sink sink = new Sink();
            float before = bed.Volume;
            int n = bed.Release(sph, sink, 0.09f, 1000000);
            bool coarseBack = true; for (int i = 0; i < sink.mat.Count; i++) if (sink.mat[i] != row) coarseBack = false;
            Check(n > 0 && coarseBack, "sand a shape wakes comes back as the same coarse grains (" + n + ")");
            Check(Math.Abs(before - (bed.Volume + n * gv3)) < before * 0.03f, "and the volume still balances");
        }

        Console.WriteLine(fails == 0 ? "ALL PASSED" : (fails + " FAILED"));
        return fails;
    }
}
