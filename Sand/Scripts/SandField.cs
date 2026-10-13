using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SandMod
{
    /// <summary>
    /// The sand, for one level. One of these exists at a time; the panel's brush paints into it and
    /// it does the rest: the bed (SandBed) pressed, ploughed and blown out by the machines, and the
    /// spray and dust that are only drawn.
    ///
    /// It is level-wide on purpose: sand painted in one place has to pile against sand painted in
    /// another. The notes repository's rule for this shape of mod --
    /// one shared registry, reconcile rather than react, put every global back -- is
    /// what this follows.
    /// </summary>
    public class SandField : MonoBehaviour
    {
        /// <summary>Sand steps per second. Besiege's own fixed step is 100 Hz; this
        /// deliberately is not, because stepping a medium that often would spend the
        /// whole frame on it.</summary>
        public static int StepsPerSecond = 25;

        private static SandField instance;

        private SandMaterials.Params[] materials;
        private readonly ShapeFeed shapes = new ShapeFeed();
        private readonly GroundField ground = new GroundField();

        // The sand, kept as a height field.
        private readonly SandBed bed = new SandBed();
        private readonly SandBedRenderer bedRenderer = new SandBedRenderer();
        private readonly SandBedColliders bedColliders = new SandBedColliders();

        /// <summary>Milliseconds a sand step may spend cooking the bed's colliders.</summary>
        private const float ColliderBudgetMs = 3f;
        private readonly ShapeFeed bedFeed = new ShapeFeed();
        private readonly BedShapes bedShapes = new BedShapes();

        /// <summary>How close to a shape the sand must be for the shape to plough it, in metres.</summary>
        private const float BedShapeMargin = 0.09f;

        /// <summary>Sand steps between re-scans for colliders: a second at the 25 steps a second
        /// the bed takes. A machine that breaks, or is built, mid-run is picked up quickly.</summary>
        private const int RescanSteps = 25;

        /// <summary>Scans between full ones, which walk every object in the scene: forty minutes
        /// of a run with a machine on its sand, rather more if there is not one. The level's fixed
        /// colliders do not change during a run, a full scan is the start of every run, and the
        /// machines' blocks are read from the machines in between, so the full one is only there
        /// for loose bodies that turn up later.</summary>
        private const int FullRescanScans = 2400;
        private int fullRescanCountdown;

        // Distance tests ploughing may make a step.
        private const int BedPloughTests = 40000;

        /// <summary>Columns checked for slumping per step. Each costs a few dozen lookups, and a
        /// big brush stroke or a loaded sand file starts with many columns needing a look.</summary>
        private const int BedRelaxColumns = 300;

        /// <summary>Milliseconds a sand step may go on slumping past that, while there is
        /// slumping to do: a steep wall of sand should fall to its angle in a second or two.</summary>
        private const float BedRelaxMs = 3f;

        /// <summary>Milliseconds a fixed update may spend raycasting new ground for the bed's
        /// surroundings. A big bed's ground arrives over a few frames.</summary>
        private const float GroundBudgetMs = 3f;

        private float accumulator;
        private int rescanCountdown;

        /// <summary>
        /// Rows beyond the media and their coarse rows (no longer used; kept so saved sand's rows stay where they
        /// were). A brush whose advanced settings are open gets one of these instead of editing the medium everywhere.
        /// </summary>
        private const int MaxVariants = 59;
        private int variantsUsed;

        public SandMaterials.Params[] Materials { get { return materials; } }

        /// <summary>Whether the spray that is only drawn is drawn (and made) at all: the panel's "Show cosmetic particles".</summary>
        public static bool ShowCosmetic = true;

        /// <summary>Whether wheels raise dust at all, on the sand or off it: the panel's "Wheel dust" (SandDust).</summary>
        public static bool WheelDust = true;

        /// <summary>
        /// Fills the material table from the media, and every row a brush may claim from the
        /// medium it would otherwise be. Called once at startup and again on every level change:
        /// a variant row belongs to the brush that claimed it, and that brush went with the level
        /// before it, so its row is the medium's again rather than the last level's.
        /// </summary>
        private void SeedTable()
        {
            SandMaterials.Params[] baseTable = SandMaterials.Build();
            for (int i = 0; i < baseTable.Length; i++) materials[i] = baseTable[i];
            for (int i = SandMaterials.FirstVariant; i < materials.Length; i++) materials[i] = materials[0];
            // Each row's look: a coarse row looks like the medium it is a coarse version of, and
            // a brush's variant is overwritten with its own base when it is claimed.
            int[] looks = SandMaterials.InitialLooks(variantBase.Length);
            for (int i = 0; i < looks.Length; i++) variantBase[i] = looks[i];
            variantsUsed = 0;
            materialsDirty = true;
        }

        /// <summary>
        /// Claims a row for a brush, seeded from the medium it is based on.
        /// Returns the base index unchanged when the table is full, so a level with
        /// too many customised brushes loses the customising and not the sand.
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

        /// <summary>Writes a variant's row. Ignored for the base media, which no
        /// single brush is allowed to redefine for everyone else.</summary>
        public void WriteVariant(int slot, SandMaterials.Params row)
        {
            if (slot < SandMaterials.FirstVariant || slot >= materials.Length) return;
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

        /// <summary>The field if one exists, without creating it.</summary>
        public static SandField Existing { get { return instance; } }

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

        private bool materialsDirty;
        private bool wasSimulating;
        private readonly int[] variantBase = new int[SandMaterials.FirstVariant + MaxVariants];

        private void Awake()
        {
            materials = new SandMaterials.Params[SandMaterials.FirstVariant + MaxVariants];
            SeedTable();
            bed.Enabled = true;
            bed.FlowLimited = true;
            bed.SetGround(ground);
            bed.SetMaterials(materials);

            spray.Init(transform);
            dust.Init(transform);
            bedRenderer.Init();
            // Under an object of their own, so they can be switched off together in a menu (see InMenu).
            GameObject holder = new GameObject("SandColliders");
            holder.transform.SetParent(transform, false);
            colliderHolder = holder;
            bedColliders.Init(holder.transform, bed);
            bedRenderer.Looks = variantBase;
            bedRenderer.OnRetire = bedColliders.Destroy;
            // The brush's column collector, made once: a field initializer cannot name an instance
            // method in this compiler, and a closure a frame is an allocation a frame.
            collect = CollectColumn;
            remove = RemoveColumn;
            SceneManager.sceneLoaded += SceneLoaded;
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= SceneLoaded;
            RestoreCollision();
            bedColliders.Dispose();
            bedRenderer.Dispose(bed);
        }

        // ------------------------------------------------------------------ menus
        //
        // The sand is kept from level to level as the machine is, and like the machine it is not in the menus:
        // the title screen and the islands' level selects have no machine and no sand. Kept, not shown --
        // nothing simulated, nothing drawn, no colliders -- until a level or the builder is back.

        private GameObject colliderHolder;
        private static string sceneName = "";
        private static bool sceneHooked;

        private static void SceneNamed(Scene scene, LoadSceneMode mode) { sceneName = scene.name ?? ""; }

        /// <summary>Whether the player is in a menu rather than a level or the builder.</summary>
        public static bool InMenu
        {
            get
            {
                if (!sceneHooked)
                {
                    sceneHooked = true;
                    SceneManager.sceneLoaded += SceneNamed;
                    sceneName = SceneManager.GetActiveScene().name ?? "";
                }
                bool title;
                try { title = StatMaster.isMainMenu; } catch (System.Exception) { title = false; }
                return title || sceneName == "MainMenu" || sceneName.StartsWith("LevelSelect");
            }
        }

        private void SceneLoaded(Scene scene, LoadSceneMode mode)
        {
            // The sand stays when the level changes, as the machine does: it is the player's, not the
            // level's (a level that brings sand of its own replaces it, see ApplyLevelSand). What belonged
            // to the old level goes -- every collider the feed holds, the ground under the sand, and the
            // copy a run would have gone back to.
            bedAtStart = null;
            ground.Forget();
            // ...and straight back in, for the sand's own colliders: they are on the field, which a
            // scene load does not destroy, and Forget takes the raycasts' list of things to see
            // through with it. Without this the first ground cast of the new level measures the bed
            // as its own floor.
            bedColliders.Reignore();
            shapes.Rescan();
            bedFeed.CopyBodySources(shapes);
            rescanCountdown = 0;
            // The rows a brush claimed belong to that brush, and it has gone with the level: without
            // this the table filled up over a session and, from the sixtieth claim, every brush's
            // Advanced settings silently stopped applying -- for the rest of the game, with nothing
            // in the log to say so.
            SeedTable();
        }

        private uint varietySeed = 0x9E3779B9u;

        /// <summary>A colour with its medium's variety: some puffs lighter, some darker,
        /// picked by a cheap hash so neighbours differ.</summary>
        private uint Varied(uint colour, int row)
        {
            varietySeed ^= varietySeed << 13; varietySeed ^= varietySeed >> 17; varietySeed ^= varietySeed << 5;
            int look = row >= 0 && row < variantBase.Length ? variantBase[row] : 0;
            return SandMaterials.Vary(colour, look, (int)(varietySeed >> 8));
        }

        public void Clear()
        {
            spray.Clear();
            // Nothing will be stepping the bed now, so nothing would ever hand these back.
            RestoreCollision();
            bed.Clear();
            bed.ForgetJournal();
            strokeClosedAt = -1f;
            SandNet.HostCleared();
        }

        // ------------------------------------------------------------------ undo and redo of brush strokes
        //
        // Sand is undone in Besiege's own history, the machine's (UndoSystem), with its buttons and keys: each brush
        // stroke, and each "Clear all sand", is one step among the machine's edits (SandUndo). A stroke runs from the
        // press that first pours or takes sand away to the release, when its pile is settled in place; its step goes into
        // the history when it begins, and is filled with the chunks it changed, as they were (SandBed's journal), when it
        // is finished -- added at the end instead, a block placed while the pile settled went in ahead of it. Only while
        // building, as the machine's own undo is: strokes made during a run are not steps. A step whose sand has since been replaced wholesale -- a sand file or a level's sand loaded, a run
        // whose changes were kept, a multiplayer session joined -- would put back chunks into sand that is no longer
        // theirs, so it stands down and the history steps past it (UndoSystem tries the next one when a step says
        // it did nothing). Single player and the host: a client's sand is the host's.

        private float strokeClosedAt = -1f;
        private int undoEpoch;

        /// <summary>Strokes and clears go into the machine's history now.</summary>
        private bool Recording
        {
            get { return !SandNet.IsClient && bed.Enabled && !StatMaster.levelSimulating && History != null; }
        }

        private static UndoSystem History
        {
            get
            {
                try { Machine m = Machine.Active(); return m != null ? m.UndoSystem : null; }
                catch (System.Exception) { return null; }
            }
        }

        // The step of the stroke under way, already in the history, filled when the stroke is finished.
        private SandUndo openStep;

        // A sand file's load, waiting to go into the history once the load has settled.
        private SandUndo loadStep;
        private int loadStepFrames;

        /// <summary>A brush stroke begins: its step goes into the history now, so it stands before anything done to the
        /// machine after it, and is filled in when the stroke is finished.</summary>
        public void BeginStroke()
        {
            // What settling cost is measured afresh for each stroke: one huge stroke used to leave it so high
            // that a pile was never settled while painting again for the rest of the session.
            settleUsPerColumn = SettleUsStart;
            if (!Recording) return;
            FinishStroke();
            UndoSystem history = History;
            SandUndo step = new SandUndo(undoEpoch);
            try { history.AddAction(step); }
            catch (System.Exception ex) { Debug.LogWarning("[Sand] could not add the sand to the undo history: " + ex.Message); return; }
            openStep = step;
            bed.BeginJournal();
            strokeClosedAt = -1f;
        }

        /// <summary>The brush is let go: its pile is settled now, and the stroke finished -- at once if the ground it
        /// settles over is known, else once it is (UndoTick).</summary>
        public void EndStroke()
        {
            if (!bed.Journaling || strokeClosedAt >= 0f) return;
            strokeClosedAt = Time.realtimeSinceStartup;
            StrokeTick(true);
            UndoTick();
        }

        private void FinishStroke()
        {
            strokeClosedAt = -1f;
            SandUndo step = openStep;
            openStep = null;
            SandBed.Edit e = bed.Journaling ? bed.EndJournal() : null;
            if (step != null) step.Edit = e;
        }

        private void AddLoadStep()
        {
            SandUndo step = loadStep;
            loadStep = null;
            UndoSystem history = History;
            if (step == null || history == null || step.Epoch != undoEpoch) return;
            try { history.AddAction(step); }
            catch (System.Exception ex) { Debug.LogWarning("[Sand] could not add the sand to the undo history: " + ex.Message); }
        }

        /// <summary>Whether the machine history's last step is a machine load's -- the load screen's own.</summary>
        private static bool LastStepIsMachineLoad()
        {
            try
            {
                UndoSystem history = History;
                if (history == null) return false;
                System.Collections.ObjectModel.ReadOnlyCollection<UndoAction> all = history.UndoActions;
                return all != null && all.Count > 0 && all[all.Count - 1] is ReplaceMachineUndoAction;
            }
            catch (System.Exception) { return false; }
        }

        private void UndoTick()
        {
            if (loadStep != null && ++loadStepFrames >= 1) AddLoadStep();
            if (strokeClosedAt < 0f) return;
            // The pile is placed settled (SandBed.SettleDeferred); what slumping it sets off further out is not waited
            // for.
            bool settled = !bed.HasDeferred && !smoothPending;
            if (settled || Time.realtimeSinceStartup - strokeClosedAt > 3f) FinishStroke();
        }

        /// <summary>The sand has been replaced wholesale: steps recorded before now stand down.</summary>
        private void Replaced()
        {
            FinishStroke();
            bed.ForgetJournal();
            undoEpoch++;
        }

        /// <summary>Takes a step: puts its chunks back, if its sand is still the sand here, and keeps what they replaced
        /// in it for the way back. False when the step stands down. A stroke still open -- its step undone before its
        /// pile had settled -- is finished first, and then undone.</summary>
        public bool TakeStep(SandUndo step)
        {
            if (step.Epoch != undoEpoch || SandNet.IsClient || !bed.Enabled) return false;
            if (step == openStep)
            {
                // Undone while its pile is still being placed: what is left of placing it is dropped, not finished --
                // the chunks go back as they were at once.
                bed.ForgetDeferred();
                smoothPending = false;
            }
            else StrokeTick(true);
            FinishStroke();
            if (step.Edit == null) return false;
            step.Edit = bed.Apply(step.Edit);
            return true;
        }

        /// <summary>The panel's "Clear all sand": one step in the history, which puts it all back.</summary>
        public void ClearAll()
        {
            if (SandNet.IsClient) return;
            if (!Recording) { Clear(); return; }
            StrokeTick(true);
            FinishStroke();
            SandBed.Edit before = bed.PackAll();
            Clear();
            if (before.Count == 0) return;
            SandUndo step = new SandUndo(undoEpoch);
            step.Edit = before;
            try { History.AddAction(step); }
            catch (System.Exception ex) { Debug.LogWarning("[Sand] could not add the sand to the undo history: " + ex.Message); }
        }

        // ------------------------------------------------------------------ between runs
        //
        // Besiege puts a machine back where it was built when a run stops, and by default the
        // sand goes back too: the sand as it was when the run started -- including anything
        // poured or brushed in while building -- is captured then and restored now. With
        // KeepBetweenRuns the sand stays as the run left it instead.

        /// <summary>Leave the sand as a run left it, rather than as it was before the run.</summary>
        public static bool KeepBetweenRuns = true;

        /// <summary>The sand paused: nothing is pressed, ploughed or blown out, and nothing slumps; machines drive on it
        /// as on rock, and wheels still raise their dust and spray.</summary>
        public static bool Frozen;

        private bool wasClient;

        // ------------------------------------------------------------------ multiplayer client

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
        }

        /// <summary>A client's frame: the network, and the ground under the host's sand, for the edges of its mesh.</summary>
        private void ClientUpdate()
        {
            SandNet.ClientTick();
            if (bed.Active) ground.Ensure(bed.Extent, 2f);
        }

        /// <summary>Somebody joined: everything goes out again.</summary>
        public void ResendAll()
        {
            bed.TrackNet = true;
            bed.MarkAllNet();
        }

        private object bedAtStart;

        private void StartRun()
        {
            // The machine is a new one, perhaps on another layer than the one being built.
            checkedLayer = -1;
            bedAtStart = bed.Enabled ? bed.Capture() : null;
            FinishStroke();
        }

        private void EndRun()
        {
            if (KeepBetweenRuns)
            {
                bedAtStart = null;
                Replaced();
                return;
            }

            SandNet.HostCleared();
            // The run is over, so nothing is watching the bodies it sped up: hand them back here,
            // rather than leaving them in continuous collision detection until the next run's first
            // step happens to look at them again.
            RestoreCollision();
            if (bedAtStart != null) bed.Restore(bedAtStart); else bed.Clear();
            bed.ForgetJournal();
            strokeClosedAt = -1f;
            bedAtStart = null;
        }

        // ------------------------------------------------------------------ saved with a level
        //
        // In the level editor the sand is saved into the level file, as the level's own custom
        // data: the bed packed (BedCodec). Besiege keeps a mod's custom data and hands it back on load; a
        // game without this mod ignores it, and the level loads with no sand.

        private const string LevelKey = "SandBed";
        // "SNL2".
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
            // Creating the field is the whole of this hook: it is deliberately not made in the
            // middle of the game's own loading, so this is where it happens, on the next frame.
            // (The local the old line assigned was never read.)
            if (Instance == null) needField = true;
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
            if (toggling)
            {
                // Whatever is waiting belongs to the level before this one, and is not this one's
                // sand to put in later: forgetting it here is what keeps ApplyLevelSand's retry
                // from carrying one level's sand into the next.
                levelSand = null;
                levelSandWaiting = false;
                return;
            }
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
            // setting itself up.
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
            return w.ToArray();
        }

        /// <summary>
        /// Rows as a level keeps them: a medium as itself, and a row of the
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
            if (SandNet.IsClient) { levelSandWaiting = false; levelSand = null; return; }
            // The event that brings a level's sand in can come just before the run's start shows, so
            // this is asked again a frame later -- which is what the flag is for, and it has to stay
            // set until the sand actually goes in. It used to be cleared before these three were
            // checked, so a level whose sand arrived during a run change simply lost it.
            if (StatMaster.levelSimulating || wasSimulating || Time.realtimeSinceStartup - lastRunToggle < 2f) return;
            levelSandWaiting = false;
            byte[] data = levelSand;
            levelSand = null;
            // A level with no sand of its own leaves the sand that came with the player.
            if (data == null) return;
            Clear();
            Replaced();
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
        public static void LoadSandFile(byte[] data) { LoadSandFile(data, true); }

        /// <summary>The same; <paramref name="ours"/> false for a file another player sent, which is no step in this
        /// player's history.</summary>
        public static void LoadSandFile(byte[] data, bool ours)
        {
            if (data == null) return;
            // A client's sand is the host's: the file goes to the host, which loads it for everybody if it allows that.
            if (SandNet.IsClient) { SandNet.RequestLoad(data); return; }
            SandField f = Instance;
            if (f == null) return;
            lastSandFileAt = Time.realtimeSinceStartup;
            levelSandWaiting = false;
            levelSand = null;
            bool record = ours && f.Recording;
            f.StrokeTick(true);
            f.FinishStroke();
            SandBed.Edit before = record ? f.bed.PackAll() : null;
            f.Clear();
            if (!record) f.Replaced();
            f.ReadSand(data, "the file's");
            if (before == null) return;
            // A step, as clearing is: the sand there was, and none where the file put sand that was not there. It goes into
            // the history straight away -- undoable while the sand is still appearing -- but after the step the load
            // screen adds for the machine, so that it is the first undone: now if that one is already in, else on the
            // next frame (UndoTick), the screen adding it in the same call that loads the file.
            f.bed.AddMissing(before);
            SandUndo step = new SandUndo(f.undoEpoch);
            step.Edit = before;
            f.loadStep = step;
            f.loadStepFrames = 0;
            if (LastStepIsMachineLoad()) f.AddLoadStep();
        }

        // ------------------------------------------------------------------ water and fire
        //
        // What makes dry sand wet and wet sand dry. Each is
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

        /// <summary>Snow near a heat source melts.</summary>
        private void Melt(Vector3 at, float reach, float rate, float dt)
        {
            if (bed.Enabled) bed.Melt(at, reach, rate * dt, meltable);
        }

        /// <summary>A stream's melting: where its axis meets the sand.</summary>
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
                if (bed.Enabled) bed.ConvertSurface(p, FireReach * 0.8f, dryOf, 1f / 0.7f);
                Melt(p, FireMeltReach, MeltRate, dt);
            }
        }

        /// <summary>Wets or dries the bed where a stream's axis meets it.</summary>
        private void Stream(Vector3 origin, Vector3 dir, float range, int[] map, float shade)
        {
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

        // What a brush's columns are collected into, and the timer they are collected under. Both
        // live here rather than being made per call: Paint runs every frame while a button is held,
        // and a closure and a Stopwatch each time is a hundred and twenty allocations a second for
        // nothing. The brush's own numbers go in fields, which is what the closure captured.
        private readonly System.Diagnostics.Stopwatch frameTimer = new System.Diagnostics.Stopwatch();
        private readonly System.Diagnostics.Stopwatch settleTimer = new System.Diagnostics.Stopwatch();
        private float brushRise, brushArea;
        private System.Action<float, float, float> collect, remove;

        private void CollectColumn(float x, float z, float w)
        {
            if (brushCount == brushX.Length)
            {
                System.Array.Resize(ref brushX, brushCount * 2); System.Array.Resize(ref brushZ, brushCount * 2);
                System.Array.Resize(ref brushVolume, brushCount * 2);
            }
            brushX[brushCount] = x; brushZ[brushCount] = z; brushVolume[brushCount] = brushRise * w * brushArea;
            brushCount++;
        }

        private void RemoveColumn(float x, float z, float w)
        {
            bed.RemoveVolume(x, z, brushRise * w);
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
            // The brush is the one thing that can put a number the bed has never seen straight
            // into a column's height, and one NaN there makes the bed's whole extent NaN for the
            // rest of the session. Its callers check what came from outside (SandNet.OnBrush,
            // the panel), and this is the last of it: every path in, refused in one place.
            if (!SandGuard.Ok(centre) || !SandGuard.Ok(radius) || !SandGuard.Ok(rise)) return;
            radius = Mathf.Clamp(radius, 0.05f, MaxBrushRadius);
            if (SandNet.IsClient) { SandNet.RequestBrush(centre, radius, rise, medium, false, style, seed, colour); return; }
            if (!bed.Enabled || rise <= 0f) return;
            if (!BrushReady(centre, radius)) return;
            bed.JournalArea(centre.x, centre.z, radius + 1f);
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
            frameTimer.Reset(); frameTimer.Start();
            brushCount = 0;
            brushRise = rise; brushArea = area;
            ForBrush(centre, radius, style, seed, BrushTaper(centre, style, medium), rise, collect);
            bed.PourBatch(brushX, brushZ, brushVolume, brushCount, medium, colour, DeferGrowth);
            sweep.Adapt(frameTimer.Elapsed.TotalMilliseconds, BrushBudgetMs);

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
                        settleTimer.Reset(); settleTimer.Start();
                        if (SettleStroke(margin))
                        {
                            float ms = (float)settleTimer.Elapsed.TotalMilliseconds;
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
        private const float SettleUsStart = 0.3f;
        private float settleUsPerColumn = SettleUsStart;     // what settling has cost, measured as it goes
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
                bed.JournalArea(smI0 - 1, smJ0 - 1, smI1 + 1, smJ1 + 1);
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
            bed.JournalArea(i0 - margin, j0 - margin, i1 + margin, j1 + margin);
            bed.SettleDeferred(margin);
            deferSince = -1f;
            return true;
        }

        /// <summary>
        /// Takes sand away under a brush, up to <paramref name="depth"/> where it is at full
        /// strength.
        /// </summary>
        public void Erase(Vector3 centre, float radius, float depth, int style, int seed)
        {
            // As Paint: the brush's numbers are checked where they come from, and refused here.
            if (!SandGuard.Ok(centre) || !SandGuard.Ok(radius) || !SandGuard.Ok(depth)) return;
            radius = Mathf.Clamp(radius, 0.05f, MaxBrushRadius);
            if (SandNet.IsClient) { SandNet.RequestBrush(centre, radius, depth, 0, true, style, seed, 0u); return; }
            if (depth <= 0f) return;
            bed.JournalArea(centre.x, centre.z, radius + 2f);
            if (bed.Enabled)
            {
                brushRise = depth;
                ForBrush(centre, radius, style, seed, 0f, depth, remove);
            }
        }

        // ------------------------------------------------------------------ stepping

        // The spray that is only drawn, and how many puffs go with each throw.
        private readonly SandSpray spray = new SandSpray();
        private readonly SandDust dust = new SandDust();
        private const int SprayPerThrow = 3;

        /// <summary>How much bigger than its medium's grain a puff of spray is, but for dust's.</summary>
        private const float CosmeticScale = 2f;

        /// <summary>The least share of a slipping surface's speed its spray leaves with, however much the medium clings:
        /// glue's share by its stickiness was next to nothing, and its puffs lay on the sand where nobody saw them.</summary>
        private const float CosmeticShareLeast = 0.4f;

        /// <summary>How fast, in m/s, a shape must come down on the sand for it to splash.</summary>
        private const float ImpactSpeed = 3f;

        /// <summary>The stickiness at which a medium's spray leaves with half the share a loose one's does.</summary>
        private const float StickyThrow = 150f;

        /// <summary>
        /// The drawn spray of every shape slipping over the sand -- a wheel spinning, a block dragged -- or landing on it
        /// hard (a splash all round, from <see cref="ImpactSpeed"/>), every sand step, and with the sand paused: a fixed
        /// amount per shape and step; only "Show cosmetic particles" turns it off. Each puff
        /// leaves from somewhere along the back of a turning shape's rim, where its throw would (SandBed's
        /// KickExitLeast..KickExitMost round from the bottom), with the rim's speed there and climbing; a shape that is
        /// not turning throws back the way its underside moves. As much as the medium would be thrown: stickier media
        /// fly less. It moves no sand.
        /// </summary>
        private void ShapeSpray()
        {
            if (!ShowCosmetic || !bed.Enabled) return;
            int shapes = bedShapes.Count;
            for (int s = 0; s < shapes; s++)
            {
                Vector3 lo, hi;
                bedShapes.Extent(s, out lo, out hi);
                Vector3 hub = (lo + hi) * 0.5f;
                Vector3 bottom = new Vector3(hub.x, lo.y, hub.z);
                float top;
                if (!bed.TryTop(bottom.x, bottom.z, out top) || bottom.y > top + 0.1f) continue;
                Vector3 v = bedShapes.SurfaceVelocity(s, bottom);
                float run = Mathf.Sqrt(v.x * v.x + v.z * v.z);
                int i = Mathf.FloorToInt(bottom.x / SandBed.Cell), j = Mathf.FloorToInt(bottom.z / SandBed.Cell);
                int row = bed.MaterialAt(i, j);
                if (row < 0) continue;
                bool slipping = run >= Mathf.Max(materials[row].digSpeed, 0.5f);
                float down = -v.y;
                if (!slipping && down < ImpactSpeed) continue;
                float t, b;
                uint colour;
                if (!bed.Thickness(i, j, out t, out b, out colour)) continue;
                float share = Mathf.Max(SandBed.KickShare / (1f + materials[row].adhesion / StickyThrow), CosmeticShareLeast);
                if (!slipping)
                {
                    // Landing on the sand: a splash out from under it all round, by how hard it came down.
                    Vector3 half = (hi - lo) * 0.5f;
                    int splash = Mathf.Clamp(Mathf.RoundToInt(down * 1.2f * share), 1, 10);
                    for (int q = 0; q < splash; q++)
                    {
                        varietySeed ^= varietySeed << 13; varietySeed ^= varietySeed >> 17; varietySeed ^= varietySeed << 5;
                        float a = (varietySeed & 0xFFFF) / 65535f * Mathf.PI * 2f, ca = Mathf.Cos(a), sa = Mathf.Sin(a);
                        float out_ = down * share * (0.3f + (varietySeed >> 24) / 255f * 0.4f);
                        Vector3 rim = new Vector3(bottom.x + ca * half.x, top + 0.02f, bottom.z + sa * half.z);
                        Cosmetic(rim, new Vector3(ca * out_, out_ * 0.8f, sa * out_), colour, row);
                    }
                    continue;
                }
                int puffs = Mathf.Clamp(Mathf.RoundToInt(run * 1.2f * share), 1, 10);

                // The shape's spin, read off its surface's velocity field.
                Vector3 ax = bedShapes.SurfaceVelocity(s, bottom + Vector3.right) - v;
                Vector3 ay = bedShapes.SurfaceVelocity(s, bottom + Vector3.up) - v;
                Vector3 spin = new Vector3(ay.z, -ax.z, ax.y);
                float turning = spin.magnitude;
                for (int q = 0; q < puffs; q++)
                {
                    Vector3 from = new Vector3(bottom.x, top + 0.02f, bottom.z), throwV = v;
                    if (turning > 1f)
                    {
                        Vector3 axis = spin / turning, arm = bottom - hub, rim = Vector3.Cross(spin, arm);
                        varietySeed ^= varietySeed << 13; varietySeed ^= varietySeed >> 17; varietySeed ^= varietySeed << 5;
                        float exit = (SandBed.KickExitLeast + (varietySeed & 0xFFFF) / 65535f * (SandBed.KickExitMost - SandBed.KickExitLeast)) * Mathf.Deg2Rad;
                        float ec = Mathf.Cos(exit), es = Mathf.Sin(exit);
                        Vector3 moved = hub + Rotate(arm, axis, ec, es);
                        if (moved.y > top) from = moved;
                        throwV = v - rim + Rotate(rim, axis, ec, es);
                    }
                    float tr = Mathf.Sqrt(throwV.x * throwV.x + throwV.z * throwV.z);
                    if (throwV.y < tr * SandBed.KickLift) throwV.y = tr * SandBed.KickLift;
                    Cosmetic(from, throwV * share, colour, row);
                }
            }
        }

        /// <summary>
        /// A blast's drawn spray, from the sand over its crater (BlastThrow.Throw): a puff for every 0.05 m^2 of crater,
        /// 40 to 400. It moves no sand.
        /// </summary>
        private void BlastSpray(BlastThrow sink, Vector3 centre, float crater)
        {
            if (!ShowCosmetic || crater <= 0f) return;
            int puffs = Mathf.Clamp(Mathf.RoundToInt(Mathf.PI * crater * crater / 0.05f), 40, 400);
            for (int q = 0; q < puffs; q++)
            {
                varietySeed ^= varietySeed << 13; varietySeed ^= varietySeed >> 17; varietySeed ^= varietySeed << 5;
                float a = (varietySeed & 0xFFFF) / 65535f * Mathf.PI * 2f, r = Mathf.Sqrt((varietySeed >> 16) / 65535f) * crater;
                float x = centre.x + Mathf.Cos(a) * r, z = centre.z + Mathf.Sin(a) * r;
                int i = Mathf.FloorToInt(x / SandBed.Cell), j = Mathf.FloorToInt(z / SandBed.Cell);
                float t, b;
                uint colour;
                if (!bed.Thickness(i, j, out t, out b, out colour)) continue;
                int row = bed.MaterialAt(i, j);
                if (row < 0) continue;
                Vector3 p = new Vector3(x, t + 0.05f, z);
                Cosmetic(p, sink.Throw(p, row), colour, row);
            }
        }

        /// <summary>Turns <paramref name="v"/> about the unit <paramref name="k"/> by the angle whose cosine and sine are
        /// given (Rodrigues).</summary>
        private static Vector3 Rotate(Vector3 v, Vector3 k, float c, float s)
        {
            return v * c + Vector3.Cross(k, v) * s + k * (Vector3.Dot(k, v) * (1f - c));
        }

        /// <summary>
        /// One throw of drawn spray, as many puffs for every medium, each the size of the medium's grain (a brush's own
        /// row, its own) -- dust's soft and part clear, the rest opaque.
        /// </summary>
        private void Cosmetic(Vector3 position, Vector3 velocity, uint colour, int material)
        {
            if (!ShowCosmetic) return;
            int row = Mathf.Clamp(material, 0, materials.Length - 1);
            int fine = row >= SandMaterials.FirstVariant ? row : SandMaterials.MediumOf(row);
            float radius = materials[Mathf.Clamp(fine, 0, materials.Length - 1)].radius;
            int look = row < variantBase.Length ? variantBase[row] : SandMaterials.Sand;
            bool dust = look == SandMaterials.Dust;
            float least, most;
            SpraySizes(look, out least, out most);
            spray.Emit(position, velocity, Varied(colour, row), radius * least, radius * most,
                       dust ? SandSpray.DustOpacity : (byte)255, dust ? SandSpray.DustAcross : SandSpray.DiscAcross, dust, SprayPerThrow);
        }

        /// <summary>
        /// The sizes a medium's puffs of spray are spread over, as multiples of its fine grain, each puff somewhere between
        /// at random: most media three quarters of twice the grain to a quarter over (twice, as a spray reads at a
        /// distance); gravel's half that, its grain being big already; mud's double; snow's reaching further up, to three
        /// grains; dust's -- its soft puffs -- from three quarters of a grain to two.
        /// </summary>
        private static void SpraySizes(int look, out float least, out float most)
        {
            switch (look)
            {
                case SandMaterials.Dust: least = 0.75f; most = 2f; return;
                case SandMaterials.Gravel: least = 0.75f; most = 1.25f; return;
                case SandMaterials.Mud: least = 0.75f * 4f; most = 1.25f * 4f; return;
                case SandMaterials.Snow: least = 1.5f; most = 3f; return;
                default: least = 0.75f * CosmeticScale; most = 1.25f * CosmeticScale; return;
            }
        }

        /// <summary>
        /// The most puffs of drawn spray at once. Not a limit anybody meets: what sets how much spray there is is a fixed
        /// amount per shape and blast each step (ShapeSpray, BlastSpray). Unity's particle system wants a most, and this
        /// only guards against something going wrong.
        /// </summary>
        private const int CosmeticMost = 50000;

        // ------------------------------------------------------------------ explosions
        //
        // A bomb blows a bowl out of the sand and heaps what it was in a rim round it, with a spray that
        // is only drawn going outward and up from the blast, faster the nearer it was. What it leaves
        // round the hole is steeper than it stands and slumps in.
        //
        // The game has no explosion event for a mod to listen to, but every kind of explosive has the same
        // few public fields -- radius, power, upPower and hasExploded -- so they are looked at, twenty times
        // a second, and the ones that have gone off are noted once each.

        /// <summary>How hard explosions throw the sand, as a multiple.</summary>
        public static float ExplosionStrength = 1f;
        /// <summary>The share of an explosion's radius that is blown out of the ground as a crater.</summary>
        public static float CraterShare = 0.55f;

        private readonly System.Collections.Generic.HashSet<int> exploded = new System.Collections.Generic.HashSet<int>();
        private readonly System.Collections.Generic.HashSet<int> seenNow = new System.Collections.Generic.HashSet<int>();
        private float scanNow;
        private System.Predicate<Vector4> blastOld;
        private System.Predicate<int> notSeenNow;
        private float nextExplosionScan;
        private int explosionBlocksScan;

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
            if (!bed.Active) return;
            float now = Time.time;
            if (now < nextExplosionScan) return;
            nextExplosionScan = now + 0.05f;
            seenNow.Clear();
            scanNow = now;
            if (blastOld == null)
            {
                // Made once, not every scan: twenty scans a second, three closures each.
                blastOld = delegate (Vector4 r) { return scanNow - r.w > 3f; };
                notSeenNow = delegate (int id) { return !seenNow.Contains(id); };
            }
            recentBlasts.RemoveAll(blastOld);

            Machine machine = Machine.Active();
            if (machine != null && machine.isSimulating && machine.SimulationBlocks != null)
            {
                List<BlockBehaviour> blocks = machine.SimulationBlocks;
                for (int i = 0; i < blocks.Count; i++)
                {
                    BlockBehaviour b = blocks[i];
                    if (b == null) continue;
                    ExplodeOnCollideBlock bomb = b as ExplodeOnCollideBlock;
                    if (bomb != null) { Noticed("bomb", bomb.GetInstanceID(), bomb.hasExploded, bomb.transform.position, bomb.radius, bomb.power, bomb.upPower); continue; }
                    ControllableBomb remote = b as ControllableBomb;
                    if (remote != null) { Noticed("remote bomb", remote.GetInstanceID(), remote.hasExploded, remote.transform.position, remote.radius, remote.power, remote.upPower); }
                }
            }

            // Everything else that goes off -- level objects, cannonballs -- is in the scene, not in the
            // machine. The level explosives are walked every 16th scan (0.8 s): a cannonball's crater then
            // appears at most 0.8 s late, with its exact numbers, and anything the slower walk still misses
            // is caught by the effect walk below.
            explosionBlocksScan++;
            bool explosiveScan = (explosionBlocksScan & 15) == 0;
            if (explosiveScan)
            {
                foreach (ExplodeOnCollide e in Object.FindObjectsOfType<ExplodeOnCollide>()) Noticed("level explosive", e.GetInstanceID(), e.hasExploded, e.transform.position, e.radius, e.power, e.upPower);
                foreach (ExplodeOnCollideSmall e in Object.FindObjectsOfType<ExplodeOnCollideSmall>()) Noticed("small explosive", e.GetInstanceID(), e.hasExploded, e.transform.position, e.radius, e.power, e.upPower);
                foreach (SmallExplosion e in Object.FindObjectsOfType<SmallExplosion>()) Noticed("small bomb", e.GetInstanceID(), e.hasExploded, e.transform.position, e.radius, e.power, e.upPower);
                foreach (CannonBallExplosion e in Object.FindObjectsOfType<CannonBallExplosion>()) Noticed("cannonball", e.GetInstanceID(), e.hasExploded, e.transform.position, e.radius, e.power, e.upPower);
            }
            // The effects every explosion starts are walked every 4th scan (0.2 s), as before: they are
            // the safety net that turns an otherwise-unexplained effect into a crater, so an effect must
            // not wait for the slower walk above.
            if ((explosionBlocksScan & 3) == 0)
            {
                foreach (ExplosionEffect fx in Object.FindObjectsOfType<ExplosionEffect>()) NoticedEffect(fx.GetInstanceID(), fx.transform.position, fx.dustCraterQuad);
                foreach (ExplosionEffectAdv fx in Object.FindObjectsOfType<ExplosionEffectAdv>()) NoticedEffect(fx.GetInstanceID(), fx.transform.position, null);
            }
            // Between the scans that did not walk them, what was seen is carried over, or it would look
            // new again -- and an object that had gone off would go off a second time.
            if (!explosiveScan)
            {
                foreach (int id in exploded) seenNow.Add(id);
            }
            if ((explosionBlocksScan & 3) != 0)
            {
                foreach (int id in effectsSeen) seenNow.Add(id);
            }
            exploded.RemoveWhere(notSeenNow);
            effectsSeen.RemoveWhere(notSeenNow);

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
                BlastThrow sink = new BlastThrow(this, centre, radius, fastest, lift);
                // A bowl as wide as a share of the blast and as deep as a third of its width, never more than two metres.
                float crater = radius * CraterShare;
                // A multiplayer client only sprays: the crater is the host's, and comes with its sand.
                if (!SandNet.IsClient) bed.Blast(centre, crater, Mathf.Min(crater * 0.35f, 2f));
                BlastSpray(sink, centre, crater);
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

        /// <summary>The speed a blast's spray leaves with: away from the blast, and up, and slower the farther it was.</summary>
        private class BlastThrow
        {
            private readonly SandField field;
            private readonly Vector3 centre;
            private readonly float radius, fastest, lift;
            private uint seed = 2463534242u;

            public BlastThrow(SandField f, Vector3 c, float r, float fast, float up) { field = f; centre = c; radius = r; fastest = fast; lift = up; }

            /// <summary>What the blast gives a puff of a medium at a point: away from it, and up, slower the farther.</summary>
            public Vector3 Throw(Vector3 position, int material)
            {
                Vector3 away = position - centre;
                float d = away.magnitude;
                Vector3 dir = d > 0.01f ? away / d : Vector3.up;
                dir = (dir + Vector3.up * lift).normalized;
                seed ^= seed << 13; seed ^= seed >> 17; seed ^= seed << 5;
                float spread = 0.7f + (seed & 0xFFFF) / 65535f * 0.6f;
                float fall = Mathf.Clamp01(1f - d / (radius * 1.8f));
                return dir * (fastest * fall * spread * field.BlastBoost(material, fastest));
            }
        }

        private void FixedUpdate()
        {
            bool menu = InMenu;
            if (colliderHolder != null && colliderHolder.activeSelf == menu) colliderHolder.SetActive(!menu);
            if (menu) return;
            // A multiplayer client draws the host's sand and simulates none of its own.
            bool client = SandNet.IsClient;
            if (client != wasClient)
            {
                // Into or out of someone else's session: what is here is not theirs, or not ours.
                bed.Clear();
                Replaced();
                wasClient = client;
            }
            if (client)
            {
                ClientCosmetics();
                return;
            }

            if (!Frozen) ExplosionScan();

            bool simulating = StatMaster.levelSimulating;
            if (simulating != wasSimulating) lastRunToggle = Time.realtimeSinceStartup;
            if (wasSimulating && !simulating) EndRun();

            // A run builds its machine anew, so every collider the feed remembers belongs
            // to one that no longer exists.
            if (!wasSimulating && simulating)
            {
                rescanCountdown = 0;
                fullRescanCountdown = 0;
                StartRun();
            }
            wasSimulating = simulating;

            // Paused: nothing is pressed, ploughed or blown out, but the sand is still ground --
            // its colliders follow the machines, so they drive on it as on rock.
            if (Frozen)
            {
                FrozenStep();
                return;
            }

            // What the bed's colliders reported pushing on it last physics step is in: the bed sinks under
            // it a sand step at a time (Step), under the sum of the fixed updates since over their number.
            // Its slopes slide by the time that passed (SandBed.FlowLimited).
            bed.Clock += Time.fixedDeltaTime;
            bed.LoadFrame();

            // Slumping, every fixed update, for as long as there is a backlog and harder the
            // bigger it is: a wall of sand left standing is a cliff a machine digs through
            // rather than climbs.
            if (bed.Enabled && bed.Unsettled > 0)
                bed.Relax(100, Mathf.Clamp(bed.Unsettled / 2000f, 0.3f, 2f));

            if (!bed.Active) return;

            accumulator += Time.fixedDeltaTime;
            float period = 1f / Mathf.Max(StepsPerSecond, 1);
            if (accumulator < period) return;
            float dt = accumulator;
            accumulator = 0f;
            Step(dt);
        }

        /// <summary>The scene scanned for colliders, every so many sand steps.</summary>
        private void Rescan()
        {
            if (rescanCountdown <= 0)
            {
                // One scan of the scene for both feeds: FindObjectsOfType is the expensive
                // part. Everything at the start of a run and now and then; between those, only
                // the rigidbodies, which is all that comes and goes.
                if (fullRescanCountdown <= 0)
                {
                    shapes.Rescan();
                    fullRescanCountdown = FullRescanScans;
                }
                else shapes.RescanBodies();
                fullRescanCountdown--;
                if (bed.Enabled) bedFeed.CopyBodySources(shapes);
                // No machine near its sand: it looks four times less often.
                rescanCountdown = bedShapes.Count == 0 ? RescanSteps * 4 : RescanSteps;
            }
            rescanCountdown--;
        }

        private float frozenClock, clientClock;

        /// <summary>
        /// A multiplayer client's own drawn spray. The host simulates the sand and sends it; the spray is only drawn, so
        /// each player makes their own, a sand step at a time, from the machines as their game shows them (their bodies
        /// are moved by the network: their speed is read off how they move, see ShapeFeed.Moved) -- no wait for the host,
        /// and each player's "Show cosmetic particles" is their own. Wheels' spray and blasts'; it moves no sand.
        /// </summary>
        private void ClientCosmetics()
        {
            if (!ShowCosmetic || !bed.Enabled || bed.FilledColumns <= 0) return;
            clientClock += Time.fixedDeltaTime;
            if (clientClock < 1f / Mathf.Max(StepsPerSecond, 1)) return;
            clientClock = 0f;
            Rescan();
            Bounds reach = bed.Extent;
            reach.Expand(1f);
            bedFeed.Gather(reach);
            bedShapes.Prepare(bedFeed);
            ShapeSpray();
            if (!SandNet.HostFrozen) ExplosionScan();   // nothing explodes into the host's paused sand
        }

        /// <summary>
        /// A sand step while the sand is paused: no ploughing, no pressing (what the colliders report pushing
        /// on the sand is let go each fixed update), only the colliders kept where the machines are.
        /// </summary>
        private void FrozenStep()
        {
            if (!bed.Enabled) return;
            bed.DropLoads();
            if (bed.FilledColumns <= 0) return;
            frozenClock += Time.fixedDeltaTime;
            if (frozenClock < 1f / Mathf.Max(StepsPerSecond, 1)) return;
            frozenClock = 0f;

            Rescan();
            ground.Ensure(bed.Extent, GroundBudgetMs);

            Bounds reach = bed.Extent;
            reach.Expand(1f);
            bedFeed.Gather(reach);
            Continuous(bedFeed.Bodies);
            bedShapes.Prepare(bedFeed);
            try
            {
                bedColliders.Layer = SolidLayer(ground.GroundLayer);
                bedColliders.Update(bedShapes, ColliderBudgetMs);
            }
            catch (System.Exception e) { BedFailed("colliders", e); }
            // The spray goes on, paused or not: it moves no sand.
            ShapeSpray();
        }

        /// <summary>A sand step: the colliders and what the machines do to the sand.</summary>
        private void Step(float dt)
        {
            // Pressing, a sand step at a time: it ran every fixed update, four times as often as anything
            // that reads it (the colliders are cooked and the sand ploughed a sand step at a time), and
            // was the dearest thing the bed did with a big machine on it.
            bed.Press(dt);

            Rescan();

            // Water cannons wet sand; steam, flamethrowers and burning blocks dry it.
            WaterAndFire(dt);

            // Around the bed, a few milliseconds' worth of ground a step. Each tile is cast once.
            ground.Ensure(bed.Extent, GroundBudgetMs);

            if (materialsDirty)
            {
                bed.SetMaterials(materials);
                materialsDirty = false;
            }
            StepBed(bed.Extent, dt);
        }

        private int idleSteps;

        // Bodies this switched to continuous collision detection, to switch back.
        private readonly System.Collections.Generic.Dictionary<int, Rigidbody> continuous = new System.Collections.Generic.Dictionary<int, Rigidbody>();
        private readonly System.Collections.Generic.HashSet<int> continuousSeen = new System.Collections.Generic.HashSet<int>();
        private readonly System.Collections.Generic.List<int> continuousGone = new System.Collections.Generic.List<int>();

        /// <summary>Speed, in m/s, above which a body near the sand gets continuous collision detection, and below
        /// which it goes back to discrete. A physics step is a hundredth of a second, so at 3 m/s a body moves 3 cm a
        /// step, about what PhysX's contact offset still catches against a surface with nothing under it.</summary>
        private const float ContinuousOn = 3f, ContinuousOff = 1.5f;

        /// <summary>How high over the sand under it, in metres, a body still counts as near it.</summary>
        private const float ContinuousHeight = 2f;

        /// <summary>
        /// Continuous collision detection against static colliders for the bodies that need it: fast, and near the sand.
        /// The bed's colliders are surfaces with nothing under them, and a wheel that moved through one between two
        /// physics steps was inside the sand with nothing to push it out -- which is how a fast machine went into a
        /// pile instead of up it. It was every body over the sand, always: on a 70-body machine over five hundred
        /// sand colliders that is a great deal of PhysX's time for bodies standing or crawling, which cannot tunnel.
        /// Switched back when they slow down, rise clear, or leave.
        /// </summary>
        private void Continuous(System.Collections.Generic.List<Rigidbody> bodies)
        {
            continuousSeen.Clear();
            for (int i = 0; i < bodies.Count; i++)
            {
                Rigidbody rb = bodies[i];
                if (rb == null || rb.isKinematic) continue;
                int id = rb.GetInstanceID();
                continuousSeen.Add(id);
                float speed = rb.velocity.magnitude;
                // The body the entry is about must be this body. Unity hands instance IDs round again
                // once an object is gone, and an entry left over from the last body's ID would
                // answer for this one: a fast body near the sand takes the "already switched" branch,
                // does nothing, and never gets the continuous collision detection this is here for.
                Rigidbody was = null;
                bool switched = continuous.TryGetValue(id, out was);
                if (switched && was != rb) { continuous.Remove(id); switched = false; }
                if (switched)
                {
                    if (speed < ContinuousOff || !NearSand(rb.position))
                    {
                        if (rb.collisionDetectionMode == CollisionDetectionMode.Continuous)
                            rb.collisionDetectionMode = CollisionDetectionMode.Discrete;
                        continuous.Remove(id);
                    }
                }
                else if (speed > ContinuousOn && rb.collisionDetectionMode == CollisionDetectionMode.Discrete && NearSand(rb.position))
                {
                    rb.collisionDetectionMode = CollisionDetectionMode.Continuous;
                    continuous[id] = rb;
                }
            }
            // Gone from over the sand: back as they were.
            if (continuous.Count == continuousSeen.Count) return;
            continuousGone.Clear();
            foreach (System.Collections.Generic.KeyValuePair<int, Rigidbody> kv in continuous)
            {
                if (continuousSeen.Contains(kv.Key)) continue;
                continuousGone.Add(kv.Key);
                if (kv.Value != null && kv.Value.collisionDetectionMode == CollisionDetectionMode.Continuous)
                    kv.Value.collisionDetectionMode = CollisionDetectionMode.Discrete;
            }
            for (int n = 0; n < continuousGone.Count; n++) continuous.Remove(continuousGone[n]);
        }

        /// <summary>Everything this switched to continuous collision detection goes back to how it
        /// was found, all of it, now.
        ///
        /// The sweep above does that for a body that slows down or moves away, but it is only
        /// reached while the bed has sand in it and the level is stepping: FixedUpdate stands down
        /// when the bed is inactive, and FrozenStep when there is none. So clearing the sand, or
        /// ending a run with nothing kept, left every body it had switched in that mode -- and a
        /// machine that spends the rest of the session in continuous collision detection is paying
        /// PhysX for it over nothing.</summary>
        private void RestoreCollision()
        {
            foreach (System.Collections.Generic.KeyValuePair<int, Rigidbody> kv in continuous)
            {
                Rigidbody rb = kv.Value;
                if (rb != null && rb.collisionDetectionMode == CollisionDetectionMode.Continuous)
                    rb.collisionDetectionMode = CollisionDetectionMode.Discrete;
            }
            continuous.Clear();
            continuousSeen.Clear();
        }

        private bool NearSand(Vector3 p)
        {
            float top;
            if (!bed.TryTop(p.x, p.z, out top)) top = ground.Height(p.x, p.z);
            return top > -9000f && p.y - top < ContinuousHeight;
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

        private void StepBed(Bounds wide, float dt)
        {
            // Nothing settling and no machine anywhere near the sand: nothing to do but notice
            // a machine arriving, which five times a second does.
            bool idle = bed.Unsettled == 0 && bedShapes.Count == 0 && bedShapes.StanderCount == 0;
            if (idle && (idleSteps++ % 5) != 0) return;

            Bounds reach = wide;
            reach.Expand(1f);
            bedFeed.Gather(reach);
            Continuous(bedFeed.Bodies);
            bedShapes.Prepare(bedFeed);

            // Each part on its own, so that one that throws (and says so, once in a while) does not take the
            // ploughing, the colliders and the slumping with it.
            try { bed.Plough(bedShapes, BedShapeMargin, BedPloughTests, dt); }
            catch (System.Exception e) { BedFailed("ploughing", e); }
            ShapeSpray();
            try
            {
                bedColliders.Layer = SolidLayer(ground.GroundLayer);
                bedColliders.Update(bedShapes, ColliderBudgetMs);
            }
            catch (System.Exception e) { BedFailed("colliders", e); }
            try { bed.Relax(BedRelaxColumns, BedRelaxMs); }
            catch (System.Exception e) { BedFailed("slumping", e); }
        }

        private float nextBedErrorLog;

        private void BedFailed(string part, System.Exception e)
        {
            if (Time.realtimeSinceStartup < nextBedErrorLog) return;
            nextBedErrorLog = Time.realtimeSinceStartup + 5f;
            Debug.LogError("[Sand] the bed failed while " + part + ": " + e);
        }

        private void Update()
        {
            if (InMenu) return;
            bed.TrackNet = SandNet.IsHost;
            StrokeTick();
            UndoTick();

            // The copy of the sand a run started from, packed small a little at a time.
            if (bedAtStart != null) SandBed.Compact(bedAtStart, 1f);

            if (levelSandWaiting) ApplyLevelSand();
            if (SandNet.IsClient) ClientUpdate();
            else SandNet.HostSend(this, bed);

            // The bed is drawn whenever it holds sand.
            if (bed.Enabled) bedRenderer.Draw(bed, ground, 0);

            spray.SetLimit(CosmeticMost);
            if (!ShowCosmetic && spray.Count > 0) spray.Clear();
            dust.Tick(bed.Enabled ? bed : null, materials);
        }
    }
}
