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
        app.Context = () => $"sort '{app.Name("DataSort")}' | first callsign {First("Callsign")} | headers {string.Join(", ", app.FindAll(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text)).Select(e => e.Current.Name).Where(n => n.StartsWith("ID", StringComparison.Ordinal)))}";

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

        // A header sorts by its column, and a second click reverses it.
        app.Foreground();
        var header = app.Wait(() => app.FindNamed("ID", ControlType.Text).FirstOrDefault(), "the ID header");
        ClickAt(header.Current.BoundingRectangle);
        app.Until(() => app.Name("DataSort").Contains("ID", StringComparison.Ordinal) && First("ID") == "100", "ascending by ID");
        header = app.Wait(() => app.FindNamed("ID \u2191", ControlType.Text).FirstOrDefault(), "the header shows the order");
        ClickAt(header.Current.BoundingRectangle);
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
        var row = app.Rows()[1];
        app.Foreground();
        RightClickAt(row.Current.BoundingRectangle);
        app.First("记录详情", ControlType.MenuItem).Invoke();
        app.One("RowDetails");
        Assert.Single(app.Query("data-state")["selectedRows"]!.AsArray());
        app.Close();
    }
}
