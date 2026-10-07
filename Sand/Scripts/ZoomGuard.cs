using System;
using UnityEngine;
using UnityEngine.EventSystems;

namespace SandMod
{
    // Stops the camera zooming while the pointer is over this. DisableCameraZoom is
    // a counter, so each hold is given back exactly once, even if destroyed while
    // hovered. One on a window covers every child.
    public class ZoomGuard : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        /// <summary>Whether to tell Besiege a menu is open as well: the editor's
        /// window does; the docked panel is already under a menu.</summary>
        public bool menu;

        private bool held;

        /// <summary>Frames before the menu hold follows the zoom hold. Both in one
        /// frame left `MouseOrbit` easing toward a stale wheel reading, zooming for
        /// as long as the pointer stayed.</summary>
        private int waiting;
        private bool menued;

        /// <summary>How many of this mod's pieces hold a menu open, so ours can be
        /// told from the game's.</summary>
        public static int Raised;

        /// <summary>How many zoom holds this mod has on the camera now, to tell its own from the game's.</summary>
        public static int Holding;

        // The wheel is dead while StatMaster's zoom counter is above nought, and the counter is a
        // count of holds from everywhere: any one that is not given back leaves the camera deaf to the
        // wheel until the level is left. Besiege takes its own while the pointer is over its own
        // scrollbars and gives them back as the pointer leaves, so a count above what this mod holds,
        // with the pointer over nothing and no button down, for a while, is a hold nobody is going to
        // give back; it is let go. One guard does it, once a frame.
        private static int healedFrame = -1;
        private static float strandedFor;
        public static int Healed;

        private static void Heal()
        {
            if (healedFrame == Time.frameCount) return;
            healedFrame = Time.frameCount;
            try
            {
                int extra = StatMaster.stopZoomCounter - Holding;
                EventSystem es = EventSystem.current;
                bool free = es != null && !es.IsPointerOverGameObject()
                            && !Input.GetMouseButton(0) && !Input.GetMouseButton(1) && !Input.GetMouseButton(2);
                if (extra <= 0 || !free) { strandedFor = 0f; return; }
                strandedFor += Time.unscaledDeltaTime;
                if (strandedFor < 1.5f) return;
                strandedFor = 0f;
                for (int n = 0; n < extra && n < 16; n++) StatMaster.DisableCameraZoom(false);
                Healed++;
            }
            catch (Exception)
            {
                strandedFor = 0f;
            }
        }

        /// <summary>Tells Besiege a menu is open, counted. Everything here that
        /// raises `SetInMenu` goes through this, or the editor takes its own for
        /// the pause menu.
        /// </summary>
        public static void Menu(bool on)
        {
            StatMaster.SetInMenu(on);
            Raised += on ? 1 : -1;
            if (Raised < 0)
            {
                Raised = 0;
            }
        }

        /// <summary>Holds the menu flag for a drag wherever the pointer goes, so
        /// Besiege's camera does not take the drag up. The caller keeps the flag;
        /// call it from `OnDisable` too.</summary>
        public static bool Grip(bool want, bool have)
        {
            if (want == have)
            {
                return have;
            }
            try
            {
                Menu(want);
            }
            catch (Exception)
            {
                return have;
            }
            return want;
        }

        public void OnPointerEnter(PointerEventData pointer) { Hold(true); }
        public void OnPointerExit(PointerEventData pointer) { Hold(false); }

        private void OnDisable() { Hold(false); }

        /// <summary>Destroyed while hovered: `OnDisable` covers an object switched
        /// off, but not every route here ends that way.</summary>
        private void OnDestroy() { Hold(false); }

        /// <summary>The canvas this hangs under, asked once.</summary>
        private Canvas roof;
        private bool looked;

        /// <summary>Whether the pointer could still be told to leave. A disabled
        /// `Canvas` leaves its objects active -- so no `OnDisable` -- and stops uGUI
        /// sending the exit that gives the hold back: Tab hides the game's interface
        /// that way, and a hold taken over the board outlived every route that
        /// returns it, leaving the camera's wheel dead until something else
        /// balanced the count.</summary>
        private bool Live()
        {
            if (!looked)
            {
                looked = true;
                roof = GetComponentInParent<Canvas>();
            }
            return isActiveAndEnabled && gameObject.activeInHierarchy
                && (roof == null || roof.isActiveAndEnabled);
        }

        private void Update()
        {
            Heal();
            // Given back the moment it cannot be given back by hand.
            if (held && !Live())
            {
                Hold(false);
                return;
            }
            if (waiting <= 0)
            {
                return;
            }
            waiting--;
            if (waiting > 0 || !held || menued)
            {
                return;
            }
            try
            {
                Menu(true);
                menued = true;
            }
            catch (Exception)
            {
            }
        }

        private void Hold(bool on)
        {
            if (held == on) return;
            try
            {
                StatMaster.DisableCameraZoom(on);
                held = on;
                Holding += on ? 1 : -1;
                if (Holding < 0) Holding = 0;
                if (!menu)
                {
                    return;
                }
                if (on)
                {
                    // Three rather than one: which of this and the camera runs
                    // first in a frame is not ours to know.
                    waiting = 3;
                    return;
                }
                waiting = 0;
                if (menued)
                {
                    Menu(false);
                    menued = false;
                }
            }
            catch (Exception)
            {
                held = false;
            }
        }
    }
}
