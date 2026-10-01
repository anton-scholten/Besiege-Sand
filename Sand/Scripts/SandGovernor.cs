namespace SandMod
{
    /// <summary>
    /// Keeps a sand step inside a time budget, by deciding how many grains the machine can
    /// afford and spending quality, in order of how little it shows, when that is not
    /// enough.
    ///
    /// The solver's cost is per grain and the machine is the player's, so the governor
    /// measures instead of guessing: each step reports its time and the number of live
    /// grains, which gives a cost per grain, and the budget divided by that is how many
    /// grains are affordable. That becomes the ceiling on how many are admitted
    /// (<see cref="SoftCapacity"/>), and it is a direct answer rather than a feedback loop
    /// -- a time average reacts only after the damage is done, which for a fast pour is
    /// thousands of grains too late.
    ///
    /// Staying under the ceiling is a matter of how much sand a grain stands for. As the
    /// live count fills the ceiling, pressure rises a rung at a time, and each rung is a
    /// pair of settings:
    ///
    ///   0  full solver, ordinary grains
    ///   1  fewer solver iterations (barely visible)
    ///   2  and coarser grains for what is emitted from now on
    ///   3  coarser still
    ///   4  and fewer iterations again
    ///   5  coarsest grains
    ///
    /// The faster the ceiling is closing, the faster the next rung comes. Pressure falls
    /// back, much more slowly, when the live count has dropped well below the ceiling, so a
    /// pile that has settled gets fine grains back.
    /// </summary>
    public class SandGovernor
    {
        public static readonly int[] IterationsAt = { 3, 2, 2, 2, 1, 1 };
        public static readonly int[] LodAt = { 0, 0, 1, 2, 2, 3 };
        public const int MaxPressure = 5;

        /// <summary>Floor on the ceiling, so a struggling machine still shows sand.</summary>
        public const int MinCapacity = 2000;

        /// <summary>Fills of the ceiling above which pressure rises, and below which it falls.</summary>
        private const float RiseUrgent = 0.85f, RiseSoon = 0.65f, FallBelow = 0.45f;

        public float BudgetMs = 20f;

        public int Pressure { get; private set; }
        public float CostMs { get; private set; }
        public float UsPerGrain { get; private set; }
        public int SoftCapacity { get; private set; }

        private float hold;

        public int Iterations(int wanted)
        {
            int allowed = IterationsAt[Pressure];
            return wanted < allowed ? wanted : allowed;
        }

        public int Lod { get { return LodAt[Pressure]; } }

        /// <summary>Starts with an admission ceiling and no history.</summary>
        public void Reset(int ceiling)
        {
            Pressure = 0;
            CostMs = 0f;
            UsPerGrain = 0f;
            hold = 0f;
            SoftCapacity = ceiling;
        }

        /// <summary>Forgets the last run's load without touching the ceiling.</summary>
        public void NewRun()
        {
            Pressure = 0;
            CostMs = 0f;
            hold = 0f;
        }

        /// <summary>
        /// Records how long a step took with <paramref name="count"/> grains live and
        /// decides what to change. <paramref name="capacity"/> is the array's size, the
        /// most the ceiling can ever be; <paramref name="dt"/> is the step's length.
        /// </summary>
        public void Record(float ms, int count, int capacity, float dt)
        {
            CostMs = CostMs <= 0f ? ms : CostMs * 0.8f + ms * 0.2f;

            // Cost per grain, from steps with enough grains for the figure to mean
            // something: a step with a handful is mostly fixed overhead.
            if (count >= 200)
            {
                float us = ms * 1000f / count;
                UsPerGrain = UsPerGrain <= 0f ? us : UsPerGrain * 0.8f + us * 0.2f;
            }

            int target = SoftCapacity;
            if (UsPerGrain > 0f)
            {
                float affordable = BudgetMs * 1000f / UsPerGrain;
                target = (int)affordable;
                if (target < MinCapacity) target = MinCapacity;
                if (target > capacity) target = capacity;

                if (target < SoftCapacity)
                    SoftCapacity -= (SoftCapacity - target + 1) / 2;   // half way, at once
                else
                {
                    int grown = SoftCapacity + (capacity / 100 > 50 ? capacity / 100 : 50);
                    SoftCapacity = grown < target ? grown : target;
                }
            }

            hold -= dt;
            if (hold > 0f) return;

            float fill = SoftCapacity > 0 ? (float)count / SoftCapacity : 1f;

            // While the ceiling is still growing towards what is affordable, a count that
            // is high against today's ceiling is not yet a reason to spend quality -- unless
            // it is over the ceiling outright.
            bool converging = SoftCapacity < target * 0.9f && fill <= 1f;
            if (converging) return;

            if (fill > RiseUrgent && Pressure < MaxPressure)
            {
                Pressure++;
                hold = 0.75f;
            }
            else if (fill > RiseSoon && Pressure < MaxPressure)
            {
                Pressure++;
                hold = 2f;
            }
            else if (fill < FallBelow && Pressure > 0)
            {
                Pressure--;
                hold = 4f;
            }
        }
    }
}
