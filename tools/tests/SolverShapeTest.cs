using System;
using UnityEngine;
using SandMod;

class FlatLow : ISandGround
{
    public bool Built { get { return true; } }
    public float Height(Vector3 p) { return -5f; }
    public Vector3 Normal(Vector3 p) { return Vector3.up; }
}

class SolverShape
{
    public static int Main(string[] a)
    {
        SandMaterials.Params[] table = SandMaterials.Build();
        int cap = 4000;
        CpuSolver solver = new CpuSolver();
        solver.Init(cap, table);
        solver.Ground = new FlatLow();
        solver.Bed = null;

        // One movable box, top face at y = 0, centred under the grains. Body slot 0.
        GpuShape box = new GpuShape();
        box.centre = new Vector3(0.3f, -0.25f, 0.3f);
        box.halfExtent = new Vector3(1f, 0.25f, 1f);
        box.radius = 0f;
        box.kindBlock = (uint)SandFormat.ShapeBox | (0u << 8);
        box.rot = new Vector4(0f, 0f, 0f, 1f);
        box.pointVel = Vector3.zero;
        box.movable = 1f;
        GpuShape[] shapes = new GpuShape[] { box };
        solver.SetShapes(shapes, 1, 1);

        System.Random rnd = new System.Random(3);
        int N = 600;
        Grain[] g = solver.Grains;
        for (int i = 0; i < N; i++)
        {
            Grain q = new Grain();
            q.pos = new Vector3(0.1f + (float)rnd.NextDouble() * 0.4f, 0.2f + (float)rnd.NextDouble() * 0.8f, 0.1f + (float)rnd.NextDouble() * 0.4f);
            q.prevPos = q.pos; q.material = 0; q.life = 5400f; q.age = 1f;
            g[i] = q;
        }

        Vector3 gravity = new Vector3(0f, -32.8f, 0f);
        float dt = 0.04f;
        float[] f = new float[6];
        float avgY = 0f; int avgN = 0;
        int steps = 300;
        for (int s = 0; s < steps; s++)
        {
            Vector3 lo = g[0].pos, hi = lo;
            for (int i = 1; i < N; i++) { lo = Vector3.Min(lo, g[i].pos); hi = Vector3.Max(hi, g[i].pos); }
            Bounds b = new Bounds((lo + hi) * 0.5f, hi - lo); b.Expand(1f);
            solver.Step(dt, 2, 3, gravity, null, b, N);
            solver.ReadForces(f, 1);
            if (s >= steps - 50) { avgY += f[1]; avgN++; }
            if (s % 60 == 0) Console.WriteLine("step " + s + " force y " + f[1].ToString("F1") + " N");
        }

        float minY = 1e9f; int below = 0;
        for (int i = 0; i < N; i++) { if (g[i].pos.y < minY) minY = g[i].pos.y; if (g[i].pos.y < -0.01f) below++; }
        float mass = 1f / table[0].invMass;
        float weight = N * mass * 32.8f;
        Console.WriteLine("lowest grain y " + minY + " (floor at 0, radius " + table[0].radius + "); grains below the floor: " + below);
        Console.WriteLine("mean downward load on the box over the last 50 steps: " + (-avgY / avgN) + " N; weight of the sand: " + weight + " N");
        bool ok = below == 0 && Math.Abs(-avgY / avgN) > weight * 0.15f;
        Console.WriteLine(ok ? "PASS sand rests on a movable collider and loads it" : "FAIL");
        solver.Dispose();
        return ok ? 0 : 1;
    }
}
