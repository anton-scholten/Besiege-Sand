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

        /// <summary>
        /// The size of the object at scale 1, in metres, as the entity XML draws it: a box is a
        /// slab ten metres square and one tall, a cylinder and a sphere three across; each is
        /// raised half its height, so its origin is the middle of its base. A one-metre region
        /// was too small to drive onto.
        /// </summary>
        public Vector3 Unit
        {
            get { return Kind == SandField.RegionBox ? new Vector3(10f, 1f, 10f) : new Vector3(3f, 3f, 3f); }
        }

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
            if (!simulating) Tint(settings.Colour);
        }

        private Color shownColour = new Color(-1f, -1f, -1f, -1f);
        private MaterialPropertyBlock tint;

        /// <summary>
        /// The marker drawn in its medium's colour -- the colour the sand will be, which follows
        /// the medium unless the colour control has been moved -- through a property block, so
        /// the shared material every region uses is left alone. Only when it changes.
        /// </summary>
        private void Tint(Color colour)
        {
            if (renderers == null || colour == shownColour) return;
            shownColour = colour;
            if (tint == null) tint = new MaterialPropertyBlock();
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer r = renderers[i];
                if (r == null) continue;
                Color c = colour;
                Material m = r.sharedMaterial;
                if (m != null && m.HasProperty("_Color")) c.a = m.color.a;
                r.GetPropertyBlock(tint);
                tint.SetColor("_Color", c);
                r.SetPropertyBlock(tint);
            }
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

            // The object's origin is the middle of its base (the entity XML raises the mesh and
            // the collider half a unit), so the region's centre is half a unit up in its own
            // frame -- which, scaled and turned with it, is half its height along its own up.
            Vector3 unit = Unit;
            Vector3 half = Vector3.Scale(transform.lossyScale, unit) * 0.5f;
            Vector3 centre = transform.TransformPoint(new Vector3(0f, unit.y * 0.5f, 0f));
            // Which region, where and of what: a run whose sand was kept leaves this one alone
            // next time unless it has been moved, resized or changed since. By shape and place
            // rather than by object, so it does not matter whether the editor hands the next run
            // the same object or a copy.
            long key = Kind + 1;
            key = key * 31 + Mathf.RoundToInt(centre.x * 20f);
            key = key * 31 + Mathf.RoundToInt(centre.y * 20f);
            key = key * 31 + Mathf.RoundToInt(centre.z * 20f);
            key = key * 31 + Mathf.RoundToInt(half.x * 40f);
            key = key * 31 + Mathf.RoundToInt(half.y * 40f);
            key = key * 31 + Mathf.RoundToInt(half.z * 40f);
            key = key * 31 + Mathf.RoundToInt(transform.rotation.eulerAngles.y);
            key = key * 31 + material;
            if (key == 0) key = 1;

            field.FillRegion(Kind, centre, transform.rotation,
                                         half, material, settings.Colour, spacing,
                                         settings.Lifetime, key);
        }
    }
}
