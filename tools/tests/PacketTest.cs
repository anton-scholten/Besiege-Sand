using System;
using System.Collections.Generic;
using UnityEngine;
using SandMod;

/// <summary>
/// A packet that arrived from another player.
///
/// The brush messages are guarded number by number (GuardTest), but a chunk is not numbers in a
/// list -- it is a byte stream that decides how big an array to make and which element of a
/// column table to read, and the sizes it decides are cast from <c>uint</c> to <c>int</c> on the
/// way in. Nothing on the path in catches an exception: SandNet hands the bytes to SandField,
/// which hands them to the bed. So this suite says two things: a packet built to ask for something
/// impossible is refused rather than obeyed, and no amount of damage makes the decoder throw.
/// </summary>
class PacketTest
{
    static int fails = 0;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) fails++; }

    /// <summary>2^32-1 as the five bytes of varint the reader accepts.</summary>
    static readonly byte[] Huge = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x0F };

    /// <summary>A packet of one deep column, saying which column and how many layers it has.
    /// Everything else is an empty chunk, which is the smallest thing that reaches the section.</summary>
    static byte[] OneDeep(byte version, byte[] column, byte[] layers)
    {
        List<byte> s = new List<byte>();
        s.Add(version);                 // the format's version
        s.Add(0x00);                    // chunk x
        s.Add(0x00);                    // chunk z
        s.Add(0x00);                    // the lowest column
        for (int i = 0; i < 256; i++) s.Add(0x00);   // every column empty
        s.Add(0x00);                    // no palette
        s.Add(0x01);                    // one column has layers under it
        s.AddRange(column);
        s.AddRange(layers);
        // and one layer's own bytes: its medium, the height it reaches, its colour. Without these
        // the reader runs off the end and refuses the packet, which would let the well-formed
        // case below pass for the wrong reason.
        s.Add(0x01); s.Add(0x64); s.Add(0x11); s.Add(0x22); s.Add(0x33); s.Add(0x44);
        return Lz.Compress(s.ToArray(), s.Count);
    }

    /// <summary>Does it hand back false, rather than obeying it or throwing?</summary>
    static bool Refused(byte[] packet)
    {
        try { return !BedCodec.Unpack(packet, new SandBed.Chunk()); }
        catch (System.Exception e) { Console.WriteLine("      threw " + e.GetType().Name); return false; }
    }

    public static int Main(string[] a)
    {
        // A real packet, to take its version byte from: the reader's, not a number guessed here.
        SandBed.Chunk c = new SandBed.Chunk();
        c.cx = 0; c.cz = 0; c.filled = 1;
        c.top[0] = 0.50f; c.bottom[0] = 0f; c.material[0] = 1; c.packed[0] = 0f;
        c.split[0] = 0.20f;
        SandBed.Deep d = new SandBed.Deep();
        d.material = 1; d.upper = 0.10f; d.colour = 0xFF203040u;
        List<SandBed.Deep> dl = new List<SandBed.Deep>();
        dl.Add(d);
        Dictionary<int, List<SandBed.Deep>> dd = new Dictionary<int, List<SandBed.Deep>>();
        dd[0] = dl;
        c.deep = dd;
        byte[] good = BedCodec.Pack(c, 0.004f);
        byte version = Lz.Decompress(good)[0];

        Check(Refused(OneDeep(version, Huge, new byte[] { 0x01 })),
              "a column index of 2^32-1 -- an int of -1, which is not over the limit -- is refused");
        Check(Refused(OneDeep(version, new byte[] { 0x00 }, Huge)),
              "a layer count of 2^32-1 is refused before it sizes a list to -1");
        Check(Refused(OneDeep(version, new byte[] { 0x80, 0x02 }, new byte[] { 0x01 })),
              "and a column index one past the last one is still refused");

        // An honest one of the same shape is taken, so the checks above are not passing by
        // refusing everything.
        Check(!Refused(OneDeep(version, new byte[] { 0x00 }, new byte[] { 0x01 })),
              "a well-formed packet with one layer is still read");

        // Damage. Single bytes cannot reach the deep section -- it wants a five-byte varint --
        // so this walks every offset and overwrites a run of five, which is what it takes to
        // make a number there absurd, and also a run of three, for the shorter fields.
        byte[] raw = Lz.Decompress(good);
        int threw = 0; string first = null;
        System.Action<byte[]> attempt = delegate(byte[] packet)
        {
            try { BedCodec.Unpack(packet, new SandBed.Chunk()); }
            catch (System.Exception e) { threw++; if (first == null) first = e.GetType().Name + " at " + e.Message; }
        };
        for (int i = 0; i + 4 < raw.Length; i++)
            for (int v = 0; v < 256; v += 1)
            {
                byte[] d2 = (byte[])raw.Clone();
                for (int q = 0; q < 5; q++) d2[i + q] = (byte)v;
                attempt(Lz.Compress(d2, d2.Length));
            }
        Check(threw == 0, "a run of five bytes changed anywhere in a chunk's stream is refused or read, never thrown on ("
                          + threw + " threw" + (first == null ? "" : ", first " + first) + ")");

        threw = 0;
        for (int n = 0; n <= raw.Length; n++)
        {
            byte[] d2 = new byte[n];
            Array.Copy(raw, d2, n);
            attempt(Lz.Compress(d2, n));
        }
        Check(threw == 0, "the same stream cut short at every length, never thrown on (" + threw + " threw)");

        // The size the decompressor takes before reading a token. A chunk's columns measure under
        // 3 kB at their worst, so anything near the old 64 MB is a peer asking for memory.
        List<byte> big = new List<byte>();
        big.Add(0xFF); big.Add(0xFF); big.Add(0xFF); big.Add(0xFF); big.Add(0x07);   // 2^31-1
        Check(Lz.Decompress(big.ToArray()) == null, "a stream claiming to be 2 GB is refused rather than allocated");

        Console.WriteLine(fails == 0 ? "ALL PASSED" : (fails + " FAILED"));
        return fails;
    }
}