using System;
using System.Collections.Generic;
using UnityEngine;
using SandMod;

class FlatGround : IBedGround { public float Height(float x, float z) { return 0f; } }

class SphereShapes : IBedShapes
{
    public List<Vector3> c = new List<Vector3>(); public List<float> r = new List<float>();
    public List<Vector3> look = new List<Vector3>();   // where each is being looked for
    public int Count { get { return c.Count; } }
    public void Extent(int i, out Vector3 min, out Vector3 max)
    { Vector3 v = new Vector3(r[i], r[i], r[i]); min = c[i] - v; max = c[i] + v; }
    public float Distance(int i, Vector3 p) { return (p - c[i] - look[i]).magnitude - r[i]; }
        public void Shift(int i, Vector3 delta) { while (look.Count <= i) look.Add(Vector3.zero); look[i] += delta; }
    public SandShape Shape(int i)
    {
        SandShape g = new SandShape();
        g.centre = c[i]; g.radius = r[i]; g.kindBlock = SandFormat.ShapeSphere;
        return g;
    }
    public void Union(out Vector3 min, out Vector3 max)
    { min = new Vector3(1e9f,1e9f,1e9f); max = -min;
      for (int i = 0; i < c.Count; i++) { Vector3 a,b; Extent(i,out a,out b); min = Vector3.Min(min,a); max = Vector3.Max(max,b);} }
}

class BedUnit
{
    static int fails = 0;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) fails++; }

    /// <summary>Ground with a hole in it: a stripe through the middle where there is none at all.
    /// Negative infinity is what a raycast that hits nothing comes to, and is what a column over
    /// such a gap settles from.</summary>
    class HoleGround : IBedGround
    {
        public float Height(float x, float z) { return Math.Abs(x) < 0.30f ? float.NegativeInfinity : 0f; }
    }

    static SandBed NewBed()
    {
        SandBed b = new SandBed(); b.Enabled = true; b.SetGround(new FlatGround());
        b.SetMaterials(SandMaterials.Build()); return b;
    }

    // A flat layer of sand, depth metres deep, over the square from 0 to side metres.
    static void Layer(SandBed bed, float side, float depth)
    {
        int n = Mathf.RoundToInt(side / SandBed.Cell);
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
                bed.AddVolume((i + 0.5f) * SandBed.Cell, (j + 0.5f) * SandBed.Cell, depth * SandBed.Cell * SandBed.Cell, 0, 0xFFFFFFFFu);
    }

    public static int Main(string[] a)
    {
        SandMaterials.Params[] table = SandMaterials.Build();

        // 1. a layer: its volume, its surface, its normal
        {
            SandBed bed = NewBed();
            Layer(bed, 2.4f, 0.3f);
            float expect = 2.4f * 2.4f * 0.3f;
            Check(Math.Abs(bed.Volume - expect) < expect * 0.001f, "a layer holds what was put down (" + bed.Volume.ToString("F3") + " of " + expect.ToString("F3") + ")");
            float h; bool ok = bed.TryTop(0.6f, 0.6f, out h);
            Check(ok && Math.Abs(h - 0.3f) < 0.01f, "its surface is as high as it is deep");
            Vector3 n = bed.Normal(0.6f, 0.6f);
            Check(n.y > 0.99f, "flat pile normal is up");
            float hb; Check(!bed.TryTop(5f, 5f, out hb), "no pile over bare ground");
        }

        // A pile grown in place and settled where the ground is not known. Settling works on a
        // scratch array it keeps between calls, and a column with no ground under it used to leave
        // its entry alone -- so the next column to read it read whatever was there before, and on a
        // fresh bed that is a zero, which is the value for "this column was cut". The settle then
        // hung a skirt off the sand beside the hole and brought the pile down with it. Hold has
        // always guarded against this; Fall did not, and the guard is now written once per column.
        // The heights are pinned to the corrected settle: which of the two is right cannot be
        // derived here, but a zero the settle never wrote is not an answer either.
        {
            SandBed bed = new SandBed(); bed.Enabled = true; bed.SetGround(new HoleGround()); bed.SetMaterials(table);
            int lo = -30, n = 60, m = n * n;
            float[] xs = new float[m], zs = new float[m], vol = new float[m];
            for (int j = 0; j < n; j++) for (int i = 0; i < n; i++)
            {
                int q = j * n + i;
                xs[q] = (lo + i + 0.5f) * SandBed.Cell;
                zs[q] = (lo + j + 0.5f) * SandBed.Cell;
                float d = (float)Math.Sqrt((i - n / 2) * (i - n / 2) + (j - n / 2) * (j - n / 2));
                vol[q] = Math.Max(0f, 2.6f - d * 0.26f) * SandBed.Cell * SandBed.Cell;
            }
            bed.PourBatch(xs, zs, vol, m, 0, 0xFFFFFFFFu, true);
            float before = bed.Volume;
            while (bed.HasDeferred) bed.SettleDeferred(8);
            float sum = 0f, t, b; uint c;
            for (int i = -6; i <= 6; i++) { bed.Thickness(i, 0, out t, out b, out c); sum += t; }
            Check(Math.Abs(bed.Volume - before) < before * 1e-3f,
                  "a pile settled over ground that is missing under part of it keeps all its sand (" + bed.Volume.ToString("F3") + " of " + before.ToString("F3") + " m^3)");
            Check(Math.Abs(sum / 13f - 0.224f) < 0.005f,
                  "and settles to " + (sum / 13f * 100f).ToString("F1") + " cm across the hole, not the "
                  + "19.8 cm it came to when a column with no ground under it read a zero for 'cut here'");
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

        // 4. a sphere inside the sand pushes it aside, and none is lost
        {
            SandBed bed = NewBed();
            Layer(bed, 2.4f, 0.3f);
            float before = bed.Volume;
            SphereShapes sh = new SphereShapes(); sh.c.Add(new Vector3(1.2f, 0.2f, 1.2f)); sh.r.Add(0.25f);
            int n = bed.Plough(sh, 0.09f, 1000000);
            float h; bed.TryTop(1.2f, 1.2f, out h);
            Console.WriteLine("pushed aside " + n + " columns; under the sphere " + h.ToString("F2") + " m");
            Check(n > 0, "a sphere in the sand pushes it aside");
            Check(h < 0.3f - 0.05f, "and the sand under it goes down");
            Check(Math.Abs(bed.Volume - before) < before * 0.002f, "pushing aside conserves volume");
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


        // What a wheel standing in the sand is answered, and whether the answer is one the wheel's
        // own contacts can agree on. This is the query the support force is built on, and the only
        // part of it that can be checked without PhysX: the force itself needs a rigidbody.
        {
            SandBed bed = NewBed();
            Layer(bed, 6f, 0.4f);
            float vol = bed.Volume;
            float top, room;
            bool got = bed.Support(3f, 3f, out top, out room);
            Check(got && Math.Abs(top - 0.4f) < 0.005f, "flat sand answers at its own surface (" + top.ToString("F3") + " m)");
            Check(got && room > 0.01f, "and has room left to compact (" + (room * 100f).ToString("F1") + " cm)");

            // A rut dug across it, and the same question asked down in the rut and out on the rim.
            SandBed dig = NewBed();
            Layer(dig, 6f, 0.4f);
            for (int i = 0; i < 60; i++) dig.Blast(new Vector3(3f, 0.35f, 3f), 0.35f, 3.2f);
            for (int i = 0; i < 4000; i++) dig.Relax(2000);
            float rt, rr;
            dig.Support(3f, 3f, out rt, out rr);
            for (int i = 0; i < 400; i++) dig.Relax(2000);
            // The mean over the columns about a point is what a wheel's contacts share, so it must
            // sit between the hole and the rim rather than on either.
            dig.Support(3f, 3f, out rt, out rr);
            Check(rt > 0.05f && rt < 0.4f, "a point in a rut is answered between the floor and the rim (" + rt.ToString("F3") + " m)");
            float hole; dig.TryTop(3f, 3f, out hole);
            Check(rt > hole, "and above the floor of the hole itself (" + hole.ToString("F3") + " m), which is what makes one wheel's contacts agree");
            Check(Math.Abs(dig.Volume - vol) < vol * 0.02f, "asking the sand what it will bear moves none of it");
        }


        // What the sand will bear, which is what a thing standing in it is held up by, and which
        // is not a share of the load: a light machine and a heavy one meet the same resistance and
        // differ only in whether it is enough. Each medium has its own, and they must not.
        {
            SandMaterials.Params[] bearing = SandMaterials.Build();
            foreach (int medium in new int[] { SandMaterials.Snow, SandMaterials.Dust, SandMaterials.Sand })
            {
                SandBed under = NewBed();
                under.FillShape(SandBed.ShapeBox, new Vector3(3f, 0.25f, 3f), Quaternion.identity,
                                new Vector3(3f, 0.25f, 3f), medium, 0xFFFFFFFFu);
                while (under.Unsettled > 0) under.Relax(1000000);
                float top, room, bear;
                bool got = under.Support(3f, 3f, out top, out room, out bear);
                string what = medium == SandMaterials.Snow ? "snow"
                            : medium == SandMaterials.Dust ? "dust" : "sand";
                Console.WriteLine("   " + what + " bears " + (bear / 1000f).ToString("F1")
                                  + " kPa, and has " + (room * 100f).ToString("F1")
                                  + " cm of compaction left");
                Check(got && Math.Abs(bear - bearing[medium].firmness) < 1f, what
                      + " bears the pressure its own row says (" + (bear / 1000f).ToString("F1")
                      + " against " + (bearing[medium].firmness / 1000f).ToString("F1") + " kPa)");
                Check(got && bear > 0f && room > 0f, "and it has something to answer with");
            }
            // Dust is barely a solid and sand is a bank; they must not bear the same.
            SandBed soft = NewBed(), hard = NewBed();
            soft.FillShape(SandBed.ShapeBox, new Vector3(3f, 0.25f, 3f), Quaternion.identity,
                           new Vector3(3f, 0.25f, 3f), SandMaterials.Dust, 0xFFFFFFFFu);
            hard.FillShape(SandBed.ShapeBox, new Vector3(3f, 0.25f, 3f), Quaternion.identity,
                           new Vector3(3f, 0.25f, 3f), SandMaterials.Sand, 0xFFFFFFFFu);
            while (soft.Unsettled > 0) soft.Relax(1000000);
            while (hard.Unsettled > 0) hard.Relax(1000000);
            float t1, r1, dust, t2, r2, sand;
            soft.Support(3f, 3f, out t1, out r1, out dust);
            hard.Support(3f, 3f, out t2, out r2, out sand);
            Check(sand > dust * 5f, "and sand bears many times what dust does ("
                  + (sand / 1000f).ToString("F1") + " against " + (dust / 1000f).ToString("F1") + " kPa)");
        }

        Console.WriteLine(fails == 0 ? "ALL PASSED" : (fails + " FAILED"));
        return fails;
    }
}
