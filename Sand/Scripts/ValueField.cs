using System;
using UnityEngine;
using UnityEngine.EventSystems;

namespace SandMod
{
    // A transparent sheet over an Input Field. A drag that stays on the field
    // selects text, the way any text box does; a drag that leaves it changes the
    // value. A click puts the caret where it landed and a double-click takes the
    // lot.
    //
    // It has to be a sheet rather than a behaviour on the field itself: uGUI hands
    // a drag to the first handler at or above the object the pointer pressed, so
    // whichever of the two wants to decide has to be the lower one. The sheet
    // decides, and passes what it does not want down to the field.
    //
    // The pointer-down has to be handled here as well, and it is not obvious why.
    // StandaloneInputModule.ProcessMousePress records the press as whatever
    // ExecuteHierarchy finds for IPointerDownHandler; on release it dispatches the
    // click only if that object is also what GetEventHandler finds for
    // IPointerClickHandler. Without a down handler here the press resolves to the
    // InputField above and the click to this sheet, the two do not match, and the
    // click is never dispatched at all -- no OnPointerClick, so no click count and
    // no double-click. Handling the down and forwarding it makes both resolve to
    // the sheet, and the field still gets its caret.
    public class ValueField : MonoBehaviour, IPointerDownHandler, IPointerUpHandler,
        IBeginDragHandler, IDragHandler, IEndDragHandler, IPointerClickHandler
    {
        public UnityEngine.UI.InputField field;

        // Pixels dragged, handed to whoever owns the number.
        public Action<float> dragged;

        // Off while the row's Auto switch is off: the sheet stays, so the field
        // below it cannot be typed into either.
        public bool locked;

        // How far the pointer may wander before a click counts as a drag. Without
        // it a hand that moves one pixel between press and release turned a click
        // into a drag: the field never took focus, so the caret came and went, and
        // the value was nudged by whatever that pixel was worth.
        private const float Slack = 4f;

        // How far past the field's own edge the pointer goes before the drag stops
        // being a selection and starts being a value. Enough that reaching the last
        // character does not tip it over by accident, little enough to be a
        // deliberate flick.
        private const float Leeway = 4f;

        // Decided once per gesture and kept: a drag that has left the field is a
        // value drag until the button comes up, wherever the pointer wanders back
        // to. Handing it back and forth at the boundary is unusable.
        private bool scrubbing;

        // Whether the gesture that is ending was a value drag. The click that comes
        // after one must not be read as a double-click and select everything.
        private bool scrubbed;

        public void OnPointerDown(PointerEventData press)
        {
            if (locked) return;
            Pass(press, ExecuteEvents.pointerDownHandler);
        }

        public void OnPointerUp(PointerEventData press)
        {
            if (locked) return;
            Pass(press, ExecuteEvents.pointerUpHandler);
        }

        public void OnBeginDrag(PointerEventData move)
        {
            scrubbing = false;
            scrubbed = false;
            if (locked) return;
            Pass(move, ExecuteEvents.beginDragHandler);
        }

        public void OnDrag(PointerEventData move)
        {
            if (locked) return;

            if (!scrubbing && Beyond(move)) scrubbing = true;
            if (!scrubbing) { Pass(move, ExecuteEvents.dragHandler); return; }

            // Whatever the drag selected on its way off the field is not wanted --
            // a highlighted number under a value that is moving reads as a mistake.
            // Every frame, not once: the value is rewritten as it is dragged, and
            // the field re-selects when its text changes under a live drag.
            if (field != null) field.caretPosition = field.text.Length;

            if (dragged != null) dragged(move.delta.x);
        }

        public void OnEndDrag(PointerEventData move)
        {
            if (!locked && !scrubbing) Pass(move, ExecuteEvents.endDragHandler);
            scrubbed = scrubbing;
            scrubbing = false;
        }

        // Whether the pointer is off the field, by more than Leeway, in any
        // direction. A pointer whose position will not map onto the rect at all
        // counts as off it.
        private bool Beyond(PointerEventData move)
        {
            RectTransform rect = transform as RectTransform;
            if (rect == null) return true;

            Vector2 local;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    rect, move.position, move.pressEventCamera, out local))
                return true;

            Rect box = rect.rect;
            return local.x < box.xMin - Leeway || local.x > box.xMax + Leeway
                || local.y < box.yMin - Leeway || local.y > box.yMax + Leeway;
        }

        private void Pass<T>(PointerEventData move, ExecuteEvents.EventFunction<T> what)
            where T : IEventSystemHandler
        {
            if (field == null) return;
            ExecuteEvents.Execute(field.gameObject, move, what);
        }

        public void OnPointerClick(PointerEventData click)
        {
            if (locked || field == null) return;

            // Checked before the wander test, not after. A second click on a field
            // the caret is already in is the one most likely to drift a few pixels
            // -- the hand is not repositioning, it is tapping -- and bailing out on
            // that is what stopped a double-click selecting anything once the field
            // had focus. A double-click that wandered is still a double-click.
            if (click.clickCount >= 2 && !scrubbed)
            {
                waiting = Patience;
                selecting = Insist;
                held = field.text;
                return;
            }

            // A drag ends with a click too; only a pointer that stayed put means
            // "type here".
            if (!Steady(click)) return;

            held = field.text;

            // Not activated here. The sheet handles neither pointer-down nor
            // selection, so both reach the field on their own -- which is what puts
            // the caret where the pointer is. Activating it as well queues a second
            // activation that lands at the end of the frame and drags the caret
            // back to the start. Watched instead, and only stepped in on if the
            // field somehow has not taken focus by itself.
            waiting = Patience;
        }

        private static bool Steady(PointerEventData pointer)
        {
            return (pointer.position - pointer.pressPosition).sqrMagnitude < Slack * Slack;
        }

        // Double-click selects the lot. The field would do this itself, but the
        // sheet is what the pointer hits. Not in the click either: ActivateInputField
        // only asks for focus, and an unfocused field has nothing to select.
        //
        // Asserted over a few frames: the field settles its own caret in its
        // LateUpdate, and which of the two runs first is not ours to decide.
        private const int Insist = 3;

        // Frames a click is given to focus the field by itself before this does it.
        private const int Patience = 2;

        private int selecting;
        private int waiting;

        // The text as it stood when a click asked for focus or a selection. The
        // moment it differs, the player is typing and this sheet stands off.
        private string held;

        private void LateUpdate()
        {
            if (field == null) return;

            // Typing cancels both. Nothing this sheet does may outlive the moment
            // the player starts entering a value: a select-all asserted over typing
            // eats every character but the last, because the field is fully
            // selected again before the next key arrives.
            if ((selecting > 0 || waiting > 0) && held != null && field.text != held)
            {
                selecting = 0;
                waiting = 0;
                held = null;
            }

            if (waiting > 0)
            {
                waiting--;
                if (waiting == 0 && !field.isFocused) field.ActivateInputField();
            }

            if (selecting <= 0 || !field.isFocused) return;

            // Anchor and focus, and nothing else. `caretPosition` looks like the
            // third thing to set and is not: its setter writes both ends at once,
            // so it collapses the selection that was just made.
            selecting--;
            field.selectionAnchorPosition = 0;
            field.selectionFocusPosition = field.text.Length;

            // Those two setters move the field's caret and anchor and nothing else:
            // they do not mark the caret graphic dirty, so the highlight that shows
            // a selection was never rebuilt and the field looked untouched while
            // holding a perfectly good selection. This is what redraws it.
            field.ForceLabelUpdate();
        }
    }
}
