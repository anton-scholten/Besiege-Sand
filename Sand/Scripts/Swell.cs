using UnityEngine;
using UnityEngine.EventSystems;

namespace SandMod
{
    // Grows what it is pointed at while the pointer is on it, the way Besiege's
    // own buttons do. UI Factory's controls bring their own version of this; the
    // hand-built selector does not, so it gets this one.
    public class Swell : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        private const float Speed = 14f;

        // What actually grows -- the lettering, not the plate behind it, so a row
        // of them keeps its spacing.
        public Transform grows;

        // How much it grows by.
        public float grown = 1.15f;

        private UnityEngine.UI.Selectable gate;
        private float wanted = 1f;
        private float at = 1f;

        public void OnPointerEnter(PointerEventData pointer)
        {
            if (Off()) return;
            wanted = grown;
        }

        public void OnPointerExit(PointerEventData pointer) { wanted = 1f; }

        // Looked up here rather than in Awake: AddComponent runs Awake there and
        // then, before whatever this is guarding has been put on the object.
        private bool Off()
        {
            if (gate == null) gate = GetComponent<UnityEngine.UI.Selectable>();
            return gate != null && !gate.IsInteractable();
        }

        private void OnDisable()
        {
            wanted = 1f;
            at = 1f;
            Apply();
        }

        private void Update()
        {
            // Switched off under the pointer: it has had no exit and would sit
            // grown until one arrives.
            if (Off()) wanted = 1f;

            if (Mathf.Abs(at - wanted) < 0.001f)
            {
                if (at == wanted) return;
                at = wanted;
            }
            else
            {
                // Unscaled: the build menu is open at any time scale, pause
                // included.
                at = Mathf.Lerp(at, wanted, Time.unscaledDeltaTime * Speed);
            }
            Apply();
        }

        private void Apply()
        {
            Transform target = grows != null ? grows : transform;
            if (target != null) target.localScale = new Vector3(at, at, 1f);
        }
    }
}
