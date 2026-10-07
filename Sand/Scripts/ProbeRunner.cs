using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SandMod
{
    /// <summary>
    /// Writes the capability report. Runs once at load, and again on F9 so the same
    /// report can be taken inside a level, where the scene has a terrain, a machine
    /// and the game's own water in it.
    /// </summary>
    public class ProbeRunner : MonoBehaviour
    {
        private const string Tag = "[Sand] ";

        private static void Say(string line)
        {
            UnityEngine.Debug.Log(Tag + line);
        }

        private void OnEnable()
        {
            SceneManager.sceneLoaded += SceneLoaded;
        }

        private void OnDisable()
        {
            SceneManager.sceneLoaded -= SceneLoaded;
        }

        /// <summary>
        /// The collider and terrain census only means anything in a level, and the
        /// game's water kernels only exist once one is loaded. Taking the report
        /// automatically saves having to remember a keypress; F9 still forces one.
        /// </summary>
        private void SceneLoaded(Scene scene, LoadSceneMode mode)
        {
            StartCoroutine(ReportAfterSettling(scene.name));
        }

        private System.Collections.IEnumerator ReportAfterSettling(string sceneName)
        {
            // One frame is not enough: the level's own objects are spawned across
            // several, and a census taken too early counts almost nothing.
            for (int i = 0; i < 120; i++) yield return null;
            Say("==== scene report: " + sceneName + " ====");
            ReportPhysics();
            ReportSceneColliders();
            Say("==== end of scene report ====");
        }

        private void Start()
        {
            Say("==== capability report (load) ====");
            ReportDevice();
            ReportPhysics();
            ReportCpuThroughput();
            ReportBlockPrefabColliders();
            Say("==== end of report. F9 for a scene report. ====");
            StartCoroutine(MaybeLoadRequestedLevel());
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F9))
            {
                Say("==== scene report (F9) ====");
                ReportPhysics();
                ReportSceneColliders();
                ReportSand();
                Say("==== end of scene report ====");
            }
        }

        /// <summary>What the sand field sees: colliders, contacts, forces. Press F9 while
        /// sand is falling on a machine.</summary>
        private void ReportSand()
        {
            SandField field = SandField.Existing;
            if (field == null) { Say("sand field: not created yet"); return; }
            System.Collections.Generic.List<string> lines = field.Describe();
            for (int i = 0; i < lines.Count; i++) Say("sand: " + lines[i]);
        }

        /// <summary>
        /// Launching with <c>-sandprobe-level 2</c> makes the probe load that scene
        /// by itself a few seconds in. The census only means anything in a level,
        /// and driving Besiege's menus from outside the game needs an input
        /// injector this machine has not got.
        /// </summary>
        private System.Collections.IEnumerator MaybeLoadRequestedLevel()
        {
            string wanted = null;
            string[] args = System.Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "-sandprobe-level") wanted = args[i + 1];
            }
            if (wanted == null) yield break;

            // A comma-separated list walks several levels in one run, which is the
            // only way to find out which of them carry water.
            string[] scenes = wanted.Split(',');
            float until = Time.realtimeSinceStartup + 15f;
            while (Time.realtimeSinceStartup < until) yield return null;

            for (int i = 0; i < scenes.Length; i++)
            {
                string scene = scenes[i].Trim();
                if (scene.Length == 0) continue;
                Say("loading scene '" + scene + "' directly; this is not the game's own"
                    + " route into a level, so treat anything odd as an artefact of that");
                SceneManager.LoadScene(scene);

                float next = Time.realtimeSinceStartup + 12f;
                while (Time.realtimeSinceStartup < next) yield return null;
            }
            Say("scripted level walk finished");
        }

        // ------------------------------------------------------------ the device

        private void ReportDevice()
        {
            Say("unity " + Application.unityVersion + ", platform " + (int)Application.platform);
            Say("gpu: " + SystemInfo.graphicsDeviceName + " / " + SystemInfo.graphicsDeviceVendor);
            Say("api: " + SystemInfo.graphicsDeviceVersion
                + ", deviceType " + (int)SystemInfo.graphicsDeviceType
                + ", shaderLevel " + SystemInfo.graphicsShaderLevel);
            Say("supportsInstancing = " + SystemInfo.supportsInstancing);
            Say("supports3DTextures = " + SystemInfo.supports3DTextures
                + ", supportsImageEffects = " + SystemInfo.supportsImageEffects);
            Say("maxTextureSize = " + SystemInfo.maxTextureSize
                + ", processorCount = " + SystemInfo.processorCount
                + ", systemMemory = " + SystemInfo.systemMemorySize + " MB"
                + ", graphicsMemory = " + SystemInfo.graphicsMemorySize + " MB");
            Say("RFloat rendertarget = " + SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.RFloat)
                + ", ARGBFloat = " + SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBFloat)
                + ", ARGBHalf = " + SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBHalf));
        }

        private void ReportPhysics()
        {
            Say("fixedDeltaTime = " + Time.fixedDeltaTime
                + " (" + (1f / Time.fixedDeltaTime) + " Hz)"
                + ", maximumDeltaTime = " + Time.maximumDeltaTime
                + ", timeScale = " + Time.timeScale);
            Say("gravity = " + Physics.gravity
                + ", solverIterations = " + Physics.defaultSolverIterations
                + ", solverVelocityIterations = " + Physics.defaultSolverVelocityIterations
                + ", sleepThreshold = " + Physics.sleepThreshold);
        }



        // ------------------------------------- what the game itself already ships



        // --------------------------------------- what sand would have to collide with

        private void ReportSceneColliders()
        {
            Collider[] all = Object.FindObjectsOfType<Collider>();
            int box = 0, sphere = 0, capsule = 0, meshConvex = 0, meshConcave = 0, terrain = 0, wheel = 0, other = 0;
            int triggers = 0;
            for (int i = 0; i < all.Length; i++)
            {
                Collider c = all[i];
                if (c.isTrigger) triggers++;

                if (c is BoxCollider) box++;
                else if (c is SphereCollider) sphere++;
                else if (c is CapsuleCollider) capsule++;
                else if (c is TerrainCollider) terrain++;
                else if (c is WheelCollider) wheel++;
                else if (c is MeshCollider)
                {
                    MeshCollider m = (MeshCollider)c;
                    if (m.convex) meshConvex++; else meshConcave++;
                }
                else other++;
            }
            Say("colliders in scene: " + all.Length + " (" + triggers + " triggers)");
            Say("  box " + box + ", sphere " + sphere + ", capsule " + capsule
                + ", meshConvex " + meshConvex + ", meshConcave " + meshConcave
                + ", terrain " + terrain + ", wheel " + wheel + ", other " + other);

            Rigidbody[] bodies = Object.FindObjectsOfType<Rigidbody>();
            int awake = 0;
            for (int i = 0; i < bodies.Length; i++) if (!bodies[i].IsSleeping()) awake++;
            Say("rigidbodies: " + bodies.Length + " (" + awake + " awake)");

            Terrain[] terrains = Terrain.activeTerrains;
            Say("active terrains: " + (terrains == null ? 0 : terrains.Length));
            if (terrains != null && terrains.Length > 0 && terrains[0].terrainData != null)
            {
                TerrainData d = terrains[0].terrainData;
                Say("  terrain 0: size " + d.size + ", heightmap " + d.heightmapWidth + "x" + d.heightmapHeight);
            }
        }

        // ------------------------------------------- how much arithmetic Mono gives

        /// <summary>
        /// A crude calibration of this machine's Mono, so the CPU particle budget is
        /// a measurement rather than a guess. Not a solver: a dependent
        /// multiply-add chain over arrays, which is the cheap end of what a contact
        /// iteration costs per particle.
        /// </summary>
        private void ReportCpuThroughput()
        {
            const int n = 200000;
            float[] x = new float[n];
            float[] v = new float[n];
            for (int i = 0; i < n; i++) { x[i] = i * 0.001f; v[i] = 1f; }

            // warm the JIT up before timing it
            Step(x, v, n, 0.02f);

            Stopwatch watch = Stopwatch.StartNew();
            const int passes = 50;
            for (int p = 0; p < passes; p++) Step(x, v, n, 0.02f);
            watch.Stop();

            double msPerPass = watch.ElapsedMilliseconds / (double)passes;
            Say("mono throughput: " + n + " particles x " + passes + " integrate passes in "
                + watch.ElapsedMilliseconds + " ms = " + msPerPass + " ms/pass ("
                + (msPerPass * 1000000.0 / n) + " ns/particle)");
        }

        // --------------------------- the whole point: a kernel this mod wrote itself



        // ------------------------------- what shapes the sand has to collide against

        /// <summary>
        /// Every block the game can place, counted by the collider shapes on it.
        /// This is what the solver has to be handed each step, and the mix decides
        /// whether analytic shapes are enough or a convex hull path is needed. Read
        /// from the prefabs rather than from a machine, so it works at the title
        /// screen with nothing built.
        /// </summary>
        private void ReportBlockPrefabColliders()
        {
            Dictionary<int, BlockPrefab> prefabs = PrefabMaster.BlockPrefabs;
            if (prefabs == null)
            {
                Say("PrefabMaster.BlockPrefabs is null at this point");
                return;
            }

            // foreach over Dictionary.Values is an internal error in Besiege's
            // compiler; copying into a List first is the way round it.
            List<BlockPrefab> all = new List<BlockPrefab>(prefabs.Values);

            int box = 0, sphere = 0, capsule = 0, meshConvex = 0, meshConcave = 0, wheel = 0, other = 0;
            int blocksWithNone = 0, worstCount = 0;
            string worstName = "";
            for (int i = 0; i < all.Count; i++)
            {
                BlockPrefab p = all[i];
                if (p == null || p.gameObject == null) continue;
                Collider[] cols = p.gameObject.GetComponentsInChildren<Collider>(true);
                if (cols.Length == 0) blocksWithNone++;
                if (cols.Length > worstCount) { worstCount = cols.Length; worstName = p.name; }

                for (int c = 0; c < cols.Length; c++)
                {
                    Collider col = cols[c];
                    if (col is BoxCollider) box++;
                    else if (col is SphereCollider) sphere++;
                    else if (col is CapsuleCollider) capsule++;
                    else if (col is WheelCollider) wheel++;
                    else if (col is MeshCollider)
                    {
                        MeshCollider m = (MeshCollider)col;
                        if (m.convex) meshConvex++; else meshConcave++;
                    }
                    else other++;
                }
            }
            Say("block prefabs: " + all.Count + " (" + blocksWithNone + " with no collider at all)");
            Say("  collider shapes across all prefabs: box " + box + ", sphere " + sphere
                + ", capsule " + capsule + ", meshConvex " + meshConvex
                + ", meshConcave " + meshConcave + ", wheel " + wheel + ", other " + other);
            Say("  most colliders on one block: " + worstCount + " on '" + worstName + "'");
        }

        private static void Step(float[] x, float[] v, int n, float dt)
        {
            for (int i = 0; i < n; i++)
            {
                float vi = v[i] - 9.81f * dt;
                float xi = x[i] + vi * dt;
                if (xi < 0f) { xi = 0f; vi = -vi * 0.3f; }
                v[i] = vi;
                x[i] = xi;
            }
        }
    }
}
