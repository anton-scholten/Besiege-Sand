using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace SandMod
{
    /// <summary>
    /// The sand panel: a small column of buttons on the left of the screen, there while
    /// building and while a run is going, and the brush it drives.
    ///
    ///   Brush            off / on -- a dashed circle follows the cursor on the ground; hold
    ///                    the left button to pour into it
    ///   Mode             add sand / remove sand
    ///   Medium           which of the media the brush pours
    ///   Size             the brush's radius
    ///   Sources          sprayers and regions pour / are paused
    ///   Sand             computed / frozen where it is
    ///   Between runs     the sand goes back to how it was when a run started / is kept
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
        private Text brushText, modeText, mediumText, sizeText, sourcesText, frozenText, keepText;

        private bool brushOn;
        private bool removing;
        private int medium = SandMaterials.Sand;
        private float radius = 1f;
        private static readonly float[] Sizes = { 0.25f, 0.5f, 0.75f, 1f, 1.5f, 2f, 3f, 4f };

        // The brush's spot, found each frame while it is on.
        private bool haveSpot;
        private bool spotOnMachine;
        private Vector3 spot;
        private readonly RaycastHit[] hits = new RaycastHit[32];
        private static readonly Dictionary<int, bool> machineBodies = new Dictionary<int, bool>();
        private Material lineMaterial;

        // The shield: each camera's own eventMask while it is held at zero.
        private readonly Dictionary<Camera, int> heldMasks = new Dictionary<Camera, int>();

        /// <summary>How fast the brush pours, as metres of rise a second over its whole circle.</summary>
        private const float PourRate = 0.6f;

        private void Start()
        {
            Build();
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

        private void Build()
        {
            if (font == null) font = Resources.GetBuiltinResource<Font>("Arial.ttf");

            GameObject root = new GameObject("SandHudCanvas");
            root.transform.SetParent(transform, false);
            canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 28000;
            CanvasScaler scaler = root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 1f;
            root.AddComponent<GraphicRaycaster>();

            const float width = 230f, row = 34f, gap = 4f;
            string[] rows = { "title", "brush", "mode", "medium", "size", "sources", "frozen", "keep", "clear" };
            float height = rows.Length * (row + gap) + 12f;

            GameObject p = Rect("Panel", root.transform);
            panel = p.GetComponent<RectTransform>();
            panel.anchorMin = panel.anchorMax = new Vector2(0f, 0.5f);
            panel.pivot = new Vector2(0f, 0.5f);
            panel.anchoredPosition = new Vector2(12f, 0f);
            panel.sizeDelta = new Vector2(width, height);
            Image bg = p.AddComponent<Image>();
            bg.color = new Color(0.08f, 0.09f, 0.1f, 0.78f);

            float y = -8f;
            Label(p.transform, "SAND", y, width, row, 20, FontStyle.Bold);
            y -= row + gap;
            brushText = Button(p.transform, y, width, row, ToggleBrush); y -= row + gap;
            modeText = Button(p.transform, y, width, row, ToggleMode); y -= row + gap;
            mediumText = Button(p.transform, y, width, row, NextMedium); y -= row + gap;

            // Size: a smaller button, the value, a bigger button.
            Button(p.transform, y, 40f, row, Smaller, 8f).text = "-";
            sizeText = Label(p.transform, "", y, width, row, 16, FontStyle.Normal);
            Button(p.transform, y, 40f, row, Bigger, width - 48f).text = "+";
            y -= row + gap;

            sourcesText = Button(p.transform, y, width, row, ToggleSources); y -= row + gap;
            frozenText = Button(p.transform, y, width, row, ToggleFrozen); y -= row + gap;
            keepText = Button(p.transform, y, width, row, ToggleKeep); y -= row + gap;
            Button(p.transform, y, width, row, ClearAll).text = "Clear all sand";

            Refresh();
        }

        private static GameObject Rect(string name, Transform parent)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return go;
        }

        private static Text Label(Transform parent, string text, float y, float width, float height, int size, FontStyle style)
        {
            GameObject go = Rect("Label", parent);
            RectTransform r = go.GetComponent<RectTransform>();
            r.anchorMin = r.anchorMax = new Vector2(0f, 1f);
            r.pivot = new Vector2(0f, 1f);
            r.anchoredPosition = new Vector2(0f, y);
            r.sizeDelta = new Vector2(width, height);
            Text t = go.AddComponent<Text>();
            t.font = font;
            t.fontSize = size;
            t.fontStyle = style;
            t.alignment = TextAnchor.MiddleCenter;
            t.color = new Color(0.92f, 0.9f, 0.85f);
            t.raycastTarget = false;
            t.text = text;
            return t;
        }

        private static Text Button(Transform parent, float y, float width, float height,
                                   UnityEngine.Events.UnityAction onClick)
        {
            return Button(parent, y, width - 16f, height, onClick, 8f);
        }

        private static Text Button(Transform parent, float y, float width, float height,
                                   UnityEngine.Events.UnityAction onClick, float x)
        {
            GameObject go = Rect("Button", parent);
            RectTransform r = go.GetComponent<RectTransform>();
            r.anchorMin = r.anchorMax = new Vector2(0f, 1f);
            r.pivot = new Vector2(0f, 1f);
            r.anchoredPosition = new Vector2(x, y);
            r.sizeDelta = new Vector2(width, height);
            Image img = go.AddComponent<Image>();
            img.color = new Color(0.22f, 0.24f, 0.26f, 0.95f);
            UnityEngine.UI.Button b = go.AddComponent<UnityEngine.UI.Button>();
            b.targetGraphic = img;
            ColorBlock cb = b.colors;
            cb.highlightedColor = new Color(1.25f, 1.25f, 1.25f, 1f);
            cb.pressedColor = new Color(0.8f, 0.8f, 0.8f, 1f);
            b.colors = cb;
            b.onClick.AddListener(onClick);

            GameObject label = Rect("Text", go.transform);
            RectTransform lr = label.GetComponent<RectTransform>();
            lr.anchorMin = Vector2.zero; lr.anchorMax = Vector2.one;
            lr.offsetMin = lr.offsetMax = Vector2.zero;
            Text t = label.AddComponent<Text>();
            t.font = font;
            t.fontSize = 16;
            t.alignment = TextAnchor.MiddleCenter;
            t.color = new Color(0.92f, 0.9f, 0.85f);
            t.raycastTarget = false;
            return t;
        }

        private void Refresh()
        {
            if (brushText == null) return;
            brushText.text = "Brush: " + (brushOn ? "ON" : "off");
            modeText.text = removing ? "Mode: remove sand" : "Mode: add sand";
            mediumText.text = "Medium: " + SandMaterials.Names[medium];
            sizeText.text = "Size " + (radius * 2f).ToString("0.##") + " m";
            sourcesText.text = SandField.SourcesPaused ? "Sprayers: PAUSED" : "Sprayers: pouring";
            frozenText.text = SandField.Frozen ? "Sand: FROZEN" : "Sand: moving";
            keepText.text = SandField.KeepBetweenRuns ? "Between runs: keep" : "Between runs: reset";
        }

        private void ToggleBrush() { brushOn = !brushOn; Refresh(); }
        private void ToggleMode() { removing = !removing; Refresh(); }
        private void NextMedium() { medium = (medium + 1) % SandMaterials.Count; Refresh(); }
        private void ToggleSources() { SandField.SourcesPaused = !SandField.SourcesPaused; Refresh(); }
        private void ToggleFrozen() { SandField.Frozen = !SandField.Frozen; Refresh(); }
        private void ToggleKeep() { SandField.KeepBetweenRuns = !SandField.KeepBetweenRuns; Refresh(); }

        private void ClearAll()
        {
            SandField f = SandField.Existing;
            if (f != null) f.Clear();
        }

        private void Smaller() { Step(-1); }
        private void Bigger() { Step(1); }

        private void Step(int by)
        {
            int at = 0;
            for (int i = 0; i < Sizes.Length; i++) if (Mathf.Abs(Sizes[i] - radius) < 1e-3f) at = i;
            radius = Sizes[Mathf.Clamp(at + by, 0, Sizes.Length - 1)];
            Refresh();
        }

        // ------------------------------------------------------------------ each frame

        private void Update()
        {
            if (canvas == null) return;

            // Off at the title screen, and with the rest of the interface when Tab hides it.
            bool show = !StatMaster.isMainMenu && !StatMaster.hudHidden;
            if (canvas.enabled != show) canvas.enabled = show;

            bool overPanel = show && RectTransformUtility.RectangleContainsScreenPoint(panel, Input.mousePosition, null);
            if (overPanel) HoldShield(); else ReleaseShield();

            haveSpot = false;
            if (!show || !brushOn) return;

            Camera cam = Camera.main;
            if (cam == null) return;
            FindSpot(cam.ScreenPointToRay(Input.mousePosition));
            if (!haveSpot) return;

            SandField field = SandField.Existing;
            if (field == null && !removing) field = SandField.Instance;
            if (field == null || !field.HasBed) return;

            // The ground under the brush is cast before anything can be poured on it.
            field.PrepareGround(new Bounds(spot, new Vector3(radius * 2f + 1f, 1f, radius * 2f + 1f)), 2f);

            // Pour while the left button is held over the world -- not over any interface, and
            // not on the machine, where a click is the game's for placing a block.
            bool overUi = EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();
            if (!Input.GetMouseButton(0) || overUi || spotOnMachine) return;

            float area = Mathf.PI * radius * radius;
            if (removing) field.Erase(spot, radius, PourRate * 2f * Time.deltaTime);
            else field.Paint(spot, radius, area * PourRate * Time.deltaTime, medium);
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
            const int segments = 64;
            float w = Mathf.Max(0.03f, radius * 0.04f);
            Color colour = spotOnMachine ? new Color(0.6f, 0.6f, 0.6f, 0.6f)
                         : (removing ? new Color(1f, 0.35f, 0.25f, 0.9f) : new Color(1f, 0.85f, 0.3f, 0.9f));

            lineMaterial.SetPass(0);
            GL.Begin(GL.QUADS);
            GL.Color(colour);
            for (int s = 0; s < segments; s += 2)
            {
                float a0 = s * Mathf.PI * 2f / segments, a1 = (s + 1) * Mathf.PI * 2f / segments;
                Vector3 in0 = Point(field, a0, radius - w), out0 = Point(field, a0, radius + w);
                Vector3 in1 = Point(field, a1, radius - w), out1 = Point(field, a1, radius + w);
                GL.Vertex(in0); GL.Vertex(out0); GL.Vertex(out1); GL.Vertex(in1);
            }
            GL.End();
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
