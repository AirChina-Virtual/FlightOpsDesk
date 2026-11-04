using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Windows.Automation;
using VamSys.TestSupport;
using VamSys.UiTests.Driver;
using static VamSys.UiTests.Driver.AppWindow;

namespace VamSys.UiTests;

/// <summary>
/// 20,000 routes in the product package: the rows are virtualized, and the last page and its scroll position survive a
/// language switch on another page. The time of the switch goes to language-switch.json. Replaces scripts/test-api-ui-20k.ps1.
/// </summary>
public class LargeTableLanguageTests
{
    static ScrollPattern TableScroll(AppWindow app) => app.Wait(() => app.FindAll(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.List))
        .Select(l => l.TryGetCurrentPattern(ScrollPattern.Pattern, out var pattern) ? (ScrollPattern)pattern : null)
        .FirstOrDefault(p => p is { Current.VerticallyScrollable: true }), "the scrollable data table");

    static string[] ShownIds(AppWindow app) => [.. app.FindNamed("ID", ControlType.Edit).Select(e => e.Text())];

    [WindowFact]
    public void The_last_page_and_its_scroll_position_survive_a_language_switch()
    {
        using var app = Start("large-table-language", DataUiFixture.SeedDemoRoutes, Package.Product);
        app.Context = () => app.Name("StatusLabel");

        app.SelectItem("数据管理");
        app.First("ID", ControlType.Edit);
        var materialized = app.Rows().Length;
        Assert.InRange(materialized, 1, 100);

        app.One("DataLastPage").Invoke();
        app.Until(() => ShownIds(app).Contains("19800"), "the last page's first row");
        TableScroll(app).SetScrollPercent(ScrollPattern.NoScroll, 100);
        Thread.Sleep(250);
        var before = TableScroll(app).Current.VerticalScrollPercent;

        app.SelectItem("连接与设置");
        app.One("LanguagePicker").Expand();
        var timer = Stopwatch.StartNew();
        app.SelectItem("English");
        app.First("Connection & settings", ControlType.ListItem);
        timer.Stop();

        app.SelectItem("Data management");
        // The virtualized rows take a moment to come back; a single look can find none during the refresh.
        var ids = app.Wait(() => ShownIds(app) is { Length: > 0 } shown && shown[^1] == "19999" ? shown : null, "the last row is shown again");
        var after = TableScroll(app).Current.VerticalScrollPercent;
        Assert.True(Math.Abs(before - after) <= 0.05 && after >= 99.9, $"Scroll position lost: {before} -> {after}");
        Assert.True(ids.Length <= 100, $"Rows not virtualized: {ids.Length}");

        File.WriteAllText(Path.Combine(app.OutputDirectory, "language-switch.json"), new JsonObject
        {
            ["languageSwitchMilliseconds"] = timer.ElapsedMilliseconds, ["materialized"] = materialized, ["restoredRows"] = ids.Length, ["firstId"] = ids[0], ["lastId"] = ids[^1],
            ["scrollBefore"] = before, ["scrollAfter"] = after, ["workingSetBytes"] = app.WorkingSetBytes
        }.ToJsonString());
        app.Close();
    }
}
