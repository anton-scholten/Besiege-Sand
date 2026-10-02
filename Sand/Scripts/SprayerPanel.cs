using UnityEngine;

namespace SandMod
{
    /// <summary>
    /// The sprayer's settings in Besiege's own interface, through UI Factory, docked under
    /// the block mapper -- the same panel as the SpecialEffects blocks', whose framework this
    /// is (see Panel). The spray key stays on the stock mapper above it, which draws keys as
    /// the game does everywhere else.
    ///
    /// Without UI Factory there is no panel and the stock mapper shows everything, as before.
    /// </summary>
    public class SprayerPanel : Panel
    {
        protected override Component Serves(BlockMapper mapper)
        {
            return mapper.Block.GetComponent<SprayerBehaviour>();
        }

        protected override float Layout()
        {
            StartLayout();
            float y = Margin;

            // What it sprays and what colour.
            Selector(ref y, "Media");
            Hue(ref y, "MediaColour");
            Rule(ref y);

            // How it sprays. Picking a medium presets these; every one stays editable.
            Bar(ref y, "SprayRate");
            Bar(ref y, "SpraySpeed");
            Bar(ref y, "SpraySpread");
            Bar(ref y, "MediaLifetime");
            Rule(ref y);

            // The medium's own numbers, for this sprayer alone: opening the section claims a
            // variant row seeded from the medium (see MediaSettings), so nothing else that
            // pours the same medium changes with it.
            Section(ref y, "MediaAdvanced", "ADVANCED");
            Bar(ref y, "MediaRadius");
            Bar(ref y, "MediaDensity");
            Bar(ref y, "MediaFriction");
            Bar(ref y, "MediaCohesion");
            Bar(ref y, "MediaBounce");
            Bar(ref y, "MediaStickiness");
            Bar(ref y, "MediaFirmness");
            Bar(ref y, "MediaAirDrag");
            Bar(ref y, "MediaWaterDrag");
            Bar(ref y, "MediaPacking");
            Bar(ref y, "MediaWetCohesion");
            Bar(ref y, "MediaWetTime");
            EndSection();

            return Complete() ? y : -1f;
        }
    }
}
