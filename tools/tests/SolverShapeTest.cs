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
        // The readback is summed over the step's substeps, each of which carried the whole
        // weight, so a pile at rest reads substeps times its weight. It used to read about
        // one weight -- half -- and the old check only asked for 15%, so nobody noticed that a
        // block under sand felt half of it.
        float load = -avgY / avgN / 2f;
        Console.WriteLine("lowest grain y " + minY + " (floor at 0, radius " + table[0].radius + "); grains below the floor: " + below);
        Console.WriteLine("mean load on the box over the last 50 steps: " + load.ToString("F0") + " N per substep; weight of the sand: " + weight.ToString("F0") + " N");
        bool ok = below == 0 && load > weight * 0.9f && load < weight * 1.1f;
        Console.WriteLine(ok ? "PASS sand rests on a movable collider and loads it with its own weight (within 10%)" : "FAIL sand on a movable collider should load it with its own weight");
        solver.Dispose();

        int fails = ok ? 0 : 1;
        fails += Wheel(table);
        fails += Plough(table);
        Console.WriteLine(fails == 0 ? "ALL PASSED" : (fails + " FAILED"));
        return fails;
    }

    static Bounds Around(Grain[] g, int n)
    {
        Vector3 lo = g[0].pos, hi = lo;
        for (int i = 1; i < n; i++) { lo = Vector3.Min(lo, g[i].pos); hi = Vector3.Max(hi, g[i].pos); }
        Bounds b = new Bounds((lo + hi) * 0.5f, hi - lo); b.Expand(1f); return b;
    }

    /// <summary>A layer of sand on flat ground, five grains deep.</summary>
    static int Layer(Grain[] g, float r, int across, int deep)
    {
        int n = 0; float sp = 2f * r * 1.02f;
        for (int x = 0; x < across; x++) for (int z = 0; z < deep; z++) for (int y = 0; y < 5; y++)
        {
            Grain q = new Grain();
            q.pos = new Vector3(x * sp - across * sp * 0.5f, r + y * sp * 0.9f, z * sp - deep * sp * 0.5f);
            q.prevPos = q.pos; q.material = 0; q.life = 5400f; q.age = 1f; g[n++] = q;
        }
        return n;
    }

    /// <summary>
    /// A wheel spinning in sand, its centre held still: its rim drags the sand backwards and
    /// the sand pushes it forwards. Before the solver knew a shape's spin, a wheel's surface
    /// was taken to move with its centre -- standing still -- so a wheel spinning in a pile
    /// threw nothing and got no grip.
    /// </summary>
    static int Wheel(SandMaterials.Params[] table)
    {
        CpuSolver s = new CpuSolver(); s.Init(8000, table); s.Ground = new Flat0();
        Grain[] g = s.Grains; float r = table[0].radius;
        int n = Layer(g, r, 40, 16);
        for (int i = 0; i < 30; i++) { s.SetShapes(new GpuShape[1], 0, 0); s.Step(0.04f, 2, 3, new Vector3(0, -32.8f, 0), null, Around(g, n), n); }

        GpuShape wheel = new GpuShape();
        wheel.centre = new Vector3(0f, 0.3f + 0.24f, 0f);   // resting on the top of the layer
        wheel.radius = 0.3f; wheel.rot = new Vector4(0, 0, 0, 1);
        wheel.kindBlock = (uint)SandFormat.ShapeSphere; wheel.movable = 1f;
        // Turning about -z: the bottom of the rim moves towards -x at 3 m/s, as on a wheel
        // driving towards +x.
        s.ShapeSpin = new Vector3[] { new Vector3(0f, 0f, -10f) };
        s.BodyCentres = new Vector3[] { wheel.centre };
        // Pressed slowly down into the sand, as its weight would: a wheel nothing holds against
        // the sand has nothing to grip with.
        wheel.pointVel = new Vector3(0f, -0.3f, 0f);
        float[] f = new float[6]; float push = 0f; int samples = 0;
        for (int i = 0; i < 20; i++)
        {
            wheel.centre.y -= 0.3f * 0.04f;
            s.BodyCentres[0] = wheel.centre;
            s.SetShapes(new GpuShape[] { wheel }, 1, 1);
            s.Step(0.04f, 2, 3, new Vector3(0, -32.8f, 0), null, Around(g, n), n);
            s.ReadForces(f, 1);
            if (i >= 5) { push += f[0]; samples++; }
        }
        push /= samples;
        float thrown = 0f; int behind = 0;
        for (int k = 0; k < n; k++) if (g[k].vel.x < thrown) thrown = g[k].vel.x;
        for (int k = 0; k < n; k++) if (g[k].vel.x < -1f) behind++;
        s.Dispose();
        bool ok = push > 50f && behind > 5;
        Console.WriteLine((ok ? "PASS" : "FAIL") + " a wheel spinning in sand throws it backwards (" + behind + " grains over 1 m/s, fastest " + (-thrown).ToString("F1") + ") and is pushed forwards (" + push.ToString("F0") + " N)");
        return ok ? 0 : 1;
    }

    /// <summary>
    /// A box dragged through sand at 2 m/s feels the sand hold it back. The solver used to
    /// hand back only the normal part of each contact, so a plough or a bucket felt the sand
    /// it shoved but nothing of the sand dragging along its sides and bottom.
    /// </summary>
    static int Plough(SandMaterials.Params[] table)
    {
        CpuSolver s = new CpuSolver(); s.Init(8000, table); s.Ground = new Flat0();
        Grain[] g = s.Grains; float r = table[0].radius;
        int n = Layer(g, r, 40, 20);
        for (int i = 0; i < 30; i++) { s.SetShapes(new GpuShape[1], 0, 0); s.Step(0.04f, 2, 3, new Vector3(0, -32.8f, 0), null, Around(g, n), n); }

        float[] f = new float[6]; float drag = 0f; int samples = 0; float fastest = 0f;
        for (int i = 0; i < 25; i++)
        {
            GpuShape box = new GpuShape();
            box.centre = new Vector3(-0.8f + i * 0.08f, 0.2f, 0f);
            box.halfExtent = new Vector3(0.1f, 0.2f, 0.3f); box.rot = new Vector4(0, 0, 0, 1);
            box.kindBlock = (uint)SandFormat.ShapeBox; box.movable = 1f;
            box.pointVel = new Vector3(2f, 0f, 0f);
            s.ShapeSpin = null; s.BodyCentres = new Vector3[] { box.centre };
            s.SetShapes(new GpuShape[] { box }, 1, 1);
            s.Step(0.04f, 2, 3, new Vector3(0, -32.8f, 0), null, Around(g, n), n);
            s.ReadForces(f, 1);
            if (i > 8) { drag += f[0]; samples++; }
            // Sideways only: sand heaped on the box falls into the trench behind it at the
            // speed a fall of that height gives, which is not the plough's doing.
            for (int k = 0; k < n; k++) fastest = Mathf.Max(fastest, new Vector2(g[k].vel.x, g[k].vel.z).magnitude);
        }
        drag /= samples;
        s.Dispose();
        bool ok = drag < -100f && fastest < 4f;
        Console.WriteLine((ok ? "PASS" : "FAIL") + " a box ploughing through sand at 2 m/s is held back (" + drag.ToString("F0") + " N along its motion) and does not fling it (fastest sideways " + fastest.ToString("F1") + " m/s)");
        return ok ? 0 : 1;
    }
}

class Flat0 : ISandGround
{
    public bool Built { get { return true; } }
    public float Height(Vector3 p) { return 0f; }
    public Vector3 Normal(Vector3 p) { return Vector3.up; }
}
