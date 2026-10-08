using UnityEngine;

namespace SandMod
{
    /// <summary>
    /// The level editor's sprayer and regions in the same panel as the sprayer block's. The level editor
    /// opens Besiege's block mapper on a level object too -- its holder is then the object's
    /// <c>GenericEntity</c>, with a SETTINGS and a LOGIC page -- so the panel docks under it exactly as
    /// it does for a block, while the SETTINGS page is showing.
    /// </summary>
    public static class EntityPanel
    {
        /// <summary>The component of type T on the level object the mapper is open on, or null if it is
        /// open on a block, on another object, or on the object's LOGIC page.</summary>
        public static T Of<T>(BlockMapper mapper) where T : Component
        {
            GenericEntity entity = mapper.Entity;
            if (entity == null || mapper.IsLogic) return null;
            T found = entity.GetComponent<T>();
            return found != null ? found : entity.GetComponentInParent<T>();
        }
    }

    public class SprayerEntityPanel : Panel
    {
        protected override Component Serves(BlockMapper mapper)
        {
            return EntityPanel.Of<SprayerEntityBehaviour>(mapper);
        }

        protected override float Layout()
        {
            StartLayout();
            float y = Margin;

            Selector(ref y, "Media");
            Hue(ref y, "MediaColour");
            Rule(ref y);

            // The block's key is the level's switch here: on at the start, or driven by sandspray.
            Switch(ref y, "SprayOnStart");
            Bar(ref y, "SprayRate");
            Bar(ref y, "SpraySpeed");
            Bar(ref y, "SpraySpread");
            Bar(ref y, "MediaLifetime");
            Rule(ref y);

            Section(ref y, "MediaAdvanced", "ADVANCED", true);
            Bar(ref y, "MediaGrainSize");
            Bar(ref y, "MediaWeight");
            Bar(ref y, "MediaGrip");
            Bar(ref y, "MediaStick");
            Bar(ref y, "MediaSoftness");
            EndSection(y);

            return Complete() ? y : -1f;
        }
    }

    public class RegionPanel : Panel
    {
        protected override Component Serves(BlockMapper mapper)
        {
            return EntityPanel.Of<RegionEntityBehaviour>(mapper);
        }

        protected override float Layout()
        {
            StartLayout();
            float y = Margin;

            Selector(ref y, "Media");
            Hue(ref y, "MediaColour");
            Rule(ref y);

            Bar(ref y, "RegionPacking");
            Bar(ref y, "MediaLifetime");
            Rule(ref y);

            Section(ref y, "MediaAdvanced", "ADVANCED", true);
            Bar(ref y, "MediaGrainSize");
            Bar(ref y, "MediaWeight");
            Bar(ref y, "MediaGrip");
            Bar(ref y, "MediaStick");
            Bar(ref y, "MediaSoftness");
            EndSection(y);

            return Complete() ? y : -1f;
        }
    }
}
