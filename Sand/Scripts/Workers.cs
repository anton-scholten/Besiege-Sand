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

        /// <summary>
        /// Above nought while other threads may be working -- a loop here, or the solver's own pass. Every bit of
        /// the sand's threading is fork-join from the main thread, so at nought only the main thread is touching
        /// anything, and what it reads need not be kept per thread (see SandBed.FindChunk).
        /// </summary>
        public static int Running;

        // One loop at a time: the main thread is the only caller. Each call has its own state, so a pool
        // thread that only gets round to its work item after the loop is over finds nothing left in that
        // loop and goes, without touching the next one.
        private class Loop
        {
            public int count, next, done;
            public System.Action<int, int> body;
            public volatile bool failed;
        }

        // Set by whoever finishes a loop's last item, the caller included, and waited on by the caller whatever
        // the count says, so a Set always belongs to the loop being waited for. It was Monitor.Wait and PulseAll:
        // Besiege's Mono let the helper taking the lock to pulse wait a tenth of a second now and then while the
        // caller sat in Wait, and the frame waited with it. The solver's threads use an event too.
        private static readonly ManualResetEvent finished = new ManualResetEvent(false);

        /// <summary>
        /// Runs <paramref name="work"/>(item, worker) for each item from 0 to <paramref name="items"/>
        /// - 1, each exactly once, on up to <see cref="Helpers"/> + 1 threads; worker 0 is the caller.
        /// Returns whether it all ran: false if a thread threw, and the caller then does the lot again.
        ///
        /// The caller waits for the items, not for the helpers: a helper the pool only starts after the caller
        /// has done every item alone is not waited for.
        /// </summary>
        public static bool For(int items, System.Action<int, int> work)
        {
            if (items <= 0) return true;
            if (!Parallel || helpers == 0 || items < 2)
            {
                for (int i = 0; i < items; i++) work(i, 0);
                return true;
            }
            Loop loop = new Loop();
            loop.body = work; loop.count = items;
            finished.Reset();
            int use = System.Math.Min(helpers, items - 1);
            Interlocked.Increment(ref Running);
            try
            {
                for (int h = 1; h <= use; h++)
                {
                    int id = h;
                    ThreadPool.QueueUserWorkItem(delegate (object o) { Run(loop, id); });
                }
                Run(loop, 0);
                finished.WaitOne();
            }
            finally { Interlocked.Decrement(ref Running); }
            return !loop.failed;
        }

        private static void Run(Loop loop, int id)
        {
            for (;;)
            {
                int i = Interlocked.Increment(ref loop.next) - 1;
                if (i >= loop.count) return;
                try { loop.body(i, id); }
                catch (System.Exception) { loop.failed = true; }
                finally
                {
                    if (Interlocked.Increment(ref loop.done) == loop.count) finished.Set();
                }
            }
        }
    }
}
