using System;
using System.Collections.Generic;
using UnityEngine;
using SandMod;

class Floor : ISandGround
{
    public bool Built { get { return true; } }
    public Vector3 Normal(Vector3 p) { return Vector3.up; }
    public float Height(Vector3 p) { return 0f; }
}

/// <summary>
/// What the sand has to do as a material, on the real solver: hold a stack up, stop
/// sliding, make a heap, collapse a block without throwing it, and settle without
/// bouncing. Each of these was broken at some point -- grains could not stand on one
/// another at all, so a pile spread into a sheet and a placed block of sand went off like
/// a charge -- and none of it showed up in anything that measured volume or speed alone.
/// </summary>
class SolverPhysicsTest
{
    static int fails = 0;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) fails++; }

    static readonly Vector3 gravity = new Vector3(0, -32.8f, 0);

    static CpuSolver Make(SandMaterials.Params[] table, int capacity)
    {
        CpuSolver s = new CpuSolver(); s.Init(capacity, table); s.Ground = new Floor();
        s.SetShapes(new GpuShape[1], 0, 0); return s;
    }
    static Grain Grain0(Vector3 p, Vector3 v)
    {
        Grain q = new Grain(); q.pos = p; q.prevPos = p - v * 0.02f; q.vel = v; q.material = 0;
        q.life = 5400f; q.age = 1f; q.colour = 0xFFFFFFFFu; return q;
    }
    static void Step(CpuSolver s, Grain[] g, int n)
    {
        Vector3 lo = g[0].pos, hi = lo;
        for (int k = 1; k < n; k++) { lo = Vector3.Min(lo, g[k].pos); hi = Vector3.Max(hi, g[k].pos); }
        Bounds b = new Bounds((lo + hi) * 0.5f, hi - lo); b.Expand(1f);
        s.Step(0.04f, 2, 3, gravity, null, b, n);
    }

    public static int Main(string[] a)
    {
        SandMaterials.Params[] table = SandMaterials.Build();
        float r = table[0].radius;

        // 1. a grain sliding on the ground stops in a short distance
        {
            CpuSolver s = Make(table, 10); Grain[] g = s.Grains;
            g[0] = Grain0(new Vector3(0, r, 0), new Vector3(2f, 0, 0));
            for (int i = 0; i < 8; i++) Step(s, g, 1);
            Check(g[0].pos.x < 0.12f && g[0].vel.magnitude < 0.05f, "a grain sliding at 2 m/s stops within 12 cm (" + g[0].pos.x.ToString("F3") + " m)");
            s.Dispose();
        }

        // 2. a grain in the pocket of three stays on top of them
        {
            CpuSolver s = Make(table, 10); Grain[] g = s.Grains;
            float d = 2f * r * 1.001f;
            g[0] = Grain0(new Vector3(0, r, 0), Vector3.zero);
            g[1] = Grain0(new Vector3(d, r, 0), Vector3.zero);
            g[2] = Grain0(new Vector3(d * 0.5f, r, d * 0.866f), Vector3.zero);
            float h = Mathf.Sqrt(Mathf.Max(4f * r * r - (d * 0.5774f) * (d * 0.5774f), 0f));
            g[3] = Grain0(new Vector3(d * 0.5f, r + h, d * 0.2887f), Vector3.zero);
            for (int i = 0; i < 80; i++) Step(s, g, 4);
            Check(g[3].pos.y > r + h * 0.9f, "a grain in the pocket of three stays on top of them (height " + g[3].pos.y.ToString("F3") + ", perch " + (r + h).ToString("F3") + ")");
            s.Dispose();
        }

        // 3. a pour makes a heap, not a sheet
        {
            CpuSolver s = Make(table, 2000); Grain[] g = s.Grains; int n = 600; System.Random rnd = new System.Random(8);
            for (int i = 0; i < n; i++)
                g[i] = Grain0(new Vector3((float)(rnd.NextDouble() - 0.5) * 0.12f, 0.15f + (float)rnd.NextDouble() * 0.9f, (float)(rnd.NextDouble() - 0.5) * 0.12f), Vector3.zero);
            for (int i = 0; i < 200; i++) Step(s, g, n);
            float top = 0f; int onFloor = 0;
            for (int i = 0; i < n; i++) { if (g[i].pos.y > top) top = g[i].pos.y; if (g[i].pos.y < r * 1.5f) onFloor++; }
            Console.WriteLine("   600 grains poured: height " + top.ToString("F2") + " m, " + onFloor + " on the floor");
            Check(top > 0.10f, "a pour of 600 grains piles at least a few layers high (" + top.ToString("F2") + " m)");
            Check(onFloor < n * 0.9f, "and the grains are not all on the floor (" + onFloor + " of " + n + ")");
            s.Dispose();
        }

        // 4. a one metre block of loose grains. KNOWN LIMIT: a column sixteen grains deep
        // compresses under Besiege's gravity faster than three iterations can pass the weight
        // down it, and the bottom squirts out -- a quick burst of about a third of the grains
        // that no friction setting changes. That is why a region placed in the level is held
        // as bed, not grains (SandBed.FillShape), and this only records what the fallback does:
        // the block still ends as a believable heap and comes to rest.
        {
            float half = 0.5f, spacing = r * 2f * 1.1f, jitter = Math.Max(spacing - 2f * r, 0f) * 0.45f;
            CpuSolver s = Make(table, 6000); Grain[] g = s.Grains; int n = 0; System.Random rnd = new System.Random(2);
            int cells = Mathf.FloorToInt(half * 2f / spacing);
            for (int x = 0; x < cells; x++) for (int y = 0; y < cells; y++) for (int z = 0; z < cells; z++)
            {
                Vector3 p = new Vector3((x + 0.5f) * spacing - half, (y + 0.5f) * spacing, (z + 0.5f) * spacing - half)
                          + new Vector3((float)rnd.NextDouble() * 2f - 1f, (float)rnd.NextDouble() * 2f - 1f, (float)rnd.NextDouble() * 2f - 1f) * jitter;
                if (p.y < r) continue;
                g[n++] = Grain0(p, Vector3.zero);
            }
            int mostFast = 0; float peak = 0f, widest = 0f, top = 0f, mean = 0f, lateFastest = 0f;
            for (int i = 0; i < 160; i++)
            {
                Step(s, g, n);
                int fast = 0; float m = 0f;
                for (int k = 0; k < n; k++) { float v = g[k].vel.magnitude; if (v > 3f) fast++; if (v > m) m = v; }
                if (fast > mostFast) mostFast = fast;
                if (m > peak) peak = m;
                if (i >= 100 && m > lateFastest) lateFastest = m;
            }
            for (int k = 0; k < n; k++)
            {
                widest = Math.Max(widest, new Vector2(g[k].pos.x, g[k].pos.z).magnitude);
                top = Math.Max(top, g[k].pos.y); mean += g[k].vel.magnitude;
            }
            mean /= n;
            Console.WriteLine("   loose block of " + n + " grains (the known limit): at most " + mostFast + " over 3 m/s at once, peak " + peak.ToString("F1") + " m/s; ends " + (widest * 2f).ToString("F2") + " m wide, " + top.ToString("F2") + " m tall, mean speed " + mean.ToString("F3"));
            Check(peak < 7.0f, "even the loose block's burst stays bounded (peak " + peak.ToString("F1") + " m/s)");
            Check(widest * 2f < 3.2f, "it spreads like a collapsing block, not a liquid (" + (widest * 2f).ToString("F2") + " m wide)");
            Check(top > 0.15f, "and leaves a heap, not a film (" + top.ToString("F2") + " m)");
            Check(mean < 0.15f && lateFastest < 1.5f, "and is nearly at rest after six seconds (mean " + mean.ToString("F3") + ", fastest " + lateFastest.ToString("F2") + " m/s)");
            s.Dispose();
        }

        // 5. sand dropped on the ground settles without hopping
        {
            CpuSolver s = Make(table, 1000); Grain[] g = s.Grains; int n = 400; System.Random rnd = new System.Random(4);
            for (int i = 0; i < n; i++)
                g[i] = Grain0(new Vector3((float)rnd.NextDouble() * 0.3f, 0.5f + (float)rnd.NextDouble() * 0.6f, (float)rnd.NextDouble() * 0.3f), Vector3.zero);
            double upCount = 0; int samples = 0; float fastestUp = 0f;
            for (int i = 0; i < 120; i++)
            {
                Step(s, g, n);
                if (i >= 40)
                {
                    for (int k = 0; k < n; k++) { float vy = g[k].vel.y; if (vy > 0.3f) { upCount++; if (vy > fastestUp) fastestUp = vy; } }
                    samples++;
                }
            }
            double avg = upCount / samples;
            Check(avg < 5.0 && fastestUp < 1.0f, "settled sand does not hop (it was 20 of 400 at up to 1.5 m/s): " + avg.ToString("F1") + " of 400 grains moving up per step, fastest " + fastestUp.ToString("F2") + " m/s");
            s.Dispose();
        }

        Console.WriteLine(fails == 0 ? "ALL PASSED" : (fails + " FAILED"));
        return fails;
    }
}
