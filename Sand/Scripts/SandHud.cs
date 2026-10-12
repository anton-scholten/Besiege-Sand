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
    ///   Paint | Remove sand      a dashed circle follows the cursor on the ground; hold the
    ///                            left button to pour into it, or with Remove to take sand away
    ///   brush                    its shape
    ///   Size, Rate               the circle's width (slid 1-25 m, typed 0.5-10000 m), how fast
    ///   medium, Colour, Advanced what the brush pours
    ///   Show cosmetic particles  the spray that is only drawn
    ///   Wheel dust               whether wheels raise the sand's dust
    ///   Pause sand simulations   nothing is pressed, ploughed or slumps; machines drive on it as on rock
    ///   Keep changes to sand     the sand is kept between runs rather than put back
    ///   Clear all sand           asked twice: "Clear sand?", a tick and a cross
    ///   Save | Load              the sand as a file of its own
    ///   Allow others to load     host only, with others in the game
    ///   Other players place sand multiplayer only: whether clients may use the brush
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
        private static readonly SandObjectCache machineBodies = new SandObjectCache(4096);
        private Material lineMaterial;

        // The shield: each camera's own eventMask while it is held at zero.
        private readonly Dictionary<Camera, int> heldMasks = new Dictionary<Camera, int>();


        private void Start()
        {
            Recall();
            MakeCanvas();
            startedAt = Time.realtimeSinceStartup;
            // UI Factory is asked for its prefabs from Update, once it has loaded (UIFGate): asked here, before it had, the
            // call failed and took UI Factory away for the whole session.
            lineMaterial = SandLook.DepthMaterial();
            lineMaterial.SetFloat("_ZWrite", 0f);
            lineMaterial.SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Off);
            lineMaterial.renderQueue = 3100;
        }

        private void OnDisable()
        {
            MenuHold(false);
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
            grip = Mathf.Clamp(SandSettings.Get("friction", grip), 0f, 3f);
            softness = Mathf.Clamp(SandSettings.Get("softness", softness), 0.01f, 20f);
            digSpeed = Mathf.Clamp(SandSettings.Get("digSpeed", digSpeed), 0.001f, 1f);
            rutDepth = Mathf.Clamp(SandSettings.Get("rutDepth", rutDepth), 0f, 1f);
            fillSpeed = Mathf.Clamp(SandSettings.Get("fillSpeed", fillSpeed), 0.001f, 3f);
            cloudSize = Mathf.Clamp(SandSettings.Get("cloudSize", cloudSize), 0f, 15f);
            cloudLength = Mathf.Clamp(SandSettings.Get("cloudLength", cloudLength), 0.1f, 15f);
            Recustom();
            SandField.Frozen = SandSettings.Get("pauseSand", SandField.Frozen);
            SandField.ShowCosmetic = SandSettings.Get("showCosmetic", SandField.ShowCosmetic);
            SandField.WheelDust = SandSettings.Get("wheelDust", SandField.WheelDust);
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
            SandSettings.Set("friction", grip);
            SandSettings.Set("softness", softness);
            SandSettings.Set("digSpeed", digSpeed);
            SandSettings.Set("rutDepth", rutDepth);
            SandSettings.Set("fillSpeed", fillSpeed);
            SandSettings.Set("cloudSize", cloudSize);
            SandSettings.Set("cloudLength", cloudLength);
            SandSettings.Set("pauseSand", SandField.Frozen);
            SandSettings.Set("showCosmetic", SandField.ShowCosmetic);
            SandSettings.Set("wheelDust", SandField.WheelDust);
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
        private bool factoryMissing, factoryAsked;

        /// <summary>
        /// Whether UI Factory is there and ready, without falling over when it is not there at
        /// all. Asking UIF.Available directly from Update threw every frame when UI Factory was
        /// not loaded -- the assembly it names cannot be found as the call is compiled -- which
        /// stopped the panel from ever being built, plain or otherwise.
        /// </summary>
        private bool FactoryReady()
        {
            if (factoryMissing || !UIFGate.Loaded) return false;
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
            public bool hostOnly;      // shown to the host only (with multiplayerOnly: in a game with others)
        }

        private readonly List<Row> rows = new List<Row>();
        private RectTransform content;
        private float topHeight;
        private const float Pad = 10f, RowHeight = 30f, Gap = 3f;
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
                bool on = (!r.multiplayerOnly || othersShown) && (!r.advancedOnly || advanced) && (!r.hostOnly || !SandNet.IsClient);
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

        private UnityEngine.UI.Toggle brushToggle, removeToggle, freezeToggle, cosmeticToggle, dustToggle, keepToggle, othersToggle, loadToggle;

        // What a multiplayer client may not use, greyed out there; and the sand file buttons, greyed out while a
        // machine runs (and Load on a client unless the host allows it).
        private readonly List<GameObject> hostOnlyObjects = new List<GameObject>();
        private GameObject saveObject, loadObject;

        private GameObject LastRow() { return rows.Count > 0 ? rows[rows.Count - 1].rect.gameObject : null; }

        /// <summary>The object directly under <paramref name="parent"/> that holds a control: what its swell and its
        /// fade are set on.</summary>
        private static GameObject Top(Component control, Transform parent)
        {
            if (control == null) return null;
            Transform t = control.transform;
            while (t.parent != null && t.parent != parent) t = t.parent;
            return t.gameObject;
        }

        /// <summary>
        /// A control usable or not: greyed out (faded, as the game's own unusable buttons are), deaf, and not growing
        /// under the pointer -- UI Factory's hover swell does not ask whether a control works.
        /// </summary>
        private static void Usable(GameObject go, bool on)
        {
            if (go == null) return;
            UnityEngine.UI.Selectable[] all = go.GetComponentsInChildren<UnityEngine.UI.Selectable>(true);
            for (int i = 0; i < all.Length; i++) if (all[i] != null) all[i].interactable = on;
            Image catcher = null;
            ValueField[] drags = go.GetComponentsInChildren<ValueField>(true);
            for (int i = 0; i < drags.Length; i++) if (drags[i] != null) { drags[i].enabled = on; catcher = drags[i].GetComponent<Image>(); if (catcher != null) catcher.raycastTarget = on; }
            if (UIFGate.Loaded) UIF.SetSwell(go, on);   // the plain panel has no swell, and may be there without UI Factory
            CanvasGroup fade = go.GetComponent<CanvasGroup>();
            if (fade == null) fade = go.AddComponent<CanvasGroup>();
            fade.alpha = on ? 1f : 0.4f;
        }

        // What the panel shows: on a multiplayer client, the host's switches; otherwise this game's own.
        private static bool ShownFrozen { get { return SandNet.IsClient ? SandNet.HostFrozen : SandField.Frozen; } }
        private static bool ShownKeeps { get { return SandNet.IsClient ? SandNet.HostKeeps : SandField.KeepBetweenRuns; } }
        private static bool ShownOthers { get { return SandNet.IsClient ? SandNet.HostAllowsOthers : SandNet.AllowOthers; } }

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
            factoryBuilt = true;
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

            // Over it the wheel does not zoom. It is not a menu just for being under the pointer: Besiege's keys
            // stand down while a menu is open, and the keyboard should reach the game wherever the pointer is.
            // A press on the panel and typing in one of its boxes are held as a menu instead (MenuHold).
            ZoomGuard guard = w.GetComponent<ZoomGuard>();
            if (guard == null) guard = w.AddComponent<ZoomGuard>();
            guard.menu = false;

            topHeight = 32f;
            GameObject strip = Rect("Bar", w.transform);
            UIF.Fit(strip.transform as RectTransform, 0f, 0f, panelWidth, topHeight);
            strip.AddComponent<Image>().color = BarShade;
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
            // The brush: painting and removing side by side, its shape, its size and rate; then what it pours.
            {
                GameObject row = Rect("Brush", content);
                float half = (inner - Gap) * 0.5f;
                brushToggle = HalfToggle(row.transform, "Paint", brushOn, delegate (bool v) { if (!echo) { brushOn = v; Refresh(); } }, 0f, half);
                removeToggle = HalfToggle(row.transform, "Remove sand", removing, delegate (bool v) { if (!echo) { removing = v; Refresh(); } }, half + Gap, half);
                AddRow(row, Gap, false);
            }

            // The spotlight's selectors: arrows step through the choices, the name -- or a right
            // click anywhere on it -- opens the list of all of them.
            styleChooser = Chooser.Make(content, canvas.transform, Pad, 0f, inner, RowHeight,
                                        new List<string>(SandField.BrushNames), style);
            styleChooser.Changed = delegate (int i) { style = i; };
            AddRow(styleChooser.gameObject, Gap, false);

            sizeNumber = new Number("Size", MinSlid, MaxSlid, MinSize, MaxSize, 0.5f,
                                    delegate { return size; }, delegate (float v) { size = v; });
            AddRow(NumberRow(sizeNumber, inner), Gap, false);
            rateNumber = new Number("Rate", MinRateSlid, MaxRateSlid, MinRate, MaxRate, 0.05f,
                                    delegate { return rate; }, delegate (float v) { rate = v; });
            AddRow(NumberRow(rateNumber, inner), Gap, false);

            mediumChooser = Chooser.Make(content, canvas.transform, Pad, 0f, inner, RowHeight,
                                         new List<string>(SandMaterials.Names), medium);
            mediumChooser.Changed = delegate (int i) { medium = i; ResetToMedium(); };
            AddRow(mediumChooser.gameObject, Gap, false);

            AddRow(ColourRow(inner), Gap, false);

            // The medium's own numbers, to make a sand of one's own from it. Shown with the
            // switch; a new medium chosen starts them again from that medium.
            advancedToggle = FactoryToggle("Advanced", advanced, delegate (bool v)
            {
                advanced = v;
                CloseLists();
                Lay();
                // The rows were hidden while their numbers changed with the medium: shown, they show them now.
                if (!echo) Refresh();
            }, Gap, false);
            BuildAdvanced(inner);

            // The switches that act on the whole level are the host's in multiplayer: on a client they show the host's
            // and are greyed out (Usable, ApplyRole). The echo guard keeps Refresh showing those from writing them here.
            cosmeticToggle = FactoryToggle("Show cosmetic particles", SandField.ShowCosmetic, delegate (bool v) { if (!echo) SandField.ShowCosmetic = v; }, Gap, false);
            dustToggle = FactoryToggle("Wheel dust", SandField.WheelDust, delegate (bool v) { if (!echo) SandField.WheelDust = v; }, Gap, false);
            freezeToggle = FactoryToggle("Pause sand simulations", SandField.Frozen, delegate (bool v) { if (!echo) SandField.Frozen = v; }, Gap, false);
            hostOnlyObjects.Add(LastRow());
            keepToggle = FactoryToggle("Keep changes to sand", SandField.KeepBetweenRuns, delegate (bool v) { if (!echo) SandField.KeepBetweenRuns = v; }, Gap, false);
            hostOnlyObjects.Add(LastRow());

            // Clear all sand, asked twice: the button, then a tick or a cross beside "Clear sand?".
            {
                GameObject row = Rect("Clear", content);
                UnityEngine.UI.Selectable main = FactoryButton(row.transform, "Clear all sand", ClearPressed, 0f, inner);
                UnityEngine.UI.Selectable yes = FactoryButton(row.transform, "", ClearConfirmed, 0f, RowHeight);
                UnityEngine.UI.Selectable no = FactoryButton(row.transform, "", ClearCancelled, 0f, RowHeight);
                ClearRow(row, main, yes, no, inner);
                AddRow(row, Gap, false);
                hostOnlyObjects.Add(row);
            }

            // The sand on its own, as a file in the game's save and load screens (see SandFiles).
            {
                GameObject row = Rect("Files", content);
                float half = (panelWidth - Pad * 2f - Gap) * 0.5f;
                saveObject = Top(FactoryButton(row.transform, "Save", SandFiles.Save, 0f, half), row.transform);
                loadObject = Top(FactoryButton(row.transform, "Load", SandFiles.Load, half + Gap, half), row.transform);
                AddRow(row, Gap, false);
            }
            // The host's, in a game with others: whether they may load a sand file in place of everyone's sand.
            loadToggle = FactoryToggle("Allow others to load sand", SandNet.AllowLoad, delegate (bool v) { if (!echo) SandNet.AllowLoad = v; }, Gap, true);
            rows[rows.Count - 1].hostOnly = true;

            // Last, and only with somebody else in the game.
            othersToggle = FactoryToggle("Other players place sand", SandNet.AllowOthers, delegate (bool v) { if (!echo) SandNet.AllowOthers = v; }, Gap, true);
            hostOnlyObjects.Add(LastRow());

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
            grip = from.friction;
            softness = SandMaterials.SoftnessOf(from);
            digSpeed = from.digSpeed;
            rutDepth = from.rutDepth;
            fillSpeed = from.fillSpeed;
            cloudSize = from.cloudSize;
            cloudLength = from.cloudLength;
            Recustom();
            Refresh();
        }

        // The advanced settings (SandMaterials.Custom): each the row's own number, set to the
        // medium's when a medium is chosen.
        private float grip = 0.5f, softness = 0.5f, digSpeed = 0.07f, rutDepth = 0.1f, fillSpeed = 0.6f,
                      cloudSize = 4.5f, cloudLength = 2.2f;

        private SandMaterials.Params BaseRow()
        {
            SandField f = SandField.Existing;
            int m = Mathf.Clamp(medium, 0, SandMaterials.Count - 1);
            return f != null ? f.Materials[m] : SandMaterials.Build()[m];
        }

        private void Recustom()
        {
            custom = SandMaterials.Custom(BaseRow(), grip, softness, digSpeed, rutDepth, fillSpeed, cloudSize, cloudLength);
            customDirty = true;
        }

        /// <summary>
        /// The row the brush pours: the medium itself, or with Advanced on, a row of the panel's
        /// own -- one of the field's variant rows, claimed once, re-seeded when the medium
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
            Advanced("Grip", 0f, 3f, 0.01f, delegate { return grip; }, delegate (float v) { grip = v; }, width);
            Advanced("Softness", 0.01f, 20f, 0.01f, delegate { return softness; }, delegate (float v) { softness = v; }, width);
            Advanced("Dig speed", 0.001f, 1f, 0.001f, delegate { return digSpeed; }, delegate (float v) { digSpeed = v; }, width);
            Advanced("Rut depth", 0f, 1f, 0.01f, delegate { return rutDepth; }, delegate (float v) { rutDepth = v; }, width);
            Advanced("Fill speed", 0.001f, 3f, 0.005f, delegate { return fillSpeed; }, delegate (float v) { fillSpeed = v; }, width);
            Advanced("Cloud size", 0f, 15f, 0.05f, delegate { return cloudSize; }, delegate (float v) { cloudSize = v; }, width);
            Advanced("Cloud length", 0.1f, 15f, 0.05f, delegate { return cloudLength; }, delegate (float v) { cloudLength = v; }, width);
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
        /// colour as six hex digits.
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
            number.Set(SandGuard.Clamp(v, number.Min, number.Max, number.Get()));
            Refresh();
        }

        private void SetSize(float v)
        {
            size = SandGuard.Clamp(v, MinSize, MaxSize, size);
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
        /// A toggle on part of a row. The prefab's caption is a fixed 160 wide and hung over its neighbour, winning
        /// its clicks, so it is pinned to its own toggle and deaf; and its swell grows the lettering in place rather
        /// than the whole toggle sideways into the next one.
        /// </summary>
        private UnityEngine.UI.Toggle HalfToggle(Transform row, string text, bool on, UnityEngine.Events.UnityAction<bool> changed,
                                                 float x, float width)
        {
            GameObject go = UIF.Spawn(UIF.TogglePrefab, row);
            if (go == null) return null;
            UIF.Fit(go.transform as RectTransform, x, 0f, width, RowHeight);
            UIF.NoSwell(go);
            Text label = UIF.Caption(go, text);
            if (label != null)
            {
                RectTransform r = label.rectTransform;
                r.anchorMin = Vector2.zero;
                r.anchorMax = Vector2.one;
                r.offsetMin = Vector2.zero;
                r.offsetMax = Vector2.zero;
                label.raycastTarget = false;
                Swell swell = go.AddComponent<Swell>();
                swell.grows = label.transform;
            }
            UnityEngine.UI.Toggle t = go.GetComponent<UnityEngine.UI.Toggle>();
            if (t == null) t = go.GetComponentInChildren<UnityEngine.UI.Toggle>(true);
            if (t == null) return null;
            t.isOn = on;
            t.onValueChanged.AddListener(changed);
            return t;
        }

        // ------------------------------------------------------------------ clearing, asked twice

        private UnityEngine.UI.Selectable clearMain, clearYes, clearNo;
        private Transform clearRow;
        private Image clearPlate;
        private Color clearShade;
        private Text clearWords;
        private float clearWidth;
        private bool confirming;

        /// <summary>The clear button and its tick and cross, in one row; the two hidden until it is pressed.</summary>
        private void ClearRow(GameObject row, UnityEngine.UI.Selectable main, UnityEngine.UI.Selectable yes,
                              UnityEngine.UI.Selectable no, float width)
        {
            clearRow = row.transform; clearMain = main; clearYes = yes; clearNo = no; clearWidth = width;
            if (main != null)
            {
                clearPlate = main.targetGraphic as Image;
                if (clearPlate != null) clearShade = clearPlate.color;
                clearWords = main.GetComponentInChildren<Text>(true);
            }
            Mark(yes, true);
            Mark(no, false);
            Confirm(false);
        }

        /// <summary>A tick or a cross drawn on a button, as two bars: no font is sure to have the glyphs.</summary>
        private static void Mark(UnityEngine.UI.Selectable button, bool tick)
        {
            if (button == null) return;
            Text words = button.GetComponentInChildren<Text>(true);
            if (words != null) words.text = "";
            Transform at = button.transform;
            if (tick)
            {
                Bar(at, -4f, -2f, 9f, -45f);
                Bar(at, 3f, 1f, 16f, 45f);
            }
            else
            {
                Bar(at, 0f, 0f, 18f, 45f);
                Bar(at, 0f, 0f, 18f, -45f);
            }
        }

        private static void Bar(Transform parent, float x, float y, float length, float angle)
        {
            GameObject go = Rect("Mark", parent);
            RectTransform r = go.transform as RectTransform;
            r.anchorMin = r.anchorMax = new Vector2(0.5f, 0.5f);
            r.pivot = new Vector2(0.5f, 0.5f);
            r.sizeDelta = new Vector2(length, 3f);
            r.anchoredPosition = new Vector2(x, y);
            r.localRotation = Quaternion.Euler(0f, 0f, angle);
            Image img = go.AddComponent<Image>();
            img.color = Color.white;
            img.raycastTarget = false;
        }

        /// <summary>The row as it asks or as it was: "Clear sand?" darker, with the tick and cross to its right, or
        /// "Clear all sand" across the row.</summary>
        private void Confirm(bool asking)
        {
            confirming = asking;
            float mark = RowHeight;
            float mainWidth = asking ? clearWidth - (mark + Gap) * 2f : clearWidth;
            if (clearMain != null) UIF.Fit(Top(clearMain, clearRow).transform as RectTransform, 0f, 0f, mainWidth, RowHeight);
            if (clearYes != null)
            {
                GameObject y = Top(clearYes, clearRow);
                y.SetActive(asking);
                UIF.Fit(y.transform as RectTransform, mainWidth + Gap, 0f, mark, RowHeight);
            }
            if (clearNo != null)
            {
                GameObject n = Top(clearNo, clearRow);
                n.SetActive(asking);
                UIF.Fit(n.transform as RectTransform, mainWidth + Gap + mark + Gap, 0f, mark, RowHeight);
            }
            if (clearWords != null) clearWords.text = asking ? "Clear sand?" : "Clear all sand";
            if (clearPlate != null)
                clearPlate.color = asking ? new Color(clearShade.r * 0.55f, clearShade.g * 0.55f, clearShade.b * 0.55f, clearShade.a) : clearShade;
        }

        private void ClearPressed() { if (SandNet.IsClient) return; Confirm(!confirming); }
        private void ClearConfirmed() { Confirm(false); ClearAll(); }
        private void ClearCancelled() { Confirm(false); }

        /// <summary>A press anywhere but the clear row's own buttons puts the question away.</summary>
        private void ConfirmTick()
        {
            if (!confirming) return;
            if (!Input.GetMouseButtonDown(0) && !Input.GetMouseButtonDown(1)) return;
            Vector3 m = Input.mousePosition;
            if (Over(clearMain, m) || Over(clearYes, m) || Over(clearNo, m)) return;
            Confirm(false);
        }

        private static bool Over(Component c, Vector3 mouse)
        {
            if (c == null || !c.gameObject.activeInHierarchy) return false;
            RectTransform r = c.transform as RectTransform;
            return r != null && RectTransformUtility.RectangleContainsScreenPoint(r, mouse, null);
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
        private bool factoryBuilt;

        /// <summary>The top bar's dark strip over UI Factory's window, and the plain panel's plate.</summary>
        private static readonly Color BarShade = new Color(0f, 0f, 0f, 0.25f), PlainPlate = new Color(0.11f, 0.12f, 0.14f, 1f);
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

        /// <summary>
        /// The folded button: the sand pile on the same background as the open panel's top bar,
        /// its colour and its transparency. Under UI Factory that is the window's own translucent,
        /// blurred background with the bar's dark strip over it, so the icon is a window of its
        /// own with the same strip; on the plain panel the bar is the panel's opaque plate.
        /// The picture itself is clear round the sand (SandIcon).
        /// </summary>
        private void MakeIcon()
        {
            if (icon != null) return;
            if (factoryBuilt) icon = UIF.Spawn(UIF.WindowPrefab, canvas.transform);
            if (icon != null)
            {
                icon.name = "SandIcon";
                Transform bar = Attach.Find(icon.transform, "TopBar");
                if (bar != null) bar.gameObject.SetActive(false);
                ScrollRect scroll = icon.GetComponentInChildren<ScrollRect>(true);
                if (scroll != null) scroll.enabled = false;
                Transform view = Attach.Find(icon.transform, "ScrollView");
                if (view != null) view.gameObject.SetActive(false);
                GameObject strip = Rect("Bar", icon.transform);
                Stretch(strip.GetComponent<RectTransform>(), 0f, 0f, 0f, 0f);
                Image dark = strip.AddComponent<Image>();
                dark.color = BarShade;
                dark.raycastTarget = false;
            }
            else
            {
                icon = Rect("SandIcon", canvas.transform);
                Rounded(icon.AddComponent<Image>(), PlainPlate);
            }
            iconRect = icon.GetComponent<RectTransform>();
            Pin(iconRect);
            iconRect.sizeDelta = new Vector2(IconSize, IconSize);
            GameObject picture = Rect("Pile", icon.transform);
            Stretch(picture.GetComponent<RectTransform>(), 0f, 0f, 0f, 0f);
            Image img = picture.AddComponent<Image>();
            img.raycastTarget = false;
            img.sprite = PileSprite();
            // An Image with no sprite draws a white quad. Nothing is drawn instead: the button
            // still works and can still be dragged, there is simply no picture on it.
            if (img.sprite == null) img.color = new Color(1f, 1f, 1f, 0f);
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
            if (icon.GetComponent<ZoomGuard>() == null) icon.AddComponent<ZoomGuard>();
        }

        private static Sprite pile;

        /// <summary>The texture <c>Mod.xml</c> declares as <c>SandIcon</c>, which the game
        /// loads back out of the mod folder for the button. It is the panel's own little pile
        /// of sand and nothing else -- the mod's Workshop picture is <c>SandThumbnail</c>, a
        /// different file, and the two are not kept in step because they are not one picture.</summary>
        private const string ThumbnailResource = "SandIcon";

        /// <summary>
        /// The folded panel's icon: one pile of sand with grains still falling on it, clear round
        /// the sand (MakeIcon puts the top bar's background behind it).
        ///
        /// It is <b>loaded, not drawn</b>. The picture is a file, <c>Sand/Resources/Icon.png</c>,
        /// made by <c>tools/make-icon.sh</c> out of <see cref="SandIcon"/>; the Node Editor mod
        /// loads its own artwork the same way (<c>ModResource.GetTexture</c>). Drawing it here
        /// instead would mean the art lives in the assembly, where it cannot be edited without
        /// a compiler and cannot be looked at.
        ///
        /// It is deliberately not the mod's Workshop thumbnail, which is <c>SandThumbnail</c>:
        /// that is a picture with the mod's name on it, and it does not read at 52 units.
        ///
        /// If the file cannot be had -- a mod folder without it, say -- the pile is drawn
        /// instead, so the button is never missing. Nothing about the panel depends on which of
        /// the two it got.
        /// </summary>
        private static Sprite PileSprite()
        {
            if (pile != null) return pile;
            Texture texture = Thumbnail();
            // Sprite.Create takes a Texture2D, and a mod texture is one underneath the Texture
            // the resource system hands back. If it somehow is not, there is no sprite to make.
            Texture2D as2d = texture as Texture2D;
            if (as2d == null) return null;
            // The sand is pixel art: smoothing between the pixels is exactly what must not
            // happen to it, and the plate's corners are the one soft edge in the picture.
            as2d.filterMode = FilterMode.Point;
            pile = Sprite.Create(as2d, new Rect(0f, 0f, as2d.width, as2d.height),
                                 new Vector2(0.5f, 0.5f), 100f);
            pile.hideFlags = HideFlags.HideAndDontSave;
            return pile;
        }

        /// <summary>The thumbnail, loaded from the mod folder; or the same picture drawn, if the
        /// file is not there to be loaded. Never null unless the drawing itself fails.</summary>
        private static Texture Thumbnail()
        {
            try
            {
                Texture made = Modding.ModResource.GetTexture(ThumbnailResource).Texture;
                if (made != null) return made;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("Sand: no " + ThumbnailResource + " in the mod folder (" + e.Message
                                 + "); drawing the panel's icon instead.");
            }

            Texture2D drawn = new Texture2D(IconPixels, IconPixels, TextureFormat.ARGB32, false);
            drawn.wrapMode = TextureWrapMode.Clamp;
            drawn.hideFlags = HideFlags.HideAndDontSave;
            drawn.SetPixels(SandIcon.Draw(IconPixels));
            drawn.Apply();
            return drawn;
        }

        /// <summary>How square the icon is, in pixels, when it has to be drawn rather than
        /// loaded. Well over the 52 units it is shown at, so it is sharp on a screen far denser
        /// than this one. The file is drawn at whatever size <c>make-thumbnail.sh</c> is given.</summary>
        private const int IconPixels = 512;

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

        private Text brushText, modeText, styleText, mediumText, sizeText, rateText, frozenText, cosmeticText, dustText, keepText, othersText, loadOthersText;
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
            Rounded(p.AddComponent<Image>(), PlainPlate);

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

            float inner = panelWidth - Pad * 2f;
            {
                GameObject row = Rect("Brush", content);
                AddRow(row, Gap, false);
                float half = (inner - Gap) * 0.5f;
                brushText = Button(row.transform, ToggleBrush);
                UIF.Fit(brushText.transform.parent as RectTransform, 0f, 0f, half, RowHeight);
                modeText = Button(row.transform, ToggleMode);
                UIF.Fit(modeText.transform.parent as RectTransform, half + Gap, 0f, half, RowHeight);
            }
            styleText = PlainButton(NextStyle, false);
            sizeText = Stepper(Smaller, Bigger);
            rateText = Stepper(Slower, Faster);
            mediumText = PlainButton(NextMedium, false);

            cosmeticText = PlainButton(ToggleCosmetic, false);
            dustText = PlainButton(ToggleDust, false);
            frozenText = PlainButton(ToggleFrozen, false);
            keepText = PlainButton(ToggleKeep, false);
            {
                GameObject row = Rect("Clear", content);
                AddRow(row, Gap, false);
                Text main = Button(row.transform, ClearPressed);
                main.text = "Clear all sand";
                Text yes = Button(row.transform, ClearConfirmed);
                Text no = Button(row.transform, ClearCancelled);
                ClearRow(row, Owner(main), Owner(yes), Owner(no), inner);
                hostOnlyObjects.Add(row);
            }
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
                saveObject = Top(Owner(save), row.transform);
                loadObject = Top(Owner(load), row.transform);
            }
            loadOthersText = PlainButton(ToggleLoadOthers, true);
            rows[rows.Count - 1].hostOnly = true;
            othersText = PlainButton(ToggleOthers, true);
            hostOnlyObjects.Add(Top(Owner(frozenText), content));
            hostOnlyObjects.Add(Top(Owner(keepText), content));
            hostOnlyObjects.Add(Top(Owner(othersText), content));
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
            if (!factoryMissing && UIFGate.Loaded)
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
            if (cosmeticToggle != null) cosmeticToggle.isOn = SandField.ShowCosmetic;
            if (dustToggle != null) dustToggle.isOn = SandField.WheelDust;
            if (freezeToggle != null) freezeToggle.isOn = ShownFrozen;
            if (keepToggle != null) keepToggle.isOn = ShownKeeps;
            if (othersToggle != null) othersToggle.isOn = ShownOthers;
            if (loadToggle != null) loadToggle.isOn = SandNet.AllowLoad;
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
            modeText.text = "Remove: " + (removing ? "ON" : "off");
            styleText.text = "Brush: " + SandField.BrushNames[style];
            mediumText.text = SandMaterials.Names[medium];
            sizeText.text = "Size " + NumberText(size) + " m";
            rateText.text = "Rate " + NumberText(rate) + " m/s";
            frozenText.text = "Pause sand simulations: " + (ShownFrozen ? "ON" : "off");
            cosmeticText.text = "Show cosmetic particles: " + (SandField.ShowCosmetic ? "ON" : "off");
            dustText.text = "Wheel dust: " + (SandField.WheelDust ? "ON" : "off");
            keepText.text = "Keep changes to sand: " + (ShownKeeps ? "ON" : "off");
            othersText.text = "Other players place sand: " + (ShownOthers ? "ON" : "off");
            loadOthersText.text = "Allow others to load sand: " + (SandNet.AllowLoad ? "ON" : "off");
        }

        private void ToggleOthers() { if (SandNet.IsClient) return; SandNet.AllowOthers = !SandNet.AllowOthers; Refresh(); }
        private void ToggleLoadOthers() { if (SandNet.IsClient) return; SandNet.AllowLoad = !SandNet.AllowLoad; Refresh(); }

        private bool lastClient, lastAllowed = true, lastSave = true, lastLoad = true, roleKnown;
        private int lastHostState = -1;

        /// <summary>
        /// In multiplayer only the host runs the sand, so on a client the controls that act on the whole level --
        /// freezing, keeping, clearing, loading, who may place sand -- show the host's
        /// settings and are greyed out, and the brush with them when the host has said other players may not use it.
        /// Load is greyed out on a client unless the host allows it, and Save and Load while a machine runs.
        /// </summary>
        private void ApplyRole()
        {
            bool client = SandNet.IsClient;
            bool allowed = !client || SandNet.HostAllowsOthers;
            bool save = SandFiles.Available, load = SandFiles.CanLoad;
            if (client && SandNet.HostStateVersion != lastHostState) { lastHostState = SandNet.HostStateVersion; Refresh(); }
            if (roleKnown && client == lastClient && allowed == lastAllowed && save == lastSave && load == lastLoad) return;
            bool roleChanged = !roleKnown || client != lastClient;
            roleKnown = true; lastClient = client; lastAllowed = allowed; lastSave = save; lastLoad = load;

            for (int i = 0; i < hostOnlyObjects.Count; i++) Usable(hostOnlyObjects[i], !client);
            Usable(saveObject, save);
            Usable(loadObject, load);
            if (brushToggle != null) brushToggle.interactable = allowed;
            if (plainBrush != null) plainBrush.interactable = allowed;
            if (!allowed && brushOn) brushOn = false;
            if (roleChanged) Lay();
            Refresh();
        }

        private void ToggleBrush() { brushOn = !brushOn; Refresh(); }
        private void ToggleMode() { removing = !removing; Refresh(); }
        private void NextMedium() { medium = (medium + 1) % SandMaterials.Count; ResetToMedium(); }
        private void NextStyle() { style = (style + 1) % SandField.BrushNames.Length; Refresh(); }
        private void Slower() { rate = Mathf.Max(MinRate, rate / 1.5f); Refresh(); }
        private void Faster() { rate = Mathf.Min(MaxRate, rate * 1.5f); Refresh(); }
        private void ToggleFrozen() { if (SandNet.IsClient) return; SandField.Frozen = !SandField.Frozen; Refresh(); }
        private void ToggleKeep() { if (SandNet.IsClient) return; SandField.KeepBetweenRuns = !SandField.KeepBetweenRuns; Refresh(); }
        private void ToggleCosmetic() { SandField.ShowCosmetic = !SandField.ShowCosmetic; Refresh(); }
        private void ToggleDust() { SandField.WheelDust = !SandField.WheelDust; Refresh(); }

        private void ClearAll()
        {
            if (SandNet.IsClient) return;
            SandField f = SandField.Existing;
            if (f != null) f.ClearAll();
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
            try { return SandField.InMenu || StatMaster.inMenuCounter > ZoomGuard.Raised; }
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
            if (canvas == null) return;

            // UI Factory, if it is there, loads a moment after this mod; build with it as soon
            // as it is ready, or by hand once it plainly is not coming.
            if (!built)
            {
                // UI Factory calls BuildFactory itself once its prefabs are in (WhenReady, in
                // Start). Asking every frame as well spawned a window before they were, which
                // failed -- and said so in the log -- every frame until they were.
                if (!factoryAsked && UIFGate.Loaded)
                {
                    // Guarded here, at the call: without UI Factory the call itself fails, as the method it calls is
                    // compiled, before any try inside it can catch anything.
                    factoryAsked = true;
                    try { UIF.WhenReady(BuildFactory); }
                    catch (System.Exception) { factoryMissing = true; }
                }
                if (FactoryReady())
                {
                    if (Time.realtimeSinceStartup >= nextFactoryTry)
                    {
                        nextFactoryTry = Time.realtimeSinceStartup + 2f;
                        BuildFactory();
                    }
                }
                else if (factoryMissing || (Modding.ModResource.AllResourcesLoaded && Time.realtimeSinceStartup - startedAt > 5f
                                                && !UIFGate.Loaded))
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
            ConfirmTick();

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
            MenuHold(show && (pressedOnUs || Typing()));

            // A stroke ends when the button comes up or the brush is put away (see SandField.BeginStroke).
            if (inStroke && (!Input.GetMouseButton(0) || !brushOn || !show))
            {
                inStroke = false;
                SandField open = SandField.Existing;
                if (open != null) open.EndStroke();
            }

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
            if (!inStroke && !SandNet.IsClient) { inStroke = true; field.BeginStroke(); }
            if (removing) field.Erase(spot, radius, amount, style, strokeSeed);
            else field.Paint(spot, radius, amount, BrushRow(field), style, strokeSeed, SandMaterials.PackColour(colour));
        }

        private bool pressedOnUs, pressedOnGame;
        private bool menuHeld;

        // A brush stroke, from the press that first pours or removes sand to the release: one step in the machine's
        // undo history (SandField.BeginStroke, SandUndo).
        private bool inStroke;

        /// <summary>
        /// Tells Besiege a menu is open while a press that began on the panel is held -- so a drag there does not
        /// turn the camera or box-select blocks -- and while one of its boxes is being typed into, so the letters
        /// do not drive the camera or fire block keys. Otherwise the keyboard is the game's, pointer over the panel
        /// or not. Counted in ZoomGuard.Raised, so the panel does not take its own hold for another menu's.
        /// </summary>
        private void MenuHold(bool want)
        {
            menuHeld = ZoomGuard.Grip(want, menuHeld);
        }

        private bool Typing()
        {
            EventSystem es = EventSystem.current;
            GameObject at = es != null ? es.currentSelectedGameObject : null;
            if (at == null || panel == null) return false;
            UnityEngine.UI.InputField field = at.GetComponent<UnityEngine.UI.InputField>();
            return field != null && field.isFocused && at.transform.IsChildOf(panel);
        }

        private readonly RaycastHit[] uiHits = new RaycastHit[4];

        /// <summary>
        /// Whether the pointer is over Besiege's own interface. Most of it is not uGUI, which the
        /// EventSystem would know about, but meshes with colliders drawn by cameras of their own
        /// and clicked through OnMouseOver (notes, 09-overlay-ui). So it is asked the way Unity
        /// asks: a ray from each of those cameras, against the layers it both draws and takes
        /// the mouse on. A camera that draws the world's own layer is the world, not interface.
        /// </summary>
        // The scene's cameras, read once a frame and shared. Camera.allCameras builds a fresh array
        // every time it is read, and it is read up to three times a frame wherever the pointer is
        // over the panel or painting; Besiege has a good many cameras (the level editor's, the
        // water's, the depth and water-height renderers'). Once a frame is the right grain: a
        // camera that appears mid-frame is picked up on the next one, which is what gathering
        // every call was doing anyway.
        private Camera[] cameras;
        private float camerasAt = -1f;

        private Camera[] Cameras()
        {
            if (cameras == null || Time.unscaledTime != camerasAt)
            {
                cameras = Camera.allCameras;
                camerasAt = Time.unscaledTime;
            }
            return cameras;
        }

        private bool OverGameInterface()
        {
            Vector3 mouse = Input.mousePosition;
            Camera main = Camera.main;
            Camera[] all = Cameras();
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
            // Cached, and keyed safely: Unity recycles instance IDs after destruction, so a cache
            // of the ID alone would let one body's answer stand for another's.
            bool yes;
            if (!machineBodies.TryGet(rb, out yes))
            {
                yes = rb.GetComponent<BlockBehaviour>() != null;
                machineBodies.Put(rb, yes);
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
            // Gathered once a frame: cameras come and go with the scene.
            Camera[] all = Cameras();
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
