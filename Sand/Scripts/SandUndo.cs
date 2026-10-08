namespace SandMod
{
    /// <summary>
    /// A brush stroke, or a "Clear all sand", as one step in the machine's undo history (Besiege's UndoSystem), so
    /// the game's own undo and redo buttons and keys take sand back and put it in again, in order with the machine's
    /// edits. It holds the chunks the step changed as they were before it (SandBed.Edit); undoing puts those back and
    /// keeps how they were just then, which redoing puts back in turn -- so Undo and Redo are the same swap.
    ///
    /// None of the change flags is set, so the history does nothing round it for the machine (no interpolation reset,
    /// no cluster rebuild). When the sand has been replaced wholesale since (SandField.TakeStep), the step returns
    /// false, which UndoSystem takes as "nothing done here" and goes on to the step before.
    /// </summary>
    public class SandUndo : UndoAction
    {
        private SandBed.Edit edit;
        private readonly int epoch;

        public SandUndo(SandBed.Edit e, int recordedIn)
        {
            edit = e;
            epoch = recordedIn;
        }

        public override bool Undo() { return Swap(); }
        public override bool Redo() { return Swap(); }

        private bool Swap()
        {
            SandField field = SandField.Existing;
            if (field == null || edit == null) return false;
            SandBed.Edit back = field.TakeStep(edit, epoch);
            if (back == null) return false;
            edit = back;
            return true;
        }
    }
}
