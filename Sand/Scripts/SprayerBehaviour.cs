using Modding.Modules;
using UnityEngine;

namespace SandMod
{
    /// <summary>
    /// The sprayer: a crate that throws granular medium out of its open face.
    ///
    /// The media selector presets the numbers and then gets out of the way -- picking
    /// Gravel writes gravel's rate, speed and spread into the sliders, and the player
    /// is free to edit any of them afterwards. Presets are only applied to a change
    /// the player made: loading a machine also raises ValueChanged, and clobbering a
    /// saved setting on load would be indistinguishable from the setting not saving.
    /// </summary>
    public class SprayerBehaviour : BlockModuleBehaviour<SprayerModule>
    {
        // The end of the pipe in block space: it stands from z = 0 to 1 and its bore is 0.63 across, so
        // grains start just past its mouth, never inside the block's own collider, and inside the bore's width.
        private const float NozzleZ = 1.05f;
        private const float MouthRadius = 0.25f;

        /// <summary>Media picker, colour and the advanced physical settings.</summary>
        private readonly MediaSettings settings = new MediaSettings();

        private MSlider rate;
        private MSlider speed;
        private MSlider spread;
        private MKey trigger;
        private MToggle toggleMode;
        private bool toggledOn;       // in toggle mode: spraying since the last press

        /// <summary>False until the block has lived a frame, so that the values a
        /// machine load writes are not treated as the player choosing a medium.</summary>
        private bool presetsArmed;

        private float carry;          // fractional grains owed from the last frame
        private bool emulatedHeld;
        private bool emulatedPress;

        public override void SafeAwake()
        {
            settings.Build(this);

            rate = AddSlider("Rate", "SprayRate", 400f, 0f, 3000f);
            speed = AddSlider("Speed", "SpraySpeed", 8f, 0f, 40f);
            spread = AddSlider("Spread", "SpraySpread", 8f, 0f, 60f);

            // Only MKey carries emulation, so the trigger has to be a key if the
            // sprayer is ever to be automated.
            trigger = AddKey("Spray", "SprayKey", KeyCode.C);
            // Toggle: a press starts the spray and the next stops it, rather than spraying while held.
            toggleMode = AddToggle("Toggle", "SprayToggle", false);

            settings.MediaPicked += MediaChanged;
        }

        public override void BuildingUpdate()
        {
            // BuildingUpdate only runs while nothing is simulating, which makes it a
            // safe place to decide the block has finished being loaded.
            presetsArmed = true;
            settings.Arm();
        }

        /// <summary>
        /// Writes a medium's own defaults into the sliders. Preset, not lock: the
        /// player can move any of them afterwards and the choice sticks.
        /// </summary>
        private void MediaChanged(int value)
        {
            if (!presetsArmed) return;

            switch (value)
            {
                case SandMaterials.WetSand:
                    Apply(300f, 7f, 5f); break;
                case SandMaterials.Dust:
                    // Thin and fast and wide: a dust cloud wants many small grains
                    // with somewhere to go.
                    Apply(1200f, 14f, 35f); break;
                case SandMaterials.Snow:
                    Apply(600f, 6f, 20f); break;
                case SandMaterials.Mud:
                    Apply(350f, 7f, 8f); break;
                case SandMaterials.Glue:
                    // Big blobs: fewer of them for the same volume.
                    Apply(200f, 6f, 6f); break;
                case SandMaterials.Gravel:
                    // Gravel grains are big, so the same volume is far fewer of them.
                    Apply(90f, 10f, 12f); break;
                default:
                    Apply(400f, 8f, 8f); break;
            }
        }

        private void Apply(float perSecond, float metresPerSecond, float degrees)
        {
            rate.Value = perSecond;
            speed.Value = metresPerSecond;
            spread.Value = degrees;
        }

        // ------------------------------------------------------------------ the spray

        /// <summary>
        /// Emulated presses do not arrive in Update: they are raised in Besiege's own
        /// emulation pass, once per fixed step. Latching them here and consuming them
        /// in the frame update is what makes an automated sprayer fire exactly as
        /// often as it was told to.
        /// </summary>
        public override void KeyEmulationUpdate()
        {
            // On a multiplayer client the block is built without its mapper controls, and
            // this still runs (notes repository, 03-keys-and-automation).
            if (trigger == null) return;
            emulatedPress |= trigger.EmulationPressed();
            emulatedHeld = trigger.EmulationHeld(true);
        }

        public override void SimulateUpdateAlways()
        {
            if (trigger == null) return;
            bool pressed = trigger.IsPressed || emulatedPress;
            bool held = trigger.IsHeld || emulatedHeld || pressed;
            emulatedPress = false;
            if (toggleMode != null && toggleMode.IsActive)
            {
                if (pressed) toggledOn = !toggledOn;
                held = toggledOn;
            }
            if (!held) { carry = 0f; return; }

            Emit(rate.Value * Time.deltaTime);
        }

        /// <summary>
        /// Puts <paramref name="wanted"/> grains into the field, carrying the fraction
        /// over so a low rate still emits evenly instead of rounding to nothing.
        /// </summary>
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

            Transform t = transform;
            Vector3 forward = t.forward;
            Vector3 right = t.right;
            Vector3 up = t.up;
            Vector3 mouth = t.position + forward * NozzleZ;

            // A sprayer carried on a moving machine should throw its medium forward
            // from where the machine already is, not from a standing start.
            Rigidbody body = Rigidbody;
            Vector3 carried = body != null ? body.GetPointVelocity(mouth) : Vector3.zero;

            float tangent = Mathf.Tan(Mathf.Clamp(spread.Value, 0f, 89f) * Mathf.Deg2Rad);

            for (int i = 0; i < n; i++)
            {
                Vector2 disc = Random.insideUnitCircle * MouthRadius;
                Vector3 origin = mouth + right * disc.x + up * disc.y;

                Vector2 cone = Random.insideUnitCircle * tangent;
                Vector3 direction = (forward + right * cone.x + up * cone.y).normalized;

                field.Spawn(origin, carried + direction * speed.Value, material, tint, life);
            }
        }
    }
}
