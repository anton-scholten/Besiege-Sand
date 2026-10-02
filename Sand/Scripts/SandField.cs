using System.Collections.Generic;
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
        private readonly SandBedColliders bedColliders = new SandBedColliders();

        /// <summary>Milliseconds a sand step may spend cooking the bed's colliders.</summary>
        private const float ColliderBudgetMs = 3f;
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
        private const int RescanSteps = 25;

        /// <summary>Rescans between full ones, which look at every collider in the level
        /// rather than only those on rigidbodies: sixty, about a minute. The level's fixed
        /// colliders do not change during a run, and a full scan is the start of every run.</summary>
        private const int FullRescanScans = 60;
        private int fullRescanCountdown;

        private const int BedAbsorbPerStep = 3000;
        private const int BedReleaseTests = 6000;

        /// <summary>Most grains the bed gives back in one step. A machine driving onto a deep
        /// pile used to have thousands handed to the solver at once, and the step after that
        /// cost several times the one before; at this rate a wheel still meets sand, and the
        /// governor sees the load coming.</summary>
        private const int BedReleasePerStep = 600;

        /// <summary>Columns checked for slumping per step. Each costs a few dozen lookups, and a
        /// region placed in the level starts with every column needing a look.</summary>
        private const int BedRelaxColumns = 300;

        /// <summary>Milliseconds a sand step may go on slumping past that, while there is
        /// slumping to do: a region's walls should fall to their angle in a second or two.</summary>
        private const float BedRelaxMs = 3f;

        /// <summary>Milliseconds a fixed update may spend raycasting new ground for a region
        /// or for the bed's surroundings. A big region's ground arrives over a few frames.</summary>
        private const float GroundBudgetMs = 3f;

        private Grain[] grains;
        private int count;
        private float[] forces = new float[6];
        private float accumulator;
        private bool dirtyUpload;
        private int rescanCountdown;

        /// <summary>
        /// Rows beyond the media and their coarse rows. An emitter whose advanced settings are open
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
        /// Which medium each row is a variant of. A variant borrows its
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
                + " columns in " + bed.ChunkCount + " chunks, " + bed.Volume.ToString("F3") + " m^3, "
                + bedColliders.Count + " chunk colliders near machines");
            lines.Add("ground: " + ground.TileCount + " tiles of 4 m cast"
                + (pendingFills.Count > 0 ? ", " + pendingFills.Count + " regions waiting for theirs" : ""));
            lines.Add("slowest recent frame: " + (worstFrame.Length > 0 ? worstFrame : "none over " + HitchMs + " ms"));
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
            bedColliders.Init(transform, bed);
            bedRenderer.Looks = variantBase;
            bedRenderer.OnRetire = bedColliders.Destroy;
            SceneManager.sceneLoaded += SceneLoaded;
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= SceneLoaded;
            if (solver != null) solver.Dispose();
            renderer.Dispose();
            bedColliders.Dispose();
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
            pendingFills.Clear();
            bedAtStart = null;
            countAtStart = 0;
            regionsFilled.Clear();
            regionsKept.Clear();
            water.Forget();
            ground.Forget();
            shapes.Rescan();
            bedFeed.CopyBodySources(shapes);
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
            if (SourcesPaused || SandNet.IsClient) return;   // in multiplayer the host sprays
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
            g.colour = Varied(SandMaterials.PackColour(colour), (int)g.material);
            g.packing = 0f;
            g.wetTimer = 0f;
            g.life = Mathf.Max(lifetime, 0.01f);
            g.age = age;

            grains[count] = g;
            count++;
            dirtyUpload = true;
        }

        private uint varietySeed = 0x9E3779B9u;

        /// <summary>A grain's colour with its medium's variety: some grains lighter, some
        /// darker, picked by a cheap hash so neighbours in a pour differ.</summary>
        private uint Varied(uint colour, int row)
        {
            varietySeed ^= varietySeed << 13; varietySeed ^= varietySeed >> 17; varietySeed ^= varietySeed << 5;
            int look = row >= 0 && row < variantBase.Length ? variantBase[row] : 0;
            return SandMaterials.Vary(colour, look, (int)(varietySeed >> 8));
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
            return FillRegion(kind, centre, rotation, halfExtent, material, colour, spacing, lifetime, 0L);
        }

        /// <summary>The same, for a region identified by <paramref name="regionKey"/> -- which
        /// object, where, and of what -- so a run whose sand was kept does not fill it twice.</summary>
        public int FillRegion(int kind, Vector3 centre, Quaternion rotation, Vector3 halfExtent,
                              int material, Color colour, float spacing, float lifetime, long regionKey)
        {
            if (spacing <= 1e-4f) return 0;

            // On the CPU path a region is sand placed in the level, which is what the bed
            // is. Filled with grains instead, a tall block goes off like a charge under
            // Besiege's gravity, and a big one costs the whole grain budget before anything
            // has touched it. See SandBed.FillShape.
            //
            // It is not filled here but queued, and filled once the ground under it has been
            // cast, a few milliseconds of raycasts a frame (see FillPending): every region in a
            // level fills on the frame the run starts, and casting all their ground at once was
            // a stall.
            // In multiplayer the host fills the regions and sends the sand.
            if (SandNet.IsClient) return 0;

            // A run kept from last time still has what this region filled; filling it again
            // would put a second load on top of it.
            if (regionKey != 0)
            {
                if (regionsKept.Contains(regionKey)) return 0;
                regionsFilled.Add(regionKey);
            }

            if (bed.Enabled)
            {
                PendingFill f = new PendingFill();
                f.kind = kind; f.centre = centre; f.rotation = rotation; f.halfExtent = halfExtent;
                f.material = material; f.colour = colour; f.spacing = spacing; f.lifetime = lifetime;
                f.row = int.MinValue;
                pendingFills.Add(f);
                return 0;
            }
            return FillLattice(kind, centre, rotation, halfExtent, material, colour, spacing, lifetime);
        }

        private struct PendingFill
        {
            public int kind, material;
            public Vector3 centre, halfExtent;
            public Quaternion rotation;
            public Color colour;
            public float spacing, lifetime;
            public int row;   // the next row of columns to fill; see SandBed.FillShape
            public RegionSupport support;   // what it rests on, found before it is filled
        }

        /// <summary>Half the size of a turned box on each world axis.</summary>
        private static Vector3 WorldHalf(Quaternion r, Vector3 half)
        {
            float x = r.x, y = r.y, z = r.z, w = r.w;
            float xx = x * x, yy = y * y, zz = z * z;
            float m00 = 1f - 2f * (yy + zz), m01 = 2f * (x * y - z * w), m02 = 2f * (x * z + y * w);
            float m10 = 2f * (x * y + z * w), m11 = 1f - 2f * (xx + zz), m12 = 2f * (y * z - x * w);
            float m20 = 2f * (x * z - y * w), m21 = 2f * (y * z + x * w), m22 = 1f - 2f * (xx + yy);
            return new Vector3(
                Mathf.Abs(m00) * half.x + Mathf.Abs(m01) * half.y + Mathf.Abs(m02) * half.z,
                Mathf.Abs(m10) * half.x + Mathf.Abs(m11) * half.y + Mathf.Abs(m12) * half.z,
                Mathf.Abs(m20) * half.x + Mathf.Abs(m21) * half.y + Mathf.Abs(m22) * half.z);
        }

        /// <summary>Columns of a region filled per fixed update, a few milliseconds' worth.</summary>
        private const int FillColumnsPerUpdate = 1500;

        /// <summary>Milliseconds a fixed update may spend filling regions, and casting the ground
        /// under them, while any are waiting: they should be there by the time a machine gets
        /// to them.</summary>
        private const float FillMs = 6f;
        private const float FillGroundBudgetMs = 8f;

        private readonly System.Collections.Generic.List<PendingFill> pendingFills =
            new System.Collections.Generic.List<PendingFill>();

        /// <summary>
        /// Fills the queued regions whose ground is ready, casting a little more of it each
        /// fixed update. In order, so two overlapping regions stack the way they did.
        /// </summary>
        private void FillPending()
        {
            if (SourcesPaused) return;
            while (pendingFills.Count > 0)
            {
                PendingFill f = pendingFills[0];
                // The ground under its footprint only: its bounding cube was twice the area
                // of a flat region, and casting it a few milliseconds at a time was most of why
                // a big region took seconds to appear, while machines drove through it.
                Bounds around = new Bounds(f.centre, WorldHalf(f.rotation, f.halfExtent) * 2f);

                PhaseStart();
                bool ready = ground.Ensure(around, FillGroundBudgetMs);
                PhaseEnd(PhaseGround);
                if (!ready) return;

                // What it rests on, if that is not the level's fixed ground; and whether it is
                // in the air, in which case it is filled as grains and falls.
                if (f.support == null)
                {
                    Vector3 e = WorldHalf(f.rotation, f.halfExtent);
                    f.support = new RegionSupport(ground, f.centre - e, f.centre + e);
                    pendingFills[0] = f;
                }
                if (!f.support.Done)
                {
                    PhaseStart();
                    bool found = f.support.Step(FillGroundBudgetMs);
                    PhaseEnd(PhaseGround);
                    if (!found) return;
                }
                if (f.row == int.MinValue && f.support.InAir)
                {
                    PhaseStart();
                    pendingFills.RemoveAt(0);
                    FillLattice(f.kind, f.centre, f.rotation, f.halfExtent, f.material, f.colour,
                                f.spacing, f.lifetime);
                    PhaseEnd(PhaseFill);
                    continue;
                }

                PhaseStart();
                int shape = f.kind == RegionCylinder ? SandBed.ShapeCylinder
                          : (f.kind == RegionSphere ? SandBed.ShapeEllipsoid : SandBed.ShapeBox);
                // Rows until the time for this update is spent.
                System.Diagnostics.Stopwatch fillTimer = System.Diagnostics.Stopwatch.StartNew();
                int columns = 0;
                do
                {
                    columns = bed.FillShape(shape, f.centre, f.rotation, f.halfExtent, f.material,
                                            SandMaterials.PackColour(f.colour), ref f.row,
                                            FillColumnsPerUpdate);
                }
                while (columns >= 0 && f.row != int.MaxValue && fillTimer.Elapsed.TotalMilliseconds < FillMs);
                // Too big to hold as columns: the grain lattice instead.
                if (columns < 0)
                    FillLattice(f.kind, f.centre, f.rotation, f.halfExtent, f.material, f.colour,
                                f.spacing, f.lifetime);
                PhaseEnd(PhaseFill);

                if (f.row != int.MaxValue)
                {
                    pendingFills[0] = f;   // the rest of it next fixed update
                    return;
                }
                pendingFills.RemoveAt(0);
            }
        }

        /// <summary>The region as grains on a lattice: the GPU path, and the fallback for a
        /// region too big to hold as bed.</summary>
        private int FillLattice(int kind, Vector3 centre, Quaternion rotation, Vector3 halfExtent,
                                int material, Color colour, float spacing, float lifetime)
        {

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
                ground.Ensure(area, -1f);
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
            pendingFills.Clear();
            dirtyUpload = true;
            regionsFilled.Clear();
            regionsKept.Clear();
            SandNet.HostCleared();

            // A new run is a new load; judge it afresh rather than carry over the last one's.
            governor.NewRun();
        }

        // ------------------------------------------------------------------ between runs
        //
        // Besiege puts a machine back where it was built when a run stops, and by default the
        // sand goes back too: the sand as it was when the run started -- including anything
        // poured or brushed in while building -- is captured then and restored now. With
        // KeepBetweenRuns the sand stays as the run left it instead, and the regions, which
        // fill at the start of every run, do not fill again on top of what they left.

        /// <summary>Leave the sand as a run left it, rather than as it was before the run.</summary>
        public static bool KeepBetweenRuns;

        /// <summary>Sprayers and regions add nothing while this is set; what is there stays.</summary>
        public static bool SourcesPaused;

        /// <summary>No sand computation at all while this is set: the sand is drawn where it is.</summary>
        public static bool Frozen;

        private bool wasClient;

        // ------------------------------------------------------------------ multiplayer client

        private float sinceGrains;

        /// <summary>A chunk of the host's bed.</summary>
        public void ReceiveChunk(byte[] data)
        {
            if (!bed.Enabled) return;
            bed.Decode(data);
        }

        /// <summary>The host is about to send its bed from scratch.</summary>
        public void ReceiveClear()
        {
            bed.Clear();
            count = 0;
        }

        /// <summary>The host's moving grains, as SandNet.EncodeGrains packed them.</summary>
        public void ReceiveGrains(byte[] b, int at, int kept, Vector3 origin, uint[] palette)
        {
            int n = Mathf.Min(kept, grains.Length);
            int h = at;
            for (int k = 0; k < n && h + 11 <= b.Length; k++)
            {
                Grain g = new Grain();
                float x = Bytes.GetShort(b, ref h) / 100f, y = Bytes.GetShort(b, ref h) / 100f, z = Bytes.GetShort(b, ref h) / 100f;
                g.pos = origin + new Vector3(x, y, z);
                g.prevPos = g.pos;
                g.vel = new Vector3((sbyte)b[h], (sbyte)b[h + 1], (sbyte)b[h + 2]) * 0.25f;
                h += 3;
                g.material = (uint)Mathf.Min(b[h++], materials.Length - 1);
                int pi = b[h++];
                g.colour = palette[pi < palette.Length ? pi : 0];
                g.life = 1f;
                grains[k] = g;
            }
            count = n;
            sinceGrains = 0f;
        }

        /// <summary>A client's grains between snapshots: carried along their velocity, for as long
        /// as a snapshot is late, so the sand does not jump five times a second.</summary>
        private void ClientUpdate()
        {
            SandNet.ClientTick();
            float dt = Time.deltaTime;
            if (sinceGrains < 0.3f)
                for (int k = 0; k < count; k++) grains[k].pos += grains[k].vel * dt;
            sinceGrains += dt;

            // The ground under the host's sand, for the edges of its mesh.
            if (bed.Active) ground.Ensure(bed.Extent, 2f);
        }

        /// <summary>Somebody joined: everything goes out again.</summary>
        public void ResendAll()
        {
            bed.TrackNet = true;
            bed.MarkAllNet();
        }

        private object bedAtStart;
        private Grain[] grainsAtStart;
        private int countAtStart;
        // Regions, by a key of which one and where it is: filled this run, and still here from
        // a run that was kept. A kept region is not filled again; one placed or moved since is.
        private readonly System.Collections.Generic.HashSet<long> regionsFilled = new System.Collections.Generic.HashSet<long>();
        private readonly System.Collections.Generic.HashSet<long> regionsKept = new System.Collections.Generic.HashSet<long>();

        private void StartRun()
        {
            if (stepper != null && stepper.InProgress) stepper.Abort();
            Reap();
            bedAtStart = bed.Enabled ? bed.Capture() : null;
            if (grainsAtStart == null || grainsAtStart.Length < count) grainsAtStart = new Grain[Mathf.Max(count, 16)];
            System.Array.Copy(grains, grainsAtStart, count);
            countAtStart = count;
            // Not regionsFilled: a region's own Update may well have run before this, on the
            // run's first frame, and queued its fill already. Clearing it here forgot that, so a
            // kept run did not know its regions had filled and filled them again on top.
        }

        private void EndRun()
        {
            if (KeepBetweenRuns)
            {
                regionsKept.UnionWith(regionsFilled);
                regionsFilled.Clear();
                bedAtStart = null;
                return;
            }

            if (stepper != null) stepper.Abort();
            pendingFills.Clear();
            SandNet.HostCleared();
            if (bedAtStart != null) bed.Restore(bedAtStart); else bed.Clear();
            count = 0;
            if (grainsAtStart != null && countAtStart > 0 && countAtStart <= grains.Length)
            {
                System.Array.Copy(grainsAtStart, grains, countAtStart);
                count = countAtStart;
            }
            bedAtStart = null;
            dirtyUpload = true;
            regionsFilled.Clear();
            regionsKept.Clear();
            governor.NewRun();
        }

        // ------------------------------------------------------------------ water and fire
        //
        // What makes dry sand wet and wet sand dry, besides falling in the sea (which the
        // solver handles: a grain under the water surface takes its row's wet medium). Each is
        // a few distance tests a sand step for each water cannon firing, each flamethrower
        // lit and each block on fire, and nothing at all while none is.

        private int[] wetOf = new int[0];
        private int[] dryOf = new int[0];

        /// <summary>Which row each row becomes wet and dry: from the table's own wet media.</summary>
        private void MapWetness()
        {
            int n = materials.Length;
            if (wetOf.Length != n) { wetOf = new int[n]; dryOf = new int[n]; }
            for (int r = 0; r < n; r++) { wetOf[r] = -1; dryOf[r] = -1; }
            for (int r = 0; r < n; r++)
            {
                int w = materials[r].wetMaterial;
                if (w < 0 || w >= n || w == r) continue;
                wetOf[r] = w;
                if (dryOf[w] < 0) dryOf[w] = r;
            }
        }

        private const float CannonRange = 12f;
        private const float FlameRange = 7f;
        private const float StreamSpread = 0.15f;   // the cone's widening per metre
        private const float FireReach = 1.5f;

        private void WaterAndFire()
        {
            List<WaterCannonController> cannons = shapes.Cannons;
            List<FlamethrowerController> flamers = shapes.Flamers;
            List<FireTag> fires = shapes.Fires;
            bool any = false;
            for (int i = 0; i < cannons.Count && !any; i++) if (cannons[i] != null && cannons[i].isActive) any = true;
            for (int i = 0; i < flamers.Count && !any; i++) if (flamers[i] != null && flamers[i].isFlaming) any = true;
            for (int i = 0; i < fires.Count && !any; i++) if (fires[i] != null && fires[i].burning) any = true;
            if (!any) return;

            MapWetness();
            for (int i = 0; i < cannons.Count; i++)
            {
                WaterCannonController c = cannons[i];
                if (c == null || !c.isActive) continue;
                // Steam, when it boils, dries rather than wets.
                ParticleSystem[] ps = c.boiling ? c.steamParticles : c.waterParticles;
                Transform from = ps != null && ps.Length > 0 && ps[0] != null ? ps[0].transform : c.transform;
                Stream(from.position, from.forward, CannonRange, c.boiling ? dryOf : wetOf, c.boiling ? 1f / 0.7f : 0.7f);
            }
            for (int i = 0; i < flamers.Count; i++)
            {
                FlamethrowerController f = flamers[i];
                if (f == null || !f.isFlaming) continue;
                Transform from = f.fireParticles != null ? f.fireParticles.transform : f.transform;
                Stream(from.position, from.forward, FlameRange, dryOf, 1f / 0.7f);
            }
            for (int i = 0; i < fires.Count; i++)
            {
                FireTag f = fires[i];
                if (f == null || !f.burning) continue;
                Vector3 p = f.transform.position;
                Convert(p, FireReach, dryOf);
                if (bed.Enabled) bed.ConvertSurface(p, FireReach * 0.8f, dryOf, 1f / 0.7f);
            }
        }

        /// <summary>Wets or dries the grains in a cone, and the bed where its axis meets it.</summary>
        private void Stream(Vector3 origin, Vector3 dir, float range, int[] map, float shade)
        {
            for (int k = 0; k < count; k++)
            {
                Vector3 v = grains[k].pos - origin;
                float t = v.x * dir.x + v.y * dir.y + v.z * dir.z;
                if (t <= 0f || t > range) continue;
                float off = (v - dir * t).sqrMagnitude;
                float w = 0.3f + t * StreamSpread;
                if (off > w * w) continue;
                Turn(k, map);
            }
            if (!bed.Enabled) return;
            for (float t = 0.5f; t < range; t += 0.25f)
            {
                Vector3 p = origin + dir * t;
                float h = SurfaceAt(p.x, p.z);
                if (h > -9000f && p.y <= h)
                {
                    bed.ConvertSurface(new Vector3(p.x, h, p.z), 0.3f + t * StreamSpread, map, shade);
                    break;
                }
            }
        }

        private void Convert(Vector3 centre, float radius, int[] map)
        {
            float r2 = radius * radius;
            for (int k = 0; k < count; k++)
                if ((grains[k].pos - centre).sqrMagnitude < r2) Turn(k, map);
        }

        private void Turn(int k, int[] map)
        {
            uint m = grains[k].material;
            int row = (int)(m & SandFormat.MaterialMask);
            int to = row < map.Length ? map[row] : -1;
            if (to < 0) return;
            grains[k].material = (uint)to | (m & ~SandFormat.MaterialMask & ~SandFormat.FlagWet);
            grains[k].wetTimer = 0f;
        }

        // ------------------------------------------------------------------ the brush

        /// <summary>Whether the settled bed is in use, which the brush needs.</summary>
        public bool HasBed { get { return bed.Enabled; } }

        /// <summary>Height of the sand's surface, or the ground's where there is none, at a
        /// point; far below everything where neither is known yet.</summary>
        public float SurfaceAt(float x, float z)
        {
            float g = ground.Height(x, z);
            float b;
            if (bed.TryTop(x, z, out b) && b > g) g = b;
            return g;
        }

        /// <summary>Casts the ground under an area, a few milliseconds' worth; true when ready.</summary>
        public bool PrepareGround(Bounds area, float budgetMs)
        {
            return ground.Ensure(area, budgetMs);
        }

        /// <summary>
        /// The widest the brush pours, in metres from its middle. A size can be typed far past
        /// this, but every metre of brush is a square metre of bed held in memory and swept each
        /// frame, and a kilometre-wide circle is gigabytes; the brush stops growing here.
        /// </summary>
        public const float MaxBrushRadius = 100f;

        /// <summary>
        /// Columns a brush touches in one frame. A wider circle is sampled: that many columns
        /// picked at random across it, each given its share scaled up, which over a second of
        /// holding the button comes to the same layer without a frame sweeping millions.
        /// </summary>
        private const int BrushColumns = 20000;

        private readonly System.Random brushRandom = new System.Random(7);
        private float[] brushX = new float[0], brushZ = new float[0], brushW = new float[0];

        /// <summary>
        /// The columns under a brush and how much of it each gets, 1 in the middle to 0 at the
        /// edge: every column of a small brush, a random sample of a large one. Returns how many.
        /// </summary>
        private int BrushCells(Vector3 centre, float radius, out float scale)
        {
            float cell = SandBed.Cell;
            float cells = Mathf.PI * radius * radius / (cell * cell);
            int n = 0;
            scale = 1f;
            if (cells <= BrushColumns)
            {
                int i0 = Mathf.FloorToInt((centre.x - radius) / cell), i1 = Mathf.FloorToInt((centre.x + radius) / cell);
                int j0 = Mathf.FloorToInt((centre.z - radius) / cell), j1 = Mathf.FloorToInt((centre.z + radius) / cell);
                int most = (i1 - i0 + 1) * (j1 - j0 + 1);
                BrushRoom(most);
                for (int j = j0; j <= j1; j++)
                    for (int i = i0; i <= i1; i++)
                    {
                        float x = (i + 0.5f) * cell, z = (j + 0.5f) * cell;
                        float d2 = ((x - centre.x) * (x - centre.x) + (z - centre.z) * (z - centre.z)) / (radius * radius);
                        if (d2 >= 1f) continue;
                        brushX[n] = x; brushZ[n] = z; brushW[n] = 1f - d2; n++;
                    }
                return n;
            }

            BrushRoom(BrushColumns);
            scale = cells / BrushColumns;
            for (int k = 0; k < BrushColumns; k++)
            {
                // Uniform over the disc: the square root spreads the samples evenly by area.
                double r = System.Math.Sqrt(brushRandom.NextDouble()), a = brushRandom.NextDouble() * System.Math.PI * 2.0;
                float x = centre.x + (float)(System.Math.Cos(a) * r) * radius;
                float z = centre.z + (float)(System.Math.Sin(a) * r) * radius;
                // Onto the column's own middle.
                x = (Mathf.Floor(x / cell) + 0.5f) * cell;
                z = (Mathf.Floor(z / cell) + 0.5f) * cell;
                float d2 = ((x - centre.x) * (x - centre.x) + (z - centre.z) * (z - centre.z)) / (radius * radius);
                if (d2 >= 1f) continue;
                brushX[n] = x; brushZ[n] = z; brushW[n] = 1f - d2; n++;
            }
            return n;
        }

        private void BrushRoom(int n)
        {
            if (brushX.Length >= n) return;
            brushX = new float[n]; brushZ = new float[n]; brushW = new float[n];
        }

        /// <summary>
        /// Lays <paramref name="volume"/> cubic metres of a medium over a circle as a low dome,
        /// most in the middle. Slumping then shapes it, so held in one place it grows into a
        /// cone at the medium's own angle of repose.
        /// </summary>
        public void Paint(Vector3 centre, float radius, float volume, int medium)
        {
            radius = Mathf.Clamp(radius, 0.05f, MaxBrushRadius);
            if (SandNet.IsClient) { SandNet.RequestBrush(centre, radius, volume, medium, false); return; }
            if (!bed.Enabled || volume <= 0f) return;
            medium = Mathf.Clamp(medium, 0, SandMaterials.Count - 1);
            uint colour = SandMaterials.PackColour(SandMaterials.Tints[medium]);
            float scale;
            int n = BrushCells(centre, radius, out scale);
            float total = 0f;
            for (int k = 0; k < n; k++) total += brushW[k];
            if (total <= 0f) return;
            for (int k = 0; k < n; k++)
                bed.AddVolume(brushX[k], brushZ[k], volume * brushW[k] / total, medium, colour);
        }

        /// <summary>
        /// Takes sand away over a circle, up to <paramref name="depth"/> at its middle and less
        /// towards its edge, and any loose grains standing in it.
        /// </summary>
        public void Erase(Vector3 centre, float radius, float depth)
        {
            radius = Mathf.Clamp(radius, 0.05f, MaxBrushRadius);
            if (SandNet.IsClient) { SandNet.RequestBrush(centre, radius, depth, 0, true); return; }
            if (depth <= 0f) return;
            if (bed.Enabled)
            {
                float scale;
                int n = BrushCells(centre, radius, out scale);
                // A sampled column is dug for every column it stands for.
                for (int k = 0; k < n; k++)
                    bed.RemoveVolume(brushX[k], brushZ[k], depth * scale * brushW[k]);
            }
            float r2 = radius * radius;
            for (int k = 0; k < count; k++)
            {
                Vector3 p = grains[k].pos;
                if ((p.x - centre.x) * (p.x - centre.x) + (p.z - centre.z) * (p.z - centre.z) < r2)
                    grains[k].material |= SandFormat.FlagDead;
            }
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
            g.colour = Varied(colour, (int)g.material);
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
            // A multiplayer client draws the host's sand and simulates none of its own.
            bool client = SandNet.IsClient;
            if (client != wasClient)
            {
                // Into or out of someone else's session: what is here is not theirs, or not ours.
                if (stepper != null) stepper.Abort();
                count = 0;
                bed.Clear();
                pendingFills.Clear();
                wasClient = client;
            }
            if (client)
            {
                ReportHitch("fixed update");
                return;
            }

            bool simulating = StatMaster.levelSimulating;
            if (wasSimulating && !simulating) EndRun();

            // A run builds its machine anew, so every collider the feed remembers belongs
            // to one that no longer exists. Without this the first sand of a second run
            // saw the old, destroyed machine and passed through the new one until the
            // next scheduled scan.
            if (!wasSimulating && simulating)
            {
                rescanCountdown = 0;
                fullRescanCountdown = 0;
                StartRun();
            }
            wasSimulating = simulating;

            // Frozen: the sand stays exactly where it is and costs nothing but drawing.
            if (Frozen)
            {
                ReportHitch("fixed update");
                return;
            }

            // The bed sinks under what its colliders reported pushing on it last physics step.
            if (bed.Enabled) bed.Press(Time.fixedDeltaTime);

            // A step in progress is advanced a slice at a time, one slice per fixed update,
            // so its cost is spread over the frames it spans instead of landing on one.
            if (stepper != null && stepper.InProgress)
            {
                AdvanceSlice();
                ReportHitch("fixed update");
                return;
            }

            if (pendingFills.Count > 0) FillPending();

            // Slumping, every fixed update, for as long as there is a backlog and harder the
            // bigger it is. A region fills as a block with upright walls, and until they have
            // slumped a machine meeting one hits a cliff of sand -- which it digs through rather
            // than climbs. Mounds the brush makes were always slopes, and were driven onto
            // properly; regions now become slopes within a second or so too.
            if (bed.Enabled && bed.Unsettled > 0)
            {
                PhaseStart();
                bed.Relax(100, Mathf.Clamp(bed.Unsettled / 2000f, 0.3f, 10f));
                PhaseEnd(PhaseBed);
            }

            if (count <= 0 && !bed.Active)
            {
                ReportHitch("fixed update");
                return;
            }

            accumulator += Time.fixedDeltaTime;
            float period = 1f / Mathf.Max(StepsPerSecond, 1);
            if (accumulator < period)
            {
                ReportHitch("fixed update");
                return;
            }

            float dt = accumulator;
            accumulator = 0f;
            slices = Mathf.Max(1, Mathf.RoundToInt(period / Mathf.Max(Time.fixedDeltaTime, 1e-4f)));
            Step(dt);
            if (stepper != null && stepper.InProgress) AdvanceSlice();
            ReportHitch("fixed update");
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
            phaseMs[PhaseSolver] += (float)stepTimer.Elapsed.TotalMilliseconds;

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
                // One scan of the scene for both feeds: FindObjectsOfType is the expensive
                // part. Everything at the start of a run and now and then; between those, only
                // the rigidbodies, which is all that comes and goes.
                PhaseStart();
                if (fullRescanCountdown <= 0)
                {
                    shapes.Rescan();
                    fullRescanCountdown = FullRescanScans;
                }
                else shapes.RescanBodies();
                fullRescanCountdown--;
                if (bed.Enabled) bedFeed.CopyBodySources(shapes);
                // A field with no grains and no machine near its sand looks four times less often.
                rescanCountdown = (count == 0 && bedShapes.Count == 0) ? RescanSteps * 4 : RescanSteps;
                PhaseEnd(PhaseScan);
            }
            rescanCountdown--;

            // Water cannons wet sand; steam, flamethrowers and burning blocks dry it.
            PhaseStart();
            WaterAndFire();
            PhaseEnd(PhaseBed);

            // The solver's own grid is sized by the grains alone. The ground and the
            // bed's shapes have to cover the bed as well, which can be far larger.
            Bounds bounds = count > 0 ? Bounds(0.5f) : bed.Extent;
            Bounds wide = bounds;
            if (useBed && count > 0) wide.Encapsulate(bed.Extent);

            // Ground under every grain now, whatever it costs, or the grain falls through;
            // around the bed, a few milliseconds' worth a step. Each tile is cast once.
            PhaseStart();
            ground.EnsureUnder(grains, count);
            if (useBed) ground.Ensure(bed.Extent, GroundBudgetMs);
            PhaseEnd(PhaseGround);

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

                PhaseStart();
                shapes.Gather(bounds);
                PhaseEnd(PhaseGather);
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
        private int idleSteps;

        private void StepBed(Bounds wide)
        {
            // Nothing moving, nothing settling and no machine anywhere near the sand: nothing
            // to do but notice a machine arriving, which five times a second does.
            bool idle = count == 0 && bed.Unsettled == 0 && bedShapes.Count == 0;
            if (idle && (idleSteps++ % 5) != 0) return;

            PhaseStart();
            Bounds reach = wide;
            reach.Expand(1f);
            bedFeed.Gather(reach);
            bedShapes.Prepare(bedFeed);

            bed.Absorb(grains, count, bedShapes, BedRestSteps, BedMinLife,
                       BedShapeMargin, BedAbsorbPerStep);
            bed.Release(bedShapes, this, BedShapeMargin, BedReleaseTests, BedReleasePerStep);
            bedColliders.Layer = ground.GroundLayer;
            bedColliders.Update(bedShapes, ColliderBudgetMs);
            bed.Relax(BedRelaxColumns, BedRelaxMs);
            PhaseEnd(PhaseBed);
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
            bed.TrackNet = SandNet.IsHost;
            if (SandNet.IsClient) ClientUpdate();
            else SandNet.HostSend(this, bed, grains, count);

            // The bed is drawn whenever it holds sand, even with no grains moving.
            PhaseStart();
            if (bed.Enabled) bedRenderer.Draw(bed, ground, 0);
            PhaseEnd(PhaseDraw);

            if (count <= 0)
            {
                ReportHitch("frame");
                return;
            }

            // The GPU path keeps grains in a compute buffer and is drawn by a vertex
            // shader reading it; until that shader exists this path draws nothing
            // rather than stalling the pipeline to fetch 40000 grains every frame.
            if (solver.OnGpu) return;

            // Layer 0. A level's cameras draw it, unlike the build area's, where
            // anything new has to be put on the machine's own layer or it is present,
            // correctly placed and invisible (notes repo, 13-drawing-over-the-machine).
            PhaseStart();
            renderer.Draw(solver.Grains, count, materials, variantBase, 0);
            PhaseEnd(PhaseDraw);
            ReportHitch("frame");
        }

        // ------------------------------------------------------------------ hitches
        //
        // Where the time went, per fixed update and per frame. A call that took longer than
        // HitchMs is written to Player.log with its breakdown, at most once a second, and the
        // worst one is kept for the F9 report -- so a stall in somebody's level says which part
        // of the sand caused it rather than just that the game froze.

        private const int PhaseGround = 0, PhaseFill = 1, PhaseScan = 2, PhaseGather = 3,
                          PhaseSolver = 4, PhaseBed = 5, PhaseDraw = 6, PhaseCount = 7;
        private static readonly string[] PhaseNames =
            { "ground raycasts", "region fill", "collider scan", "collider gather", "solver", "bed", "drawing" };
        private const float HitchMs = 25f;

        private readonly float[] phaseMs = new float[PhaseCount];
        private readonly System.Diagnostics.Stopwatch phaseTimer = new System.Diagnostics.Stopwatch();
        private float lastHitchLog = -10f;
        private float worstMs;
        private string worstFrame = "";

        private void PhaseStart()
        {
            phaseTimer.Reset();
            phaseTimer.Start();
        }

        private void PhaseEnd(int phase)
        {
            phaseMs[phase] += (float)phaseTimer.Elapsed.TotalMilliseconds;
        }

        private void ReportHitch(string where)
        {
            float total = 0f;
            for (int i = 0; i < PhaseCount; i++) total += phaseMs[i];
            if (total > HitchMs)
            {
                string line = where + " " + total.ToString("F0") + " ms:";
                for (int i = 0; i < PhaseCount; i++)
                {
                    if (phaseMs[i] < 0.5f) continue;
                    line += " " + PhaseNames[i] + " " + phaseMs[i].ToString("F0");
                    if (i == PhaseGround) line += " (" + ground.Raycasts + " rays)";
                }
                line += "; " + count + " grains, " + bed.FilledColumns + " bed columns";
                if (total > worstMs) { worstMs = total; worstFrame = line; }
                if (Time.realtimeSinceStartup - lastHitchLog > 1f)
                {
                    lastHitchLog = Time.realtimeSinceStartup;
                    Debug.Log("[Sand] slow " + line);
                }
            }
            for (int i = 0; i < PhaseCount; i++) phaseMs[i] = 0f;
            ground.Raycasts = 0;
        }
    }
}
