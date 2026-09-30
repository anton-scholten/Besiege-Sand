// Builds the mod's shader bundles. Run from the command line, never by hand:
//
//   Unity.exe -batchmode -quit -nographics -projectPath <shaders> \
//             -executeMethod SandShaderBuild.BuildAll -logFile <log>
//
// One bundle per graphics API, because a compiled shader is per API and a bundle
// carries only the variants its build target asked for. Besiege on Linux is an
// OpenGL build, so StandaloneLinux64 is the bundle it wants -- the notes repo's
// older advice was to ship the Mac bundle, which works because it is also OpenGL,
// but Linux target support for 5.4.0f3 exists and saying so outright is better
// than relying on the coincidence.

using System.IO;
using UnityEditor;
using UnityEngine;

public static class SandShaderBuild
{
    private const string BundleName = "sandcompute";

    // Where the built bundles land. Derived from Application.dataPath (which is
    // <project>/Assets) rather than written as a relative path: in batch mode the
    // process working directory is wherever the editor was launched from, not the
    // project, so a relative path here quietly writes the bundles somewhere else.
    private static string OutRoot
    {
        get
        {
            string project = Directory.GetParent(Application.dataPath).FullName;
            return Path.GetFullPath(Path.Combine(project, "../Sand/Resources/SandCompute"));
        }
    }

    private static readonly string[] AssetPaths =
    {
        "Assets/SandShaders/SandProbe.compute",
    };

    public static void BuildAll()
    {
        foreach (string path in AssetPaths)
        {
            AssetImporter importer = AssetImporter.GetAtPath(path);
            if (importer == null)
            {
                Debug.LogError("[SandShaderBuild] no asset at " + path);
                EditorApplication.Exit(1);
                return;
            }
            importer.assetBundleName = BundleName;
            importer.SaveAndReimport();
        }

        // Linux is the one that must work here: Besiege on Linux is an OpenGL
        // build. The others are built if their target support is installed and
        // skipped with a note if it is not -- a missing playback engine should not
        // cost the bundle that matters.
        int built = 0;
        if (Build(BuildTarget.StandaloneLinux64, "linux")) built++;
        if (Build(BuildTarget.StandaloneWindows64, "win")) built++;
        if (Build(BuildTarget.StandaloneOSXUniversal, "mac")) built++;

        Debug.Log("[SandShaderBuild] output root: " + OutRoot);
        Debug.Log("[SandShaderBuild] done; " + built + " of 3 targets built");
        EditorApplication.Exit(built > 0 ? 0 : 1);
    }

    private static bool Build(BuildTarget target, string folder)
    {
        string dir = Path.Combine(OutRoot, folder);
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

        AssetBundleManifest manifest = null;
        try
        {
            manifest = BuildPipeline.BuildAssetBundles(
                dir, BuildAssetBundleOptions.None, target);
        }
        catch (System.Exception e)
        {
            Debug.LogError("[SandShaderBuild] " + target + " threw: " + e.Message);
            return false;
        }

        if (manifest == null)
        {
            Debug.LogError("[SandShaderBuild] BuildAssetBundles returned null for "
                + target + " -- is that target's support installed?");
            return false;
        }
        Debug.Log("[SandShaderBuild] built " + target + " -> " + dir);
        return true;
    }
}
