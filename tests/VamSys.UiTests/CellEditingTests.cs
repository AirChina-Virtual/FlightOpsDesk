using System.Text.Json.Nodes;
using System.Windows.Automation;
using VamSys.TestSupport;
using VamSys.UiTests.Driver;
using static VamSys.UiTests.Driver.AppWindow;

namespace VamSys.UiTests;

/// <summary>
/// Real pointer and keyboard editing of text-only cells: a click opens the editor, typing commits, Tab moves to the next
/// cell's editor, and automation reports values and read-only state of deleted rows. Replaces scripts/test-cell-editing-ui.ps1.
/// </summary>
public class CellEditingTests
{
    const string Departure = "Departure Airport (ICAO/IATA)", Arrival = "Arrival Airport (ICAO/IATA)";

    [WindowFact]
    public void Text_cells_edit_by_pointer_keyboard_and_automation_and_deleted_rows_are_read_only()
    {
        using var app = Start("cell-editing", directory => DataUiFixture.Seed(directory, 20000));
        JsonNode Row(int row) => app.Query("data-row:" + row);
        JsonNode RowWhere(int row, Func<JsonNode, bool> holds, string what) => app.Wait(() => Row(row) is var s && holds(s) ? s : null, what);
        AutomationElement[] Cells(string name) => app.FindNamed(name, ControlType.Edit);
        app.Context = () =>
        {
            var focus = AutomationElement.FocusedElement.Current;
            return $"row 1: {Row(1).ToJsonString()} | UIA focus: {focus.ControlType.ProgrammaticName} '{focus.Name}' pid {focus.ProcessId}";
        };

        var opened = app.Query("data-open")["state"]!;
        Assert.True(opened.Bool("hasEditors"), "The data table has no editable cells");
        Assert.Equal(0, opened.Int("textBoxes"));

        // Pointer: click the second row's Departure cell, type, then Tab into the next cell and type there. The editor opens
        // with the caret at the end; End would reach the list there and move focus to the last row, as with any single-line
        // TextBox. Departure is a column inside the window; Remarks is scrolled out of view horizontally.
        app.Foreground();
        var departures = app.Wait(() => Cells(Departure) is { Length: > 1 } c && !c[1].Current.BoundingRectangle.IsEmpty ? c : null, "second row's Departure cell on screen");
        // Once in about six full runs the first click did nothing (focus stayed on the title bar). The likely cause, seen the
        // same way in ToolbarTests (the window got no pointer press and was deactivated), is a click at a rectangle that was
        // not laid out yet, which the driver turned into a point on another window. ClickOn now waits for a rectangle inside
        // this window whose point belongs to this program, and fails with what it found otherwise. Not yet confirmed by a
        // run, so the click still repeats and docs/TEST-REPORT.md keeps the question open.
        var editing = app.Wait(() =>
        {
            app.ClickOn(departures[1], "the second row's Departure cell");
            return Poll(() => Row(1) is var s && s.Str("focused") == "TextBox" ? s : null, TimeSpan.FromSeconds(2));
        }, "click focuses the editor");
        Assert.Equal(1, editing.Int("editors"));
        TypeDigits("123");
        RowWhere(1, s => s["fields"]!.Str(Departure) == "ZBAA123", "typing edits the row");

        var before = Row(1)["fields"]!.Str(Arrival);
        Press(Keys.Tab);
        RowWhere(1, s => s.Str("focused") == "TextBox", "Tab focuses the next editor");
        TypeDigits("456");
        var tabbed = RowWhere(1, s => s["fields"]!.Str(Arrival) == before + "456", "typing after Tab edits the next cell");
        Assert.Equal(1, tabbed.Int("editors"));
        Press(Keys.Escape);
        var closed = RowWhere(1, s => s.Int("editors") == 0, "Escape closes the editor");
        Assert.Equal("EditableCell", closed.Str("focused"));

        // Automation: one edit element per realized cell, with the row value.
        var remarks = app.Wait(() => Cells("Remarks") is { Length: > 0 } c ? c : null, "Remarks cells");
        Assert.Equal(("row 0", false), (remarks[0].Text(), remarks[0].IsReadOnly()));
        remarks[0].SetText("set by automation");
        var set = Row(0);
        Assert.Equal(("set by automation", 0), (set["fields"]!.Str("Remarks"), set.Int("editors")));

        // Deleted rows are read-only to automation.
        Assert.True(app.Query("data-mark-deleted:2").Bool("readOnly"), "Deleted row cells are editable");
        var row2 = Assert.Single(Cells("Remarks"), c => c.Text() == "row 2");
        Assert.True(row2.IsReadOnly(), "Automation reports a deleted row as editable");

        app.Close();
    }
}
