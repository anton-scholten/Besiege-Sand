using System;
using UnityEngine;
using UnityEngine.UI;

namespace SandMod
{
    // Thin wrapper over UI Factory 3 (Workshop item 2913469777), which ships
    // Besiege's real interface as Unity prefabs. Instantiating those is the only
    // way a mod panel can look like part of the game.
    //
    // UI Factory is a SOFT dependency: without it the blocks use their ordinary
    // mapper and no panel appears. A type that cannot be resolved fails as the
    // method mentioning it is compiled, so every mention of `Besiege.UI` in this
    // mod stays in this one file -- then one guarded call decides whether the
    // panels can exist at all.
    public static class UIF
    {
        public const string Package = "UIFactory3";

        public const string WindowPrefab = "Window";
        public const string SliderPrefab = "Slider";
        public const string TogglePrefab = "Text Toggle";
        public const string InputPrefab = "Input Field";


        // UI Factory's own font. An Input Field's text and placeholder come out of
        // the prefab with none -- "Font is null, replacing with default" in the log
        // -- and a Text with no font draws nothing at all.
        public static Font Font
        {
            get
            {
                try { return Besiege.UI.Make.Font; }
                catch (Exception) { return null; }
            }
        }

        // The lettering colour for anything that is not the answer.
        public static readonly Color QuietInk = new Color(0.72f, 0.72f, 0.74f, 1f);

        private static bool asked;
        private static bool available;

        // Whether UI Factory is installed and has finished loading its bundle.
        // Touching Besiege.UI at all is what fails when the mod is absent, so the
        // try has to be here, a call away from any panel code.
        public static bool Available
        {
            get
            {
                if (asked) return available;
                try
                {
                    available = Besiege.UI.Make.Instance != null
                             && Modding.ModResource.AllResourcesLoaded;
                }
                catch (Exception)
                {
                    available = false;
                }
                // Remembered only once true: UI Factory loads its bundle a moment
                // after the mod does, and "not yet" is not "not installed".
                asked = available;
                return available;
            }
        }

        // Make.Prefab throws if UI Factory's resources are not loaded, and its own
        // readiness is the only thing that knows. Everything the panel builds hangs
        // off this rather than off a guess about when the game is up.
        public static void WhenReady(Action action)
        {
            try
            {
                Besiege.UI.Make.OnReady(Package, action);
            }
            catch (Exception)
            {
                // No UI Factory. Normal -- the panels are optional and the blocks
                // fall back to Besiege's own mapper.
            }
        }

        public const string ButtonPrefab = "Text Button";

        /// <summary>Sets the caption on a spawned prefab: its first Text, Translator gone.</summary>
        public static Text Caption(GameObject go, string text)
        {
            if (go == null) return null;
            Text label = go.GetComponentInChildren<Text>(true);
            if (label != null) label.text = text;
            return label;
        }

        public static GameObject Spawn(string prefab, Transform parent)
        {
            try
            {
                GameObject made = Besiege.UI.Make.Prefab(Package, prefab, parent);
                Untranslate(made);
                return made;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Sand] UI Factory has no prefab '"
                    + prefab + "': " + e.Message);
                return null;
            }
        }

        // Strips every Translator in a spawned prefab. One puts the prefab's own
        // wording back at the next language change, and -- fatally -- its Start
        // throws on a label with no localisation key, which is every label a mod
        // writes, taking the whole panel build with it.
        //
        // Whole hierarchy including inactive, since a Translator is not always on
        // the object carrying the Text. Immediate, or Start runs first.
        public static void Untranslate(GameObject spawned)
        {
            if (spawned == null) return;
            try
            {
                Besiege.UI.Behaviours.Translator[] all =
                    spawned.GetComponentsInChildren<Besiege.UI.Behaviours.Translator>(true);
                for (int i = 0; i < all.Length; i++)
                    if (all[i] != null) UnityEngine.Object.DestroyImmediate(all[i]);
            }
            catch (Exception) { }
        }

        // Takes a control's own hover swell away. A hovered button grows 15%, which
        // is right for a button and wrong for a full-width row: it carries the text
        // sideways instead of lighting the row up.
        //
        // Destroyed rather than disabled, so SetSwell below can turn the rest of a
        // row's swells back on without bringing this one with them.
        // Anchors a rect to its parent's top-left corner and puts it at x, y with y
        // running downward, which is how both the panel and the chooser lay out.
        public static void Fit(RectTransform rect, float x, float y, float w, float h)
        {
            if (rect == null) return;
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.sizeDelta = new Vector2(w, h);
            rect.anchoredPosition = new Vector2(x, -y);
        }

        public static void NoSwell(GameObject control)
        {
            if (control == null) return;
            try
            {
                Besiege.UI.Bridge.ScaleAnimation scale =
                    control.GetComponent<Besiege.UI.Bridge.ScaleAnimation>();
                if (scale != null) UnityEngine.Object.DestroyImmediate(scale);
            }
            catch (Exception) { }
        }

        // Every swell inside a control, on or off. A Slider's white handle carries
        // its own, and ScaleAnimation does not ask whether the Slider is
        // interactable -- so a greyed-out slider still swelled under the pointer as
        // though it could be dragged.
        public static void SetSwell(GameObject control, bool on)
        {
            if (control == null) return;
            try
            {
                Besiege.UI.Bridge.ScaleAnimation[] all =
                    control.GetComponentsInChildren<Besiege.UI.Bridge.ScaleAnimation>(true);
                for (int i = 0; i < all.Length; i++)
                    if (all[i] != null) all[i].enabled = on;
            }
            catch (Exception) { }
        }
    }
}
