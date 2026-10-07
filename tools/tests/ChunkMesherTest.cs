using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using SandMod;

class MG : IBedGround { public float Height(float x, float z) { return 0.2f * Mathf.Sin(x * 0.7f) * Mathf.Cos(z * 0.5f); } }

/// <summary>
/// The chunk mesher: the light must not change across a chunk's seam (it did, in a grid every sixteen
/// columns), and running it on several threads must give exactly what one thread gives.
/// </summary>
class ChunkMesherTest
{
    static int fails = 0;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) fails++; }

    static SandBed Bumpy()
    {
        SandBed bed = new SandBed(); bed.Enabled = true; bed.SetGround(new MG()); bed.SetMaterials(SandMaterials.Build());
        float cell = SandBed.Cell;
        for (int i = -48; i < 48; i++) for (int j = -48; j < 48; j++)
            bed.AddVolume((i + 0.5f) * cell, (j + 0.5f) * cell, (0.3f + 0.25f * Mathf.Sin(i * 0.31f) * Mathf.Cos(j * 0.23f)) * cell * cell, i % 7 == 0 ? 1 : 0, 0xFFC8B478u);
        return bed;
    }

    static ChunkDraw DrawOf(SandBed.Chunk c) { return (ChunkDraw)c.renderHandle; }

    public static int Main(string[] args)
    {
        SandBed bed = Bumpy();
        int[] looks = new int[SandMaterials.FirstVariant];
        for (int i = 0; i < looks.Length; i++) looks[i] = i % SandMaterials.Count;
        List<SandBed.Chunk> chunks = new List<SandBed.Chunk>(bed.Chunks);
        MG ground = new MG();

        // Serial.
        ChunkMesherPool alone = new ChunkMesherPool(false);
        alone.Run(bed, ground, chunks, chunks.Count, looks, true);

        // The seam: vertex 16 of a chunk is vertex 0 of the next one over, and has to be lit the same.
        double worst = 0; int seams = 0;
        foreach (SandBed.Chunk c in chunks)
        {
            SandBed.Chunk e = bed.ChunkAt(c.cx + 1, c.cz), n = bed.ChunkAt(c.cx, c.cz + 1);
            for (int v = 0; v < ChunkDraw.Verts; v++)
            {
                if (e != null) { Vector3 a = DrawOf(c).normals[16 + v * 17], b = DrawOf(e).normals[0 + v * 17]; worst = Math.Max(worst, (a - b).magnitude); seams++; }
                if (n != null) { Vector3 a = DrawOf(c).normals[v + 16 * 17], b = DrawOf(n).normals[v + 0 * 17]; worst = Math.Max(worst, (a - b).magnitude); seams++; }
            }
        }
        Check(worst < 1e-4, "the normal is the same either side of a chunk's seam (" + seams + " pairs, worst difference " + worst.ToString("E1") + ")");

        // Keep the serial answer, then redo it on every core.
        List<Vector3[]> v1 = new List<Vector3[]>(), n1 = new List<Vector3[]>(); List<Color32[]> c1 = new List<Color32[]>(); List<int[]> t1 = new List<int[]>();
        foreach (SandBed.Chunk c in chunks) { ChunkDraw d = DrawOf(c); v1.Add((Vector3[])d.vertices.Clone()); n1.Add((Vector3[])d.normals.Clone()); c1.Add((Color32[])d.colours.Clone()); t1.Add((int[])d.tris.Clone()); }
        ChunkMesherPool many = new ChunkMesherPool();
        many.Run(bed, ground, chunks, chunks.Count, looks, true);
        int diff = 0;
        for (int k = 0; k < chunks.Count; k++)
        {
            ChunkDraw d = DrawOf(chunks[k]);
            for (int q = 0; q < d.vertices.Length; q++)
                if (d.vertices[q] != v1[k][q] || d.normals[q] != n1[k][q] || d.colours[q].r != c1[k][q].r || d.colours[q].g != c1[k][q].g || d.colours[q].b != c1[k][q].b) diff++;
            if (d.tris.Length != t1[k].Length) diff++;
            else for (int q = 0; q < d.tris.Length; q++) if (d.tris[q] != t1[k][q]) { diff++; break; }
        }
        Check(diff == 0, "on " + many.Threads + " threads the chunks come out exactly as on one (" + diff + " differences in " + chunks.Count + " chunks)");

        // How long.
        Stopwatch sw = Stopwatch.StartNew();
        for (int r = 0; r < 20; r++) alone.Run(bed, ground, chunks, chunks.Count, looks, true);
        double one = sw.Elapsed.TotalMilliseconds / 20;
        sw.Reset(); sw.Start();
        for (int r = 0; r < 20; r++) many.Run(bed, ground, chunks, chunks.Count, looks, true);
        double all = sw.Elapsed.TotalMilliseconds / 20;
        Console.WriteLine(chunks.Count + " chunks: one thread " + one.ToString("F1") + " ms, " + many.Threads + " threads " + all.ToString("F1") + " ms (" + (one / all).ToString("F1") + "x)");
        Check(many.Threads == 1 || all < one, "more threads are quicker");

        Console.WriteLine(fails == 0 ? "ALL PASSED" : (fails + " FAILED"));
        return fails;
    }
}
