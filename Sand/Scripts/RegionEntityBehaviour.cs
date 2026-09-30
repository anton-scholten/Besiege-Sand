using UnityEngine;

namespace SandMod
{
    /// <summary>
    /// A volume of medium placed in the level editor. Scale it and it holds more: the
    /// amount follows the region's size, and there is no rate, speed, spread or
    /// trigger, because a region does not emit -- it is already full.
    ///
    /// Mod.OnEntityPrefabCreation puts this on the entity prefab, and the editor's own
    /// cloning carries it to every placed object. That is the only per-instance hook a
    /// mod has for an entity: none of the block lifecycle applies here -- no SafeAwake,
    /// no simulation clone, no run callbacks.
    /// </summary>
    public class RegionEntityBehaviour : MonoBehaviour
    {
        /// <summary>Which shape this prefab is. Set by the prefab hook.</summary>
        public int Kind = SandField.RegionBox;

        private readonly MediaSettings settings = new MediaSettings();
        // GenericEntity and not SaveableDataHolder: the variable API a level uses
        // to drive this object lives on GenericEntity, and only the Add* calls are
        // further up the hierarchy.
        private GenericEntity holder;
        private MSlider packing;

        private bool built;
        private bool filled;
        private bool wasSimulating;
        private Renderer[] renderers;

        // Controls are added in Awake *and* Start. LevelXMLLoader.CreateEntity
        // instantiates the entity and loads its saved data in the same breath, while
        // Unity does not run Start until the end of the frame -- so a control added
        // in Start is not there for the saved value to land in, and every setting
        // silently resets on load. EntityBehaviour can still be null in Awake on some
        // paths, hence both, guarded.
        private void Awake() { Build(); }
        private void Start() { Build(); renderers = GetComponentsInChildren<Renderer>(true); }

        private void Build()
        {
            if (built) return;

            LevelEntity entity = GetComponent<LevelEntity>();
            if (entity == null || entity.EntityBehaviour == null) return;

            holder = entity.EntityBehaviour;
            built = true;

            settings.Build(new EntityHost(holder));

            // How tightly the region is packed, as a multiple of the grain diameter.
            // Below 1 the grains start overlapped and shove each other apart, which is
            // a legitimate thing to want and a good way to make a mess.
            packing = holder.AddSlider("Packing", "RegionPacking", 1.1f, 0.6f, 3f);

            settings.Arm();
        }

        private void Update()
        {
            if (!built) { Build(); return; }

            bool simulating = StatMaster.levelSimulating;

            if (simulating && !wasSimulating) Fill();
            if (!simulating && wasSimulating) filled = false;
            wasSimulating = simulating;

            // The region is a marker, not scenery: it is visible while the level is
            // being built and gone while it runs.
            ShowMarker(!simulating);
        }

        private void ShowMarker(bool show)
        {
            if (renderers == null) return;
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] != null) renderers[i].enabled = show;
            }
        }

        private void Fill()
        {
            if (filled) return;
            filled = true;

            SandField field = SandField.Instance;
            int material = settings.Material();

            // The lattice step comes from the grain the region is actually going to
            // hold, so a gravel region places far fewer, far bigger grains than a dust
            // one over the same volume without anybody choosing a number.
            float radius = field.Materials[material].radius;
            float spacing = Mathf.Max(radius * 2f * packing.Value, 0.004f);

            Vector3 half = transform.lossyScale * 0.5f;
            int added = field.FillRegion(Kind, transform.position, transform.rotation,
                                         half, material, settings.Colour, spacing,
                                         settings.Lifetime);

            Debug.Log("[Sand] region filled with " + added + " grains");
        }
    }
}
