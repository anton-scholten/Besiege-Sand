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
    public class SandField : MonoBehaviour
    {
        /// <summary>Sand steps per second. Besiege's own fixed step is 100 Hz; this
        /// deliberately is not, because stepping a medium that often would spend the
        /// whole frame on it and force a hundred GPU readbacks a second.</summary>
        public static int StepsPerSecond = 25;

        public static int Substeps = 2;
        public static int Iterations = 3;

        /// <summary>
        /// Grain ceiling per solver. The GPU number is what the hardware can carry;
        /// the CPU number is what Besiege's 100 Hz step leaves room for, measured
        /// rather than hoped for -- a contact solve is thirty to a hundred times an
        /// integration pass, and an integration pass is about 10 ns per grain per
        /// thread on a desktop core.
        /// </summary>
        public static int GpuCapacity = 250000;
        public static int CpuCapacity = 12000;

        private static SandField instance;

        private ISandSolver solver;
        private SandMaterials.Params[] materials;
        private readonly ShapeFeed shapes = new ShapeFeed();
        private readonly WaterLink water = new WaterLink();
        private readonly GroundField ground = new GroundField();
        private readonly SandRenderer renderer = new SandRenderer();

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

        /// <summary>How many more grains will fit. A region uses this to choose its
        /// own spacing rather than pouring until it is cut off.</summary>
        public int Room { get { return Mathf.Max(capacity - count, 0); } }

        /// <summary>
        /// Claims a row for an emitter, seeded from the medium it is based on.
        /// Returns the base index unchanged when the table is full, so a level with
        /// too many customised emitters loses the customising and not the sand.
        /// </summary>
        public int ClaimVariant(int baseMaterial)
        {
            if (variantsUsed >= MaxVariants) return baseMaterial;
            int slot = SandMaterials.Count + variantsUsed;
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
            if (slot < SandMaterials.Count || slot >= materials.Length) return;
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
            if (slot < SandMaterials.Count || slot >= materials.Length) return;
            int baseIndex = Mathf.Clamp(baseMaterial, 0, SandMaterials.Count - 1);
            materials[slot] = materials[baseIndex];
            variantBase[slot] = baseIndex;
            materialsDirty = true;
        }
        public bool UsingGpu { get { return solver != null && solver.OnGpu; } }

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
        private readonly int[] variantBase = new int[SandMaterials.Count + MaxVariants];

        private void Awake()
        {
            // The solver is chosen before the grain array is sized, because the two
            // ceilings are an order of magnitude apart.
            SandMaterials.Params[] baseTable = SandMaterials.Build();
            materials = new SandMaterials.Params[SandMaterials.Count + MaxVariants];
            for (int i = 0; i < baseTable.Length; i++) materials[i] = baseTable[i];
            for (int i = 0; i < variantBase.Length; i++) variantBase[i] = i;
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
            }

            renderer.Init(materials);
            SceneManager.sceneLoaded += SceneLoaded;
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= SceneLoaded;
            if (solver != null) solver.Dispose();
            renderer.Dispose();
            water.Forget();
        }

        private void SceneLoaded(Scene scene, LoadSceneMode mode)
        {
            // A level change invalidates every collider the feed holds and every grain
            // in the field: they belonged to a level that no longer exists.
            count = 0;
            water.Forget();
            ground.Forget();
            shapes.Rescan();
            rescanCountdown = 0;
        }

        // ------------------------------------------------------------------- filling

        /// <summary>Adds one grain. Silently ignored once the field is full -- a
        /// sprayer held open should slow down, not take the level with it.</summary>
        public void Spawn(Vector3 position, Vector3 velocity, int material, Color colour,
                          float lifetime)
        {
            if (count >= grains.Length) return;

            Grain g = new Grain();
            g.pos = position;
            g.prevPos = position;
            g.vel = velocity;
            g.material = (uint)Mathf.Clamp(material, 0, materials.Length - 1);
            g.colour = SandMaterials.PackColour(colour);
            g.packing = 0f;
            g.wetTimer = 0f;
            g.life = Mathf.Max(lifetime, 0.01f);
            g.age = 0f;

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

            // Spacing is a wish, not an instruction. A big region at a fine spacing
            // asks for hundreds of thousands of grains; rather than pour until the
            // field is full and stop mid-box -- which looks like half a region -- the
            // step is opened up until the whole volume fits in what is left.
            int room = Room;
            if (room > 0)
            {
                float volume = halfExtent.x * halfExtent.y * halfExtent.z * 8f;
                float needed = Mathf.Pow(volume / room, 1f / 3f);
                if (needed > spacing) spacing = needed;
            }

            int nx = Mathf.Max(1, Mathf.FloorToInt(halfExtent.x * 2f / spacing));
            int ny = Mathf.Max(1, Mathf.FloorToInt(halfExtent.y * 2f / spacing));
            int nz = Mathf.Max(1, Mathf.FloorToInt(halfExtent.z * 2f / spacing));

            int added = 0;
            for (int x = 0; x < nx; x++)
                for (int y = 0; y < ny; y++)
                    for (int z = 0; z < nz; z++)
                    {
                        Vector3 local = new Vector3(
                            (x + 0.5f) * spacing - halfExtent.x,
                            (y + 0.5f) * spacing - halfExtent.y,
                            (z + 0.5f) * spacing - halfExtent.z);

                        if (!Inside(kind, local, halfExtent)) continue;

                        local += new Vector3(Random.Range(-0.2f, 0.2f),
                                             Random.Range(-0.2f, 0.2f),
                                             Random.Range(-0.2f, 0.2f)) * spacing;

                        if (count >= grains.Length) return added;
                        Spawn(centre + rotation * local, Vector3.zero, material, colour, lifetime);
                        added++;
                    }
            return added;
        }

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
            count = 0;
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
            wasSimulating = simulating;

            if (count <= 0) return;

            accumulator += Time.fixedDeltaTime;
            float period = 1f / Mathf.Max(StepsPerSecond, 1);
            if (accumulator < period) return;

            float dt = accumulator;
            accumulator = 0f;
            Step(dt);
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

        private void Step(float dt)
        {
            Reap();
            if (count <= 0) return;

            if (rescanCountdown <= 0)
            {
                shapes.Rescan();
                rescanCountdown = StepsPerSecond;   // about once a second
            }
            rescanCountdown--;

            water.Refresh();
            if (water.Available && !solver.OnGpu) water.CacheForCpu(64);

            Bounds bounds = Bounds(0.5f);

            // Rasterising the world is up to nine thousand raycasts, so it happens
            // when the medium leaves the area it was built for, not every step.
            if (!ground.Covers(bounds)) ground.Build(bounds);

            shapes.Gather(bounds);
            solver.SetShapes(shapes.Shapes, shapes.ShapeCount, shapes.Bodies.Count);

            if (dirtyUpload)
            {
                solver.Upload(grains, count);
                dirtyUpload = false;
            }

            if (materialsDirty)
            {
                solver.SetMaterials(materials);
                materialsDirty = false;
            }

            // Gravity is read from the level every step and never assumed. The space
            // levels have their own, and StatMaster.GodTools.GravityDisabled is the
            // player's switch, not this mod's.
            solver.Step(dt, Substeps, Iterations, Physics.gravity, water, bounds, count);

            ApplyForces();
        }

        private void ApplyForces()
        {
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
                if (force.sqrMagnitude > 1e-6f) rb.AddForce(force);
                if (torque.sqrMagnitude > 1e-6f) rb.AddTorque(torque);
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
