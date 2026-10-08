using UnityEngine;

namespace SandMod
{
    /// <summary>
    /// Sand that is only drawn: a spray of fine grains thrown with the ones the solver carries, which fly, fall and
    /// are gone in a second, with no physics at all -- no contacts, no bed, no forces. A wheel's throw and a blast are
    /// simulated as fewer, coarser grains (<see cref="SandBed.KickLevel"/>), which hold the sand; these give the
    /// throw the look of a fine spray at almost no cost. Drawn with the grains (<see cref="SandRenderer"/>).
    /// </summary>
    public class SandSpray
    {
        /// <summary>The most at once; a new one takes the place of the oldest past it.</summary>
        public const int Most = 20000;

        public readonly Vector3[] Pos = new Vector3[Most];
        public readonly float[] Radius = new float[Most];
        public readonly uint[] Colour = new uint[Most];
        public readonly uint[] Look = new uint[Most];
        private readonly Vector3[] vel = new Vector3[Most];
        private readonly float[] life = new float[Most];
        private readonly float[] floor = new float[Most];
        private readonly float[] drag = new float[Most];
        private int count, oldest;
        private uint seed = 0x2545F491u;

        public int Count { get { return count; } }

        private float Rand()
        {
            seed ^= seed << 13; seed ^= seed >> 17; seed ^= seed << 5;
            return (seed & 0xFFFFFF) / (float)0x1000000;
        }

        /// <summary>Throws <paramref name="n"/> fine grains from a point, about as fast as the grain they go with and a
        /// little scattered, each <paramref name="radius"/> across, lasting about a second.</summary>
        public void Emit(Vector3 at, Vector3 velocity, uint colour, float radius, float airDrag, int n)
        {
            float speed = velocity.magnitude;
            for (int q = 0; q < n; q++)
            {
                int k;
                if (count < Most) k = count++;
                else { k = oldest; oldest = (oldest + 1) % Most; }
                Vector3 jitter = new Vector3(Rand() - 0.5f, Rand() - 0.5f, Rand() - 0.5f) * (speed * 0.45f + 0.3f);
                Pos[k] = at + new Vector3(Rand() - 0.5f, Rand() - 0.5f, Rand() - 0.5f) * (radius * 4f);
                vel[k] = velocity * (0.75f + Rand() * 0.45f) + jitter;
                Radius[k] = radius * (0.5f + Rand() * 0.5f);   // half the grain to the grain, for variety
                Colour[k] = colour;
                Look[k] = (seed | 1u);
                life[k] = 0.6f + Rand() * 0.8f;
                floor[k] = at.y - 0.3f;
                drag[k] = airDrag;
            }
        }

        /// <summary>Moves the spray on by a frame. A grain is gone when its time is up or it falls back past where it
        /// was thrown from -- into the sand, which it does not touch.</summary>
        public void Step(float dt, Vector3 gravity)
        {
            if (count == 0 || dt <= 0f) return;
            for (int k = 0; k < count; k++)
            {
                life[k] -= dt;
                Vector3 v = vel[k] + gravity * dt;
                float keep = 1f - drag[k] * dt;
                v *= keep < 0f ? 0f : keep;
                vel[k] = v;
                Pos[k] += v * dt;
                if (life[k] > 0f && (Pos[k].y > floor[k] || v.y > 0f)) continue;
                count--;
                Pos[k] = Pos[count]; vel[k] = vel[count]; Radius[k] = Radius[count]; Colour[k] = Colour[count];
                Look[k] = Look[count]; life[k] = life[count]; floor[k] = floor[count]; drag[k] = drag[count];
                if (oldest > count) oldest = 0;
                k--;
            }
        }

        public void Clear() { count = 0; oldest = 0; }
    }
}
