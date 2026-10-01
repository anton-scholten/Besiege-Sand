using System.Threading;
using UnityEngine;

namespace SandMod
{
    /// <summary>
    /// The granular solver in managed code. Same model as Sand.compute, same order of
    /// operations, same material table -- so switching between the two changes the
    /// grain count a machine can afford and nothing about how the medium behaves.
    ///
    /// Measured budget on Besiege's 100 Hz fixed step: a plain integration pass costs
    /// about 9.8 ns per grain per pass on one thread of this machine's Mono (see
    /// docs/FINDINGS.md). The contact solve is thirty to a hundred times that, which
    /// is what puts the honest ceiling in the low tens of thousands.
    ///
    /// The solve is written as a range over grains on purpose: it writes only to the
    /// grain it owns, so handing ranges to worker threads is a change of caller and
    /// not of algorithm. <c>System.Threading</c> is not blacklisted and the audio
    /// note in the notes repository is the precedent.
    /// </summary>
    public class CpuSolver : ISandSolver
    {
        private Grain[] grains;
        private SandMaterials.Params[] materials;

        private GpuShape[] shapes;
        private int shapeCount;
        private float[] forces;      // six per body: force xyz, torque xyz
        private int bodyCount;

        // ------------------------------------------------------------------ workers
        //
        // Only the two passes that are safe to split are split. Predict and Finalize
        // touch one grain each. Solve reads every neighbour but writes only the grain
        // it owns, which is what makes a Jacobi sweep parallel without a lock.
        // BuildGrid is not split: it appends to shared cells and would need atomics
        // for a pass that costs almost nothing.
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

        /// <summary>The level's static floor. Set by the field; may be null.</summary>
        public ISandGround Ground;

        /// <summary>Sand that has come to rest, stored as a height field. May be null.
        /// Read here as ground; written only by the field, between steps.</summary>
        public SandBed Bed;
        private int passKind;          // 0 predict, 1 solve, 2 finalize
        private int passCount;
        private float passH;
        private Vector3 passGravity;
        private WaterLink passWater;
        private float[][] threadForces;

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
        /// almost nothing, so a heap of grains spread out to a gentle mound. Three roughly
        /// doubles the slope a pour settles at; much more saturates -- friction is then limited
        /// by the geometry of spheres, which roll -- and starts to make a pile creep.
        /// </summary>
        public const float FrictionBoost = 3f;

        // What the neighbour loop reads of each material, as flat arrays: copying a
        // 48-byte Params row for every pair of grains was a large part of the cost.
        private float[] mRadius, mInvMass, mFriction, mRestitution;
        private float[] mCohesion, mPackGain, mWetCohesion;
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

        public bool OnGpu { get { return false; } }
        public Grain[] Grains { get { return grains; } }
        public ComputeBuffer GrainBuffer { get { return null; } }

        public bool Init(int capacity, SandMaterials.Params[] table)
        {
            grains = new Grain[capacity];
            materials = table;
            forces = new float[6];
            sorted = new int[capacity];
            grainCell = new int[capacity];
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

                RunPass(id + 1, Slice(id + 1), Slice(id + 2));

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

        private void RunPass(int forceSlot, int from, int to)
        {
            if (from >= to) return;
            if (passKind == 0) Predict(passH, passGravity, passWater, from, to);
            else if (passKind == 1) Solve(passH, from, to, forceSlot);
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
                RunPass(0, 0, count);
                return;
            }

            allDone.Reset();
            pending = workers.Length;
            for (int i = 0; i < workers.Length; i++) go[i].Set();

            RunPass(0, Slice(0), Slice(1));

            allDone.WaitOne();
        }

        public void Upload(Grain[] source, int count)
        {
            if (source != grains) System.Array.Copy(source, grains, count);
        }

        public void SetMaterials(SandMaterials.Params[] table)
        {
            // The CPU solver reads the field's own array, so a changed row is already
            // visible; this only matters if the array itself was replaced.
            materials = table;
        }

        public void SetShapes(GpuShape[] s, int count, int bodies)
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

        /// <summary>Throws away a step in progress, for when the sand is cleared under it.</summary>
        public void Abort() { jobActive = false; }

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

            RefreshMaterials();
            PrepareShapes(bounds);

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
                if (phase == 0) Parallel(0, jobH, jobGravity, jobWater, jobCount);
                else if (phase == 1) BuildGrid(jobBounds, jobCount);
                else if (phase < 2 + jobIterations) Parallel(1, jobH, jobGravity, jobWater, jobCount);
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
            }
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
            }
            maxRadius = 0f;
            for (int i = 0; i < n; i++)
            {
                SandMaterials.Params m = materials[i];
                mRadius[i] = m.radius; mInvMass[i] = m.invMass;
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
        private void PrepareShapes(Bounds bounds)
        {
            int n = shapeCount;
            if (sMin.Length < n)
            {
                int cap = Mathf.Max(n, 64);
                sMin = new Vector3[cap]; sMax = new Vector3[cap];
                sRot = new Quaternion[cap]; sInv = new Quaternion[cap];
            }

            // Margin: a grain this big can touch a shape whose box it is outside of.
            float pad = maxRadius + 0.02f;

            for (int i = 0; i < n; i++)
            {
                GpuShape sh = shapes[i];
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
                sMin[i] = sh.centre - e - new Vector3(pad, pad, pad);
                sMax[i] = sh.centre + e + new Vector3(pad, pad, pad);
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
        /// Position corrections for one range of grains. Writes only to grains in the
        /// range, reads everything else -- which is what makes it safe to run several
        /// of these at once.
        /// </summary>
        private void Solve(float h, int from, int to, int forceSlot)
        {
            float[] myForces = threadForces[forceSlot];
            for (int i = from; i < to; i++)
            {
                Grain g = grains[i];
                if ((g.material & SandFormat.FlagDead) != 0) continue;

                int mi = (int)(g.material & SandFormat.MaterialMask);
                float radius = mRadius[mi];
                float invMass = mInvMass[mi];
                float friction = mFriction[mi];
                float restitution = mRestitution[mi];

                // Sleeping grains still sweep their neighbours. They used to skip it, which was
                // cheaper and wrong: gravity still moved them, nothing pushed back, and a
                // sleeper sank into whatever held it up until an awake neighbour noticed the
                // overlap and woke it. That cycle is what made a stack of grains collapse and
                // a pile shiver. Sleep now only means a grain is slow, and the bed uses it.

                Vector3 correction = Vector3.zero;
                float pressure = 0f;

                float cohesion = mCohesion[mi] + g.packing * mPackGain[mi];
                if ((g.material & SandFormat.FlagWet) != 0) cohesion += mWetCohesion[mi];

                {
                    int bx = Mathf.Clamp((int)((g.pos.x - gridOrigin.x) / cellSize), 0, cellsX - 1);
                    int by = Mathf.Clamp((int)((g.pos.y - gridOrigin.y) / cellSize), 0, cellsY - 1);
                    int bz = Mathf.Clamp((int)((g.pos.z - gridOrigin.z) / cellSize), 0, cellsZ - 1);

                    int xlo = bx > 0 ? bx - 1 : 0;
                    int xhi = bx < cellsX - 1 ? bx + 1 : cellsX - 1;

                    for (int dz = -1; dz <= 1; dz++)
                    {
                        int z = bz + dz; if (z < 0 || z >= cellsZ) continue;
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            int y = by + dy; if (y < 0 || y >= cellsY) continue;

                            // The three cells of this row are consecutive in the sort, so
                            // they are one run of grains.
                            int row = (y + z * cellsY) * cellsX;
                            int end = cellStart[row + xhi + 1];
                            for (int k = cellStart[row + xlo]; k < end; k++)
                            {
                                int j = sorted[k];
                                if (j == i) continue;

                                Vector3 d = g.pos - grains[j].pos;
                                float d2 = d.x * d.x + d.y * d.y + d.z * d.z;

                                int mq = (int)(grains[j].material & SandFormat.MaterialMask);
                                float rest = radius + mRadius[mq];
                                if (d2 >= rest * rest * 4f) continue;   // past cohesion's reach

                                float dist = Mathf.Sqrt(d2);
                                if (dist < 1e-6f) continue;
                                Vector3 nrm = d / dist;

                                if (dist < rest)
                                {
                                    // Touching a sleeper wakes it. Two threads may
                                    // write this at once and both write the same bit,
                                    // so the race is harmless.
                                    if ((grains[j].material & SandFormat.FlagAsleep) != 0)
                                        grains[j].material &= ~SandFormat.FlagAsleep;

                                    float overlap = rest - dist;
                                    float wsum = invMass + mInvMass[mq];
                                    if (wsum <= 0f) continue;
                                    correction += nrm * (overlap * invMass / wsum);
                                    pressure += overlap;

                                    // Friction resists how far the two have slipped past each other
                                    // so far this substep -- their displacements since it began,
                                    // which include every earlier iteration's corrections. A
                                    // velocity from the start of the substep cannot see slip that
                                    // the contacts themselves produce, and friction built on it
                                    // held nothing: a grain on another slid off, a pile spread
                                    // into a sheet.
                                    Vector3 rel = g.vel - grains[j].vel;
                                    float rn = rel.x * nrm.x + rel.y * nrm.y + rel.z * nrm.z;

                                    Vector3 slip = (g.pos - g.prevPos) - (grains[j].pos - grains[j].prevPos);
                                    float sn = slip.x * nrm.x + slip.y * nrm.y + slip.z * nrm.z;
                                    Vector3 tang = slip - nrm * sn;
                                    float tlen = tang.magnitude;
                                    if (tlen > 1e-7f)
                                    {
                                        float mu = 0.5f * (friction + mFriction[mq]);
                                        float lim = Mathf.Min(mu * FrictionBoost * overlap / tlen, 1f);
                                        correction -= tang * (lim * invMass / wsum);
                                    }

                                    float e = 0.5f * (restitution + mRestitution[mq]);
                                    if (rn < 0f && e > 0f)
                                        correction += nrm * (-rn * e * h * invMass / wsum);
                                }
                                else if (cohesion > 0f)
                                {
                                    float t = 1f - (dist - rest) / rest;
                                    correction -= nrm * (cohesion * t * t * h * h);
                                }
                            }
                        }
                    }
                }

                // The ground. Besiege's world is concave mesh geometry, which never
                // reaches the shape list, so without this the medium falls through
                // the level and nothing it does matters. A pile that has come to rest is
                // part of the ground: the bed's surface stands in for it wherever it
                // is higher.
                float gh = -10000f;
                bool onBed = false;
                if (Ground != null && Ground.Built) gh = Ground.Height(g.pos);
                if (Bed != null && Bed.Active)
                {
                    float bh;
                    if (Bed.TryTop(g.pos.x, g.pos.z, out bh) && bh > gh)
                    {
                        gh = bh;
                        onBed = true;
                    }
                }
                if (gh > -9000f)
                {
                    float sink = (gh + radius) - g.pos.y;
                    if (sink > 0f)
                    {
                        Vector3 gn = onBed ? Bed.Normal(g.pos.x, g.pos.z) : Ground.Normal(g.pos);
                        correction += gn * sink;
                        pressure += sink;

                        Vector3 move = g.pos - g.prevPos;
                        Vector3 gtang = move - gn * Vector3.Dot(move, gn);
                        float gtlen = gtang.magnitude;
                        if (gtlen > 1e-7f)
                        {
                            float lim = Mathf.Min(friction * sink / gtlen, 1f);
                            correction -= gtang * lim;
                        }

                        float gvn = Vector3.Dot(g.vel, gn);
                        if (gvn < 0f && restitution > 0f)
                            correction += gn * (-gvn * restitution * h);
                    }
                }

                // Colliders: only those whose box touches this grain's cell, and of those
                // only the ones whose box this grain is inside, get a real distance test.
                if (shapeCount > 0)
                {
                    int sx = Mathf.Clamp((int)((g.pos.x - sgOrigin.x) / sgCell), 0, sgX - 1);
                    int sz = Mathf.Clamp((int)((g.pos.z - sgOrigin.z) / sgCell), 0, sgZ - 1);
                    int sc = sx + sz * sgX;
                    int sEnd = sgStart[sc + 1];
                    Vector3 gp = g.pos;

                    for (int si = sgStart[sc]; si < sEnd; si++)
                    {
                        int s = sgItems[si];
                        Vector3 lo = sMin[s], hi = sMax[s];
                        if (gp.x < lo.x || gp.x > hi.x || gp.y < lo.y || gp.y > hi.y
                            || gp.z < lo.z || gp.z > hi.z) continue;

                        float dist;
                        Vector3 normal;
                        ShapeMath.DistanceInv(shapes[s], sRot[s], sInv[s], gp, out dist, out normal);
                        if (dist >= radius) continue;

                        // A shape reaching into a sleeping grain wakes it: this is how a
                        // wheel digs into a pile that had settled.
                        g.material &= ~SandFormat.FlagAsleep;

                        float overlap = radius - dist;
                        correction += normal * overlap;
                        pressure += overlap;

                        Vector3 rel = g.vel - shapes[s].pointVel;
                        Vector3 slipS = (g.pos - g.prevPos) - shapes[s].pointVel * h;
                        Vector3 tang = slipS - normal * Vector3.Dot(slipS, normal);
                        float tlen = tang.magnitude;
                        if (tlen > 1e-7f)
                        {
                            float lim = Mathf.Min(friction * overlap / tlen, 1f);
                            correction -= tang * lim;
                        }

                        float vns = Vector3.Dot(rel, normal);
                        if (vns < 0f && restitution > 0f)
                            correction += normal * (-vns * restitution * h);

                        if (shapes[s].movable > 0.5f && g.age > SandFormat.SettleSeconds)
                        {
                            int slot = (int)(shapes[s].kindBlock >> 8);
                            if (slot < bodyCount)
                            {
                                float mass = 1f / Mathf.Max(invMass, 1e-6f);
                                Vector3 force = -normal * (overlap * mass) / Mathf.Max(h * h, 1e-9f);

                                float cap = mass * forceCapAccel;
                                if (force.sqrMagnitude > cap * cap) force = force.normalized * cap;
                                Vector3 lever = g.pos - shapes[s].centre;
                                Vector3 torque = Vector3.Cross(lever, force);
                                int b = slot * SandFormat.ForceStride;
                                myForces[b + 0] += force.x; myForces[b + 1] += force.y; myForces[b + 2] += force.z;
                                myForces[b + 3] += torque.x; myForces[b + 4] += torque.y; myForces[b + 5] += torque.z;
                            }
                        }
                    }
                }

                // A single iteration may not move a grain further than its own
                // radius. Without this, one deep overlap -- a region dropped into the
                // ground, say -- teleports the grain and the velocity that comes out
                // of the position solve is enormous.
                float maxMove = radius;
                if (correction.sqrMagnitude > maxMove * maxMove)
                    correction = correction.normalized * maxMove;

                g.pos += correction;
                g.packing = Mathf.Lerp(g.packing, Mathf.Clamp01(pressure / Mathf.Max(radius, 1e-6f)), 0.1f);
                grains[i] = g;
            }
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
            for (int i = from; i < to; i++)
            {
                Grain g = grains[i];
                g.vel = (g.pos - g.prevPos) / Mathf.Max(h, 1e-9f);

                float speedSq = g.vel.sqrMagnitude;
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
