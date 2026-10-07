using System;
using System.Collections.Generic;
using UnityEngine;
using SandMod;

class FlatGround : IBedGround { public float Height(float x, float z) { return 0f; } }

class SphereShapes : IBedShapes
{
    public List<Vector3> c = new List<Vector3>(); public List<float> r = new List<float>();
    public int Count { get { return c.Count; } }
    public void Extent(int i, out Vector3 min, out Vector3 max)
    { Vector3 v = new Vector3(r[i], r[i], r[i]); min = c[i] - v; max = c[i] + v; }
    public float Distance(int i, Vector3 p) { return (p - c[i]).magnitude - r[i]; }
    public void Union(out Vector3 min, out Vector3 max)
    { min = new Vector3(1e9f,1e9f,1e9f); max = -min;
      for (int i = 0; i < c.Count; i++) { Vector3 a,b; Extent(i,out a,out b); min = Vector3.Min(min,a); max = Vector3.Max(max,b);} }
}

class Sink : IBedSink
{
    public int room = 1000000; public List<Vector3> pos = new List<Vector3>(); public List<int> mat = new List<int>();
    public int Room { get { return room - pos.Count; } }
    public void Release(Vector3 p, int m, uint col) { pos.Add(p); mat.Add(m); }
}

class BedUnit
{
    static int fails = 0;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) fails++; }

    static SandBed NewBed()
    {
        SandBed b = new SandBed(); b.Enabled = true; b.SetGround(new FlatGround());
        b.SetMaterials(SandMaterials.Build()); return b;
    }

    static Grain[] Lattice(int nx, int nz, int layers, float spacing, float r)
    {
        List<Grain> l = new List<Grain>();
        for (int k = 0; k < layers; k++)
          for (int z = 0; z < nz; z++)
            for (int x = 0; x < nx; x++)
            {
                Grain g = new Grain();
                g.pos = new Vector3(0.01f + x * spacing, r + k * 2f * r, 0.01f + z * spacing);
                g.material = SandFormat.FlagAsleep | ((uint)SandFormat.RestMax << SandFormat.RestShift);
                g.life = 5400f; g.colour = 0xFFFFFFFFu; l.Add(g);
            }
        return l.ToArray();
    }

    public static int Main(string[] a)
    {
        SandMaterials.Params[] table = SandMaterials.Build();
        float r = table[0].radius;

        // 1. absorb: conservation, stacking
        {
            SandBed bed = NewBed();
            Grain[] gr = Lattice(20, 20, 5, 0.06f, r);
            int total = gr.Length, absorbed = 0, rounds = 0, got;
            do { got = bed.Absorb(gr, gr.Length, null, 12, 600f, 0.09f, 100000); absorbed += got; rounds++; } while (got > 0 && rounds < 60);
            float expect = total * bed.GrainVolume(0);
            Console.WriteLine("absorbed " + absorbed + "/" + total + " in " + rounds + " rounds, volume " + bed.Volume + " expect " + expect + " columns " + bed.FilledColumns);
            Check(absorbed == total, "all stacked grains absorbed");
            Check(Math.Abs(bed.Volume - expect) < expect * 0.001f, "absorb conserves volume");
            float h; bool ok = bed.TryTop(0.6f, 0.6f, out h);
            Console.WriteLine("top at centre " + h);
            Check(ok && h > 0.15f && h < 0.45f, "surface height plausible for 5 layers");
            Vector3 n = bed.Normal(0.6f, 0.6f);
            Check(n.y > 0.99f, "flat pile normal is up");
            float hb; Check(!bed.TryTop(5f, 5f, out hb), "no pile over bare ground");
        }

        // 2. grains above the surface are refused; grains near shapes are refused
        {
            SandBed bed = NewBed();
            Grain[] gr = Lattice(2, 1, 1, 0.06f, r);
            gr[0].pos.y = 1.0f;                      // floating
            SphereShapes sh = new SphereShapes(); sh.c.Add(new Vector3(gr[1].pos.x, 0.06f, gr[1].pos.z)); sh.r.Add(0.03f);
            int got = bed.Absorb(gr, gr.Length, sh, 12, 600f, 0.09f, 100);
            Check(got == 0, "floating grain and grain beside a shape are not absorbed");
            gr[1].material &= ~(SandFormat.FlagAsleep | SandFormat.RestMask);
            gr[0].pos.y = r;
            got = bed.Absorb(gr, gr.Length, null, 12, 600f, 0.09f, 100);
            Check(got == 1, "awake grains are not absorbed, asleep one is");
        }

        // 3. relax: steep spike slumps, volume conserved, slopes bounded
        {
            SandBed bed = NewBed();
            float V = 0.05f;
            bed.AddVolume(0.06f, 0.06f, V, 0, 0xFFFFFFFFu);
            float before = bed.Volume;
            int iters = 0;
            for (; iters < 20000; iters++) bed.Relax(4000);
            float after = bed.Volume;
            Console.WriteLine("relax volume " + before + " -> " + after + ", columns " + bed.FilledColumns);
            Check(Math.Abs(before - after) < before * 0.001f, "relax conserves volume");
            // max slope between orthogonal neighbours
            float maxSlope = 0f; int cols = 0;
            for (int j = -40; j <= 40; j++) for (int i = -40; i <= 40; i++)
            {
                float t, b, t2, b2; uint c;
                float ta = 0f, tb = 0f;
                bool sa = bed.Thickness(i, j, out t, out b, out c); if (sa) ta = t;
                bool sb = bed.Thickness(i + 1, j, out t2, out b2, out c); if (sb) tb = t2;
                if (sa || sb) { float s = Math.Abs(ta - tb) / SandBed.Cell; if (s > maxSlope) maxSlope = s; }
                if (sa) cols++;
            }
            float limit = table[0].friction * 1.15f;
            Console.WriteLine("max slope " + maxSlope + " limit " + limit + " columns " + cols);
            Check(maxSlope <= limit + 0.07f, "slopes within angle of repose after relaxing");
            Check(cols > 20, "spike spread into a cone");
        }

        // 4. release: sphere in a pile
        {
            SandBed bed = NewBed();
            Grain[] gr = Lattice(20, 20, 5, 0.06f, r);
            int got, rounds = 0; do { got = bed.Absorb(gr, gr.Length, null, 12, 600f, 0.09f, 100000); rounds++; } while (got > 0 && rounds < 60);
            float before = bed.Volume;
            SphereShapes sh = new SphereShapes(); sh.c.Add(new Vector3(0.6f, 0.2f, 0.6f)); sh.r.Add(0.25f);
            Sink sink = new Sink();
            int n = bed.Release(sh, sink, 0.09f, 1000000, 1000000);
            float after = bed.Volume;
            float moved = n * bed.GrainVolume(0);
            Console.WriteLine("released " + n + " grains; volume " + before + " -> " + after + " + grains " + moved);
            Check(n > 50, "sphere wakes sand");
            Check(Math.Abs(before - (after + moved)) < before * 0.02f, "release conserves volume");
            bool inside = true;
            for (int i = 0; i < sink.pos.Count; i++)
            { Vector3 p = sink.pos[i]; if (p.x < 0f || p.x > 1.3f || p.z < 0f || p.z > 1.3f || p.y < 0f || p.y > 1f) inside = false; }
            Check(inside, "released grains lie within the pile");
            // a second release with the shape in the same place finds little left to do
            int n2 = bed.Release(sh, sink, 0.09f, 1000000, 1000000);
            Console.WriteLine("second release " + n2);
            Check(n2 <= n / 4, "second release finds little new");
        }

        // 5. release respects Room
        {
            SandBed bed = NewBed();
            Grain[] gr = Lattice(20, 20, 5, 0.06f, r);
            int got, rounds = 0; do { got = bed.Absorb(gr, gr.Length, null, 12, 600f, 0.09f, 100000); rounds++; } while (got > 0 && rounds < 60);
            float before = bed.Volume;
            SphereShapes sh = new SphereShapes(); sh.c.Add(new Vector3(0.6f, 0.2f, 0.6f)); sh.r.Add(0.25f);
            Sink sink = new Sink(); sink.room = 30;
            int n = bed.Release(sh, sink, 0.09f, 1000000, 1000000);
            float after = bed.Volume;
            Check(n <= 30 && n > 0, "release stops at the room available (" + n + ")");
            Check(Math.Abs(before - (after + n * bed.GrainVolume(0))) < before * 0.02f, "capped release still conserves volume");
        }

        // 6. performance sanity
        {
            SandBed bed = NewBed();
            Grain[] gr = Lattice(150, 150, 1, 0.06f, r);
            System.Diagnostics.Stopwatch w = System.Diagnostics.Stopwatch.StartNew();
            int got, rounds = 0; do { got = bed.Absorb(gr, gr.Length, null, 12, 600f, 0.09f, 100000); rounds++; } while (got > 0 && rounds < 10);
            w.Stop();
            Console.WriteLine("absorbed " + gr.Length + " grains in " + w.ElapsedMilliseconds + " ms (" + bed.ChunkCount + " chunks)");
            Check(w.ElapsedMilliseconds < 500, "absorbing 22500 grains is fast");
        }

        // 7. filling a shape: the sand holds the shape's volume, whatever the shape and pose
        {
            Func<int, Vector3, Quaternion, Vector3, float> fill = delegate(int kind, Vector3 c, Quaternion q, Vector3 half)
            {
                SandBed b = NewBed();
                b.FillShape(kind, c, q, half, 0, 0xFFFFFFFFu);
                return b.Volume;
            };
            Quaternion id = new Quaternion(0f, 0f, 0f, 1f);
            Vector3 unit = new Vector3(0.5f, 0.5f, 0.5f);
            float box = fill(SandBed.ShapeBox, new Vector3(3f, 0.5f, 3f), id, unit);
            Check(Math.Abs(box - 1f) < 0.03f, "a 1 m box of sand holds a cubic metre (" + box.ToString("F3") + ")");
            float lifted = fill(SandBed.ShapeBox, new Vector3(3f, 4f, 3f), id, unit);
            Check(Math.Abs(lifted - box) < 0.001f, "the same box in mid-air holds the same sand; it drops onto the ground");
            float sphere = fill(SandBed.ShapeEllipsoid, new Vector3(3f, 0.5f, 3f), id, unit);
            float sphereExact = 4f / 3f * Mathf.PI * 0.125f;
            Check(Math.Abs(sphere - sphereExact) < sphereExact * 0.04f, "a sphere holds 4/3 pi r^3 (" + sphere.ToString("F3") + " vs " + sphereExact.ToString("F3") + ")");
            float cyl = fill(SandBed.ShapeCylinder, new Vector3(3f, 0.5f, 3f), id, unit);
            float cylExact = Mathf.PI * 0.25f * 1f;
            Check(Math.Abs(cyl - cylExact) < cylExact * 0.04f, "a cylinder holds pi r^2 h (" + cyl.ToString("F3") + " vs " + cylExact.ToString("F3") + ")");
            // turned 45 degrees about the vertical axis, and tipped 30 about a horizontal one
            float s45 = Mathf.Sin(22.5f * Mathf.Deg2Rad), c45 = Mathf.Cos(22.5f * Mathf.Deg2Rad);
            Quaternion yaw = new Quaternion(0f, s45, 0f, c45);
            float turned = fill(SandBed.ShapeBox, new Vector3(3f, 0.5f, 3f), yaw, unit);
            Check(Math.Abs(turned - 1f) < 0.04f, "a box turned 45 degrees still holds a cubic metre (" + turned.ToString("F3") + ")");
            float s15 = Mathf.Sin(15f * Mathf.Deg2Rad), c15 = Mathf.Cos(15f * Mathf.Deg2Rad);
            Quaternion tip = new Quaternion(s15, 0f, 0f, c15);
            float tipped = fill(SandBed.ShapeBox, new Vector3(3f, 0.5f, 3f), tip, unit);
            Check(Math.Abs(tipped - 1f) < 0.04f, "a box tipped 30 degrees still holds a cubic metre (" + tipped.ToString("F3") + ")");
            float flat = fill(SandBed.ShapeBox, new Vector3(3f, 0.1f, 3f), id, new Vector3(2f, 0.1f, 1f));
            Check(Math.Abs(flat - 1.6f) < 0.05f, "a 4 x 0.2 x 2 slab holds 1.6 cubic metres (" + flat.ToString("F3") + ")");
        }

        // 8. a filled block slumps to its angle of repose without losing sand, and wakes round a shape
        {
            SandBed bed = NewBed();
            bed.FillShape(SandBed.ShapeBox, new Vector3(3f, 0.5f, 3f), new Quaternion(0f, 0f, 0f, 1f), new Vector3(0.5f, 0.5f, 0.5f), 0, 0xFFFFFFFFu);
            float before = bed.Volume; int cols0 = bed.FilledColumns;
            for (int i = 0; i < 4000; i++) bed.Relax(2000);
            Console.WriteLine("   block of sand: " + cols0 + " columns -> " + bed.FilledColumns + " after slumping, volume " + before.ToString("F3") + " -> " + bed.Volume.ToString("F3"));
            Check(Math.Abs(bed.Volume - before) < before * 0.002f, "slumping conserves the volume of a placed block");
            Check(bed.FilledColumns > cols0 * 2, "and it spreads out into a heap");
            float top = 0f; bed.TryTop(3f, 3f, out top);
            Check(top < 0.9f && top > 0.2f, "whose crest is lower than the block was (" + top.ToString("F2") + " m)");
        }

        Console.WriteLine(fails == 0 ? "ALL PASSED" : (fails + " FAILED"));
        return fails;
    }
}
