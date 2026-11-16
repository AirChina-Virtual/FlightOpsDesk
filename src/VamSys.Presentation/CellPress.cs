namespace VamSys.Presentation;

// A pointer press on a table cell, as the pointer reports it. WinUI reports a touch contact and a pen-tip contact as the
// left (primary) button, a pen with its barrel button held as the right button, and an eraser contact by its own flag.
public readonly record struct CellPress(bool Left, bool Right = false, bool Middle = false, bool Barrel = false, bool Eraser = false)
{
    // Only the primary button edits: a right click is left to the list so that it opens the row menu (taking it opened
    // the editor instead, which replaced the text the click was on, and no menu came). Whatever else is held or flagged
    // with it, a press that includes the primary button starts editing, and one without it never does.
    public static bool StartsEdit(CellPress press, bool editorOpen, bool readOnly) => !editorOpen && !readOnly && press.Left;
}
