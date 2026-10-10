using Modding;
using Modding.Modules;
using UnityEngine;

namespace SandMod
{
    /// <summary>
    /// Entry point. Puts the sand panel in the scene; the medium itself is created on demand by whatever first
    /// asks <see cref="SandField.Instance"/> for it.
    /// </summary>
    public class Mod : ModEntryPoint
    {
        public override void OnLoad()
        {
            // Multiplayer messages are made at load or not at all.
            SandNet.Init();

            // The sand is saved with a level in the editor, and comes back with it.
            // Guarded: these run inside the game's own saving and loading, and anything thrown
            // here would be thrown there.
            Events.OnLevelSave += delegate (Modding.Levels.Level level)
            {
                try { SandField.SaveToLevel(level); }
                catch (System.Exception e) { Debug.LogWarning("[Sand] saving the sand with the level failed: " + e); }
            };
            Events.OnLevelLoaded += delegate (Modding.Levels.Level level)
            {
                try { SandField.LoadFromLevel(level); }
                catch (System.Exception e) { Debug.LogWarning("[Sand] reading the level's sand failed: " + e); }
            };

            // The sand on its own, saved and loaded through the game's own screens.
            SandFiles.Init();

            // The sand panel and its brush, on the screen while building and while running.
            GameObject hud = new GameObject("SandHud");
            Object.DontDestroyOnLoad(hud);
            hud.AddComponent<SandHud>();
        }
    }
}
