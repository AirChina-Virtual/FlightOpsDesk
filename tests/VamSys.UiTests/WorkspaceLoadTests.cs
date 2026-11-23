using VamSys.Core;
using VamSys.Infrastructure;
using VamSys.TestSupport;
using VamSys.UiTests.Driver;
using static VamSys.UiTests.Driver.AppWindow;
using static VamSys.UiTests.Driver.Disk;

namespace VamSys.UiTests;

/// <summary>
/// Workspace reads run on a worker (2026-11-23, stage 7 item 3): at startup the window shows only the loading panel until
/// the first workspace is in place; while a switch reads, edits, navigation, commands, submission and autosave wait and the
/// panel shows the progress in the window's language; a failed read keeps the current workspace; closing waits for the
/// read; quick successive choices end on one workspace that the picker names.
/// </summary>
public class WorkspaceLoadTests
{
    const string Large = "QA 20000 full route fields";
    const string Other = "QA other";
    const string Third = "QA third";

    // The 20 000-route workspace of the window baseline (opened first: its name sorts first) and two small ones, the
    // first of them with a fleet to edit.
    static void Seed(string directory)
    {
        DataUiFixture.Seed(directory, 20000);
        var store = new WorkspaceStore(directory);
        var other = new Workspace { Name = Other, Mode = RunMode.Demo };
        other.Resources[ResourceKind.Fleets].Draft.Add(new DataRow { Fields = new Dictionary<string, string> { ["Name"] = "fleet before", ["Type Code"] = "B738" } });
        store.Save(other);
        store.Save(new Workspace { Name = Third, Mode = RunMode.Demo });
    }

    static System.Text.Json.Nodes.JsonNode Idle(AppWindow app, string workspace) =>
        app.Wait(() => app.Query("load-state") is var s && !s.Bool("loading") && s.Bool("idle") && s.Str("workspaceName") == workspace ? s : null, $"'{workspace}' is shown");

    static void Pick(AppWindow app, string name)
    {
        // The picker offers the workspaces that are not shown, in name order; switch until the wanted one is shown.
        for (var n = 0; n < 3 && app.Query("load-state").Str("workspaceName") != name; n++)
        {
            app.Query("workspace-pick-other");
            app.Until(() => app.Query("load-state") is var s && !s.Bool("loading") && s.Bool("idle"), "the switch ends");
        }
    }

    [WindowTheory]
    [InlineData("en-US", "Loading the workspace…", "2,000 of 40,006 rows read")]
    [InlineData("zh-CN", "正在读取工作区…", "已读取 2,000 / 40,006 行")]
    public void A_switch_reads_in_the_background_with_the_window_locked_and_its_progress_shown(string language, string text, string rows)
    {
        using var app = Start("load-lock-" + language, Seed);
        app.Context = () => app.Query("load-state").ToJsonString();
        app.Query("language:" + language);
        Idle(app, Large);
        app.Query("workspace-pick-other");
        Idle(app, Other);
        app.Query("prepare");
        var before = app.Query("load-state");

        app.Query("load-hold");
        app.Query("workspace-pick-other");
        var held = app.Wait(() => app.Query("load-state") is var s && s.Bool("held") && s.Str("detail") == rows ? s : null, "the read is held after its first rows");
        Assert.True(held.Bool("loading") && held.Bool("panel") && held.Bool("ring"));
        Assert.Equal(text, held.Str("text"));
        Assert.False(held.Bool("navigation") || held.Bool("picker") || held.Bool("resources") || held.Bool("page"), "a control is usable during the read");
        Assert.False(held.Bool("canEdit") || held.Bool("canAct") || held.Bool("canInvoke") || held.Bool("canRunTask") || held.Bool("canSwitch") || held.Bool("canNavigate"));
        Assert.Equal(Other, held.Str("workspaceName"));

        // An edit, autosave, a submission and an undo change nothing and save nothing; another choice is not acted on.
        var tried = app.Query("load-try");
        Assert.Equal("fleet before", tried.Str("fleetName"));
        var state = tried["state"]!;
        Assert.Equal((before.Int("jobs"), before.Int("draftSaves"), before.Int("fullSaves")), (state.Int("jobs"), state.Int("draftSaves"), state.Int("fullSaves")));
        Assert.False(state.Bool("autosavePending") || state.Bool("dialog"));
        app.Query("workspace-pick-other");
        Assert.Equal(1, app.Query("load-state").Int("reads") - before.Int("reads"));

        app.Query("load-release");
        var after = Idle(app, Large);
        Assert.Equal(Large, after.Str("pickerName"));
        Assert.True(after.Bool("navigation") && after.Bool("picker") && after.Bool("page") && after.Bool("canEdit"));
        Assert.False(after.Bool("panel") || after.Bool("ring"));
        Assert.Equal(1, after.Int("reads") - before.Int("reads"));
        app.Close();
        Assert.Equal("fleet before", Stored(app.DataDirectory, n => n == Other).Resources[ResourceKind.Fleets].Draft[0].Get("Name"));
    }

    [WindowFact]
    public void A_read_that_fails_keeps_the_current_workspace_and_reports_the_error()
    {
        using var app = Start("load-fail", Seed);
        app.Context = () => app.Query("load-state").ToJsonString();
        var before = Idle(app, Large);
        app.Query("load-fail");
        app.Query("workspace-pick-other");
        var failed = app.Wait(() => app.Query("load-state") is var s && s.Int("reads") > before.Int("reads") && !s.Bool("loading") && s.Bool("idle") ? s : null, "the failed read ends");
        Assert.Equal((Large, Large), (failed.Str("workspaceName"), failed.Str("pickerName")));
        Assert.StartsWith("Operation did not complete: ", failed.Str("status"));
        Assert.True(failed.Bool("navigation") && failed.Bool("picker") && failed.Bool("page") && failed.Bool("canEdit"));
        Assert.False(failed.Bool("panel"));

        // The next switch works.
        app.Query("workspace-pick-other");
        Assert.NotEqual(Large, app.Wait(() => app.Query("load-state") is var s && !s.Bool("loading") && s.Bool("idle") && s.Str("workspaceName") != Large ? s : null, "the next switch").Str("workspaceName"));
        app.Close();
    }

    [WindowFact]
    public void Closing_during_a_read_waits_for_it_then_saves_and_closes()
    {
        using var app = Start("load-close", Seed);
        app.Context = () => app.Query("load-state").ToJsonString();
        Idle(app, Large);
        app.Query("workspace-pick-other");
        var other = Idle(app, Other).Str("workspaceName");
        app.Query("load-hold");
        app.Query("workspace-pick-other");
        app.Until(() => app.Query("load-state").Bool("held"), "the read is held");
        app.Query("close");
        var waiting = app.Wait(() => app.Query("load-state") is var s && s.Str("status") == "Loading the workspace. The window closes when it has loaded." ? s : null, "the close waits");
        Assert.True(waiting.Bool("loading"));
        app.Query("close");
        app.Query("load-release");
        app.WaitForExit();

        // Nothing was lost: every workspace reads back whole.
        Assert.Equal(20000, Stored(app.DataDirectory, n => n == Large).Resources[ResourceKind.Routes].Draft.Count);
        Assert.Equal("fleet before", Stored(app.DataDirectory, n => n == other).Resources[ResourceKind.Fleets].Draft[0].Get("Name"));
        using var again = app.Reopen();
        Idle(again, Large);
        again.Close();
    }

    [WindowFact]
    public void Quick_successive_choices_end_on_one_workspace_that_the_picker_names()
    {
        using var app = Start("load-quick", Seed);
        app.Context = () => app.Query("load-state").ToJsonString();
        Idle(app, Large);
        for (var n = 0; n < 6; n++)
            app.Query("workspace-pick-other");
        var settled = app.Wait(() => app.Query("load-state") is var s && !s.Bool("loading") && s.Bool("idle") && s.Str("pickerName") == s.Str("workspaceName") ? s : null, "the switches settle");
        Assert.False(settled.Bool("panel"));
        Assert.DoesNotContain("Operation did not complete", settled.Str("status") ?? "");
        Pick(app, Large);
        var back = Idle(app, Large);
        Assert.Equal(Large, back.Str("pickerName"));
        Assert.Equal(20000, app.Query("startup").Int("routes"));
        app.Close();
    }

    [WindowFact]
    public void The_window_offers_nothing_but_the_loading_panel_until_the_first_workspace_is_read()
    {
        using var app = Start("load-startup", Seed);
        var startup = app.Query("startup");
        var first = startup["atConstruction"]!;
        Assert.True(first.Bool("loading") && first.Bool("panel") && first.Bool("ring"));
        Assert.Equal("Loading the workspace…", first.Str("text"));
        Assert.False(first.Bool("navigation") || first.Bool("picker") || first.Bool("resources") || first.Bool("page") || first.Bool("canEdit") || first.Bool("canAct"));
        Assert.Equal(20000, startup.Int("routes"));
        var shown = app.Query("load-state");
        Assert.False(shown.Bool("loading") || shown.Bool("panel"));
        Assert.Equal((Large, Large, "workspace"), (shown.Str("workspaceName"), shown.Str("pickerName"), shown.Str("shellPage")));
        Assert.True(shown.Bool("navigation") && shown.Bool("picker"));
        app.Close();
    }
}
