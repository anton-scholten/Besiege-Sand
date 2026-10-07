using System;
using System.Threading;
using UnityEngine;
using SandMod;

class RG : IBedGround { public float Height(float x, float z) { return 0f; } }

/// <summary>
/// The solver's worker threads read the bed (TryTop) while the main thread waits. A lookup cache shared
/// between them answered one height in six wrongly; this reads from six threads and compares with one.
/// </summary>
class BedThreadTest
{
    public static int Main(string[] a)
    {
        SandBed bed = new SandBed(); bed.Enabled = true; bed.SetGround(new RG()); bed.SetMaterials(SandMaterials.Build());
        // a landscape whose height is a different, known number in every chunk
        float cell = SandBed.Cell;
        for (int i = -64; i < 64; i++) for (int j = -64; j < 64; j++)
        {
            int cx = i >> 4, cz = j >> 4;
            float h = 0.2f + 0.1f * ((cx * 7 + cz * 13) & 15);
            bed.AddVolume((i + 0.5f) * cell, (j + 0.5f) * cell, h * cell * cell, 0, 0xFFFFFFFFu);
        }
        while (bed.Unsettled > 0) bed.Relax(100000);   // settle first, so heights are stable
        // reference, single thread
        int N = 4000; float[] xs = new float[N], zs = new float[N], want = new float[N];
        System.Random rnd = new System.Random(5);
        for (int k = 0; k < N; k++) { xs[k] = ((float)rnd.NextDouble() * 120f - 60f) * cell; zs[k] = ((float)rnd.NextDouble() * 120f - 60f) * cell; float h; bed.TryTop(xs[k], zs[k], out h); want[k] = h; }
        int threads = 6, bad = 0, total = 0;
        Thread[] ts = new Thread[threads];
        for (int t = 0; t < threads; t++)
        {
            int id = t;
            ts[t] = new Thread(delegate ()
            {
                int mybad = 0, mytotal = 0;
                for (int rep = 0; rep < 200; rep++)
                    for (int k = 0; k < N; k++)
                    {
                        int q = (k * 31 + id * 977 + rep) % N;
                        float h; bed.TryTop(xs[q], zs[q], out h);
                        mytotal++; if (Mathf.Abs(h - want[q]) > 1e-4f) mybad++;
                    }
                lock (ts) { bad += mybad; total += mytotal; }
            });
            ts[t].Start();
        }
        foreach (Thread t in ts) t.Join();
        Console.WriteLine((bad == 0 ? "PASS" : "FAIL") + " six threads reading the bed at once all get the single-thread answer (" + bad + " wrong of " + total + ")");
        Console.WriteLine(bad == 0 ? "ALL PASSED" : "1 FAILED");
        return bad == 0 ? 0 : 1;
    }
}
