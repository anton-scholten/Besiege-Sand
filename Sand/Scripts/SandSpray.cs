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
    /// in native code; they were quads built in managed code every frame. Every medium's are alike -- as many to a
    /// grain, as big, slowed as much by the air -- in the medium's colour.
    /// </summary>
    public class SandSpray
    {
        /// <summary>The most at once: the field sets it (see SandField.SprayMost).</summary>
        public int Limit = 6000;

        /// <summary>The radius of the fine grain a puff stands for, every medium alike.</summary>
        public const float Radius = 0.035f;

        /// <summary>A puff's half-width, in grain radii: the soft rim takes up most of it.</summary>
        private const float Across = 2.6f;

        private NativePuffs puffs;
        private uint seed = 0x2545F491u;

        /// <summary>Makes the system, under <paramref name="parent"/>.</summary>
        public void Init(Transform parent)
        {
            if (puffs != null) return;
            // Opaque in the middle, on the denser of the two puff textures: as soft at the rim, but about twice as
            // much of each puff shows.
            puffs = new NativePuffs("SandSpray", parent, Limit, 1f, 4f, 0.03f, 1f, false, true);
        }

        public int Count { get { return puffs != null ? puffs.Count : 0; } }

        private float Rand()
        {
            seed ^= seed << 13; seed ^= seed >> 17; seed ^= seed << 5;
            return (seed & 0xFFFFFF) / (float)0x1000000;
        }

        /// <summary>Throws <paramref name="n"/> fine grains from a point, about as fast as the grain they go with and a
        /// little scattered, each <paramref name="radius"/> across, lasting about a second.</summary>
        public void Emit(Vector3 at, Vector3 velocity, uint colour, float radius, int n)
        {
            NativePuffs into = puffs;
            if (into == null || !into.Ready) return;
            float speed = velocity.magnitude;
            Color32 c = new Color32((byte)(colour & 0xFF), (byte)((colour >> 8) & 0xFF), (byte)((colour >> 16) & 0xFF), 255);
            for (int q = 0; q < n; q++)
            {
                Vector3 jitter = new Vector3(Rand() - 0.5f, Rand() - 0.5f, Rand() - 0.5f) * (speed * 0.45f + 0.3f);
                Vector3 p = at + new Vector3(Rand() - 0.5f, Rand() - 0.5f, Rand() - 0.5f) * (radius * 4f);
                Vector3 v = velocity * (0.75f + Rand() * 0.45f) + jitter;
                float r = radius * (0.75f + Rand() * 0.25f);   // three quarters of the grain to the grain, for variety
                into.Emit(p, v, r * Across * 2f, 0.6f + Rand() * 0.8f, c);
            }
        }

        /// <summary>The cap, set every frame by the field.</summary>
        public void SetLimit(int most)
        {
            Limit = most;
            if (puffs != null) puffs.Most = Mathf.Max(most, 1);
        }

        public void Clear()
        {
            if (puffs != null) puffs.Clear();
        }
    }
}
