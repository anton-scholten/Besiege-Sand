using System;
using System.Collections.Generic;
using UnityEngine;
using SandMod;

class LGround : IBedGround { public float Height(float x, float z) { return 0f; } }

/// <summary>
/// Painting several media on and round one another leaves a pile in layers, one for each, in the order
/// they went on, with as much of each as was poured -- through pouring, slumping, digging out, packing for
/// the other players and keeping a copy.
/// </summary>
delegate void Blob(int cx, int cz, int r, int rin, int medium, float thick, int frames);

class BedLayerTest
{
    static int fails = 0;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) fails++; }

    // Volume of each medium in the bed, by walking every column's layers.
    static float[] Volumes(SandBed bed, int lo, int hi)
    {
        float[] v = new float[64];
        float[] amount = new float[SandBed.MaxLayers]; byte[] material = new byte[SandBed.MaxLayers];
        for (int i = lo; i < hi; i++) for (int j = lo; j < hi; j++)
        {
            int n = bed.LayersAt(i, j, amount, material);
            for (int q = 0; q < n; q++) v[material[q]] += amount[q] * SandBed.Cell * SandBed.Cell;
        }
        return v;
    }

    static string Stack(SandBed bed, int i, int j)
    {
        float[] amount = new float[SandBed.MaxLayers]; byte[] material = new byte[SandBed.MaxLayers];
        int n = bed.LayersAt(i, j, amount, material);
        string s = "";
        for (int q = 0; q < n; q++) s += (q > 0 ? " over " : "") + SandMaterials.Names[material[q]] + " " + (amount[q] * 100f).ToString("F0") + " cm";
        return s;
    }

    public static int Main(string[] args)
    {
        SandMaterials.Params[] table = SandMaterials.Build();
        float cell = SandBed.Cell, area = cell * cell;
        int[] order = { SandMaterials.Sand, SandMaterials.Gravel, SandMaterials.Snow, SandMaterials.Mud, SandMaterials.WetSand };
        int side = 40;

        // Five media, one on the other, over one disc, each poured as a batch (the brush's way).
        {
            SandBed bed = new SandBed(); bed.Enabled = true; bed.SetGround(new LGround()); bed.SetMaterials(table);
            int[] ci = new int[side * side * 4], cj = new int[ci.Length]; float[] dv = new float[ci.Length]; int n = 0;
            for (int i = -side; i < side; i++) for (int j = -side; j < side; j++)
                if (i * i + j * j < side * side / 2) { ci[n] = i; cj[n] = j; dv[n] = 0.02f; n++; }
            float[] poured = new float[64];
            for (int m = 0; m < order.Length; m++)
            {
                for (int f = 0; f < 6; f++) { bed.PourColumns(ci, cj, dv, n, order[m], 0xFFFFFFFFu, false); poured[order[m]] += n * 0.02f * area; }
            }
            string stack = Stack(bed, 0, 0);
            Console.WriteLine("middle of the disc: " + stack);
            float[] got = Volumes(bed, -side - 4, side + 4);
            bool all = true;
            for (int m = 0; m < order.Length; m++) if (Math.Abs(got[order[m]] - poured[order[m]]) > poured[order[m]] * 0.02f) all = false;
            Check(all, "five media poured one after another are all in the pile, each as much as was poured");
            float[] amount = new float[SandBed.MaxLayers]; byte[] material = new byte[SandBed.MaxLayers];
            int layers = bed.LayersAt(0, 0, amount, material);
            bool inOrder = layers == 5;
            for (int q = 0; q < layers && inOrder; q++) if (material[q] != order[order.Length - 1 - q]) inOrder = false;
            Check(inOrder, "and the middle of the pile has five layers, the last poured on top (" + layers + " layers)");
        }

        // Round and onto one another: a ring of gravel on a disc of sand, a snow cap on part of both, mud against the side.
        {
            SandBed bed = new SandBed(); bed.Enabled = true; bed.SetGround(new LGround()); bed.SetMaterials(table);
            float[] poured = new float[64];
            Blob blob = delegate (int cx, int cz, int r, int rin, int medium, float thick, int frames)
            {
                List<int> ci = new List<int>(), cj = new List<int>();
                for (int i = cx - r; i <= cx + r; i++) for (int j = cz - r; j <= cz + r; j++)
                {
                    int d2 = (i - cx) * (i - cx) + (j - cz) * (j - cz);
                    if (d2 <= r * r && d2 >= rin * rin) { ci.Add(i); cj.Add(j); }
                }
                float[] t = new float[ci.Count]; for (int q = 0; q < t.Length; q++) t[q] = thick;
                int[] ai = ci.ToArray(), aj = cj.ToArray();
                for (int f = 0; f < frames; f++) bed.PourColumns(ai, aj, t, t.Length, medium, 0xFFFFFFFFu, false);
                poured[medium] += ai.Length * thick * frames * area;
            };
            blob(0, 0, 30, 0, SandMaterials.Sand, 0.02f, 15);
            blob(0, 0, 24, 14, SandMaterials.Gravel, 0.02f, 10);
            blob(10, 0, 14, 0, SandMaterials.Snow, 0.02f, 8);
            blob(-34, 0, 12, 0, SandMaterials.Mud, 0.02f, 10);
            while (bed.Unsettled > 0) bed.Relax(100000);
            float[] got = Volumes(bed, -90, 90);
            bool all = true;
            string what = "";
            foreach (int m in new int[] { SandMaterials.Sand, SandMaterials.Gravel, SandMaterials.Snow, SandMaterials.Mud })
            {
                float r = got[m] / poured[m];
                what += SandMaterials.Names[m] + " " + (r * 100f).ToString("F1") + "% ";
                if (Math.Abs(r - 1f) > 0.02f) all = false;
            }
            Check(all, "painted round and onto one another, and slumped, each medium keeps its volume (" + what.Trim() + ")");
            Console.WriteLine("at the sand/gravel/snow overlap: " + Stack(bed, 10, 0) + "; in the gravel ring: " + Stack(bed, 18, 0) + "; hole of the ring: " + Stack(bed, 0, 0));
            float[] amount = new float[SandBed.MaxLayers]; byte[] material = new byte[SandBed.MaxLayers];
            int n1 = bed.LayersAt(10, 0, amount, material);
            Check(n1 >= 2, "where they overlap there are layers, not one blended medium (" + n1 + ")");
            bool snowOnTop = bed.MaterialAt(10, 0) == SandMaterials.Snow;
            Check(snowOnTop, "and what was poured last is on top there");

            // Packed for the other players and unpacked: the same layers.
            SandBed copy = new SandBed(); copy.Enabled = true; copy.SetGround(new LGround()); copy.SetMaterials(table);
            foreach (SandBed.Chunk c in bed.Chunks) copy.Decode(bed.Encode(c));
            float[] gotCopy = Volumes(copy, -90, 90);
            bool same = true;
            for (int m = 0; m < 64; m++) if (Math.Abs(gotCopy[m] - got[m]) > 0.01f + got[m] * 0.002f) same = false;
            Check(same && Stack(copy, 10, 0) == Stack(bed, 10, 0), "packed and sent to another player, the layers come out the same (" + Stack(copy, 10, 0) + ")");

            // A copy kept for a run to start from.
            object kept = bed.Capture();
            bed.Clear();
            bed.Restore(kept);
            float[] gotKept = Volumes(bed, -90, 90);
            same = true;
            for (int m = 0; m < 64; m++) if (Math.Abs(gotKept[m] - got[m]) > 0.01f + got[m] * 0.002f) same = false;
            Check(same, "and so with the copy a run starts from");
        }

        Blast();
        Console.WriteLine(fails == 0 ? "ALL PASSED" : (fails + " FAILED"));
        return fails;
    }

    // An explosion blows a bowl out of a layered pile: none of the sand lost, every layer in it, heaped round the hole.
    static void Blast()
    {
        SandMaterials.Params[] table = SandMaterials.Build();
        float cell = SandBed.Cell, area = cell * cell;
        SandBed bed = new SandBed(); bed.Enabled = true; bed.SetGround(new LGround()); bed.SetMaterials(table);
        int[] media = { SandMaterials.Sand, SandMaterials.Gravel, SandMaterials.Snow };
        for (int i = -60; i < 60; i++) for (int j = -60; j < 60; j++)
            for (int m = 0; m < media.Length; m++) bed.AddVolume((i + 0.5f) * cell, (j + 0.5f) * cell, 0.3f * area, media[m], 0xFFFFFFFFu);
        while (bed.Unsettled > 0) bed.Relax(100000);
        float before = bed.Volume;
        float[] was = Volumes(bed, -90, 90);
        float middle = 0f, far = 0f; float t, b; uint c;
        bed.Thickness(0, 0, out t, out b, out c); middle = t - b;
        bed.Thickness(50, 50, out t, out b, out c); far = t - b;
        float blown = bed.Blast(new Vector3(0.06f, 0.5f, 0.06f), 3f, 0.6f);
        bed.Thickness(0, 0, out t, out b, out c); float after = t - b;
        float outside; bed.Thickness(50, 50, out t, out b, out c); outside = t - b;
        Console.WriteLine("blast: " + blown.ToString("F2") + " m^3 blown out, the middle went from " + (middle * 100f).ToString("F0") + " to " + (after * 100f).ToString("F0") + " cm");
        Check(blown > 0f, "a blast blows sand out");
        Check(Math.Abs(bed.Volume - before) < before * 0.001f, "no sand is lost: it is all in the bed, round the hole");
        Check(after < middle - 0.5f * 0.6f && Math.Abs(outside - far) < 1e-4f, "the middle is blown out to about the depth asked for, and nothing outside the radius is touched");
        float[] now = Volumes(bed, -90, 90);
        bool each = true; string what = "";
        foreach (int m in media)
        {
            float total = now[m];
            what += SandMaterials.Names[m] + " " + (total / was[m] * 100f).ToString("F1") + "% ";
            if (Math.Abs(total / was[m] - 1f) > 0.002f) each = false;
        }
        Check(each, "and each medium is all there, in the bed and the rim (" + what.Trim() + ")");
        BlastThinLayer();
    }

    // A blast puts what it takes into the rim beside the hole, all of it, including the slivers
    // that no column's share can hold. It used to skip any medium whose total across the whole
    // blast came to less than a hundredth of a column -- 144 cm^3 -- which is one column of thin
    // snow on top of a sand bed, and that snow was simply deleted. The bed here is three hundred
    // times that by volume, so only counting the medium says so; the totals all look fine.
    static void BlastThinLayer()
    {
        SandMaterials.Params[] table = SandMaterials.Build();
        float cell = SandBed.Cell, area = cell * cell;
        bool all = true; string what = "";
        foreach (int span in new int[] { 1, 2, 3 })
            foreach (float coat in new float[] { 0.005f, 0.009f, 0.02f })
            {
                SandBed bed = new SandBed(); bed.Enabled = true; bed.SetGround(new LGround()); bed.SetMaterials(table);
                for (int i = -60; i < 60; i++) for (int j = -60; j < 60; j++)
                    bed.AddVolume((i + 0.5f) * cell, (j + 0.5f) * cell, 0.4f * area, SandMaterials.Sand, 0xFFFFFFFFu);
                // Snow on a patch no wider than the crater, so only that much of it is taken.
                for (int i = 0; i < span; i++) for (int j = 0; j < span; j++)
                    bed.AddVolume((i + 0.5f) * cell, (j + 0.5f) * cell, coat * area, SandMaterials.Snow, 0xFFFFFFFFu);
                float was = Volumes(bed, -70, 70)[SandMaterials.Snow];
                float blown = bed.Blast(new Vector3(0.06f, 0.5f, 0.06f), 1f, 0.3f);
                float now = Volumes(bed, -70, 70)[SandMaterials.Snow];
                // One column of 5 mm is 72 cm^3, which is under the limit that used to skip it.
                if (blown <= 0f) continue;
                if (now < was * 0.9999f) { all = false; what += (was - now).ToString("E1") + " m^3 at " + span + "x" + span + "x" + (coat * 1000f).ToString("F0") + "mm, "; }
            }
        Check(all, "a blast over a column or two of a thin second medium puts it all in the rim ("
                    + (all ? "every size kept" : "lost " + what.TrimEnd(' ', ',')) + ")");
    }
}
