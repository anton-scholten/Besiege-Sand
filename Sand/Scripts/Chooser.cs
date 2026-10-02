using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace SandMod
{
    // A menu control: `< choice >`, and the choice opens a list of all of them.
    //
    // Built out of plain uGUI rather than a UI Factory prefab. The prefab
    // drop-down had two faults in parts a mod cannot reach: its list drew through
    // the rows behind it, and it spilled past its own scroll view so the choices
    // that should have been scrolled away were painted over the world. Everything
    // here is ours instead -- the list is opaque, a RectMask2D clips it, and it
    // hangs off the canvas rather than the scrolling content so nothing clips it
    // in turn.
    public class Chooser : MonoBehaviour
    {
        // Width of one of the step arrows, and the air between it and the face.
        public const float ArrowWidth = 30f;
        public const float ArrowGap = 2f;

        private const float ItemHeight = 26f;
        private const float MaxListHeight = 264f;
        private const float BarWidth = 8f;
        private const int Size = 20;

        // The arrows answer the pointer harder than the name does: they are small,
        // and they are the half of the control that is quick to use.
        private const float ArrowGrown = 1.4f;
        private const float FaceGrown = 1.15f;

        private static readonly Color FaceInk = new Color(0.07f, 0.09f, 0.12f, 0.90f);
        private static readonly Color Sheet = new Color(0.06f, 0.08f, 0.11f, 1f);
        private static readonly Color Edge = new Color(0.24f, 0.29f, 0.35f, 1f);
        private static readonly Color Ink = new Color(0.88f, 0.90f, 0.92f, 1f);
        private static readonly Color Clear = new Color(1f, 1f, 1f, 0f);
        private static readonly Color Lit = new Color(0.35f, 0.55f, 0.75f, 0.55f);
        private static readonly Color Held = new Color(0.45f, 0.65f, 0.85f, 0.75f);
        private static readonly Color Rail = new Color(0.14f, 0.17f, 0.21f, 1f);
        private static readonly Color Grip = new Color(0.42f, 0.48f, 0.56f, 1f);

        private List<string> items = new List<string>();
        private int index;

        // Where the list hangs: the panel's canvas, not the row. A list parented
        // to the scrolling content is clipped by the scroll view the moment it
        // reaches past the bottom of the panel, which is most of the time.
        private Transform root;

        private RectTransform faceRect;
        private Text caption;
        private GameObject list;
        private RectTransform listRect;
        private GameObject blocker;
        private float listHeight;

        public int Index { get { return index; } }

        /// <summary>Whether the list of all the choices is open.</summary>
        public bool IsOpen { get { return list != null && list.activeSelf; } }

        // Told whenever the choice changes by the pointer: an arrow, or an item in
        // the list. Not by Set.
        public System.Action<int> Changed;

        public static Chooser Make(Transform host, Transform root, float x, float y,
            float w, float h, List<string> choices, int picked)
        {
            return Make(host, root, x, y, w, h, choices, picked, true);
        }

        // `arrows` off leaves the face alone: three of these across one row have no
        // width to spare for stepping, and the list is still one click away.
        public static Chooser Make(Transform host, Transform root, float x, float y,
            float w, float h, List<string> choices, int picked, bool arrows)
        {
            GameObject go = new GameObject("Chooser");
            go.transform.SetParent(host, false);
            UIF.Fit(go.AddComponent<RectTransform>(), x, y, w, h);

            Chooser self = go.AddComponent<Chooser>();
            self.root = root;

            if (arrows)
            {
                self.Arrow(go.transform, "<", 0f, h, -1);
                self.Arrow(go.transform, ">", w - ArrowWidth, h, 1);
                self.Face(go.transform, ArrowWidth + ArrowGap,
                    w - (ArrowWidth + ArrowGap) * 2f, h);
            }
            else
            {
                self.Face(go.transform, 0f, w, h);
            }

            self.Set(choices, picked);
            return self;
        }

        // The choices, and which one is on. Called whenever the panel refills
        // itself from the block, so the list is thrown away and rebuilt lazily.
        public void Set(List<string> choices, int picked)
        {
            // Only thrown away when the choices themselves differ. The panel refills
            // itself on every open, and rebuilding a list of forty fonts each time
            // is most of what made opening one slow.
            if (!Same(choices))
            {
                items = choices != null ? choices : new List<string>();
                Discard();
            }
            index = items.Count == 0 ? 0 : Mathf.Clamp(picked, 0, items.Count - 1);
            Recaption();
        }

        // A right click anywhere on the row opens the whole list, as the left one on
        // the name does; a Button answers only the left.
        private class RightClick : MonoBehaviour, IPointerClickHandler
        {
            public System.Action Clicked;

            public void OnPointerClick(PointerEventData click)
            {
                if (click.button == PointerEventData.InputButton.Right && Clicked != null) Clicked();
            }
        }

        private bool Same(List<string> choices)
        {
            if (choices == null) return items.Count == 0;
            if (choices.Count != items.Count) return false;
            for (int i = 0; i < items.Count; i++)
                if (items[i] != choices[i]) return false;
            return true;
        }

        public void Close()
        {
            if (list != null) list.SetActive(false);
            if (blocker != null) blocker.SetActive(false);
        }

        // ---- the row ---------------------------------------------------------

        private void Arrow(Transform parent, string glyph, float x, float h, int step)
        {
            GameObject go = Plate(parent, "Arrow", FaceInk);
            UIF.Fit(go.GetComponent<RectTransform>(), x, 0f, ArrowWidth, h);
            Grow(go, Word(go, glyph, TextAnchor.MiddleCenter), ArrowGrown);
            int by = step;
            Press(go).onClick.AddListener(delegate { Step(by); });
            go.AddComponent<RightClick>().Clicked = Flip;
        }

        private void Face(Transform parent, float x, float w, float h)
        {
            GameObject go = Plate(parent, "Face", FaceInk);
            faceRect = go.GetComponent<RectTransform>();
            UIF.Fit(faceRect, x, 0f, w, h);
            caption = Word(go, "", TextAnchor.MiddleCenter);
            Grow(go, caption, FaceGrown);
            Press(go).onClick.AddListener(Flip);
            go.AddComponent<RightClick>().Clicked = Flip;
        }

        private void Step(int by)
        {
            if (items.Count == 0) return;
            index = (index + by + items.Count) % items.Count;
            Recaption();
            Close();
            if (Changed != null) Changed(index);
        }

        private void Flip()
        {
            if (list != null && list.activeSelf) Close();
            else Open();
        }

        private void Recaption()
        {
            if (caption == null) return;
            caption.text = index >= 0 && index < items.Count ? items[index] : "";
        }

        // ---- the list --------------------------------------------------------

        private void Open()
        {
            Build();
            if (list == null) return;

            // Last of all, so it is drawn over the panel; the blocker just under
            // it, so a click anywhere else closes the list rather than falling
            // through to whatever it was covering.
            blocker.transform.SetAsLastSibling();
            list.transform.SetAsLastSibling();
            blocker.SetActive(true);
            list.SetActive(true);
            Reposition();
        }

        private void Build()
        {
            if (list != null || root == null || faceRect == null) return;

            blocker = Plate(root, "ChooserBlocker", new Color(0f, 0f, 0f, 0.004f));
            RectTransform veil = blocker.GetComponent<RectTransform>();
            veil.anchorMin = Vector2.zero;
            veil.anchorMax = Vector2.one;
            veil.offsetMin = Vector2.zero;
            veil.offsetMax = Vector2.zero;
            Press(blocker).onClick.AddListener(Close);

            // The frame is the border; the viewport inside it is the opaque sheet,
            // a pixel in on every side.
            list = Plate(root, "ChooserList", Edge);
            listRect = list.GetComponent<RectTransform>();
            listRect.anchorMin = new Vector2(0.5f, 0.5f);
            listRect.anchorMax = new Vector2(0.5f, 0.5f);
            listRect.pivot = new Vector2(0f, 1f);

            GameObject viewport = Plate(list.transform, "Viewport", Sheet);
            RectTransform view = viewport.GetComponent<RectTransform>();
            view.anchorMin = Vector2.zero;
            view.anchorMax = Vector2.one;
            view.offsetMin = new Vector2(1f, 1f);
            view.offsetMax = new Vector2(-1f, -1f);
            viewport.AddComponent<RectMask2D>();

            GameObject content = new GameObject("Content");
            content.transform.SetParent(viewport.transform, false);
            RectTransform inside = content.AddComponent<RectTransform>();
            inside.anchorMin = new Vector2(0f, 1f);
            inside.anchorMax = new Vector2(1f, 1f);
            inside.pivot = new Vector2(0.5f, 1f);
            inside.sizeDelta = new Vector2(0f, items.Count * ItemHeight);
            inside.anchoredPosition = Vector2.zero;

            for (int i = 0; i < items.Count; i++) Item(content.transform, i);

            ScrollRect scroll = list.AddComponent<ScrollRect>();
            scroll.viewport = view;
            scroll.content = inside;
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = ItemHeight;
            scroll.inertia = false;

            listHeight = Mathf.Min(items.Count * ItemHeight + 2f, MaxListHeight);

            // A scrollbar only where the choices do not all fit, and the items
            // narrowed to leave room for it.
            if (items.Count * ItemHeight > listHeight)
            {
                view.offsetMax = new Vector2(-(BarWidth + 1f), -1f);
                scroll.verticalScrollbar = Rung(list.transform);
                scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.Permanent;
            }

            // Or the wheel scrolls the list and pulls the camera in at the same
            // time.
            list.AddComponent<ZoomGuard>();

            list.SetActive(false);
            blocker.SetActive(false);
        }

        private void Item(Transform parent, int i)
        {
            GameObject go = Plate(parent, "Item", Color.white);
            RectTransform rect = go.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.sizeDelta = new Vector2(0f, ItemHeight);
            rect.anchoredPosition = new Vector2(0f, -i * ItemHeight);
            Word(go, items[i], TextAnchor.MiddleLeft);

            // The plate is white and the tint decides what is seen of it, so an
            // item is nothing until it is under the pointer.
            Button button = Press(go);
            ColorBlock colours = button.colors;
            colours.normalColor = Clear;
            colours.highlightedColor = Lit;
            colours.pressedColor = Held;
            colours.fadeDuration = 0.05f;
            button.colors = colours;

            int picked = i;
            button.onClick.AddListener(delegate
            {
                index = picked;
                Recaption();
                Close();
                if (Changed != null) Changed(index);
            });
        }

        // Under the face, or above it where there is no room below.
        private void Reposition()
        {
            RectTransform area = root as RectTransform;
            if (listRect == null || faceRect == null || area == null) return;

            Vector3[] box = new Vector3[4];
            faceRect.GetWorldCorners(box);
            Vector3 below = area.InverseTransformPoint(box[0]);
            Vector3 above = area.InverseTransformPoint(box[1]);

            listRect.sizeDelta = new Vector2(faceRect.rect.width, listHeight);
            float top = below.y;
            if (top - listHeight < area.rect.yMin) top = above.y + listHeight;
            listRect.anchoredPosition = new Vector2(below.x, top);
        }

        // The panel is docked under the mapper every frame and the mapper is
        // dragged, so an open list has to keep up with its own row.
        private void LateUpdate()
        {
            if (list != null && list.activeSelf) Reposition();
        }

        private void OnDisable()
        {
            Close();
        }

        private void OnDestroy()
        {
            Discard();
        }

        // The list is not a child of this object, so nothing else collects it.
        private void Discard()
        {
            if (list != null) Destroy(list);
            if (blocker != null) Destroy(blocker);
            list = null;
            listRect = null;
            blocker = null;
        }

        // Qualified: Besiege has a Scrollbar of its own in the global namespace.
        private static UnityEngine.UI.Scrollbar Rung(Transform parent)
        {
            GameObject go = Plate(parent, "Scrollbar", Rail);
            RectTransform rect = go.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(1f, 0f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(1f, 1f);
            rect.offsetMin = new Vector2(-BarWidth - 1f, 1f);
            rect.offsetMax = new Vector2(-1f, -1f);

            GameObject area = new GameObject("SlidingArea");
            area.transform.SetParent(go.transform, false);
            RectTransform slide = area.AddComponent<RectTransform>();
            slide.anchorMin = Vector2.zero;
            slide.anchorMax = Vector2.one;
            slide.offsetMin = Vector2.zero;
            slide.offsetMax = Vector2.zero;

            GameObject grip = Plate(area.transform, "Handle", Grip);
            RectTransform handle = grip.GetComponent<RectTransform>();
            handle.offsetMin = Vector2.zero;
            handle.offsetMax = Vector2.zero;

            UnityEngine.UI.Scrollbar bar = go.AddComponent<UnityEngine.UI.Scrollbar>();
            bar.direction = UnityEngine.UI.Scrollbar.Direction.BottomToTop;
            bar.handleRect = handle;
            bar.targetGraphic = grip.GetComponent<Image>();
            return bar;
        }

        // ---- scraps ----------------------------------------------------------

        private static GameObject Plate(Transform parent, string name, Color colour)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<RectTransform>();
            go.AddComponent<Image>().color = colour;
            return go;
        }

        private static Button Press(GameObject go)
        {
            Button button = go.AddComponent<Button>();
            button.targetGraphic = go.GetComponent<Image>();
            return button;
        }

        private static void Grow(GameObject on, Text label, float by)
        {
            Swell swell = on.AddComponent<Swell>();
            swell.grows = label.transform;
            swell.grown = by;
        }

        private static Text Word(GameObject on, string text, TextAnchor align)
        {
            GameObject go = new GameObject("Label");
            go.transform.SetParent(on.transform, false);
            RectTransform rect = go.AddComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(6f, 0f);
            rect.offsetMax = new Vector2(-6f, 0f);

            Text label = go.AddComponent<Text>();
            label.font = UIF.Font != null
                ? UIF.Font : Resources.GetBuiltinResource<Font>("Arial.ttf");
            label.fontSize = Size;
            label.alignment = align;
            label.color = Ink;
            label.text = text;
            label.horizontalOverflow = HorizontalWrapMode.Overflow;
            label.verticalOverflow = VerticalWrapMode.Overflow;
            // Or it eats the click meant for the item under it.
            label.raycastTarget = false;
            return label;
        }
    }
}
