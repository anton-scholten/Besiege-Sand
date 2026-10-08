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
    public class SandField : MonoBehaviour, IBedKickSink
    {
        /// <summary>Sand steps per second. Besiege's own fixed step is 100 Hz; this
        /// deliberately is not, because stepping a medium that often would spend the
        /// whole frame on it.</summary>
        public static int StepsPerSecond = 25;

        public static int Substeps = 2;
        public static int Iterations = 3;

        /// <summary>
        /// Size of the grain array. It is only a ceiling on the array: how many grains are
        /// actually admitted is decided by the governor from the measured cost of a step
        /// (see SandGovernor), because that depends on the player's machine, and sand that
        /// has come to rest is held by the bed and costs no grains at all.
        /// </summary>
        public static int CpuCapacity = 80000;

        /// <summary>
        /// Milliseconds one whole sand step may take before the field starts spending
        /// quality to stay inside it. A step is advanced in slices over the four fixed
        /// updates it spans, so this is about four times what a single frame pays. It was 20: at 25 steps a
        /// second that let the sand have half of every second, and with wheels throwing sand driving lagged.
        /// </summary>
        public static float StepBudgetMs = 10f;

        private static SandField instance;

        private CpuSolver solver;
        private SandMaterials.Params[] materials;
        private readonly ShapeFeed shapes = new ShapeFeed();
        private readonly WaterLink water = new WaterLink();
        private readonly GroundField ground = new GroundField();
        private readonly SandRenderer renderer = new SandRenderer();

        // Sand that has come to rest, kept as a height field instead of as grains.
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

        /// <summary>Rescans between full ones, which walk every object in the scene: about ten
        /// minutes' worth. The level's fixed colliders do not change during a run, a full scan
        /// is the start of every run, and the machines' blocks are read from the machines in
        /// between, so the full one is only there for loose bodies that turn up later.</summary>
        private const int FullRescanScans = 2400;
        private int fullRescanCountdown;

        private const int BedAbsorbPerStep = 3000;
        // Distance tests digging may make a step. At 12000 a machine's dozens of shapes near the sand took turns, a few a
        // step, and each wheel threw for a step and then waited for the rest to have theirs.
        private const int BedReleaseTests = 40000;

        /// <summary>Most grains the bed gives back in one step. A machine driving onto a deep
        /// pile used to have thousands handed to the solver at once, and the step after that
        /// cost several times the one before; at this rate a wheel still meets sand, and the
        /// governor sees the load coming. Less while the governor's ceiling is low: a twelfth
        /// of it a step (see <see cref="ReleasePerStep"/>).</summary>
        private const int BedReleasePerStep = 1500;

        /// <summary>What digging may hand the solver this step: a twelfth of what the governor admits, between
        /// 200 and <see cref="BedReleasePerStep"/>. Wheels throwing ordinary grains at a flat 600 a step filled the
        /// field faster than the solver could carry it, and driving on sand lagged; at a twentieth, now that they throw
        /// coarse ones, a machine's wheels threw for a second, ran out, and waited.</summary>
        private int ReleasePerStep { get { return Mathf.Clamp(Limit / 12, 200, BedReleasePerStep); } }

        /// <summary>Columns checked for slumping per step. Each costs a few dozen lookups, and a
        /// big brush stroke or a loaded sand file starts with many columns needing a look.</summary>
        private const int BedRelaxColumns = 300;

        /// <summary>Milliseconds a sand step may go on slumping past that, while there is
        /// slumping to do: a steep wall of sand should fall to its angle in a second or two.</summary>
        private const float BedRelaxMs = 3f;

        /// <summary>Milliseconds a fixed update may spend raycasting new ground for the bed's
        /// surroundings. A big bed's ground arrives over a few frames.</summary>
        private const float GroundBudgetMs = 3f;

        private Grain[] grains;
        private int count;
        private float[] forces = new float[6];
        private float accumulator;
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

        /// <summary>How many more grains will be admitted. This is the governor's
        /// ceiling, which sits below the array's size while the solver is struggling.</summary>
        public int Room { get { return Mathf.Max(Mathf.Min(governor.SoftCapacity, Limit) - count, 0); } }

        /// <summary>
        /// The most sand grains simulated at once, as the panel's "Sand limit" sets it: the governor admits no more,
        /// whatever the machine could afford, and digging and blasts none past it. The spray that is only drawn may
        /// hold as many again (<see cref="SandSpray"/>). Between <see cref="MinSandLimit"/> and the grain array.
        /// </summary>
        public static int SandLimit = 40000;
        public const int MinSandLimit = 1000, MaxSandLimit = 80000;

        /// <summary>Whether the spray that is only drawn is drawn (and made) at all: the panel's "Show cosmetic particles".</summary>
        public static bool ShowCosmetic = true;

        private int Limit { get { return Mathf.Clamp(SandLimit, MinSandLimit, Mathf.Max(capacity, MinSandLimit)); } }

        // ------------------------------------------------------------------ governor
        //
        // Times its own steps and spends quality to stay inside a budget; see
        // SandGovernor. Only the CPU path is governed.
        private readonly SandGovernor governor = new SandGovernor();
        private readonly System.Diagnostics.Stopwatch stepTimer = new System.Diagnostics.Stopwatch();


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
            if (row.radius < SandMaterials.MinRadius)
            {
                // Smaller grains are the same sand as many more of them: kept to dust's size,
                // with the mass that size has at the row's density.
                float scale = SandMaterials.MinRadius / row.radius;
                row.radius = SandMaterials.MinRadius;
                row.invMass /= scale * scale * scale;
            }
            materials[slot] = row;
            materialsDirty = true;
        }


        /// <summary>Re-seeds a variant's row from a medium, keeping the row.</summary>
        public void ReseatVariant(int slot, int baseMaterial)
        {
            if (slot < SandMaterials.FirstVariant || slot >= materials.Length) return;
            int baseIndex = Mathf.Clamp(baseMaterial, 0, SandMaterials.Count - 1);
            materials[slot] = materials[baseIndex];
            variantBase[slot] = baseIndex;
            materialsDirty = true;
        }

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
            lines.Add("grains " + count + " of " + capacity
                + ", simulating " + StatMaster.levelSimulating);
            lines.Add("bed " + (bed.Enabled ? "on" : "off") + ": " + bed.FilledColumns
                + " columns in " + bed.ChunkCount + " chunks, " + bed.Volume.ToString("F3") + " m^3, "
                + bedColliders.Count + " chunk colliders near machines");
            lines.Add("ground: " + ground.TileCount + " tiles of 4 m cast");
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
            lines.Add("last dig: " + bed.LastDig);
            lines.Add("digging: " + bedShapes.Count + " shapes on bodies in reach of the bed; room for grains " + Room
                + " (governor), " + digSink.Room + " (for digging)" + (bedError.Length > 0 ? "; the bed failed: " + bedError : ""));
            lines.Add("explosions: " + explosionLog);
            lines.Add("ground contact: " + BedContact.LastContact);
            lines.Add("last push: " + lastPushed + " bodies, " + lastForce.ToString("F1")
                + " N summed over substeps (a still pile of sand weighs half of that)");

            if (count > 0)
            {
                Grain[] g = solver.Grains;
                int touching = 0;
                SandShape[] sh = shapes.Shapes;
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
            capacity = CpuCapacity;
            solver = new CpuSolver();
            solver.Init(capacity, materials);
            // One array between the two: Spawn writes where the solver reads, so reaping a dead grain is
            // a single swap rather than a copy of the whole field.
            grains = solver.Grains;
            solver.Ground = ground;

            bed.Enabled = true;
            bed.SetGround(ground);
            bed.SetMaterials(materials);
            solver.Bed = bed;

            // Start low and let the governor measure what the machine can afford: forty
            // thousand grains admitted in the first second is a stall on a slow one.
            governor.BudgetMs = StepBudgetMs;
            governor.Reset(Mathf.Min(capacity, 6000));

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
            solver.Abort();
            count = 0;
            bed.Clear();
            bedAtStart = null;
            countAtStart = 0;
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
            // Thrown sand pushes what it hits from the moment it leaves the nozzle: sprayed sand
            // that waited out SettleSeconds first blasted a block at close range without moving it.
            if (SourcesPaused || SandNet.IsClient) return;   // in multiplayer the host sprays
            Add(position, velocity, material, colour, lifetime, SandFormat.SettleSeconds + 0.01f);
            // And a little drawn spray with it, as a wheel's throw has.
            if (ShowCosmetic)
            {
                int row = Mathf.Clamp(material, 0, materials.Length - 1);
                int fine = row >= SandMaterials.FirstVariant ? row : SandMaterials.MediumOf(row);
                spray.Emit(position, velocity, Varied(SandMaterials.PackColour(colour), row),
                           materials[Mathf.Clamp(fine, 0, materials.Length - 1)].radius, materials[row].airDrag, SprayPerSprayed);
            }
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

        public void Clear()
        {
            // A step under way was working on grains that no longer exist.
            solver.Abort();
            count = 0;
            spray.Clear();
            bed.Clear();
            SandNet.HostCleared();

            // A new run is a new load; judge it afresh rather than carry over the last one's.
            governor.NewRun();
        }

        // ------------------------------------------------------------------ between runs
        //
        // Besiege puts a machine back where it was built when a run stops, and by default the
        // sand goes back too: the sand as it was when the run started -- including anything
        // poured or brushed in while building -- is captured then and restored now. With
        // KeepBetweenRuns the sand stays as the run left it instead.

        /// <summary>Leave the sand as a run left it, rather than as it was before the run.</summary>
        public static bool KeepBetweenRuns = true;

        /// <summary>Sprayers add nothing while this is set; what is there stays.</summary>
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

        private void StartRun()
        {
            // The machine is a new one, perhaps on another layer than the one being built.
            checkedLayer = -1;
            if (solver.InProgress) solver.Abort();
            Reap();
            bedAtStart = bed.Enabled ? bed.Capture() : null;
            if (grainsAtStart == null || grainsAtStart.Length < count) grainsAtStart = new Grain[Mathf.Max(count, 16)];
            System.Array.Copy(grains, grainsAtStart, count);
            countAtStart = count;
        }

        private void EndRun()
        {
            if (KeepBetweenRuns)
            {
                bedAtStart = null;
                return;
            }

            solver.Abort();
            SandNet.HostCleared();
            if (bedAtStart != null) bed.Restore(bedAtStart); else bed.Clear();
            count = 0;
            if (grainsAtStart != null && countAtStart > 0 && countAtStart <= grains.Length)
            {
                System.Array.Copy(grainsAtStart, grains, countAtStart);
                count = countAtStart;
            }
            bedAtStart = null;
            governor.NewRun();
        }

        // ------------------------------------------------------------------ saved with a level
        //
        // In the level editor the sand is saved into the level file, as the level's own custom
        // data: the bed packed (BedCodec). Besiege keeps a mod's custom data and hands it back on load; a
        // game without this mod ignores it, and the level loads with no sand.

        private const string LevelKey = "SandBed";
        // "SNL2": seven media. "SNL1" was saved with six, so its coarse rows are numbered
        // differently and are renumbered as it loads.
        private const int LevelMagic = 0x534E4C32;

        private static byte[] levelSand;
        private static float lastRunToggle = -100f;
        private static bool needField;

        /// <summary>Called every frame by the HUD, which always exists: makes the field when a
        /// level brought sand and there is no field yet to put it in.</summary>
        public static void Tick()
        {
            if (!needField) return;
            needField = false;
            SandField f = Instance;
        }
        private static bool levelSandWaiting;
        private static float lastSandFileAt = -100f;

        /// <summary>Writes the sand into a level being saved.</summary>
        public static void SaveToLevel(Modding.Levels.Level level)
        {
            SandField f = instance;
            try
            {
                if (f == null || !f.bed.Enabled || f.bed.FilledColumns <= 0)
                {
                    if (level.CustomData.HasKey(LevelKey)) level.CustomData.Remove(LevelKey);
                    return;
                }
                byte[] data = f.LevelBytes();
                level.CustomData.Write(LevelKey, System.Convert.ToBase64String(data));
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[Sand] could not save the sand with the level: " + e.Message);
            }
        }

        /// <summary>Takes the sand out of a level that has loaded, or none if it has none.</summary>
        public static void LoadFromLevel(Modding.Levels.Level level)
        {
            // The level editor puts its level back when a run starts or stops, and says so as a
            // load. That is not the player opening a level: the sand is the run's business then
            // -- kept or put back by the run itself -- and taking the level's saved sand instead
            // threw away everything brushed or dug during a kept run.
            bool simulating = StatMaster.levelSimulating;
            bool toggling = simulating
                || (instance != null && instance.wasSimulating != simulating)
                || Time.realtimeSinceStartup - lastRunToggle < 2f;
            if (toggling) return;
            // A sand file has just been loaded, and putting the machine back after it can make the level editor say the
            // level loaded: taken at its word, the level's own saved sand replaced what the file had just put in.
            if (Time.realtimeSinceStartup - lastSandFileAt < 3f) return;
            levelSand = null;
            try
            {
                if (level != null && level.CustomData.HasKey(LevelKey))
                    levelSand = System.Convert.FromBase64String(level.CustomData.ReadString(LevelKey));
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[Sand] the level's sand could not be read: " + e.Message);
                levelSand = null;
            }
            // Put in on the next frame rather than now: the scene a level loads into may still be
            // clearing out the last one's sand.
            levelSandWaiting = true;
            // The field has to exist for its Update to put the sand in -- but it is not made here,
            // in the middle of the game's own loading: the next frame's hook does it.
            if (levelSand != null) needField = true;
        }

        /// <summary>
        /// The sand as the level should keep it: as it stands when building, and while a run is
        /// going the sand it will go back to -- unless that run is being kept.
        /// </summary>
        private byte[] LevelBytes()
        {
            bool running = StatMaster.levelSimulating && !KeepBetweenRuns && bedAtStart != null;
            byte[] beds;
            if (running)
            {
                SandBed.Snapshot snap = bedAtStart as SandBed.Snapshot;
                SandBed scratch = new SandBed();
                scratch.Enabled = true;
                scratch.SetGround(ground);
                scratch.SetMaterials(materials);
                scratch.Restore(snap);
                beds = scratch.SaveAll(SavedRows());
            }
            else beds = bed.SaveAll(SavedRows());

            BedCodec.Writer w = new BedCodec.Writer(beds.Length + 64);
            w.Int(LevelMagic);
            w.Unsigned((uint)beds.Length);
            w.Bytes(beds, beds.Length);
            // An empty list where older versions kept one that nothing reads now: the form stays the same.
            w.Unsigned(0u);
            return w.ToArray();
        }

        /// <summary>
        /// Rows as a level keeps them: a medium as itself, and a row of an emitter's or the
        /// panel's own as the medium it is based on. Those rows are made while the game runs and
        /// are not in the file, so their numbers would mean nothing to the next load.
        /// </summary>
        private int[] SavedRows()
        {
            int[] map = new int[variantBase.Length];
            for (int i = 0; i < map.Length; i++)
                map[i] = i < SandMaterials.FirstVariant ? i : Mathf.Clamp(variantBase[i], 0, SandMaterials.Count - 1);
            return map;
        }

        private void ApplyLevelSand()
        {
            levelSandWaiting = false;
            byte[] data = levelSand;
            levelSand = null;
            if (SandNet.IsClient) return;
            // Asked again a frame later: the event can come just before the run's start shows.
            if (StatMaster.levelSimulating || wasSimulating || Time.realtimeSinceStartup - lastRunToggle < 2f) return;
            Clear();
            if (data == null) return;
            ReadSand(data, "the level's");
        }

        /// <summary>Puts in sand written by <see cref="LevelBytes"/>, onto a cleared field.</summary>
        private void ReadSand(byte[] data, string whose)
        {
            BedCodec.Reader r = new BedCodec.Reader(data, 0);
            int magic = r.Int();
            if (magic != LevelMagic) { Debug.LogWarning("[Sand] " + whose + " sand is not in a form this version reads"); return; }
            byte[] beds = r.Bytes((int)r.Unsigned());
            if (!bed.LoadAll(beds)) Debug.LogWarning("[Sand] " + whose + " sand was cut short; what could be read is there");
            // What follows is a list older versions wrote, which nothing reads now.
        }

        /// <summary>The sand as a sand file keeps it (the same form as a level's), or null when there is none.</summary>
        public static byte[] SandFileBytes()
        {
            SandField f = instance;
            if (f == null || !f.bed.Enabled || f.bed.FilledColumns <= 0) return null;
            return f.LevelBytes();
        }

        /// <summary>The sand under a point is given a collider now (see <see cref="SandBedColliders.WantAt"/>).</summary>
        public void WantColliderAt(Vector3 p)
        {
            if (bed.Enabled && !SandNet.IsClient) bedColliders.WantAt(p);
        }

        /// <summary>Where the sand is, for a camera to look at: false when there is none.</summary>
        public static bool SandBounds(out Bounds where)
        {
            SandField f = instance;
            where = new Bounds();
            if (f == null || !f.bed.Enabled || f.bed.FilledColumns <= 0) return false;
            return f.bed.FilledBounds(out where);
        }

        /// <summary>Draws the sand for one camera, now (see <see cref="SandBedRenderer.DrawFor"/>).</summary>
        public static void DrawSandFor(Camera cam)
        {
            SandField f = instance;
            if (f != null && f.bed.Enabled) f.bedRenderer.DrawFor(cam, 0);
        }

        /// <summary>Replaces the sand with what a sand file holds: on the host, or alone; a client asks for nothing.</summary>
        public static void LoadSandFile(byte[] data)
        {
            if (SandNet.IsClient || data == null) return;
            SandField f = Instance;
            if (f == null) return;
            lastSandFileAt = Time.realtimeSinceStartup;
            levelSandWaiting = false;
            levelSand = null;
            f.Clear();
            f.ReadSand(data, "the file's");
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

        // ------------------------------------------------------------------ melting
        //
        // Heat melts snow: a burning block, a flamethrower's flame and boiling steam take it off
        // the bed and out of the air, fastest close by, so a pile melts from the side nearest
        // the fire. Riding on the drying: the same sources, found the same way, at the same
        // moments -- and nothing at all while none is lit.

        /// <summary>Metres of snow a second melted right at a burning block, and how far its heat
        /// reaches.</summary>
        private const float MeltRate = 0.25f;
        private const float FireMeltReach = 3f;
        /// <summary>Under a flamethrower's flame or a steam jet, where it meets the snow.</summary>
        private const float StreamMeltRate = 0.6f;
        private const float StreamMeltReach = 1.5f;

        private bool[] meltable = new bool[0];

        private void MapMeltable()
        {
            if (meltable.Length != variantBase.Length) meltable = new bool[variantBase.Length];
            for (int i = 0; i < meltable.Length; i++) meltable[i] = variantBase[i] == SandMaterials.Snow;
        }

        /// <summary>Snow near a heat source melts: in the bed, and loose grains, which go one by
        /// one, the nearest likeliest.</summary>
        private void Melt(Vector3 at, float reach, float rate, float dt)
        {
            if (bed.Enabled) bed.Melt(at, reach, rate * dt, meltable);
            float r2 = reach * reach;
            for (int k = 0; k < count; k++)
            {
                int row = (int)(grains[k].material & SandFormat.MaterialMask);
                if (row >= meltable.Length || !meltable[row]) continue;
                float d2 = (grains[k].pos - at).sqrMagnitude;
                if (d2 >= r2) continue;
                float w = 1f - Mathf.Sqrt(d2) / reach;
                // A grain is a few centimetres of snow: it goes with the chance the bed beside it
                // would lose that much this step.
                float chance = rate * dt * w * w / Mathf.Max(materials[row].radius * 2f, 0.01f);
                varietySeed ^= varietySeed << 13; varietySeed ^= varietySeed >> 17; varietySeed ^= varietySeed << 5;
                if ((varietySeed & 0xFFFF) / 65535f < chance) grains[k].material |= SandFormat.FlagDead;
            }
        }

        /// <summary>A stream's melting: where its axis meets the sand, and the grains along it.</summary>
        private void MeltStream(Vector3 origin, Vector3 dir, float range, float dt)
        {
            for (float t = 0.5f; t < range; t += 0.25f)
            {
                Vector3 p = origin + dir * t;
                float h = SurfaceAt(p.x, p.z);
                if (h > -9000f && p.y <= h)
                {
                    Melt(new Vector3(p.x, h, p.z), StreamMeltReach + t * StreamSpread, StreamMeltRate, dt);
                    return;
                }
            }
        }

        private void WaterAndFire(float dt)
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
            MapMeltable();
            for (int i = 0; i < cannons.Count; i++)
            {
                WaterCannonController c = cannons[i];
                if (c == null || !c.isActive) continue;
                // Steam, when it boils, dries rather than wets.
                ParticleSystem[] ps = c.boiling ? c.steamParticles : c.waterParticles;
                Transform from = ps != null && ps.Length > 0 && ps[0] != null ? ps[0].transform : c.transform;
                Stream(from.position, from.forward, CannonRange, c.boiling ? dryOf : wetOf, c.boiling ? 1f / 0.7f : 0.7f);
                if (c.boiling) MeltStream(from.position, from.forward, CannonRange, dt);
            }
            for (int i = 0; i < flamers.Count; i++)
            {
                FlamethrowerController f = flamers[i];
                if (f == null || !f.isFlaming) continue;
                Transform from = f.fireParticles != null ? f.fireParticles.transform : f.transform;
                Stream(from.position, from.forward, FlameRange, dryOf, 1f / 0.7f);
                MeltStream(from.position, from.forward, FlameRange, dt);
            }
            for (int i = 0; i < fires.Count; i++)
            {
                FireTag f = fires[i];
                if (f == null || !f.burning) continue;
                Vector3 p = f.transform.position;
                Convert(p, FireReach, dryOf);
                if (bed.Enabled) bed.ConvertSurface(p, FireReach * 0.8f, dryOf, 1f / 0.7f);
                Melt(p, FireMeltReach, MeltRate, dt);
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


        /// <summary>
        /// The widest the brush pours, in metres from its middle. A size can be typed far past
        /// this, but every metre of brush is a square metre of bed held in memory and swept each
        /// frame, and a kilometre-wide circle is gigabytes; the brush stops growing here.
        /// </summary>
        public const float MaxBrushRadius = 100f;

        // Brush shapes, as the panel lists them.
        public const int BrushCircle = BrushShape.Circle, BrushFlat = BrushShape.Flat, BrushSquare = BrushShape.Square,
                         BrushRandom = BrushShape.Random, BrushRing = BrushShape.Ring;
        public static readonly string[] BrushNames = { "Circle", "Flat circle", "Square", "Random", "Ring" };

        /// <summary>
        /// What one frame of brushing may cost, in milliseconds. A brush too big to be swept in that
        /// is swept in bands of rows, one band a frame, each band given as many frames' worth as
        /// there are bands: the same layer, evenly, without a frame sweeping millions of columns.
        /// (It used to pour on a random sample instead, each sampled column standing for many --
        /// which left large brushes as a bed of spikes. And it used to cap a frame at 60000
        /// columns, which on a big brush was still fifty to a hundred milliseconds.)
        /// </summary>
        public static float BrushBudgetMs = 3f;

        private readonly BrushSweep sweep = new BrushSweep();
        private float[] brushX = new float[4096], brushZ = new float[4096], brushVolume = new float[4096];
        private int brushCount;

        /// <summary>
        /// How far in from a flat brush's outline its edge should reach: the slope the medium rests
        /// at, over the depth of sand already under the brush's centre. A layer laid with that edge
        /// stands at its angle of repose all round and has nothing to slump; one with a few columns'
        /// edge was a cliff, and the bed worked on it for as long as the brush was held.
        /// </summary>
        private float BrushTaper(Vector3 centre, int style, int medium)
        {
            if (style != BrushFlat && style != BrushSquare) return 0f;
            float depth = bed.DepthAt(centre.x, centre.z);
            float repose = bed.Repose(medium) * 0.85f;
            return depth > 0f && repose > 0f ? depth / repose : 0f;
        }

        private void ForBrush(Vector3 centre, float radius, int style, int seed, float taper, float layer,
                              System.Action<float, float, float> each)
        {
            sweep.Run(centre.x, centre.z, radius, style, seed, BrushBudgetMs, taper, layer, each);
        }

        /// <summary>Whether the ground under a brush is known, casting some more of it if not.
        /// Sand poured where it is not yet known would be lost.</summary>
        public bool BrushReady(Vector3 centre, float radius)
        {
            radius = Mathf.Clamp(radius, 0.05f, MaxBrushRadius);
            return ground.Ensure(new Bounds(centre, new Vector3(radius * 2f + 1f, 1f, radius * 2f + 1f)), 4f);
        }

        /// <summary>
        /// Raises the sand under a brush by <paramref name="rise"/> metres where the brush is at
        /// full strength and less where it tapers -- the same depth for any size of brush, so a
        /// big one lays the layer a small one does. Slumping then shapes it, so held in one place
        /// it grows into a heap at the medium's own angle of repose.
        /// </summary>
        public void Paint(Vector3 centre, float radius, float rise, int medium, int style, int seed, uint colour)
        {
            radius = Mathf.Clamp(radius, 0.05f, MaxBrushRadius);
            if (SandNet.IsClient) { SandNet.RequestBrush(centre, radius, rise, medium, false, style, seed, colour); return; }
            if (!bed.Enabled || rise <= 0f) return;
            if (!BrushReady(centre, radius)) return;
            // A medium, or a row of its own -- the panel's advanced settings.
            medium = Mathf.Clamp(medium, 0, materials.Length - 1);
            colour |= 0xFF000000u;
            float area = SandBed.Cell * SandBed.Cell;
            // A pile is settled as one medium: a different one starts a new stroke.
            if (bed.HasDeferred && bed.DeferredMaterial != medium) StrokeTick(true);
            if (bed.HasDeferred)
            {
                // A brush that has moved well away from the pile it was growing leaves that one to settle now,
                // not in a box that takes in both.
                int a, b, c, d;
                bed.DeferredBounds(out a, out b, out c, out d);
                float mx = (a + c + 1) * 0.5f * SandBed.Cell, mz = (b + d + 1) * 0.5f * SandBed.Cell;
                float reach = Mathf.Max(c - a + 1, d - b + 1) * 0.5f * SandBed.Cell + radius * 2f + 4f;
                if (Mathf.Abs(centre.x - mx) > reach || Mathf.Abs(centre.z - mz) > reach) StrokeTick(true);
            }
            // Collected, then poured as a batch: most of the columns of a brush need nothing but adding to.
            System.Diagnostics.Stopwatch frame = System.Diagnostics.Stopwatch.StartNew();
            brushCount = 0;
            ForBrush(centre, radius, style, seed, BrushTaper(centre, style, medium), rise, delegate (float x, float z, float w)
            {
                if (brushCount == brushX.Length)
                {
                    System.Array.Resize(ref brushX, brushCount * 2); System.Array.Resize(ref brushZ, brushCount * 2);
                    System.Array.Resize(ref brushVolume, brushCount * 2);
                }
                brushX[brushCount] = x; brushZ[brushCount] = z; brushVolume[brushCount] = rise * w * area;
                brushCount++;
            });
            bed.PourBatch(brushX, brushZ, brushVolume, brushCount, medium, colour, DeferGrowth);
            sweep.Adapt(frame.Elapsed.TotalMilliseconds, BrushBudgetMs);

            if (Time.unscaledTime - lastPaint > StrokeIdle) settleWait = 0;     // a new stroke settles from its first frame
            lastPaint = Time.unscaledTime;
            if (bed.HasDeferred && deferSince < 0f) deferSince = lastPaint;
            // The pile is let down to its angle of repose as it grows, so the brush shows the sand where it will
            // lie, not a tower that falls into shape when the button is let go: every frame while that is cheap,
            // and every few frames, by what the last one cost, once the pile is big. A pile whose settling would
            // take several frames' budget at once (a brush tens of metres across) is left for the end of the
            // stroke, as before.
            if (bed.HasDeferred && SettleWhilePainting && --settleWait <= 0)
            {
                settleWait = 10;
                int i0, j0, i1, j1;
                bed.DeferredBounds(out i0, out j0, out i1, out j1);
                float most = Mathf.Max(BrushSettleMs, 0.1f) * 4f;
                if ((float)(i1 - i0 + 1) * (j1 - j0 + 1) * settleUsPerColumn * 0.001f < most)
                {
                    int margin = bed.SettleMargin();
                    float columns = (float)(i1 - i0 + 1 + 2 * margin) * (j1 - j0 + 1 + 2 * margin);
                    if (columns * settleUsPerColumn * 0.001f < most)
                    {
                        System.Diagnostics.Stopwatch settle = System.Diagnostics.Stopwatch.StartNew();
                        if (SettleStroke(margin))
                        {
                            float ms = (float)settle.Elapsed.TotalMilliseconds;
                            settleUsPerColumn = Mathf.Lerp(settleUsPerColumn, ms * 1000f / columns, 0.3f);
                            settleWait = Mathf.Clamp(Mathf.CeilToInt(ms / Mathf.Max(BrushSettleMs, 0.1f)), 1, 30);
                        }
                        else settleWait = 1;
                    }
                }
            }
            if (sweep.LastStride > 1)
            {
                // A brush too big to lay every column every frame lays every other one, and the layer is
                // a speckle until it is evened out, when the brush is let go.
                float cell = SandBed.Cell;
                int i0 = Mathf.FloorToInt((centre.x - radius) / cell), i1 = Mathf.FloorToInt((centre.x + radius) / cell);
                int j0 = Mathf.FloorToInt((centre.z - radius) / cell), j1 = Mathf.FloorToInt((centre.z + radius) / cell);
                if (!smoothPending) { smI0 = i0; smJ0 = j0; smI1 = i1; smJ1 = j1; smoothPending = true; }
                else { smI0 = Mathf.Min(smI0, i0); smJ0 = Mathf.Min(smJ0, j0); smI1 = Mathf.Max(smI1, i1); smJ1 = Mathf.Max(smJ1, j1); }
            }
        }

        /// <summary>
        /// Whether a pile a brush is growing is left where it lands and let down to its angle of repose once, when
        /// the brush is let go, instead of being rolled to rest a little at a time every frame -- which took a
        /// frame longer and longer the bigger the pile got.
        /// </summary>
        public static bool DeferGrowth = true;

        /// <summary>Seconds without painting after which a pile that is growing is settled.</summary>
        private const float StrokeIdle = 0.3f;
        /// <summary>The longest a pile is left growing in place, seconds: it stands as a tower until then.</summary>
        private const float StrokeLongest = 6f;

        /// <summary>Whether a pile a brush is growing is settled while the brush is held, not only when it is let go.</summary>
        public static bool SettleWhilePainting = true;

        /// <summary>Milliseconds a frame of painting spends on average settling the pile it grows: a settle that
        /// costs more waits that many frames' worth before the next.</summary>
        public static float BrushSettleMs = 4f;

        private int settleWait;
        private float settleUsPerColumn = 0.3f;     // what settling has cost, measured as it goes
        private float lastPaint = -1000f, deferSince = -1f;
        private bool smoothPending;
        private int smI0, smJ0, smI1, smJ1;

        /// <summary>
        /// Settles what a brush has left to settle, once it has stopped (or has gone on a long while): lets
        /// the pile down to its angle of repose, and evens out what a big brush laid in a speckle.
        /// </summary>
        private void StrokeTick() { StrokeTick(false); }

        private void StrokeTick(bool force)
        {
            if (!bed.Enabled || SandNet.IsClient) return;
            if (!bed.HasDeferred && !smoothPending) { deferSince = -1f; return; }
            float now = Time.unscaledTime;
            if (!force && now - lastPaint < StrokeIdle && (deferSince < 0f || now - deferSince < StrokeLongest)) return;
            if (bed.HasDeferred && !SettleStroke(bed.SettleMargin())) return;
            if (smoothPending)
            {
                bed.SmoothBox(smI0, smJ0, smI1, smJ1);
                bed.SmoothBox(smI0, smJ0, smI1, smJ1);
                smoothPending = false;
            }
        }

        /// <summary>Lets what a brush has left to settle down to its angle of repose. False, settling nothing, while
        /// the ground it may spread over is still being cast.</summary>
        private bool SettleStroke(int margin)
        {
            int i0, j0, i1, j1;
            bed.DeferredBounds(out i0, out j0, out i1, out j1);
            float cell = SandBed.Cell;
            Bounds area = new Bounds(new Vector3((i0 + i1 + 1) * 0.5f * cell, 0f, (j0 + j1 + 1) * 0.5f * cell),
                                     new Vector3((i1 - i0 + 1 + 2 * margin) * cell, 1f, (j1 - j0 + 1 + 2 * margin) * cell));
            // Ground it can spread over has to be known first, and is cast a few milliseconds a frame.
            if (!ground.Ensure(area, GroundBudgetMs)) return false;
            bed.SettleDeferred(margin);
            deferSince = -1f;
            return true;
        }

        /// <summary>
        /// Takes sand away under a brush, up to <paramref name="depth"/> where it is at full
        /// strength, and any loose grains standing in it.
        /// </summary>
        public void Erase(Vector3 centre, float radius, float depth, int style, int seed)
        {
            radius = Mathf.Clamp(radius, 0.05f, MaxBrushRadius);
            if (SandNet.IsClient) { SandNet.RequestBrush(centre, radius, depth, 0, true, style, seed, 0u); return; }
            if (depth <= 0f) return;
            if (bed.Enabled)
                ForBrush(centre, radius, style, seed, 0f, depth, delegate (float x, float z, float w)
                {
                    bed.RemoveVolume(x, z, depth * w);
                });
            for (int k = 0; k < count; k++)
            {
                Vector3 p = grains[k].pos;
                if (BrushShape.Weight(style, p.x - centre.x, p.z - centre.z, radius, seed, 0f) > 0.05f)
                    grains[k].material |= SandFormat.FlagDead;
            }
        }

        /// <summary>
        /// Puts back a grain the bed is giving up. It is born old enough to push a
        /// block at once -- a wheel that has just sunk into a pile should meet sand, not
        /// a settling period -- and with a lifetime the bed will not mind.
        /// </summary>
        /// <summary>A grain given back with a speed of its own (see <see cref="IBedKickSink"/>): thrown by a wheel.</summary>
        public void Release(Vector3 position, int material, uint colour, Vector3 velocity)
        {
            if (count >= grains.Length) return;
            // Thrown: a coarse grain the solver carries, and a spray of fine ones that are only drawn (SandSpray).
            {
                int row = Mathf.Clamp(material, 0, materials.Length - 1);
                int fine = SandMaterials.MediumOf(row) == row || row >= SandMaterials.FirstVariant ? row : SandMaterials.MediumOf(row);
                float fineRadius = materials[Mathf.Clamp(fine, 0, materials.Length - 1)].radius;
                if (ShowCosmetic) spray.Emit(position, velocity, Varied(colour, row), fineRadius, materials[row].airDrag, SprayPerThrown);
            }
            Grain g = new Grain();
            g.pos = position;
            g.material = (uint)Mathf.Clamp(material, 0, materials.Length - 1);
            g.colour = Varied(colour, (int)g.material);
            g.vel = velocity;
            g.prevPos = position - velocity * Time.fixedDeltaTime;
            g.packing = 0f;
            g.wetTimer = 0f;
            g.life = MediaSettings.DefaultLifetime;
            g.age = SandFormat.SettleSeconds + 0.1f;
            grains[count] = g;
            count++;
        }

        public void Release(Vector3 position, int material, uint colour)
        {
            if (count >= grains.Length) return;

            Grain g = new Grain();
            g.pos = position;
            g.prevPos = position;
            g.vel = Vector3.zero;
            g.material = (uint)Mathf.Clamp(material, 0, materials.Length - 1);
            g.colour = Varied(colour, (int)g.material);
            // A column given back is a stack of grains in one column's footprint, and grains that
            // cannot roll stood as a stack: dug mud left pillars in the hole for seconds. Each
            // grain starts with a little sideways speed, a different way for each, and the higher
            // it is the more -- a wall falling in -- so the stack comes down as a heap. (Cohesion
            // also waits a second before taking hold; see CpuSolver.Knit.)
            {
                varietySeed ^= varietySeed << 13; varietySeed ^= varietySeed >> 17; varietySeed ^= varietySeed << 5;
                float a = (varietySeed & 0xFFFF) / 65535f * Mathf.PI * 2f;
                float floor = ground.Height(position.x, position.z);
                float high = floor > -9000f ? Mathf.Clamp(position.y - floor, 0f, 2f) : 0f;
                float sp = (0.3f + ((varietySeed >> 16) & 0xFF) / 255f * 0.4f) * (1f + high * 3f);
                g.vel = new Vector3(Mathf.Cos(a) * sp, 0f, Mathf.Sin(a) * sp);
                g.prevPos = position - g.vel * Time.fixedDeltaTime;
            }
            g.packing = 0f;
            g.wetTimer = 0f;
            g.life = MediaSettings.DefaultLifetime;
            g.age = SandFormat.SettleSeconds + 0.1f;

            grains[count] = g;
            count++;
        }

        // ------------------------------------------------------------------ stepping

        // The spray that is only drawn, and how many of its grains go with each grain thrown.
        private readonly SandSpray spray = new SandSpray();
        private readonly SandDust dust = new SandDust();
        private const int SprayPerThrown = 3, SprayPerSprayed = 2;

        // When the grains were last shown where they are, and how long apart the sand steps come, for drawing between.
        private float lastShownAt, shownInterval = 0.04f;

        // ------------------------------------------------------------------ explosions
        //
        // A bomb blows a bowl out of the sand and throws what it was: the grains it makes go outward and
        // up from the blast faster the nearer they were, and the grains already in the air are knocked the
        // same way. What it leaves round the hole is steeper than it stands and slumps in.
        //
        // The game has no explosion event for a mod to listen to, but every kind of explosive has the same
        // few public fields -- radius, power, upPower and hasExploded -- so they are looked at, ten times a
        // second, and the ones that have gone off are noted once each.

        /// <summary>How hard explosions throw the sand, as a multiple.</summary>
        public static float ExplosionStrength = 1f;
        /// <summary>The share of an explosion's radius that is blown out of the ground as a crater.</summary>
        public static float CraterShare = 0.55f;

        private readonly System.Collections.Generic.HashSet<int> exploded = new System.Collections.Generic.HashSet<int>();
        private readonly System.Collections.Generic.HashSet<int> seenNow = new System.Collections.Generic.HashSet<int>();
        private float nextExplosionScan;
        private int explosionBlocksScan;

        private string explosionLog = "none seen yet";
        private int explosionsSeen, scannedBlocks, scannedBombs;
        private readonly System.Collections.Generic.List<Vector4> recentBlasts = new System.Collections.Generic.List<Vector4>();
        private readonly System.Collections.Generic.HashSet<int> effectsSeen = new System.Collections.Generic.HashSet<int>();

        private void ExplosionScan()
        {
            if (!StatMaster.levelSimulating)
            {
                if (exploded.Count > 0) exploded.Clear();
                if (effectsSeen.Count > 0) effectsSeen.Clear();
                recentBlasts.Clear();
                return;
            }
            if (!bed.Active && count <= 0) return;
            float now = Time.time;
            if (now < nextExplosionScan) return;
            nextExplosionScan = now + 0.05f;
            seenNow.Clear();
            scannedBlocks = scannedBombs = 0;
            recentBlasts.RemoveAll(delegate (Vector4 r) { return now - r.w > 3f; });

            Machine machine = Machine.Active();
            if (machine != null && machine.isSimulating && machine.SimulationBlocks != null)
            {
                List<BlockBehaviour> blocks = machine.SimulationBlocks;
                scannedBlocks = blocks.Count;
                for (int i = 0; i < blocks.Count; i++)
                {
                    BlockBehaviour b = blocks[i];
                    if (b == null) continue;
                    ExplodeOnCollideBlock bomb = b as ExplodeOnCollideBlock;
                    if (bomb != null) { scannedBombs++; Noticed("bomb", bomb.GetInstanceID(), bomb.hasExploded, bomb.transform.position, bomb.radius, bomb.power, bomb.upPower); continue; }
                    ControllableBomb remote = b as ControllableBomb;
                    if (remote != null) { scannedBombs++; Noticed("remote bomb", remote.GetInstanceID(), remote.hasExploded, remote.transform.position, remote.radius, remote.power, remote.upPower); }
                }
            }

            // Everything else that goes off -- level objects, cannonballs -- is in the scene, not in the
            // machine; found a few times a second. So are the effects every explosion starts: one that no
            // source above explained is still an explosion, of a kind this does not know the numbers of.
            explosionBlocksScan++;
            if ((explosionBlocksScan & 3) == 0)
            {
                foreach (ExplodeOnCollide e in Object.FindObjectsOfType<ExplodeOnCollide>()) Noticed("level explosive", e.GetInstanceID(), e.hasExploded, e.transform.position, e.radius, e.power, e.upPower);
                foreach (ExplodeOnCollideSmall e in Object.FindObjectsOfType<ExplodeOnCollideSmall>()) Noticed("small explosive", e.GetInstanceID(), e.hasExploded, e.transform.position, e.radius, e.power, e.upPower);
                foreach (SmallExplosion e in Object.FindObjectsOfType<SmallExplosion>()) Noticed("small bomb", e.GetInstanceID(), e.hasExploded, e.transform.position, e.radius, e.power, e.upPower);
                foreach (CannonBallExplosion e in Object.FindObjectsOfType<CannonBallExplosion>()) Noticed("cannonball", e.GetInstanceID(), e.hasExploded, e.transform.position, e.radius, e.power, e.upPower);
                foreach (ExplosionEffect fx in Object.FindObjectsOfType<ExplosionEffect>()) NoticedEffect(fx.GetInstanceID(), fx.transform.position, fx.dustCraterQuad);
                foreach (ExplosionEffectAdv fx in Object.FindObjectsOfType<ExplosionEffectAdv>()) NoticedEffect(fx.GetInstanceID(), fx.transform.position, null);
            }
            else
            {
                // Between those scans what was seen is carried over, or it would look new again.
                foreach (int id in exploded) seenNow.Add(id);
                foreach (int id in effectsSeen) seenNow.Add(id);
            }
            exploded.RemoveWhere(delegate (int id) { return !seenNow.Contains(id); });
            effectsSeen.RemoveWhere(delegate (int id) { return !seenNow.Contains(id); });

            if (decals.Count > 0) UpdateDecals();
        }

        private void Noticed(string what, int id, bool hasExploded, Vector3 where, float radius, float power, float upPower)
        {
            seenNow.Add(id);
            if (!hasExploded || exploded.Contains(id)) return;
            exploded.Add(id);
            if (SandNet.IsClient) return;
            recentBlasts.Add(new Vector4(where.x, where.y, where.z, Time.time));
            Explode(what, where, radius, power, upPower);
        }

        /// <summary>An explosion's effect has appeared. If a source already accounted for it, nothing; if not, the sand is
        /// thrown about by the commonest bomb's numbers (radius 5, power 20).</summary>
        private void NoticedEffect(int id, Vector3 where, Transform craterQuad)
        {
            seenNow.Add(id);
            if (craterQuad != null) WatchDecal(craterQuad);
            if (!effectsSeen.Add(id)) return;
            if (SandNet.IsClient) return;
            for (int i = 0; i < recentBlasts.Count; i++)
            {
                Vector4 r = recentBlasts[i];
                if ((new Vector3(r.x, r.y, r.z) - where).sqrMagnitude < 16f) return;
            }
            recentBlasts.Add(new Vector4(where.x, where.y, where.z, Time.time));
            Explode("explosion effect", where, 5f, 20f, 5f);
        }

        // ---- explosion decals. The scorch and dust mark an explosion lays on the ground is a flat quad just
        // above it, and under sand it is hidden by the sand's depth -- up close. From far off the depth buffer
        // cannot tell a quad a few centimetres under the sand's surface from the sand, and the mark shows
        // through. So a mark is hidden by the mod where there is sand over it, and shown where there is not
        // (the crater a blast leaves, bare ground, or sand the mark was laid on top of).
        private readonly System.Collections.Generic.Dictionary<int, Renderer[]> decals = new System.Collections.Generic.Dictionary<int, Renderer[]>();
        private readonly System.Collections.Generic.Dictionary<int, Transform> decalQuads = new System.Collections.Generic.Dictionary<int, Transform>();
        private readonly System.Collections.Generic.List<int> goneDecals = new System.Collections.Generic.List<int>();

        private void WatchDecal(Transform quad)
        {
            int id = quad.GetInstanceID();
            if (decals.ContainsKey(id)) return;
            decals[id] = quad.GetComponentsInChildren<Renderer>(true);
            decalQuads[id] = quad;
        }

        private void UpdateDecals()
        {
            goneDecals.Clear();
            foreach (KeyValuePair<int, Transform> kv in decalQuads)
            {
                Transform quad = kv.Value;
                if (quad == null) { goneDecals.Add(kv.Key); continue; }
                Vector3 p = quad.position;
                float top;
                bool covered = bed.Active && bed.TryTop(p.x, p.z, out top) && p.y < top - 0.04f;
                Renderer[] rs = decals[kv.Key];
                for (int i = 0; i < rs.Length; i++)
                    if (rs[i] != null && rs[i].enabled == covered) rs[i].enabled = !covered;
            }
            for (int i = 0; i < goneDecals.Count; i++) { decals.Remove(goneDecals[i]); decalQuads.Remove(goneDecals[i]); }
        }

        /// <summary>
        /// Throws the sand about by an explosion at <paramref name="centre"/>, of the game's own radius and power.
        /// </summary>
        public void Explode(string what, Vector3 centre, float radius, float power, float upPower)
        {
            explosionsSeen++;
            explosionLog = explosionsSeen + " seen; the last a " + what + " at " + centre.ToString("F1") + ", radius " + radius.ToString("F1") + ", power " + power.ToString("F1")
                           + " (" + scannedBombs + " bombs among " + scannedBlocks + " blocks watched)";
            if (radius <= 0.1f || power <= 0f || !isActiveAndEnabled) return;
            radius = Mathf.Min(radius, 60f);
            // How fast the nearest sand goes. By how far it should fly: under Besiege's gravity (32.8) a grain
            // thrown at 6 m/s rises half a metre, which is a puff, so the speed is the one that lifts it
            // one and a half blast radii -- 22 m/s for a bomb's five metres, about thirteen metres of range at
            // forty-five degrees -- and a stronger bomb throws it harder, by the square root of how much more
            // than a bomb's usual 20 its power is.
            float pull = Mathf.Max(Physics.gravity.magnitude, 1f);
            float strength = Mathf.Sqrt(Mathf.Clamp(power / 20f, 0.4f, 9f));
            float fastest = Mathf.Min(Mathf.Sqrt(2f * pull * radius * 1.5f) * strength * ExplosionStrength, 70f);
            // Up as much as out, and more for a bomb whose upward push is large against its power.
            float lift = 0.9f + Mathf.Clamp01(upPower / Mathf.Max(power, 1f)) * 2f;

            if (bed.Enabled)
            {
                BlastSink sink = new BlastSink(this, centre, radius, fastest, lift);
                // A bowl as wide as a share of the blast and as deep as a third of its width, never more than two metres.
                float crater = radius * CraterShare;
                bed.Blast(centre, crater, Mathf.Min(crater * 0.35f, 2f), sink, 1500);
            }

            // Grains in the air or lying loose are knocked outward and up by the blast.
            {
                Grain[] g = solver.Grains;
                float dt = Time.fixedDeltaTime;
                for (int i = 0; i < count; i++)
                {
                    Vector3 away = g[i].pos - centre;
                    float d = away.magnitude;
                    if (d >= radius) continue;
                    float fall = 1f - d / radius;
                    Vector3 dir = d > 0.01f ? away / d : Vector3.up;
                    dir = (dir + Vector3.up * lift * 0.5f).normalized;
                    Vector3 v = g[i].vel + dir * (fastest * fall * fall * BlastBoost((int)(g[i].material & SandFormat.MaterialMask), fastest));
                    g[i].vel = v;
                    g[i].prevPos = g[i].pos - v * dt;
                }
            }
        }

        /// <summary>
        /// How much harder a blast throws a medium the air holds back. Dust's drag stops a grain within a sixth
        /// of a second, so at the speed that sends sand fifteen metres it went three and hung there: a bomb in
        /// dust threw almost none of it. A medium is thrown faster by its drag against what gravity does in the
        /// time the blast's speed takes to fall off: dust about twice as hard -- a plume some metres up and out,
        /// that then drifts down -- and sand or mud a few per cent.
        /// </summary>
        public float BlastBoost(int row, float fastest)
        {
            float drag = materials[Mathf.Clamp(row, 0, materials.Length - 1)].airDrag;
            float pull = Mathf.Max(Physics.gravity.magnitude, 1f);
            return Mathf.Min(1f + drag * fastest / (4f * pull), 80f / Mathf.Max(fastest, 1f));
        }

        /// <summary>Gives what a blast digs out its speed: away from the blast, and up, and slower the farther it was.</summary>
        private class BlastSink : IBedSink
        {
            private readonly SandField field;
            private readonly Vector3 centre;
            private readonly float radius, fastest, lift;
            private uint seed = 2463534242u;

            public BlastSink(SandField f, Vector3 c, float r, float fast, float up) { field = f; centre = c; radius = r; fastest = fast; lift = up; }

            public int Room { get { return Mathf.Max(Mathf.Min(field.grains.Length, field.Limit) - field.count, 0); } }

            public void Release(Vector3 position, int material, uint colour)
            {
                Vector3 away = position - centre;
                float d = away.magnitude;
                Vector3 dir = d > 0.01f ? away / d : Vector3.up;
                dir = (dir + Vector3.up * lift).normalized;
                seed ^= seed << 13; seed ^= seed >> 17; seed ^= seed << 5;
                float spread = 0.7f + (seed & 0xFFFF) / 65535f * 0.6f;
                float fall = Mathf.Clamp01(1f - d / (radius * 1.8f));
                field.Release(position, material, colour, dir * (fastest * fall * spread * field.BlastBoost(material, fastest)));
            }
        }

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
                solver.Abort();
                count = 0;
                bed.Clear();
                    wasClient = client;
            }
            if (client)
            {
                ReportHitch("fixed update");
                return;
            }

            GivePending();
            ExplosionScan();

            bool simulating = StatMaster.levelSimulating;
            if (simulating != wasSimulating) lastRunToggle = Time.realtimeSinceStartup;
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
            if (solver.InProgress)
            {
                AdvanceSlice();
                ReportHitch("fixed update");
                return;
            }

            // Slumping, every fixed update, for as long as there is a backlog and harder the
            // bigger it is: a wall of sand left standing is a cliff a machine digs through
            // rather than climbs.
            if (bed.Enabled && bed.Unsettled > 0)
            {
                PhaseStart();
                bed.Relax(100, Mathf.Clamp(bed.Unsettled / 2000f, 0.3f, 2f));
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
            if (solver.InProgress) AdvanceSlice();
            ReportHitch("fixed update");
        }

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
                (float)solver.PassesFor(Substeps, stepIterations) / slices));
            bool done = solver.Advance(per);
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
                }
                else i++;
            }
        }

        /// <summary>
        /// Begins a sand step: everything that has to be settled before the solver runs.
        /// The solver is then advanced a slice per fixed update and
        /// <see cref="EndStep"/> runs when it finishes.
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
            WaterAndFire(dt);
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
                if (water.Available) water.CacheForCpu(64);

                PhaseStart();
                shapes.Gather(bounds);
                PhaseEnd(PhaseGather);
                solver.SetShapes(shapes.Shapes, shapes.ShapeCount, shapes.Bodies.Count);
                solver.ShapeSpin = shapes.Spin;
                solver.BodyCentres = shapes.BodyCentres;

                lastBounds = bounds;
                governor.BudgetMs = StepBudgetMs;
                stepIterations = governor.Iterations(Iterations);

                // Gravity is read from the level every step and never assumed. The space levels have their
                // own, and StatMaster.GodTools.GravityDisabled is the player's switch, not this mod's.
                solver.BeginStep(dt, Substeps, stepIterations, Physics.gravity, water, bounds, count);
                stepTimer.Stop();
                stepMs = (float)stepTimer.Elapsed.TotalMilliseconds;
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
            // Where each grain is now, for the frames until the next step to draw it moving towards (SandRenderer).
            {
                Grain[] g = solver.Grains;
                for (int i = 0; i < count; i++)
                {
                    Vector3 was = g[i].shown;
                    g[i].shownFrom = (was.x == 0f && was.y == 0f && was.z == 0f) ? g[i].pos : was;
                    g[i].shown = g[i].pos;
                }
                float now = Time.time;
                if (lastShownAt > 0f && now > lastShownAt) shownInterval = Mathf.Lerp(shownInterval, Mathf.Clamp(now - lastShownAt, 0.01f, 0.25f), 0.3f);
                lastShownAt = now;
            }
            if (count > 0 && solver != null)
            {
                ApplyForces(stepDt / Mathf.Max(Substeps, 1));
                if (timed) governor.Record(stepMs, count, Limit, stepDt);
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

        private readonly System.Collections.Generic.HashSet<int> continuous = new System.Collections.Generic.HashSet<int>();

        /// <summary>
        /// Every body over the sand gets continuous collision detection against static colliders.
        /// The bed's colliders are surfaces with nothing under them, and a wheel that moved
        /// through one between two physics steps was inside the sand with nothing to push it out
        /// -- which is how a fast machine went into a pile instead of up it. Each body once.
        /// </summary>
        private void Continuous(System.Collections.Generic.List<Rigidbody> bodies)
        {
            for (int i = 0; i < bodies.Count; i++)
            {
                Rigidbody rb = bodies[i];
                if (rb == null || !continuous.Add(rb.GetInstanceID())) continue;
                if (rb.isKinematic || rb.collisionDetectionMode != CollisionDetectionMode.Discrete) continue;
                rb.collisionDetectionMode = CollisionDetectionMode.Continuous;
            }
            if (continuous.Count > 20000) continuous.Clear();
        }

        private int checkedLayer = -1, solidLayer;

        /// <summary>
        /// The floor's layer if machines collide with it, else the default layer. Whatever the
        /// ground raycasts find is not always something a machine stands on, and sand on a layer
        /// the machine ignores is sand it drives through.
        /// </summary>
        private int SolidLayer(int floor)
        {
            if (floor == checkedLayer) return solidLayer;
            checkedLayer = floor;
            int blocks = BlockLayer();
            solidLayer = floor;
            if (floor < 0 || floor > 31 || Physics.GetIgnoreLayerCollision(floor, blocks))
            {
                solidLayer = 0;
                if (Physics.GetIgnoreLayerCollision(0, blocks))
                    for (int l = 0; l < 32; l++)
                        if (!Physics.GetIgnoreLayerCollision(l, blocks)) { solidLayer = l; break; }
            }
            return solidLayer;
        }

        /// <summary>The layer a machine's blocks are on, from the local machine; 0 if there is none.</summary>
        private static int BlockLayer()
        {
            try
            {
                Machine m = Machine.Active();
                if (m != null)
                {
                    List<BlockBehaviour> blocks = m.isSimulating ? m.SimulationBlocks : m.BuildingBlocks;
                    if (blocks != null)
                        for (int i = 0; i < blocks.Count; i++)
                            if (blocks[i] != null) return blocks[i].gameObject.layer;
                }
            }
            catch (System.Exception) { }
            return 0;
        }

        private void StepBed(Bounds wide)
        {
            // Nothing moving, nothing settling and no machine anywhere near the sand: nothing
            // to do but notice a machine arriving, which five times a second does.
            bool idle = count == 0 && bed.Unsettled == 0 && bedShapes.Count == 0 && bedShapes.StanderCount == 0;
            if (idle && (idleSteps++ % 5) != 0) return;

            PhaseStart();
            Bounds reach = wide;
            reach.Expand(1f);
            bedFeed.Gather(reach);
            Continuous(bedFeed.Bodies);
            bedShapes.Prepare(bedFeed);

            // Each part on its own, so that one that throws (and says so, once in a while) does not take the
            // digging, the colliders and the slumping with it.
            try { bed.Absorb(grains, count, bedShapes, BedRestSteps, BedMinLife, BedShapeMargin, BedAbsorbPerStep); }
            catch (System.Exception e) { BedFailed("absorbing", e); }
            bed.StepSeconds = 1f / Mathf.Max(StepsPerSecond, 1);
            try { bed.Release(bedShapes, digSink, BedShapeMargin, BedReleaseTests, ReleasePerStep); }
            catch (System.Exception e) { BedFailed("digging", e); }
            try
            {
                bedColliders.Layer = SolidLayer(ground.GroundLayer);
                bedColliders.Update(bedShapes, ColliderBudgetMs);
            }
            catch (System.Exception e) { BedFailed("colliders", e); }
            try { bed.Relax(BedRelaxColumns, BedRelaxMs); }
            catch (System.Exception e) { BedFailed("slumping", e); }
            PhaseEnd(PhaseBed);
        }

        private string bedError = "";
        private float nextBedErrorLog;

        private void BedFailed(string part, System.Exception e)
        {
            bedError = part + ": " + e.Message;
            if (Time.realtimeSinceStartup < nextBedErrorLog) return;
            nextBedErrorLog = Time.realtimeSinceStartup + 5f;
            Debug.LogError("[Sand] the bed failed while " + part + ": " + e);
        }

        /// <summary>
        /// Where the grains a machine digs out go. Not the governor's ceiling, which sinks while the solver
        /// is struggling and is for what sprayers make: a wheel at the foot of its own pile would otherwise
        /// find no room, and neither dig nor throw anything, for as long as the grains it threw before were
        /// still lying about. Digging may use twice the ceiling, and never less than eight thousand grains: what
        /// wheels throw is coarse grains now, a third as many for the solver, and at a quarter over the ceiling a
        /// machine's wheels filled it in a second and threw nothing more until the grains in the air had settled.
        /// </summary>
        private class DigSink : IBedKickSink
        {
            private readonly SandField field;
            public DigSink(SandField f) { field = f; }
            public int Room
            {
                get
                {
                    // The panel's sand limit, and nothing else: wheels threw, met a ceiling of the governor's, and stopped
                    // until what they had thrown settled.
                    int ceiling = field.Limit;
                    return Mathf.Max(Mathf.Min(field.grains.Length, ceiling) - field.count, 0);
                }
            }
            public void Release(Vector3 position, int material, uint colour) { field.Release(position, material, colour); }
            public void Release(Vector3 position, int material, uint colour, Vector3 velocity) { field.Release(position, material, colour, velocity); }
        }

        private DigSink digSinkInstance;
        private DigSink digSink { get { if (digSinkInstance == null) digSinkInstance = new DigSink(this); return digSinkInstance; } }

        /// <summary>Most speed sand may add to a body in one step, in metres a second. Normal
        /// contact is a fraction of this; it exists so that a bad frame -- a block dropped
        /// into the middle of a pile -- shoves rather than launches.</summary>
        private const float MaxDeltaVPerStep = 6f;

        /// <summary>The same for spin, in radians a second.</summary>
        private const float MaxDeltaSpinPerStep = 5f;

        // The impulses a sand step has worked out, not yet given: each fixed update gives an equal
        // share until the next sand step. Given all at once, a step's whole impulse landed in one
        // physics step -- four times the force the sand really exerted over the 40 ms, in a single
        // 10 ms -- and a joint, which breaks on force, broke on it.
        private readonly System.Collections.Generic.List<Rigidbody> pendingBodies = new System.Collections.Generic.List<Rigidbody>();
        private readonly System.Collections.Generic.List<Vector3> pendingImpulse = new System.Collections.Generic.List<Vector3>();
        private readonly System.Collections.Generic.List<Vector3> pendingTurn = new System.Collections.Generic.List<Vector3>();
        private int pendingLeft;
        private int pendingShares = 1;

        /// <summary>Gives one share of the pending impulses to their bodies.</summary>
        private void GivePending()
        {
            if (pendingLeft <= 0) return;
            float share = 1f / pendingShares;
            for (int i = 0; i < pendingBodies.Count; i++)
            {
                Rigidbody rb = pendingBodies[i];
                if (rb == null) continue;
                if (pendingImpulse[i].sqrMagnitude > 0f) rb.AddForce(pendingImpulse[i] * share, ForceMode.Impulse);
                if (pendingTurn[i].sqrMagnitude > 0f) rb.AddTorque(pendingTurn[i] * share, ForceMode.Impulse);
            }
            if (--pendingLeft <= 0) { pendingBodies.Clear(); pendingImpulse.Clear(); pendingTurn.Clear(); }
        }

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
            // What was not given of the last step goes now, rather than being dropped or doubled up.
            while (pendingLeft > 0) GivePending();
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
                    float mass = Mathf.Max(rb.mass, 0.01f);
                    float most = mass * MaxDeltaVPerStep;
                    if (impulse.sqrMagnitude > most * most)
                    {
                        scale = most / impulse.magnitude;
                        impulse *= scale;
                    }
                    // No bounce: see SandCoupling.Allowed.
                    float share = SandCoupling.Allowed(impulse, mass, rb.velocity, Physics.gravity.magnitude, stepDt);
                    scale *= share;
                    impulse *= share;
                    pendingBodies.Add(rb); pendingImpulse.Add(impulse); pendingTurn.Add(Vector3.zero);
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
                    int at = pendingBodies.Count > 0 && pendingBodies[pendingBodies.Count - 1] == rb ? pendingBodies.Count - 1 : -1;
                    if (at >= 0) pendingTurn[at] = angular;
                    else { pendingBodies.Add(rb); pendingImpulse.Add(Vector3.zero); pendingTurn.Add(angular); }
                }
            }
            pendingShares = Mathf.Max(1, slices);
            pendingLeft = pendingBodies.Count > 0 ? pendingShares : 0;
        }

        private Bounds Bounds(float pad)
        {
            Grain[] g = solver.Grains;

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
            StrokeTick();

            // The copy of the sand a run started from, packed small a little at a time.
            if (bedAtStart != null) SandBed.Compact(bedAtStart, 1f);

            if (levelSandWaiting) ApplyLevelSand();
            if (SandNet.IsClient) ClientUpdate();
            else SandNet.HostSend(this, bed, grains, count);

            // The bed is drawn whenever it holds sand, even with no grains moving.
            PhaseStart();
            if (bed.Enabled) bedRenderer.Draw(bed, ground, 0);
            PhaseEnd(PhaseDraw);

            spray.Limit = Limit;
            if (!ShowCosmetic && spray.Count > 0) spray.Clear();
            spray.Step(Time.deltaTime, Physics.gravity);
            dust.Tick(bed.Enabled ? bed : null, materials);
            if (count <= 0 && spray.Count == 0)
            {
                ReportHitch("frame");
                return;
            }

            // Layer 0. A level's cameras draw it, unlike the build area's, where
            // anything new has to be put on the machine's own layer or it is present,
            // correctly placed and invisible (notes repo, 13-drawing-over-the-machine).
            PhaseStart();
            renderer.Draw(solver.Grains, count, materials, variantBase, 0, !SandNet.IsClient && !Frozen,
                          (Time.time - lastShownAt) / Mathf.Max(shownInterval, 0.01f), ShowCosmetic ? spray : null);
            PhaseEnd(PhaseDraw);
            ReportHitch("frame");
        }

        // ------------------------------------------------------------------ hitches
        //
        // Where the time went, per fixed update and per frame. A call that took longer than
        // HitchMs is written to Player.log with its breakdown, at most once a second, and the
        // worst one is kept for the F9 report -- so a stall in somebody's level says which part
        // of the sand caused it rather than just that the game froze.

        private const int PhaseGround = 0, PhaseScan = 1, PhaseGather = 2,
                          PhaseSolver = 3, PhaseBed = 4, PhaseDraw = 5, PhaseCount = 6;
        private static readonly string[] PhaseNames =
            { "ground raycasts", "collider scan", "collider gather", "solver", "bed", "drawing" };
        private const float HitchMs = 25f;

        private readonly float[] phaseMs = new float[PhaseCount];
        private readonly System.Diagnostics.Stopwatch phaseTimer = new System.Diagnostics.Stopwatch();
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
            }
            for (int i = 0; i < PhaseCount; i++) phaseMs[i] = 0f;
            ground.Raycasts = 0;
        }
    }
}
