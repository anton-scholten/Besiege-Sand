using UnityEngine;

namespace SandMod
{
    /// <summary>
    /// The sprayer as a level object: the same crate, aimed the same way, but driven
    /// by the level instead of by a key.
    ///
    /// A modded event would be the obvious way to switch it on and is the wrong tool:
    /// a modded event's properties have no slider and no colour picker, and it cannot
    /// be aimed at one placed object anyway. Besiege's own <b>Modify Variable</b> event
    /// has a scope picker that names a single entity, so a level variable is how a
    /// trigger reaches this one sprayer and leaves the rest alone.
    ///
    /// The variable is <c>sandspray</c>. Zero or more turns it on; negative means "no
    /// opinion" and hands control back to the SETTINGS tab. That convention exists
    /// because nothing can delete a level variable once it is set -- without a value
    /// reserved for "not mine", a level that took a sprayer over could never give it
    /// back.
    /// </summary>
    public class SprayerEntityBehaviour : MonoBehaviour
    {
        private const string SprayVariable = "sandspray";
        private const string RateVariable = "sandrate";

        // The pipe stands centred on the object, half a unit each way, open end up: grains start just past it.
        private const float NozzleZ = 0.55f;
        private const float MouthRadius = 0.25f;

        private readonly MediaSettings settings = new MediaSettings();
        // GenericEntity and not SaveableDataHolder: the variable API a level uses
        // to drive this object lives on GenericEntity, and only the Add* calls are
        // further up the hierarchy.
        private GenericEntity holder;

        private MSlider rate;
        private MSlider speed;
        private MSlider spread;
        private MToggle onAtStart;

        private bool built;
        private float carry;

        // ---- standing the pipe up. How the level editor turns an entity's mesh could not be worked out from outside the
        // game -- turned -90 about X in its XML the pipe pointed forward, and -180 pointed it sideways -- so it is measured
        // here instead: the pipe's mesh is found, its axis read off its own vertices (its longest extent; the open end is
        // the one with the wider collar), and the mesh turned so that end points along the object's up. The spray leaves
        // along wherever the open end actually points, so the two cannot disagree.
        private MeshFilter pipe;
        private Vector3 openLocal = Vector3.forward;
        private bool stood;
        private int standTries;

        private void StandUp()
        {
            if (++standTries > 300) { stood = true; return; }
            MeshFilter[] all = GetComponentsInChildren<MeshFilter>(true);
            MeshFilter best = null;
            for (int i = 0; i < all.Length; i++)
                if (all[i] != null && all[i].sharedMesh != null && (best == null || all[i].sharedMesh.vertexCount > best.sharedMesh.vertexCount)) best = all[i];
            if (best == null || best.sharedMesh.vertexCount < 100) return;
            pipe = best;

            Vector3[] v = best.sharedMesh.vertices;
            Bounds b = best.sharedMesh.bounds;
            Vector3 e = b.extents;
            int axis = e.x > e.y ? (e.x > e.z ? 0 : 2) : (e.y > e.z ? 1 : 2);
            float lo = b.min[axis], hi = b.max[axis], span = Mathf.Max(hi - lo, 1e-6f);
            // The widest the mesh gets in the top and bottom fifths of its length: the collar is at the open end.
            float wideHi = 0f, wideLo = 0f;
            for (int i = 0; i < v.Length; i++)
            {
                Vector3 off = v[i] - b.center;
                float along = (v[i][axis] - lo) / span;
                off[axis] = 0f;
                float r = off.sqrMagnitude;
                if (along > 0.8f && r > wideHi) wideHi = r;
                if (along < 0.2f && r > wideLo) wideLo = r;
            }
            openLocal = Vector3.zero;
            openLocal[axis] = wideHi >= wideLo ? 1f : -1f;

            // Turned to point up -- only a mesh of its own under the object: the object's own transform is the level's.
            if (best.transform != transform)
            {
                Vector3 now = best.transform.TransformDirection(openLocal).normalized;
                best.transform.rotation = Quaternion.FromToRotation(now, transform.up) * best.transform.rotation;
            }
            stood = true;
        }

        private Vector3 Opening()
        {
            if (pipe == null) return transform.up;
            return pipe.transform.TransformDirection(openLocal).normalized;
        }

        private void Awake() { Build(); }
        private void Start() { Build(); }

        private void Build()
        {
            if (built) return;

            LevelEntity entity = GetComponent<LevelEntity>();
            if (entity == null || entity.EntityBehaviour == null) return;

            holder = entity.EntityBehaviour;
            built = true;

            settings.Build(new EntityHost(holder));

            rate = holder.AddSlider("Rate", "SprayRate", 400f, 0f, 3000f);
            speed = holder.AddSlider("Speed", "SpraySpeed", 8f, 0f, 40f);
            spread = holder.AddSlider("Spread", "SpraySpread", 8f, 0f, 60f);
            onAtStart = holder.AddToggle("On At Start", "SprayOnStart", false);

            settings.MediaPicked += MediaChanged;
            settings.Arm();
        }

        private void MediaChanged(int value)
        {
            switch (value)
            {
                case SandMaterials.WetSand: Apply(300f, 7f, 5f); break;
                case SandMaterials.Dust: Apply(1200f, 14f, 35f); break;
                case SandMaterials.Snow: Apply(600f, 6f, 20f); break;
                case SandMaterials.Gravel: Apply(90f, 10f, 12f); break;
                default: Apply(400f, 8f, 8f); break;
            }
        }

        private void Apply(float perSecond, float metresPerSecond, float degrees)
        {
            rate.Value = perSecond;
            speed.Value = metresPerSecond;
            spread.Value = degrees;
        }

        /// <summary>
        /// Reads a level variable, treating a negative value as no opinion. Returns
        /// false when the level has never set it, or has set it back to negative.
        /// </summary>
        private bool Variable(string key, out float value)
        {
            value = 0f;
            float set;
            if (holder == null) return false;
            if (!holder.GetVariableValue(key, out set)) return false;
            if (set < 0f) return false;
            value = set;
            return true;
        }

        private void Update()
        {
            if (!stood) StandUp();
            if (!built) { Build(); return; }
            if (!StatMaster.levelSimulating) { carry = 0f; return; }

            float variable;
            bool on = Variable(SprayVariable, out variable) ? variable > 0.5f : onAtStart.IsActive;
            if (!on) { carry = 0f; return; }

            float perSecond = rate.Value;
            float overridden;
            if (Variable(RateVariable, out overridden)) perSecond = overridden;

            Emit(perSecond * Time.deltaTime);
        }

        private void Emit(float wanted)
        {
            // At a coarser grain size one grain stands for several, so fewer are made: the
            // rate is a rate of sand, not of grains, and stays what the player set.
            SandField field = SandField.Instance;
            float stands;
            int material = field.Coarsen(settings.Material(), out stands);

            carry += wanted / stands;
            int n = (int)carry;
            if (n <= 0) return;
            carry -= n;
            Color tint = settings.Colour;
            float life = settings.Lifetime;

            // Out of the open end of the pipe, wherever it points (see StandUp): the object's up once it is stood up.
            Transform t = transform;
            Vector3 forward = Opening();
            Vector3 right = Vector3.Cross(forward, Mathf.Abs(forward.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
            Vector3 up = Vector3.Cross(right, forward);
            Vector3 centre = pipe != null ? pipe.transform.position : t.position;
            Vector3 mouth = centre + forward * NozzleZ * Mathf.Max(t.lossyScale.y, 0.1f);

            float tangent = Mathf.Tan(Mathf.Clamp(spread.Value, 0f, 89f) * Mathf.Deg2Rad);

            for (int i = 0; i < n; i++)
            {
                Vector2 disc = Random.insideUnitCircle * MouthRadius;
                Vector3 origin = mouth + right * disc.x + up * disc.y;

                Vector2 cone = Random.insideUnitCircle * tangent;
                Vector3 direction = (forward + right * cone.x + up * cone.y).normalized;

                field.Spawn(origin, direction * speed.Value, material, tint, life);
            }
        }
    }
}
