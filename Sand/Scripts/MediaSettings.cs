using System.Collections.Generic;
using UnityEngine;

namespace SandMod
{
    /// <summary>
    /// The media picker and its advanced settings, built once and used by both the
    /// sprayer block and the level editor's region objects.
    ///
    /// One class serves both because <c>AddSlider</c> and the rest are declared on
    /// <c>SaveableDataHolder</c>, which a block's <c>BlockBehaviour</c> and a level
    /// object's <c>GenericEntity</c> both derive from. So the SETTINGS tab of a level
    /// object takes the identical panel a block's mapper does, and neither has to
    /// know about the other.
    ///
    /// The advanced sliders do not edit the medium for everyone. Opening them claims
    /// a variant row seeded from the chosen medium, so two sprayers can throw sand of
    /// different coarseness in the same level and neither changes what the other
    /// pours.
    /// </summary>
    public class MediaSettings
    {
        private IMediaHost holder;

        private MMenu media;
        private MToggle advanced;
        private MColourSlider colour;

        private MSlider radius;
        private MSlider density;
        private MSlider friction;
        private MSlider cohesion;
        private MSlider restitution;
        private MSlider airDrag;
        private MSlider waterDrag;
        private MSlider packing;
        private MSlider wetCohesion;
        private MSlider wetTime;
        private MSlider stickiness;
        private MSlider firmness;

        private MSlider lifetime;
        private MSlider[] advancedSliders;

        /// <summary>Ninety minutes. Long enough that medium is effectively permanent
        /// for a run, short enough that a level left running does not fill up.</summary>
        public const float DefaultLifetime = 5400f;
        public const float MinLifetime = 0.01f;

        /// <summary>The row this settings panel pours. Either one of the media,
        /// or a variant of it while the advanced settings are open.</summary>
        private int variantSlot = -1;

        private bool armed;
        private bool dirty = true;

        /// <summary>Raised when the player picks a different medium, so the owner can
        /// preset whatever else it has -- the sprayer's rate, speed and spread.</summary>
        public delegate void MediaHandler(int material);
        public event MediaHandler MediaPicked;

        public Color Colour { get { return colour.Value; } }

        /// <summary>Seconds a grain lives. Clamped on read rather than on the slider,
        /// so a typed value survives the save and a nonsensical one still cannot
        /// produce a grain that never dies.</summary>
        public float Lifetime
        {
            get { return Mathf.Clamp(lifetime.Value, MinLifetime, DefaultLifetime); }
        }
        public int BaseMedia { get { return Mathf.Clamp(media.Value, 0, SandMaterials.Count - 1); } }

        /// <summary>
        /// Arms the presets. Until this is called a value arriving from a machine or
        /// level load is not treated as the player choosing anything, which is what
        /// stops a load overwriting the settings it just restored.
        /// </summary>
        public void Arm() { armed = true; }

        public void Build(IMediaHost owner)
        {
            holder = owner;

            List<string> names = new List<string>();
            for (int i = 0; i < SandMaterials.Count; i++) names.Add(SandMaterials.Names[i]);

            media = holder.Menu("Media", SandMaterials.Sand, names, true);

            // The colour starts as the medium's own, and follows it when the medium
            // changes: picking Snow should make the swatch white, not leave it
            // showing the last medium's sand.
            colour = holder.ColourSlider("Colour", "MediaColour",
                                         SandMaterials.Tints[SandMaterials.Sand], false);
            advanced = holder.Toggle("Advanced", "MediaAdvanced", false);

            // The track covers the range worth dragging; the value may be typed
            // anywhere from a hundredth of a second to ninety minutes, which is why
            // this one is unclamped. Always seconds, never minutes, so a typed number
            // means one thing.
            lifetime = holder.SliderUnclamped("Lifetime", "MediaLifetime",
                                              DefaultLifetime, 0.5f, 100f);

            // Ranges are wide enough to be silly with on purpose: the interesting
            // settings for a sand mod are the ones outside what a real material does.
            radius = holder.Slider("Grain Radius", "MediaRadius", 0.03f, 0.004f, 0.25f);
            density = holder.Slider("Density", "MediaDensity", 1600f, 50f, 8000f);
            friction = holder.Slider("Friction", "MediaFriction", 0.6f, 0f, 2f);
            cohesion = holder.Slider("Cohesion", "MediaCohesion", 0f, 0f, 40f);
            restitution = holder.Slider("Bounce", "MediaBounce", 0.1f, 0f, 0.9f);
            airDrag = holder.Slider("Air Drag", "MediaAirDrag", 0.2f, 0f, 20f);
            waterDrag = holder.Slider("Water Drag", "MediaWaterDrag", 6f, 0f, 30f);
            packing = holder.Slider("Packing", "MediaPacking", 0f, 0f, 20f);
            wetCohesion = holder.Slider("Wet Cohesion", "MediaWetCohesion", 14f, 0f, 60f);
            wetTime = holder.Slider("Wet Time", "MediaWetTime", 6f, 0f, 60f);
            // How hard grains cling to a machine, and how hard the settled medium is to press
            // a rut into, in kilopascals a metre.
            stickiness = holder.Slider("Stickiness", "MediaStickiness", 0f, 0f, 150f);
            firmness = holder.Slider("Firmness", "MediaFirmness", 50f, 5f, 600f);

            advancedSliders = new MSlider[]
            {
                radius, density, friction, cohesion, restitution,
                airDrag, waterDrag, packing, wetCohesion, wetTime, stickiness, firmness
            };

            media.ValueChanged += OnMediaChanged;
            advanced.Toggled += OnAdvancedToggled;
            for (int i = 0; i < advancedSliders.Length; i++)
                advancedSliders[i].ValueChanged += OnAdvancedSliderChanged;

            ShowAdvanced(advanced.IsActive);
            LoadSlidersFrom(BaseMedia);
        }

        /// <summary>
        /// Sets visibility of the advanced rows. Safe to call every frame on a level
        /// object -- <c>DisplayInMapper</c>'s setter returns early when the value has
        /// not changed -- but a block should not churn it, so this is only called on
        /// an actual change.
        /// </summary>
        private void ShowAdvanced(bool show)
        {
            // Through Controls, which stands down while the sprayer's own panel is drawing the
            // block: that panel has taken these off the stock mapper and shows them itself.
            Controls.Show(show, advancedSliders);
        }

        private void OnAdvancedToggled(bool value)
        {
            ShowAdvanced(value);
            dirty = true;
        }

        private void OnAdvancedSliderChanged(float value)
        {
            dirty = true;
        }

        private void OnMediaChanged(int value)
        {
            if (!armed) return;

            // Picking a medium re-seeds the advanced numbers from it, so the panel
            // always shows what the chosen medium actually is before it is edited.
            LoadSlidersFrom(value);
            colour.Value = SandMaterials.Tints[Mathf.Clamp(value, 0, SandMaterials.Count - 1)];
            dirty = true;

            if (variantSlot >= 0) SandField.Instance.ReseatVariant(variantSlot, value);
            if (MediaPicked != null) MediaPicked(value);
        }

        /// <summary>Copies a medium's own numbers into the advanced sliders.</summary>
        private void LoadSlidersFrom(int material)
        {
            SandMaterials.Params[] table = SandField.Instance.Materials;
            SandMaterials.Params p = table[Mathf.Clamp(material, 0, SandMaterials.Count - 1)];

            radius.Value = p.radius;
            // The table keeps inverse mass because that is what the solver wants; the
            // slider shows density, because that is what a person wants.
            density.Value = DensityOf(p);
            friction.Value = p.friction;
            cohesion.Value = p.cohesion;
            restitution.Value = p.restitution;
            airDrag.Value = p.airDrag;
            waterDrag.Value = p.waterDrag;
            packing.Value = p.packGain;
            wetCohesion.Value = p.wetCohesion;
            wetTime.Value = p.wetSeconds;
            stickiness.Value = p.adhesion;
            firmness.Value = p.firmness / 1000f;
        }

        private static float DensityOf(SandMaterials.Params p)
        {
            float volume = 4f / 3f * Mathf.PI * p.radius * p.radius * p.radius;
            return 1f / Mathf.Max(p.invMass, 1e-9f) / Mathf.Max(volume, 1e-9f);
        }

        /// <summary>
        /// The row to pour. Cheap to call every emission: the variant is only rewritten
        /// when something actually moved.
        /// </summary>
        public int Material()
        {
            int baseMedia = BaseMedia;
            if (!advanced.IsActive)
            {
                dirty = false;
                return baseMedia;
            }

            SandField field = SandField.Instance;
            if (variantSlot < 0)
            {
                variantSlot = field.ClaimVariant(baseMedia);
                if (variantSlot < SandMaterials.Count) return variantSlot;  // table full
                dirty = true;
            }

            if (dirty)
            {
                field.WriteVariant(variantSlot, Row(baseMedia));
                dirty = false;
            }
            return variantSlot;
        }

        /// <summary>Builds a material row from the advanced sliders.</summary>
        private SandMaterials.Params Row(int baseMedia)
        {
            SandMaterials.Params p = new SandMaterials.Params();
            p.radius = Mathf.Max(radius.Value, 0.001f);

            float volume = 4f / 3f * Mathf.PI * p.radius * p.radius * p.radius;
            p.invMass = 1f / Mathf.Max(volume * Mathf.Max(density.Value, 1f), 1e-6f);

            p.friction = friction.Value;
            p.cohesion = cohesion.Value;
            p.restitution = restitution.Value;
            p.airDrag = airDrag.Value;
            p.waterDrag = waterDrag.Value;
            p.buoyancy = 1000f / Mathf.Max(density.Value, 1f);
            p.packGain = packing.Value;
            p.wetCohesion = wetCohesion.Value;
            p.wetSeconds = wetTime.Value;
            p.adhesion = stickiness.Value;
            p.firmness = Mathf.Max(firmness.Value, 1f) * 1000f;

            // No sliders for these two: they follow the medium.
            SandMaterials.Params from = SandField.Instance.Materials[Mathf.Clamp(baseMedia, 0, SandMaterials.Count - 1)];
            p.packLimit = from.packLimit;
            p.digSpeed = from.digSpeed;

            // A variant of dry sand still turns into wet sand; a variant of anything
            // else keeps its own row, exactly as the medium it came from does.
            p.wetMaterial = baseMedia == SandMaterials.Sand ? SandMaterials.WetSand : -1;
            return p;
        }
    }
}
