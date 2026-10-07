using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace SandMod
{
    // A block's settings drawn in Besiege's own interface, through UI Factory,
    // docked under the block mapper so the two read as one window with a seam.
    //
    // The half that is the same for every block: the canvas, the mapper hooks, the
    // docking, the row primitives and the commit. A subclass says which block it
    // serves and lays its rows out. Rows are built once and rebound -- see Bond.
    //
    // UI Factory is a soft dependency (see UIF); without it none of this runs.
    public abstract class Panel : MonoBehaviour
    {
        private const int CanvasOrder = 2400;
        private const float DefaultWidth = 434f;
        protected const float Margin = 12f;
        private const float RowHeight = 26f;
        private const float RowGap = 4f;
        private const float LabelWidth = 104f;

        // The box on the right of a slider, wide enough for a four-figure number.
        private const float ValueWidth = 72f;

        // How wide the naming half of a selector is drawn. The arrows go outside
        // it, so the whole control is this plus two of them.
        private const float SelectorWidth = 250f;

        // The strip down the right that the scrollbar lives in. The rows stop
        // short of it, or the bar is drawn over the end of every one of them.
        private const float BarGutter = 18f;

        // The window scrolls, so it never has to be as tall as its contents: it
        // takes what is under the mapper, up to this.
        private const float MaxHeight = 720f;
        private const float MinHeight = 180f;

        private static readonly Vector2 Reference = new Vector2(1920f, 1080f);
        private static readonly Color DimInk = new Color(0.45f, 0.45f, 0.48f, 1f);
        private static readonly Color RuleInk = new Color(0.55f, 0.62f, 0.72f, 0.30f);

        private const float RuleHeight = 2f;
        private const float RuleGap = 3f;

        // The panel gave up, so the stock mapper is the only way to set the block
        // and has to keep its controls.
        public static bool Failed { get; private set; }

        private float width = DefaultWidth;
        private Transform host;

        // Where a row is put. The host, unless something is gathering rows into a
        // container of its own -- see AutoRow.
        private Transform pen;

        private GameObject window;
        private RectTransform windowRect;
        private Canvas canvas;
        private Camera mapperEye;
        private ScrollRect scroll;
        private float contentHeight;
        private bool hooked;
        private bool built;
        private bool ready;

        // True while the panel is writing its controls from the block, so their own
        // change events do not echo back. `echo` is the narrower case: a box and
        // the track beside it each drive the other.
        private bool filling;
        private bool echo;

        // Settings changed here that Besiege has not been told about yet. Assigning
        // MapperType.Value writes the live value only, so a panel that stopped there
        // would work now and be forgotten on save. Committing reserialises the block
        // and adds an undo entry, so a drag writes live every frame and commits once.
        private readonly List<MapperType> pending = new List<MapperType>();

        // ---- what a subclass supplies ----------------------------------------

        // The block this panel serves, or null if the mapper opened on another.
        protected abstract Component Serves(BlockMapper mapper);

        // Builds the rows. `host` is where they go; return the height used.
        protected abstract float Layout();

        // ---- the block's controls --------------------------------------------

        // One of the block's controls, held by key rather than by reference. The
        // rows read and write through this, and every key is looked up again each
        // time the mapper opens, so the same rows serve every block of the kind.
        private class Bond
        {
            public string Key;
            public MapperType Control;

            public MSlider Slider { get { return Control as MSlider; } }
            public MColourSlider Colour { get { return Control as MColourSlider; } }
            public MMenu Menu { get { return Control as MMenu; } }
            public MToggle Toggle { get { return Control as MToggle; } }
            public MText Text { get { return Control as MText; } }
        }

        private SaveableDataHolder holder;
        private string missing;

        // Every control the layout asked for, in the order it asked. Also what the
        // stock mapper is stripped of.
        private readonly List<Bond> bonds = new List<Bond>();

        private readonly List<Bond> menus = new List<Bond>();
        private readonly List<Chooser> selectors = new List<Chooser>();
        private readonly List<int> showing = new List<int>();

        private readonly List<Bond> bars = new List<Bond>();
        private readonly List<UnityEngine.UI.Slider> tracks = new List<UnityEngine.UI.Slider>();

        private readonly List<Bond> hues = new List<Bond>();
        private readonly List<UnityEngine.UI.Slider> hueBars = new List<UnityEngine.UI.Slider>();

        private readonly List<Bond> fields = new List<Bond>();
        private readonly List<UnityEngine.UI.InputField> boxes = new List<UnityEngine.UI.InputField>();

        private readonly List<Bond> switches = new List<Bond>();
        private readonly List<UnityEngine.UI.Toggle> flips = new List<UnityEngine.UI.Toggle>();

        private readonly List<Group> groups = new List<Group>();
        // Closed innermost first, which is why everything walks it backwards: a
        // group has to be shaded after the section it sits in.
        private readonly List<Fold> folds = new List<Fold>();
        private readonly List<Fold> nesting = new List<Fold>();

        private Fold Innermost
        {
            get { return nesting.Count == 0 ? null : nesting[nesting.Count - 1]; }
        }

        // Binds a key, remembering it for the rebind. The control it answers with
        // is only for laying the row out -- its caption, its range, its choices,
        // all of which every block of the kind agrees on.
        private Bond Bind<T>(string key) where T : MapperType
        {
            Bond bond = new Bond();
            bond.Key = key;
            bond.Control = Look(key) as T;
            if (bond.Control == null && missing == null) missing = key;
            bonds.Add(bond);
            return bond;
        }

        // Scanned rather than looked up: SaveableDataHolder.GetMapperType compares
        // "bmt-" + Key against its argument, so it answers null for the key the
        // block actually registered.
        private MapperType Look(string key)
        {
            if (holder == null || holder.MapperTypes == null) return null;
            for (int i = 0; i < holder.MapperTypes.Count; i++)
            {
                MapperType one = holder.MapperTypes[i];
                if (one != null && one.Key == key) return one;
            }
            return null;
        }

        // Points every row at the block whose mapper is open now.
        private bool Rebind()
        {
            for (int i = 0; i < bonds.Count; i++)
            {
                bonds[i].Control = Look(bonds[i].Key);
                if (bonds[i].Control == null)
                {
                    Debug.LogWarning("[Sand] this block has no mapper control '"
                        + bonds[i].Key + "', so the panel steps aside.");
                    return false;
                }
            }
            return true;
        }

        // True if every key the layout asked for was there. Otherwise it says which
        // was not, and what the block does have, and the stock mapper stands.
        protected bool Complete()
        {
            if (missing == null) return true;
            Debug.LogWarning("[Sand] no mapper control '" + missing
                + "'. The block registered: " + Keys());
            return false;
        }

        private string Keys()
        {
            if (holder == null || holder.MapperTypes == null) return "(nothing)";
            System.Text.StringBuilder all = new System.Text.StringBuilder();
            for (int i = 0; i < holder.MapperTypes.Count; i++)
            {
                MapperType one = holder.MapperTypes[i];
                if (one == null) continue;
                if (all.Length > 0) all.Append(", ");
                // Key only: GetType().Name is MemberInfo.get_Name, which the mod
                // loader's blacklist refuses.
                all.Append(one.Key);
            }
            return all.ToString();
        }

        // ---- declarative rows ------------------------------------------------

        // Cleared here rather than in Serves: the close-poll calls Serves every
        // frame, and clearing there would empty these while the panel is open.
        protected void StartLayout()
        {
            missing = null;
            bonds.Clear();
            menus.Clear(); selectors.Clear(); showing.Clear();
            bars.Clear(); tracks.Clear();
            hues.Clear(); hueBars.Clear(); cells.Clear();
            fields.Clear(); boxes.Clear();
            switches.Clear(); flips.Clear();
            groups.Clear();
            folds.Clear(); nesting.Clear();
        }

        private float Full { get { return width - Margin * 2f - gutter; } }

        // Room kept clear down the right for the scrollbar.
        private float gutter = BarGutter;

        // Whether this panel is long enough to need a scrollbar. A panel that says
        // no keeps no room for one and its rows have the full width -- eighteen
        // pixels a row given back. Say no only for a panel that fits inside
        // MaxHeight however its switches are set; the warning in Build catches one
        // that has since grown past it.
        protected virtual bool Scrolls { get { return true; } }
        private float TrackX { get { return Margin + LabelWidth; } }
        private float TrackW { get { return Full - LabelWidth - ValueWidth - RowGap; } }

        // A `< choice >` selector, centred in its row: the arrows step through the
        // choices, the name between them opens the whole list.
        protected void Selector(ref float y, string key)
        {
            Bond bond = Bind<MMenu>(key);
            MMenu menu = bond.Menu;
            if (menu == null) return;

            float w = Mathf.Min(SelectorWidth + (Chooser.ArrowWidth + Chooser.ArrowGap) * 2f, Full);
            Chooser control = Chooser.Make(pen, canvas.transform,
                Margin + (Full - w) * 0.5f, y, w, RowHeight, menu.Items, menu.Value);
            menus.Add(bond); selectors.Add(control); showing.Add(menu.Value);
            y += RowHeight + RowGap;
        }

        // Caption, track, and a box on the right holding the number -- which can be
        // typed into and dragged, so a value the track cannot reach is still
        // reachable. See Write for when one outside the range is kept.
        protected void Bar(ref float y, string key)
        {
            Bar(ref y, key, false);
        }

        // `shade` puts a black-to-white block in the value box, for a slider whose
        // number is an amount of something: an opacity reads better as a shade than
        // as 0.75.
        protected void Bar(ref float y, string key, bool shade)
        {
            Bond bond = Bind<MSlider>(key);
            MSlider slider = bond.Slider;
            if (slider == null) return;

            Label(Caption(slider), Margin, y, LabelWidth, TextAnchor.MiddleLeft);
            UnityEngine.UI.Slider track = MakeSlider(TrackX, y, TrackW, slider.Min, slider.Max);
            Cellar cell = Cell(TrackX + TrackW + RowGap, y, ValueWidth, bond, false);
            Couple(cell, track, shade);

            bars.Add(bond); tracks.Add(track);
            y += RowHeight + RowGap;
        }

        // A colour, as a band of hue, with the hex beside it so it can be read off
        // and typed in. Alpha is left alone: where a block has one it is its own
        // slider, and a second would be the same setting twice.
        protected void Hue(ref float y, string key)
        {
            Bond bond = Bind<MColourSlider>(key);
            MColourSlider colour = bond.Colour;
            if (colour == null) return;

            Label(Caption(colour), Margin, y, LabelWidth, TextAnchor.MiddleLeft);
            float trackW = Full - LabelWidth - ValueWidth - RowGap;
            UnityEngine.UI.Slider bar = MakeSlider(TrackX, y, trackW, 0f, 1f);
            Cellar hex = Cell(TrackX + trackW + RowGap, y, ValueWidth, bond, true);

            if (bar != null)
            {
                Rainbow(bar);
                Bond target = bond;
                Cellar box = hex;
                bar.onValueChanged.AddListener(delegate(float v) { Picked(target, box, v); });
            }
            hex.Band = bar;
            hues.Add(bond); hueBars.Add(bar);
            y += RowHeight + RowGap;
        }

        protected void Field(ref float y, string key)
        {
            Bond bond = Bind<MText>(key);
            if (bond.Text == null) return;

            Label(Caption(bond.Text), Margin, y, LabelWidth, TextAnchor.MiddleLeft);
            UnityEngine.UI.InputField box = MakeInput(TrackX, y, Full - LabelWidth);
            Bond target = bond;
            if (box != null) box.onEndEdit.AddListener(delegate(string typed) { Typed(target, typed); });
            fields.Add(bond); boxes.Add(box);
            y += RowHeight + RowGap;
        }


        // ---- the swapping row ------------------------------------------------

        // A value with a switch beside it that swaps it for two or three others:
        // the Spot Light's Auto sweep, the emitter's Changes pair. Both layouts go
        // in one band of fixed height, so the switch changes what is in the row
        // without moving anything below it. `colour` makes the value a band of hue
        // and the rest of the boxes hex.



        private void Band(ref float y, Bond value, Bond toggle, string caption,
            bool colour, bool shade, string[] names, Bond[] bonds, bool[] colours)
        {
            if (value.Control == null || toggle.Control == null) return;
            for (int i = 0; i < bonds.Length; i++)
                if (bonds[i].Control == null) return;

            float rest = Full - AutoLabel - AutoSwitch - RowGap * 2f;
            float restX = Margin + AutoLabel + AutoSwitch + RowGap * 2f;

            // One object holding both layouts, so the panel's own clipping has a
            // single row to decide about and the swap is free to hide what it likes
            // inside it.
            GameObject band = Cradle(y, TinyHeight + RowHeight);
            Transform outer = pen;
            pen = band.transform;

            Group group = new Group();
            group.Auto = toggle;

            Label(Caption(value.Control), Margin, TinyHeight, AutoLabel, TextAnchor.MiddleLeft);
            if (caption != null)
            {
                group.Switch = MakeToggle(caption, Margin + AutoLabel + RowGap,
                    TinyHeight, AutoSwitch);
                if (group.Switch != null)
                {
                    Bond target = toggle;
                    group.Switch.onValueChanged.AddListener(delegate(bool on) { Flipped(target, on); });
                }
            }

            Fixed(group, value, restX, rest, colour, shade);
            Swept(group, restX, rest, names, bonds, colours, shade);

            pen = outer;
            groups.Add(group);
            y += TinyHeight + RowHeight + RowGap;
        }




        // How the caption and the switch divide the left of an auto row. The
        // switch is wide enough for "Changes", which is the longest thing on one.
        private const float AutoLabel = 84f;
        private const float AutoSwitch = 84f;

        // The one number, shown while Auto is off.
        private void Fixed(Group group, Bond value, float x, float w, bool colour, bool shade)
        {
            float trackW = w - ValueWidth - RowGap;

            MSlider slider = value.Slider;
            UnityEngine.UI.Slider track = colour
                ? MakeSlider(x, TinyHeight, trackW, 0f, 1f)
                : MakeSlider(x, TinyHeight, trackW, slider.Min, slider.Max);
            Cellar cell;

            if (colour)
            {
                if (track != null) Rainbow(track);
                cell = Cell(x + trackW + RowGap, TinyHeight, ValueWidth, value, true);
                cell.Band = track;
                if (track != null)
                {
                    Bond target = value;
                    Cellar box = cell;
                    track.onValueChanged.AddListener(delegate(float v) { Picked(target, box, v); });
                }
                hues.Add(value); hueBars.Add(track);
            }
            else
            {
                cell = Cell(x + trackW + RowGap, TinyHeight, ValueWidth, value, false);
                Couple(cell, track, shade);
                bars.Add(value); tracks.Add(track);
            }

            if (track != null) group.Plain.Add(track.gameObject);
            if (cell.Box != null) group.Plain.Add(cell.Box.gameObject);
        }

        // Ties a value box to the slider beside it: the box follows the track as it
        // is dragged, and takes the edge preview if the row shows one.
        private void Couple(Cellar cell, UnityEngine.UI.Slider track, bool shade)
        {
            cell.Track = track;
            if (shade) cell.Swatch = Stroke(cell.Box);
            if (track == null) return;

            Cellar shown = cell;
            track.onValueChanged.AddListener(delegate(float v) { Dragged(shown, v); });
        }

        // The boxes that replace the one value, with their names above them, shown
        // while the switch is on. Each can be typed into or dragged sideways.
        private void Swept(Group group, float x, float w, string[] names,
            Bond[] bonds, bool[] colours, bool shade)
        {
            int count = names.Length;
            float each = (w - RowGap * (count - 1)) / count;

            for (int i = 0; i < count; i++)
            {
                float at = x + (each + RowGap) * i;
                group.Swept.Add(Tiny(names[i], at, 0f, each));

                Cellar cell = Cell(at, TinyHeight, each, bonds[i], colours[i]);
                if (shade) cell.Swatch = Stroke(cell.Box);
                if (cell.Box != null) group.Swept.Add(cell.Box.gameObject);
            }
        }

        // One layout or the other, never both.
        private static void Swap(Group group)
        {
            bool on = group.Auto.Toggle != null && group.Auto.Toggle.IsActive;
            Reveal(group.Plain, !on);
            Reveal(group.Swept, on);
        }

        private static void Reveal(List<GameObject> parts, bool on)
        {
            for (int i = 0; i < parts.Count; i++)
                if (parts[i] != null && parts[i].activeSelf != on) parts[i].SetActive(on);
        }

        private class Group
        {
            public Bond Auto;
            public UnityEngine.UI.Toggle Switch;
            // The two layouts sharing the band: the one number, and the three.
            public readonly List<GameObject> Plain = new List<GameObject>();
            public readonly List<GameObject> Swept = new List<GameObject>();
        }

        // One number -- or one colour -- typed or dragged.
        private class Cellar
        {
            public UnityEngine.UI.InputField Box;
            public ValueField Sheet;
            public Bond Bound;
            public bool Colour;

            // The track this box belongs to, where it is a slider's readout rather
            // than one of an auto row's three numbers.
            public UnityEngine.UI.Slider Track;

            // The stroke round the inside edge, showing what the value is.
            public UnityEngine.UI.Image Swatch;

            // The band of hue this box is the readout of, where it is one.
            public UnityEngine.UI.Slider Band;
        }

        // Every cell the layout makes, so ReadFromBlock can refresh the ones that
        // no row is holding on to.
        private readonly List<Cellar> cells = new List<Cellar>();

        private Cellar Cell(float x, float y, float w, Bond control, bool colour)
        {
            Cellar cell = new Cellar();
            cell.Bound = control;
            cell.Colour = colour;
            cell.Box = MakeInput(x, y, w);
            if (cell.Box == null) return cell;
            if (colour)
            {
                cell.Swatch = Stroke(cell.Box);
                Hexed(cell.Box);
            }

            cell.Box.onEndEdit.AddListener(delegate(string typed) { Entered(cell, typed); });

            // The drag sheet, stretched over the field.
            GameObject sheet = new GameObject("Drag");
            sheet.transform.SetParent(cell.Box.transform, false);
            RectTransform rect = sheet.AddComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            UnityEngine.UI.Image catcher = sheet.AddComponent<UnityEngine.UI.Image>();
            catcher.color = new Color(0f, 0f, 0f, 0f);
            catcher.raycastTarget = true;

            cell.Sheet = sheet.AddComponent<ValueField>();
            cell.Sheet.field = cell.Box;
            cell.Sheet.dragged = delegate(float pixels) { Nudged(cell, pixels); };
            cells.Add(cell);
            return cell;
        }

        // The stroke sits on the box's own edge -- flush with it, and no further
        // out than that -- which leaves the inside clear for a value at the same
        // size as every other one.
        private const float StrokeInset = 0f;
        private const float TextPad = 7f;

        // The hash in front of a colour, and the room the six characters have.
        private const float HashWidth = 10f;
        private const float HashPad = 4f;

        // The colour as a stroke round the inside edge rather than a block beside
        // the value, for a box too narrow to give any of its width away. The nine
        // slice's border and nothing else, so the corners are the field's own and
        // the value shows through the middle.
        private static UnityEngine.UI.Image Stroke(UnityEngine.UI.InputField field)
        {
            if (field == null) return null;

            GameObject go = new GameObject("Stroke");
            go.transform.SetParent(field.transform, false);
            go.transform.SetAsFirstSibling();

            RectTransform rect = go.AddComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(StrokeInset, StrokeInset);
            rect.offsetMax = new Vector2(-StrokeInset, -StrokeInset);

            UnityEngine.UI.Image image = go.AddComponent<UnityEngine.UI.Image>();
            UnityEngine.UI.Image back = field.GetComponent<UnityEngine.UI.Image>();
            if (back != null)
            {
                image.sprite = back.sprite;
                image.type = back.type;
            }
            image.fillCenter = false;
            image.raycastTarget = false;
            return image;
        }

        // A colour field, the way Besiege's own are: a hash that is not part of the
        // value, and six characters that are.
        //
        // The hash is a label of its own rather than the first character of the
        // text, so it cannot be selected, typed over or backspaced away, and it
        // does not shift as the value is edited.
        private static void Hexed(UnityEngine.UI.InputField field)
        {
            if (field == null) return;

            UnityEngine.UI.Text label = field.textComponent;
            Beside(label);
            Beside(field.placeholder as UnityEngine.UI.Text);

            if (label != null)
            {
                GameObject go = new GameObject("Hash");
                go.transform.SetParent(field.transform, false);

                RectTransform rect = go.AddComponent<RectTransform>();
                rect.anchorMin = new Vector2(0f, 0f);
                rect.anchorMax = new Vector2(0f, 1f);
                rect.pivot = new Vector2(0f, 0.5f);
                rect.sizeDelta = new Vector2(HashWidth, 0f);
                rect.anchoredPosition = new Vector2(HashPad, 0f);

                UnityEngine.UI.Text hash = go.AddComponent<UnityEngine.UI.Text>();
                hash.font = label.font;
                hash.fontSize = label.fontSize;
                hash.color = label.color;
                hash.alignment = TextAnchor.MiddleLeft;
                hash.horizontalOverflow = HorizontalWrapMode.Overflow;
                hash.verticalOverflow = VerticalWrapMode.Overflow;
                hash.text = "#";
                hash.raycastTarget = false;
            }

            // Six hex characters and nothing else. Three are allowed too: Unity
            // reads #ABC as shorthand, so a short answer is still an answer.
            field.characterLimit = 6;
            field.onValidateInput = Hex;
        }

        private static char Hex(string text, int index, char typed)
        {
            char up = char.ToUpper(typed);
            bool known = (up >= '0' && up <= '9') || (up >= 'A' && up <= 'F');
            return known ? up : '\0';
        }

        // Centred in what the hash leaves, so the value reads as the answer and the
        // hash as the label it is.
        private static void Beside(UnityEngine.UI.Text label)
        {
            if (label == null) return;
            label.alignment = TextAnchor.MiddleCenter;

            RectTransform rect = label.rectTransform;
            rect.offsetMin = new Vector2(HashPad + HashWidth, rect.offsetMin.y);
            rect.offsetMax = new Vector2(-HashPad, rect.offsetMax.y);
        }

        // Centred, with the whole box to itself.
        private static void Middle(UnityEngine.UI.Text label)
        {
            if (label == null) return;
            label.alignment = TextAnchor.MiddleCenter;
            RectTransform rect = label.rectTransform;
            rect.offsetMin = new Vector2(TextPad, rect.offsetMin.y);
            rect.offsetMax = new Vector2(-TextPad, rect.offsetMax.y);
        }

        // The colour the stroke shows. A box that is not a colour shows its number
        // as a shade instead, black at the bottom of the slider's range and white
        // at the top.
        private static void Tint(Cellar cell)
        {
            if (cell.Swatch == null || cell.Box == null) return;

            Color shown;
            if (cell.Colour)
            {
                MColourSlider colour = cell.Bound.Colour;
                if (colour == null) return;
                shown = colour.Value;
            }
            else
            {
                MSlider slider = cell.Bound.Slider;
                if (slider == null) return;
                float level = Mathf.Clamp01(Mathf.InverseLerp(slider.Min, slider.Max, slider.Value));
                shown = new Color(level, level, level, 1f);
            }

            shown.a = cell.Box.interactable ? 1f : 0.3f;
            cell.Swatch.color = shown;
        }

        // A drag is worth a fixed fraction of the range per pixel, so a long slider
        // and a short one feel the same under the hand.
        private const float DragPerPixel = 0.004f;
        private const float HuePerPixel = 0.003f;

        private void Nudged(Cellar cell, float pixels)
        {
            if (cell.Colour)
            {
                MColourSlider colour = cell.Bound.Colour;
                if (colour == null) return;
                float h, sat, v;
                Color.RGBToHSV(colour.Value, out h, out sat, out v);
                Paint(colour, Mathf.Repeat(h + pixels * HuePerPixel, 1f));
                Show(cell, true);
                Ride(cell);
                Queue(colour);
                return;
            }

            MSlider slider = cell.Bound.Slider;
            if (slider == null) return;
            Write(slider, slider.Value + pixels * (slider.Max - slider.Min) * DragPerPixel);
            Show(cell, true);
            Sync(cell);
            Queue(slider);
        }

        private void Entered(Cellar cell, string typed)
        {
            if (filling) return;
            if (cell.Colour)
            {
                MColourSlider colour = cell.Bound.Colour;
                Color parsed;
                if (colour == null || !ColorUtility.TryParseHtmlString("#" + typed, out parsed))
                {
                    Show(cell);
                    return;
                }
                parsed.a = colour.Value.a;
                colour.Value = parsed;
                Show(cell);
                Ride(cell);
                Queue(colour);
                return;
            }

            MSlider slider = cell.Bound.Slider;
            float number;
            if (slider == null || !float.TryParse(typed, out number)) { Show(cell); return; }
            Write(slider, number);
            Show(cell);
            Sync(cell);
            Queue(slider);
        }

        // A value outside the slider's range is kept only where it would survive
        // being saved. Besiege clamps a clamped slider as the machine loads --
        // unless the game's own "disable slider limits" option is on -- so keeping
        // one anywhere else would work now and be quietly lost on the next load.
        private static void Write(MSlider slider, float number)
        {
            // Never below the slider's own floor where that floor is zero or above:
            // a range that starts at 0 has nothing under it to mean, whatever it
            // will take above the top. Gravity and dampening start below zero and
            // keep their negatives.
            if (slider.Min >= 0f && number < slider.Min) number = slider.Min;
            slider.Value = Free(slider)
                ? number : Mathf.Clamp(number, slider.Min, slider.Max);
        }

        private static bool Free(MSlider slider)
        {
            if (slider.Unclamped) return true;
            try { return StatMaster.KeyMapper.disableSliderLimits; }
            catch (Exception) { return false; }
        }

        // Puts the track back under a number typed or dragged into the box. Its own
        // change event would write the clamped track value straight back over an
        // out-of-range one, so it is silenced while this happens.
        // Puts the band of hue under a colour typed or dragged into the box beside
        // it, so the handle does not sit where the colour no longer is.
        private void Ride(Cellar cell)
        {
            MColourSlider colour = cell.Bound.Colour;
            if (cell.Band == null || colour == null) return;

            float hue, sat, value;
            Color.RGBToHSV(colour.Value, out hue, out sat, out value);
            echo = true;
            cell.Band.value = hue;
            echo = false;
        }

        private void Sync(Cellar cell)
        {
            MSlider slider = cell.Bound.Slider;
            if (cell.Track == null || slider == null) return;
            echo = true;
            cell.Track.value = Mathf.Clamp(slider.Value, slider.Min, slider.Max);
            echo = false;
        }

        // What a cell reads, which is a hex colour or a number. `forced` is the
        // drag: the field is not focused then, but the number under the pointer has
        // to keep up with it.
        private void Show(Cellar cell) { Show(cell, false); }

        private void Show(Cellar cell, bool forced)
        {
            if (cell == null || cell.Box == null) return;

            // A box the player is typing in is theirs. `forced` is for a value the
            // player is changing some other way -- dragging the sheet, or the
            // slider beside it -- and those all happen with the button held, so a
            // held button is what tells the two apart. Without this the box was
            // rewritten from the block between one keystroke and the next: only the
            // last character stayed, and it was committed against a value the
            // player never typed.
            if (cell.Box.isFocused && !(forced && Input.GetMouseButton(0))) return;
            if (cell.Colour)
            {
                MColourSlider colour = cell.Bound.Colour;
                if (colour != null) cell.Box.text = ColorUtility.ToHtmlStringRGB(colour.Value);
                Tint(cell);
                return;
            }
            MSlider slider = cell.Bound.Slider;
            if (slider != null) cell.Box.text = Figure(slider.Value);
            Tint(cell);
        }

        // The little name above a field.
        private const float TinyHeight = 20f;

        private GameObject Tiny(string text, float x, float y, float w)
        {
            UnityEngine.UI.Text label = Label(text, x, y, w, TextAnchor.LowerCenter);
            if (label == null) return null;
            RectTransform rect = label.rectTransform;
            rect.sizeDelta = new Vector2(rect.sizeDelta.x, TinyHeight);
            label.fontSize = 16;
            return label.gameObject;
        }

        // A thin line across the panel, to break a long list into parts.
        protected void Rule(ref float y)
        {
            GameObject go = new GameObject("Rule");
            go.transform.SetParent(pen, false);
            go.AddComponent<RectTransform>();
            UnityEngine.UI.Image line = go.AddComponent<UnityEngine.UI.Image>();
            line.color = RuleInk;
            line.raycastTarget = false;
            Place(go, Margin, y + RuleGap, Full, RuleHeight);
            y += RuleGap * 2f + RuleHeight;
        }


        // A section whose heading *is* its switch: the strobe's Activate rather
        // than a word above it and a toggle below. Everything laid out until
        // EndSection greys out and stops answering while it is off.
        protected void Section(ref float y, string toggleKey, string name)
        {
            Bond bond = Bind<MToggle>(toggleKey);
            if (bond.Toggle == null) return;

            Fold fold = Begin(bond);
            fold.Switch = MakeToggle(name, Margin, y, Full);
            if (fold.Switch != null)
            {
                fold.Switch.onValueChanged.AddListener(delegate(bool on) { Flipped(bond, on); });
            }
            y += RowHeight + RowGap;
            fold.From = host.childCount;
            fold.Top = y;
        }

        protected void EndSection()
        {
            Close();
        }





        private Fold Begin(Bond bond)
        {
            Fold fold = new Fold();
            fold.Bound = bond;
            fold.Parent = Innermost;
            nesting.Add(fold);
            return fold;
        }

        // Nothing is shaded here: the lettering's own colours are read now, and a
        // group closed inside a section would otherwise have dimmed the rows the
        // section is about to read its originals from. ReadFromBlock shades the lot
        // once the layout is done.
        private void Close()
        {
            Fold fold = Innermost;
            if (fold == null) return;
            nesting.RemoveAt(nesting.Count - 1);
            fold.To = host.childCount;

            for (int i = fold.From; i < fold.To && i < host.childCount; i++)
            {
                UnityEngine.UI.Text[] words =
                    host.GetChild(i).GetComponentsInChildren<UnityEngine.UI.Text>(true);
                for (int w = 0; w < words.Length; w++)
                {
                    if (words[w] == null) continue;
                    fold.Words.Add(words[w]);
                    fold.Inks.Add(words[w].color);
                }
            }

            folds.Add(fold);
        }

        private class Fold
        {
            public Bond Bound;

            public UnityEngine.UI.Toggle Switch;

            // Sections nest, and a group inside a section that is off is dimmed
            // with it however its own gate stands.
            public Fold Parent;
            public bool Lit;

            public int From;
            public int To;
            public float Top;

            // What the section's lettering was before it was dimmed. Read once, at
            // the end of the layout, before anything has been shaded: reading it on
            // every shade would take DimInk for the original the second time round
            // and never light up again.
            public readonly List<UnityEngine.UI.Text> Words = new List<UnityEngine.UI.Text>();
            public readonly List<Color> Inks = new List<Color>();
        }

        // Outermost first, so a group knows whether the section around it is on
        // before it decides about itself.
        private void Reshade()
        {
            for (int i = folds.Count - 1; i >= 0; i--) Shade(folds[i]);

        }

        // Dim and deafen a whole section at once.
        private void Shade(Fold fold)
        {
            bool own = fold.Bound.Toggle != null && fold.Bound.Toggle.IsActive;
            bool on = own && (fold.Parent == null || fold.Parent.Lit);
            fold.Lit = on;

            for (int i = fold.From; i < fold.To && i < host.childCount; i++)
            {
                Transform row = host.GetChild(i);
                if (row == null) continue;

                UnityEngine.UI.Selectable[] parts =
                    row.GetComponentsInChildren<UnityEngine.UI.Selectable>(true);
                for (int p = 0; p < parts.Length; p++)
                    if (parts[p] != null) parts[p].interactable = on;

                ValueField[] sheets = row.GetComponentsInChildren<ValueField>(true);
                for (int v = 0; v < sheets.Length; v++)
                    if (sheets[v] != null) sheets[v].locked = !on;

                UIF.SetSwell(row.gameObject, on);
            }

            for (int i = 0; i < fold.Words.Count; i++)
                if (fold.Words[i] != null) fold.Words[i].color = on ? fold.Inks[i] : DimInk;
        }


        private static string Caption(MapperType control)
        {
            string name = control.DisplayName;
            if (string.IsNullOrEmpty(name)) name = control.Key;
            return name.Replace("\n", " ").Trim();
        }

        // Trailing zeroes off: a box 72 wide holds "1000" but not "1000.00".
        private static string Figure(float value)
        {
            return value.ToString("0.###");
        }

        // ---- reading and writing ---------------------------------------------

        // Everything the panel drew comes off the stock mapper. It is not put back
        // when the panel hides: every display change dirties the mapper and makes
        // it rebuild all of its widgets, so putting sixty controls back on every
        // close cost two rebuilds an open and one more at the start of every run.
        // Left off, the mapper builds the short list once and stays quiet. Keys are
        // never taken -- binding one needs Besiege's own key capture, which UI
        // Factory has no prefab for.
        private void StockControls(bool visible)
        {
            for (int i = 0; i < bonds.Count; i++)
                if (bonds[i].Control != null) bonds[i].Control.DisplayInMapper = visible;
        }

        private void ReadFromBlock()
        {
            filling = true;
            for (int i = 0; i < menus.Count; i++)
            {
                MMenu menu = menus[i].Menu;
                if (menu == null) continue;
                if (selectors[i] != null) selectors[i].Set(menu.Items, menu.Value);
                showing[i] = menu.Value;
            }
            for (int i = 0; i < bars.Count; i++)
            {
                MSlider slider = bars[i].Slider;
                if (slider == null || tracks[i] == null) continue;
                tracks[i].value = Mathf.Clamp(slider.Value, slider.Min, slider.Max);
            }
            for (int i = 0; i < hues.Count; i++)
            {
                MColourSlider colour = hues[i].Colour;
                if (colour == null || hueBars[i] == null) continue;
                float h, sat, v;
                Color.RGBToHSV(colour.Value, out h, out sat, out v);
                hueBars[i].value = h;
            }
            for (int i = 0; i < cells.Count; i++) Show(cells[i]);
            for (int i = 0; i < fields.Count; i++)
                if (boxes[i] != null && !boxes[i].isFocused && fields[i].Text != null)
                    boxes[i].text = fields[i].Text.Value;
            for (int i = 0; i < switches.Count; i++)
                if (flips[i] != null && switches[i].Toggle != null)
                    flips[i].isOn = switches[i].Toggle.IsActive;

            for (int i = 0; i < folds.Count; i++)
                if (folds[i].Switch != null && folds[i].Bound.Toggle != null)
                    folds[i].Switch.isOn = folds[i].Bound.Toggle.IsActive;
            Reshade();
            for (int i = 0; i < groups.Count; i++)
            {
                Group group = groups[i];
                if (group.Switch != null && group.Auto.Toggle != null)
                    group.Switch.isOn = group.Auto.Toggle.IsActive;
                Swap(group);
            }
            filling = false;
        }

        // The selectors are polled rather than subscribed to: one integer each a
        // frame costs nothing next to keeping a listener per control alive.
        private void WatchSelectors()
        {
            for (int i = 0; i < menus.Count; i++)
            {
                int picked = selectors[i] == null ? -1 : selectors[i].Index;
                if (picked < 0 || picked == showing[i]) continue;
                showing[i] = picked;
                MMenu menu = menus[i].Menu;
                if (menu == null) continue;
                menu.Value = picked;
                Queue(menu);
                Restock();
                // A group can be gated on this menu.
                Reshade();
            }
        }

        private void Dragged(Cellar cell, float value)
        {
            if (echo) return;
            MSlider slider = cell.Bound.Slider;
            if (!filling && slider != null)
            {
                slider.Value = value;
                Queue(slider);
            }
            // Forced: the box is showing what the block holds, and the drag is what
            // is changing it.
            Show(cell, true);
        }

        private void Flipped(Bond bond, bool on)
        {
            if (filling || bond.Toggle == null) return;
            bond.Toggle.IsActive = on;
            Queue(bond.Toggle);
            Restock();
            // A switch can head a section, take a group away, or swap an auto row
            // between its two layouts.
            Reshade();
            // The control, not the binding: two rows governed by one switch each
            // bound it for themselves, so the opacity follows the colour.
            for (int i = 0; i < groups.Count; i++)
                if (groups[i].Auto.Control == bond.Control) Swap(groups[i]);
        }

        private void Typed(Bond bond, string typed)
        {
            if (filling || bond.Text == null) return;
            bond.Text.Value = typed;
            Queue(bond.Text);
        }

        private void Picked(Bond bond, Cellar hex, float hue)
        {
            MColourSlider colour = bond.Colour;
            if (echo || filling || colour == null) return;
            Paint(colour, hue);
            Show(hex, true);
            Queue(colour);
        }

        private static void Paint(MColourSlider colour, float hue)
        {
            Color picked = Color.HSVToRGB(hue, 1f, 1f);
            picked.a = colour.Value.a;
            colour.Value = picked;
        }

        // A block's own change handlers show the controls a switch or a menu
        // governs, and they do not know the panel has taken them off the mapper.
        private void Restock()
        {
            if (window != null && window.activeSelf) StockControls(false);
        }

        // ---- the rainbow ----------------------------------------------------

        private static Texture2D band;
        private static Sprite bandSprite;

        // Besiege's colour slider is a band of hue rather than a plain track. The
        // track is found by elimination: the prefab's children are not named what a
        // guess would guess, but the Slider knows its own fill and handle.
        private static void Rainbow(UnityEngine.UI.Slider bar)
        {
            if (bandSprite == null)
            {
                band = new Texture2D(256, 1, TextureFormat.RGB24, false);
                band.wrapMode = TextureWrapMode.Clamp;
                band.hideFlags = HideFlags.HideAndDontSave;
                for (int i = 0; i < 256; i++) band.SetPixel(i, 0, Color.HSVToRGB(i / 255f, 1f, 1f));
                band.Apply();
                bandSprite = Sprite.Create(band, new Rect(0f, 0f, band.width, band.height),
                    new Vector2(0.5f, 0.5f), 100f);
                bandSprite.hideFlags = HideFlags.HideAndDontSave;
            }

            UnityEngine.UI.Image[] all = bar.GetComponentsInChildren<UnityEngine.UI.Image>(true);
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] == null) continue;
                if (bar.fillRect != null && all[i].transform.IsChildOf(bar.fillRect)) continue;
                if (bar.handleRect != null && all[i].transform.IsChildOf(bar.handleRect)) continue;
                all[i].sprite = bandSprite;
                all[i].color = Color.white;
                return;
            }
        }

        // ---- lifetime --------------------------------------------------------

        private void Start()
        {
            // Unconditional: UI Factory is usually not up yet when this runs, and
            // "not yet" is not "not installed". If it is absent the call fails
            // inside UIF and Ready is simply never reached.
            UIF.WhenReady(Ready);
            Hook();
        }

        private void Ready()
        {
            ready = true;
        }

        private void OnDestroy()
        {
            Unhook();
            Suspend(false);
            Teardown();
        }

        private void Hook()
        {
            if (hooked) return;
            try
            {
                BlockMapper.onMapperOpen += MapperOpened;
                BlockMapper.onMapperClose += MapperClosed;
                hooked = true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Sand] no panel, could not watch the mapper: " + e.Message);
                Failed = true;
            }
        }

        private void Unhook()
        {
            if (!hooked) return;
            try
            {
                BlockMapper.onMapperOpen -= MapperOpened;
                BlockMapper.onMapperClose -= MapperClosed;
            }
            catch (Exception) { }
            hooked = false;
        }

        private void MapperOpened()
        {
            if (Failed || !ready || !UIF.Available) return;

            Component opened = null;
            try
            {
                BlockMapper mapper = BlockMapper.CurrentInstance;
                if (mapper != null && mapper.Block != null)
                {
                    // The block's own control list, which every lookup reads. Set
                    // here rather than in a subclass: forgetting it there is what
                    // made every panel report "the block registered: nothing".
                    holder = mapper.Current;
                    opened = Serves(mapper);
                }
            }
            catch (Exception) { }

            try
            {
                if (opened == null) { Hide(); return; }
                Show();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Sand] panel show failed, stock mapper stands: " + e.Message);
                try { StockControls(true); } catch (Exception) { }
            }
        }

        private void MapperClosed()
        {
            // BlockMapper.onMapperClose is a plain Action, so anything thrown here
            // stops the rest of Besiege's own close handling and the menu stays up.
            try { Hide(); }
            catch (Exception e) { Debug.LogWarning("[Sand] panel hide failed: " + e.Message); }
        }

        private void Show()
        {
            Rect frame;
            bool docked = MapperFrame(out frame);
            if (docked) Widen(frame);

            // Not latched: a build can fail because UI Factory was a frame short of
            // ready, and latching is what made the panel vanish for the rest of the
            // session after one bad open.
            if (!built && !Build()) { StockControls(true); return; }

            // The rows were laid out against whichever block was open at the time.
            // This is the block open now.
            if (!Rebind()) { StockControls(true); Hide(); return; }

            StockControls(false);
            Suspend(true);
            window.SetActive(true);
            ReadFromBlock();
            Canvas.ForceUpdateCanvases();
            Dock();
            Curtain(true);
        }

        private void Hide()
        {
            if (window != null) window.SetActive(false);
            Suspend(false);
            CommitPending();
        }

        // Only the panel that put the flag up takes it down: every panel is told
        // that a mapper opened, and the three that do not serve the block hide
        // themselves a moment after the one that does has shown itself.
        private bool suspending;

        private void Suspend(bool on)
        {
            if (on) { Controls.Suspended = true; suspending = true; return; }
            if (!suspending) return;
            Controls.Suspended = false;
            suspending = false;
        }

        // ---- the window ------------------------------------------------------

        private bool Build()
        {
            try
            {
                Teardown();
                if (!BuildCanvas()) return false;

                window = UIF.Spawn(UIF.WindowPrefab, canvas.transform);
                if (window == null) return false;

                windowRect = window.GetComponent<RectTransform>();
                windowRect.anchorMin = new Vector2(0.5f, 0.5f);
                windowRect.anchorMax = new Vector2(0.5f, 0.5f);
                windowRect.pivot = new Vector2(0.5f, 0.5f);

                gutter = Scrolls ? BarGutter : 0f;
                Untitle();
                host = ScrollContent();
                pen = host;

                // Negative means the layout asked for a control the block does not
                // have, and Complete has already said which. Better no panel than a
                // half-built one over the stock mapper it just hid.
                contentHeight = Layout();
                if (contentHeight < 0f) { Teardown(); return false; }

                // Said, not worked out: the rows are laid out to a width, so by the
                // time the height is known it is too late to change it, and laying
                // out twice would strand a selector's list on the canvas.
                if (!Scrolls && Shown > MaxHeight)
                    Debug.LogWarning("[Sand] " + GetType() + " says it does not"
                        + " scroll but lays out to " + Shown + ", past " + MaxHeight
                        + " -- its scrollbar will sit over the rows.");

                Uncurtain();

                FitContent();
                windowRect.sizeDelta = new Vector2(width, Tall(MaxHeight));

                built = true;
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Sand] could not build the panel, so the stock mapper stands: " + e);
                // Or a half-built frame is left on screen with nothing in it.
                Teardown();
                return false;
            }
        }

        private bool BuildCanvas()
        {
            if (canvas != null) return true;

            GameObject go = new GameObject("SandPanel");
            go.transform.SetParent(transform, false);
            canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = CanvasOrder;

            CanvasScaler scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = Reference;
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 1f;   // match on height

            go.AddComponent<GraphicRaycaster>();
            return true;
        }

        // The Window prefab is a frame plus a full ScrollRect over Viewport/Content.
        // Rows put on the window itself are drawn behind the scroll view and never
        // seen, and the scroll view is left holding the prefab's own 500-unit
        // placeholder -- an empty window with a scrollbar beside it, which is
        // exactly what the first attempt did.
        private Transform ScrollContent()
        {
            scroll = window.GetComponentInChildren<ScrollRect>(true);
            if (scroll == null || scroll.content == null)
            {
                Debug.LogWarning("[Sand] the UI Factory Window has no ScrollRect; "
                    + "rows will go on the frame.");
                return window.transform;
            }

            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = RowHeight;
            if (scroll.horizontalScrollbar != null)
                scroll.horizontalScrollbar.gameObject.SetActive(false);

            Clip();
            Rail();
            return scroll.content;
        }

        // The prefab's Viewport carries the Mask that is meant to clip the rows,
        // but it comes anchored to a corner at zero size, so it masked nothing:
        // rows scrolled up were drawn over the mapper's key field, and the last row
        // hung below the frame until it was clear of it altogether. Give the mask
        // the frame to work on, less the gutter.
        private void Clip()
        {
            RectTransform view = scroll.viewport;
            if (view == null)
            {
                Transform found = Attach.Find(window.transform, "Viewport");
                if (found != null) view = found.GetComponent<RectTransform>();
                scroll.viewport = view;
            }
            if (view == null) return;

            view.anchorMin = Vector2.zero;
            view.anchorMax = Vector2.one;
            view.pivot = new Vector2(0f, 1f);
            view.offsetMin = Vector2.zero;
            view.offsetMax = new Vector2(-gutter, 0f);

            Mask mask = view.GetComponent<Mask>();
            if (mask != null)
            {
                mask.enabled = true;
                // The window already has a background; this one would be a second
                // plate over it.
                mask.showMaskGraphic = false;
            }
            else if (view.GetComponent<RectMask2D>() == null)
            {
                view.gameObject.AddComponent<RectMask2D>();
            }
        }

        // The scrollbar into the gutter the rows leave for it, full height.
        private void Rail()
        {
            UnityEngine.UI.Scrollbar bar = scroll.verticalScrollbar;
            if (bar == null) return;

            RectTransform rect = bar.GetComponent<RectTransform>();
            if (rect != null)
            {
                rect.anchorMin = new Vector2(1f, 0f);
                rect.anchorMax = new Vector2(1f, 1f);
                rect.pivot = new Vector2(1f, 1f);
                rect.sizeDelta = new Vector2(BarGutter, 0f);
                rect.anchoredPosition = Vector2.zero;
            }
            bar.gameObject.SetActive(true);
            scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;
        }

        // The panel has no title of its own -- it is the mapper's lower half -- so
        // the Window prefab's TopBar goes, and with it the drag handle and the
        // close cross that would shut only this half.
        //
        // The ScrollView is anchored below the bar, so hiding the bar alone leaves
        // a bar's worth of empty frame at the top: it has to be stretched over the
        // whole window afterwards.
        private void Untitle()
        {
            Transform bar = Attach.Find(window.transform, "TopBar");
            if (bar != null) bar.gameObject.SetActive(false);

            Transform view = Attach.Find(window.transform, "ScrollView");
            if (view == null) return;
            RectTransform rect = view.GetComponent<RectTransform>();
            if (rect == null) return;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        // Sizes the scroll content to what was actually built, so the scrollbar
        // reflects the rows rather than the placeholder.
        private void FitContent()
        {
            if (scroll == null || scroll.content == null) return;
            scroll.content.sizeDelta = new Vector2(scroll.content.sizeDelta.x, Shown);
        }

        // What the panel is holding.
        private float Shown { get { return contentHeight + Margin; } }

        // How tall the window is drawn: its contents, but never more than the room
        // under the mapper, and never more than MaxHeight. The rest is scrolled.
        private float Tall(float room)
        {
            float wanted = Mathf.Min(Shown, MaxHeight, room);
            return Mathf.Max(MinHeight, wanted);
        }

        private void Teardown()
        {
            if (window != null) Destroy(window);
            window = null;
            windowRect = null;
            scroll = null;
            host = null;
            built = false;
        }

        // ---- rows ------------------------------------------------------------

        private UnityEngine.UI.Text Label(string text, float x, float y, float w, TextAnchor align)
        {
            GameObject go = UIF.Spawn(UIF.TextPrefab, pen);
            if (go == null) return null;
            Place(go, x, y, w, RowHeight);
            UnityEngine.UI.Text label = go.GetComponent<UnityEngine.UI.Text>();
            if (label != null)
            {
                label.text = text;
                label.alignment = align;
                label.color = UIF.QuietInk;
                // A caption is never clicked, and one wider than its own slot
                // would take the click meant for whatever it overhangs.
                label.raycastTarget = false;
            }
            return label;
        }

        private UnityEngine.UI.Slider MakeSlider(float x, float y, float w, float min, float max)
        {
            GameObject go = UIF.Spawn(UIF.SliderPrefab, pen);
            if (go == null) return null;
            Place(go, x, y, w, RowHeight);
            UIF.NoSwell(go);
            UnityEngine.UI.Slider slider = go.GetComponent<UnityEngine.UI.Slider>();
            if (slider != null)
            {
                slider.minValue = min;
                slider.maxValue = max;
                slider.wholeNumbers = false;
            }
            return slider;
        }

        private UnityEngine.UI.Toggle MakeToggle(string caption, float x, float y, float w)
        {
            GameObject go = UIF.Spawn(UIF.TogglePrefab, pen);
            if (go == null) return null;
            Place(go, x, y, w, RowHeight);
            // The prefab's own swell grows the whole row 15%, which on a full-width
            // toggle carries the lettering sideways out of the panel. Ours grows
            // the lettering in place instead, and stops when the row is greyed out.
            UIF.NoSwell(go);
            UnityEngine.UI.Text label = go.GetComponentInChildren<UnityEngine.UI.Text>();
            if (label != null)
            {
                label.text = caption;

                // The prefab's caption is a fixed 160 wide and is the last child,
                // so on a toggle narrower than that it hung over its neighbours and
                // won their clicks: the right-hand end of one toggle belonged to the
                // next. Pinned to its own toggle, and deaf -- the background behind
                // it is what the toggle is clicked on.
                RectTransform rect = label.rectTransform;
                rect.anchorMin = Vector2.zero;
                rect.anchorMax = Vector2.one;
                rect.offsetMin = Vector2.zero;
                rect.offsetMax = Vector2.zero;
                label.raycastTarget = false;

                Swell swell = go.AddComponent<Swell>();
                swell.grows = label.transform;
            }
            return go.GetComponent<UnityEngine.UI.Toggle>();
        }

        private UnityEngine.UI.InputField MakeInput(float x, float y, float w)
        {
            GameObject go = UIF.Spawn(UIF.InputPrefab, pen);
            if (go == null) return null;
            Place(go, x, y, w, RowHeight);

            UnityEngine.UI.InputField field = go.GetComponent<UnityEngine.UI.InputField>();
            if (field != null)
            {
                Middle(field.textComponent);
                Middle(field.placeholder as UnityEngine.UI.Text);
                // The prefab's text and placeholder carry no font, and a Text with
                // no font draws nothing -- which is a field that swallows what you
                // type rather than one that is empty.
                Ink(field.textComponent, Color.white);
                Ink(field.placeholder as UnityEngine.UI.Text, UIF.QuietInk);
            }
            return field;
        }

        private static void Ink(UnityEngine.UI.Text label, Color colour)
        {
            if (label == null) return;
            if (label.font == null && UIF.Font != null) label.font = UIF.Font;
            label.color = colour;
            label.supportRichText = false;
            label.resizeTextForBestFit = false;
            label.horizontalOverflow = HorizontalWrapMode.Overflow;
            label.verticalOverflow = VerticalWrapMode.Overflow;
        }

        // A row that holds other rows, so what is inside it can come and go without
        // the panel's own clipping having an opinion about any of it.
        private GameObject Cradle(float y, float h)
        {
            GameObject go = new GameObject("Band");
            go.transform.SetParent(pen, false);
            go.AddComponent<RectTransform>();
            Place(go, 0f, y, width, h);
            return go;
        }

        private void Place(GameObject go, float x, float y, float w, float h)
        {
            UIF.Fit(go.GetComponent<RectTransform>(), x, y, w, h);
        }

        // ---- writing back ----------------------------------------------------

        // Queues a setting for the commit that happens when the mouse comes up.
        private void Queue(MapperType changed)
        {
            if (filling || changed == null) return;
            if (!pending.Contains(changed)) pending.Add(changed);
        }

        private void Update()
        {
            if (window != null && window.activeSelf) WatchSelectors();
            if (pending.Count > 0 && !Input.GetMouseButton(0)) CommitPending();
        }

        private void CommitPending()
        {
            for (int i = 0; i < pending.Count; i++) Commit(pending[i]);
            pending.Clear();
        }

        // Writes a setting through the mapper, so it survives a save and can be
        // undone. ApplyValue is the fallback where that machinery is not up.
        private void Commit(MapperType changed)
        {
            if (changed == null) return;
            try
            {
                BlockMapper mapper = BlockMapper.CurrentInstance;
                if (mapper != null && mapper.Current != null)
                {
                    BlockMapper.OnEditField(mapper.Current, changed);
                    return;
                }
            }
            catch (Exception) { }
            try { changed.ApplyValue(); } catch (Exception) { }
        }

        // ---- where the window sits -------------------------------------------

        // In LateUpdate: the mapper is dragged by its own behaviour, so a panel
        // placed in Update is placed against where the mapper was.
        // Reconciled every frame rather than left to onMapperClose. That event does
        // not fire for every way a mapper goes away -- clicking off the block, or
        // the block being deselected -- and the panel was left hanging over the
        // world with nothing behind it. See notes/08 "reconcile, do not react".
        private void LateUpdate()
        {
            // Tab hides Besiege's own HUD, and the panel is the mapper's lower half.
            // The canvas rather than the window: switching the window off is the
            // path that hands every control back to the stock mapper, and coming
            // out of Tab would then find the panel gone.
            if (canvas != null) canvas.enabled = !StatMaster.hudHidden;

            if (window == null || !window.activeSelf) return;

            if (!MapperStillOpen()) { Hide(); return; }
            if (!built) return;
            Dock();
            Curtain(false);
        }

        private bool MapperStillOpen()
        {
            try
            {
                BlockMapper mapper = BlockMapper.CurrentInstance;
                if (mapper == null || !BlockMapper.IsOpen || mapper.Block == null) return false;
                return Serves(mapper) != null;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private void Dock()
        {
            if (windowRect == null || window == null || !window.activeSelf) return;

            Rect frame;
            if (!MapperFrame(out frame)) return;

            if (Widen(frame))
            {
                // Rebuilt and placed in the same frame. Returning here instead
                // leaves the window where it was and clears the flag that lets this
                // run at all, so it never docks and never follows again.
                if (!Build() || !Rebind()) return;
                ReadFromBlock();
                Canvas.ForceUpdateCanvases();
            }

            float scale = Scale();
            float left = (frame.xMin - Screen.width * 0.5f) * scale;
            float bottom = (frame.yMin - Screen.height * 0.5f) * scale;

            // What is left between the bottom of the mapper and the bottom of the
            // screen. The window takes that and scrolls the rest.
            float tall = Tall(frame.yMin * scale - Margin);
            if (Mathf.Abs(windowRect.sizeDelta.y - tall) > 0.5f)
                windowRect.sizeDelta = new Vector2(width, tall);

            Vector2 size = windowRect.sizeDelta;
            windowRect.anchoredPosition =
                new Vector2(left + size.x * 0.5f, bottom - size.y * 0.5f);
        }

        private void Uncurtain()
        {
            curtainAt = float.NaN;
            curtainTall = float.NaN;
        }

        // The rows are hidden rather than clipped.
        //
        // The Window's Viewport carries a Mask and sizing it did not make it clip:
        // rows scrolled past the top were still drawn over the mapper above, and
        // the last one hung below the frame. Rather than keep guessing at prefab
        // internals, the panel clips itself -- a row is drawn only while all of it
        // is inside the frame, so nothing is ever cut in half and nothing reaches
        // outside. Rows appear and disappear at the edges rather than sliding under
        // them, which is what a list of whole rows should look like anyway.
        private float curtainAt = float.NaN;
        private float curtainTall = float.NaN;

        private const float Slop = 0.5f;

        private void Curtain(bool force)
        {
            if (scroll == null || scroll.content == null || host == null) return;

            float at = scroll.content.anchoredPosition.y;
            float tall = windowRect != null ? windowRect.sizeDelta.y : 0f;
            if (!force && Mathf.Abs(at - curtainAt) < Slop
                       && Mathf.Abs(tall - curtainTall) < Slop) return;
            curtainAt = at;
            curtainTall = tall;

            for (int i = 0; i < host.childCount; i++)
            {
                RectTransform row = host.GetChild(i) as RectTransform;
                if (row == null) continue;
                float top = at + row.anchoredPosition.y;
                float bottom = top - row.sizeDelta.y;
                bool inside = top <= Slop && bottom >= -tall - Slop;
                if (row.gameObject.activeSelf != inside) row.gameObject.SetActive(inside);
            }
        }

        // Canvas units per screen pixel. The scaler matches on height against a
        // 1080-tall reference, so one unit is one pixel at 1080p.
        private float Scale()
        {
            return Screen.height > 0 ? Reference.y / Screen.height : 1f;
        }

        // Takes the panel's width from the mapper's. True if it changed, in which
        // case the rows -- laid out to it -- no longer fit.
        private bool Widen(Rect frame)
        {
            float wide = frame.width * Scale();
            if (Mathf.Abs(wide - width) <= 0.5f) return false;
            width = wide;
            built = false;
            return true;
        }

        // Besiege's mapper window in screen pixels.
        //
        // The window is the TALLEST renderer named "Background" -- they all share
        // its width, and only the frame reaches the bottom edge. Two plausible
        // alternatives are wrong and both have been shipped by other mods:
        // "WideShadow" is an eleventh wider and sits higher, and "Visual" is a
        // 93-pixel button. See notes/05-docking-a-window.md.
        private bool MapperFrame(out Rect frame)
        {
            frame = new Rect();
            try
            {
                BlockMapper mapper = BlockMapper.CurrentInstance;
                if (mapper == null) return false;

                Renderer[] parts = mapper.GetComponentsInChildren<Renderer>(false);
                Renderer best = null;
                float tallest = 0f;
                for (int i = 0; i < parts.Length; i++)
                {
                    if (parts[i] == null || parts[i].gameObject.name != "Background") continue;
                    float height = parts[i].bounds.size.y;
                    if (height > tallest) { tallest = height; best = parts[i]; }
                }
                if (best == null) return false;

                Camera eye = MapperCamera(best.gameObject.layer);
                if (eye == null) return false;

                frame = ScreenRect(best, eye);
                return frame.width > 1f && frame.height > 1f;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static Rect ScreenRect(Renderer part, Camera eye)
        {
            Bounds box = part.bounds;
            Vector3 a = eye.WorldToScreenPoint(new Vector3(box.min.x, box.min.y, box.center.z));
            Vector3 b = eye.WorldToScreenPoint(new Vector3(box.max.x, box.max.y, box.center.z));
            float xMin = Mathf.Min(a.x, b.x);
            float yMin = Mathf.Min(a.y, b.y);
            return new Rect(xMin, yMin, Mathf.Abs(b.x - a.x), Mathf.Abs(b.y - a.y));
        }

        // The mapper is drawn in the world, so only the camera whose culling mask
        // includes its layer knows where on screen it lands. The topmost such
        // camera: the interface is drawn last.
        private Camera MapperCamera(int layer)
        {
            if (mapperEye != null && (mapperEye.cullingMask & (1 << layer)) != 0) return mapperEye;

            Camera best = null;
            Camera[] all = Camera.allCameras;
            for (int i = 0; i < all.Length; i++)
            {
                if ((all[i].cullingMask & (1 << layer)) == 0) continue;
                if (best == null || all[i].depth > best.depth) best = all[i];
            }
            mapperEye = best;
            return best;
        }
    }
}
