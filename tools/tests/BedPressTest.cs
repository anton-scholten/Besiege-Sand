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

/// <summary>Spheres that turn as well: the surface velocity is the centre's plus the spin's.</summary>
class TurningSpheres : MovingSpheres, IBedShapeMotion
{
    public List<Vector3> w = new List<Vector3>();
    public new Vector3 SurfaceVelocity(int i, Vector3 p) { return v[i] + Vector3.Cross(w[i], p - c[i]); }
}

class MediaSink : IBedSink
{
    public int[] byMaterial = new int[64];
    public int Room { get { return 1000000; } }
    public void Release(Vector3 p, int m, uint col) { byMaterial[m]++; }
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

        // Buried and still: the sand it is in is pushed aside, not turned into grains. Grains
        // cannot hold a machine up, and a wheel that found itself inside a pile sank on through.
        {
            SandBed bed = Slab(); MovingSpheres s = new MovingSpheres(); PressSink sink = new PressSink();
            float volume = bed.Volume;
            s.c.Add(new Vector3(0.06f, 0.3f, 0.06f)); s.r.Add(0.15f); s.v.Add(Vector3.zero);
            bed.Release(s, sink, 0.09f, 100000);
            Check(sink.count == 0 && TopAt(bed, 0.06f, 0.06f) < 0.3f && Math.Abs(bed.Volume - volume) < 1e-4f,
                  "a still shape buried in the sand pushes it aside (" + sink.count + " grains, top under it " + TopAt(bed, 0.06f, 0.06f).ToString("F2") + " m)");

            // Sliding, it digs.
            SandBed bed2 = Slab(); s.v[0] = new Vector3(3f, 0f, 0f);
            bed2.Release(s, sink, 0.09f, 100000);
            Check(sink.count > 0, "and one sliding through it digs it (" + sink.count + " grains)");
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
            for (int di = -4; di <= 4; di++) for (int dj = -4; dj <= 4; dj++)
                lip = Math.Max(lip, TopAt(bed, 0.06f + di * SandBed.Cell, 0.06f + dj * SandBed.Cell) - before);
            Check(lip > 0.001f, "and the sand it pushes aside makes a lip (" + (lip * 100f).ToString("F1") + " cm)");
            Check(Math.Abs(TopAt(bed, 0.7f, 0.7f) - beside) < 1e-4f, "sand well away from it is untouched (" + ((TopAt(bed, 0.7f, 0.7f) - beside) * 1000f).ToString("F2") + " mm)");
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

        // The brush: sand poured on one spot as a dome, a little at a time while slumping runs,
        // grows into a cone at its own angle of repose -- steeper for gravel, flatter for dust.
        {
            SandMaterials.Params[] table = SandMaterials.Build();
            int[] media = { SandMaterials.Dust, SandMaterials.Sand, SandMaterials.Gravel };
            float[] got = new float[media.Length];
            for (int m = 0; m < media.Length; m++)
            {
                SandBed bed = new SandBed(); bed.Enabled = true; bed.SetGround(new PressGround());
                bed.SetMaterials(table);
                for (int step = 0; step < 300; step++)
                {
                    // What SandField.Paint does: a dome over a 0.5 m circle.
                    for (int i = -5; i <= 5; i++) for (int j = -5; j <= 5; j++)
                    {
                        float x = (i + 0.5f) * SandBed.Cell, z = (j + 0.5f) * SandBed.Cell;
                        float d2 = (x * x + z * z) / 0.25f;
                        if (d2 < 1f) bed.AddVolume(x, z, 0.004f * (1f - d2), media[m], 0xFFFFFFFFu);
                    }
                    bed.Relax(300, 1000f);
                }
                float top = TopAt(bed, 0.06f, 0.06f), r = 0f;
                for (int i = 0; i < 80; i++) if (TopAt(bed, 0.06f + i * SandBed.Cell, 0.06f) > 0.02f) r = i * SandBed.Cell;
                got[m] = Mathf.Atan2(top, r) * Mathf.Rad2Deg;
            }
            Console.WriteLine("   brushed mounds: dust " + got[0].ToString("F0") + ", sand " + got[1].ToString("F0") + ", gravel " + got[2].ToString("F0") + " degrees");
            Check(got[0] < got[1] && got[1] < got[2], "a brushed mound stands at its medium's angle: dust flattest, gravel steepest");
            Check(got[1] > 22f && got[1] < 38f, "and sand's is about thirty degrees (" + got[1].ToString("F0") + ")");
        }

        // Between runs: what is captured when a run starts is exactly what comes back.
        {
            SandBed bed = Slab();
            float before = bed.Volume, topBefore = TopAt(bed, 0.06f, 0.06f);
            object saved = bed.Capture();
            for (int k = 0; k < 50; k++) { bed.AddLoad(new Vector3(0.06f, 0.5f, 0.06f), 1500f); bed.Press(0.01f); }
            bed.AddVolume(3f, 3f, 0.5f, 0, 0xFFFFFFFFu);
            Check(bed.RemoveVolume(0.3f, 0.3f, 0.1f) > 0.09f, "the brush can take sand away");
            bed.Restore(saved);
            Check(Math.Abs(bed.Volume - before) < 1e-4f && Math.Abs(TopAt(bed, 0.06f, 0.06f) - topBefore) < 1e-4f,
                  "and a run's sand goes back exactly to how it was (" + before.ToString("F4") + " -> " + bed.Volume.ToString("F4") + " m^3)");
        }

        // Media stay what they are. A column used to take the medium there was more of, so
        // wet sand poured on a dry pile turned dry. Now it lies on it as a layer of its own.
        {
            SandMaterials.Params[] table = SandMaterials.Build();
            SandBed bed = new SandBed(); bed.Enabled = true; bed.SetGround(new PressGround());
            bed.SetMaterials(table);
            float cellArea = SandBed.Cell * SandBed.Cell;
            bed.AddVolume(0.06f, 0.06f, 0.30f * cellArea, SandMaterials.Sand, 0xFFFFFFFFu);
            bed.AddVolume(0.06f, 0.06f, 0.10f * cellArea, SandMaterials.WetSand, 0xFF808080u);
            Check(bed.MaterialAt(0, 0) == SandMaterials.WetSand, "wet sand poured on dry sand stays wet sand, on top");

            MovingSpheres s = new MovingSpheres(); MediaSink sink = new MediaSink();
            // Sliding through it, so it digs rather than pushes aside.
            s.c.Add(new Vector3(0.06f, 0.2f, 0.06f)); s.r.Add(0.1f); s.v.Add(new Vector3(3f, 0f, 0f));
            bed.Release(s, sink, 0.09f, 100000);
            float wetVol = sink.byMaterial[SandMaterials.WetSand] * bed.GrainVolume(SandMaterials.WetSand);
            float dryVol = sink.byMaterial[SandMaterials.Sand] * bed.GrainVolume(SandMaterials.Sand);
            Check(sink.byMaterial[SandMaterials.WetSand] > 0 && sink.byMaterial[SandMaterials.Sand] > 0,
                  "and digging the column out gives back both: " + sink.byMaterial[SandMaterials.WetSand] + " wet grains and " + sink.byMaterial[SandMaterials.Sand] + " dry");
            Check(Math.Abs(wetVol - 0.10f * cellArea) < bed.GrainVolume(SandMaterials.WetSand) * 1.5f,
                  "in the amounts that went in (" + (wetVol / cellArea * 100f).ToString("F1") + " cm of wet sand of 10)");

            // Slumping carries each layer's own medium.
            SandBed b2 = new SandBed(); b2.Enabled = true; b2.SetGround(new PressGround());
            b2.SetMaterials(table);
            b2.AddVolume(0.06f, 0.06f, 0.6f * cellArea, SandMaterials.Sand, 0xFFFFFFFFu);
            b2.AddVolume(0.06f, 0.06f, 0.3f * cellArea, SandMaterials.WetSand, 0xFF808080u);
            for (int k = 0; k < 20; k++) b2.Relax(300, 1000f);
            int wetCols = 0;
            for (int i = -6; i <= 6; i++) for (int j = -6; j <= 6; j++) if (b2.MaterialAt(i, j) == SandMaterials.WetSand) wetCols++;
            Check(wetCols > 1 && b2.MaterialAt(0, 0) >= 0, "and a slumping layered pile spreads its wet top as wet sand (" + wetCols + " wet-topped columns)");
        }

        // Under a wheel: the rut a wheel presses is not refilled by slumping while it stands
        // there, and a wheel standing in its own rut is not dug out from under. Both used to
        // happen, every step, until a machine had gone through the pile.
        {
            SandBed bed = Slab();
            Vector3 at = new Vector3(0.06f, 0.5f, 0.06f);
            for (int k = 0; k < 60; k++) { bed.AddLoad(at, 3000f); bed.Press(0.01f); bed.Relax(300, 1000f); }
            float rut = TopAt(bed, 0.06f, 0.06f);
            for (int k = 0; k < 5; k++) { bed.AddLoad(at, 3000f); bed.Press(0.01f); bed.Relax(300, 1000f); }
            Check(TopAt(bed, 0.06f, 0.06f) <= rut + 1e-4f, "slumping does not refill the rut under a wheel standing in it");

            MovingSpheres s = new MovingSpheres(); PressSink sink = new PressSink();
            s.c.Add(new Vector3(0.06f, rut + 0.3f - 0.08f, 0.06f)); s.r.Add(0.3f); s.v.Add(Vector3.zero);
            bed.AddLoad(at, 3000f); bed.Press(0.01f);
            bed.Release(s, sink, 0.09f, 100000);
            Check(sink.count == 0, "and a still wheel sunk in its own rut is not dug out (" + sink.count + " grains)");
        }

        // Rolling fast in a shallow rut: the rim meets the front of the rut moving at a good
        // fraction of the machine's speed, but into the sand, not along it. Digging on that dug
        // every rut deeper the faster a machine went. A wheel spinning in place does dig.
        {
            SandBed bed = Slab(); TurningSpheres s = new TurningSpheres(); PressSink sink = new PressSink();
            float R = 0.3f, speed = 4f;
            s.c.Add(new Vector3(0.06f, 0.5f + R - 0.04f, 0.06f)); s.r.Add(R);
            s.v.Add(new Vector3(speed, 0f, 0f)); s.w.Add(new Vector3(0f, 0f, -speed / R));
            bed.Release(s, sink, 0.09f, 100000);
            Check(sink.count == 0, "a wheel rolling at 4 m/s, 4 cm into the sand, digs nothing (" + sink.count + " grains)");

            s.v[0] = Vector3.zero; s.w[0] = new Vector3(0f, 0f, -10f);
            bed.Release(s, sink, 0.09f, 100000);
            Check(sink.count > 0, "a wheel spinning in place digs (" + sink.count + " grains)");
        }

        // Sticky media come away on a wheel rolling over them; dry sand does not.
        {
            int[] got = new int[SandMaterials.Count];
            foreach (int m in new int[] { SandMaterials.Sand, SandMaterials.Mud, SandMaterials.Glue })
            {
                SandBed bed = new SandBed(); bed.Enabled = true; bed.SetGround(new PressGround());
                bed.SetMaterials(SandMaterials.Build());
                bed.FillShape(SandBed.ShapeBox, new Vector3(0f, 0.25f, 0f), Quaternion.identity, new Vector3(1f, 0.25f, 1f), m, 0xFFFFFFFFu);
                TurningSpheres s = new TurningSpheres(); PressSink sink = new PressSink();
                float R = 0.3f, speed = 2f;
                s.c.Add(new Vector3(0.06f, 0.5f + R - 0.01f, 0.06f)); s.r.Add(R);
                s.v.Add(new Vector3(speed, 0f, 0f)); s.w.Add(new Vector3(0f, 0f, -speed / R));
                for (int k = 0; k < 40; k++) bed.Release(s, sink, 0.09f, 100000);
                got[m] = sink.count;
            }
            Check(got[SandMaterials.Sand] == 0 && got[SandMaterials.Mud] > 0 && got[SandMaterials.Glue] > 0,
                  "a rolling wheel picks up mud (" + got[SandMaterials.Mud] + " grains) and glue (" + got[SandMaterials.Glue] + ") but not dry sand (" + got[SandMaterials.Sand] + ")");
        }

        // Multiplayer: a chunk the host sends arrives as it left -- both layers, their media,
        // heights to the millimetre.
        {
            SandMaterials.Params[] table = SandMaterials.Build();
            SandBed host = new SandBed(); host.Enabled = true; host.SetGround(new PressGround()); host.SetMaterials(table);
            float area = SandBed.Cell * SandBed.Cell;
            for (int i = 0; i < 16; i++) for (int j = 0; j < 16; j++)
                host.AddVolume((i + 0.5f) * SandBed.Cell, (j + 0.5f) * SandBed.Cell, (0.1f + 0.01f * i) * area, SandMaterials.Sand, 0xFF2080C0u);
            host.AddVolume(0.06f, 0.06f, 0.05f * area, SandMaterials.WetSand, 0xFF101010u);
            SandBed client = new SandBed(); client.Enabled = true; client.SetGround(new PressGround()); client.SetMaterials(table);
            client.Decode(host.Encode(host.ChunkAt(0, 0)));
            float worst = 0f;
            for (int i = 0; i < 16; i++) for (int j = 0; j < 16; j++)
                worst = Math.Max(worst, Math.Abs(TopAt(host, (i + 0.5f) * SandBed.Cell, (j + 0.5f) * SandBed.Cell)
                                              - TopAt(client, (i + 0.5f) * SandBed.Cell, (j + 0.5f) * SandBed.Cell)));
            Check(worst < 0.002f && client.MaterialAt(0, 0) == SandMaterials.WetSand && client.MaterialAt(1, 0) == SandMaterials.Sand
                  && Math.Abs(client.Volume - host.Volume) < 1e-3f,
                  "a chunk sent to another player arrives as it left (worst height " + (worst * 1000f).ToString("F1") + " mm, " + host.Encode(host.ChunkAt(0, 0)).Length + " bytes)");
        }

        // Packed: a kept run, a saved level. A 20 m mound of varied sand comes back to the
        // millimetre, in a small part of the memory it takes as columns.
        {
            SandMaterials.Params[] table = SandMaterials.Build();
            SandBed bed = new SandBed(); bed.Enabled = true; bed.SetGround(new PressGround()); bed.SetMaterials(table);
            System.Random rnd = new System.Random(3);
            uint[] shades = new uint[8];
            for (int q = 0; q < 8; q++) shades[q] = 0xFF000000u | (uint)(0x405060 + q * 0x030303);
            float area = SandBed.Cell * SandBed.Cell;
            int side = (int)(20f / SandBed.Cell);
            for (int i = 0; i < side; i++) for (int j = 0; j < side; j++)
            {
                float x = (i + 0.5f) * SandBed.Cell - 10f, z = (j + 0.5f) * SandBed.Cell - 10f;
                float h = Math.Max(0f, 4f - (float)Math.Sqrt(x * x + z * z) * 0.6f);
                if (h <= 0f) continue;
                bed.AddVolume(x, z, h * area, SandMaterials.Sand, shades[rnd.Next(8)]);
            }
            bed.AddVolume(0.06f, 0.06f, 0.2f * area, SandMaterials.Snow, 0xFFFFFFFFu);
            for (int k = 0; k < 20; k++) { bed.AddLoad(new Vector3(2f, 3f, 2f), 2000f); bed.Press(0.01f); }

            float before = bed.Volume;
            object kept = bed.Capture();
            SandBed.Compact(kept, 1e6f);
            SandBed.Snapshot snap = (SandBed.Snapshot)kept;
            int chunks = snap.Packed.Count;
            bed.AddVolume(1f, 1f, 1f, 0, 0xFFFFFFFFu);
            bed.Restore(kept);
            Check(Math.Abs(bed.Volume - before) < before * 1e-3f && bed.MaterialAt(0, 0) == SandMaterials.Snow,
                  "a packed run comes back as it was (" + before.ToString("F2") + " -> " + bed.Volume.ToString("F2") + " m^3)");
            float raw = chunks * (16 * 16 * 26f);
            Check(snap.Bytes < raw / 6f,
                  "and is held in " + (snap.Bytes / 1024f).ToString("F0") + " KB rather than " + (raw / 1024f).ToString("F0") + " KB as columns (" + chunks + " chunks)");

            byte[] saved = bed.SaveAll();
            SandBed loaded = new SandBed(); loaded.Enabled = true; loaded.SetGround(new PressGround()); loaded.SetMaterials(table);
            loaded.LoadAll(saved);
            float worst = 0f;
            for (int i = 0; i < side; i += 3) for (int j = 0; j < side; j += 3)
            {
                float x = (i + 0.5f) * SandBed.Cell - 10f, z = (j + 0.5f) * SandBed.Cell - 10f;
                worst = Math.Max(worst, Math.Abs(TopAt(bed, x, z) - TopAt(loaded, x, z)));
            }
            Check(worst <= 0.0006f && Math.Abs(loaded.Volume - bed.Volume) < 1e-3f,
                  "a level's sand saves in " + (saved.Length / 1024f).ToString("F0") + " KB and loads to within " + (worst * 1000f).ToString("F1") + " mm");
            Check(!loaded.LoadAll(new byte[] { 1, 2, 3, 4, 5, 6 }) && loaded.FilledColumns == 0, "and bytes that are not sand load as none");
        }

        // The compressor on its own: noise, runs and the empty case all come back exactly.
        {
            System.Random rnd = new System.Random(5);
            bool ok = true;
            foreach (int n in new int[] { 0, 1, 7, 1000, 70000 })
                for (int kind = 0; kind < 3; kind++)
                {
                    byte[] b = new byte[n];
                    for (int q = 0; q < n; q++) b[q] = (byte)(kind == 0 ? rnd.Next(256) : kind == 1 ? 0 : (q / 37) % 5);
                    byte[] back = Lz.Decompress(Lz.Compress(b, n));
                    if (back == null || back.Length != n) { ok = false; continue; }
                    for (int q = 0; q < n; q++) if (back[q] != b[q]) { ok = false; break; }
                }
            Check(ok, "the LZ compressor gives back exactly what it was given");
        }

        Console.WriteLine(fails == 0 ? "ALL PASSED" : (fails + " FAILED"));
        return fails;
    }
}
