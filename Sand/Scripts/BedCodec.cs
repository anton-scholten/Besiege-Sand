using System.Collections.Generic;

namespace SandMod
{
    /// <summary>
    /// A chunk of bed packed small, for keeping sand between runs, saving it with a level and
    /// sending it to the other players. A chunk in memory is about 6.6 KB; packed, a typical
    /// one is a few hundred bytes, and an even one -- a region's flat top -- under a hundred.
    ///
    ///   Heights are whole millimetres, and each is stored as how far it is from what its
    ///   neighbours predict (the left, the one behind and the one diagonally between, as
    ///   lossless image coders do). Sand is smooth, so nearly every number is 0 or 1.
    ///   Colours go into a palette of the chunk's own, and each column's is an index of only as
    ///   many bits as the palette needs.
    ///   The whole is then run through a small LZ compressor, which folds the runs of zeros,
    ///   the repeated media and the empty columns into almost nothing.
    ///
    /// Written by hand, as Bytes is: System.IO.Compression is one of the namespaces the mod
    /// loader refuses.
    /// </summary>
    public static class BedCodec
    {
        private const byte Version = 1;
        private const int Side = SandBed.ChunkSide;
        private const int Columns = Side * Side;

        // ------------------------------------------------------------------ chunks

        /// <summary>The chunk as packed bytes.</summary>
        // Scratch, kept: packing runs for hundreds of chunks after a run starts, and fresh
        // buffers for each were tens of kilobytes of garbage a chunk -- enough to bring on the
        // collector, whose pauses landed in whatever the sand was doing at the time. Only ever
        // used from the main thread.
        private static readonly Writer packWriter = new Writer(4096);
        private static readonly byte[] packKind = new byte[Columns];
        private static readonly int[] packTop = new int[Columns], packBottom = new int[Columns];
        private static readonly int[] packAt = new int[Columns * 2];
        private static readonly List<uint> packPalette = new List<uint>();
        private static readonly Dictionary<uint, int> packIndex = new Dictionary<uint, int>();
        private static readonly byte[] unKind = new byte[Columns];
        private static readonly int[] unTop = new int[Columns], unBottom = new int[Columns];
        private static uint[] unPalette = new uint[64];

        public static byte[] Pack(SandBed.Chunk c, float minThickness)
        {
            return Pack(c, minThickness, null);
        }

        /// <summary>The same, with every row renumbered through <paramref name="map"/>.</summary>
        public static byte[] Pack(SandBed.Chunk c, float minThickness, int[] map)
        {
            Writer w = packWriter;
            w.Length = 0;
            w.Byte(Version);
            w.Signed(c.cx);
            w.Signed(c.cz);

            byte[] kind = packKind;
            int[] top = packTop, bottom = packBottom;
            System.Array.Clear(kind, 0, Columns);
            int lowest = int.MaxValue;
            for (int k = 0; k < Columns; k++)
            {
                if (c.top[k] - c.bottom[k] <= minThickness) continue;
                bool two = c.split[k] > c.bottom[k] + 1e-5f && c.split[k] < c.top[k] - 1e-5f;
                kind[k] = (byte)(two ? 2 : 1);
                bottom[k] = Mm(c.bottom[k]);
                top[k] = Mm(c.top[k]);
                if (top[k] <= bottom[k]) top[k] = bottom[k] + 1;
                if (bottom[k] < lowest) lowest = bottom[k];
            }
            if (lowest == int.MaxValue) lowest = 0;
            w.Signed(lowest);
            for (int k = 0; k < Columns; k++) w.Byte(kind[k]);

            // Heights, from what the neighbours predict.
            Residuals(w, kind, top, lowest);
            Residuals(w, kind, bottom, lowest);

            for (int k = 0; k < Columns; k++) if (kind[k] != 0) w.Byte(Mapped(c.material[k], map));
            for (int k = 0; k < Columns; k++) if (kind[k] != 0) w.Unsigned((uint)UnityEngine.Mathf.Max(0, Mm(c.packed[k])));
            for (int k = 0; k < Columns; k++)
                if (kind[k] == 2)
                {
                    w.Unsigned((uint)UnityEngine.Mathf.Max(0, Mm(c.split[k]) - bottom[k]));
                    w.Byte(Mapped(c.lowMaterial[k], map));
                }

            // Colours: a palette and an index each.
            List<uint> palette = packPalette;
            Dictionary<uint, int> index = packIndex;
            palette.Clear(); index.Clear();
            int[] at = packAt;
            int n = 0;
            for (int pass = 0; pass < 2; pass++)
                for (int k = 0; k < Columns; k++)
                {
                    if (kind[k] == 0 || (pass == 1 && kind[k] != 2)) continue;
                    uint colour = pass == 0 ? c.colour[k] : c.lowColour[k];
                    int p;
                    if (!index.TryGetValue(colour, out p)) { p = palette.Count; palette.Add(colour); index[colour] = p; }
                    at[n++] = p;
                }
            w.Unsigned((uint)palette.Count);
            for (int p = 0; p < palette.Count; p++) w.Int(unchecked((int)palette[p]));
            int bits = Bits(palette.Count);
            uint acc = 0; int held = 0;
            for (int q = 0; q < n; q++)
            {
                acc |= (uint)at[q] << held;
                held += bits;
                while (held >= 8) { w.Byte((byte)acc); acc >>= 8; held -= 8; }
            }
            if (held > 0) w.Byte((byte)acc);

            return Lz.Compress(w.Buffer, w.Length);
        }

        /// <summary>
        /// Unpacks a chunk into <paramref name="into"/>, which takes its place and columns; false
        /// for bytes that are not a packed chunk.
        /// </summary>
        public static bool Unpack(byte[] packed, SandBed.Chunk into)
        {
            byte[] b = Lz.Decompress(packed);
            if (b == null || b.Length < 4 || b[0] != Version) return false;
            Reader r = new Reader(b, 1);
            into.cx = r.Signed();
            into.cz = r.Signed();
            int lowest = r.Signed();
            byte[] kind = unKind;
            for (int k = 0; k < Columns; k++) kind[k] = r.Byte();

            int[] top = unTop, bottom = unBottom;
            Unresiduals(r, kind, top, lowest);
            Unresiduals(r, kind, bottom, lowest);

            int filled = 0;
            for (int k = 0; k < Columns; k++)
            {
                into.top[k] = into.bottom[k] = into.split[k] = into.packed[k] = 0f;
                into.material[k] = into.lowMaterial[k] = 0;
                into.colour[k] = into.lowColour[k] = 0;
                if (kind[k] == 0) continue;
                filled++;
                into.bottom[k] = bottom[k] * 0.001f;
                into.top[k] = top[k] * 0.001f;
                into.split[k] = into.bottom[k];
            }
            into.filled = filled;

            for (int k = 0; k < Columns; k++) if (kind[k] != 0) into.material[k] = r.Byte();
            for (int k = 0; k < Columns; k++) if (kind[k] != 0) into.packed[k] = r.Unsigned() * 0.001f;
            for (int k = 0; k < Columns; k++)
                if (kind[k] == 2)
                {
                    into.split[k] = (bottom[k] + (int)r.Unsigned()) * 0.001f;
                    into.lowMaterial[k] = r.Byte();
                }

            int count = (int)r.Unsigned();
            if (count > 65536) return false;
            if (unPalette.Length < System.Math.Max(count, 1)) unPalette = new uint[System.Math.Max(count, 1)];
            uint[] palette = unPalette;
            for (int p = 0; p < count; p++) palette[p] = unchecked((uint)r.Int());
            int bits = Bits(count);
            uint mask = bits >= 32 ? 0xFFFFFFFFu : (1u << bits) - 1u;
            uint acc = 0; int held = 0;
            for (int pass = 0; pass < 2; pass++)
                for (int k = 0; k < Columns; k++)
                {
                    if (kind[k] == 0 || (pass == 1 && kind[k] != 2)) continue;
                    while (held < bits) { acc |= (uint)r.Byte() << held; held += 8; }
                    int p = (int)(acc & mask);
                    acc = bits >= 32 ? 0 : acc >> bits;
                    held -= bits;
                    uint colour = palette[p < count ? p : 0];
                    if (pass == 0) into.colour[k] = colour; else into.lowColour[k] = colour;
                }
            return !r.Overrun;
        }

        private static byte Mapped(byte row, int[] map)
        {
            return map != null && row < map.Length ? (byte)map[row] : row;
        }

        private static int Mm(float metres)
        {
            return (int)System.Math.Round(metres * 1000.0);
        }

        private static int Bits(int count)
        {
            int bits = 0;
            while ((1 << bits) < count) bits++;
            return bits;
        }

        /// <summary>
        /// The median edge predictor: the left neighbour, the one behind, or the plane through
        /// them and the diagonal, whichever lies between -- exact on a slope and on a step.
        /// Empty neighbours are stood in for by the last value written.
        /// </summary>
        private static int Predict(byte[] kind, int[] v, int k, int last)
        {
            int x = k % Side, y = k / Side;
            bool hasL = x > 0 && kind[k - 1] != 0, hasU = y > 0 && kind[k - Side] != 0;
            bool hasUL = x > 0 && y > 0 && kind[k - Side - 1] != 0;
            if (hasL && hasU && hasUL)
            {
                int a = v[k - 1], b = v[k - Side], c = v[k - Side - 1];
                int lo = a < b ? a : b, hi = a < b ? b : a;
                if (c >= hi) return lo;
                if (c <= lo) return hi;
                return a + b - c;
            }
            if (hasL) return v[k - 1];
            if (hasU) return v[k - Side];
            return last;
        }

        private static void Residuals(Writer w, byte[] kind, int[] v, int start)
        {
            int last = start;
            for (int k = 0; k < Columns; k++)
            {
                if (kind[k] == 0) continue;
                w.Signed(v[k] - Predict(kind, v, k, last));
                last = v[k];
            }
        }

        private static void Unresiduals(Reader r, byte[] kind, int[] v, int start)
        {
            int last = start;
            for (int k = 0; k < Columns; k++)
            {
                if (kind[k] == 0) continue;
                v[k] = Predict(kind, v, k, last) + r.Signed();
                last = v[k];
            }
        }

        // ------------------------------------------------------------------ bytes

        /// <summary>A growing byte buffer with varints: small numbers take one byte.</summary>
        public class Writer
        {
            public byte[] Buffer;
            public int Length;

            public Writer(int capacity) { Buffer = new byte[System.Math.Max(capacity, 16)]; }

            private void Room(int n)
            {
                if (Length + n <= Buffer.Length) return;
                int size = Buffer.Length * 2;
                while (size < Length + n) size *= 2;
                byte[] grown = new byte[size];
                System.Array.Copy(Buffer, grown, Length);
                Buffer = grown;
            }

            public void Byte(byte v) { Room(1); Buffer[Length++] = v; }

            public void Unsigned(uint v)
            {
                Room(5);
                while (v >= 0x80) { Buffer[Length++] = (byte)(v | 0x80); v >>= 7; }
                Buffer[Length++] = (byte)v;
            }

            public void Signed(int v) { Unsigned((uint)((v << 1) ^ (v >> 31))); }

            public void Int(int v)
            {
                Room(4);
                Buffer[Length++] = (byte)v; Buffer[Length++] = (byte)(v >> 8);
                Buffer[Length++] = (byte)(v >> 16); Buffer[Length++] = (byte)(v >> 24);
            }

            public void Bytes(byte[] b, int n) { Bytes(b, 0, n); }

            public void Bytes(byte[] b, int from, int n)
            {
                Room(n);
                System.Array.Copy(b, from, Buffer, Length, n);
                Length += n;
            }

            public byte[] ToArray()
            {
                byte[] b = new byte[Length];
                System.Array.Copy(Buffer, b, Length);
                return b;
            }
        }

        /// <summary>Reads what a Writer wrote. Reading past the end gives zeros and sets Overrun.</summary>
        public class Reader
        {
            private readonly byte[] b;
            public int At;
            public bool Overrun;

            public Reader(byte[] bytes, int at) { b = bytes; At = at; }

            public byte Byte()
            {
                if (At >= b.Length) { Overrun = true; return 0; }
                return b[At++];
            }

            public uint Unsigned()
            {
                uint v = 0;
                for (int shift = 0; shift < 35; shift += 7)
                {
                    byte x = Byte();
                    v |= (uint)(x & 0x7F) << shift;
                    if ((x & 0x80) == 0) break;
                }
                return v;
            }

            public int Signed() { uint u = Unsigned(); return (int)(u >> 1) ^ -(int)(u & 1); }

            public int Int() { return Byte() | (Byte() << 8) | (Byte() << 16) | (Byte() << 24); }

            public byte[] Bytes(int n)
            {
                if (n < 0 || At + n > b.Length) { Overrun = true; return new byte[0]; }
                byte[] o = new byte[n];
                System.Array.Copy(b, At, o, 0, n);
                At += n;
                return o;
            }
        }
    }

    /// <summary>
    /// A small LZ77 compressor in the manner of LZ4: runs of literal bytes and copies of earlier
    /// bytes, found through a hash of every four. Fast both ways, and good at what packed sand
    /// is made of -- zeros, repeats and runs.
    /// </summary>
    public static class Lz
    {
        private const int MinMatch = 4;
        private const int HashBits = 13;
        private const int Window = 65535;

        private static readonly int[] table = new int[1 << HashBits];
        private static readonly BedCodec.Writer lzWriter = new BedCodec.Writer(4096);

        /// <summary>Main thread only: the hash table and the output buffer are kept between calls.</summary>
        public static byte[] Compress(byte[] src, int length)
        {
            BedCodec.Writer w = lzWriter;
            w.Length = 0;
            w.Unsigned((uint)length);
            for (int t = 0; t < table.Length; t++) table[t] = -1;

            int anchor = 0, i = 0;
            int limit = length - MinMatch;
            while (i <= limit)
            {
                int h = Hash(src, i);
                int cand = table[h];
                table[h] = i;
                if (cand >= 0 && i - cand <= Window
                    && src[cand] == src[i] && src[cand + 1] == src[i + 1]
                    && src[cand + 2] == src[i + 2] && src[cand + 3] == src[i + 3])
                {
                    int len = MinMatch;
                    while (i + len < length && src[cand + len] == src[i + len]) len++;
                    Sequence(w, src, anchor, i - anchor, i - cand, len);
                    // Remember a few positions inside the match, so the next one can find them.
                    int end = i + len;
                    for (int p = i + 1; p < end && p <= limit; p += 2) table[Hash(src, p)] = p;
                    i = end;
                    anchor = i;
                }
                else i++;
            }
            // The tail: literals only.
            Sequence(w, src, anchor, length - anchor, 0, 0);
            return w.ToArray();
        }

        public static byte[] Decompress(byte[] data)
        {
            if (data == null || data.Length == 0) return null;
            BedCodec.Reader r = new BedCodec.Reader(data, 0);
            int length = (int)r.Unsigned();
            if (length < 0 || length > 64 * 1024 * 1024) return null;
            byte[] o = new byte[length];
            int n = 0;
            while (n < length && !r.Overrun)
            {
                int token = r.Byte();
                int lit = token >> 4;
                if (lit == 15) lit += Extra(r);
                if (n + lit > length || r.At + lit > data.Length) return null;
                System.Array.Copy(data, r.At, o, n, lit);
                r.At += lit; n += lit;
                if (n >= length) break;
                int offset = r.Byte() | (r.Byte() << 8);
                int len = token & 15;
                if (len == 15) len += Extra(r);
                len += MinMatch;
                if (offset <= 0 || offset > n || n + len > length) return null;
                // Byte by byte: a copy may overlap what it is writing, which is how a run repeats.
                for (int q = 0; q < len; q++, n++) o[n] = o[n - offset];
            }
            return r.Overrun ? null : o;
        }

        private static int Hash(byte[] b, int i)
        {
            uint v = (uint)(b[i] | (b[i + 1] << 8) | (b[i + 2] << 16) | (b[i + 3] << 24));
            return (int)((v * 2654435761u) >> (32 - HashBits));
        }

        private static void Sequence(BedCodec.Writer w, byte[] src, int from, int lit, int offset, int len)
        {
            int ml = len > 0 ? len - MinMatch : 0;
            int token = (System.Math.Min(lit, 15) << 4) | System.Math.Min(ml, 15);
            w.Byte((byte)token);
            if (lit >= 15) More(w, lit - 15);
            w.Bytes(src, from, lit);
            if (len == 0) return;
            w.Byte((byte)offset);
            w.Byte((byte)(offset >> 8));
            if (ml >= 15) More(w, ml - 15);
        }

        private static void More(BedCodec.Writer w, int v)
        {
            while (v >= 255) { w.Byte(255); v -= 255; }
            w.Byte((byte)v);
        }

        private static int Extra(BedCodec.Reader r)
        {
            int v = 0, x;
            do { x = r.Byte(); v += x; } while (x == 255 && !r.Overrun);
            return v;
        }
    }
}
