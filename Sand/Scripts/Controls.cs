using Modding;

namespace SandMod
{
    // The mapper shows one page of a block's settings at a time by hiding the rest,
    // which is a lot of DisplayInMapper assignments.
    public static class Controls
    {
        // True while a panel is drawing the block whose mapper is open. The block's
        // own page and switch handlers then have nothing to do -- their controls
        // are off the mapper already -- and every display change they make marks
        // the mapper dirty, which costs it a rebuild of all of its widgets. That
        // rebuild is the hitch you feel when a switch that governs several other
        // controls is flipped.
        public static bool Suspended;

        // Null-tolerant: a panel looks its controls up by key and may not have
        // found them, and this runs inside BlockMapper's own open and close events
        // -- where a NullReferenceException stops the rest of the mapper's own
        // handling and leaves the menu stuck open.
        public static void Show(bool state, params MapperType[] controls)
        {
            if (Suspended) return;
            foreach (MapperType control in controls)
                if (control != null) control.DisplayInMapper = state;
        }
    }
}
