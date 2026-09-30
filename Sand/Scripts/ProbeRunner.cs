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
            ReportShippedComputeShaders();
            ReportGameComputeKernels();
            ReportSceneColliders();
            Say("==== end of scene report ====");
        }

        private void Start()
        {
            Say("==== capability report (load) ====");
            ReportDevice();
            ReportPhysics();
            ReportComputeBuffer();
            ReportRandomWriteTarget();
            ReportShippedComputeShaders();
            ReportCpuThroughput();
            ReportBlockPrefabColliders();
            ReportModComputeShader();
            Say("==== end of report. F9 for a scene report. ====");
            StartCoroutine(MaybeLoadRequestedLevel());
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F9))
            {
                Say("==== scene report (F9) ====");
                ReportPhysics();
                ReportShippedComputeShaders();
                ReportGameComputeKernels();
                ReportSceneColliders();
                Say("==== end of scene report ====");
            }
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
            // only way to find out which of them carry water -- and so which carry
            // the game's own compute kernels.
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
            Say("supportsComputeShaders = " + SystemInfo.supportsComputeShaders);
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

        // ------------------------------------------- can we hold state on the GPU

        private void ReportComputeBuffer()
        {
            ComputeBuffer buffer = null;
            try
            {
                buffer = new ComputeBuffer(4096, 16);
                Vector4[] up = new Vector4[4096];
                for (int i = 0; i < up.Length; i++) up[i] = new Vector4(i, i * 2f, 0f, 1f);
                buffer.SetData(up);

                Vector4[] down = new Vector4[4096];
                buffer.GetData(down);

                bool ok = down[0].x == 0f && down[7].y == 14f && down[4095].x == 4095f;
                Say("ComputeBuffer(4096,16) SetData/GetData round-trip: " + (ok ? "OK" : "MISMATCH"));

                Stopwatch watch = Stopwatch.StartNew();
                for (int i = 0; i < 100; i++) buffer.GetData(down);
                watch.Stop();
                Say("GetData readback of 64 KB: " + (watch.ElapsedMilliseconds / 100f) + " ms each");
            }
            catch (System.Exception e)
            {
                Say("ComputeBuffer FAILED: " + e.Message);
            }
            finally
            {
                if (buffer != null) buffer.Release();
            }
        }

        /// <summary>
        /// A compute shader writes into a RenderTexture with random write enabled.
        /// If that texture cannot even be created, the compute route is closed
        /// before any shader is written.
        /// </summary>
        private void ReportRandomWriteTarget()
        {
            RenderTexture rt = null;
            try
            {
                rt = new RenderTexture(256, 256, 0, RenderTextureFormat.ARGBFloat);
                rt.enableRandomWrite = true;
                bool created = rt.Create();
                Say("RenderTexture ARGBFloat 256x256 enableRandomWrite: created = " + created
                    + ", enableRandomWrite reads back as " + rt.enableRandomWrite);
            }
            catch (System.Exception e)
            {
                Say("random-write RenderTexture FAILED: " + e.Message);
            }
            finally
            {
                if (rt != null)
                {
                    rt.Release();
                    Object.Destroy(rt);
                }
            }
        }

        // ------------------------------------- what the game itself already ships

        private void ReportShippedComputeShaders()
        {
            ComputeShader[] shaders = Resources.FindObjectsOfTypeAll<ComputeShader>();
            Say("ComputeShader assets loaded right now: " + shaders.Length);
            for (int i = 0; i < shaders.Length && i < 20; i++)
            {
                Say("  compute shader: " + shaders[i].name);
            }
        }

        /// <summary>
        /// Besiege's water is CodeAnimo Surface Waves, which wraps every dispatch in
        /// a ComputeKernel with its own support test, and falls back to an SM3
        /// fragment-shader kernel when compute is unavailable. Which of the two is
        /// live in a water level is the most direct evidence there is about whether
        /// compute actually runs on this build.
        /// </summary>
        private void ReportGameComputeKernels()
        {
            CodeAnimo.GPGPU.ComputeKernel[] compute =
                Resources.FindObjectsOfTypeAll<CodeAnimo.GPGPU.ComputeKernel>();
            Say("CodeAnimo ComputeKernel instances in scene: " + compute.Length);
            for (int i = 0; i < compute.Length && i < 20; i++)
            {
                CodeAnimo.GPGPU.ComputeKernel k = compute[i];
                Say("  kernel '" + k.kernelName + "' on " + k.name
                    + ": SupportedBySystem = " + k.SupportedBySystem()
                    + ", kernelFound = " + k.kernelFound
                    + ", shader = " + (k.simulationShader == null ? "null" : k.simulationShader.name));
            }

            CodeAnimo.GPGPU.SM3Kernel[] sm3 = Resources.FindObjectsOfTypeAll<CodeAnimo.GPGPU.SM3Kernel>();
            Say("CodeAnimo SM3Kernel (fragment-shader fallback) instances: " + sm3.Length);
        }

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

        /// <summary>
        /// The one thing the rest of the report cannot settle. The runtime supports
        /// compute; what is unproven is whether a kernel a *mod* authored survives
        /// the trip through a Unity 5.4 asset bundle and Modding.ModResource, and
        /// dispatches. The kernel writes values that can be predicted exactly, so a
        /// buffer that was never touched cannot be mistaken for success.
        /// </summary>
        private void ReportModComputeShader()
        {
            Modding.ModAssetBundle bundle = PickComputeBundle();
            if (bundle == null)
            {
                Say("mod compute bundle: not present -- build it with"
                    + " tools/build-bundles.sh and list it in Mod.xml");
                return;
            }

            ComputeShader[] found = bundle.AssetBundle.LoadAllAssets<ComputeShader>();
            Say("mod compute bundle: " + found.Length + " ComputeShader asset(s) inside");
            if (found.Length == 0) return;

            ComputeShader shader = found[0];
            ComputeBuffer buffer = null;
            try
            {
                int kernel = shader.FindKernel("CSMain");
                Say("  kernel 'CSMain' index = " + kernel + " in '" + shader.name + "'");

                const int count = 4096;
                buffer = new ComputeBuffer(count, 16);
                shader.SetBuffer(kernel, "Result", buffer);
                shader.SetVector("_Params", new Vector4(42f, 0f, 0f, 0f));
                shader.Dispatch(kernel, count / 64, 1, 1);

                Vector4[] got = new Vector4[count];
                buffer.GetData(got);

                bool ok = got[7].x == 7f && got[7].y == 14f && got[7].z == 42f
                          && got[count - 1].x == (count - 1);
                Say("  DISPATCH RESULT: " + (ok ? "OK -- the GPU ran a mod's own kernel" : "WRONG")
                    + "; element 7 = " + got[7]);

                // The earlier readback timing was taken with nothing dispatched in
                // front of it, which measures nothing. This one has to wait for the
                // GPU, so it is the number that matters to a coupled simulation.
                Stopwatch watch = Stopwatch.StartNew();
                const int rounds = 50;
                for (int i = 0; i < rounds; i++)
                {
                    shader.Dispatch(kernel, count / 64, 1, 1);
                    buffer.GetData(got);
                }
                watch.Stop();
                Say("  dispatch + 64 KB readback round trip: "
                    + (watch.ElapsedMilliseconds / (float)rounds) + " ms each");
            }
            catch (System.Exception e)
            {
                Say("  mod compute shader FAILED: " + e.Message);
            }
            finally
            {
                if (buffer != null) buffer.Release();
            }
        }

        /// <summary>
        /// A compiled shader is per graphics API, so the mod ships one bundle each
        /// and picks by what the player is actually running -- falling back through
        /// the others, because a shader that did not compile for this API loads
        /// perfectly and then draws or computes nothing.
        /// </summary>
        private Modding.ModAssetBundle PickComputeBundle()
        {
            string[] order;
            if (Application.platform == RuntimePlatform.WindowsPlayer)
                order = new string[] { "SandComputeWin", "SandComputeLinux", "SandComputeMac" };
            else if (Application.platform == RuntimePlatform.OSXPlayer)
                order = new string[] { "SandComputeMac", "SandComputeLinux", "SandComputeWin" };
            else
                order = new string[] { "SandComputeLinux", "SandComputeMac", "SandComputeWin" };

            for (int i = 0; i < order.Length; i++)
            {
                Modding.ModAssetBundle bundle = null;
                try
                {
                    bundle = Modding.ModResource.GetAssetBundle(order[i]);
                }
                catch (System.Exception e)
                {
                    Say("  bundle '" + order[i] + "' not loadable: " + e.Message);
                    continue;
                }
                if (bundle == null || bundle.HasError || bundle.AssetBundle == null)
                {
                    Say("  bundle '" + order[i] + "' unusable"
                        + (bundle != null && bundle.HasError ? ": " + bundle.Error : ""));
                    continue;
                }
                Say("  using bundle '" + order[i] + "'");
                return bundle;
            }
            return null;
        }

        // ------------------------------- what shapes the sand has to collide against

        /// <summary>
        /// Every block the game can place, counted by the collider shapes on it.
        /// This is what a GPU solver has to be handed each step, and the mix decides
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
