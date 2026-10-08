using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace SandMod
{
    /// <summary>
    /// The sand panel: a small window on the left of the screen, there while building and while
    /// a run is going, and the brush it drives.
    ///
    ///   Paint                    a dashed circle follows the cursor on the ground; hold the
    ///                            left button to pour into it
    ///   Remove sand              the brush takes sand away instead
    ///   medium                   which of the media the brush pours
    ///   Size                     the circle's width: slid 1-25 m, typed 0.5-10000 m
    ///   Pause sprayers           sprayers and regions stop pouring
    ///   Pause sand simulations   the sand stops being computed, left where it is
    ///   Keep changes to sand     the sand is kept between runs rather than put back
    ///   Other players place sand multiplayer only: whether clients may use the brush
    ///   Clear all sand
    ///
    /// Built as uGUI on its own canvas rather than with OnGUI, for the reasons in the notes
    /// repository's 09-overlay-ui: Besiege composites its uGUI over IMGUI, so OnGUI would draw
    /// behind the game's own interface; and Besiege asks the EventSystem whether the pointer is
    /// over UI before placing a block, which a uGUI panel answers and an OnGUI one does not.
    /// While the pointer is over the panel every camera's eventMask is held at zero, so the
    /// game's own collider-based buttons behind it do not take the click either.
    /// </summary>
    public class SandHud : MonoBehaviour
    {
        private Canvas canvas;
        private RectTransform panel;

        private bool brushOn;
        private bool removing;
        private int medium = SandMaterials.Sand;

        /// <summary>The brush's size, across, in metres: typed anywhere in Min/MaxSize, slid in Min/MaxSlid.</summary>
        private float size = 6f;
        private const float MinSize = 0.5f, MaxSize = 10000f;
        private const float MinSlid = 1f, MaxSlid = 25f;
        private float radius { get { return Mathf.Min(size * 0.5f, SandField.MaxBrushRadius); } }

        /// <summary>How fast the brush raises the sand where it is at full strength, in metres a
        /// second; the same for every size, so a big brush lays the layer a small one does.</summary>
        private float rate = 1f;
        private const float MinRate = 0.01f, MaxRate = 100f;
        private const float MinRateSlid = 0.1f, MaxRateSlid = 5f;

        private int style = SandField.BrushCircle;
        private int strokeSeed;

        // The brush's spot, found each frame while it is on.
        private bool haveSpot;
        private bool spotOnMachine;
        private Vector3 spot;
        private readonly RaycastHit[] hits = new RaycastHit[32];
        private static readonly Dictionary<int, bool> machineBodies = new Dictionary<int, bool>();
        private Material lineMaterial;

        // The shield: each camera's own eventMask while it is held at zero.
        private readonly Dictionary<Camera, int> heldMasks = new Dictionary<Camera, int>();


        private void Start()
        {
            Recall();
            MakeCanvas();
            startedAt = Time.realtimeSinceStartup;
            // Guarded here, at the call: without UI Factory the call itself fails, as the
            // method it calls is compiled, before any try inside it can catch anything.
            try { UIF.WhenReady(BuildFactory); }
            catch (System.Exception) { factoryMissing = true; }
            lineMaterial = SandLook.DepthMaterial();
            lineMaterial.SetFloat("_ZWrite", 0f);
            lineMaterial.SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Off);
            lineMaterial.renderQueue = 3100;
        }

        private void OnDisable()
        {
            ReleaseShield();
        }

        private void OnApplicationQuit()
        {
            Remember();
            SandSettings.Flush();
        }

        // ------------------------------------------------------------------ kept between runs of the game
        //
        // Everything the panel sets but the paint switch -- medium, colour, brush, size, rate, removing, the advanced
        // numbers, the switches, where the panel is and whether it is folded -- kept in the mod's settings file
        // (SandSettings) and put back when the game next starts. Painting itself starts off.

        private float nextRemember;
        private bool recalledFold;

        private void Recall()
        {
            medium = Mathf.Clamp(SandSettings.Get("medium", medium), 0, SandMaterials.Count - 1);
            style = Mathf.Clamp(SandSettings.Get("brush", style), 0, SandField.BrushNames.Length - 1);
            size = Mathf.Clamp(SandSettings.Get("size", size), MinSize, MaxSize);
            rate = Mathf.Clamp(SandSettings.Get("rate", rate), MinRate, MaxRate);
            removing = SandSettings.Get("removing", removing);
            ResetToMedium();
            string hex = SandSettings.Get("colour", "");
            Color c;
            if (hex.Length > 0 && ColorUtility.TryParseHtmlString("#" + hex, out c)) { c.a = 1f; colour = c; }
            advanced = SandSettings.Get("advanced", advanced);
            grainCm = Mathf.Clamp(SandSettings.Get("grainCm", grainCm), SandMaterials.MinRadius * 200f, 50f);
            weightTimes = Mathf.Clamp(SandSettings.Get("weight", weightTimes), 0.1f, 10f);
            gripTimes = Mathf.Clamp(SandSettings.Get("grip", gripTimes), 0.1f, 3f);
            stick = Mathf.Clamp(SandSettings.Get("stickiness", stick), 0f, 80000f);
            softTimes = Mathf.Clamp(SandSettings.Get("softness", softTimes), 0.1f, 10f);
            Recustom();
            SandField.SourcesPaused = SandSettings.Get("pauseSprayers", SandField.SourcesPaused);
            SandField.Frozen = SandSettings.Get("pauseSand", SandField.Frozen);
            SandField.KeepBetweenRuns = SandSettings.Get("keepChanges", SandField.KeepBetweenRuns);
            SandNet.AllowOthers = SandSettings.Get("othersPlace", SandNet.AllowOthers);
            recalledFold = SandSettings.Get("folded", false);
        }

        // Puts the panel where it was left, once it is built.
        private void Placed()
        {
            if (panel == null) return;
            float x = SandSettings.Get("panelX", float.NaN), y = SandSettings.Get("panelY", float.NaN);
            if (!float.IsNaN(x) && !float.IsNaN(y))
            {
                // Kept on screen: a panel restored past the edge of a smaller window would have no way back.
                RectTransform area = canvas != null ? canvas.transform as RectTransform : null;
                float w = area != null ? area.rect.width : 1920f, h = area != null ? area.rect.height : 1080f;
                x = Mathf.Clamp(x, -w + 120f, 0f);
                y = Mathf.Clamp(y, -h + 40f, 0f);
                panel.anchoredPosition = new Vector2(x, y);
            }
            if (recalledFold) Fold();
        }

        private void Remember()
        {
            SandSettings.Set("medium", medium);
            SandSettings.Set("brush", style);
            SandSettings.Set("size", size);
            SandSettings.Set("rate", rate);
            SandSettings.Set("removing", removing);
            SandSettings.Set("colour", ColorUtility.ToHtmlStringRGB(colour));
            SandSettings.Set("advanced", advanced);
            SandSettings.Set("grainCm", grainCm);
            SandSettings.Set("weight", weightTimes);
            SandSettings.Set("grip", gripTimes);
            SandSettings.Set("stickiness", stick);
            SandSettings.Set("softness", softTimes);
            SandSettings.Set("pauseSprayers", SandField.SourcesPaused);
            SandSettings.Set("pauseSand", SandField.Frozen);
            SandSettings.Set("keepChanges", SandField.KeepBetweenRuns);
            SandSettings.Set("othersPlace", SandNet.AllowOthers);
            if (panel != null)
            {
                bool folded = !panel.gameObject.activeSelf && icon != null && icon.activeSelf;
                SandSettings.Set("folded", folded);
                Vector2 at = folded && iconRect != null ? iconRect.anchoredPosition : panel.anchoredPosition;
                SandSettings.Set("panelX", at.x);
                SandSettings.Set("panelY", at.y);
            }
        }

        private void OnDestroy()
        {
            ReleaseShield();
            if (lineMaterial != null) Destroy(lineMaterial);
        }

        // ------------------------------------------------------------------ the panel

        private static Font font;

        private float startedAt;
        private float nextFactoryTry = 10f;
        private bool built;
        private bool factoryMissing;

        /// <summary>
        /// Whether UI Factory is there and ready, without falling over when it is not there at
        /// all. Asking UIF.Available directly from Update threw every frame when UI Factory was
        /// not loaded -- the assembly it names cannot be found as the call is compiled -- which
        /// stopped the panel from ever being built, plain or otherwise.
        /// </summary>
        private bool FactoryReady()
        {
            if (factoryMissing) return false;
            try { return UIF.Available; }
            catch (System.Exception)
            {
                factoryMissing = true;
                return false;
            }
        }

        private void MakeCanvas()
        {
            GameObject root = new GameObject("SandHudCanvas");
            root.transform.SetParent(transform, false);
            canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 28000;
            CanvasScaler scaler = root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 1f;
            root.AddComponent<GraphicRaycaster>();
        }

        // ------------------------------------------------------------------ rows

        /// <summary>
        /// One row of the panel. The rows are stacked by <see cref="Lay"/> rather than placed
        /// once, so a row that is not wanted -- the multiplayer switch outside a multiplayer
        /// game -- is taken out and the panel closes up round it.
        /// </summary>
        private class Row
        {
            public RectTransform rect;
            public float height;
            public float gapAfter;
            public bool multiplayerOnly;
            public bool advancedOnly;
        }

        private readonly List<Row> rows = new List<Row>();
        private RectTransform content;
        private float topHeight;
        private const float Pad = 10f, RowHeight = 30f, Gap = 6f;
        private float panelWidth;

        private void AddRow(GameObject go, float gapAfter, bool multiplayerOnly)
        {
            if (go == null) return;
            Row r = new Row();
            r.rect = go.transform as RectTransform;
            r.height = RowHeight;
            r.gapAfter = gapAfter;
            r.multiplayerOnly = multiplayerOnly;
            rows.Add(r);
        }

        private void AddAdvancedRow(GameObject go)
        {
            AddRow(go, Gap, false);
            if (go != null) rows[rows.Count - 1].advancedOnly = true;
        }

        private void Lay()
        {

            float y = Pad;
            for (int i = 0; i < rows.Count; i++)
            {
                Row r = rows[i];
                bool on = (!r.multiplayerOnly || othersShown) && (!r.advancedOnly || advanced);
                if (r.rect.gameObject.activeSelf != on) r.rect.gameObject.SetActive(on);
                if (!on) continue;
                UIF.Fit(r.rect, Pad, y, panelWidth - Pad * 2f, r.height);
                y += r.height + r.gapAfter;
            }
            y += Pad - Gap;
            if (content != null) content.sizeDelta = new Vector2(0f, y);
            if (panel != null) panel.sizeDelta = new Vector2(panelWidth, topHeight + y);
        }

        // ------------------------------------------------------------------ in Besiege's own look

        private UnityEngine.UI.Toggle brushToggle, removeToggle, pauseToggle, freezeToggle, keepToggle, othersToggle;
        private UnityEngine.UI.Selectable clearControl, saveControl, loadControl;
        private GameObject filesRow;
        private bool filesOn = true;

        private UnityEngine.UI.Selectable FactoryButton(Transform parent, string text, UnityEngine.Events.UnityAction onClick, float x, float width)
        {
            GameObject go = UIF.Spawn(UIF.ButtonPrefab, parent);
            if (go == null) return null;
            UIF.Caption(go, text);
            UIF.Fit(go.transform as RectTransform, x, 0f, width, RowHeight);
            UnityEngine.UI.Button b = go.GetComponent<UnityEngine.UI.Button>();
            if (b == null) b = go.GetComponentInChildren<UnityEngine.UI.Button>(true);
            if (b != null) b.onClick.AddListener(onClick);
            return b;
        }
        private Chooser mediumChooser, styleChooser;
        private Number sizeNumber, rateNumber;
        private bool echo;

        /// <summary>
        /// The panel as UI Factory's window and controls, the way the node editor builds its
        /// board: the window prefab's own opaque, rounded background with its title bar and
        /// scroll view switched off, a darker strip of our own across the top that drags the
        /// panel and folds it, Besiege's toggles and button, the spotlight's selectors for the
        /// brush's shape and the medium, and sliders with a box to type into for the size and
        /// the rate.
        /// </summary>
        private void BuildFactory()
        {
            if (built || canvas == null) return;
            GameObject w = UIF.Spawn(UIF.WindowPrefab, canvas.transform);
            if (w == null) return;
            built = true;
            w.name = "SandPanel";

            panel = w.transform as RectTransform;
            Pin(panel);
            panelWidth = 300f;

            // Off rather than destroyed, as the node editor does: the window's background
            // belongs to the window itself, and the two parts only bring a title we do not
            // want and a scroll we do not need.
            Transform prefabBar = Attach.Find(w.transform, "TopBar");
            if (prefabBar != null) prefabBar.gameObject.SetActive(false);
            ScrollRect scroll = w.GetComponentInChildren<ScrollRect>(true);
            if (scroll != null) scroll.enabled = false;
            Transform view = Attach.Find(w.transform, "ScrollView");
            if (view != null) view.gameObject.SetActive(false);

            // Over it, it is a menu: the wheel does not zoom and a right drag does not turn
            // the camera. Counted in ZoomGuard.Raised, so the panel does not hide itself.
            ZoomGuard guard = w.GetComponent<ZoomGuard>();
            if (guard == null) guard = w.AddComponent<ZoomGuard>();
            guard.menu = true;

            topHeight = 32f;
            GameObject strip = Rect("Bar", w.transform);
            UIF.Fit(strip.transform as RectTransform, 0f, 0f, panelWidth, topHeight);
            strip.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.25f);
            PanelDrag drag = strip.AddComponent<PanelDrag>();
            drag.frame = panel;
            // Centred on the whole strip; the fold button sits over its right end.
            Text title = Word(strip.transform, "SAND", 20, TextAnchor.MiddleCenter, true);
            Stretch(title.rectTransform, 0f, 0f, 0f, 0f);

            GameObject fold = UIF.Spawn(UIF.ButtonPrefab, strip.transform);
            if (fold != null)
            {
                UIF.Fit(fold.transform as RectTransform, panelWidth - 34f, 3f, 28f, 26f);
                UIF.NoSwell(fold);
                UIF.Caption(fold, "-");
                UnityEngine.UI.Button b = fold.GetComponent<UnityEngine.UI.Button>();
                if (b == null) b = fold.GetComponentInChildren<UnityEngine.UI.Button>(true);
                if (b != null) b.onClick.AddListener(Fold);
            }

            GameObject c = Rect("Content", w.transform);
            content = c.GetComponent<RectTransform>();
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.anchoredPosition = new Vector2(0f, -topHeight);

            float inner = panelWidth - Pad * 2f;
            brushToggle = FactoryToggle("Paint", brushOn, delegate (bool v) { brushOn = v; Refresh(); }, Gap, false);
            removeToggle = FactoryToggle("Remove sand", removing, delegate (bool v) { removing = v; Refresh(); }, Gap, false);

            // The spotlight's selectors: arrows step through the choices, the name -- or a right
            // click anywhere on it -- opens the list of all of them.
            styleChooser = Chooser.Make(content, canvas.transform, Pad, 0f, inner, RowHeight,
                                        new List<string>(SandField.BrushNames), style);
            styleChooser.Changed = delegate (int i) { style = i; };
            AddRow(styleChooser.gameObject, Gap, false);

            mediumChooser = Chooser.Make(content, canvas.transform, Pad, 0f, inner, RowHeight,
                                         new List<string>(SandMaterials.Names), medium);
            mediumChooser.Changed = delegate (int i) { medium = i; ResetToMedium(); };
            AddRow(mediumChooser.gameObject, Gap, false);

            AddRow(ColourRow(inner), Gap, false);

            sizeNumber = new Number("Size", MinSlid, MaxSlid, MinSize, MaxSize, 0.5f,
                                    delegate { return size; }, delegate (float v) { size = v; });
            AddRow(NumberRow(sizeNumber, inner), Gap, false);
            rateNumber = new Number("Rate", MinRateSlid, MaxRateSlid, MinRate, MaxRate, 0.05f,
                                    delegate { return rate; }, delegate (float v) { rate = v; });
            AddRow(NumberRow(rateNumber, inner), Gap, false);

            // The medium's own numbers, to make a sand of one's own from it. Shown with the
            // switch; a new medium chosen starts them again from that medium.
            advancedToggle = FactoryToggle("Advanced", advanced, delegate (bool v)
            {
                advanced = v;
                CloseLists();
                Lay();
            }, Gap, false);
            BuildAdvanced(inner);
            if (rows.Count > 0) rows[rows.Count - 1].gapAfter = Gap + 4f;

            pauseToggle = FactoryToggle("Pause sprayers", SandField.SourcesPaused, delegate (bool v) { SandField.SourcesPaused = v; }, Gap, false);
            freezeToggle = FactoryToggle("Pause sand simulations", SandField.Frozen, delegate (bool v) { SandField.Frozen = v; }, Gap, false);
            keepToggle = FactoryToggle("Keep changes to sand", SandField.KeepBetweenRuns, delegate (bool v) { SandField.KeepBetweenRuns = v; }, Gap + 4f, false);

            GameObject clear = UIF.Spawn(UIF.ButtonPrefab, content);
            if (clear != null)
            {
                UIF.Caption(clear, "Clear all sand");
                UnityEngine.UI.Button b = clear.GetComponent<UnityEngine.UI.Button>();
                if (b == null) b = clear.GetComponentInChildren<UnityEngine.UI.Button>(true);
                if (b != null) b.onClick.AddListener(ClearAll);
                clearControl = b;
                AddRow(clear, Gap + 4f, false);
            }

            // The sand on its own, as a file in the game's save and load screens (see SandFiles).
            {
                GameObject row = Rect("Files", content);
                float half = (panelWidth - Pad * 2f - Gap) * 0.5f;
                saveControl = FactoryButton(row.transform, "Save", SandFiles.Save, 0f, half);
                loadControl = FactoryButton(row.transform, "Load", SandFiles.Load, half + Gap, half);
                filesRow = row;
                AddRow(row, Gap + 4f, false);
            }

            // Last, and only with somebody else in the game.
            othersToggle = FactoryToggle("Other players place sand", SandNet.AllowOthers, delegate (bool v) { SandNet.AllowOthers = v; }, Gap, true);

            Lay();
            Refresh();
            Placed();
        }

        // ------------------------------------------------------------------ colour and advanced

        private Color colour = SandMaterials.Tints[SandMaterials.Sand];
        private bool advanced;
        private SandMaterials.Params custom = SandMaterials.Build()[SandMaterials.Sand];
        private bool customDirty;
        private int customSlot = -1, customBase = -1;
        private SandField customField;
        private readonly List<Number> advancedNumbers = new List<Number>();
        private UnityEngine.UI.Toggle advancedToggle;
        private UnityEngine.UI.Slider hueSlider;
        private Image swatch;
        private UnityEngine.UI.InputField hexField;

        /// <summary>A medium chosen: its own colour, and its own numbers to start from.</summary>
        private void ResetToMedium()
        {
            colour = SandMaterials.Tints[Mathf.Clamp(medium, 0, SandMaterials.Count - 1)];
            colour.a = 1f;
            SandMaterials.Params from = BaseRow();
            grainCm = from.radius * 200f;
            weightTimes = 1f; gripTimes = 1f; softTimes = 1f;
            stick = from.adhesion;
            Recustom();
            Refresh();
        }

        // The five advanced settings, as the sprayer's are (SandMaterials.Custom): the medium's
        // own grain size and stickiness, and its weight, grip and softness times its own.
        private float grainCm = 6f, weightTimes = 1f, gripTimes = 1f, stick, softTimes = 1f;

        private SandMaterials.Params BaseRow()
        {
            SandField f = SandField.Existing;
            int m = Mathf.Clamp(medium, 0, SandMaterials.Count - 1);
            return f != null ? f.Materials[m] : SandMaterials.Build()[m];
        }

        private void Recustom()
        {
            custom = SandMaterials.Custom(BaseRow(), grainCm * 0.005f, weightTimes, gripTimes, stick, softTimes);
            customDirty = true;
        }

        /// <summary>
        /// The row the brush pours: the medium itself, or with Advanced on, a row of the panel's
        /// own -- one of the field's emitter rows, claimed once, re-seeded when the medium
        /// changes and rewritten when a number does.
        /// </summary>
        private int BrushRow(SandField f)
        {
            if (!advanced || SandNet.IsClient || f == null) return medium;
            if (f != customField || customSlot < 0)
            {
                customField = f;
                customSlot = f.ClaimVariant(medium);
                customBase = medium;
                customDirty = true;
            }
            if (customSlot < SandMaterials.FirstVariant) { customSlot = -1; return medium; }   // the table is full
            if (customBase != medium) { f.ReseatVariant(customSlot, medium); customBase = medium; customDirty = true; }
            if (customDirty) { f.WriteVariant(customSlot, custom); customDirty = false; }
            return customSlot;
        }

        private void BuildAdvanced(float width)
        {
            Advanced("Grain size (cm)", SandMaterials.MinRadius * 200f, 50f, 0.5f, delegate { return grainCm; }, delegate (float v) { grainCm = v; }, width);
            Advanced("Weight", 0.1f, 10f, 0.05f, delegate { return weightTimes; }, delegate (float v) { weightTimes = v; }, width);
            Advanced("Grip", 0.1f, 3f, 0.05f, delegate { return gripTimes; }, delegate (float v) { gripTimes = v; }, width);
            Advanced("Stickiness", 0f, 80000f, 50f, delegate { return stick; }, delegate (float v) { stick = v; }, width);
            Advanced("Softness", 0.1f, 10f, 0.05f, delegate { return softTimes; }, delegate (float v) { softTimes = v; }, width);
        }

        private void Advanced(string caption, float min, float max, float step, System.Func<float> get,
                              System.Action<float> set, float width)
        {
            System.Action<float> setter = set;
            Number n = new Number(caption, min, max, min, max, step, get,
                                  delegate (float v) { setter(v); Recustom(); });
            n.LabelWidth = 96f;
            advancedNumbers.Add(n);
            AddAdvancedRow(NumberRow(n, width));
        }

        /// <summary>
        /// "Colour": a band of hue to slide along, the colour itself, and a box that takes any
        /// colour as six hex digits -- the sprayer's colour control, for the brush.
        /// </summary>
        private GameObject ColourRow(float width)
        {
            GameObject row = Rect("Colour", content);
            const float label = 52f, box = 74f, gap = 6f, chip = 24f;

            Text caption = Word(row.transform, "Colour", 18, TextAnchor.MiddleLeft, false);
            UIF.Fit(caption.rectTransform, 0f, 0f, label, RowHeight);

            GameObject sl = UIF.Spawn(UIF.SliderPrefab, row.transform);
            if (sl != null)
            {
                UIF.Fit(sl.transform as RectTransform, label, 0f, width - label - chip - box - gap * 2f, RowHeight);
                UIF.NoSwell(sl);
                hueSlider = sl.GetComponent<UnityEngine.UI.Slider>();
                if (hueSlider == null) hueSlider = sl.GetComponentInChildren<UnityEngine.UI.Slider>(true);
                if (hueSlider != null)
                {
                    hueSlider.minValue = 0f;
                    hueSlider.maxValue = 1f;
                    hueSlider.wholeNumbers = false;
                    Rainbow(hueSlider);
                    hueSlider.onValueChanged.AddListener(delegate (float h)
                    {
                        if (echo) return;
                        float hh, sat, val;
                        Color.RGBToHSV(colour, out hh, out sat, out val);
                        // A grey has no hue to move; give it some colour to show the slide.
                        if (sat < 0.05f) sat = 0.6f;
                        if (val < 0.1f) val = 0.6f;
                        colour = Color.HSVToRGB(h, sat, val);
                        Refresh();
                    });
                }
            }

            GameObject sw = Rect("Swatch", row.transform);
            UIF.Fit(sw.transform as RectTransform, width - box - gap - chip, 3f, chip, RowHeight - 6f);
            swatch = sw.AddComponent<Image>();
            swatch.raycastTarget = false;

            GameObject f = UIF.Spawn(UIF.InputPrefab, row.transform);
            if (f != null)
            {
                UIF.Fit(f.transform as RectTransform, width - box, 0f, box, RowHeight);
                hexField = f.GetComponent<UnityEngine.UI.InputField>();
                if (hexField != null)
                {
                    Inked(hexField.textComponent, Color.white);
                    Inked(hexField.placeholder as Text, UIF.QuietInk);
                    hexField.characterLimit = 7;
                    hexField.onEndEdit.AddListener(delegate (string typed)
                    {
                        Color parsed;
                        string t = typed.Trim().TrimStart('#');
                        if (ColorUtility.TryParseHtmlString("#" + t, out parsed)) { parsed.a = 1f; colour = parsed; }
                        Refresh();
                    });
                }
            }
            return row;
        }

        private static Sprite rainbow;

        /// <summary>The slider's track drawn as the band of hues.</summary>
        private static void Rainbow(UnityEngine.UI.Slider bar)
        {
            if (rainbow == null)
            {
                Texture2D band = new Texture2D(256, 1, TextureFormat.RGB24, false);
                band.wrapMode = TextureWrapMode.Clamp;
                band.hideFlags = HideFlags.HideAndDontSave;
                for (int i = 0; i < 256; i++) band.SetPixel(i, 0, Color.HSVToRGB(i / 255f, 1f, 1f));
                band.Apply();
                rainbow = Sprite.Create(band, new Rect(0f, 0f, 256f, 1f), new Vector2(0.5f, 0.5f), 100f);
                rainbow.hideFlags = HideFlags.HideAndDontSave;
            }
            Image[] all = bar.GetComponentsInChildren<Image>(true);
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] == null) continue;
                if (bar.fillRect != null && all[i].transform.IsChildOf(bar.fillRect)) continue;
                if (bar.handleRect != null && all[i].transform.IsChildOf(bar.handleRect)) continue;
                all[i].sprite = rainbow;
                all[i].type = Image.Type.Simple;
                all[i].color = Color.white;
                break;
            }
            // The fill would paint over the band; the handle alone says where it is.
            if (bar.fillRect != null)
            {
                Image fill = bar.fillRect.GetComponent<Image>();
                if (fill != null) fill.color = new Color(1f, 1f, 1f, 0f);
            }
        }

        private void ShowColour()
        {
            if (swatch != null) swatch.color = colour;
            if (hexField != null && !hexField.isFocused) hexField.text = ColorUtility.ToHtmlStringRGB(colour);
            if (hueSlider != null)
            {
                float h, sat, val;
                Color.RGBToHSV(colour, out h, out sat, out val);
                if (sat >= 0.05f) hueSlider.value = h;
            }
        }

        /// <summary>
        /// A number with a slider over its usual range and a box that takes anything in its
        /// wider one, typed or dragged.
        /// </summary>
        private class Number
        {
            public readonly string Caption;
            public readonly float SlidMin, SlidMax, Min, Max, Step;
            public readonly System.Func<float> Get;
            public readonly System.Action<float> Set;
            public UnityEngine.UI.Slider Slider;
            public UnityEngine.UI.InputField Field;
            public float LabelWidth = 52f;

            public Number(string caption, float slidMin, float slidMax, float min, float max, float step,
                          System.Func<float> get, System.Action<float> set)
            {
                Caption = caption; SlidMin = slidMin; SlidMax = slidMax; Min = min; Max = max; Step = step;
                Get = get; Set = set;
            }
        }

        private GameObject NumberRow(Number number, float width)
        {
            GameObject row = Rect(number.Caption, content);
            float label = number.LabelWidth;
            const float box = 74f, gap = 6f;

            Text caption = Word(row.transform, number.Caption, 18, TextAnchor.MiddleLeft, false);
            UIF.Fit(caption.rectTransform, 0f, 0f, label, RowHeight);

            GameObject s = UIF.Spawn(UIF.SliderPrefab, row.transform);
            if (s != null)
            {
                UIF.Fit(s.transform as RectTransform, label, 0f, width - label - box - gap, RowHeight);
                UIF.NoSwell(s);
                number.Slider = s.GetComponent<UnityEngine.UI.Slider>();
                if (number.Slider == null) number.Slider = s.GetComponentInChildren<UnityEngine.UI.Slider>(true);
                if (number.Slider != null)
                {
                    number.Slider.minValue = number.SlidMin;
                    number.Slider.maxValue = number.SlidMax;
                    number.Slider.wholeNumbers = false;
                    number.Slider.value = Mathf.Clamp(number.Get(), number.SlidMin, number.SlidMax);
                    Number slid = number;
                    // Slid values come in steps; a typed one keeps what was typed.
                    number.Slider.onValueChanged.AddListener(delegate (float v)
                    {
                        if (echo) return;
                        SetNumber(slid, Mathf.Round(v / slid.Step) * slid.Step);
                    });
                }
            }

            GameObject f = UIF.Spawn(UIF.InputPrefab, row.transform);
            if (f != null)
            {
                UIF.Fit(f.transform as RectTransform, width - box, 0f, box, RowHeight);
                number.Field = f.GetComponent<UnityEngine.UI.InputField>();
                if (number.Field != null)
                {
                    Inked(number.Field.textComponent, Color.white);
                    Inked(number.Field.placeholder as Text, UIF.QuietInk);
                    number.Field.contentType = UnityEngine.UI.InputField.ContentType.DecimalNumber;
                    Number typed = number;
                    number.Field.onEndEdit.AddListener(delegate (string text) { Typed(typed, text); });

                    // A sheet over the box, as on the block panels: a click types, a drag
                    // sideways off it changes the number.
                    GameObject sheet = Rect("Drag", f.transform);
                    Stretch(sheet.transform as RectTransform, 0f, 0f, 0f, 0f);
                    Image catcher = sheet.AddComponent<Image>();
                    catcher.color = new Color(0f, 0f, 0f, 0f);
                    ValueField dragField = sheet.AddComponent<ValueField>();
                    dragField.field = number.Field;
                    // Proportionally, so a big number and a small one both drag well; from zero,
                    // by a share of the range.
                    dragField.dragged = delegate (float pixels)
                    {
                        float now = typed.Get();
                        SetNumber(typed, now > 1e-4f ? now * Mathf.Exp(pixels * 0.01f)
                                                     : Mathf.Max(0f, pixels) * (typed.Max - typed.Min) * 0.002f);
                    };
                }
            }
            return row;
        }

        private static void Inked(Text label, Color colour)
        {
            if (label == null) return;
            if (label.font == null) label.font = UIF.Font != null ? UIF.Font : Resources.GetBuiltinResource<Font>("Arial.ttf");
            label.color = colour;
            label.alignment = TextAnchor.MiddleCenter;
            label.horizontalOverflow = HorizontalWrapMode.Overflow;
            label.verticalOverflow = VerticalWrapMode.Overflow;
        }

        private void Typed(Number number, string typed)
        {
            float v;
            if (float.TryParse(typed, System.Globalization.NumberStyles.Float,
                               System.Globalization.CultureInfo.InvariantCulture, out v)
                || float.TryParse(typed, out v))
                SetNumber(number, v);
            else
                Refresh();
        }

        private void SetNumber(Number number, float v)
        {
            if (float.IsNaN(v) || float.IsInfinity(v)) v = number.Get();
            number.Set(Mathf.Clamp(v, number.Min, number.Max));
            Refresh();
        }

        private void SetSize(float v)
        {
            if (float.IsNaN(v) || float.IsInfinity(v)) v = size;
            size = Mathf.Clamp(v, MinSize, MaxSize);
            Refresh();
        }

        private static string NumberText(float v)
        {
            System.Globalization.CultureInfo c = System.Globalization.CultureInfo.InvariantCulture;
            return v < 1f ? v.ToString("0.###", c) : v < 100f ? v.ToString("0.##", c) : v.ToString("0", c);
        }

        private void ShowNumber(Number number)
        {
            if (number == null) return;
            if (number.Slider != null) number.Slider.value = Mathf.Clamp(number.Get(), number.SlidMin, number.SlidMax);
            if (number.Field != null && !number.Field.isFocused) number.Field.text = NumberText(number.Get());
        }

        private UnityEngine.UI.Toggle FactoryToggle(string text, bool on, UnityEngine.Events.UnityAction<bool> changed,
                                                    float gapAfter, bool multiplayerOnly)
        {
            GameObject go = UIF.Spawn(UIF.TogglePrefab, content);
            if (go == null) return null;
            UIF.Caption(go, text);
            AddRow(go, gapAfter, multiplayerOnly);
            UnityEngine.UI.Toggle t = go.GetComponent<UnityEngine.UI.Toggle>();
            if (t == null) t = go.GetComponentInChildren<UnityEngine.UI.Toggle>(true);
            if (t == null) return null;
            t.isOn = on;
            t.onValueChanged.AddListener(changed);
            return t;
        }

        /// <summary>
        /// Holds a panel by its top-right corner, a little in from the right of the screen. Held
        /// by the middle of a side, as it was, it grew and shrank both ways as rows came and
        /// went, and its corner -- where the icon sits -- moved with it.
        /// </summary>
        private static void Pin(RectTransform r)
        {
            r.anchorMin = r.anchorMax = new Vector2(1f, 1f);
            r.pivot = new Vector2(1f, 1f);
            r.anchoredPosition = new Vector2(-12f, -90f);
        }

        private GameObject icon;
        private RectTransform iconRect;
        private const float IconSize = 52f;

        /// <summary>
        /// The strip's button folds the panel away into a small sand-pile icon in its top-left
        /// corner; the icon opens it again there. The icon can be dragged, and the panel opens
        /// wherever it was left.
        /// </summary>
        private void Fold()
        {
            if (panel == null) return;
            CloseLists();
            MakeIcon();
            iconRect.anchoredPosition = panel.anchoredPosition;
            panel.gameObject.SetActive(false);
            icon.SetActive(true);
        }

        private void Unfold()
        {
            if (panel == null) return;
            if (iconRect != null) panel.anchoredPosition = iconRect.anchoredPosition;
            panel.gameObject.SetActive(true);
            if (icon != null) icon.SetActive(false);
            Lay();
            Refresh();
        }

        private void MakeIcon()
        {
            if (icon != null) return;
            icon = Rect("SandIcon", canvas.transform);
            iconRect = icon.GetComponent<RectTransform>();
            Pin(iconRect);
            iconRect.sizeDelta = new Vector2(IconSize, IconSize);
            Image img = icon.AddComponent<Image>();
            img.sprite = PileSprite();
            UnityEngine.UI.Button b = icon.AddComponent<UnityEngine.UI.Button>();
            b.targetGraphic = img;
            ColorBlock cb = b.colors;
            cb.highlightedColor = new Color(1.15f, 1.15f, 1.15f, 1f);
            cb.pressedColor = new Color(0.85f, 0.85f, 0.85f, 1f);
            b.colors = cb;
            // A click opens the panel; a drag only moves the icon. Unity still calls a press and
            // release on the same button a click however far it was dragged in between, so the
            // drag says so and the click that ends it is let go.
            IconDrag drag = icon.AddComponent<IconDrag>();
            drag.frame = iconRect;
            b.onClick.AddListener(delegate { if (!drag.dragged) Unfold(); });
            icon.AddComponent<ZoomGuard>();
        }

        private static Sprite pile;

        /// <summary>A little heap of sand on the panel's own dark rounded plate, drawn here.</summary>
        private static Sprite PileSprite()
        {
            if (pile != null) return pile;
            const int n = 64;
            const float corner = 12f;
            Texture2D t = new Texture2D(n, n, TextureFormat.ARGB32, false);
            t.wrapMode = TextureWrapMode.Clamp;
            t.hideFlags = HideFlags.HideAndDontSave;
            Color plate = new Color(0.11f, 0.12f, 0.14f, 1f);
            Color sand = SandMaterials.Tints[SandMaterials.Sand];
            Color[] px = new Color[n * n];
            uint seed = 0x9E3779B9u;
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    // The plate: a rounded square, its edge smoothed over a pixel.
                    float cx = Mathf.Clamp(x + 0.5f, corner, n - corner), cy = Mathf.Clamp(y + 0.5f, corner, n - corner);
                    float d = Mathf.Sqrt((x + 0.5f - cx) * (x + 0.5f - cx) + (y + 0.5f - cy) * (y + 0.5f - cy));
                    float plateA = Mathf.Clamp01(corner - d + 0.5f);
                    Color c = plate;

                    // The heap: a bell over a flat base, lit from the upper left, speckled.
                    float u = (x + 0.5f - 32f) / 17f;
                    float top = 11f + 30f * Mathf.Exp(-u * u);
                    float inside = Mathf.Clamp01(top - (y + 0.5f) + 0.5f) * (y >= 9 ? 1f : 0f);
                    if (inside > 0f && x >= 6 && x < n - 6)
                    {
                        seed ^= seed << 13; seed ^= seed >> 17; seed ^= seed << 5;
                        float speck = ((seed & 0xFF) / 255f - 0.5f) * 0.22f;
                        float slope = -2f * u * 30f * Mathf.Exp(-u * u) / 17f;      // dtop/dx
                        float light = 1f + Mathf.Clamp(slope * 0.25f, -0.25f, 0.25f) + speck;
                        Color s2 = new Color(sand.r * light, sand.g * light, sand.b * light, 1f);
                        c = Color.Lerp(c, s2, inside);
                    }
                    // A few grains still falling onto the peak.
                    if ((x == 31 && y == 47) || (x == 34 && y == 52) || (x == 30 && y == 56)) c = sand;
                    c.a = plateA;
                    px[y * n + x] = c;
                }
            t.SetPixels(px);
            t.Apply();
            pile = Sprite.Create(t, new Rect(0f, 0f, n, n), new Vector2(0.5f, 0.5f), 100f);
            pile.hideFlags = HideFlags.HideAndDontSave;
            return pile;
        }

        /// <summary>Drags the folded icon, remembering that it was a drag and not a click.</summary>
        private class IconDrag : MonoBehaviour, IPointerDownHandler, IBeginDragHandler, IDragHandler
        {
            public RectTransform frame;
            public bool dragged;

            public void OnPointerDown(PointerEventData press) { dragged = false; }
            public void OnBeginDrag(PointerEventData move) { dragged = true; }

            public void OnDrag(PointerEventData move)
            {
                if (frame == null) return;
                Canvas c = frame.GetComponentInParent<Canvas>();
                float scale = c != null && c.scaleFactor > 0f ? c.scaleFactor : 1f;
                frame.anchoredPosition += move.delta / scale;
            }
        }

        /// <summary>Drags the panel by its top strip.</summary>
        private class PanelDrag : MonoBehaviour, IDragHandler
        {
            public RectTransform frame;

            public void OnDrag(PointerEventData move)
            {
                if (frame == null) return;
                Canvas c = frame.GetComponentInParent<Canvas>();
                float scale = c != null && c.scaleFactor > 0f ? c.scaleFactor : 1f;
                frame.anchoredPosition += move.delta / scale;
            }
        }

        // ------------------------------------------------------------------ plain, without UI Factory

        private Text brushText, modeText, styleText, mediumText, sizeText, rateText, sourcesText, frozenText, keepText, othersText;
        private UnityEngine.UI.Selectable[] plainHostOnly;
        private UnityEngine.UI.Selectable plainBrush;

        private static UnityEngine.UI.Selectable Owner(Text label)
        {
            return label != null ? label.GetComponentInParent<UnityEngine.UI.Selectable>() : null;
        }

        /// <summary>
        /// The same panel built by hand, for a game without UI Factory: an opaque plate with
        /// rounded corners, drawn here, and buttons on the same plate.
        /// </summary>
        private void BuildPlain()
        {
            if (built || canvas == null) return;
            built = true;
            if (font == null) font = Resources.GetBuiltinResource<Font>("Arial.ttf");

            panelWidth = 250f;
            GameObject p = Rect("SandPanel", canvas.transform);
            panel = p.GetComponent<RectTransform>();
            Pin(panel);
            Rounded(p.AddComponent<Image>(), new Color(0.11f, 0.12f, 0.14f, 1f));

            topHeight = 36f;
            GameObject strip = Rect("Bar", p.transform);
            UIF.Fit(strip.transform as RectTransform, 0f, 0f, panelWidth, topHeight);
            strip.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0f);
            strip.AddComponent<PanelDrag>().frame = panel;
            Text title = Word(strip.transform, "SAND", 20, TextAnchor.MiddleCenter, true);
            Stretch(title.rectTransform, 0f, 0f, 0f, 0f);
            Text fold = Button(strip.transform, Fold);
            fold.text = "-";
            UIF.Fit(fold.transform.parent as RectTransform, panelWidth - 34f, 5f, 26f, 26f);

            GameObject c = Rect("Content", p.transform);
            content = c.GetComponent<RectTransform>();
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.anchoredPosition = new Vector2(0f, -topHeight + Pad);

            brushText = PlainButton(ToggleBrush, false);
            modeText = PlainButton(ToggleMode, false);
            styleText = PlainButton(NextStyle, false);
            mediumText = PlainButton(NextMedium, false);
            sizeText = Stepper(Smaller, Bigger);
            rateText = Stepper(Slower, Faster);

            sourcesText = PlainButton(ToggleSources, false);
            frozenText = PlainButton(ToggleFrozen, false);
            keepText = PlainButton(ToggleKeep, false);
            Text clearText = PlainButton(ClearAll, false);
            clearText.text = "Clear all sand";
            {
                GameObject row = Rect("Files", content);
                AddRow(row, Gap, false);
                float half = (panelWidth - Pad * 2f - Gap) * 0.5f;
                Text save = Button(row.transform, SandFiles.Save);
                save.text = "Save";
                UIF.Fit(save.transform.parent as RectTransform, 0f, 0f, half, RowHeight);
                Text load = Button(row.transform, SandFiles.Load);
                load.text = "Load";
                UIF.Fit(load.transform.parent as RectTransform, half + Gap, 0f, half, RowHeight);
                saveControl = Owner(save);
                loadControl = Owner(load);
                filesRow = row;
            }
            othersText = PlainButton(ToggleOthers, true);
            plainHostOnly = new UnityEngine.UI.Selectable[]
            {
                Owner(sourcesText), Owner(frozenText), Owner(keepText), Owner(othersText), Owner(clearText)
            };
            plainBrush = Owner(brushText);

            Lay();
            Refresh();
            Placed();
        }

        /// <summary>A smaller button, the value, a bigger button.</summary>
        private Text Stepper(UnityEngine.Events.UnityAction down, UnityEngine.Events.UnityAction up)
        {
            GameObject row = Rect("Stepper", content);
            AddRow(row, Gap, false);
            Text minus = Button(row.transform, down);
            UIF.Fit(minus.transform.parent as RectTransform, 0f, 0f, 40f, RowHeight);
            minus.text = "-";
            Text value = Word(row.transform, "", 16, TextAnchor.MiddleCenter, false);
            Stretch(value.rectTransform, 44f, 0f, -44f, 0f);
            Text plus = Button(row.transform, up);
            plus.text = "+";
            RectTransform pr = plus.transform.parent as RectTransform;
            pr.anchorMin = pr.anchorMax = pr.pivot = new Vector2(1f, 1f);
            pr.sizeDelta = new Vector2(40f, RowHeight);
            pr.anchoredPosition = Vector2.zero;
            return value;
        }

        private Text PlainButton(UnityEngine.Events.UnityAction onClick, bool multiplayerOnly)
        {
            Text t = Button(content, onClick);
            AddRow(t.transform.parent.gameObject, Gap, multiplayerOnly);
            return t;
        }

        private static GameObject Rect(string name, Transform parent)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return go;
        }

        private static void Stretch(RectTransform r, float left, float bottom, float right, float top)
        {
            r.anchorMin = Vector2.zero;
            r.anchorMax = Vector2.one;
            r.offsetMin = new Vector2(left, bottom);
            r.offsetMax = new Vector2(right, top);
        }

        /// <summary>A label in the game's font when UI Factory is there, else the built-in one.</summary>
        private Text Word(Transform parent, string text, int fontSize, TextAnchor align, bool bold)
        {
            GameObject go = Rect("Label", parent);
            Text t = go.AddComponent<Text>();
            Font f = null;
            if (!factoryMissing)
            {
                try { f = GameFont(); } catch (System.Exception) { }
            }
            if (f == null)
            {
                if (font == null) font = Resources.GetBuiltinResource<Font>("Arial.ttf");
                f = font;
            }
            t.font = f;
            t.fontSize = fontSize;
            t.fontStyle = bold ? FontStyle.Bold : FontStyle.Normal;
            t.alignment = align;
            t.color = new Color(0.92f, 0.92f, 0.93f);
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            t.raycastTarget = false;
            t.text = text;
            return t;
        }

        // Its own method, so that without UI Factory it is this call that fails, inside the try.
        private static Font GameFont() { return UIF.Font; }

        private Text Button(Transform parent, UnityEngine.Events.UnityAction onClick)
        {
            GameObject go = Rect("Button", parent);
            Image img = go.AddComponent<Image>();
            Rounded(img, new Color(0.22f, 0.24f, 0.27f, 1f));
            UnityEngine.UI.Button b = go.AddComponent<UnityEngine.UI.Button>();
            b.targetGraphic = img;
            ColorBlock cb = b.colors;
            cb.highlightedColor = new Color(1.25f, 1.25f, 1.25f, 1f);
            cb.pressedColor = new Color(0.8f, 0.8f, 0.8f, 1f);
            b.colors = cb;
            b.onClick.AddListener(onClick);

            Text t = Word(go.transform, "", 16, TextAnchor.MiddleCenter, false);
            Stretch(t.rectTransform, 0f, 0f, 0f, 0f);
            return t;
        }

        private static Sprite plate;

        /// <summary>
        /// A plate with rounded, smooth corners as a nine-sliced sprite, so the corners keep
        /// their radius at any size -- the node editor's look, drawn here rather than shipped.
        /// </summary>
        private static void Rounded(Image image, Color colour)
        {
            if (plate == null)
            {
                const int side = 64;
                const float corner = 16f;
                Texture2D t = new Texture2D(side, side, TextureFormat.ARGB32, false);
                t.wrapMode = TextureWrapMode.Clamp;
                t.hideFlags = HideFlags.HideAndDontSave;
                Color[] px = new Color[side * side];
                for (int y = 0; y < side; y++)
                    for (int x = 0; x < side; x++)
                    {
                        float cx = Mathf.Clamp(x + 0.5f, corner, side - corner);
                        float cy = Mathf.Clamp(y + 0.5f, corner, side - corner);
                        float d = Mathf.Sqrt((x + 0.5f - cx) * (x + 0.5f - cx) + (y + 0.5f - cy) * (y + 0.5f - cy));
                        px[y * side + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(corner - d + 0.5f));
                    }
                t.SetPixels(px);
                t.Apply();
                float edge = corner + 1f;
                // Read at 160 pixels a unit, so a corner is ten of the canvas's.
                plate = Sprite.Create(t, new Rect(0f, 0f, side, side), new Vector2(0.5f, 0.5f), 160f, 0,
                                      SpriteMeshType.FullRect, new Vector4(edge, edge, edge, edge));
                plate.hideFlags = HideFlags.HideAndDontSave;
            }
            image.sprite = plate;
            image.type = Image.Type.Sliced;
            image.color = colour;
        }

        // ------------------------------------------------------------------ state

        private void Refresh()
        {
            echo = true;
            if (brushToggle != null) brushToggle.isOn = brushOn;
            if (removeToggle != null) removeToggle.isOn = removing;
            ShowNumber(sizeNumber);
            ShowNumber(rateNumber);
            for (int i = 0; i < advancedNumbers.Count; i++) ShowNumber(advancedNumbers[i]);
            ShowColour();
            if (advancedToggle != null) advancedToggle.isOn = advanced;
            if (mediumChooser != null && mediumChooser.Index != medium)
                mediumChooser.Set(new List<string>(SandMaterials.Names), medium);
            if (styleChooser != null && styleChooser.Index != style)
                styleChooser.Set(new List<string>(SandField.BrushNames), style);
            echo = false;
            if (brushText == null) return;
            brushText.text = "Paint: " + (brushOn ? "ON" : "off");
            modeText.text = "Remove sand: " + (removing ? "ON" : "off");
            styleText.text = "Brush: " + SandField.BrushNames[style];
            mediumText.text = SandMaterials.Names[medium];
            sizeText.text = "Size " + NumberText(size) + " m";
            rateText.text = "Rate " + NumberText(rate) + " m/s";
            sourcesText.text = "Pause sprayers: " + (SandField.SourcesPaused ? "ON" : "off");
            frozenText.text = "Pause sand simulations: " + (SandField.Frozen ? "ON" : "off");
            keepText.text = "Keep changes to sand: " + (SandField.KeepBetweenRuns ? "ON" : "off");
            othersText.text = "Other players place sand: " + (SandNet.AllowOthers ? "ON" : "off");
        }

        private void ToggleOthers() { SandNet.AllowOthers = !SandNet.AllowOthers; Refresh(); }

        private bool lastClient, lastAllowed = true, roleKnown;

        /// <summary>
        /// In multiplayer only the host runs the sand, so on a client the switches that act on
        /// the whole level -- pausing, freezing, keeping, clearing, who may place sand -- are
        /// greyed out, and the brush with them when the host has said other players may not use it.
        /// </summary>
        private void ApplyRole()
        {
            bool client = SandNet.IsClient;
            bool allowed = !client || SandNet.HostAllowsOthers;
            if (roleKnown && client == lastClient && allowed == lastAllowed) return;
            roleKnown = true; lastClient = client; lastAllowed = allowed;

            UnityEngine.UI.Selectable[] hostOnly = { pauseToggle, freezeToggle, keepToggle, othersToggle, clearControl };
            for (int i = 0; i < hostOnly.Length; i++) if (hostOnly[i] != null) hostOnly[i].interactable = !client;
            if (plainHostOnly != null)
                for (int i = 0; i < plainHostOnly.Length; i++) if (plainHostOnly[i] != null) plainHostOnly[i].interactable = !client;
            if (brushToggle != null) brushToggle.interactable = allowed;
            if (plainBrush != null) plainBrush.interactable = allowed;
            if (!allowed && brushOn) { brushOn = false; Refresh(); }
        }

        private void ToggleBrush() { brushOn = !brushOn; Refresh(); }
        private void ToggleMode() { removing = !removing; Refresh(); }
        private void NextMedium() { medium = (medium + 1) % SandMaterials.Count; ResetToMedium(); }
        private void NextStyle() { style = (style + 1) % SandField.BrushNames.Length; Refresh(); }
        private void Slower() { rate = Mathf.Max(MinRate, rate / 1.5f); Refresh(); }
        private void Faster() { rate = Mathf.Min(MaxRate, rate * 1.5f); Refresh(); }
        private void ToggleSources() { SandField.SourcesPaused = !SandField.SourcesPaused; Refresh(); }
        private void ToggleFrozen() { SandField.Frozen = !SandField.Frozen; Refresh(); }
        private void ToggleKeep() { SandField.KeepBetweenRuns = !SandField.KeepBetweenRuns; Refresh(); }

        private void ClearAll()
        {
            SandField f = SandField.Existing;
            if (f != null) f.Clear();
        }

        // The plain panel's steps: the slider's sizes, then half again each time.
        private static readonly float[] Steps = { 0.5f, 1f, 2f, 3f, 4f, 6f, 8f, 10f, 12f, 15f, 20f, 25f };

        private void Smaller()
        {
            float v = size / 1.5f;
            for (int i = Steps.Length - 1; i >= 0; i--) if (Steps[i] < size - 1e-3f) { v = Mathf.Max(v, Steps[i]); break; }
            SetSize(v);
        }

        private void Bigger()
        {
            float v = size * 1.5f;
            for (int i = 0; i < Steps.Length; i++) if (Steps[i] > size + 1e-3f) { v = Steps[i]; break; }
            SetSize(v);
        }

        /// <summary>
        /// Whether a menu of the game's own is up -- pause, save, load, the level settings --
        /// that the panel should get out of the way of. inMenuCounter is a count this panel
        /// adds to while hovered, so only other holds count.
        /// </summary>
        private static bool OtherMenu()
        {
            try { return StatMaster.isMainMenu || StatMaster.inMenuCounter > ZoomGuard.Raised; }
            catch (System.Exception) { return false; }
        }

        // ------------------------------------------------------------------ each frame

        private void Update()
        {
            SandField.Tick();
            SandFiles.Tick();
            AimGodExplosion();
            if (Time.unscaledTime >= nextRemember) { nextRemember = Time.unscaledTime + 0.5f; Remember(); }
            SandSettings.Tick();
            if (saveControl != null || loadControl != null)
            {
                bool files = SandFiles.Available;
                if (files != filesOn)
                {
                    // Greyed out while a machine is running, as the game's own buttons are then, and not growing under
                    // the pointer: UI Factory's hover swell does not ask whether a button works.
                    filesOn = files;
                    if (saveControl != null) saveControl.interactable = files;
                    if (loadControl != null) loadControl.interactable = files;
                    if (filesRow != null)
                    {
                        UIF.SetSwell(filesRow, files);
                        CanvasGroup fade = filesRow.GetComponent<CanvasGroup>();
                        if (fade == null) fade = filesRow.AddComponent<CanvasGroup>();
                        fade.alpha = files ? 1f : 0.4f;
                    }
                }
            }
            if (canvas == null) return;

            // UI Factory, if it is there, loads a moment after this mod; build with it as soon
            // as it is ready, or by hand once it plainly is not coming.
            if (!built)
            {
                // UI Factory calls BuildFactory itself once its prefabs are in (WhenReady, in
                // Start). Asking every frame as well spawned a window before they were, which
                // failed -- and said so in the log -- every frame until they were.
                if (FactoryReady())
                {
                    if (Time.realtimeSinceStartup >= nextFactoryTry)
                    {
                        nextFactoryTry = Time.realtimeSinceStartup + 2f;
                        BuildFactory();
                    }
                }
                else if (factoryMissing || (Modding.ModResource.AllResourcesLoaded && Time.realtimeSinceStartup - startedAt > 5f))
                    BuildPlain();
                if (!built) return;
            }

            // Off at the title screen, with the rest of the interface when Tab hides it,
            // and when another menu opens over the game.
            bool show = !StatMaster.hudHidden && !OtherMenu();
            if (canvas.enabled != show)
            {
                canvas.enabled = show;
                if (!show) CloseLists();
            }

            // The switch about other players is a row only with other players in the game -- the
            // level editor is a multiplayer session of one. Asked twice a second.
            if (Time.unscaledTime >= othersCheckAt)
            {
                othersCheckAt = Time.unscaledTime + 0.5f;
                bool others = SandNet.OthersPresent;
                if (others != othersShown) { othersShown = others; Lay(); }
            }

            ApplyRole();

            // Ours: the pointer is over the panel or an open list, or a press that began on
            // them is still held. All of it is kept from the game -- the cameras' clicks, the
            // brush -- so a click or a drag that starts on the panel does nothing behind it,
            // wherever the pointer is let go.
            bool overPanel = show && panel.gameObject.activeSelf
                && RectTransformUtility.RectangleContainsScreenPoint(panel, Input.mousePosition, null);
            bool overIcon = show && icon != null && icon.activeSelf
                && RectTransformUtility.RectangleContainsScreenPoint(iconRect, Input.mousePosition, null);
            bool overOurs = show && (overPanel || overIcon || ListOpen());
            if (Input.GetMouseButtonDown(0) || Input.GetMouseButtonDown(1))
            {
                pressedOnUs = overOurs;
                // A press that lands on the game's own interface -- the building bars, the level
                // editor's menus, a popup -- is the game's for as long as it is held.
                pressedOnGame = !overOurs && OverGameInterface();
            }
            if (!Input.GetMouseButton(0) && !Input.GetMouseButton(1) && !Input.GetMouseButtonDown(0) && !Input.GetMouseButtonDown(1))
                pressedOnUs = pressedOnGame = false;
            if (overOurs || pressedOnUs) HoldShield(); else ReleaseShield();

            haveSpot = false;
            if (!show || !brushOn) return;

            Camera cam = Camera.main;
            if (cam == null) return;
            FindSpot(cam.ScreenPointToRay(Input.mousePosition));
            if (!haveSpot) return;

            SandField field = SandField.Existing;
            if (field == null && !removing) field = SandField.Instance;
            if (field == null || !field.HasBed) return;

            // The ground under the brush is cast before anything can be poured on it: sand poured
            // where it is not known yet would be lost, and a big brush came out thin and patchy.
            spotReady = SandNet.IsClient || field.BrushReady(spot, radius);

            // A stroke: each press is a new one, so the random brush lays new lumps.
            if (Input.GetMouseButtonDown(0)) strokeSeed = Random.Range(1, 100000);

            // Pour while the left button is held over the world -- a press that began there, not
            // over any interface, and not on the machine, where a click is the game's.
            bool overUi = EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();
            if (!Input.GetMouseButton(0) || pressedOnUs || pressedOnGame || overOurs || overUi || spotOnMachine || !spotReady) return;
            if (OverGameInterface()) return;

            float amount = rate * Time.deltaTime;
            if (removing) field.Erase(spot, radius, amount, style, strokeSeed);
            else field.Paint(spot, radius, amount, BrushRow(field), style, strokeSeed, SandMaterials.PackColour(colour));
        }

        private bool pressedOnUs, pressedOnGame;
        private readonly RaycastHit[] uiHits = new RaycastHit[4];

        /// <summary>
        /// Whether the pointer is over Besiege's own interface. Most of it is not uGUI, which the
        /// EventSystem would know about, but meshes with colliders drawn by cameras of their own
        /// and clicked through OnMouseOver (notes, 09-overlay-ui). So it is asked the way Unity
        /// asks: a ray from each of those cameras, against the layers it both draws and takes
        /// the mouse on. A camera that draws the world's own layer is the world, not interface.
        /// </summary>
        private bool OverGameInterface()
        {
            Vector3 mouse = Input.mousePosition;
            Camera main = Camera.main;
            Camera[] all = Camera.allCameras;
            for (int i = 0; i < all.Length; i++)
            {
                Camera c = all[i];
                if (c == null || c == main || !c.isActiveAndEnabled || c.targetTexture != null) continue;
                if ((c.cullingMask & 1) != 0) continue;
                if (!c.pixelRect.Contains(mouse)) continue;
                int own;
                int mask = c.cullingMask & (heldMasks.TryGetValue(c, out own) ? own : c.eventMask);
                if (mask == 0) continue;
                Ray ray = c.ScreenPointToRay(mouse);
                if (Physics.RaycastNonAlloc(ray, uiHits, c.farClipPlane, mask) > 0) return true;
            }
            return false;
        }
        private bool spotReady = true;
        private bool othersShown;
        private float othersCheckAt;

        private bool ListOpen()
        {
            return (mediumChooser != null && mediumChooser.IsOpen) || (styleChooser != null && styleChooser.IsOpen);
        }

        private void CloseLists()
        {
            if (mediumChooser != null) mediumChooser.Close();
            if (styleChooser != null) styleChooser.Close();
        }

        /// <summary>
        /// With the god tool's explode on click on, the sand under the cursor is given a collider, so the tool's own raycast
        /// meets the sand's surface and the explosion happens there; without one it went through to the ground under the
        /// sand. Only when the cursor's ray reaches the sand before anything solid.
        /// </summary>
        private void AimGodExplosion()
        {
            if (!StatMaster.GodTools.ExplodeOnClick || !StatMaster.levelSimulating) return;
            SandField field = SandField.Existing;
            Camera cam = Camera.main;
            if (field == null || cam == null) return;
            Ray ray = cam.ScreenPointToRay(Input.mousePosition);
            RaycastHit hit;
            float solid = Physics.Raycast(ray, out hit, 2000f) ? hit.distance : 400f;
            for (float d = 0f; d < Mathf.Min(solid, 400f); d += 0.15f)
            {
                Vector3 p = ray.origin + ray.direction * d;
                float h = field.SurfaceAt(p.x, p.z);
                if (h > -9000f && p.y <= h) { field.WantColliderAt(p); return; }
            }
        }

        /// <summary>
        /// Where the cursor's ray meets the world: the nearest solid thing it hits, or the
        /// sand's surface before that, which is not always a collider.
        /// </summary>
        private void FindSpot(Ray ray)
        {
            int n = Physics.RaycastNonAlloc(ray, hits, 2000f);
            float best = float.MaxValue;
            bool machine = false;
            for (int k = 0; k < n; k++)
            {
                Collider c = hits[k].collider;
                if (c == null || c.isTrigger) continue;
                if (hits[k].distance < best)
                {
                    best = hits[k].distance;
                    machine = OnMachine(c);
                }
            }

            // March down the ray to the sand's surface, which only has a collider near a
            // machine.
            SandField field = SandField.Existing;
            if (field != null)
            {
                float far = Mathf.Min(best, 400f);
                for (float d = 0f; d < far; d += 0.15f)
                {
                    Vector3 p = ray.origin + ray.direction * d;
                    float h = field.SurfaceAt(p.x, p.z);
                    if (h > -9000f && p.y <= h)
                    {
                        best = d;
                        machine = false;
                        break;
                    }
                }
            }

            if (best == float.MaxValue) return;
            spot = ray.origin + ray.direction * best;
            spotOnMachine = machine;
            haveSpot = true;
        }

        private static bool OnMachine(Collider c)
        {
            Rigidbody rb = c.attachedRigidbody;
            if (rb == null) return false;
            int id = rb.GetInstanceID();
            bool yes;
            if (!machineBodies.TryGetValue(id, out yes))
            {
                yes = rb.GetComponent<BlockBehaviour>() != null;
                if (machineBodies.Count > 4096) machineBodies.Clear();
                machineBodies[id] = yes;
            }
            return yes;
        }

        // ------------------------------------------------------------------ the circle

        /// <summary>
        /// The brush, drawn as a dashed ring lying on the ground and the sand: each dash is a
        /// thin quad following the surface, raised a little so it is not buried in it.
        /// </summary>
        private void OnRenderObject()
        {
            if (!haveSpot || lineMaterial == null) return;
            if (Camera.current != Camera.main) return;

            SandField field = SandField.Existing;
            float w = Mathf.Max(0.03f, radius * 0.02f);
            Color colour = spotOnMachine || !spotReady ? new Color(0.6f, 0.6f, 0.6f, 0.6f)
                         : (removing ? new Color(1f, 0.35f, 0.25f, 0.9f) : new Color(1f, 0.85f, 0.3f, 0.9f));

            lineMaterial.SetPass(0);
            GL.Begin(GL.QUADS);
            GL.Color(colour);
            if (style == SandField.BrushSquare) DashedSquare(field, radius, w);
            else
            {
                DashedCircle(field, radius, w);
                if (style == SandField.BrushRing) DashedCircle(field, radius * 0.6f, w);
            }
            GL.End();
        }

        private void DashedCircle(SandField field, float r, float w)
        {
            const int segments = 64;
            for (int s = 0; s < segments; s += 2)
            {
                float a0 = s * Mathf.PI * 2f / segments, a1 = (s + 1) * Mathf.PI * 2f / segments;
                Vector3 in0 = Point(field, a0, r - w), out0 = Point(field, a0, r + w);
                Vector3 in1 = Point(field, a1, r - w), out1 = Point(field, a1, r + w);
                GL.Vertex(in0); GL.Vertex(out0); GL.Vertex(out1); GL.Vertex(in1);
            }
        }

        private void DashedSquare(SandField field, float r, float w)
        {
            const int perSide = 16;
            for (int side = 0; side < 4; side++)
            {
                // Each side, corner to corner, as dashes: along it by t, out of it by n.
                Vector2 from, along, outward;
                switch (side)
                {
                    case 0: from = new Vector2(-r, -r); along = new Vector2(1f, 0f); outward = new Vector2(0f, -1f); break;
                    case 1: from = new Vector2(r, -r); along = new Vector2(0f, 1f); outward = new Vector2(1f, 0f); break;
                    case 2: from = new Vector2(r, r); along = new Vector2(-1f, 0f); outward = new Vector2(0f, 1f); break;
                    default: from = new Vector2(-r, r); along = new Vector2(0f, -1f); outward = new Vector2(-1f, 0f); break;
                }
                float step = 2f * r / perSide;
                for (int q = 0; q < perSide; q += 2)
                {
                    Vector2 p0 = from + along * (q * step), p1 = from + along * ((q + 1) * step);
                    GL.Vertex(At(field, p0 - outward * w)); GL.Vertex(At(field, p0 + outward * w));
                    GL.Vertex(At(field, p1 + outward * w)); GL.Vertex(At(field, p1 - outward * w));
                }
            }
        }

        private Vector3 At(SandField field, Vector2 offset)
        {
            float x = spot.x + offset.x, z = spot.z + offset.y;
            float y = spot.y;
            if (field != null)
            {
                float h = field.SurfaceAt(x, z);
                if (h > -9000f && Mathf.Abs(h - spot.y) < 5f) y = h;
            }
            return new Vector3(x, y + 0.04f, z);
        }

        private Vector3 Point(SandField field, float angle, float r)
        {
            float x = spot.x + Mathf.Cos(angle) * r, z = spot.z + Mathf.Sin(angle) * r;
            float y = spot.y;
            if (field != null)
            {
                float h = field.SurfaceAt(x, z);
                if (h > -9000f && Mathf.Abs(h - spot.y) < 5f) y = h;
            }
            return new Vector3(x, y + 0.04f, z);
        }

        // ------------------------------------------------------------------ the shield

        private void HoldShield()
        {
            // Gathered every frame: cameras come and go with the scene.
            Camera[] all = Camera.allCameras;
            for (int i = 0; i < all.Length; i++)
            {
                Camera c = all[i];
                if (c == null || heldMasks.ContainsKey(c)) continue;
                heldMasks[c] = c.eventMask;
                c.eventMask = 0;
            }
        }

        private void ReleaseShield()
        {
            if (heldMasks.Count == 0) return;
            foreach (KeyValuePair<Camera, int> kv in heldMasks)
                if (kv.Key != null) kv.Key.eventMask = kv.Value;
            heldMasks.Clear();
        }
    }
}
