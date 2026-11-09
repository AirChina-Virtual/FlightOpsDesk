using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Windows.Automation;
using VamSys.TestSupport;
using VamSys.UiTests.Driver;
using static VamSys.UiTests.Driver.AppWindow;

namespace VamSys.UiTests;

/// <summary>
/// Spreadsheet-style editing with the real keyboard: typing over a cell, a time put into its format, Enter moving down,
/// Escape taking an edit back without leaving an undo step, a list to pick a fixed value from, a block pasted from the
/// clipboard as one undo step, a value filled down the selected rows, and the record details pane.
/// </summary>
public partial class TableEditingTests
{
    const string Departure = "Departure Airport (ICAO/IATA)", Time = "Departure Time (HH:MM)";
    const byte Up = 0x26, Right = 0x27, Down = 0x28;

    [WindowFact]
    public void Cells_edit_like_a_spreadsheet()
    {
        using var app = Start("table-editing");
        app.WaitText("工作区");
        app.Click("创建演示工作区");
        app.First("打开数据管理", ControlType.Button);
        app.SelectItem("数据管理");
        app.Until(() => app.Rows().Length > 3, "rows are shown");
        string Field(int row, string field) => app.Query("data-row:" + row)["fields"]!.Str(field) ?? "";
        int UndoSteps() => app.Query("state").Int("undo");
        string FocusedName() => AutomationElement.FocusedElement.Current.Name;
        app.Context = () =>
        {
            var focus = AutomationElement.FocusedElement.Current;
            return $"row 0: {app.Query("data-row:0").ToJsonString()} | UIA focus: {focus.ControlType.ProgrammaticName} '{focus.Name}' class {focus.ClassName}";
        };

        // A cell of the first row without an editor: click into Departure, take it back with Escape, then move with the
        // arrow keys, which scroll the table sideways as they go.
        void FocusCell(int row, string field)
        {
            // Back to the first row and the left edge, where Departure is on screen.
            app.Query("data-scroll:0,0");
            app.Foreground();
            var cells = app.Wait(() => app.FindNamed(Departure, ControlType.Edit) is { Length: > 0 } c && !c[0].Current.BoundingRectangle.IsEmpty ? c : null, "Departure cells on screen");
            app.Wait(() =>
            {
                ClickAt(cells[0].Current.BoundingRectangle);
                return Poll(() => app.Query("data-row:0").Str("focused") == "TextBox" ? "editing" : null, TimeSpan.FromSeconds(2));
            }, "a click opens the editor");
            Press(Keys.Escape);
            app.Until(() => app.Query("data-row:0").Int("editors") == 0 && FocusedName() == Departure, "Escape leaves the cell focused without an editor");
            for (var n = 0; FocusedName() != field; n++)
            {
                Assert.True(n < 40, $"No '{field}' column to the right of Departure");
                Press(Right);
            }
            for (var n = 0; n < row; n++)
                Press(Down);
            Thread.Sleep(200);
        }

        // Typing over a cell replaces its value; Enter puts the time into its format and moves down.
        FocusCell(0, Time);
        var steps = UndoSteps();
        TypeDigits("930");
        Press(Keys.Enter);
        app.Until(() => Field(0, Time) == "09:30", "930 becomes 09:30");
        Assert.Equal(steps + 1, UndoSteps());
        app.Until(() => FocusedName() == Time && app.Query("data-row:1").Int("editors") == 0, "Enter moves to the next row without an editor");

        // Escape takes an edit back and leaves no undo step behind.
        var second = Field(1, Time);
        TypeDigits("1");
        app.Until(() => Field(1, Time) == "1", "typing replaces the value");
        Press(Keys.Escape);
        app.Until(() => Field(1, Time) == second, "Escape returns the value");
        Assert.Equal(steps + 1, UndoSteps());

        // A block pasted on a cell fills the cells below it, after a confirmation, as one undo step.
        Press(Up);
        SetClipboard("0700\r\n0715\r\n");
        PressWithControl((byte)'V');
        app.DialogPrimary("粘贴").Invoke();
        app.DialogClosed();
        app.Until(() => Field(0, Time) == "07:00" && Field(1, Time) == "07:15", "the pasted block is in place");
        Assert.Equal(steps + 2, UndoSteps());
        app.Foreground();
        PressWithControl((byte)'Z');
        app.Until(() => Field(0, Time) == "09:30" && Field(1, Time) == second, "one undo takes the whole block back");

        // Ctrl+D copies the cell to the same column of the selected records.
        var rows = app.Rows();
        rows[1].AddToSelection();
        rows[2].AddToSelection();
        FocusCell(0, Time);
        PressWithControl((byte)'D');
        app.DialogPrimary("填充").Invoke();
        app.DialogClosed();
        app.Until(() => Field(1, Time) == "09:30" && Field(2, Time) == "09:30", "the value is filled down the selection");

        // Resting the pointer on a changed cell shows the value the server has.
        FocusCell(0, Time);
        var focusedCell = AutomationElement.FocusedElement.Current.BoundingRectangle;
        SetCursorPos((int)(focusedCell.X + focusedCell.Width / 2), (int)(focusedCell.Y + focusedCell.Height / 2));
        app.Wait(() => AutomationElement.RootElement.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ToolTip)) is { } tip
            && tip.Current.Name.Contains("服务器上的值", StringComparison.Ordinal) ? tip : null, "the hint with the server's value");

        // Ctrl+Enter shows the whole record beside the table; its fields edit the same row.
        PressWithControl(Keys.Enter);
        var pane = app.Wait(() => app.Find("RowDetails").FirstOrDefault(), "the details pane");
        var paneTime = Below(pane, new AndCondition(new PropertyCondition(AutomationElement.NameProperty, Time), new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)));
        Assert.Equal("09:30", paneTime[0].Text());
        paneTime[0].SetText("11:45");
        app.Until(() => Field(0, Time) == "11:45", "an edit in the pane changes the row");
        app.Screenshot("details.png");
        app.One("RowDetailsClose").Invoke();
        app.Until(() => app.Find("RowDetails").Length == 0, "the pane closes");

        // A column with fixed values opens its list with the editor; Enter takes the highlighted value.
        FocusCell(2, "Type");
        Press(Keys.Enter);
        app.Wait(() => app.FindNamed("charter", ControlType.ListItem).FirstOrDefault(), "the list of route types");
        Press(Down);
        Press(Down);
        Press(Down);
        Press(Keys.Enter);
        app.Until(() => Field(2, "Type") == "charter", "the picked type is the value");
        Press(Keys.Enter);        app.Close();
    }

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool SetCursorPos(int x, int y);

    // clip.exe puts its input on the clipboard; the test needs no STA thread of its own for that.
    static void SetClipboard(string text)
    {
        using var clip = Process.Start(new ProcessStartInfo("clip.exe") { RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true })!;
        clip.StandardInput.Write(text);
        clip.StandardInput.Close();
        clip.WaitForExit();
    }
}
