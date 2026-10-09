using System;
using System.IO;
using UnityEngine;
using SandMod;

/// <summary>
/// Writes the sand panel's folded button: the little pile of sand the panel becomes when it is
/// folded away, drawn by the same <see cref="SandIcon"/> the rest of the art comes from.
///
///     make-icon <out.png> [pixels]
///
/// It has to be a file rather than something the game draws at startup: the mod loader refuses
/// System.IO, so the mod cannot open an image from disk, and a mod's pictures reach it as
/// declared Resources instead. Mod.xml declares this one as SandIcon, and SandHud loads it
/// back for the button.
///
/// This is **not** the mod's Workshop thumbnail. That is Thumbnail.png, the artwork with the
/// mod's name on it, which make-thumbnail.sh copies into the mod folder. They are two pictures
/// on purpose: one is a button that has to read at 52 units, the other is a picture for a page.
/// They were one file for a while and it served neither -- the artwork does not read at 52
/// units, and a square icon is not a thumbnail anybody can tell the mod by.
///
/// Nothing here ships in the mod: the game cannot read a PNG from disk (the loader refuses
/// System.IO), so this file is produced out of band and reached as a declared Resource.
///
/// Pixel size must be a positive multiple of SandIcon.Design, or the drawing is resampled and
/// the sand's edges go soft -- which is most of what makes it read.
///
/// The PNG is written by hand rather than through Unity: EncodeToPNG needs the engine
/// running, and this runs with no engine and no display, like the tests do.
/// </summary>
public static class MakeIcon
{
    public static int Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("usage: make-thumbnail <out.png> [pixels]");
            return 2;
        }
        int n = args.Length > 1 ? int.Parse(args[1]) : 1024;
        if (n < SandIcon.Design || n % SandIcon.Design != 0)
        {
            Console.Error.WriteLine("pixels must be a multiple of " + SandIcon.Design);
            return 2;
        }

        Png.Write(args[0], n, SandIcon.Draw(n));

        Console.WriteLine("Wrote " + args[0] + " (" + n + "x" + n + ").");
        return 0;
    }

    
}

/// <summary>
/// The smallest PNG writer that will do: 8-bit RGBA, no interlacing, one IDAT. PNG's own
/// check words are easy to get subtly wrong, so they are spelled out rather than skimmed.
/// </summary>
static class Png
{
    static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };

    public static void Write(string path, int n, Color[] px)
    {
        // Each scanline is prefixed with its filter type. 0 (None) costs a little size and
        // is exactly right for art this smooth; the rows are near enough alike that the
        // filters which would compress them well are not worth their complexity here.
        byte[] raw = new byte[n * (1 + n * 4)];
        int at = 0;
        for (int y = 0; y < n; y++)
        {
            raw[at++] = 0;
            for (int x = 0; x < n; x++)
            {
                // Unity textures have (0,0) bottom-left; a PNG has it top-left, so the rows go out in reverse.
                Color c = px[(n - 1 - y) * n + x];
                raw[at++] = Byte(c.r);
                raw[at++] = Byte(c.g);
                raw[at++] = Byte(c.b);
                raw[at++] = Byte(c.a);
            }
        }

        using (FileStream f = new FileStream(path, FileMode.Create, FileAccess.Write))
        {
            f.Write(Signature, 0, Signature.Length);

            byte[] header = new byte[13];
            Int32(header, 0, n);            // width
            Int32(header, 4, n);            // height
            header[8] = 8;                   // bits per channel
            header[9] = 6;                   // colour type: truecolour with alpha
            header[10] = 0; header[11] = 0;  // deflate, adaptive filtering
            header[12] = 0;                  // no interlacing
            Chunk(f, "IHDR", header);
            Chunk(f, "IDAT", Zlib(raw));
            Chunk(f, "IEND", new byte[0]);
        }
    }

    static byte Byte(float v)
    {
        int i = (int)(v * 255f + 0.5f);
        return (byte)(i < 0 ? 0 : i > 255 ? 255 : i);
    }

    static void Int32(byte[] b, int at, int v)
    {
        b[at] = (byte)(v >> 24); b[at + 1] = (byte)(v >> 16);
        b[at + 2] = (byte)(v >> 8); b[at + 3] = (byte)v;
    }

    /// <summary>A length-prefixed, CRC'd chunk, as every chunk after the signature is.</summary>
    static void Chunk(Stream f, string type, byte[] data)
    {
        byte[] len = new byte[4];
        Int32(len, 0, data.Length);
        f.Write(len, 0, 4);

        byte[] body = new byte[4 + data.Length];
        for (int i = 0; i < 4; i++) body[i] = (byte)type[i];
        Array.Copy(data, 0, body, 4, data.Length);
        f.Write(body, 0, body.Length);

        byte[] crc = new byte[4];
        Int32(crc, 0, unchecked((int)Crc(body)));
        f.Write(crc, 0, 4);
    }

    /// <summary>The zlib stream a PNG's IDAT holds: a two-byte header, the deflated data,
    /// and Adler-32 of the uncompressed bytes.</summary>
    static byte[] Zlib(byte[] raw)
    {
        using (MemoryStream ms = new MemoryStream())
        {
            ms.WriteByte(0x78);      // deflate, 32K window
            ms.WriteByte(0x01);      // no preset dictionary, fastest level
            byte[] deflated = Deflate.Compress(raw);
            ms.Write(deflated, 0, deflated.Length);
            uint a = 1, b = 0;
            for (int i = 0; i < raw.Length; i++)
            {
                a = (a + raw[i]) % 65521;
                b = (b + a) % 65521;
            }
            ms.WriteByte((byte)(b >> 8));      // Adler-32: the high half then the low
            ms.WriteByte((byte)b);
            ms.WriteByte((byte)(a >> 8));
            ms.WriteByte((byte)a);
            return ms.ToArray();
        }
    }

    static uint[] table;

    /// <summary>CRC-32 as PNG uses it: the reflected 0xEDB88320 polynomial, bit-flipped
    /// in and out. Built once; 256 entries is a moment's work.</summary>
    static uint Crc(byte[] b)
    {
        if (table == null)
        {
            table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                table[i] = c;
            }
        }
        uint crc = 0xFFFFFFFFu;
        for (int i = 0; i < b.Length; i++) crc = table[(crc ^ b[i]) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }
}

/// <summary>
/// DEFLATE, in as little as will do, because Mono's own DeflateStream is no use here:
/// it binds to a native helper (MonoPosixHelper) that is not present in the game
/// installation this runs on, and the art is one flat buffer that compresses well enough
/// with the simplest scheme that is legal.
///
/// One fixed-Huffman block, and LZ77 over a hash chain: for each position the hash of the
/// three bytes there gives a bucket of earlier positions with the same three bytes, and
/// the longest of those (within the window, and within a bounded number of candidates) is
/// emitted as a back-reference. Fixed Huffman costs a few bits per literal on the rarer
/// bytes and needs no code table in the file; on art this smooth the back-references more
/// than pay for it. A PNG reader must accept it, and every one does.
///
/// This is the one part of the tool where being subtly wrong would be quiet, so it is
/// written to the spec's tables rather than to a remembered shortcut.
/// </summary>
static class Deflate
{
    /// <summary>Length codes 257..285: the shortest match each stands for, and how many
    /// extra bits follow it (RFC 1950, section 3.2.5).</summary>
    private static readonly int[] LenBase = { 3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31,
                                               35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258 };
    private static readonly int[] LenExtra = { 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2,
                                                3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0 };

    /// <summary>Distance codes 0..29: the distance each stands for, and its extra bits.</summary>
    private static readonly int[] DistBase = { 1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193,
                                                257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577 };
    private static readonly int[] DistExtra = { 0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6,
                                                 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13 };

    private const int MinMatch = 3;
    private const int MaxMatch = 258;
    private const int Window = 32768;
    private const int HashBits = 15;
    private const int HashSize = 1 << HashBits;

    /// <summary>How far back the hash chain is walked. Long enough to find the long runs
    /// this art is full of, short enough that the walk stays a moment's work on a
    /// megapixel.</summary>
    private const int MaxChain = 96;

    public static byte[] Compress(byte[] data)
    {
        int n = data.Length;
        int[] head = new int[HashSize];
        for (int i = 0; i < HashSize; i++) head[i] = -1;
        int[] prev = new int[n < 1 ? 1 : n];

        Bits bits = new Bits(n / 2 + 64);
        bits.Put(1, 1);        // final block
        bits.Put(1, 2);        // fixed Huffman

        int at = 0;
        while (at < n)
        {
            int bestLen = 0, bestDist = 0;
            if (at + MinMatch <= n)
            {
                uint h = Hash(data, at);
                int cand = head[h];
                int limit = at - Window;
                for (int chain = MaxChain; cand >= 0 && cand > limit && chain > 0; chain--)
                {
                    int len = Match(data, cand, at, n);
                    if (len > bestLen)
                    {
                        bestLen = len;
                        bestDist = at - cand;
                        if (len == MaxMatch) break;
                    }
                    cand = prev[cand];
                }
                prev[at] = head[h];
                head[h] = at;
            }

            if (bestLen >= MinMatch)
            {
                Match_(bits, bestLen, bestDist);
                // Everything the match covered joins the chains too, or a run of matched
                // bytes hides every match that starts inside it.
                for (int i = at + 1; i < at + bestLen && i + MinMatch <= n; i++)
                {
                    uint h = Hash(data, i);
                    prev[i] = head[h];
                    head[h] = i;
                }
                at += bestLen;
            }
            else
            {
                Literal(bits, data[at]);
                at++;
            }
        }
        Literal(bits, 256);        // end of block
        return bits.Bytes();
    }

    /// <summary>How far a match starting at <c>earlier</c> runs, up to the one at <c>at</c>.</summary>
    private static int Match(byte[] d, int earlier, int at, int n)
    {
        int len = 0;
        while (len < MaxMatch && at + len < n && d[earlier + len] == d[at + len]) len++;
        return len;
    }

    private static uint Hash(byte[] d, int at)
    {
        return ((uint)d[at] << 10 ^ (uint)d[at + 1] << 5 ^ (uint)d[at + 2]) & (HashSize - 1);
    }

    private static void Literal(Bits bits, int symbol)
    {
        // The fixed code for a literal or the end marker, per RFC 1951 section 3.2.6.
        if (symbol < 144) bits.Code(0x30 + symbol, 8);
        else if (symbol < 256) bits.Code(0x190 + symbol - 144, 9);
        else if (symbol < 280) bits.Code(symbol - 256, 7);
        else bits.Code(0xC0 + symbol - 280, 8);
    }

    private static void Match_(Bits bits, int len, int dist)
    {
        int lc = 28;
        while (lc > 0 && LenBase[lc] > len) lc--;
        Literal(bits, 257 + lc);
        if (LenExtra[lc] > 0) bits.Put((uint)(len - LenBase[lc]), LenExtra[lc]);

        int dc = 29;
        while (dc > 0 && DistBase[dc] > dist) dc--;
        bits.Code(dc, 5);                       // distances are five fixed bits, all of them
        if (DistExtra[dc] > 0) bits.Put((uint)(dist - DistBase[dc]), DistExtra[dc]);
    }

    /// <summary>
    /// Bits out, least significant bit of each byte first, as DEFLATE packs them -- except
    /// Huffman codes, which are written most significant bit first, so <see cref="Code"/>
    /// turns one round before handing it over. Its extra bits are the other way round,
    /// which is why they go through <see cref="Put"/> directly.
    /// </summary>
    private class Bits
    {
        byte[] buf;
        int at;
        uint acc;
        int held;

        public Bits(int size) { buf = new byte[size < 16 ? 16 : size]; }

        public void Put(uint v, int count)
        {
            acc |= (v & (count == 32 ? 0xFFFFFFFFu : (1u << count) - 1u)) << held;
            held += count;
            while (held >= 8)
            {
                Grow(at + 1);
                buf[at++] = (byte)(acc & 0xFF);
                acc >>= 8;
                held -= 8;
            }
        }

        /// <summary>A Huffman code: written most significant bit first.</summary>
        public void Code(int value, int count)
        {
            uint reversed = 0;
            for (int i = 0; i < count; i++) reversed |= (uint)(((value >> i) & 1) << (count - 1 - i));
            Put(reversed, count);
        }

        void Grow(int need)
        {
            if (need <= buf.Length) return;
            int size = buf.Length;
            while (size < need) size *= 2;
            byte[] bigger = new byte[size];
            Array.Copy(buf, bigger, buf.Length);
            buf = bigger;
        }

        public byte[] Bytes()
        {
            if (held > 0)
            {
                Grow(at + 1);
                buf[at++] = (byte)(acc & 0xFF);
                acc = 0;
                held = 0;
            }
            byte[] outBuf = new byte[at];
            Array.Copy(buf, outBuf, at);
            return outBuf;
        }
    }
}