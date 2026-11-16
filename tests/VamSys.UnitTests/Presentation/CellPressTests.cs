using VamSys.Presentation;

namespace VamSys.UnitTests.Presentation;

/// <summary>
/// Which pointer presses on a cell start editing (EditableCell.OnPointerPressed). Only the primary button does, so a right
/// click opens the row menu instead of the editor. Device presses are written as WinUI reports them.
/// </summary>
public class CellPressTests
{
    public static TheoryData<string, CellPress, bool> Presses => new()
    {
        { "mouse left", new CellPress(Left: true), true },
        { "mouse right", new CellPress(Left: false, Right: true), false },
        { "mouse middle", new CellPress(Left: false, Middle: true), false },
        { "mouse left with right held", new CellPress(Left: true, Right: true), true },
        { "touch contact (reported as left)", new CellPress(Left: true), true },
        { "pen tip contact (reported as left)", new CellPress(Left: true), true },
        { "pen with barrel button (reported as right)", new CellPress(Left: false, Right: true, Barrel: true), false },
        { "eraser reported without the primary button", new CellPress(Left: false, Eraser: true), false },
        // Kept as it was: an eraser contact that WinUI also reports as the primary button edits.
        { "eraser reported with the primary button", new CellPress(Left: true, Eraser: true), true },
        { "nothing pressed (hover)", new CellPress(Left: false), false },
    };

    [Theory]
    [MemberData(nameof(Presses))]
    public void Only_a_press_with_the_primary_button_starts_editing(string what, CellPress press, bool starts)
        => Assert.True(CellPress.StartsEdit(press, editorOpen: false, readOnly: false) == starts, what);

    [Fact]
    public void An_open_editor_or_a_read_only_cell_never_starts_another_edit()
    {
        Assert.False(CellPress.StartsEdit(new CellPress(Left: true), editorOpen: true, readOnly: false));
        Assert.False(CellPress.StartsEdit(new CellPress(Left: true), editorOpen: false, readOnly: true));
    }
}
