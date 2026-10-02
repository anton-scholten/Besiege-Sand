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
    ///   Freeze sand              the sand stops being computed, left where it is
    ///   Keep sand                the sand is kept between runs rather than put back
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
                Debug.Log("[Sand] UI Factory is not loaded; the sand panel is a plain one");
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
        }

        private readonly List<Row> rows = new List<Row>();
        private RectTransform content;
        private float topHeight;
        private const float Pad = 10f, RowHeight = 30f, Gap = 6f;
        private float panelWidth;
        private bool collapsed;

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

        private void Lay()
        {

            float y = Pad;
            for (int i = 0; i < rows.Count; i++)
            {
                Row r = rows[i];
                bool on = !r.multiplayerOnly || othersShown;
                if (r.rect.gameObject.activeSelf != on) r.rect.gameObject.SetActive(on);
                if (!on) continue;
                UIF.Fit(r.rect, Pad, y, panelWidth - Pad * 2f, r.height);
                y += r.height + r.gapAfter;
            }
            y += Pad - Gap;
            if (content != null) content.sizeDelta = new Vector2(0f, y);
            if (panel != null) panel.sizeDelta = new Vector2(panelWidth, topHeight + (collapsed ? 0f : y));
        }

        // ------------------------------------------------------------------ in Besiege's own look

        private UnityEngine.UI.Toggle brushToggle, removeToggle, pauseToggle, freezeToggle, keepToggle, othersToggle;
        private UnityEngine.UI.Selectable clearControl;
        private Chooser mediumChooser, styleChooser;
        private Number sizeNumber, rateNumber;
        private Text foldText;
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
            panel.anchorMin = panel.anchorMax = new Vector2(0f, 0.5f);
            panel.pivot = new Vector2(0f, 0.5f);
            panel.anchoredPosition = new Vector2(12f, 0f);
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
                foldText = UIF.Caption(fold, "-");
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
            mediumChooser.Changed = delegate (int i) { medium = i; };
            AddRow(mediumChooser.gameObject, Gap, false);

            sizeNumber = new Number("Size", MinSlid, MaxSlid, MinSize, MaxSize, 0.5f,
                                    delegate { return size; }, delegate (float v) { size = v; });
            AddRow(NumberRow(sizeNumber, inner), Gap, false);
            rateNumber = new Number("Rate", MinRateSlid, MaxRateSlid, MinRate, MaxRate, 0.05f,
                                    delegate { return rate; }, delegate (float v) { rate = v; });
            AddRow(NumberRow(rateNumber, inner), Gap + 4f, false);

            pauseToggle = FactoryToggle("Pause sprayers", SandField.SourcesPaused, delegate (bool v) { SandField.SourcesPaused = v; }, Gap, false);
            freezeToggle = FactoryToggle("Freeze sand", SandField.Frozen, delegate (bool v) { SandField.Frozen = v; }, Gap, false);
            keepToggle = FactoryToggle("Keep sand", SandField.KeepBetweenRuns, delegate (bool v) { SandField.KeepBetweenRuns = v; }, Gap + 4f, false);

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

            // Last, and only with somebody else in the game.
            othersToggle = FactoryToggle("Other players place sand", SandNet.AllowOthers, delegate (bool v) { SandNet.AllowOthers = v; }, Gap, true);

            Lay();
            Refresh();
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
            const float label = 52f, box = 74f, gap = 6f;

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
                    dragField.dragged = delegate (float pixels) { SetNumber(typed, typed.Get() * Mathf.Exp(pixels * 0.01f)); };
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
            return v < 100f ? v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)
                            : v.ToString("0", System.Globalization.CultureInfo.InvariantCulture);
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

        /// <summary>The strip's button folds the panel away to the strip, and back.</summary>
        private void Fold()
        {
            collapsed = !collapsed;
            if (content != null) content.gameObject.SetActive(!collapsed);
            if (foldText != null) foldText.text = collapsed ? "+" : "-";
            CloseLists();
            Lay();
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
            panel.anchorMin = panel.anchorMax = new Vector2(0f, 0.5f);
            panel.pivot = new Vector2(0f, 0.5f);
            panel.anchoredPosition = new Vector2(12f, 0f);
            Rounded(p.AddComponent<Image>(), new Color(0.11f, 0.12f, 0.14f, 1f));

            topHeight = 36f;
            GameObject strip = Rect("Bar", p.transform);
            UIF.Fit(strip.transform as RectTransform, 0f, 0f, panelWidth, topHeight);
            strip.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0f);
            strip.AddComponent<PanelDrag>().frame = panel;
            Text title = Word(strip.transform, "SAND", 20, TextAnchor.MiddleCenter, true);
            Stretch(title.rectTransform, 0f, 0f, 0f, 0f);

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
            othersText = PlainButton(ToggleOthers, true);
            plainHostOnly = new UnityEngine.UI.Selectable[]
            {
                Owner(sourcesText), Owner(frozenText), Owner(keepText), Owner(othersText), Owner(clearText)
            };
            plainBrush = Owner(brushText);

            Lay();
            Refresh();
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
            frozenText.text = "Freeze sand: " + (SandField.Frozen ? "ON" : "off");
            keepText.text = "Keep sand: " + (SandField.KeepBetweenRuns ? "ON" : "off");
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
        private void NextMedium() { medium = (medium + 1) % SandMaterials.Count; Refresh(); }
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
            bool overPanel = show && RectTransformUtility.RectangleContainsScreenPoint(panel, Input.mousePosition, null);
            bool overOurs = show && (overPanel || ListOpen());
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
            else field.Paint(spot, radius, amount, medium, style, strokeSeed);
        }

        private bool pressedOnUs, pressedOnGame;
        private static bool camerasLogged;
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
            if (!camerasLogged)
            {
                // Once, so a menu the brush still pours through can be traced to its camera.
                camerasLogged = true;
                for (int i = 0; i < all.Length; i++)
                    if (all[i] != null)
                        Debug.Log("[Sand] camera '" + all[i].name + "': culling " + all[i].cullingMask.ToString("X")
                                  + ", events " + all[i].eventMask.ToString("X") + (all[i] == main ? " (main)" : ""));
            }
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
