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

            // The control block exists to bisect a fault, not to be used. Remove it
            // and its XML once the sprayer behaves.
            CustomModules.AddBlockModule<ControlModule, ControlBehaviour>("SandControl", false);

            GameObject go = new GameObject("SandProbe");
            Object.DontDestroyOnLoad(go);
            go.AddComponent<ProbeRunner>();
        }

        // Entity IDs, as the <ID> in each entity's XML. Changing one breaks every
        // saved level that placed it, exactly as a block's does.
        //
        // They are deliberately not 1, 2, 3, 4. OnEntityPrefabCreation is called on
        // EVERY loaded mod's entry point for EVERY entity, and mods filter it with a
        // bare integer -- so two mods that both use id 1 decorate each other's
        // objects. That is where the spotlight lens on a sand region came from:
        // SpecialEffects' SpotLightEntity is id 1 and its hook attached its beam and
        // lens to ours. Ours did the same to its light in return.
        private const int BoxRegionId = 41;
        private const int CylinderRegionId = 42;
        private const int SphereRegionId = 43;
        private const int SprayerEntityId = 44;

        /// <summary>
        /// The only hook a mod has for a level editor object, and it fires on the
        /// prefab rather than on anything placed. Putting a component here is what
        /// gets it onto every instance, because the editor clones the prefab.
        /// </summary>
        public override void OnEntityPrefabCreation(int entityId, GameObject prefab)
        {
            // Logged once per entity so the id another mod is about to collide with is
            // visible in Player.log rather than guessed at from a lens that should not
            // be there.
            Debug.Log("[Sand] entity prefab " + entityId + " '" + prefab.name + "'");

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
