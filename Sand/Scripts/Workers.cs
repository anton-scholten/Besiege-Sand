using System.Threading;

namespace SandMod
{
    /// <summary>
    /// A few threads to share a loop between, the calling thread among them. The sand's work is
    /// done in pieces that touch nothing in common (a chunk's columns, a chunk's mesh), so the
    /// pieces can go to every core while the main thread waits, the way the solver's own workers do.
    /// Besiege's runtime is old Mono: no tasks and no <c>Parallel</c>, only threads and the pool.
    /// </summary>
    public static class Workers
    {
        private static readonly int helpers = System.Math.Max(0, System.Math.Min(System.Environment.ProcessorCount - 1, 5));

        /// <summary>Threads besides the caller.</summary>
        public static int Helpers { get { return helpers; } }

        /// <summary>Set to false to do everything on the calling thread, for comparing.</summary>
        public static bool Parallel = true;

        // One loop at a time: the main thread is the only caller.
        private static int count, next, pending;
        private static System.Action<int, int> body;
        private static volatile bool failed;
        private static readonly object gate = new object();

        /// <summary>
        /// Runs <paramref name="work"/>(item, worker) for each item from 0 to <paramref name="items"/>
        /// - 1, each exactly once, on up to <see cref="Helpers"/> + 1 threads; worker 0 is the caller.
        /// Returns whether it all ran: false if a thread threw, and the caller then does the lot again.
        /// </summary>
        public static bool For(int items, System.Action<int, int> work)
        {
            if (items <= 0) return true;
            if (!Parallel || helpers == 0 || items < 2)
            {
                for (int i = 0; i < items; i++) work(i, 0);
                return true;
            }
            body = work; count = items; next = 0; failed = false;
            int use = System.Math.Min(helpers, items - 1);
            pending = use;
            for (int h = 1; h <= use; h++)
            {
                int id = h;
                ThreadPool.QueueUserWorkItem(delegate (object o)
                {
                    try { Run(id); }
                    catch (System.Exception) { failed = true; }
                    finally { lock (gate) { if (--pending == 0) Monitor.PulseAll(gate); } }
                });
            }
            Run(0);
            lock (gate) { while (pending > 0) Monitor.Wait(gate); }
            body = null;
            return !failed;
        }

        private static void Run(int id)
        {
            for (;;)
            {
                int i = Interlocked.Increment(ref next) - 1;
                if (i >= count) return;
                body(i, id);
            }
        }
    }
}
