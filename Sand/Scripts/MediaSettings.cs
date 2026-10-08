using System.Collections.Generic;
using UnityEngine;

namespace SandMod
{
    /// <summary>
    /// The sprayer block's media picker and its advanced settings.
    ///
    /// The advanced sliders do not edit the medium for everyone. Opening them claims
    /// a variant row seeded from the chosen medium, so two sprayers can throw sand of
    /// different coarseness in the same level and neither changes what the other
    /// pours.
    /// </summary>
    public class MediaSettings
    {
        private Modding.ModBlockBehaviour holder;

        private MMenu media;
        private MToggle advanced;
        private MColourSlider colour;

        // Seven settings, not twelve. Each is either the medium's own number (grain size,
        // stickiness) or a multiple of it (weight, grip, softness, the wheels' dust cloud), and everything else -- how
        // grains clump, bounce, drift in air and water, pack, get wet -- follows the medium.
        // Opened and left alone they pour exactly the medium.
        private MSlider grainSize;   // centimetres across
        private MSlider weight;      // times the medium's
        private MSlider grip;        // times the medium's friction: how steep it piles
        private MSlider stickiness;  // the medium's adhesion, as it is
        private MSlider softness;    // times the medium's: rutted and dug that much more easily
        private MSlider cloudSize;   // times the medium's: the dust a wheel raises from it
        private MSlider cloudLength; // times the medium's: how long that dust hangs in the air

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

        public void Build(Modding.ModBlockBehaviour owner)
        {
            holder = owner;

            List<string> names = new List<string>();
            for (int i = 0; i < SandMaterials.Count; i++) names.Add(SandMaterials.Names[i]);

            media = holder.AddMenu("Media", SandMaterials.Sand, names, true);

            // The colour starts as the medium's own, and follows it when the medium
            // changes: picking Snow should make the swatch white, not leave it
            // showing the last medium's sand.
            colour = holder.AddColourSlider("Colour", "MediaColour",
                                         SandMaterials.Tints[SandMaterials.Sand], false);
            advanced = holder.AddToggle("Advanced", "MediaAdvanced", false);

            // The track covers the range worth dragging; the value may be typed
            // anywhere from a hundredth of a second to ninety minutes, which is why
            // this one is unclamped. Always seconds, never minutes, so a typed number
            // means one thing.
            lifetime = holder.AddSliderUnclamped("Lifetime", "MediaLifetime",
                                              DefaultLifetime, 0.5f, 100f);

            // Ranges are wide enough to be silly with on purpose: the interesting
            // settings for a sand mod are the ones outside what a real material does.
            // New keys, not the old twelve's: a machine saved with those keeps its medium.
            grainSize = holder.AddSlider("Grain Size (cm)", "MediaGrainSize", 6f, SandMaterials.MinRadius * 200f, 50f);
            weight = holder.AddSlider("Weight", "MediaWeight", 1f, 0.1f, 10f);
            grip = holder.AddSlider("Grip", "MediaGrip", 1f, 0.1f, 3f);
            stickiness = holder.AddSlider("Stickiness", "MediaStick", 0f, 0f, 80000f);
            softness = holder.AddSlider("Softness", "MediaSoftness", 1f, 0.1f, 10f);
            cloudSize = holder.AddSlider("Cloud Size", "MediaCloudSize", 1f, 0f, 5f);
            cloudLength = holder.AddSlider("Cloud Length", "MediaCloudLength", 1f, 0.1f, 5f);

            advancedSliders = new MSlider[] { grainSize, weight, grip, stickiness, softness, cloudSize, cloudLength };

            media.ValueChanged += OnMediaChanged;
            advanced.Toggled += OnAdvancedToggled;
            for (int i = 0; i < advancedSliders.Length; i++)
                advancedSliders[i].ValueChanged += OnAdvancedSliderChanged;

            ShowAdvanced(advanced.IsActive);
            LoadSlidersFrom(BaseMedia);
        }

        /// <summary>
        /// Sets visibility of the advanced rows. A block should not churn it, so this
        /// is only called on an actual change.
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

        /// <summary>Puts the advanced settings back to a medium's own: its grain size and
        /// stickiness, and once each of the rest.</summary>
        private void LoadSlidersFrom(int material)
        {
            SandMaterials.Params p = SandField.Instance.Materials[Mathf.Clamp(material, 0, SandMaterials.Count - 1)];
            grainSize.Value = p.radius * 200f;
            weight.Value = 1f;
            grip.Value = 1f;
            stickiness.Value = p.adhesion;
            softness.Value = 1f;
            cloudSize.Value = 1f;
            cloudLength.Value = 1f;
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
            SandMaterials.Params from = SandField.Instance.Materials[Mathf.Clamp(baseMedia, 0, SandMaterials.Count - 1)];
            return SandMaterials.Custom(from, grainSize.Value * 0.005f, weight.Value, grip.Value, stickiness.Value, softness.Value,
                                        cloudSize.Value, cloudLength.Value);
        }

    }
}
