using System.Collections.Generic;
using UnityEngine;

namespace SandMod
{
    /// <summary>
    /// What a region of sand placed in the level is resting on, found before it is filled.
    ///
    /// A region is filled as bed: columns standing on the ground. The ground is what the
    /// level's fixed geometry rasterises to, and it leaves out anything with a rigidbody that
    /// is not kinematic, because such things move. So a region placed on a level object with
    /// physics, or in the air, was filled on the floor underneath: it appeared somewhere other
    /// than where it was put.
    ///
    /// So under the region's footprint, one ray per ground sample is cast down from its
    /// lowest point, and the highest surface found that is not part of the player's machine,
    /// a trigger, or the sand's own collider is what it rests on; the ground is raised to it
    /// there (<see cref="GroundField.Raise"/>), so the sand stays on it, slumps off its edges,
    /// and grains land on it. Where most of the region has nothing within a short drop below
    /// it, it is in the air, and the field fills it as grains, which fall.
    ///
    /// The rays are cast a few milliseconds' worth a fixed update, like the ground's.
    /// </summary>
    public class RegionSupport
    {
        /// <summary>A drop bigger than this under the region counts as air.</summary>
        public const float AirGap = 0.3f;

        private readonly GroundField ground;
        private readonly float bottom;
        private readonly int i0, i1, j0, j1;
        private int next;
        private int samples, inAir;
        private readonly RaycastHit[] hits = new RaycastHit[16];
        private static readonly Dictionary<int, bool> machine = new Dictionary<int, bool>();

        public bool Done { get { return next >= (i1 - i0 + 1) * (j1 - j0 + 1); } }

        /// <summary>True once done, when most of the region has a drop of more than
        /// <see cref="AirGap"/> under it.</summary>
        public bool InAir { get { return samples > 0 && inAir * 2 > samples; } }

        /// <param name="lo">The region's world box, lowest corner.</param>
        /// <param name="hi">Highest corner.</param>
        public RegionSupport(GroundField ground, Vector3 lo, Vector3 hi)
        {
            this.ground = ground;
            bottom = lo.y;
            i0 = Mathf.CeilToInt(lo.x / GroundField.Cell); i1 = Mathf.FloorToInt(hi.x / GroundField.Cell);
            j0 = Mathf.CeilToInt(lo.z / GroundField.Cell); j1 = Mathf.FloorToInt(hi.z / GroundField.Cell);
            if (i1 < i0) i1 = i0;
            if (j1 < j0) j1 = j0;
        }

        /// <summary>Casts until done or <paramref name="budgetMs"/> is spent; true when done.</summary>
        public bool Step(float budgetMs)
        {
            System.Diagnostics.Stopwatch timer = System.Diagnostics.Stopwatch.StartNew();
            int across = i1 - i0 + 1;
            int total = across * (j1 - j0 + 1);
            while (next < total)
            {
                int i = i0 + next % across, j = j0 + next / across;
                next++;
                Probe(i * GroundField.Cell, j * GroundField.Cell);
                if (timer.Elapsed.TotalMilliseconds > budgetMs) break;
            }
            return Done;
        }

        private void Probe(float x, float z)
        {
            float floor = ground.Height(x, z);
            if (floor <= -9000f) return;
            samples++;

            // Resting on the ground already: nothing to find.
            if (bottom - floor <= 0.05f) return;

            float from = bottom + 0.05f;
            int n = Physics.RaycastNonAlloc(new Ray(new Vector3(x, from, z), Vector3.down), hits, from - floor + 0.05f);
            float best = floor;
            for (int k = 0; k < n; k++)
            {
                Collider c = hits[k].collider;
                if (c == null || c.isTrigger || GroundField.IsIgnored(c)) continue;
                if (OnMachine(c)) continue;
                if (hits[k].point.y > best) best = hits[k].point.y;
            }
            if (best > floor + 0.01f) ground.Raise(x, z, best);
            if (bottom - best > AirGap) inAir++;
        }

        /// <summary>Whether a collider is part of the player's machine, which the sand should
        /// not be laid on: it is about to drive off.</summary>
        private static bool OnMachine(Collider c)
        {
            Rigidbody rb = c.attachedRigidbody;
            if (rb == null) return false;
            int id = rb.GetInstanceID();
            bool yes;
            if (!machine.TryGetValue(id, out yes))
            {
                yes = rb.GetComponent<BlockBehaviour>() != null;
                if (machine.Count > 4096) machine.Clear();
                machine[id] = yes;
            }
            return yes;
        }
    }
}
