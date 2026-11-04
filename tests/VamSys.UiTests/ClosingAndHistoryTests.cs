using VamSys.Core;
using VamSys.Infrastructure;
using VamSys.TestSupport;
using VamSys.UiTests.Driver;
using static VamSys.UiTests.Driver.AppWindow;

namespace VamSys.UiTests;

/// <summary>
/// What the actual window does with drafts when it closes, autosaves or undoes, checked again from the disk afterwards.
/// Replaces scripts/test-v3-verification-ui.ps1 (default, -Autosave and -History).
/// </summary>
public class ClosingAndHistoryTests
{
    // The stored workspace as a restarted program reads it.
    static Workspace Stored(string directory, Func<string, bool>? named = null)
    {
        var store = new WorkspaceStore(directory);
        return store.Load(store.List().First(w => named?.Invoke(w.Name) ?? true).Id);
    }

    static string? FirstFleetName(Workspace workspace) => workspace.Resources[ResourceKind.Fleets].Draft[0].Get("Name");

    [WindowFact]
    public void Closing_freezes_the_window_a_failed_save_restores_it_and_a_retry_closes_with_the_last_accepted_draft()
    {
        using var app = Start("closing", d => DataUiFixture.SeedStorage(d, future: false));
        app.Context = () => app.Query("state").ToJsonString();

        app.Query("prepare");
        app.Query("edit:accepted before close");
        app.Query("arm");
        app.Query("close");
        var state = app.Wait(() => app.Query("state") is var s && s.Bool("entered") ? s : null, "the close reaches its save");
        Assert.Equal(("Saving", false), (state.Str("state"), state.Bool("enabled")));

        // Frozen: an edit, a command and a repeated close change nothing.
        app.Query("edit:must not persist");
        app.Query("action");
        app.Query("close");
        state = app.Query("state");
        Assert.Equal(("accepted before close", 0, 1), (state.Str("name"), state.Int("actions"), state.Int("saves")));

        app.Query("fail");
        state = app.Wait(() => app.Query("state") is var s && s.Str("state") == "Open" ? s : null, "the failed save reopens the window");
        Assert.True(state.Bool("enabled"), "Failure did not restore interaction");
        app.Query("edit:accepted after failure");
        app.Query("action");
        Assert.Equal(1, app.Query("state").Int("actions"));

        app.Query("arm");
        app.Query("close");
        app.Until(() => app.Query("state").Bool("entered"), "the retried close reaches its save");
        app.Query("release");
        app.WaitForExit();

        Assert.Equal("accepted after failure", FirstFleetName(Stored(app.DataDirectory)));
    }

    [WindowFact]
    public void Autosave_writes_only_the_edited_row_and_it_survives_a_forced_exit()
    {
        using var app = Start("autosave", d => DataUiFixture.SeedStorage(d, future: false));
        app.Context = () => app.Query("state").ToJsonString();

        app.Query("prepare");
        var start = app.Query("state");
        app.Query("edit:autosaved first");
        app.Until(() => app.Query("state").Int("draftSaves") >= start.Int("draftSaves") + 1, "the first autosave");
        app.Query("edit:autosaved second");
        var second = app.Wait(() => app.Query("state") is var s && s.Int("draftSaves") >= start.Int("draftSaves") + 2 ? s : null, "the second autosave");
        Assert.Equal(start.Int("fullSaves"), second.Int("fullSaves"));

        app.Kill();

        Assert.Equal("autosaved second", FirstFleetName(Stored(app.DataDirectory)));
    }

    [WindowFact]
    public void Undo_and_redo_buttons_continue_a_cell_across_autosaves_and_survive_a_workspace_switch_and_a_restart()
    {
        const string Second = "history second";
        using var app = Start("history", d => DataUiFixture.SeedStorage(d, future: false));
        app.Context = () => app.Query("state").ToJsonString();
        void Idle() => app.Until(() => app.Query("state").Bool("idle"), "the window is idle");
        void Overflow(string command)
        {
            Idle();
            app.ClickMore();
            Idle();
            app.InvokeNamed(command);
        }
        void NameBecomes(string? name) => app.Until(() => app.Query("state").Str("name") == name, $"the first fleet is named '{name}'");

        app.Query("prepare");
        var start = app.Query("state");
        var original = start.Str("name");
        app.Query("edit:history first");
        app.Until(() => app.Query("state").Int("draftSaves") >= start.Int("draftSaves") + 1, "the first autosave");
        app.Query("edit:" + Second);
        app.Until(() => app.Query("state").Int("draftSaves") >= start.Int("draftSaves") + 2, "the second autosave");
        Assert.Equal(start.Int("undo") + 1, app.Query("state").Int("undo"));

        var beforeButtons = app.Query("state");
        Overflow("Undo");
        NameBecomes(original);
        Overflow("Redo");
        NameBecomes(Second);
        var afterButtons = app.Wait(() => app.Query("state") is var s && s.Bool("idle") && s.Int("draftSaves") >= beforeButtons.Int("draftSaves") + 2 ? s : null, "undo and redo are saved");
        Assert.Equal(beforeButtons.Int("fullSaves"), afterButtons.Int("fullSaves"));

        app.Query("workspace-away");
        app.Until(() => app.Query("state").Str("workspaceName") == "QA other", "the other workspace opens");
        app.Query("workspace-back");
        app.Until(() => app.Query("state").Str("workspaceName") == start.Str("workspaceName"), "the first workspace opens again");
        app.Query("prepare");
        Assert.Equal(Second, app.Query("state").Str("name"));
        Overflow("Undo");
        NameBecomes(original);
        Overflow("Redo");
        NameBecomes(Second);
        Idle();
        app.Close();

        // The last history step is complete after a restart.
        var fleets = Stored(app.DataDirectory, name => name != "QA other").Resources[ResourceKind.Fleets];
        Assert.Equal(Second, fleets.Draft[0].Get("Name"));
        fleets.UndoEdit();
        Assert.Equal(original, fleets.Draft[0].Get("Name"));
        fleets.RedoEdit();
        Assert.Equal(Second, fleets.Draft[0].Get("Name"));
    }
}
