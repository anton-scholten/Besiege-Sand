using System.Collections.Generic;
using UnityEngine;

namespace SandMod
{
    /// <summary>
    /// Somewhere a settings panel can be built. Exists because a modded block and a
    /// level editor object offer the same mapper vocabulary through two unrelated
    /// types: <c>Modding.ModBlockBehaviour</c> is a plain MonoBehaviour, while a level
    /// object's <c>GenericEntity</c> comes from <c>SaveableDataHolder</c>. The calls
    /// match, the base classes do not, so one thin adapter each is cheaper than two
    /// copies of the panel that will drift apart.
    /// </summary>
    public interface IMediaHost
    {
        MMenu Menu(string key, int value, List<string> items, bool footer);
        MSlider Slider(string display, string key, float value, float min, float max);

        /// <summary>
        /// A slider whose typed value is allowed past the ends of its track.
        /// MSlider.Value's setter never clamps, but DeSerialize does: a value outside
        /// Min..Max works for the rest of the session and is quietly lost on the next
        /// load unless the slider was declared unclamped.
        /// </summary>
        MSlider SliderUnclamped(string display, string key, float value, float min, float max);
        MToggle Toggle(string display, string key, bool value);
        MColourSlider ColourSlider(string display, string key, Color value, bool useHue);
    }

    /// <summary>A modded block's mapper.</summary>
    public class BlockHost : IMediaHost
    {
        private readonly Modding.ModBlockBehaviour block;

        public BlockHost(Modding.ModBlockBehaviour owner) { block = owner; }

        public MMenu Menu(string key, int value, List<string> items, bool footer)
        {
            return block.AddMenu(key, value, items, footer);
        }

        public MSlider Slider(string display, string key, float value, float min, float max)
        {
            return block.AddSlider(display, key, value, min, max);
        }

        public MSlider SliderUnclamped(string display, string key, float value, float min, float max)
        {
            return block.AddSliderUnclamped(display, key, value, min, max);
        }

        public MToggle Toggle(string display, string key, bool value)
        {
            return block.AddToggle(display, key, value);
        }

        public MColourSlider ColourSlider(string display, string key, Color value, bool useHue)
        {
            return block.AddColourSlider(display, key, value, useHue);
        }
    }

    /// <summary>A level editor object's SETTINGS tab.</summary>
    public class EntityHost : IMediaHost
    {
        private readonly SaveableDataHolder holder;

        public EntityHost(SaveableDataHolder owner) { holder = owner; }

        public MMenu Menu(string key, int value, List<string> items, bool footer)
        {
            return holder.AddMenu(key, value, items, footer);
        }

        public MSlider Slider(string display, string key, float value, float min, float max)
        {
            return holder.AddSlider(display, key, value, min, max);
        }

        public MSlider SliderUnclamped(string display, string key, float value, float min, float max)
        {
            return holder.AddSliderUnclamped(display, key, value, min, max);
        }

        public MToggle Toggle(string display, string key, bool value)
        {
            return holder.AddToggle(display, key, value);
        }

        public MColourSlider ColourSlider(string display, string key, Color value, bool useHue)
        {
            return holder.AddColourSlider(display, key, value, useHue);
        }
    }
}
