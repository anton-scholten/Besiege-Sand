namespace SandMod
{
    /// <summary>
    /// Packing numbers into a byte array by hand, little-endian, for the multiplayer messages.
    /// BinaryWriter would do it, but every System.IO type a mod touches is one more thing to
    /// check against the loader's blacklist; these are a few shifts.
    ///
    /// Only the int is left. The float and the short went with the grain solver's messages, and
    /// the packed colour with the palette they carried.
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
    }
}