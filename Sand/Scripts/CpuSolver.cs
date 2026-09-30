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
        public GroundField Ground;
        private int passKind;          // 0 predict, 1 solve, 2 finalize
        private int passCount;
        private float passH;
        private Vector3 passGravity;
        private WaterLink passWater;
        private float[][] threadForces;

        private int[] cellCount;
        private int[] cellItems;
        private int cellsX, cellsY, cellsZ;
        private Vector3 gridOrigin;
        private float cellSize = 0.12f;

        public bool OnGpu { get { return false; } }
        public Grain[] Grains { get { return grains; } }
        public ComputeBuffer GrainBuffer { get { return null; } }

        public bool Init(int capacity, SandMaterials.Params[] table)
        {
            grains = new Grain[capacity];
            materials = table;
            forces = new float[6];
            StartWorkers();
            return true;
        }

        /// <summary>
        /// One worker per core bar the one the game is already using. The pool is
        /// persistent: starting a thread per step would cost more than the step.
        /// </summary>
        private void StartWorkers()
        {
            int n = Mathf.Clamp(SystemInfo.processorCount - 1, 0, 7);
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
            while (true)
            {
                go[id].WaitOne();
                go[id].Reset();
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

        public void Step(float dt, int substeps, int iterations, Vector3 gravity,
                         WaterLink water, Bounds bounds, int count)
        {
            if (count <= 0) return;
            System.Array.Clear(forces, 0, forces.Length);
            for (int i = 0; i < threadForces.Length; i++)
            {
                System.Array.Clear(threadForces[i], 0, threadForces[i].Length);
            }

            // The cap scales with the level's own gravity, so it means the same thing
            // in a space level as on the ground.
            forceCapAccel = Mathf.Max(gravity.magnitude, 1f) * SandFormat.MaxWeightMultiple;

            float h = dt / Mathf.Max(substeps, 1);
            for (int s = 0; s < substeps; s++)
            {
                Parallel(0, h, gravity, water, count);
                BuildGrid(bounds, count);
                for (int it = 0; it < iterations; it++) Parallel(1, h, gravity, water, count);
                Parallel(2, h, gravity, water, count);
            }

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

        private void BuildGrid(Bounds bounds, int count)
        {
            gridOrigin = bounds.min - new Vector3(cellSize, cellSize, cellSize);
            Vector3 span = bounds.size + new Vector3(cellSize * 2f, cellSize * 2f, cellSize * 2f);
            cellsX = Mathf.Max(1, Mathf.CeilToInt(span.x / cellSize));
            cellsY = Mathf.Max(1, Mathf.CeilToInt(span.y / cellSize));
            cellsZ = Mathf.Max(1, Mathf.CeilToInt(span.z / cellSize));

            int cells = cellsX * cellsY * cellsZ;
            if (cellCount == null || cellCount.Length < cells)
            {
                cellCount = new int[cells];
                cellItems = new int[cells * SandFormat.CellCapacity];
            }
            System.Array.Clear(cellCount, 0, cells);

            for (int i = 0; i < count; i++)
            {
                int c = CellOf(grains[i].pos);
                int slot = cellCount[c];
                if (slot < SandFormat.CellCapacity)
                    cellItems[c * SandFormat.CellCapacity + slot] = i;
                cellCount[c] = slot + 1;
            }
        }

        private int CellOf(Vector3 p)
        {
            int x = Mathf.Clamp((int)((p.x - gridOrigin.x) / cellSize), 0, cellsX - 1);
            int y = Mathf.Clamp((int)((p.y - gridOrigin.y) / cellSize), 0, cellsY - 1);
            int z = Mathf.Clamp((int)((p.z - gridOrigin.z) / cellSize), 0, cellsZ - 1);
            return x + y * cellsX + z * cellsX * cellsY;
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
                SandMaterials.Params m = materials[(int)(g.material & SandFormat.MaterialMask)];

                // A settled pile is most of the grains in a level and none of the
                // interesting ones. An asleep grain skips the neighbour sweep -- 27
                // cells of up to 16 each, which is the whole cost of the solve -- and
                // is woken by anything that touches it.
                bool asleep = (g.material & SandFormat.FlagAsleep) != 0;

                Vector3 correction = Vector3.zero;
                float pressure = 0f;

                float cohesion = m.cohesion + g.packing * m.packGain;
                if ((g.material & SandFormat.FlagWet) != 0) cohesion += m.wetCohesion;

                int bx = Mathf.Clamp((int)((g.pos.x - gridOrigin.x) / cellSize), 0, cellsX - 1);
                int by = Mathf.Clamp((int)((g.pos.y - gridOrigin.y) / cellSize), 0, cellsY - 1);
                int bz = Mathf.Clamp((int)((g.pos.z - gridOrigin.z) / cellSize), 0, cellsZ - 1);

                for (int dz = -1; dz <= 1 && !asleep; dz++)
                {
                    int z = bz + dz; if (z < 0 || z >= cellsZ) continue;
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        int y = by + dy; if (y < 0 || y >= cellsY) continue;
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int x = bx + dx; if (x < 0 || x >= cellsX) continue;

                            int cell = x + y * cellsX + z * cellsX * cellsY;
                            int n = Mathf.Min(cellCount[cell], SandFormat.CellCapacity);
                            for (int k = 0; k < n; k++)
                            {
                                int j = cellItems[cell * SandFormat.CellCapacity + k];
                                if (j == i) continue;

                                Grain q = grains[j];
                                SandMaterials.Params mq =
                                    materials[(int)(q.material & SandFormat.MaterialMask)];

                                Vector3 d = g.pos - q.pos;
                                float dist = d.magnitude;
                                if (dist < 1e-6f) continue;

                                float rest = m.radius + mq.radius;
                                Vector3 nrm = d / dist;

                                if (dist < rest)
                                {
                                    // Touching a sleeper wakes it. Two threads may
                                    // write this at once and both write the same bit,
                                    // so the race is harmless.
                                    if ((q.material & SandFormat.FlagAsleep) != 0)
                                        grains[j].material &= ~SandFormat.FlagAsleep;

                                    float overlap = rest - dist;
                                    float wsum = m.invMass + mq.invMass;
                                    if (wsum <= 0f) continue;
                                    correction += nrm * (overlap * m.invMass / wsum);
                                    pressure += overlap;

                                    Vector3 rel = g.vel - q.vel;
                                    Vector3 tang = rel - nrm * Vector3.Dot(rel, nrm);
                                    float tlen = tang.magnitude;
                                    if (tlen > 1e-6f)
                                    {
                                        float mu = 0.5f * (m.friction + mq.friction);
                                        float lim = Mathf.Min(mu * overlap / (tlen * h), 1f);
                                        correction -= tang * (lim * h);
                                    }

                                    float vn = Vector3.Dot(rel, nrm);
                                    float e = 0.5f * (m.restitution + mq.restitution);
                                    if (vn < 0f && e > 0f)
                                        correction += nrm * (-vn * e * h * m.invMass / wsum);
                                }
                                else if (dist < rest * 2f && cohesion > 0f)
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
                // the level and nothing it does matters.
                if (Ground != null && Ground.Built)
                {
                    float gh = Ground.Height(g.pos);
                    if (gh > -9000f)
                    {
                        float sink = (gh + m.radius) - g.pos.y;
                        if (sink > 0f)
                        {
                            Vector3 gn = Ground.Normal(g.pos);
                            correction += gn * sink;
                            pressure += sink;

                            Vector3 gtang = g.vel - gn * Vector3.Dot(g.vel, gn);
                            float gtlen = gtang.magnitude;
                            if (gtlen > 1e-6f)
                            {
                                float lim = Mathf.Min(m.friction * sink / (gtlen * h), 1f);
                                correction -= gtang * (lim * h);
                            }

                            float gvn = Vector3.Dot(g.vel, gn);
                            if (gvn < 0f && m.restitution > 0f)
                                correction += gn * (-gvn * m.restitution * h);
                        }
                    }
                }

                for (int s = 0; s < shapeCount; s++)
                {
                    float dist;
                    Vector3 normal;
                    ShapeMath.Distance(shapes[s], g.pos, out dist, out normal);
                    if (dist >= m.radius) continue;

                    // A shape reaching into a sleeping grain wakes it: this is how a
                    // wheel digs into a pile that had settled.
                    asleep = false;
                    g.material &= ~SandFormat.FlagAsleep;

                    float overlap = m.radius - dist;
                    correction += normal * overlap;
                    pressure += overlap;

                    Vector3 rel = g.vel - shapes[s].pointVel;
                    Vector3 tang = rel - normal * Vector3.Dot(rel, normal);
                    float tlen = tang.magnitude;
                    if (tlen > 1e-6f)
                    {
                        float lim = Mathf.Min(m.friction * overlap / (tlen * h), 1f);
                        correction -= tang * (lim * h);
                    }

                    float vns = Vector3.Dot(rel, normal);
                    if (vns < 0f && m.restitution > 0f)
                        correction += normal * (-vns * m.restitution * h);

                    if (shapes[s].movable > 0.5f && g.age > SandFormat.SettleSeconds)
                    {
                        int slot = (int)(shapes[s].kindBlock >> 8);
                        if (slot < bodyCount)
                        {
                            float mass = 1f / Mathf.Max(m.invMass, 1e-6f);
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

                // A single iteration may not move a grain further than its own
                // radius. Without this, one deep overlap -- a region dropped into the
                // ground, say -- teleports the grain and the velocity that comes out
                // of the position solve is enormous.
                float maxMove = m.radius;
                if (correction.sqrMagnitude > maxMove * maxMove)
                    correction = correction.normalized * maxMove;

                g.pos += correction;
                g.packing = Mathf.Lerp(g.packing, Mathf.Clamp01(pressure / Mathf.Max(m.radius, 1e-6f)), 0.1f);
                grains[i] = g;
            }
        }

        private void Settle(float h, Vector3 gravity, int from, int to)
        {
            bool settles = gravity.sqrMagnitude > 0.0001f;
            for (int i = from; i < to; i++)
            {
                Grain g = grains[i];
                g.vel = (g.pos - g.prevPos) / Mathf.Max(h, 1e-9f);

                if (settles && g.vel.sqrMagnitude < 0.0004f) g.material |= SandFormat.FlagAsleep;
                else g.material &= ~SandFormat.FlagAsleep;

                grains[i] = g;
            }
        }
    }
}
