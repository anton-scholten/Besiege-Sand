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

/// <summary>A flat blade, a thin box standing on the ground, turned about the vertical, sliding along +x.</summary>
class AngledBlade : IBedShapes, IBedShapeMotion
{
    public Vector3 c, v; public float angle; public Vector3 half = new Vector3(0.04f, 0.35f, 0.8f);
    public int Count { get { return 1; } }
    Vector3 Local(Vector3 p)
    {
        Vector3 d = p - c; float co = Mathf.Cos(-angle), si = Mathf.Sin(-angle);
        return new Vector3(d.x * co - d.z * si, d.y, d.x * si + d.z * co);
    }
    public float Distance(int i, Vector3 p)
    {
        Vector3 q = Local(p);
        Vector3 d = new Vector3(Mathf.Abs(q.x) - half.x, Mathf.Abs(q.y) - half.y, Mathf.Abs(q.z) - half.z);
        Vector3 o = Vector3.Max(d, Vector3.zero);
        return o.magnitude + Mathf.Min(Mathf.Max(d.x, Mathf.Max(d.y, d.z)), 0f);
    }
    public void Extent(int i, out Vector3 a, out Vector3 b) { float r = half.magnitude; a = c - new Vector3(r, half.y, r); b = c + new Vector3(r, half.y, r); }
    public void Union(out Vector3 a, out Vector3 b) { Extent(0, out a, out b); }
    public Vector3 SurfaceVelocity(int i, Vector3 p) { return v; }
}

/// <summary>
/// The bed as ground for machines: it sinks under a load, is ploughed aside by a face moving
/// into it and pushed aside by a shape inside it.
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
        // Buried and still: the sand it is in is pushed aside, and none of it is lost.
        {
            SandBed bed = Slab(); MovingSpheres s = new MovingSpheres();
            float volume = bed.Volume;
            s.c.Add(new Vector3(0.06f, 0.3f, 0.06f)); s.r.Add(0.15f); s.v.Add(Vector3.zero);
            bed.Plough(s, 0.09f, 100000);
            Check(TopAt(bed, 0.06f, 0.06f) < 0.3f && Math.Abs(bed.Volume - volume) < 1e-4f,
                  "a still shape buried in the sand pushes it aside (top under it " + TopAt(bed, 0.06f, 0.06f).ToString("F2") + " m)");
        }

        // A load presses a rut, as deep as the square root of its pressure over the firmness, and pushes up a lip.
        {
            SandBed bed = Slab();
            float before = TopAt(bed, 0.06f, 0.06f), beside = TopAt(bed, 0.42f, 0.06f), volume = bed.Volume;
            float light = 0f, heavy = 0f;
            for (int k = 0; k < 100; k++) { bed.AddLoad(new Vector3(0.06f, 0.5f, 0.06f), 0.2f); bed.Press(0.01f); }
            light = before - TopAt(bed, 0.06f, 0.06f);
            for (int k = 0; k < 100; k++) { bed.AddLoad(new Vector3(0.06f, 0.5f, 0.06f), 1500f); bed.Press(0.01f); }
            heavy = before - TopAt(bed, 0.06f, 0.06f);
            float expectLight = Mathf.Sqrt(0.2f / (9f * SandBed.Cell * SandBed.Cell) / SandMaterials.Build()[0].firmness);
            Check(light > expectLight * 0.5f && light < expectLight * 1.5f,
                  "0.2 N presses a rut about the square root of pressure over firmness deep (" + (light * 100f).ToString("F1") + " cm, expected " + (expectLight * 100f).ToString("F1") + ")");
            Check(heavy > light * 2f, "a heavier load presses deeper (" + (heavy * 100f).ToString("F1") + " cm)");
            // Past what the sand packs, it is pushed aside: a rut used to stop at the packing limit, a third of 0.5 m.
            Check(heavy > 0.5f * 0.35f * 1.5f, "and past what the sand packs, pushing it aside rather than stopping");
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

        // A tall block of sand's walls fall to the angle of repose within about a second of sand steps
        // at the field's normal slumping rate. With every column of the block queued, slumping
        // worked through the flat middle first and the walls stood for seconds, long enough
        // for a machine to drive into a vertical face and dig, where it should meet a slope.
        {
            SandBed bed = new SandBed(); bed.Enabled = true; bed.SetGround(new PressGround());
            bed.SetMaterials(SandMaterials.Build());
            bed.FillShape(SandBed.ShapeBox, new Vector3(0f, 1f, 0f), Quaternion.identity, new Vector3(3f, 1f, 3f), 0, 0xFFFFFFFFu);
            Check(bed.Unsettled < 2000, "filling a 6 m block queues only its edges for slumping (" + bed.Unsettled + " columns)");
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
        // there, and a wheel standing in its own rut is not pushed out from under. Both used to
        // happen, every step, until a machine had gone through the pile.
        {
            SandBed bed = Slab();
            Vector3 at = new Vector3(0.06f, 0.5f, 0.06f);
            for (int k = 0; k < 60; k++) { bed.AddLoad(at, 3000f); bed.Press(0.01f); bed.Relax(300, 1000f); }
            float rut = TopAt(bed, 0.06f, 0.06f);
            for (int k = 0; k < 5; k++) { bed.AddLoad(at, 3000f); bed.Press(0.01f); bed.Relax(300, 1000f); }
            Check(TopAt(bed, 0.06f, 0.06f) <= rut + 1e-4f, "slumping does not refill the rut under a wheel standing in it");

            MovingSpheres s = new MovingSpheres();
            s.c.Add(new Vector3(0.06f, rut + 0.3f - 0.08f, 0.06f)); s.r.Add(0.3f); s.v.Add(Vector3.zero);
            bed.AddLoad(at, 3000f); bed.Press(0.01f);
            float held = TopAt(bed, 0.06f, 0.06f);
            bed.Plough(s, 0.09f, 100000);
            Check(TopAt(bed, 0.06f, 0.06f) >= held - 1e-4f, "and a still wheel sunk in its own rut is not pushed out from under");
        }

        // Ploughing: a block sliding into the side of a pile pushes the sand ahead of it; a
        // wheel rolling into the same face does not, so it can still climb.
        {
            SandBed bed = Slab(); TurningSpheres s = new TurningSpheres();
            float volume = bed.Volume, face = TopAt(bed, -0.90f, 0.06f), beyond = TopAt(bed, -0.5f, 0.06f);
            s.c.Add(new Vector3(-1.15f, 0.35f, 0.06f)); s.r.Add(0.25f);
            s.v.Add(new Vector3(1f, 0f, 0f)); s.w.Add(Vector3.zero);
            bed.Plough(s, 0.09f, 100000);
            float cut = TopAt(bed, -0.90f, 0.06f);
            float most = 0f;
            for (float x = -0.9f; x < 0f; x += SandBed.Cell) most = Math.Max(most, TopAt(bed, x, 0.06f) - beyond);
            Check(cut < face - 0.2f && most > 0.05f && Math.Abs(bed.Volume - volume) < 1e-4f,
                  "a block sliding into a pile's side ploughs it: the face goes from " + face.ToString("F2") + " to " + cut.ToString("F2") + " m and a berm of " + (most * 100f).ToString("F0") + " cm rises ahead");

            SandBed bed2 = Slab(); TurningSpheres w = new TurningSpheres();
            w.c.Add(new Vector3(-1.15f, 0.35f, 0.06f)); w.r.Add(0.25f);
            w.v.Add(new Vector3(1f, 0f, 0f)); w.w.Add(new Vector3(0f, 0f, -1f / 0.25f));
            bed2.Plough(w, 0.09f, 100000);
            Check(TopAt(bed2, -0.90f, 0.06f) > face - 0.05f, "and a wheel rolling into it does not (" + TopAt(bed2, -0.90f, 0.06f).ToString("F2") + " m)");
        }

        // A thin layer too: a blade scraping along the ground clears 5 cm of sand ahead of it.
        {
            SandBed bed = new SandBed(); bed.Enabled = true; bed.SetGround(new PressGround()); bed.SetMaterials(SandMaterials.Build());
            bed.FillShape(SandBed.ShapeBox, new Vector3(0f, 0.025f, 0f), Quaternion.identity, new Vector3(1f, 0.025f, 1f), 0, 0xFFFFFFFFu);
            TurningSpheres blade = new TurningSpheres();
            blade.c.Add(new Vector3(-1.1f, 0.25f, 0.06f)); blade.r.Add(0.25f);
            blade.v.Add(new Vector3(1f, 0f, 0f)); blade.w.Add(Vector3.zero);
            float volume = bed.Volume;
            bed.Plough(blade, 0.09f, 100000);
            Check(TopAt(bed, -0.90f, 0.06f) < 0.02f && Math.Abs(bed.Volume - volume) < volume * 0.01f,
                  "a blade scraping the ground clears a thin layer ahead of it (" + (TopAt(bed, -0.90f, 0.06f) * 100f).ToString("F1") + " cm left of 5; volume " + volume.ToString("F4") + " -> " + bed.Volume.ToString("F4") + ")");
        }

        // The advanced settings, opened and left alone, are the medium exactly.
        {
            SandMaterials.Params[] table = SandMaterials.Build();
            bool same = true;
            string off = "";
            for (int m = 0; m < SandMaterials.Count; m++)
            {
                SandMaterials.Params a0 = table[m];
                SandMaterials.Params b0 = SandMaterials.Custom(a0, a0.friction, SandMaterials.SoftnessOf(a0), a0.digSpeed, a0.cloudSize, a0.cloudLength);
                float[] x = { a0.radius, a0.friction, a0.airDrag, a0.adhesion, a0.firmness, a0.packLimit, a0.digSpeed, a0.wetMaterial, a0.cloudSize, a0.cloudLength };
                float[] y = { b0.radius, b0.friction, b0.airDrag, b0.adhesion, b0.firmness, b0.packLimit, b0.digSpeed, b0.wetMaterial, b0.cloudSize, b0.cloudLength };
                for (int q = 0; q < x.Length; q++)
                    if (Math.Abs(x[q] - y[q]) > Math.Abs(x[q]) * 1e-4f + 1e-7f) { same = false; off += SandMaterials.Names[m] + "[" + q + "] "; }
            }
            Check(same, "the advanced settings left alone paint every medium exactly as it is" + (same ? "" : " (differs: " + off + ")"));
        }

        // Heat melts snow, nearest first, and leaves other media alone.
        {
            SandMaterials.Params[] table = SandMaterials.Build();
            bool[] meltable = new bool[table.Length];
            meltable[SandMaterials.Snow] = true;
            SandBed snow = new SandBed(); snow.Enabled = true; snow.SetGround(new PressGround()); snow.SetMaterials(table);
            snow.FillShape(SandBed.ShapeBox, new Vector3(0f, 0.25f, 0f), Quaternion.identity, new Vector3(1f, 0.25f, 1f), SandMaterials.Snow, 0xFFFFFFFFu);
            Vector3 fire = new Vector3(-1.3f, 0.5f, 0.06f);
            for (int k = 0; k < 100; k++) snow.Melt(fire, 3f, 0.25f * 0.01f, meltable);
            float near = 0.5f - TopAt(snow, -0.9f, 0.06f), far = 0.5f - TopAt(snow, 0.9f, 0.06f);
            SandBed sand = Slab();
            float before = sand.Volume;
            for (int k = 0; k < 100; k++) sand.Melt(fire, 3f, 0.25f * 0.01f, meltable);
            Check(near > far * 2f && near > 0.02f && Math.Abs(sand.Volume - before) < 1e-6f,
                  "heat melts snow from the near side (" + (near * 100f).ToString("F1") + " cm near, " + (far * 100f).ToString("F1") + " cm far in a second) and not sand");
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

            byte[] saved = bed.SaveAll(null);
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

        // A blade set at an angle to the way it goes pushes the sand off its face, towards its trailing end, as a snow
        // plough does; pushed straight the way it went, as it once was, the sand heaped towards the leading end.
        {
            float[] side = new float[2];
            for (int n = 0; n < 2; n++)
            {
                SandBed.PlowOffFace = n == 1;
                SandBed bed = Slab();
                AngledBlade blade = new AngledBlade();
                blade.angle = 40f * Mathf.Deg2Rad;   // its face turned towards +z: the trailing end is on that side
                blade.v = new Vector3(1.5f, 0f, 0f);
                blade.c = new Vector3(-1.3f, 0.35f, 0f);
                for (int step = 0; step < 12; step++)
                {
                    bed.Plough(blade, 0.09f, 100000);
                    blade.c += blade.v * 0.04f;
                }
                // Where the sand the blade heaped up lies across the way it went: the middle of all that rose above the
                // slab's 0.5 m, weighed by how much rose.
                double moment = 0, risen = 0;
                for (float x = -2f; x < 3f; x += SandBed.Cell)
                    for (float z = -2f; z < 2f; z += SandBed.Cell)
                    {
                        float t = TopAt(bed, x, z);
                        if (float.IsNaN(t) || t < 0.505f) continue;
                        moment += (t - 0.5f) * z; risen += t - 0.5f;
                    }
                side[n] = risen > 0 ? (float)(moment / risen) : 0f;
                Console.WriteLine("   " + (n == 1 ? "off the face" : "straight ahead") + ": heaped sand's middle " + side[n].ToString("F2") + " m across, " + (risen * SandBed.Cell * SandBed.Cell * 1000).ToString("F0") + " L risen");
            }
            SandBed.PlowOffFace = true;
            Check(side[1] - side[0] > 0.1f,
                  "an angled blade heaps its sand towards its trailing end (" + (side[1] - side[0]).ToString("F2")
                  + " m further that way than pushed straight ahead)");
        }

        // Undo and redo: a stroke journalled, then taken back and put back in, chunk for chunk -- to the millimetre the
        // packed chunks keep heights to.
        {
            SandBed bed = new SandBed(); bed.Enabled = true; bed.SetGround(new PressGround()); bed.SetMaterials(SandMaterials.Build());
            bed.FillShape(SandBed.ShapeBox, new Vector3(0f, 0.25f, 0f), Quaternion.identity, new Vector3(1f, 0.25f, 1f), 0, 0xFFFFFFFFu);
            while (bed.Unsettled > 0) bed.Relax(100000);
            double before = bed.Volume; float midBefore = TopAt(bed, 0.06f, 0.06f);
            bed.BeginJournal();
            // As the brush does: the chunks within reach are copied before they are worked on.
            bed.JournalArea(0.3f, 0.3f, 2f);
            bed.JournalArea(6f, 6f, 2f);
            // A pile on top of the slab and a new one where there was none, in another chunk, left to slump.
            bed.FillShape(SandBed.ShapeBox, new Vector3(0.3f, 0.75f, 0.3f), Quaternion.identity, new Vector3(0.3f, 0.25f, 0.3f), SandMaterials.Gravel, 0xFF0000FFu);
            bed.FillShape(SandBed.ShapeBox, new Vector3(6f, 0.2f, 6f), Quaternion.identity, new Vector3(0.5f, 0.2f, 0.5f), 0, 0xFFFFFFFFu);
            while (bed.Unsettled > 0) bed.Relax(100000);
            SandBed.Edit stroke = bed.EndJournal();
            double after = bed.Volume; float midAfter = TopAt(bed, 0.06f, 0.06f);
            SandBed.Edit back = bed.Apply(stroke);
            bool undone = Math.Abs(bed.Volume - before) < before * 1e-3 && Math.Abs(TopAt(bed, 0.06f, 0.06f) - midBefore) < 1e-3f && TopAt(bed, 6f, 6f) <= 0.001f;
            bed.Apply(back);
            bool redone = Math.Abs(bed.Volume - after) < after * 1e-3 && Math.Abs(TopAt(bed, 0.06f, 0.06f) - midAfter) < 1e-3f;
            // "Clear all sand" as a step: everything packed, cleared, and put back.
            double full = bed.Volume;
            SandBed.Edit all = bed.PackAll();
            bed.Clear();
            SandBed.Edit cleared = bed.Apply(all);
            bool restored = Math.Abs(bed.Volume - full) < full * 1e-3;
            while (bed.TakeDirty() != null) { }   // drawn, as the renderer would have
            bed.Apply(cleared);
            // Emptied wholesale, the bed holds no sand -- and its chunks must still be waiting to be drawn again, or the
            // last surface stays on screen (the renderer used to look only while the bed held sand).
            bool emptied = bed.Volume < 1e-6 && bed.DirtyCount > 0;
            Check(restored && emptied, "clearing is undone and redone (" + full.ToString("F3") + " m^3 back, then " + bed.Volume.ToString("F4") + ")");
            Check(stroke != null && undone && redone, "a stroke is undone and redone exactly (" + (stroke != null ? stroke.Count : 0) + " chunks; volume "
                  + before.ToString("F3") + " -> " + after.ToString("F3") + " -> undone " + (undone ? "ok" : "wrong") + " -> redone " + (redone ? "ok" : "wrong") + ")");
        }

        Console.WriteLine(fails == 0 ? "ALL PASSED" : (fails + " FAILED"));
        return fails;
    }
}
