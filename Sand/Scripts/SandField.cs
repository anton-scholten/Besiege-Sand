using UnityEngine;
using UnityEngine.SceneManagement;

namespace SandMod
{
    /// <summary>
    /// The medium, for one level. One of these exists at a time; blocks and level
    /// objects put grains into it and it does the rest.
    ///
    /// It is level-wide rather than per-block on purpose: sand poured by one emitter
    /// has to pile against sand poured by another, and a block behaviour only ever
    /// reaches its own block. The notes repository's rule for this shape of mod --
    /// one shared registry, reconcile rather than react, put every global back -- is
    /// what this follows.
    /// </summary>
    public class SandField : MonoBehaviour, IBedSink
    {
        /// <summary>Sand steps per second. Besiege's own fixed step is 100 Hz; this
        /// deliberately is not, because stepping a medium that often would spend the
        /// whole frame on it and force a hundred GPU readbacks a second.</summary>
        public static int StepsPerSecond = 25;

        public static int Substeps = 2;
        public static int Iterations = 3;

        /// <summary>
        /// Size of the grain array per solver. The GPU number is what the hardware can
        /// carry. The CPU number is only a ceiling on the array: how many grains are
        /// actually admitted is decided by the governor from the measured cost of a step
        /// (see SandGovernor), because that depends on the player's machine, and sand that
        /// has come to rest is held by the bed and costs no grains at all.
        /// </summary>
        public static int GpuCapacity = 250000;
        public static int CpuCapacity = 40000;

        /// <summary>
        /// Milliseconds one whole sand step may take before the field starts spending
        /// quality to stay inside it. A step is advanced in slices over the four fixed
        /// updates it spans, so this is about four times what a single frame pays.
        /// </summary>
        public static float StepBudgetMs = 20f;

        private static SandField instance;

        private ISandSolver solver;
        private SandMaterials.Params[] materials;
        private readonly ShapeFeed shapes = new ShapeFeed();
        private readonly WaterLink water = new WaterLink();
        private readonly GroundField ground = new GroundField();
        private readonly SandRenderer renderer = new SandRenderer();

        // Sand that has come to rest, kept as a height field instead of as grains. CPU
        // solver only: the GPU path has room for a quarter of a million grains and no
        // use for it.
        private readonly SandBed bed = new SandBed();
        private readonly SandBedRenderer bedRenderer = new SandBedRenderer();
        private readonly ShapeFeed bedFeed = new ShapeFeed();
        private readonly BedShapes bedShapes = new BedShapes();

        /// <summary>Solver substeps a grain must have lain nearly still before the bed
        /// takes it: twelve, which at the default rate and two substeps a step is a
        /// quarter of a second.</summary>
        private const int BedRestSteps = 12;

        /// <summary>A grain with less life than this is left alone, so a sprayer set to
        /// a short lifetime still makes sand that goes away.</summary>
        private const float BedMinLife = 600f;

        /// <summary>How close to a shape a grain must be to stay a grain: three radii of
        /// the default sand.</summary>
        private const float BedShapeMargin = 0.09f;

        /// <summary>Sand steps between re-scans for colliders: ten, about four tenths of a
        /// second. A machine that breaks, or is built, mid-run is picked up quickly, and
        /// the scan is cheap next to a solver step.</summary>
        private const int RescanSteps = 10;

        private const int BedAbsorbPerStep = 3000;
        private const int BedReleaseTests = 6000;
        private const int BedRelaxColumns = 1500;

        private Grain[] grains;
        private int count;
        private float[] forces = new float[6];
        private float accumulator;
        private bool dirtyUpload;
        private int rescanCountdown;

        /// <summary>
        /// Rows beyond the five media. An emitter whose advanced settings are open
        /// gets one of these instead of editing the medium everywhere -- two sprayers
        /// can then throw sand of different coarseness in the same level.
        /// </summary>
        private const int MaxVariants = 59;
        private int variantsUsed;

        public int Count { get { return count; } }

        public SandMaterials.Params[] Materials { get { return materials; } }

        /// <summary>How many more grains will be admitted. A region uses this to choose
        /// its own spacing rather than pouring until it is cut off. This is the governor's
        /// ceiling, which sits below the array's size while the solver is struggling.</summary>
        public int Room { get { return Mathf.Max(governor.SoftCapacity - count, 0); } }

        // ------------------------------------------------------------------ governor
        //
        // Times its own steps and spends quality to stay inside a budget; see
        // SandGovernor. Only the CPU path is governed.
        private readonly SandGovernor governor = new SandGovernor();
        private readonly System.Diagnostics.Stopwatch stepTimer = new System.Diagnostics.Stopwatch();

        /// <summary>How coarse newly emitted grains are, 0 being ordinary.</summary>
        public int DetailLevel { get { return governor.Lod; } }

        /// <summary>
        /// The table row to spawn for a medium at the present detail level, and how much
        /// more sand one such grain stands for than an ordinary one. An emitter spawns
        /// that many fewer, so the amount of sand it makes does not change.
        /// </summary>
        public int Coarsen(int material, out float volumeFactor)
        {
            volumeFactor = 1f;
            int level = governor.Lod;
            if (level <= 0 || material < 0 || material >= SandMaterials.Count) return material;

            int row = SandMaterials.LodRow(material, level);
            float ratio = materials[row].radius / Mathf.Max(materials[material].radius, 1e-6f);
            volumeFactor = ratio * ratio * ratio;
            return row;
        }

        /// <summary>
        /// Claims a row for an emitter, seeded from the medium it is based on.
        /// Returns the base index unchanged when the table is full, so a level with
        /// too many customised emitters loses the customising and not the sand.
        /// </summary>
        public int ClaimVariant(int baseMaterial)
        {
            if (variantsUsed >= MaxVariants) return baseMaterial;
            int slot = SandMaterials.FirstVariant + variantsUsed;
            variantsUsed++;
            int baseIndex = Mathf.Clamp(baseMaterial, 0, SandMaterials.Count - 1);
            materials[slot] = materials[baseIndex];
            variantBase[slot] = baseIndex;
            materialsDirty = true;
            return slot;
        }

        /// <summary>Writes a variant's row. Ignored for the five base media, which no
        /// single emitter is allowed to redefine for everyone else.</summary>
        public void WriteVariant(int slot, SandMaterials.Params row)
        {
            if (slot < SandMaterials.FirstVariant || slot >= materials.Length) return;
            materials[slot] = row;
            materialsDirty = true;
        }

        /// <summary>
        /// Which of the five media each row is a variant of. A variant borrows its
        /// tint and its atlas tile from there, so a coarser sand still looks like
        /// sand.
        /// </summary>
        public int[] VariantBase { get { return variantBase; } }

        /// <summary>Re-seeds a variant's row from a medium, keeping the row.</summary>
        public void ReseatVariant(int slot, int baseMaterial)
        {
            if (slot < SandMaterials.FirstVariant || slot >= materials.Length) return;
            int baseIndex = Mathf.Clamp(baseMaterial, 0, SandMaterials.Count - 1);
            materials[slot] = materials[baseIndex];
            variantBase[slot] = baseIndex;
            materialsDirty = true;
        }
        public bool UsingGpu { get { return solver != null && solver.OnGpu; } }

        /// <summary>The field if one exists, without creating it. The probe asks, so that
        /// pressing F9 in a level with no sand does not conjure a field.</summary>
        public static SandField Existing { get { return instance; } }

        /// <summary>
        /// What the sand can see right now, as lines for the probe's report. This is the
        /// answer to "sand goes through my blocks": it says how many colliders the feed
        /// holds and how many of them reached the solver, how many grains are touching
        /// one this instant, how hard the solver last pushed, and what is near the sand
        /// that the solver was never given.
        /// </summary>
        public System.Collections.Generic.List<string> Describe()
        {
            System.Collections.Generic.List<string> lines = new System.Collections.Generic.List<string>();
            lines.Add("solver " + (solver == null ? "none" : (solver.OnGpu ? "GPU" : "CPU"))
                + ", grains " + count + " of " + capacity
                + ", simulating " + StatMaster.levelSimulating);
            lines.Add("bed " + (bed.Enabled ? "on" : "off") + ": " + bed.FilledColumns
                + " columns in " + bed.ChunkCount + " chunks, " + bed.Volume.ToString("F3") + " m^3");
            lines.Add("ground " + (ground.Built ? "built" : "NOT built"));
            lines.Add("colliders kept by the last scan: " + shapes.SourceCount
                + "; reached the solver last step: " + shapes.ShapeCount
                + " (" + shapes.MovableShapes + " on movable bodies, " + shapes.Bodies.Count + " bodies)");
            lines.Add("governor: pressure " + governor.Pressure + " of " + SandGovernor.MaxPressure
                + " (solver iterations " + governor.Iterations(Iterations)
                + ", grain size level " + governor.Lod + "), step cost "
                + governor.CostMs.ToString("F1") + " ms of " + StepBudgetMs.ToString("F0")
                + " (" + governor.UsPerGrain.ToString("F1") + " us a grain), admitting up to "
                + governor.SoftCapacity + " grains");
            lines.Add("last push: " + lastPushed + " bodies, " + lastForce.ToString("F1")
                + " N summed over substeps (a still pile of sand weighs half of that)");

            if (solver != null && !solver.OnGpu && count > 0)
            {
                Grain[] g = solver.Grains;
                int touching = 0;
                GpuShape[] sh = shapes.Shapes;
                for (int i = 0; i < count; i++)
                {
                    int mat = (int)(g[i].material & SandFormat.MaterialMask);
                    float reach = materials[mat].radius * 1.5f;
                    for (int k = 0; k < shapes.ShapeCount; k++)
                    {
                        float d;
                        Vector3 n;
                        ShapeMath.Distance(sh[k], g[i].pos, out d, out n);
                        if (d < reach) { touching++; break; }
                    }
                }
                lines.Add("grains touching a shape right now: " + touching + " of " + count);
                lines.Add("not given to the solver, near the sand: " + shapes.DescribeIgnored(lastBounds));
            }
            return lines;
        }

        public static SandField Instance
        {
            get
            {
                if (instance == null)
                {
                    GameObject go = new GameObject("SandField");
                    Object.DontDestroyOnLoad(go);
                    instance = go.AddComponent<SandField>();
                }
                return instance;
            }
        }

        private int capacity;
        private bool materialsDirty;
        private bool wasSimulating;
        private readonly int[] variantBase = new int[SandMaterials.FirstVariant + MaxVariants];

        private void Awake()
        {
            // The solver is chosen before the grain array is sized, because the two
            // ceilings are an order of magnitude apart.
            SandMaterials.Params[] baseTable = SandMaterials.Build();
            materials = new SandMaterials.Params[SandMaterials.FirstVariant + MaxVariants];
            for (int i = 0; i < baseTable.Length; i++) materials[i] = baseTable[i];

            // Each row's look: a coarse row looks like the medium it is a coarse version
            // of, and an emitter's variant is overwritten with its own base when claimed.
            int[] looks = SandMaterials.InitialLooks(variantBase.Length);
            for (int i = 0; i < looks.Length; i++) variantBase[i] = looks[i];
            // The GPU solver declines rather than throws when the machine has no
            // compute support or the mod has no shader bundle, so this reads as a
            // preference and not as a requirement.
            GpuSolver gpu = new GpuSolver();
            if (gpu.Init(GpuCapacity, materials))
            {
                capacity = GpuCapacity;
                grains = new Grain[capacity];
                solver = gpu;
            }
            else
            {
                gpu.Dispose();
                capacity = CpuCapacity;
                grains = new Grain[capacity];
                CpuSolver cpu = new CpuSolver();
                cpu.Init(capacity, materials);
                solver = cpu;
                // One array between the two: Spawn writes where the solver reads, so
                // there is nothing to upload and reaping a dead grain is a single
                // swap rather than a copy of the whole field.
                grains = cpu.Grains;
                cpu.Ground = ground;

                bed.Enabled = true;
                bed.SetGround(ground);
                bed.SetMaterials(materials);
                cpu.Bed = bed;
                stepper = cpu;
            }

            // Start low and let the governor measure what the machine can afford: forty
            // thousand grains admitted in the first second is a stall on a slow one.
            governor.BudgetMs = StepBudgetMs;
            governor.Reset(solver.OnGpu ? capacity : Mathf.Min(capacity, 6000));

            renderer.Init(materials);
            bedRenderer.Init();
            SceneManager.sceneLoaded += SceneLoaded;
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= SceneLoaded;
            if (solver != null) solver.Dispose();
            renderer.Dispose();
            bedRenderer.Dispose(bed);
            water.Forget();
        }

        private void SceneLoaded(Scene scene, LoadSceneMode mode)
        {
            // A level change invalidates every collider the feed holds and every grain
            // in the field: they belonged to a level that no longer exists.
            if (stepper != null) stepper.Abort();
            count = 0;
            bed.Clear();
            water.Forget();
            ground.Forget();
            shapes.Rescan();
            bedFeed.Rescan();
            rescanCountdown = 0;
        }

        // ------------------------------------------------------------------- filling

        /// <summary>Adds one grain. Silently ignored once the field is full -- a
        /// sprayer held open should slow down, not take the level with it.</summary>
        public void Spawn(Vector3 position, Vector3 velocity, int material, Color colour,
                          float lifetime)
        {
            // Thrown sand pushes what it hits from the moment it leaves the nozzle. Only
            // grains placed where they may already overlap something -- a region's lattice --
            // wait out SettleSeconds before they may push a block; sprayed sand used to wait
            // too, and so blasted a block at close range without moving it.
            Add(position, velocity, material, colour, lifetime, SandFormat.SettleSeconds + 0.01f);
        }

        private void Add(Vector3 position, Vector3 velocity, int material, Color colour,
                         float lifetime, float age)
        {
            if (count >= governor.SoftCapacity) return;

            Grain g = new Grain();
            g.pos = position;
            g.prevPos = position;
            g.vel = velocity;
            g.material = (uint)Mathf.Clamp(material, 0, materials.Length - 1);
            g.colour = SandMaterials.PackColour(colour);
            g.packing = 0f;
            g.wetTimer = 0f;
            g.life = Mathf.Max(lifetime, 0.01f);
            g.age = age;

            grains[count] = g;
            count++;
            dirtyUpload = true;
        }

        public const int RegionBox = 0;
        public const int RegionCylinder = 1;
        public const int RegionSphere = 2;

        /// <summary>
        /// Fills a region with grains on a jittered lattice and returns how many went
        /// in. The count follows the volume, so scaling a region up in the level
        /// editor puts more medium in it -- which is the whole contract of the region
        /// objects.
        ///
        /// A perfect lattice settles into a visible crystal; the jitter is what makes
        /// it a pile.
        /// </summary>
        public int FillRegion(int kind, Vector3 centre, Quaternion rotation, Vector3 halfExtent,
                              int material, Color colour, float spacing, float lifetime)
        {
            if (spacing <= 1e-4f) return 0;

            // On the CPU path a region is sand placed in the level, which is what the bed
            // is. Filled with grains instead, a tall block goes off like a charge under
            // Besiege's gravity, and a big one costs the whole grain budget before anything
            // has touched it. See SandBed.FillShape.
            if (bed.Enabled)
            {
                Bounds around = new Bounds(centre, Vector3.one * (halfExtent.magnitude * 2f));
                if (!ground.Covers(around)) ground.Build(around);

                int shape = kind == RegionCylinder ? SandBed.ShapeCylinder
                          : (kind == RegionSphere ? SandBed.ShapeEllipsoid : SandBed.ShapeBox);
                int columns = bed.FillShape(shape, centre, rotation, halfExtent, material,
                                            SandMaterials.PackColour(colour));
                if (columns >= 0) return columns;

                // Too big to hold as columns: fall through to the grain lattice below.
            }

            // Spacing is a wish, not an instruction. A big region at a fine spacing
            // asks for hundreds of thousands of grains; rather than pour until the
            // field is full and stop mid-box -- which looks like half a region -- the
            // step is opened up until the whole volume fits in what is left.
            int room = Room;
            if (room > 0)
            {
                float volume = halfExtent.x * halfExtent.y * halfExtent.z * 8f;
                float needed = Mathf.Pow(volume / (room * LayerRise), 1f / 3f);

                // Coarser grains first: a region of bigger grains holds the same sand with
                // fewer of them, where a sparser lattice of the same grains holds less. Only
                // when the coarsest is still too fine does the lattice open up.
                if (needed > spacing * 1.05f && material >= 0 && material < SandMaterials.Count)
                {
                    int level = 0;
                    while (level < SandMaterials.LodLevels
                           && spacing * SandMaterials.LodScale[level] < needed) level++;
                    if (level > 0)
                    {
                        int row = SandMaterials.LodRow(material, level);
                        spacing *= materials[row].radius / Mathf.Max(materials[material].radius, 1e-6f);
                        material = row;
                    }
                }
                if (needed > spacing) spacing = needed;
            }

            // Ground under the region, so that no grain is born below it: a grain that starts
            // inside the floor is pushed out in one go and leaves at the speed that takes.
            float grainRadius = materials[Mathf.Clamp(material, 0, materials.Length - 1)].radius;
            if (bed.Enabled)
            {
                Bounds area = new Bounds(centre, Vector3.one * (halfExtent.magnitude * 2f));
                if (!ground.Covers(area)) ground.Build(area);
            }

            // Every other layer is shifted half a step, so each grain starts in the pocket of
            // the four below it. Grains cannot roll, so friction holds a column of them standing
            // where a real one would topple, and a block filled as columns stood for seconds
            // with vertical walls before it crumbled. Jitter breaks up the crystal; the solver
            // caps how fast an overlap can push grains apart, so it no longer has to be kept
            // to what the gap between them allows.
            float jitter = spacing * 0.25f;
            float rise = spacing * LayerRise;

            int nx = Mathf.Max(1, Mathf.FloorToInt(halfExtent.x * 2f / spacing));
            int ny = Mathf.Max(1, Mathf.FloorToInt(halfExtent.y * 2f / rise));
            int nz = Mathf.Max(1, Mathf.FloorToInt(halfExtent.z * 2f / spacing));

            int added = 0;
            for (int x = 0; x < nx; x++)
                for (int y = 0; y < ny; y++)
                    for (int z = 0; z < nz; z++)
                    {
                        float shift = (y & 1) * 0.5f;
                        float lx = (x + 0.5f + shift) * spacing - halfExtent.x;
                        float lz = (z + 0.5f + shift) * spacing - halfExtent.z;
                        if (lx > halfExtent.x || lz > halfExtent.z) continue;
                        Vector3 local = new Vector3(lx, (y + 0.5f) * rise - halfExtent.y, lz);

                        if (!Inside(kind, local, halfExtent)) continue;

                        local += new Vector3(Random.Range(-1f, 1f),
                                             Random.Range(-1f, 1f),
                                             Random.Range(-1f, 1f)) * jitter;

                        Vector3 at = centre + rotation * local;
                        if (bed.Enabled)
                        {
                            float floor = ground.Height(at);
                            if (floor > -9000f && at.y < floor + grainRadius) continue;
                        }

                        if (count >= governor.SoftCapacity) return added;
                        Add(at, Vector3.zero, material, colour, lifetime, 0f);
                        added++;
                    }
            return added;
        }

        /// <summary>Height of one layer of a filled region over the step across it: the layers
        /// nest, each in the pockets of the one below.</summary>
        private const float LayerRise = 0.82f;

        /// <summary>
        /// Whether a lattice point is inside the region's shape. The lattice is always
        /// the box; a cylinder and a sphere are that box with the corners rejected,
        /// which keeps one loop instead of three.
        /// </summary>
        private static bool Inside(int kind, Vector3 local, Vector3 halfExtent)
        {
            if (kind == RegionBox) return true;

            if (kind == RegionCylinder)
            {
                // The cylinder stands up: its axis is Y, which is what a player
                // expects of a cylinder dropped into a level, and it matches the mesh.
                float u = local.x / Mathf.Max(halfExtent.x, 1e-4f);
                float v = local.z / Mathf.Max(halfExtent.z, 1e-4f);
                return u * u + v * v <= 1f;
            }

            float a = local.x / Mathf.Max(halfExtent.x, 1e-4f);
            float b = local.y / Mathf.Max(halfExtent.y, 1e-4f);
            float c = local.z / Mathf.Max(halfExtent.z, 1e-4f);
            return a * a + b * b + c * c <= 1f;
        }

        public void Clear()
        {
            // A step under way was working on grains that no longer exist.
            if (stepper != null) stepper.Abort();
            count = 0;
            bed.Clear();
            dirtyUpload = true;

            // A new run is a new load; judge it afresh rather than carry over the last one's.
            governor.NewRun();
        }

        /// <summary>
        /// Puts back a grain the bed is giving up. It is born old enough to push a
        /// block at once -- a wheel that has just sunk into a pile should meet sand, not
        /// a settling period -- and with a lifetime the bed will not mind.
        /// </summary>
        public void Release(Vector3 position, int material, uint colour)
        {
            if (count >= grains.Length) return;

            Grain g = new Grain();
            g.pos = position;
            g.prevPos = position;
            g.vel = Vector3.zero;
            g.material = (uint)Mathf.Clamp(material, 0, materials.Length - 1);
            g.colour = colour;
            g.packing = 0f;
            g.wetTimer = 0f;
            g.life = MediaSettings.DefaultLifetime;
            g.age = SandFormat.SettleSeconds + 0.1f;

            grains[count] = g;
            count++;
            dirtyUpload = true;
        }

        // ------------------------------------------------------------------ stepping

        private void FixedUpdate()
        {
            // Medium belongs to a run. Stopping the simulation puts the level back as
            // it was, which is what every other thing in Besiege does, and it is also
            // what stops a level accumulating sand across a dozen attempts until it
            // crawls.
            bool simulating = StatMaster.levelSimulating;
            if (wasSimulating && !simulating) Clear();

            // A run builds its machine anew, so every collider the feed remembers belongs
            // to one that no longer exists. Without this the first sand of a second run
            // saw the old, destroyed machine and passed through the new one until the
            // next scheduled scan.
            if (!wasSimulating && simulating) rescanCountdown = 0;
            wasSimulating = simulating;

            // A step in progress is advanced a slice at a time, one slice per fixed update,
            // so its cost is spread over the frames it spans instead of landing on one.
            if (stepper != null && stepper.InProgress)
            {
                AdvanceSlice();
                return;
            }

            if (count <= 0 && !bed.Active) return;

            accumulator += Time.fixedDeltaTime;
            float period = 1f / Mathf.Max(StepsPerSecond, 1);
            if (accumulator < period) return;

            float dt = accumulator;
            accumulator = 0f;
            slices = Mathf.Max(1, Mathf.RoundToInt(period / Mathf.Max(Time.fixedDeltaTime, 1e-4f)));
            Step(dt);
            if (stepper != null && stepper.InProgress) AdvanceSlice();
        }

        /// <summary>The CPU solver, when that is what is running, which can be advanced a
        /// slice at a time. Null on the GPU path, which steps in one dispatch.</summary>
        private CpuSolver stepper;

        private int slices = 4;
        private int stepIterations = 3;
        private float stepDt;
        private float stepMs;
        private Bounds stepWide;

        private void AdvanceSlice()
        {
            stepTimer.Reset();
            stepTimer.Start();
            int per = Mathf.Max(1, Mathf.CeilToInt(
                (float)stepper.PassesFor(Substeps, stepIterations) / slices));
            bool done = stepper.Advance(per);
            stepTimer.Stop();
            stepMs += (float)stepTimer.Elapsed.TotalMilliseconds;

            if (done) EndStep(true);
        }

        /// <summary>
        /// Drops grains whose lifetime ran out, by swapping the last live one into the
        /// gap. Order is not meaningful here, so this costs one move per dead grain
        /// rather than a shuffle of the whole field.
        /// </summary>
        private void Reap()
        {
            int i = 0;
            while (i < count)
            {
                if ((grains[i].material & SandFormat.FlagDead) != 0)
                {
                    count--;
                    grains[i] = grains[count];
                    dirtyUpload = true;
                }
                else i++;
            }
        }

        /// <summary>
        /// Begins a sand step: everything that has to be settled before the solver runs.
        /// On the CPU path the solver is then advanced a slice per fixed update and
        /// <see cref="EndStep"/> runs when it finishes; on the GPU path it runs at once.
        /// </summary>
        private void Step(float dt)
        {
            stepTimer.Reset();
            stepTimer.Start();

            Reap();
            bool useBed = bed.Active;
            if (count <= 0 && !useBed) return;

            if (rescanCountdown <= 0)
            {
                shapes.Rescan();
                if (bed.Enabled) bedFeed.Rescan();
                rescanCountdown = RescanSteps;
            }
            rescanCountdown--;

            // The solver's own grid is sized by the grains alone. The ground and the
            // bed's shapes have to cover the bed as well, which can be far larger.
            Bounds bounds = count > 0 ? Bounds(0.5f) : bed.Extent;
            Bounds wide = bounds;
            if (useBed && count > 0) wide.Encapsulate(bed.Extent);

            // Rasterising the world is up to nine thousand raycasts, so it happens
            // when the medium leaves the area it was built for, not every step.
            if (!ground.Covers(wide)) ground.Build(wide);

            if (materialsDirty)
            {
                solver.SetMaterials(materials);
                bed.SetMaterials(materials);
                materialsDirty = false;
            }

            stepDt = dt;
            stepWide = wide;

            if (count > 0)
            {
                water.Refresh();
                if (water.Available && !solver.OnGpu) water.CacheForCpu(64);

                shapes.Gather(bounds);
                solver.SetShapes(shapes.Shapes, shapes.ShapeCount, shapes.Bodies.Count);
                if (stepper != null)
                {
                    stepper.ShapeSpin = shapes.Spin;
                    stepper.BodyCentres = shapes.BodyCentres;
                }

                if (dirtyUpload)
                {
                    solver.Upload(grains, count);
                    dirtyUpload = false;
                }

                lastBounds = bounds;
                governor.BudgetMs = StepBudgetMs;
                stepIterations = governor.Iterations(Iterations);

                if (stepper != null)
                {
                    // Gravity is read from the level every step and never assumed. The
                    // space levels have their own, and StatMaster.GodTools.GravityDisabled
                    // is the player's switch, not this mod's.
                    stepper.BeginStep(dt, Substeps, stepIterations, Physics.gravity, water, bounds, count);
                    stepTimer.Stop();
                    stepMs = (float)stepTimer.Elapsed.TotalMilliseconds;
                    return;
                }

                solver.Step(dt, Substeps, stepIterations, Physics.gravity, water, bounds, count);
                stepTimer.Stop();
                stepMs = (float)stepTimer.Elapsed.TotalMilliseconds;
                EndStep(false);
                return;
            }

            stepTimer.Stop();
            stepMs = 0f;
            EndStep(false);
        }

        /// <summary>
        /// What follows the solver: hand the reaction to the rigidbodies, tell the governor
        /// what the step cost, and give the bed its turn.
        /// </summary>
        private void EndStep(bool timed)
        {
            if (count > 0 && solver != null)
            {
                ApplyForces(stepDt / Mathf.Max(Substeps, 1));
                if (!solver.OnGpu && timed)
                    governor.Record(stepMs, count, capacity, stepDt);
            }
            if (bed.Enabled) StepBed(stepWide);
        }

        /// <summary>
        /// The bed's half of a step, after the grains have moved: take in the grains
        /// that have come to rest, give back the sand anything is touching, and let
        /// what is too steep slump.
        ///
        /// Absorbing runs before releasing on purpose. A grain beside a shape is never
        /// absorbed, and the sand a shape reaches into is released the same step, so a
        /// block settling into a pile does not see the pile flicker between the two.
        /// </summary>
        private void StepBed(Bounds wide)
        {
            Bounds reach = wide;
            reach.Expand(1f);
            bedFeed.Gather(reach);
            bedShapes.Prepare(bedFeed);

            bed.Absorb(grains, count, bedShapes, BedRestSteps, BedMinLife,
                       BedShapeMargin, BedAbsorbPerStep);
            bed.Release(bedShapes, this, BedShapeMargin, BedReleaseTests);
            bed.Relax(BedRelaxColumns);
        }

        /// <summary>Most speed sand may add to a body in one step, in metres a second. Normal
        /// contact is a fraction of this; it exists so that a bad frame -- a block dropped
        /// into the middle of a pile -- shoves rather than launches.</summary>
        private const float MaxDeltaVPerStep = 25f;

        /// <summary>The same for spin, in radians a second.</summary>
        private const float MaxDeltaSpinPerStep = 20f;

        private float lastForce;
        private int lastPushed;
        private Bounds lastBounds;

        /// <summary>
        /// Hands the solver's reaction to the rigidbodies as impulses. The readback is
        /// the force summed over the step's substeps -- one contribution per substep --
        /// so it lasted <paramref name="substep"/> seconds each, not the whole step, and
        /// not one fixed step either. A plain AddForce is an impulse of force times the
        /// fixed step (0.01 s) applied once per sand step (0.04 s), which handed a block
        /// about 45% of the sand's weight: sand collided with a machine and barely
        /// pushed it. The impulse is the force times the time it acted for.
        /// </summary>
        private void ApplyForces(float substep)
        {
            lastForce = 0f;
            lastPushed = 0;
            int bodies = shapes.Bodies.Count;
            if (bodies <= 0) return;

            int want = bodies * SandFormat.ForceStride;
            if (forces.Length < want) forces = new float[want];
            solver.ReadForces(forces, bodies);

            for (int i = 0; i < bodies; i++)
            {
                Rigidbody rb = shapes.Bodies[i];
                if (rb == null) continue;
                int b = i * SandFormat.ForceStride;
                Vector3 force = new Vector3(forces[b + 0], forces[b + 1], forces[b + 2]);
                Vector3 torque = new Vector3(forces[b + 3], forces[b + 4], forces[b + 5]);
                float scale = 1f;
                if (force.sqrMagnitude > 1e-6f)
                {
                    Vector3 impulse = force * substep;
                    float most = Mathf.Max(rb.mass, 0.01f) * MaxDeltaVPerStep;
                    if (impulse.sqrMagnitude > most * most)
                    {
                        scale = most / impulse.magnitude;
                        impulse *= scale;
                    }
                    rb.AddForce(impulse, ForceMode.Impulse);
                    lastForce += force.magnitude;
                    lastPushed++;
                }
                if (torque.sqrMagnitude > 1e-6f)
                {
                    // Cut with the force, so a capped push does not keep its full twist, and
                    // capped on its own against the body's easiest axis to turn: a light block
                    // levered by a heavy pile should rock, not spin up like a propeller.
                    Vector3 angular = torque * (substep * scale);
                    Vector3 inertia = rb.inertiaTensor;
                    float easiest = Mathf.Max(Mathf.Min(inertia.x, Mathf.Min(inertia.y, inertia.z)), 1e-4f);
                    float mostTurn = easiest * MaxDeltaSpinPerStep;
                    if (angular.sqrMagnitude > mostTurn * mostTurn) angular = angular.normalized * mostTurn;
                    rb.AddTorque(angular, ForceMode.Impulse);
                }
            }
        }

        private Bounds Bounds(float pad)
        {
            Grain[] g = solver.OnGpu ? null : solver.Grains;
            if (g == null) g = grains;

            Vector3 lo = g[0].pos, hi = g[0].pos;
            for (int i = 1; i < count; i++)
            {
                Vector3 p = g[i].pos;
                if (p.x < lo.x) lo.x = p.x; else if (p.x > hi.x) hi.x = p.x;
                if (p.y < lo.y) lo.y = p.y; else if (p.y > hi.y) hi.y = p.y;
                if (p.z < lo.z) lo.z = p.z; else if (p.z > hi.z) hi.z = p.z;
            }
            Bounds b = new Bounds((lo + hi) * 0.5f, hi - lo);
            b.Expand(pad * 2f);
            return b;
        }

        private void Update()
        {
            // The bed is drawn whenever it holds sand, even with no grains moving.
            if (bed.Enabled) bedRenderer.Draw(bed, ground, 0);

            if (count <= 0) return;

            // The GPU path keeps grains in a compute buffer and is drawn by a vertex
            // shader reading it; until that shader exists this path draws nothing
            // rather than stalling the pipeline to fetch 40000 grains every frame.
            if (solver.OnGpu) return;

            // Layer 0. A level's cameras draw it, unlike the build area's, where
            // anything new has to be put on the machine's own layer or it is present,
            // correctly placed and invisible (notes repo, 13-drawing-over-the-machine).
            renderer.Draw(solver.Grains, count, materials, variantBase, 0);
        }
    }
}
