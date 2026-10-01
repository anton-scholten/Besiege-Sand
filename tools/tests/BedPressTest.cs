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

        // A load presses a rut, as deep as the load over the stiffness, and pushes up a lip.
        {
            SandBed bed = Slab();
            float before = TopAt(bed, 0.06f, 0.06f), beside = TopAt(bed, 0.42f, 0.06f), volume = bed.Volume;
            float light = 0f, heavy = 0f;
            for (int k = 0; k < 100; k++) { bed.AddLoad(new Vector3(0.06f, 0.5f, 0.06f), 200f); bed.Press(0.01f); }
            light = before - TopAt(bed, 0.06f, 0.06f);
            for (int k = 0; k < 100; k++) { bed.AddLoad(new Vector3(0.06f, 0.5f, 0.06f), 1500f); bed.Press(0.01f); }
            heavy = before - TopAt(bed, 0.06f, 0.06f);
            float expectLight = 200f / (9f * SandBed.Cell * SandBed.Cell) / SandBed.PressStiffness;
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

        Console.WriteLine(fails == 0 ? "ALL PASSED" : (fails + " FAILED"));
        return fails;
    }
}
