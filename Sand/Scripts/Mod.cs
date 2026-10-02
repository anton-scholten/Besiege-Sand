using Modding;
using Modding.Modules;
using UnityEngine;

namespace SandMod
{
    /// <summary>
    /// Entry point. Registers the sprayer block's module and puts the capability
    /// probe in the scene; the medium itself is created on demand by whatever first
    /// asks <see cref="SandField.Instance"/> for it.
    /// </summary>
    public class Mod : ModEntryPoint
    {
        public override void OnLoad()
        {
            // This name, the element in Sprayer.xml and the [XmlRoot] on the module
            // all have to agree, and nothing says so if they do not: the block simply
            // never appears.
            CustomModules.AddBlockModule<SprayerModule, SprayerBehaviour>("Sprayer", false);

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

            GameObject go = new GameObject("SandProbe");
            Object.DontDestroyOnLoad(go);
            go.AddComponent<ProbeRunner>();

            // The sand panel and its brush, on the screen while building and while running.
            GameObject hud = new GameObject("SandHud");
            Object.DontDestroyOnLoad(hud);
            hud.AddComponent<SandHud>();

            // The sprayer's settings panel, docked under the block mapper while a sprayer's
            // is open. Needs UI Factory; without it this does nothing and the stock mapper
            // shows everything.
            GameObject panels = new GameObject("SandPanels");
            Object.DontDestroyOnLoad(panels);
            panels.AddComponent<SprayerPanel>();
        }

        // Entity IDs, as the <ID> in each entity's XML. Changing one breaks every
        // saved level that placed it, exactly as a block's does.
        private const int BoxRegionId = 1;
        private const int CylinderRegionId = 2;
        private const int SphereRegionId = 3;
        private const int SprayerEntityId = 4;

        /// <summary>
        /// The only hook a mod has for a level editor object, and it fires on the
        /// prefab rather than on anything placed. Putting a component here is what
        /// gets it onto every instance, because the editor clones the prefab.
        /// </summary>
        public override void OnEntityPrefabCreation(int entityId, GameObject prefab)
        {
            switch (entityId)
            {
                case BoxRegionId:
                    Region(prefab, SandField.RegionBox);
                    break;
                case CylinderRegionId:
                    Region(prefab, SandField.RegionCylinder);
                    break;
                case SphereRegionId:
                    Region(prefab, SandField.RegionSphere);
                    break;
                case SprayerEntityId:
                    if (prefab.GetComponent<SprayerEntityBehaviour>() == null)
                        prefab.AddComponent<SprayerEntityBehaviour>();
                    break;
            }
        }

        /// <summary>
        /// Puts the region component on a prefab, once. The hook can fire more than
        /// once across a session, and a second component would fill the region twice.
        /// </summary>
        private static void Region(GameObject prefab, int kind)
        {
            RegionEntityBehaviour region = prefab.GetComponent<RegionEntityBehaviour>();
            if (region == null) region = prefab.AddComponent<RegionEntityBehaviour>();
            region.Kind = kind;
        }
    }
}
