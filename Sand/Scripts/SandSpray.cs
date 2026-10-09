using UnityEngine;

namespace SandMod
{
    /// <summary>
    /// Sand that is only drawn: a spray of fine grains thrown with the ones the solver carries, which fly, fall and
    /// are gone in a second, with no physics at all -- no contacts, no bed, no forces. A wheel's throw and a blast are
    /// simulated as fewer, coarser grains (<see cref="SandBed.KickLevel"/>), which hold the sand; these give the
    /// throw the look of a fine spray at almost no cost.
    ///
    /// They are soft puffs in Unity's own particle systems (<see cref="NativePuffs"/>), which move and draw them in
    /// native code: two, one for grains the air barely slows (sand, gravel, mud) and one for those it carries (dust,
    /// snow), since how the air slows a puff is the system's. They were quads built in managed code every frame.
    /// </summary>
    public class SandSpray
    {
        /// <summary>The most at once: the field sets it (see SandField.SprayMost).</summary>
        public int Limit = 6000;

        /// <summary>A puff's half-width, in grain radii: the soft rim takes up most of it.</summary>
        private const float Across = 2.6f;
        private const byte Opacity = 150;

        /// <summary>Air drag (SandMaterials.Params.airDrag) above which a grain goes in the system the air carries.</summary>
        private const float LightDrag = 0.3f;

        private NativePuffs heavy, light;
        private uint seed = 0x2545F491u;

        /// <summary>Makes the systems, under <paramref name="parent"/>.</summary>
        public void Init(Transform parent)
        {
            if (heavy != null) return;
            heavy = new NativePuffs("SandSpray", parent, Limit, 1f, 0f, 0f, 1f, false);
            light = new NativePuffs("SandSprayLight", parent, Limit, 1f, 2f, 0.08f, 1.3f, true);
        }

        public int Count { get { return (heavy != null ? heavy.Count : 0) + (light != null ? light.Count : 0); } }

        private float Rand()
        {
            seed ^= seed << 13; seed ^= seed >> 17; seed ^= seed << 5;
            return (seed & 0xFFFFFF) / (float)0x1000000;
        }

        /// <summary>Throws <paramref name="n"/> fine grains from a point, about as fast as the grain they go with and a
        /// little scattered, each <paramref name="radius"/> across, lasting about a second.</summary>
        public void Emit(Vector3 at, Vector3 velocity, uint colour, float radius, float airDrag, int n)
        {
            NativePuffs into = airDrag > LightDrag ? light : heavy;
            if (into == null || !into.Ready) return;
            float speed = velocity.magnitude;
            Color32 c = new Color32((byte)(colour & 0xFF), (byte)((colour >> 8) & 0xFF), (byte)((colour >> 16) & 0xFF), Opacity);
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
            if (heavy != null) heavy.Most = Mathf.Max(most / 2, 1);
            if (light != null) light.Most = Mathf.Max(most / 2, 1);
        }

        public void Clear()
        {
            if (heavy != null) heavy.Clear();
            if (light != null) light.Clear();
        }
    }
}
