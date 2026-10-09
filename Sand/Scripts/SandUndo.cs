namespace SandMod
{
    /// <summary>
    /// A brush stroke, or a "Clear all sand", as one step in the machine's undo history (Besiege's UndoSystem), so
    /// the game's own undo and redo buttons and keys take sand back and put it in again, in order with the machine's
    /// edits. It holds the chunks the step changed as they were before it (SandBed.Edit); undoing puts those back and
    /// keeps how they were just then, which redoing puts back in turn -- so Undo and Redo are the same swap.
    ///
    /// A stroke's step goes into the history the moment the stroke begins, empty, and is filled when the stroke is
    /// finished (SandField.FinishStroke). Added when it finished instead -- once its pile had settled -- a block placed
    /// in between went in ahead of it, and undo took them back in the wrong order. Undoing a step whose stroke is still
    /// open finishes the stroke first.
    ///
    /// None of the change flags is set, so the history does nothing round it for the machine (no interpolation reset,
    /// no cluster rebuild). A step with nothing in it, or whose sand has been replaced wholesale since
    /// (SandField.TakeStep), returns false, which UndoSystem takes as "nothing done here" and goes on to the step
    /// before.
    /// </summary>
    public class SandUndo : UndoAction
    {
        /// <summary>The chunks to put back the next time the step is taken; null for nothing.</summary>
        public SandBed.Edit Edit;
        public readonly int Epoch;

        public SandUndo(int recordedIn) { Epoch = recordedIn; }

        public override bool Undo() { return Swap(); }
        public override bool Redo() { return Swap(); }

        private bool Swap()
        {
            SandField field = SandField.Existing;
            return field != null && field.TakeStep(this);
        }
    }
}
