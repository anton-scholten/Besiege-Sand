using System;
using System.Collections.Generic;
using UnityEngine;
using SandMod;

class PressGround : IBedGround { public float Height(float x, float z) { return 0f; } }

/// <summary>Spheres that also say how fast their surface moves, as the field's BedShapes does.</summary>
class MovingSpheres : IBedShapes, IBedShapeMotion
{
    public List<Vector3> c = new List<Vector3>(); public List<float> r = new List<float>();
    public List<Vector3> v = new List<Vector3>();
    public int Count { get { return c.Count; } }
    public void Extent(int i, out Vector3 min, out Vector3 max)
    { Vector3 e = new Vector3(r[i], r[i], r[i]); min = c[i] - e; max = c[i] + e; }
    public float Distance(int i, Vector3 p) { return (p - c[i]).magnitude - r[i]; }
    public void Union(out Vector3 min, out Vector3 max)
    { min = new Vector3(1e9f, 1e9f, 1e9f); max = -min;
      for (int i = 0; i < c.Count; i++) { Vector3 a, b; Extent(i, out a, out b); min = Vector3.Min(min, a); max = Vector3.Max(max, b); } }
    public Vector3 SurfaceVelocity(int i, Vector3 p) { return v[i]; }
}

class PressSink : IBedSink
{
    public int count;
    public int Room { get { return 1000000 - count; } }
    public void Release(Vector3 p, int m, uint col) { count++; }
}

/// <summary>
/// The bed as ground for machines: it gives sand back only where something digs, and it
/// sinks under a load instead. It used to hand back every column a shape touched, so a
/// machine resting on a pile dug itself down to the floor.
/// </summary>
class BedPress
{
    static int fails = 0;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) fails++; }

    static SandBed Slab()
    {
        SandBed b = new SandBed(); b.Enabled = true; b.SetGround(new PressGround());
        b.SetMaterials(SandMaterials.Build());
        b.FillShape(SandBed.ShapeBox, new Vector3(0f, 0.25f, 0f), Quaternion.identity, new Vector3(1f, 0.25f, 1f), 0, 0xFFFFFFFFu);
        return b;
    }

    static float TopAt(SandBed b, float x, float z)
    {
        float h; b.TryTop(x, z, out h); return h;
    }

    public static int Main(string[] a)
    {
        // A wheel resting on the surface, still: nothing is dug.
        {
            SandBed bed = Slab(); MovingSpheres s = new MovingSpheres(); PressSink sink = new PressSink();
            s.c.Add(new Vector3(0.06f, 0.5f + 0.3f - 0.02f, 0.06f)); s.r.Add(0.3f); s.v.Add(Vector3.zero);
            bed.Release(s, sink, 0.09f, 100000);
            Check(sink.count == 0, "a shape resting on the bed digs nothing (" + sink.count + " grains)");

            // Rolling: the contact point of a wheel that rolls without slipping is still too.
            s.v[0] = new Vector3(0.1f, 0f, 0f);
            bed.Release(s, sink, 0.09f, 100000);
            Check(sink.count == 0, "nor does one rolling over it (" + sink.count + " grains)");

            // Spinning or ploughing: the surface moves through the sand, and it is dug.
            s.v[0] = new Vector3(3f, 0f, 0f);
            bed.Release(s, sink, 0.09f, 100000);
            Check(sink.count > 0, "a surface moving through it at 3 m/s digs it (" + sink.count + " grains)");
        }

        // Buried: whatever it is doing, the sand round it comes back as grains.
        {
            SandBed bed = Slab(); MovingSpheres s = new MovingSpheres(); PressSink sink = new PressSink();
            s.c.Add(new Vector3(0.06f, 0.3f, 0.06f)); s.r.Add(0.15f); s.v.Add(Vector3.zero);
            bed.Release(s, sink, 0.09f, 100000);
            Check(sink.count > 0, "a still shape buried in the sand is dug out (" + sink.count + " grains)");
        }

        // On a slope: a wheel resting on it, or rolling over it, digs nothing. The column uphill
        // of a wheel always passes beside it, and the old rule dug wherever a shape was within a
        // few centimetres of a column below its top -- so a machine dug away every slope it met.
        {
            SandBed bed = new SandBed(); bed.Enabled = true; bed.SetGround(new PressGround());
            bed.SetMaterials(SandMaterials.Build());
            float slope = Mathf.Tan(34f * Mathf.Deg2Rad);
            for (int i = -10; i < 20; i++) for (int j = -10; j < 10; j++)
            {
                float x = (i + 0.5f) * SandBed.Cell, z = (j + 0.5f) * SandBed.Cell;
                float h = 0.3f + Mathf.Max(x, 0f) * slope;
                bed.AddVolume(x, z, h * SandBed.Cell * SandBed.Cell, 0, 0xFFFFFFFFu);
            }
            // Tangent to the slope at x = 0.6, its surface 1 cm off it.
            Vector3 n = new Vector3(-Mathf.Sin(34f * Mathf.Deg2Rad), Mathf.Cos(34f * Mathf.Deg2Rad), 0f);
            Vector3 onSlope = new Vector3(0.6f, 0.3f + 0.6f * slope, 0.06f);
            MovingSpheres s = new MovingSpheres(); PressSink sink = new PressSink();
            s.c.Add(onSlope + n * 0.31f); s.r.Add(0.3f); s.v.Add(Vector3.zero);
            bed.Release(s, sink, 0.09f, 100000);
            Check(sink.count == 0, "a wheel resting on a 34 degree slope digs nothing (" + sink.count + " grains)");

            // A surface not touching the sand, moving fast: the front of a rolling wheel.
            s.c[0] = onSlope + n * 0.36f; s.v[0] = new Vector3(4f, 2f, 0f);
            bed.Release(s, sink, 0.09f, 100000);
            Check(sink.count == 0, "nor does a fast surface a few centimetres off it (" + sink.count + " grains)");

            // A blade pushed into a steep face: the collider reports a side contact, and that digs.
            bed.AddLoad(new Vector3(0.9f, 0.4f, 0.06f), new Vector3(-1f, 0f, 0f), 500f);
            bed.Press(0.01f);
            s.c[0] = new Vector3(50f, 50f, 50f);
            bed.Release(s, sink, 0.09f, 100000);
            Check(sink.count > 0, "a side contact digs where it was reported (" + sink.count + " grains)");
        }

        // A load presses a rut, as deep as the load over the stiffness, and pushes up a lip.
        {
            SandBed bed = Slab();
            float before = TopAt(bed, 0.06f, 0.06f), beside = TopAt(bed, 0.42f, 0.06f), volume = bed.Volume;
            float light = 0f, heavy = 0f;
            for (int k = 0; k < 100; k++) { bed.AddLoad(new Vector3(0.06f, 0.5f, 0.06f), 200f); bed.Press(0.01f); }
            light = before - TopAt(bed, 0.06f, 0.06f);
            for (int k = 0; k < 100; k++) { bed.AddLoad(new Vector3(0.06f, 0.5f, 0.06f), 1500f); bed.Press(0.01f); }
            heavy = before - TopAt(bed, 0.06f, 0.06f);
            float expectLight = 200f / (9f * SandBed.Cell * SandBed.Cell) / SandMaterials.Build()[0].firmness;
            Check(light > expectLight * 0.5f && light < expectLight * 1.5f,
                  "200 N presses a rut about load over stiffness deep (" + (light * 100f).ToString("F1") + " cm, expected " + (expectLight * 100f).ToString("F1") + ")");
            Check(heavy > light * 2f, "a heavier load presses deeper (" + (heavy * 100f).ToString("F1") + " cm)");
            float lip = 0f;
            for (int di = -2; di <= 2; di++) for (int dj = -2; dj <= 2; dj++)
                lip = Math.Max(lip, TopAt(bed, 0.06f + di * SandBed.Cell, 0.06f + dj * SandBed.Cell) - before);
            Check(lip > 0.001f, "and the sand it pushes aside makes a lip (" + (lip * 100f).ToString("F1") + " cm)");
            Check(Math.Abs(TopAt(bed, 0.6f, 0.6f) - beside) < 1e-4f, "sand well away from it is untouched");
            float after = bed.Volume;
            Check(after <= volume + 1e-4f && after > volume * 0.98f,
                  "only a little sand is lost to packing (" + volume.ToString("F4") + " -> " + after.ToString("F4") + " m^3)");
            // Pressing again with the same load sinks no further: sand does not creep.
            float settled = TopAt(bed, 0.06f, 0.06f);
            for (int k = 0; k < 100; k++) { bed.AddLoad(new Vector3(0.06f, 0.5f, 0.06f), 1500f); bed.Press(0.01f); }
            Check(Math.Abs(TopAt(bed, 0.06f, 0.06f) - settled) < 0.002f, "and a load it has already taken presses it no further");
        }

        // A tall region's walls fall to the angle of repose within about a second of sand steps
        // at the field's normal slumping rate. With every column of the region queued, slumping
        // worked through the flat middle first and the walls stood for seconds, long enough
        // for a machine to drive into a vertical face and dig, where it should meet a slope.
        {
            SandBed bed = new SandBed(); bed.Enabled = true; bed.SetGround(new PressGround());
            bed.SetMaterials(SandMaterials.Build());
            bed.FillShape(SandBed.ShapeBox, new Vector3(0f, 1f, 0f), Quaternion.identity, new Vector3(3f, 1f, 3f), 0, 0xFFFFFFFFu);
            Check(bed.Unsettled < 2000, "filling a 6 m region queues only its edges for slumping (" + bed.Unsettled + " columns)");
            for (int step = 0; step < 25; step++) bed.Relax(300, 1000f);
            float steepest = 0f;
            float limit = SandMaterials.Build()[0].friction * 1.15f;
            for (int i = -30; i < 30; i++)
            {
                float x0 = (i + 0.5f) * SandBed.Cell, x1 = x0 + SandBed.Cell;
                float h0 = TopAt(bed, x0, 0.06f), h1 = TopAt(bed, x1, 0.06f);
                if (h0 > 0.01f && h1 > 0.01f) steepest = Math.Max(steepest, Math.Abs(h1 - h0) / SandBed.Cell);
            }
            Check(steepest < limit * 1.6f, "and a second of slumping leaves no wall steeper than about its angle (slope " + steepest.ToString("F2") + ", angle " + limit.ToString("F2") + ")");
        }

        Console.WriteLine(fails == 0 ? "ALL PASSED" : (fails + " FAILED"));
        return fails;
    }
}
