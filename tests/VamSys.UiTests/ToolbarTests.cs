using System.Windows.Automation;
using VamSys.TestSupport;
using VamSys.UiTests.Driver;
using static VamSys.UiTests.Driver.AppWindow;

namespace VamSys.UiTests;

/// <summary>
/// The data page's toolbar: resource tabs with their pending counts, the filter as choices, a column header that sorts
/// both ways, the view menu hiding a column, the commands grouped in menus, and the row menu.
/// </summary>
public class ToolbarTests
{
    const string Departure = "Departure Airport (ICAO/IATA)";

    [WindowFact]
    public void The_toolbar_groups_the_commands_and_the_table_sorts_filters_and_hides_columns()
    {
        using var app = Start("toolbar");
        app.WaitText("工作区");
        app.Click("创建演示工作区");
        app.First("打开数据管理", ControlType.Button);
        app.SelectItem("数据管理");
        app.Until(() => app.Rows().Length > 3, "rows are shown");
        int Total() => app.Query("data-state").Int("total");
        string First(string field) => app.Query("data-row:0")["fields"]!.Str(field) ?? "";
        var all = Total();
        // Where the ID header is: its rectangle and IsOffscreen, the page check box at the left end of the same header row,
        // and the table's horizontal offset (headers and rows scroll sideways together in one ScrollViewer).
        string Header(AutomationElement e) => $"'{e.Current.Name}' offscreen={e.Current.IsOffscreen} rect {e.Current.BoundingRectangle}";
        app.Context = () => $"sort '{app.Name("DataSort")}' | first callsign {First("Callsign")} | horizontal {app.Query("data-state").Str("horizontal")}"
            + $" | headers {string.Join(", ", app.FindAll(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text)).Where(e => e.Current.Name.StartsWith("ID", StringComparison.Ordinal)).Select(Header))}"
            + $" | page check {string.Join(", ", app.Find("DataSelectPage").Select(Header))}"
            + $" | row 1 editors {app.Query("data-row:1").Str("editors")} focused {app.Query("data-row:1").Str("focused")}"
            + $" | UIA focus {AutomationElement.FocusedElement?.Current.ControlType.ProgrammaticName} '{AutomationElement.FocusedElement?.Current.Name}' class {AutomationElement.FocusedElement?.Current.ClassName}"
            + $" | menu items [{string.Join(", ", app.FindAll(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem)).Select(e => e.Current.Name))}]"
            + $" | input {string.Join("; ", app.Query("input-log")["input"]!.AsArray().Select(i => i!.GetValue<string>()).TakeLast(12))}";

        // An edit shows on its resource's tab as a pending change.
        app.Edit("Callsign", "DEM900");
        app.Until(() => Below(app.One("ResourcePicker"), Condition.TrueCondition).Any(e => e.Current.Name.StartsWith("航线 · ", StringComparison.Ordinal)), "the routes tab counts its changes");
        app.SelectResource("机场");
        app.Until(() => app.Query("data-state").Int("total") != all, "the airports are shown");
        app.SelectResource("航线");
        app.Until(() => Total() == all, "back to the routes");

        // The filter choices are all in view; the changed records are one press away.
        app.One("DataFilterModified").Toggle();
        app.Until(() => Total() < all && Total() > 0, "only changed records are shown");
        app.One("DataFilterAll").Toggle();
        app.Until(() => Total() == all, "all records again");

        // A header sorts by its column, and a second click reverses it. A cell editor still open from the edit above
        // would take the first click to close; the focus moves to the table first.
        app.Foreground();
        app.Rows()[0].SetFocus();
        app.Until(() => AutomationElement.FocusedElement?.Current.ClassName != "TextBox", "the focus is on the table");
        // The ID column is at the table's left edge; window runs found its header with an empty rectangle, out of view. As a
        // user drags the scroll bar back, the table is scrolled to its left edge (the QA command sets the same horizontal
        // offset) before the header is clicked. ClickOn then waits for a rectangle inside the window, and on timeout the
        // context above says where the header and the table's offset were.
        void ShowLeftEdge() => app.Query("data-scroll:0,0");
        ShowLeftEdge();
        var header = app.Wait(() => app.FindNamed("ID", ControlType.Text).FirstOrDefault(), "the ID header");
        app.ClickOn(header, "the ID header");
        app.Until(() => app.Name("DataSort").Contains("ID", StringComparison.Ordinal) && First("ID") == "100", "ascending by ID");
        ShowLeftEdge();
        header = app.Wait(() => app.FindNamed("ID \u2191", ControlType.Text).FirstOrDefault(), "the header shows the order");
        app.ClickOn(header, "the ID \u2191 header");
        app.Until(() => First("ID") == "111", "descending by ID");
        // The view menu hides a column and shows it again.
        var columns = app.Query("data-state").Int("columns");
        app.One("DataView").Expand();
        var shown = app.First("显示的列", ControlType.MenuItem);
        ((ExpandCollapsePattern)shown.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Expand();
        app.First("Tags", ControlType.MenuItem).Toggle();
        app.Until(() => app.Query("data-state").Int("columns") == columns - 1, "Tags is hidden");
        app.One("DataView").Collapse();
        app.Screenshot("toolbar.png");
        app.One("DataView").Expand();
        ((ExpandCollapsePattern)app.First("显示的列", ControlType.MenuItem).GetCurrentPattern(ExpandCollapsePattern.Pattern)).Expand();
        app.First("显示全部列", ControlType.MenuItem).Invoke();
        app.Until(() => app.Query("data-state").Int("columns") == columns, "all columns again");

        // The data commands are in their menu, and a row's own menu opens its details.
        // Offline, checking opens a dialog only when it finds problems.
        app.Click("校验");
        if (Poll(() => app.Find("PrimaryButton").FirstOrDefault(), TimeSpan.FromSeconds(2)) != null)
            app.DialogCancel();
        app.DialogClosed();
        // A user right-clicks a cell of the record. The centre of the whole row was wherever the table's sideways scroll put
        // it, and a right click on an editable cell used to open its editor instead of the menu (EditableCell). The table is
        // shown from its left edge and the second row's Departure cell, an editable one, is right-clicked: the row menu
        // opens and no editor does.
        ShowLeftEdge();
        app.Foreground();
        var cell = app.Wait(() => Below(app.Rows()[1], new AndCondition(new PropertyCondition(AutomationElement.NameProperty, Departure),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit))).FirstOrDefault(), "the second row's Departure cell");
        app.RightClickOn(cell, "the second row's Departure cell");
        app.First("记录详情", ControlType.MenuItem);
        Assert.Equal(0, app.Query("data-row:1").Int("editors"));
        app.First("记录详情", ControlType.MenuItem).Invoke();
        app.One("RowDetails");
        Assert.Single(app.Query("data-state")["selectedRows"]!.AsArray());
        app.Close();
    }
}
