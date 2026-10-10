using UnityEngine;

namespace SandMod
{
    /// <summary>
    /// Sand that is only drawn: a spray of fine grains thrown with the ones the solver carries, which fly, fall and
    /// are gone in a second, with no physics at all -- no contacts, no bed, no forces. A wheel's throw and a blast are
    /// simulated as fewer, coarser grains (<see cref="SandBed.KickLevel"/>), which hold the sand; these give the
    /// throw the look of a fine spray at almost no cost.
    ///
    /// They are soft puffs in one of Unity's own particle systems (<see cref="NativePuffs"/>), which moves and draws them
    /// in native code; they were quads built in managed code every frame. As many go with a grain of every medium, each
    /// the size and opacity of that medium's own grains as they are drawn (SandField.Cosmetic), in its colour.
    /// </summary>
    public class SandSpray
    {
        /// <summary>The most at once: the field sets it (see SandField.CosmeticMost).</summary>
        public int Limit = 6000;

        /// <summary>A puff's half-width, in grain radii, for a medium whose grains are opaque discs: on the denser puff
        /// texture this shows about as much as the disc.</summary>
        public const float DiscAcross = 1.5f;

        private NativePuffs puffs, softPuffs;
        private uint seed = 0x2545F491u;

        /// <summary>Makes the system, under <paramref name="parent"/>.</summary>
        public void Init(Transform parent)
        {
            if (puffs != null) return;
            // Opaque in the middle, on the denser of the two puff textures: as soft at the rim, but about twice as
            // much of each puff shows.
            puffs = new NativePuffs("SandSpray", parent, Limit, 1f, 4f, 0.03f, 1f, false, true);
            // Dust's on the soft texture its own grains are drawn with (SoftDust).
            softPuffs = new NativePuffs("SandSpraySoft", parent, Limit, 1f, 4f, 0.03f, 1f, false, false);
        }

        public int Count { get { return (puffs != null ? puffs.Count : 0) + (softPuffs != null ? softPuffs.Count : 0); } }

        private float Rand()
        {
            seed ^= seed << 13; seed ^= seed >> 17; seed ^= seed << 5;
            return (seed & 0xFFFFFF) / (float)0x1000000;
        }

        /// <summary>Throws <paramref name="n"/> fine grains from a point, about as fast as the grain they go with and a
        /// little scattered, each a grain of a size between <paramref name="least"/> and <paramref name="most"/>, lasting
        /// about a second.</summary>
        /// <param name="least">the smallest grain a puff stands for, and</param>
        /// <param name="most">the biggest: each is somewhere between, at random.</param>
        /// <param name="opacity">at the puff's middle, 0 to 255.</param>
        /// <param name="across">the puff's half-width in grain radii.</param>
        /// <param name="soft">on the soft texture, as dust's grains are drawn, rather than the denser one.</param>
        public void Emit(Vector3 at, Vector3 velocity, uint colour, float least, float most, byte opacity, float across, bool soft, int n)
        {
            NativePuffs into = soft ? softPuffs : puffs;
            if (into == null || !into.Ready) return;
            float speed = velocity.magnitude;
            Color32 c = new Color32((byte)(colour & 0xFF), (byte)((colour >> 8) & 0xFF), (byte)((colour >> 16) & 0xFF), opacity);
            for (int q = 0; q < n; q++)
            {
                Vector3 jitter = new Vector3(Rand() - 0.5f, Rand() - 0.5f, Rand() - 0.5f) * (speed * 0.45f + 0.3f);
                float r = least + (most - least) * Rand();
                Vector3 p = at + new Vector3(Rand() - 0.5f, Rand() - 0.5f, Rand() - 0.5f) * (r * 4f);
                Vector3 v = velocity * (0.75f + Rand() * 0.45f) + jitter;
                into.Emit(p, v, r * across * 2f, 0.6f + Rand() * 0.8f, c);
            }
        }

        /// <summary>The cap, set every frame by the field.</summary>
        public void SetLimit(int most)
        {
            Limit = most;
            if (puffs != null) puffs.Most = Mathf.Max(most, 1);
            if (softPuffs != null) softPuffs.Most = Mathf.Max(most / 2, 1);
        }

        public void Clear()
        {
            if (puffs != null) puffs.Clear();
            if (softPuffs != null) softPuffs.Clear();
        }
    }
}
