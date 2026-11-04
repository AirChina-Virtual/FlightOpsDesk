using System.Text.Json.Nodes;
using System.Windows.Automation;
using VamSys.TestSupport;
using VamSys.UiTests.Driver;
using static VamSys.UiTests.Driver.AppWindow;

namespace VamSys.UiTests;

/// <summary>
/// The task page with 1,000 finished tasks and one of 20,000 items. Replaces scripts/test-v3-verification-ui.ps1 -Tasks
/// (and -InteractionsOnly).
/// </summary>
public class TaskPageTests
{
    static AutomationElement[] TaskCards(AppWindow app) =>
        app.Wait(() => app.FindAll(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem))
            .Where(e => e.Current.Name.Contains("TaskJobViewModel", StringComparison.Ordinal)).ToArray() is { Length: > 0 } rows ? rows : null, "task cards");

    static AutomationElement[] DetailRows(AppWindow app) =>
        app.Wait(() => app.Find("TaskDetailsList").FirstOrDefault() is { } list && ListItemsBelow(list) is { Length: > 0 } rows ? rows : null, "task detail rows");

    [WindowFact]
    public void A_large_task_page_stays_stable_with_bounded_controls_and_fast_local_updates()
    {
        using var app = Start("tasks", DataUiFixture.SeedTasks);
        app.Context = () => app.Query("task-state").ToJsonString();

        var initial = app.Query("tasks");
        Assert.Equal(1001, initial.Int("jobs"));
        var cards = TaskCards(app);
        Assert.True(cards.Length < 100, $"Task cards not virtualized: {cards.Length}");

        var progress = app.Query("progress");
        Assert.Equal(initial.Int("builds"), progress.Int("builds"));
        Assert.True(progress.Number("p95") <= 50, $"Local updates took P95 {progress.Number("p95")} ms");

        app.InvokeNamed("Details");
        var details = DetailRows(app);
        Assert.True(details.Length < 100, $"Details not virtualized: {details.Length}");
        app.InvokeNamed("Close");

        // Measured only: rebuilds every task card the old way seven times, up to about 25 seconds each.
        var baseline = app.Query("baseline", TimeSpan.FromMinutes(5));
        File.WriteAllText(Path.Combine(app.OutputDirectory, "task-ui-benchmark.json"), new JsonObject
        {
            ["jobs"] = 1001, ["historyItems"] = 20000, ["activeItems"] = 20000, ["materializedCards"] = cards.Length, ["materializedDetails"] = details.Length,
            ["localP95Ms"] = progress.Number("p95"), ["localMaxMs"] = progress.Number("max"), ["localSamples"] = progress.Int("samples"), ["pageBuilds"] = progress.Int("builds"),
            ["baselineP95Ms"] = baseline.Number("p95"), ["baselineSamples"] = baseline.Int("samples")
        }.ToJsonString());
    }

    [WindowFact]
    public void A_large_task_keeps_its_selection_and_scroll_across_languages_and_can_be_cancelled()
    {
        using var app = Start("tasks-interactions", DataUiFixture.SeedTasks);
        app.Context = () => app.Query("task-state").ToJsonString();

        app.Query("tasks");
        TaskCards(app);
        app.Query("progress");
        app.InvokeNamed("Details");
        DetailRows(app);
        app.InvokeNamed("Close");

        var list = app.Wait(() => app.Find("TaskJobsList").FirstOrDefault(), "the task list");
        var scroll = list.Scroll();
        scroll.SetScrollPercent(ScrollPattern.NoScroll, 100);
        app.Until(() => scroll.Current.VerticalScrollPercent >= 99.9, "the task list reaches its end");
        ListItemsBelow(list)[^1].Select();

        var before = app.Query("task-state");
        app.Query("language:zh-CN");
        app.Query("progress");
        app.Query("language:en-US");
        var after = app.Query("task-state");
        Assert.Equal((before.Str("selected"), before.Int("builds")), (after.Str("selected"), after.Int("builds")));
        Assert.True(scroll.Current.VerticalScrollPercent >= 99.9, "Task scroll lost");

        app.Query("cancellable");
        app.InvokeNamed("Cancel subsequent requests");
        app.Until(() => app.Query("task-state").Bool("canceled"), "the running task is cancelled");
    }
}
