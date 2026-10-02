namespace SandMod
{
    /// <summary>
    /// Packing numbers into a byte array by hand, little-endian, for the multiplayer messages.
    /// BinaryWriter would do it, but every System.IO type a mod touches is one more thing to
    /// check against the loader's blacklist; these are a few shifts.
    /// </summary>
    public static class Bytes
    {
        public static int PutInt(byte[] b, int n, int v)
        {
            b[n] = (byte)v; b[n + 1] = (byte)(v >> 8); b[n + 2] = (byte)(v >> 16); b[n + 3] = (byte)(v >> 24);
            return n + 4;
        }

        public static int GetInt(byte[] b, ref int n)
        {
            int v = b[n] | (b[n + 1] << 8) | (b[n + 2] << 16) | (b[n + 3] << 24);
            n += 4;
            return v;
        }

        public static int PutFloat(byte[] b, int n, float v)
        {
            return PutInt(b, n, System.BitConverter.ToInt32(System.BitConverter.GetBytes(v), 0));
        }

        public static float GetFloat(byte[] b, ref int n)
        {
            float v = System.BitConverter.ToSingle(b, n);
            n += 4;
            return v;
        }

        /// <summary>Sixteen bits, clamped to what they hold.</summary>
        public static int PutShort(byte[] b, int n, int v)
        {
            if (v < short.MinValue) v = short.MinValue;
            if (v > ushort.MaxValue) v = ushort.MaxValue;
            b[n] = (byte)v; b[n + 1] = (byte)(v >> 8);
            return n + 2;
        }

        public static short GetShort(byte[] b, ref int n)
        {
            short v = (short)(b[n] | (b[n + 1] << 8));
            n += 2;
            return v;
        }

        /// <summary>A packed colour's red, green and blue; alpha is always opaque.</summary>
        public static int PutColour(byte[] b, int n, uint c)
        {
            b[n] = (byte)c; b[n + 1] = (byte)(c >> 8); b[n + 2] = (byte)(c >> 16);
            return n + 3;
        }

        public static uint GetColour(byte[] b, ref int n)
        {
            uint c = (uint)(b[n] | (b[n + 1] << 8) | (b[n + 2] << 16)) | 0xFF000000u;
            n += 3;
            return c;
        }
    }
}
