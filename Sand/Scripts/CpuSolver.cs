using System.Threading;
using UnityEngine;

namespace SandMod
{
    /// <summary>
    /// The granular solver in managed code: position-based dynamics, a few substeps a
    /// sand step and a few solves a substep, on every core there is. See docs/DESIGN.md.
    ///
    /// Measured budget on Besiege's 100 Hz fixed step: a plain integration pass costs
    /// about 9.8 ns per grain per pass on one thread of this machine's Mono (see
    /// docs/FINDINGS.md). The contact solve is thirty to a hundred times that, which
    /// is what puts the honest ceiling in the low tens of thousands awake at once.
    ///
    /// Work is shared between threads by range of grains for the passes that touch one
    /// grain each, and by slab of the grid for the solve, which moves both grains of a
    /// contact. <c>System.Threading</c> is not blacklisted and the audio note in the
    /// notes repository is the precedent.
    /// </summary>
    public class CpuSolver
    {
        private Grain[] grains;
        private SandMaterials.Params[] materials;

        private SandShape[] shapes;
        private int shapeCount;
        private float[] forces;      // six per body: force xyz, torque xyz
        private int bodyCount;

        // ------------------------------------------------------------------ workers
        //
        // Predict and Settle touch one grain each and are split by range. Solve moves both
        // grains of a contact, so it is split by slab of the grid instead, alternate slabs
        // at a time, which keeps any two threads a cell apart (see Solve). BuildGrid is not
        // split: it appends to shared cells and would need atomics for a pass that costs
        // almost nothing.
        //
        // Nothing in here may touch a Unity object. Vector3 and Mathf are a struct
        // and static arithmetic, so they are fine; anything that reaches the engine
        // is not, and none of it does.
        private Thread[] workers;
        private ManualResetEvent[] go;
        private ManualResetEvent allDone;
        private int pending;
        private volatile bool stopping;
        private float forceCapAccel;
        private Vector3 gravityNow;

        /// <summary>The level's static floor. Set by the field; may be null.</summary>
        public ISandGround Ground;

        /// <summary>Sand that has come to rest, stored as a height field. May be null.
        /// Read here as ground; written only by the field, between steps.</summary>
        public SandBed Bed;
        /// <summary>Angular velocity of each shape's body, parallel to the shape array, so a
        /// contact sees the surface speed of a spinning wheel and not only its centre's.
        /// Set by the field; may be null, which means nothing spins.</summary>
        public Vector3[] ShapeSpin;

        /// <summary>Centre of mass of each body, by force slot. The torque handed back is
        /// about this point, because that is where the force is applied. May be null, in
        /// which case each shape's own centre stands in.</summary>
        public Vector3[] BodyCentres;

        private int passKind;          // 0 predict, 1 solve, 2 settle
        private int passCount;
        private float passH;
        private Vector3 passGravity;
        private WaterLink passWater;
        private float[][] threadForces;
        private int passIteration;     // which solve of the substep this is
        private float passShapeTime;   // how far into the step the shapes have moved
        private Vector3 passUp;        // against gravity; zero in a vacuum

        // Per grain, owned by the grain's index and rebuilt every substep: the floor under
        // it (the level's ground or the bed, whichever is higher) as a height and a normal,
        // and the fastest surface that pushed it. Looking the floor up once per substep
        // rather than once per solve was a dozen dictionary lookups per grain per pass.
        private float[] floorH;
        private Vector3[] floorN;
        private float[] pushSpeed;

        // A sticky grain held by a surface this substep, and that surface's velocity where it
        // holds it: the grain leaves the substep moving with it.
        private bool[] clinging;
        private Vector3[] clingVel;
        private Grain[] reorder;
        private float[] pressureAcc;

        // Support owed, per grain, and how much support it found. Leaning a contact's
        // correction towards the upper grain (StackStiffness) holds a pile up with momentum
        // that nothing paid for, so a pile resting on a block weighed less than half of
        // itself. Each contact knows exactly how much it invented, so the lower grain is
        // charged it and passes the charge on down to whatever holds it up -- other grains,
        // the ground, which absorbs it, or a block, which feels it as weight. One hop per
        // solve, double-buffered so a pass only reads what the last one wrote.
        private Vector3[] debtPrev, debtCur;
        private float[] supPrev, supCur;

        // The solve works through the grid one slab of cells at a time, a slab being every
        // cell with the same z: slabStart[z] .. slabStart[z + 1] in sort order. The even slabs
        // run at once, then the odd ones, and each thread takes the next unclaimed slab of the
        // phase, so a heap that is dense in the middle does not leave most threads idle.
        private int[] slabStart = new int[2];
        private int passPhase;
        private int nextSlab;
        private int lastCount;

        // The grain grid is a counting sort: cellStart[c] .. cellStart[c + 1] indexes
        // sorted[] for cell c. A cell holds any number of grains, the memory is one int per
        // cell plus two per grain, and the three cells in a row of the neighbour sweep are
        // one contiguous run of sorted[].
        private int[] cellStart;
        private int[] cellFill;
        private int[] sorted;
        private int[] grainCell;
        private int cellsX, cellsY, cellsZ;
        private Vector3 gridOrigin;
        private float cellSize = 0.12f;
        private const int MaxCells = 2000000;

        /// <summary>
        /// How much sideways slip a contact's push can resist, as a multiple of friction times
        /// the overlap it was pushed out of. By the time the solver looks at a contact again
        /// the overlap has been resolved and is a millimetre or two, which on its own holds
        /// a little less than the material's friction says. It was 3 while stacks could not
        /// hold themselves up and friction was doing the work of stiffness too; with
        /// StackStiffness doing that, 1.5 makes a slow pour of sand settle at about 34
        /// degrees, which is dry sand's angle of repose and the angle the bed slumps to, so
        /// sand looks the same before and after it goes to sleep.
        /// </summary>
        public const float FrictionBoost = 1.5f;

        /// <summary>Below this sine squared of a contact's tilt from vertical (about 20 degrees),
        /// friction falls away towards <see cref="MinRoll"/> of itself: grains roll off one
        /// another there rather than balancing.</summary>
        private const float RollSin2 = 0.25f;

        /// <summary>
        /// How much of its cohesion a grain has, by age: none when it is made -- dug out of the
        /// bed or thrown from a sprayer -- all of it a second after it settles. A dug column of
        /// mud is a stack of grains, and stuck together from the start it stood in the hole as
        /// a pillar; loose for its first second, it falls in and then sticks where it lands.
        /// </summary>
        private static float Knit(float age)
        {
            float t = (age - KnitStart) / KnitSeconds;
            return t <= 0f ? 0f : (t >= 1f ? 1f : t);
        }

        private const float KnitStart = SandFormat.SettleSeconds + 0.1f;
        private const float KnitSeconds = 1f;
        private const float MinRoll = 0.15f;

        /// <summary>
        /// How strongly a contact's correction is handed to the upper of the two grains.
        /// A position solver with a few iterations cannot pass a pile's weight down to the
        /// floor within a step, so a deep pile compresses, and what it gives back when it
        /// decompresses comes out as speed: a placed block of sand went off like a charge and
        /// a pour spread into a sheet. Treating the lower grain of each contact as heavier for
        /// the purpose of separating them -- mass scaling, from Macklin et al., "Unified
        /// Particle Physics for Real-Time Applications" (2014) -- makes a stack resolve from
        /// the bottom up in one sweep, the way shock propagation does. It only decides who
        /// moves how much. Leaning a contact one way invents momentum, which would take the
        /// pile's weight off whatever it rests on; that is accounted for exactly and handed
        /// down to the supports (see debtPrev), so a block under sand still carries all of it.
        /// </summary>
        public const float StackStiffness = 2.5f;

        /// <summary>
        /// Fastest a grain may come out of a substep beyond what it went in with, or beyond the
        /// surface that pushed it, in metres a second. Overlap that the solver could not clear
        /// last time is resolved this time, and in a position solver the distance moved to
        /// clear it becomes velocity -- a grain squeezed by its neighbours is fired out at
        /// whatever speed the squeeze implies. PhysX has the same cap for the same reason
        /// (maxDepenetrationVelocity). A shape moving into sand is not limited by this: grains
        /// it pushes may leave as fast as it hits them.
        /// </summary>
        public const float MaxSeparationSpeed = 0.3f;

        // What the neighbour loop reads of each material, as flat arrays: copying a
        // 48-byte Params row for every pair of grains was a large part of the cost.
        private float[] mRadius, mInvMass, mFriction, mRestitution;
        private float[] mCohesion, mPackGain, mWetCohesion, mLogInvMass, mAdhesion;

        /// <summary>How far past touching, as a fraction of its radius, a sticky grain is still
        /// pulled onto a block's surface.</summary>
        private const float ClingReach = 0.6f;
        /// <summary>Adhesion that adds another <see cref="ClingReach"/> to how far a grain reaches.</summary>
        private const float AdhesionReachPer = 2000f;

        /// <summary>The fastest a sticky grain flies upward once nothing holds it, in metres a
        /// second: under Besiege's gravity, about fifteen centimetres up.</summary>
        private const float StickyThrowUp = 3f;
        private float maxRadius;

        // Collider culling. Every grain used to test every collider in range on every
        // pass, which is why a machine of a few hundred colliders made the solver ten
        // times slower. Each shape now has a tight world box, and a grid over the ground
        // plane lists the shapes whose box touches each cell, so a grain tests only the
        // few colliders in its own cell. Rotations and their inverses are worked out once
        // per step instead of once per test.
        private Vector3[] sMin = new Vector3[0];
        private Vector3[] sMax = new Vector3[0];
        private Quaternion[] sRot = new Quaternion[0];
        private Quaternion[] sInv = new Quaternion[0];
        private int[] sgStart = new int[2];
        private int[] sgItems = new int[0];
        private int sgX = 1, sgZ = 1;
        private float sgCell = 0.5f;
        private Vector3 sgOrigin;
        private const float ShapeCellSize = 0.5f;
        private const int MaxShapeCells = 65536;

        public Grain[] Grains { get { return grains; } }

        public bool Init(int capacity, SandMaterials.Params[] table)
        {
            grains = new Grain[capacity];
            materials = table;
            forces = new float[6];
            sorted = new int[capacity];
            grainCell = new int[capacity];
            floorH = new float[capacity];
            floorN = new Vector3[capacity];
            pushSpeed = new float[capacity];
            clinging = new bool[capacity];
            clingVel = new Vector3[capacity];
            reorder = new Grain[capacity];
            pressureAcc = new float[capacity];
            debtPrev = new Vector3[capacity]; debtCur = new Vector3[capacity];
            supPrev = new float[capacity]; supCur = new float[capacity];
            cellStart = new int[2];
            cellFill = new int[1];
            RefreshMaterials();
            StartWorkers();
            return true;
        }

        /// <summary>
        /// One worker per core bar the one the game is already using. The pool is
        /// persistent: starting a thread per step would cost more than the step.
        /// </summary>
        private void StartWorkers()
        {
            int n = Mathf.Clamp(System.Environment.ProcessorCount - 1, 0, 7);
            threadForces = new float[n + 1][];
            for (int i = 0; i <= n; i++) threadForces[i] = new float[6];
            if (n <= 0) return;

            go = new ManualResetEvent[n];
            workers = new Thread[n];
            allDone = new ManualResetEvent(false);

            for (int i = 0; i < n; i++)
            {
                go[i] = new ManualResetEvent(false);
                Thread t = new Thread(new ParameterizedThreadStart(WorkerLoop));
                t.IsBackground = true;   // a stuck worker must never hold the game open
                t.Start(i);
                workers[i] = t;
            }
        }

        private void WorkerLoop(object boxed)
        {
            int id = (int)boxed;

            // A local copy: Dispose clears the field while this thread is still waking
            // up to be told to stop, and reading it then is a null reference.
            ManualResetEvent[] events = go;
            while (true)
            {
                events[id].WaitOne();
                events[id].Reset();
                if (stopping) return;

                RunPass(id + 1);

                if (Interlocked.Decrement(ref pending) == 0) allDone.Set();
            }
        }

        /// <summary>Where worker <paramref name="index"/>'s range of grains starts.
        /// Index 0 is the game thread, which takes a share like any other.</summary>
        private int Slice(int index)
        {
            int parts = (workers == null ? 0 : workers.Length) + 1;
            return (int)((long)passCount * index / parts);
        }

        private void RunPass(int part)
        {
            if (passKind == 1)
            {
                while (true)
                {
                    int z = (Interlocked.Increment(ref nextSlab) - 1) * 2 + passPhase;
                    if (z >= cellsZ) return;
                    int a = slabStart[z], b = slabStart[z + 1];
                    if (a < b) Solve(passH, a, b, part);
                }
            }

            int from = Slice(part), to = Slice(part + 1);
            if (from >= to) return;
            if (passKind == 0) Predict(passH, passGravity, passWater, from, to);
            else Settle(passH, passGravity, from, to);
        }

        /// <summary>
        /// Runs one pass across every worker and this thread, and does not return
        /// until they have all finished. Falls back to running it inline when there
        /// are no workers, which is also what a single-core machine gets.
        /// </summary>
        private void Parallel(int kind, float h, Vector3 gravity, WaterLink water, int count)
        {
            passKind = kind;
            passH = h;
            passGravity = gravity;
            passWater = water;
            passCount = count;

            if (workers == null)
            {
                RunPass(0);
                return;
            }

            allDone.Reset();
            pending = workers.Length;
            Interlocked.Increment(ref Workers.Running);
            try
            {
                for (int i = 0; i < workers.Length; i++) go[i].Set();
                RunPass(0);
                allDone.WaitOne();
            }
            finally { Interlocked.Decrement(ref Workers.Running); }
        }

        public void SetMaterials(SandMaterials.Params[] table)
        {
            // The CPU solver reads the field's own array, so a changed row is already
            // visible; this only matters if the array itself was replaced.
            materials = table;
        }

        public void SetShapes(SandShape[] s, int count, int bodies)
        {
            shapes = s;
            shapeCount = count;
            bodyCount = bodies;
            int want = Mathf.Max(bodies, 1) * SandFormat.ForceStride;
            if (forces.Length < want) forces = new float[want];
            for (int i = 0; i < threadForces.Length; i++)
            {
                if (threadForces[i].Length < want) threadForces[i] = new float[want];
            }
        }

        public void ReadForces(float[] into, int bodies)
        {
            int n = Mathf.Min(into.Length, bodies * SandFormat.ForceStride);
            System.Array.Copy(forces, into, n);
        }

        public void Dispose()
        {
            stopping = true;
            if (go != null)
            {
                for (int i = 0; i < go.Length; i++) go[i].Set();
            }
            workers = null;
            go = null;
        }

        // ------------------------------------------------------------ stepping in slices
        //
        // A step is a fixed list of passes -- predict, sort, a few solves, settle, once per
        // substep. Run in one go it is a single long stall on the game thread every fourth
        // fixed update, which is felt as a hitch however small the average. Run a few
        // passes per fixed update instead and the same work is a steady few milliseconds
        // a frame. The field begins a step, then advances it a slice at a time.
        //
        // What makes this safe: the step works from a snapshot (the grain count, the
        // shapes, the gravity) taken when it begins; grains appended while it is under way
        // sit past that count and are first stepped by the next one; and the field only
        // removes grains between steps.
        private bool jobActive;
        private int jobPass, jobPasses, jobSubsteps, jobIterations, jobCount;
        private float jobH;
        private Vector3 jobGravity;
        private WaterLink jobWater;
        private Bounds jobBounds;

        /// <summary>True from <see cref="BeginStep"/> until the last pass has run.</summary>
        public bool InProgress { get { return jobActive; } }

        /// <summary>Throws away a step in progress, for when the sand is cleared under it. The
        /// field calls this whenever it empties, so every grain after it is new.</summary>
        public void Abort() { jobActive = false; lastCount = 0; }

        /// <summary>How many passes a step of this shape takes.</summary>
        public int PassesFor(int substeps, int iterations)
        {
            return Mathf.Max(substeps, 1) * (iterations + 3);
        }

        /// <summary>Takes the snapshot a step works from and gets it ready to be advanced.</summary>
        public void BeginStep(float dt, int substeps, int iterations, Vector3 gravity,
                              WaterLink water, Bounds bounds, int count)
        {
            jobActive = false;
            if (count <= 0) return;

            System.Array.Clear(forces, 0, forces.Length);
            for (int i = 0; i < threadForces.Length; i++)
            {
                System.Array.Clear(threadForces[i], 0, threadForces[i].Length);
            }

            // The cap scales with the level's own gravity, so it means the same thing
            // in a space level as on the ground.
            forceCapAccel = Mathf.Max(gravity.magnitude, 1f) * SandFormat.MaxWeightMultiple;
            gravityNow = gravity;

            RefreshMaterials();
            PrepareShapes(bounds, dt);

            // Grains added since the last step start owing nothing; their slots may still hold
            // what the grains that last used them were owed.
            if (count > lastCount)
            {
                System.Array.Clear(debtPrev, lastCount, count - lastCount);
                System.Array.Clear(supPrev, lastCount, count - lastCount);
            }
            lastCount = count;
            SortGrains(bounds, count);

            float g = gravity.magnitude;
            passUp = g > 0.5f ? -gravity / g : Vector3.zero;

            jobSubsteps = Mathf.Max(substeps, 1);
            jobIterations = Mathf.Max(iterations, 1);
            jobH = dt / jobSubsteps;
            jobGravity = gravity;
            jobWater = water;
            jobBounds = bounds;
            jobCount = count;
            jobPass = 0;
            jobPasses = jobSubsteps * (jobIterations + 3);
            jobActive = true;
        }

        /// <summary>
        /// Runs up to <paramref name="passes"/> more passes. Returns true when the step has
        /// finished, at which point the forces are summed and ready to read.
        /// </summary>
        public bool Advance(int passes)
        {
            if (!jobActive) return true;

            int per = jobIterations + 3;
            while (passes-- > 0 && jobPass < jobPasses)
            {
                int phase = jobPass % per;
                // The shapes are where they were when the step began, moved along their
                // own velocity to where they will be at the end of this substep. Without
                // that a block crossing a pile met sand that was a whole step behind it.
                passShapeTime = jobH * (jobPass / per + 1);
                passIteration = phase - 2;
                if (phase == 0) Parallel(0, jobH, jobGravity, jobWater, jobCount);
                else if (phase == 1) BuildGrid(jobBounds, jobCount);
                else if (phase < 2 + jobIterations)
                {
                    passPhase = 0;
                    nextSlab = 0;
                    Parallel(1, jobH, jobGravity, jobWater, jobCount);
                    passPhase = 1;
                    nextSlab = 0;
                    Parallel(1, jobH, jobGravity, jobWater, jobCount);

                    Vector3[] dv = debtPrev; debtPrev = debtCur; debtCur = dv;
                    float[] sv = supPrev; supPrev = supCur; supCur = sv;
                    System.Array.Clear(debtCur, 0, jobCount);
                    System.Array.Clear(supCur, 0, jobCount);
                }
                else Parallel(2, jobH, jobGravity, jobWater, jobCount);
                jobPass++;
            }
            if (jobPass < jobPasses) return false;

            // Each worker accumulated into its own force slots, so summing them is
            // the only place the threads meet -- and it happens once per step, after
            // they have all stopped.
            int used = Mathf.Max(bodyCount, 1) * SandFormat.ForceStride;
            for (int t = 0; t < threadForces.Length; t++)
            {
                float[] src = threadForces[t];
                int n = Mathf.Min(used, src.Length);
                for (int i = 0; i < n; i++) forces[i] += src[i];
            }
            jobActive = false;
            return true;
        }

        /// <summary>A whole step in one call: what a caller with no frame to protect wants.</summary>
        public void Step(float dt, int substeps, int iterations, Vector3 gravity,
                         WaterLink water, Bounds bounds, int count)
        {
            BeginStep(dt, substeps, iterations, gravity, water, bounds, count);
            Advance(int.MaxValue);
        }

        // ------------------------------------------------------------------ passes

        private void Predict(float h, Vector3 gravity, WaterLink water, int from, int to)
        {
            bool wet = water != null && water.Available;
            for (int i = from; i < to; i++)
            {
                Grain g = grains[i];
                SandMaterials.Params m = materials[(int)(g.material & SandFormat.MaterialMask)];

                g.prevPos = g.pos;

                g.age += h;
                g.life -= h;
                if (g.life <= 0f)
                {
                    g.material |= SandFormat.FlagDead;
                    grains[i] = g;
                    continue;
                }

                float surface = wet ? water.SampleHeight(g.pos) : -1e9f;
                bool under = g.pos.y < surface;

                Vector3 accel = gravity;
                if (under) accel -= gravity * m.buoyancy;

                // A vacuum damps nothing, so air drag is only applied where there is
                // air. In the space levels this is what lets a released handful keep
                // drifting instead of quietly stopping.
                float drag = under ? m.waterDrag : m.airDrag;
                g.vel += accel * h;
                float keep = 1f - drag * h;
                if (keep < 0f) keep = 0f;
                g.vel *= keep;
                g.pos += g.vel * h;

                if (under)
                {
                    g.material |= SandFormat.FlagWet;
                    g.wetTimer = m.wetSeconds;
                    if (m.wetMaterial >= 0)
                        g.material = (uint)m.wetMaterial | (g.material & ~SandFormat.MaterialMask);
                }
                else if (g.wetTimer > 0f)
                {
                    g.wetTimer -= h;
                    if (g.wetTimer <= 0f) g.material &= ~SandFormat.FlagWet;
                }

                grains[i] = g;
                pushSpeed[i] = 0f;
                pressureAcc[i] = 0f;
                clinging[i] = false;
                CacheFloor(i, g.pos, m.radius);
            }
        }

        /// <summary>
        /// Looks up the floor under a grain once for the substep: the level's ground, or the
        /// bed's surface where that is higher. A grain moves less than its own radius per
        /// solve, so the floor at the start of the substep is the floor for all of it. The
        /// normal, which costs several more lookups, is only worked out for a grain close
        /// enough to reach the floor this substep.
        /// </summary>
        private void CacheFloor(int i, Vector3 p, float radius)
        {
            float gh = -10000f;
            bool onBed = false;
            if (Ground != null && Ground.Built) gh = Ground.Height(p);
            if (Bed != null && Bed.Active)
            {
                float bh;
                if (Bed.TryTop(p.x, p.z, out bh) && bh > gh)
                {
                    gh = bh;
                    onBed = true;
                }
            }
            floorH[i] = gh;

            Vector3 n = Vector3.up;
            if (gh > -9000f && p.y - gh < radius * (jobIterations + 2))
                n = onBed ? Bed.Normal(p.x, p.z) : Ground.Normal(p);
            floorN[i] = n;
        }

        /// <summary>Copies the material table into flat arrays. Cheap, and done every
        /// step because an emitter's advanced settings edit rows in place.</summary>
        private void RefreshMaterials()
        {
            int n = materials.Length;
            if (mRadius == null || mRadius.Length != n)
            {
                mRadius = new float[n]; mInvMass = new float[n];
                mFriction = new float[n]; mRestitution = new float[n];
                mCohesion = new float[n]; mPackGain = new float[n]; mWetCohesion = new float[n];
                mLogInvMass = new float[n];
                mAdhesion = new float[n];
            }
            maxRadius = 0f;
            for (int i = 0; i < n; i++)
            {
                SandMaterials.Params m = materials[i];
                mRadius[i] = m.radius; mInvMass[i] = m.invMass;
                mLogInvMass[i] = Mathf.Log(Mathf.Max(m.invMass, 1e-6f));
                mAdhesion[i] = m.adhesion;
                mFriction[i] = m.friction; mRestitution[i] = m.restitution;
                mCohesion[i] = m.cohesion; mPackGain[i] = m.packGain; mWetCohesion[i] = m.wetCohesion;
                if (m.radius > maxRadius) maxRadius = m.radius;
            }
        }

        /// <summary>
        /// Sorts the grains into a grid whose cells are as small as the reach of a contact
        /// allows. Two grains touch within the sum of their radii, so a cell the size of
        /// the widest pair finds every contact in the 27 around it; cohesion reaches twice
        /// as far, so cells double while anything present is cohesive. Dry sand, which is
        /// the common case, therefore searches a volume about eight times smaller than a
        /// fixed cell sized for the worst case would.
        /// </summary>
        private void BuildGrid(Bounds bounds, int count)
        {
            float widest = 0f;
            bool cohesive = false;
            for (int i = 0; i < count; i++)
            {
                uint mt = grains[i].material;
                int m = (int)(mt & SandFormat.MaterialMask);
                float pair = mRadius[m] * 2f;
                if (pair > widest) widest = pair;
                if (!cohesive)
                {
                    if (mCohesion[m] > 0f
                        || ((mt & SandFormat.FlagWet) != 0 && mWetCohesion[m] > 0f)
                        || grains[i].packing * mPackGain[m] > 0f)
                        cohesive = true;
                }
            }
            cellSize = Mathf.Max(cohesive ? widest * 2f : widest, 0.01f);

            // A very fine medium over a big area would want millions of cells; widen them
            // until the grid fits. A wider cell is still correct, only less selective.
            while (true)
            {
                gridOrigin = bounds.min - new Vector3(cellSize, cellSize, cellSize);
                Vector3 span = bounds.size + new Vector3(cellSize * 2f, cellSize * 2f, cellSize * 2f);
                cellsX = Mathf.Max(1, Mathf.CeilToInt(span.x / cellSize));
                cellsY = Mathf.Max(1, Mathf.CeilToInt(span.y / cellSize));
                cellsZ = Mathf.Max(1, Mathf.CeilToInt(span.z / cellSize));
                if ((long)cellsX * cellsY * cellsZ <= MaxCells) break;
                cellSize *= 1.25f;
            }

            int cells = cellsX * cellsY * cellsZ;
            if (cellStart.Length < cells + 1)
            {
                cellStart = new int[cells + 1];
                cellFill = new int[cells];
            }
            System.Array.Clear(cellStart, 0, cells + 1);

            for (int i = 0; i < count; i++)
            {
                int c = CellOf(grains[i].pos);
                grainCell[i] = c;
                cellStart[c + 1]++;
            }
            for (int c = 0; c < cells; c++) cellStart[c + 1] += cellStart[c];

            System.Array.Copy(cellStart, cellFill, cells);
            for (int i = 0; i < count; i++) sorted[cellFill[grainCell[i]]++] = i;

            CutSlabs(count);
        }

        /// <summary>
        /// Where each slab of cells starts in the sort, for the solve. A slab is one cell deep,
        /// and a contact never reaches further than a cell, so no two even slabs -- and no two
        /// odd ones -- share a pair of grains. See <see cref="Solve"/>.
        /// </summary>
        private void CutSlabs(int count)
        {
            if (slabStart.Length < cellsZ + 1) slabStart = new int[cellsZ + 1];
            int slab = cellsX * cellsY;
            for (int z = 0; z < cellsZ; z++) slabStart[z] = cellStart[z * slab];
            slabStart[cellsZ] = count;
        }

        /// <summary>
        /// Puts the grains in the order of the cells they are in, once per step. The
        /// neighbour sweep reads grains cell by cell; with the array in arrival order every
        /// neighbour was a jump somewhere else in memory, and in cell order neighbours are
        /// mostly next to each other. Safe here because nothing outside the solver holds a
        /// grain's index across a step, and grains appended while the step runs lie past
        /// <paramref name="count"/> and are not moved.
        /// </summary>
        private void SortGrains(Bounds bounds, int count)
        {
            if (count < 512) return;
            BuildGrid(bounds, count);
            for (int k = 0; k < count; k++) reorder[k] = grains[sorted[k]];
            System.Array.Copy(reorder, grains, count);

            // What each grain was owed moves with it.
            for (int k = 0; k < count; k++) { debtCur[k] = debtPrev[sorted[k]]; supCur[k] = supPrev[sorted[k]]; }
            Vector3[] dv = debtPrev; debtPrev = debtCur; debtCur = dv;
            float[] sv = supPrev; supPrev = supCur; supCur = sv;
            System.Array.Clear(debtCur, 0, count);
            System.Array.Clear(supCur, 0, count);
        }

        private int CellOf(Vector3 p)
        {
            int x = Mathf.Clamp((int)((p.x - gridOrigin.x) / cellSize), 0, cellsX - 1);
            int y = Mathf.Clamp((int)((p.y - gridOrigin.y) / cellSize), 0, cellsY - 1);
            int z = Mathf.Clamp((int)((p.z - gridOrigin.z) / cellSize), 0, cellsZ - 1);
            return x + y * cellsX + z * cellsX * cellsY;
        }

        /// <summary>
        /// Works out each shape's world box and rotations, and lists the shapes touching
        /// each cell of a grid over the ground plane. Runs once per step, on the game
        /// thread, before any pass.
        /// </summary>
        private void PrepareShapes(Bounds bounds, float dt)
        {
            int n = shapeCount;
            if (sMin.Length < n)
            {
                int cap = Mathf.Max(n, 64);
                sMin = new Vector3[cap]; sMax = new Vector3[cap];
                sRot = new Quaternion[cap]; sInv = new Quaternion[cap];
            }

            // Margin: a grain this big can touch a shape whose box it is outside of.
            // Sticky grains are pulled in from a little further than they touch.
            float pad = maxRadius * (1f + ClingReach) + 0.02f;

            for (int i = 0; i < n; i++)
            {
                SandShape sh = shapes[i];
                float x = sh.rot.x, y = sh.rot.y, z = sh.rot.z, w = sh.rot.w;
                sRot[i] = new Quaternion(x, y, z, w);
                // The inverse of a unit quaternion is its conjugate. The engine's own
                // Quaternion.Inverse is a native call made once per test before this.
                sInv[i] = new Quaternion(-x, -y, -z, w);

                // Rotation matrix, for the extent of an oriented box on each world axis.
                float xx = x * x, yy = y * y, zz = z * z;
                float m00 = 1f - 2f * (yy + zz), m01 = 2f * (x * y - z * w), m02 = 2f * (x * z + y * w);
                float m10 = 2f * (x * y + z * w), m11 = 1f - 2f * (xx + zz), m12 = 2f * (y * z - x * w);
                float m20 = 2f * (x * z - y * w), m21 = 2f * (y * z + x * w), m22 = 1f - 2f * (xx + yy);

                int kind = (int)(sh.kindBlock & 0xFF);
                Vector3 e;
                if (kind == SandFormat.ShapeSphere)
                {
                    e = new Vector3(sh.radius, sh.radius, sh.radius);
                }
                else if (kind == SandFormat.ShapeCapsule)
                {
                    // Runs along local Y: half the segment along that axis, plus the radius.
                    float half = sh.halfExtent.y;
                    e = new Vector3(Mathf.Abs(m01) * half + sh.radius,
                                    Mathf.Abs(m11) * half + sh.radius,
                                    Mathf.Abs(m21) * half + sh.radius);
                }
                else
                {
                    Vector3 hx = sh.halfExtent;
                    e = new Vector3(Mathf.Abs(m00) * hx.x + Mathf.Abs(m01) * hx.y + Mathf.Abs(m02) * hx.z,
                                    Mathf.Abs(m10) * hx.x + Mathf.Abs(m11) * hx.y + Mathf.Abs(m12) * hx.z,
                                    Mathf.Abs(m20) * hx.x + Mathf.Abs(m21) * hx.y + Mathf.Abs(m22) * hx.z);
                }
                // Swept over the step: the solver moves each shape along its velocity, so
                // its box has to cover everywhere it will be.
                Vector3 sweep = sh.pointVel * dt;
                sMin[i] = sh.centre + Vector3.Min(sweep, Vector3.zero) - e - new Vector3(pad, pad, pad);
                sMax[i] = sh.centre + Vector3.Max(sweep, Vector3.zero) + e + new Vector3(pad, pad, pad);
            }

            // The grid: one cell per half metre of ground, wider if the area is large.
            sgOrigin = new Vector3(bounds.min.x - pad, 0f, bounds.min.z - pad);
            float spanX = bounds.size.x + pad * 2f, spanZ = bounds.size.z + pad * 2f;
            sgCell = ShapeCellSize;
            while (true)
            {
                sgX = Mathf.Max(1, Mathf.CeilToInt(spanX / sgCell));
                sgZ = Mathf.Max(1, Mathf.CeilToInt(spanZ / sgCell));
                if (sgX * sgZ <= MaxShapeCells) break;
                sgCell *= 1.5f;
            }

            int cells = sgX * sgZ;
            if (sgStart.Length < cells + 1) sgStart = new int[cells + 1];
            System.Array.Clear(sgStart, 0, cells + 1);

            // Two passes over the shapes: count per cell, then fill.
            for (int pass = 0; pass < 2; pass++)
            {
                if (pass == 1)
                {
                    for (int c = 0; c < cells; c++) sgStart[c + 1] += sgStart[c];
                    int total = sgStart[cells];
                    if (sgItems.Length < total) sgItems = new int[Mathf.Max(total, 64)];
                    System.Array.Copy(sgStart, sgFill(cells), cells);
                }

                for (int i = 0; i < n; i++)
                {
                    int x0 = Mathf.FloorToInt((sMin[i].x - sgOrigin.x) / sgCell);
                    int x1 = Mathf.FloorToInt((sMax[i].x - sgOrigin.x) / sgCell);
                    int z0 = Mathf.FloorToInt((sMin[i].z - sgOrigin.z) / sgCell);
                    int z1 = Mathf.FloorToInt((sMax[i].z - sgOrigin.z) / sgCell);
                    if (x1 < 0 || z1 < 0 || x0 >= sgX || z0 >= sgZ) continue;   // off the grid
                    if (x0 < 0) x0 = 0; if (z0 < 0) z0 = 0;
                    if (x1 >= sgX) x1 = sgX - 1; if (z1 >= sgZ) z1 = sgZ - 1;

                    for (int cz = z0; cz <= z1; cz++)
                    {
                        for (int cx = x0; cx <= x1; cx++)
                        {
                            int c = cx + cz * sgX;
                            if (pass == 0) sgStart[c + 1]++;
                            else sgItems[sgFillArr[c]++] = i;
                        }
                    }
                }
            }
        }

        private int[] sgFillArr = new int[1];

        private int[] sgFill(int cells)
        {
            if (sgFillArr.Length < cells) sgFillArr = new int[cells];
            return sgFillArr;
        }

        /// <summary>
        /// One solve over a slab of the grid. Each contact between two grains is handled once,
        /// by whichever of the pair comes first in the sort, and moves both of them -- so what
        /// one grain is pushed by, the other is pushed back by, and the weight of a pile
        /// reaches whatever is under it. It used to be handled twice, once from each side, each
        /// moving only its own grain; the second side then saw a contact already half resolved,
        /// so the pair separated by less than its overlap and a pile on a block weighed half
        /// what it should.
        ///
        /// Moving a neighbour is only safe because no other thread can be working near it: the
        /// grid is cut into slabs along z, and the solve runs the even slabs at once and then the
        /// odd ones. A contact never reaches further than a cell, so two even slabs never share
        /// a grain pair.
        ///
        /// Grains are visited in the sort's order, which is bottom-up within each column of
        /// cells, and each is first pushed out of the floor and the shapes and only then
        /// pushes the grains above it. So in a single sweep a pile's support travels up through
        /// it, the way a stack is held up, rather than one layer per pass.
        /// </summary>
        private void Solve(float h, int from, int to, int forceSlot)
        {
            float[] myForces = threadForces[forceSlot];
            bool firstPass = passIteration == 0;
            Vector3 up = passUp;
            bool stacked = up.sqrMagnitude > 0.5f && StackStiffness > 0f;
            float shapeTime = passShapeTime;
            float invH2 = 1f / Mathf.Max(h * h, 1e-9f);
            int sliceCells = cellsX * cellsY;

            for (int k = from; k < to; k++)
            {
                int i = sorted[k];
                Grain g = grains[i];
                if ((g.material & SandFormat.FlagDead) != 0) continue;

                int mi = (int)(g.material & SandFormat.MaterialMask);
                float radius = mRadius[mi];
                float invMass = mInvMass[mi];
                float logW = mLogInvMass[mi];
                float friction = mFriction[mi];
                float restitution = mRestitution[mi];
                float adhesion = mAdhesion[mi];
                float pressure = 0f;

                // ---------------------------------------------------------- the floor
                //
                // The level's ground, or the bed where that is higher, looked up once for the
                // substep. Besiege's world is concave mesh geometry, which never reaches the
                // shape list, so without this the medium falls through the level.
                float gh = floorH[i];
                if (gh > -9000f)
                {
                    float sink = (gh + radius) - g.pos.y;
                    if (stacked && sink > -radius * SupportSlack)
                        supCur[i] += Mathf.Max(Vector3.Dot(floorN[i], up), 0f);
                    if (sink > 0f)
                    {
                        Vector3 gn = floorN[i];
                        // Along the normal, as deep as the grain is below the surface there, all
                        // the way out at once. The floor does not move, so there is nothing to
                        // share the correction with; cut to a radius per pass, as it was, a grain
                        // landing at 15 m/s sank a quarter of a metre into the ground first.
                        float depth = sink * gn.y;
                        Vector3 fix = gn * depth;
                        pressure += depth;

                        Vector3 moved = g.pos + fix - g.prevPos;
                        Vector3 gtang = moved - gn * Vector3.Dot(moved, gn);
                        float gtlen = gtang.magnitude;
                        if (gtlen > 1e-7f)
                            fix -= gtang * Mathf.Min(friction * FrictionBoost * depth / gtlen, 1f);

                        if (firstPass && restitution > 0f)
                        {
                            float gvn = Vector3.Dot(g.vel, gn);
                            if (gvn < 0f) fix += gn * (-gvn * restitution * h);
                        }
                        g.pos += fix;
                    }
                }

                // ---------------------------------------------------------- colliders
                //
                // Only those whose box touches this grain's cell, and of those only the ones
                // whose box this grain is inside, get a real distance test.
                if (shapeCount > 0)
                {
                    // The cells the grain's whole path this substep crosses, which for all but
                    // the fastest is the one it is in.
                    Vector3 from0 = g.prevPos, to0 = g.pos;
                    float pathLoX = from0.x < to0.x ? from0.x : to0.x, pathHiX = from0.x < to0.x ? to0.x : from0.x;
                    float pathLoY = from0.y < to0.y ? from0.y : to0.y, pathHiY = from0.y < to0.y ? to0.y : from0.y;
                    float pathLoZ = from0.z < to0.z ? from0.z : to0.z, pathHiZ = from0.z < to0.z ? to0.z : from0.z;
                    int sx0 = Mathf.Clamp((int)((pathLoX - sgOrigin.x) / sgCell), 0, sgX - 1);
                    int sx1 = Mathf.Clamp((int)((pathHiX - sgOrigin.x) / sgCell), 0, sgX - 1);
                    int sz0 = Mathf.Clamp((int)((pathLoZ - sgOrigin.z) / sgCell), 0, sgZ - 1);
                    int sz1 = Mathf.Clamp((int)((pathHiZ - sgOrigin.z) / sgCell), 0, sgZ - 1);

                    for (int scz = sz0; scz <= sz1; scz++)
                    for (int scx = sx0; scx <= sx1; scx++)
                    {
                    int sc = scx + scz * sgX;
                    int sEnd = sgStart[sc + 1];

                    for (int si = sgStart[sc]; si < sEnd; si++)
                    {
                        int s = sgItems[si];
                        if ((scx != sx0 || scz != sz0) && ListedBefore(s, sx0, sz0, scx, scz)) continue;
                        Vector3 lo = sMin[s], hi = sMax[s];
                        if (pathHiX < lo.x || pathLoX > hi.x || pathHiY < lo.y || pathLoY > hi.y
                            || pathHiZ < lo.z || pathLoZ > hi.z) continue;

                        // Where the shape is by the end of this substep: it was snapshotted when
                        // the step began and has been moving since. Without this a block
                        // crossing a pile met sand that was a whole step behind it.
                        SandShape sh = shapes[s];
                        sh.centre += sh.pointVel * shapeTime;

                        // A grain that crossed more than its own radius of the shape's frame this
                        // substep may have gone straight through it -- a grain falling at 12 m/s
                        // moves a quarter of a metre a substep, and a plate is two centimetres
                        // thick. Its path is walked a radius at a time, seen from where the
                        // shape ends up, and the grain is stopped where it first touches.
                        if (firstPass) g.pos = FirstTouch(sh, s, g.prevPos + sh.pointVel * h, g.pos, radius);

                        Vector3 gp = g.pos;
                        float dist;
                        Vector3 normal;
                        ShapeMath.DistanceInv(sh, sRot[s], sInv[s], gp, out dist, out normal);
                        // The stickier, the further it reaches for a surface: glue from 1.2 grain
                        // radii, snow from a little over half of one. No further: at three times
                        // that, whole layers of glue counted as stuck and pushed the block about.
                        bool clings = adhesion > 0f
                            && dist < radius * (1f + ClingReach * Mathf.Min(2f, 1f + adhesion / AdhesionReachPer));
                        if (dist >= radius * (1f + SupportSlack) && !clings) continue;

                        // Resting on it, near enough: it is one of the things holding this
                        // grain up, whether or not this pass found them overlapping.
                        float holds = 0f;
                        if (dist < radius * (1f + SupportSlack))
                        {
                            holds = stacked ? Mathf.Max(Vector3.Dot(normal, up), 0f) : 0f;
                            supCur[i] += holds;
                        }
                        if (dist >= radius && !clings) continue;

                        // The velocity of the shape's surface where the grain touches it: a
                        // wheel's centre may be standing still while its rim tears at the sand.
                        Vector3 surface = sh.pointVel;
                        if (ShapeSpin != null && s < ShapeSpin.Length)
                            surface += Vector3.Cross(ShapeSpin[s], gp - sh.centre);
                        float surfaceSpeed = surface.magnitude;
                        if (surfaceSpeed > pushSpeed[i]) pushSpeed[i] = surfaceSpeed;

                        Vector3 push;
                        if (dist >= radius)
                        {
                            // Not touching, but sticky and close: pulled onto the surface, as
                            // hard as the medium clings. Snow and wet sand gather on a wheel
                            // that turns in them; dry sand, with none, falls off.
                            push = -normal * Mathf.Min(adhesion * h * h, dist - radius);
                        }
                        else
                        {
                            // A shape reaching into a sleeping grain wakes it: this is how a
                            // wheel digs into a pile that had settled.
                            g.material &= ~SandFormat.FlagAsleep;

                            // All the way out, as for the floor: a shape is not moved by the
                            // sand within a step, so there is nothing to share the correction with.
                            float overlap = Mathf.Min(radius - dist, 1f);
                            push = normal * overlap;
                            pressure += overlap;

                            // Friction, and for a sticky medium its grip on top: what clings
                            // to a surface moves with it, up to as hard as it clings.
                            Vector3 slip = gp + push - g.prevPos - surface * h;
                            Vector3 tang = slip - normal * Vector3.Dot(slip, normal);
                            float tlen = tang.magnitude;
                            if (tlen > 1e-7f)
                                push -= tang * Mathf.Min((friction * FrictionBoost * overlap + adhesion * h * h) / tlen, 1f);

                            if (firstPass && restitution > 0f)
                            {
                                float vns = Vector3.Dot(g.vel - surface, normal);
                                if (vns < 0f) push += normal * (-vns * restitution * h);
                            }
                        }
                        g.pos += push;
                        // Riding with the surface is for a grain touching it. One still being drawn
                        // onto it was given the surface's speed for nothing -- momentum from nowhere,
                        // which it passed on to the grains round it and they to the block: a heap of
                        // sticky grains walked across a block and pushed it the other way.
                        if (adhesion > 0f && dist < radius)
                        {
                            clinging[i] = true;
                            clingVel[i] = surface;
                        }

                        // Newton's third law, back to the body: everything this contact moved
                        // the grain by, sideways as well as out, as the force that would move a
                        // grain that far in one substep. The sideways part is what a wheel
                        // pushes against to drive through sand, and what drags on a plough.
                        //
                        // Not for a sticky grain being drawn onto a block it is not touching: that
                        // pull is the stickiness, not a blow, and handed back it tugged the block
                        // towards the grain one substep and the overlap shoved it away the next --
                        // with glue's grip, many times the grain's weight each way, so a machine
                        // with glue on it shook. And a sticky grain that is touching weighs on the
                        // block by at most a few times its weight, enough to carry it round a wheel.
                        if (shapes[s].movable > 0.5f && g.age > SandFormat.SettleSeconds && dist < radius)
                        {
                            int slot = (int)(shapes[s].kindBlock >> 8);
                            if (slot < bodyCount)
                            {
                                float mass = 1f / Mathf.Max(invMass, 1e-6f);
                                Vector3 force = -push * (mass * invH2);

                                if (adhesion > 0f)
                                {
                                    // A sticky grain held to a block weighs on it and drags along it,
                                    // and that is all: straight into the face, only the part of its
                                    // weight that lies that way (on top it presses, on a side nothing,
                                    // underneath it hangs). Its pull onto the face and the overlap
                                    // pushing it off are the two halves of holding it there and
                                    // cancel; handed back separately and capped separately they did
                                    // not, and the remainder pushed a light block steadily sideways
                                    // -- a machine with glue on it shook, or drove off on its own.
                                    // Along the face: the grain's weight that lies that way, and on top
                                    // of it no more drag than friction under its weight would give --
                                    // the grip that holds a sticky grain in place is between the grain
                                    // and the face, and passing all of it on passed on every push the
                                    // grains round it gave it, which did not cancel.
                                    float fn = force.x * normal.x + force.y * normal.y + force.z * normal.z;
                                    Vector3 along = force - normal * fn;
                                    Vector3 weight = gravityNow * mass;
                                    float wn = weight.x * normal.x + weight.y * normal.y + weight.z * normal.z;
                                    Vector3 weightAlong = weight - normal * wn;
                                    Vector3 drag = along - weightAlong;
                                    float most = friction * mass * gravityNow.magnitude;
                                    if (drag.sqrMagnitude > most * most) drag = drag.normalized * most;
                                    force = weightAlong + drag + normal * wn;
                                }
                                else
                                {
                                    float cap = mass * forceCapAccel;
                                    if (force.sqrMagnitude > cap * cap) force = force.normalized * cap;
                                }

                                // The weight this grain carries for the grains above it and
                                // was owed, in the share this block holds it up by.
                                if (holds > 0f && supPrev[i] > 1e-9f)
                                    force -= debtPrev[i] * (Mathf.Min(holds / supPrev[i], 1f) * invH2);
                                Vector3 about = (BodyCentres != null && slot < BodyCentres.Length)
                                    ? BodyCentres[slot] + shapes[s].pointVel * shapeTime : sh.centre;
                                Vector3 torque = Vector3.Cross(gp - about, force);
                                int b = slot * SandFormat.ForceStride;
                                myForces[b + 0] += force.x; myForces[b + 1] += force.y; myForces[b + 2] += force.z;
                                myForces[b + 3] += torque.x; myForces[b + 4] += torque.y; myForces[b + 5] += torque.z;
                            }
                        }
                    }
                    }
                }

                // ---------------------------------------------------------- other grains
                //
                // Only pairs with a grain later in the sort: the earlier ones were handled
                // when it was their turn. In the sort's order that leaves the rest of this row
                // of cells, the row above it, and the slab of cells behind. The search starts
                // from the cell the grain was sorted into, not the one it has drifted to since,
                // because that is what the slabs were cut by.
                //
                // Written out in floats rather than Vector3: the game's Mono does not inline
                // the vector operators, and in this loop every one of them was a call.
                float cohesion = mCohesion[mi] + g.packing * mPackGain[mi];
                if ((g.material & SandFormat.FlagWet) != 0) cohesion += mWetCohesion[mi];
                cohesion *= Knit(g.age);

                int cell = grainCell[i];
                int bx = cell % cellsX;
                int by = (cell / cellsX) % cellsY;
                int bz = cell / sliceCells;
                int xlo = bx > 0 ? bx - 1 : 0;
                int xhi = bx < cellsX - 1 ? bx + 1 : cellsX - 1;

                float px = g.pos.x, py = g.pos.y, pz = g.pos.z;
                float ox = g.prevPos.x, oy = g.prevPos.y, oz = g.prevPos.z;
                float vx = g.vel.x, vy = g.vel.y, vz = g.vel.z;
                float massI = 1f / (invMass > 1e-6f ? invMass : 1e-6f);

                for (int dz = 0; dz <= 1; dz++)
                {
                    int z = bz + dz; if (z >= cellsZ) break;
                    for (int dy = (dz == 0 ? 0 : -1); dy <= 1; dy++)
                    {
                        int y = by + dy; if (y < 0 || y >= cellsY) continue;

                        // The three cells of this row are consecutive in the sort, so they are
                        // one run of grains.
                        int row = y * cellsX + z * sliceCells;
                        int start = cellStart[row + xlo];
                        int end = cellStart[row + xhi + 1];
                        if (start <= k) start = k + 1;
                        for (int kk = start; kk < end; kk++)
                        {
                            int j = sorted[kk];
                            Vector3 pj = grains[j].pos;
                            float dx = px - pj.x, dyy = py - pj.y, dzz = pz - pj.z;
                            float d2 = dx * dx + dyy * dyy + dzz * dzz;

                            uint mtj = grains[j].material;
                            int mq = (int)(mtj & SandFormat.MaterialMask);
                            float rest = radius + mRadius[mq];
                            float rest2 = rest * rest;
                            if (d2 >= rest2 * 4f) continue;
                            if ((mtj & SandFormat.FlagDead) != 0) continue;

                            if (d2 >= rest2)
                            {
                                float dist0 = (float)System.Math.Sqrt(d2);
                                float inv0 = 1f / dist0;

                                // Not overlapping this pass but resting together all the same:
                                // still a support for the debt to travel through.
                                if (stacked && dist0 < rest * (1f + SupportSlack))
                                    Carry(i, j, (dx * up.x + dyy * up.y + dzz * up.z) * inv0, 0f, 0f, 0f);

                                // Cohesion: a pull between grains a little apart, the pair's
                                // average. Twice the old per-grain pull, shared: the pair is
                                // handled once now, where each grain used to pull itself in full.
                                float cj = mCohesion[mq];
                                if (cohesion <= 0f && cj <= 0f && mPackGain[mq] <= 0f && mWetCohesion[mq] <= 0f) continue;
                                cj += grains[j].packing * mPackGain[mq];
                                if ((mtj & SandFormat.FlagWet) != 0) cj += mWetCohesion[mq];
                                cj *= Knit(grains[j].age);
                                float c = 0.5f * (cohesion + cj);
                                if (c <= 0f) continue;
                                float wsum0 = invMass + mInvMass[mq];
                                if (wsum0 <= 0f) continue;
                                float t = 1f - (dist0 - rest) / rest;
                                float pull = 2f * c * t * t * h * h * inv0;
                                float s0 = invMass / wsum0, s1 = 1f - s0;
                                px -= dx * pull * s0; py -= dyy * pull * s0; pz -= dzz * pull * s0;
                                grains[j].pos = new Vector3(pj.x + dx * pull * s1, pj.y + dyy * pull * s1, pj.z + dzz * pull * s1);
                                continue;
                            }

                            float dist = (float)System.Math.Sqrt(d2);
                            if (dist < 1e-6f) continue;
                            float invDist = 1f / dist;
                            float nx = dx * invDist, ny = dyy * invDist, nz = dzz * invDist;

                            // Touching wakes both.
                            g.material &= ~SandFormat.FlagAsleep;
                            if ((mtj & SandFormat.FlagAsleep) != 0)
                                grains[j].material = mtj & ~SandFormat.FlagAsleep;

                            // How the separation is shared: by inverse mass, tilted towards
                            // whichever grain is higher (see StackStiffness). A smooth step of the
                            // log of the ratio, so the shares of a pair always add to one.
                            float above = nx * up.x + ny * up.y + nz * up.z;
                            float share;
                            if (stacked)
                            {
                                float x = StackStiffness * above + (logW - mLogInvMass[mq]);
                                share = 0.5f + 0.5f * x / (1f + (x < 0f ? -x : x));
                            }
                            else
                            {
                                float wsum = invMass + mInvMass[mq];
                                if (wsum <= 0f) continue;
                                share = invMass / wsum;
                            }
                            float other = 1f - share;

                            float overlap = rest - dist;
                            if (overlap > rest * 0.5f) overlap = rest * 0.5f;
                            pressure += overlap;
                            pressureAcc[j] += overlap;

                            // The whole move of the pair, along the line from j to this grain;
                            // this grain takes `share` of it and j the rest, the other way.
                            float mx = nx * overlap, my = ny * overlap, mz = nz * overlap;
                            float ix = px + mx * share, iy = py + my * share, iz = pz + mz * share;
                            float jx = pj.x - mx * other, jy = pj.y - my * other, jz = pj.z - mz * other;

                            // Friction resists how far the two have slipped past each other so
                            // far this substep -- their displacements since it began, which
                            // include every earlier correction. A velocity from the start of the
                            // substep cannot see slip that the contacts themselves produce, and
                            // friction built on it held nothing: a grain on another slid off, a
                            // pile spread into a sheet.
                            Vector3 qj = grains[j].prevPos;
                            float sx = (ix - ox) - (jx - qj.x), sy = (iy - oy) - (jy - qj.y), sz2 = (iz - oz) - (jz - qj.z);
                            float sn = sx * nx + sy * ny + sz2 * nz;
                            float tx = sx - nx * sn, ty = sy - ny * sn, tz = sz2 - nz * sn;
                            float tl2 = tx * tx + ty * ty + tz * tz;
                            if (tl2 > 1e-14f)
                            {
                                float mu = 0.5f * (friction + mFriction[mq]);
                                // Grains here slide but cannot roll, and friction alone held one
                                // balanced on another however it was nudged: a column of them, as
                                // digging gives back, stood as a pillar in the hole. A real grain
                                // on top of another rolls off. So friction is weak at a contact
                                // nearly straight up and down, and full at the slant of grains
                                // nested in a pile, which is what holds a heap at its angle.
                                float side2 = 1f - above * above;
                                if (side2 < RollSin2) mu *= MinRoll + (1f - MinRoll) * side2 / RollSin2;
                                float lim = mu * FrictionBoost * overlap / (float)System.Math.Sqrt(tl2);
                                if (lim > 1f) lim = 1f;
                                tx *= lim; ty *= lim; tz *= lim;
                                ix -= tx * share; iy -= ty * share; iz -= tz * share;
                                jx += tx * other; jy += ty * other; jz += tz * other;
                                mx -= tx; my -= ty; mz -= tz;
                            }

                            // A bounce is extra separation in proportion to the speed of
                            // approach. Once per substep: added on every solve, it was a bounce
                            // three times over.
                            if (firstPass)
                            {
                                float e = 0.5f * (restitution + mRestitution[mq]);
                                if (e > 0f)
                                {
                                    Vector3 vj = grains[j].vel;
                                    float rn = (vx - vj.x) * nx + (vy - vj.y) * ny + (vz - vj.z) * nz;
                                    if (rn < 0f)
                                    {
                                        float b = -rn * e * h;
                                        ix += nx * b * share; iy += ny * b * share; iz += nz * b * share;
                                        jx -= nx * b * other; jy -= ny * b * other; jz -= nz * b * other;
                                        mx += nx * b; my += ny * b; mz += nz * b;
                                    }
                                }
                            }

                            px = ix; py = iy; pz = iz;
                            grains[j].pos = new Vector3(jx, jy, jz);

                            if (stacked)
                            {
                                // What leaning towards the upper grain invented, charged to the
                                // lower one with its share of what the upper one was owed.
                                float massJ = 1f / Mathf.Max(mInvMass[mq], 1e-6f);
                                float lk = (massI + massJ) * (share - massJ / (massI + massJ));
                                Carry(i, j, above, mx * lk, my * lk, mz * lk);
                            }
                        }
                    }
                }

                g.pos = new Vector3(px, py, pz);
                pressureAcc[i] += pressure;
                grains[i] = g;
            }
        }

        /// <summary>
        /// Whether shape <paramref name="s"/> is listed in a cell of the path's range that comes
        /// before (<paramref name="cx"/>, <paramref name="cz"/>), so a shape spanning two cells
        /// of a fast grain's path is met once. A cell's list is in shape order.
        /// </summary>
        private bool ListedBefore(int s, int x0, int z0, int cx, int cz)
        {
            for (int z = z0; z <= cz; z++)
            {
                int xEnd = z == cz ? cx - 1 : sgX - 1;
                for (int x = x0; x <= xEnd; x++)
                {
                    int c = x + z * sgX;
                    for (int k = sgStart[c]; k < sgStart[c + 1]; k++)
                    {
                        int t = sgItems[k];
                        if (t == s) return true;
                        if (t > s) break;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// The point on the straight path from <paramref name="from"/> to <paramref name="to"/>,
        /// both in the frame where the shape sits at its end-of-substep place, where a grain of
        /// this radius first touches it; <paramref name="to"/> when the path is short, starts in
        /// contact already, or never touches. Steps of a radius cannot step over anything a
        /// grain could touch, since touching means being within a radius of it.
        /// </summary>
        private Vector3 FirstTouch(SandShape sh, int s, Vector3 from, Vector3 to, float radius)
        {
            float dx = to.x - from.x, dy = to.y - from.y, dz = to.z - from.z;
            float len2 = dx * dx + dy * dy + dz * dz;
            if (len2 <= radius * radius) return to;

            float d;
            Vector3 n;
            ShapeMath.DistanceInv(sh, sRot[s], sInv[s], from, out d, out n);
            if (d < radius) return to;

            int steps = Mathf.Min(Mathf.CeilToInt((float)System.Math.Sqrt(len2) / radius), 32);
            for (int k = 1; k < steps; k++)
            {
                float t = (float)k / steps;
                Vector3 p = new Vector3(from.x + dx * t, from.y + dy * t, from.z + dz * t);
                ShapeMath.DistanceInv(sh, sRot[s], sInv[s], p, out d, out n);
                if (d < radius) return p;
            }
            return to;
        }

        /// <summary>How far apart, as a fraction of the contact distance, two things may be and
        /// still count as one holding the other up, for the support debt.</summary>
        private const float SupportSlack = 0.08f;

        /// <summary>
        /// One contact's part in the support debt: the lower of the two grains is charged what
        /// the contact invented and the share of the upper grain's debt that it holds up, and
        /// the upper grain notes it as a support. <paramref name="above"/> is how far grain
        /// <paramref name="i"/> is above <paramref name="j"/>, as the cosine against up.
        /// </summary>
        private void Carry(int i, int j, float above, float lx, float ly, float lz)
        {
            int lower = above < 0f ? i : j, upper = above < 0f ? j : i;
            float w = above < 0f ? -above : above;
            float total = supPrev[upper];
            if (total > 1e-6f)
            {
                float f = w / total;
                if (f > 1f) f = 1f;
                Vector3 d = debtPrev[upper];
                lx += d.x * f; ly += d.y * f; lz += d.z * f;
            }
            debtCur[lower].x += lx; debtCur[lower].y += ly; debtCur[lower].z += lz;
            supCur[upper] += w;
        }

        /// <summary>
        /// Speed squared under which a grain counts as at rest for the bed: twenty centimetres
        /// a second. A pile of grains under its own weight keeps shivering at a few centimetres
        /// a second, and one still settling slides slower than this, so a tighter limit leaves
        /// most of a pile as grains for many seconds; the bed, which holds it exactly and
        /// slumps it to its angle of repose, does that better and for nothing.
        /// </summary>
        private const float RestSpeedSq = 0.04f;

        private void Settle(float h, Vector3 gravity, int from, int to)
        {
            bool settles = gravity.sqrMagnitude > 0.0001f;
            float invH = 1f / Mathf.Max(h, 1e-9f);
            for (int i = from; i < to; i++)
            {
                Grain g = grains[i];

                // The velocity the moves imply, but no faster than the grain went in with, or
                // than the surface that pushed it, plus a little: what is left over is overlap
                // being cleared, and that is not speed (see MaxSeparationSpeed). g.vel still
                // holds the predicted velocity here; nothing between Predict and this writes it.
                Vector3 v = (g.pos - g.prevPos) * invH;
                // A sticky grain held by a surface moves as the surface does. Its velocity from
                // its own moves lags a turning wheel's, so it slid back off a fast one however
                // hard it clung; matched, it rides round with it, and only the pull onto the
                // surface keeps it from flying off the curve.
                if (clinging[i]) v = clingVel[i];
                // Sticky media are heavy and soft: a wheel throws them, but not high. Riding a
                // wheel round they came off the top at its rim speed and shot up like dry sand
                // from a roost, which mud and wet sand do not.
                else if (v.y > StickyThrowUp)
                {
                    int sm = (int)(g.material & SandFormat.MaterialMask);
                    if (mCohesion[sm] > 1f || mAdhesion[sm] > 20f || (g.material & SandFormat.FlagWet) != 0)
                        v.y = StickyThrowUp;
                }
                float allowed = Mathf.Max(g.vel.magnitude, pushSpeed[i]) + MaxSeparationSpeed;
                float speedSq = v.sqrMagnitude;
                if (speedSq > allowed * allowed)
                {
                    v *= allowed / Mathf.Sqrt(speedSq);
                    speedSq = allowed * allowed;
                }
                g.vel = v;

                // How hard it has been squeezed, for snow, which packs under load.
                float r = mRadius[(int)(g.material & SandFormat.MaterialMask)];
                float squeeze = Mathf.Clamp01(pressureAcc[i] / (Mathf.Max(jobIterations, 1) * Mathf.Max(r, 1e-6f)));
                g.packing += (squeeze - g.packing) * 0.27f;

                if (settles && speedSq < 0.0004f) g.material |= SandFormat.FlagAsleep;
                else g.material &= ~SandFormat.FlagAsleep;

                // How long it has been nearly still, for the bed to read. Looser than
                // the sleep test on purpose: a pile under its own weight keeps shivering
                // a little faster than a grain is allowed to sleep at, and waiting for
                // it to stop would leave most of a pile as grains for many seconds.
                int rest = SandFormat.RestSteps(g.material);
                if (settles && speedSq < RestSpeedSq)
                {
                    if (rest < SandFormat.RestMax) rest++;
                }
                else rest = 0;
                g.material = (g.material & ~SandFormat.RestMask) | ((uint)rest << SandFormat.RestShift);

                grains[i] = g;
            }
        }
    }
}
