using VamSys.Core;

namespace VamSys.UnitTests.Application;

public class WorkspaceSessionTests
{
    sealed class Fixture
    {
        public readonly FakeStorage Storage = new();
        public readonly InteractionGate Gate = new();
        public readonly FakeTimer Timer = new();
        public readonly WorkspaceSession Session;
        public int Invalidations;
        public Fixture(RunMode mode = RunMode.Offline)
        {
            Storage.Seed("first", mode);
            Session = new(Storage, Gate, Timer);
            Session.DataInvalidated += () => Invalidations++;
            Session.Open("default");
        }
        public Workspace Stored => Storage.Read(Session.Workspace.Id);
    }

    static DataRow Row(string name) => new() { Fields = new Dictionary<string, string> { ["Name"] = name } };

    static string? Code(Exception e) => MessageErrors.Describe(e).Code;

    [Fact]
    public void Opening_an_empty_database_creates_the_default_workspace_and_a_stored_online_one_opens_offline()
    {
        var empty = new FakeStorage();
        var created = new WorkspaceSession(empty, new InteractionGate(), new FakeTimer());
        created.Open("Default");
        Assert.Equal("Default", created.Workspace.Name);
        Assert.Single(empty.Stored);

        var fixture = new Fixture(RunMode.Online);
        Assert.Equal(RunMode.Offline, fixture.Session.Workspace.Mode);
        Assert.Equal("first", fixture.Session.Workspace.Name);
    }

    [Fact]
    public async Task A_save_writes_the_journal_with_the_given_scope_and_only_row_changing_saves_invalidate_views()
    {
        var f = new Fixture();
        f.Session.Workspace.Name = "renamed";
        await f.Session.SaveAsync();
        Assert.Equal(1, f.Invalidations);
        Assert.Equal("journal:All", f.Storage.Calls.Last());
        Assert.Equal("renamed", f.Stored.Name);

        await f.Session.SaveHeaderAsync();
        await f.Session.SaveJobsAsync();
        Assert.Equal(1, f.Invalidations);
        Assert.Equal(["journal:Header", "journal:Jobs"], f.Storage.Calls.TakeLast(2));
    }

    [Fact]
    public async Task A_failed_save_keeps_memory_as_it_was_and_makes_the_next_save_write_everything()
    {
        var f = new Fixture();
        var w = f.Session.Workspace;
        var row = Row("edited");
        w.Resources[ResourceKind.Airports].Draft.Add(row);
        f.Session.Edits.Cell(w, ResourceKind.Airports, row.LocalId);
        f.Storage.FailSaves = true;
        await Assert.ThrowsAsync<IOException>(() => f.Session.SaveAsync());
        Assert.Same(w, f.Session.Workspace);
        Assert.Single(w.Resources[ResourceKind.Airports].Draft);
        Assert.Empty(f.Stored.Resources[ResourceKind.Airports].Draft);
        Assert.True(f.Session.Edits.RequiresFullSave);
        Assert.False(f.Session.Edits.CanSaveRows(w));

        f.Storage.FailSaves = false;
        await f.Session.SaveEditsAsync();
        Assert.Equal("journal:All", f.Storage.Calls.Last());
        Assert.Single(f.Stored.Resources[ResourceKind.Airports].Draft);
    }

    [Fact]
    public async Task A_narrower_scope_leaves_the_recorded_cell_edits_for_later()
    {
        var f = new Fixture();
        var w = f.Session.Workspace;
        var row = Row("a");
        w.Resources[ResourceKind.Airports].Draft.Add(row);
        f.Session.Edits.Cell(w, ResourceKind.Airports, row.LocalId);
        await f.Session.SaveJobsAsync();
        Assert.True(f.Session.Edits.CanSaveRows(w));
        f.Storage.FailSaves = true;
        await Assert.ThrowsAsync<IOException>(() => f.Session.SaveHeaderAsync());
        Assert.True(f.Session.Edits.CanSaveRows(w));
    }

    [Fact]
    public async Task Autosave_writes_the_edited_rows_and_falls_back_to_the_journal_when_rows_cannot_be_saved()
    {
        var f = new Fixture();
        var w = f.Session.Workspace;
        var row = Row("a");
        w.Resources[ResourceKind.Airports].Draft.Add(row);
        f.Session.RecordCellEdit(w, ResourceKind.Airports, row.LocalId);
        Assert.True(f.Timer.IsEnabled);
        await f.Session.AutosaveElapsedAsync();
        Assert.False(f.Timer.IsEnabled);
        Assert.Equal("rows", f.Storage.Calls.Last());
        var saved = Assert.Single(f.Storage.RowSaves.Single());
        Assert.Equal(ResourceKind.Airports, saved.Resource);
        Assert.Equal([row.LocalId], saved.Rows);

        f.Storage.RowSaveAccepted = false;
        f.Session.RecordCellEdit(w, ResourceKind.Airports, row.LocalId);
        await f.Session.SaveEditsAsync();
        Assert.Equal(["rows", "journal:All"], f.Storage.Calls.TakeLast(2));
    }

    [Fact]
    public async Task A_failed_autosave_restores_the_edits_and_does_not_touch_the_workspace()
    {
        var f = new Fixture();
        var w = f.Session.Workspace;
        var row = Row("a");
        w.Resources[ResourceKind.Airports].Draft.Add(row);
        f.Session.RecordCellEdit(w, ResourceKind.Airports, row.LocalId);
        f.Storage.FailSaves = true;
        await Assert.ThrowsAsync<IOException>(() => f.Session.AutosaveElapsedAsync());
        Assert.Contains(row, w.Resources[ResourceKind.Airports].Draft);
        Assert.True(f.Session.Edits.RequiresFullSave);
        f.Storage.FailSaves = false;
        await f.Session.SaveEditsAsync();
        Assert.Equal("journal:All", f.Storage.Calls.Last());
    }

    [Fact]
    public void Scheduling_restarts_the_timer_unless_the_window_is_closing_and_pending_saves_can_be_taken()
    {
        var f = new Fixture();
        f.Session.ScheduleAutosave();
        f.Session.ScheduleAutosave();
        Assert.Equal(2, f.Timer.Starts);
        Assert.True(f.Session.TakeAutosavePending());
        Assert.False(f.Session.TakeAutosavePending());

        var closing = new TaskCompletionSource();
        _ = f.Gate.Closing.RequestAsync(false, () => closing.Task, _ => { });
        var starts = f.Timer.Starts;
        f.Session.ScheduleAutosave();
        Assert.Equal(starts, f.Timer.Starts);
        closing.SetResult();
    }

    [Fact]
    public async Task Flushing_saves_the_edits_only_when_the_timer_was_waiting()
    {
        var f = new Fixture();
        var w = f.Session.Workspace;
        var row = Row("a");
        w.Resources[ResourceKind.Airports].Draft.Add(row);
        await f.Session.FlushAutosaveAsync();
        Assert.DoesNotContain("rows", f.Storage.Calls);
        f.Session.RecordCellEdit(w, ResourceKind.Airports, row.LocalId);
        await f.Session.FlushAutosaveAsync();
        Assert.Contains("rows", f.Storage.Calls);
        Assert.False(f.Timer.IsEnabled);
    }

    [Fact]
    public async Task Replaced_rows_are_saved_at_once_with_their_history_step()
    {
        var f = new Fixture();
        var w = f.Session.Workspace;
        var row = Row("a");
        w.Resources[ResourceKind.Airports].Draft.Add(row);
        f.Session.ScheduleAutosave();
        await f.Session.SaveReplacedRowsAsync(ResourceKind.Airports, [row.LocalId]);
        Assert.False(f.Timer.IsEnabled);
        Assert.Equal("rows", f.Storage.Calls.Last());
        Assert.Equal([row.LocalId], f.Storage.RowSaves.Single().Single().Rows);
    }

    [Fact]
    public async Task Blocked_storage_refuses_every_save_and_keeps_the_pending_edits()
    {
        var f = new Fixture();
        var w = f.Session.Workspace;
        var row = Row("a");
        w.Resources[ResourceKind.Airports].Draft.Add(row);
        f.Session.Edits.Cell(w, ResourceKind.Airports, row.LocalId);
        f.Gate.BlockStorage();
        var before = f.Storage.Calls.Count;
        Assert.Equal("StorageReload", Code(await Assert.ThrowsAsync<InvalidOperationException>(() => f.Session.SaveAsync())));
        Assert.Equal("StorageReload", Code(await Assert.ThrowsAsync<InvalidOperationException>(() => f.Session.SaveEditsAsync())));
        Assert.Equal("StorageReload", Code(await Assert.ThrowsAsync<InvalidOperationException>(() => f.Session.CheckpointWriter().WriteAsync(new(CheckpointKind.Task, new BatchJob()), CancellationToken.None))));
        Assert.Equal(before, f.Storage.Calls.Count);
        Assert.True(f.Session.Edits.RequiresFullSave);
        await f.Session.SaveForCloseAsync();
        Assert.Equal(before, f.Storage.Calls.Count);
    }

    [Fact]
    public async Task Closing_saves_everything_once()
    {
        var f = new Fixture();
        f.Session.Workspace.Name = "closing";
        await f.Session.SaveForCloseAsync();
        Assert.Equal("closing", f.Stored.Name);
    }

    [Fact]
    public async Task Saves_and_credential_writes_run_one_at_a_time()
    {
        var f = new Fixture();
        var release = new TaskCompletionSource();
        var entered = new TaskCompletionSource();
        f.Storage.BeforeSave = async () => { entered.TrySetResult(); await release.Task; };
        var first = f.Session.SaveAsync();
        await entered.Task;
        var second = f.Session.SaveConnectionSettingsAsync("client", "secret", "https://example.test/", true);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(second.IsCompleted);
        Assert.DoesNotContain("settings", f.Storage.Calls);
        release.SetResult();
        await first;
        await second;
        Assert.Equal(["journal:All", "settings"], f.Storage.Calls.TakeLast(2));
        Assert.Equal("client", f.Session.Workspace.ClientId);
        await f.Session.UpdateCredentialsAsync("other", "again");
        Assert.Equal(("other", "again"), (f.Storage.Credentials[^1].Client, f.Storage.Credentials[^1].Secret));
    }

    [Fact]
    public async Task Switching_saves_the_current_workspace_then_opens_the_other_offline()
    {
        var f = new Fixture();
        var other = f.Storage.Seed("second", RunMode.Online);
        var firstId = f.Session.Workspace.Id;
        f.Session.Workspace.Name = "edited first";
        f.Session.ScheduleAutosave();
        await f.Session.SwitchWorkspaceAsync(other.Id);
        Assert.False(f.Timer.IsEnabled);
        Assert.Equal("edited first", f.Storage.Read(firstId).Name);
        Assert.Equal(other.Id, f.Session.Workspace.Id);
        Assert.Equal(RunMode.Offline, f.Session.Workspace.Mode);
    }

    [Fact]
    public async Task A_switch_whose_save_fails_stays_on_the_current_workspace()
    {
        var f = new Fixture();
        var other = f.Storage.Seed("second");
        var current = f.Session.Workspace;
        f.Storage.FailSaves = true;
        await Assert.ThrowsAsync<IOException>(() => f.Session.SwitchWorkspaceAsync(other.Id));
        Assert.Same(current, f.Session.Workspace);
        Assert.DoesNotContain("load", f.Storage.Calls.Skip(1));
    }

    [Fact]
    public async Task A_new_workspace_is_saved_after_the_current_one_and_a_failed_first_save_creates_nothing()
    {
        var f = new Fixture();
        var created = new Workspace { Name = "created" };
        await f.Session.ReplaceWithNewAsync(created);
        Assert.Same(created, f.Session.Workspace);
        Assert.Equal("created", f.Storage.Read(created.Id).Name);

        var current = f.Session.Workspace;
        f.Storage.FailSaves = true;
        await Assert.ThrowsAsync<IOException>(() => f.Session.ReplaceWithNewAsync(new Workspace { Name = "never" }));
        Assert.Same(current, f.Session.Workspace);
        Assert.DoesNotContain(f.Storage.List(), l => l.Name == "never");
    }

    [Fact]
    public void After_a_checkpoint_failure_the_database_is_reloaded_and_when_that_fails_saving_stops()
    {
        var f = new Fixture();
        f.Session.Workspace.Mode = RunMode.Online;
        f.Session.ScheduleAutosave();
        var before = f.Session.Workspace;
        f.Session.ReloadAfterCheckpointFailure();
        Assert.NotSame(before, f.Session.Workspace);
        Assert.Equal(RunMode.Offline, f.Session.Workspace.Mode);
        Assert.False(f.Timer.IsEnabled);
        Assert.False(f.Gate.StorageBlocked);

        var current = f.Session.Workspace;
        f.Storage.FailLoad = () => new IOException("unreadable");
        f.Session.ReloadAfterCheckpointFailure();
        Assert.Same(current, f.Session.Workspace);
        Assert.True(f.Gate.StorageBlocked);
    }

    [Fact]
    public void A_job_is_durable_when_storage_says_so_and_an_unreadable_database_blocks_storage_but_keeps_the_job()
    {
        var f = new Fixture();
        var job = new BatchJob();
        Assert.False(f.Session.SavedJobIsDurable(job));
        f.Session.Workspace.Jobs.Add(job);
        f.Storage.Stored[f.Session.Workspace.Id] = f.Session.Workspace.Serialize();
        Assert.True(f.Session.SavedJobIsDurable(job));
        Assert.Same(job, f.Session.LoadJob(job.Id) is null ? null : job);

        f.Storage.FailJobExists = true;
        Assert.True(f.Session.SavedJobIsDurable(new BatchJob()));
        Assert.True(f.Gate.StorageBlocked);
    }

    [Fact]
    public async Task Checkpoints_pass_through_when_nothing_else_is_saving()
    {
        var f = new Fixture();
        var job = new BatchJob();
        await f.Session.CheckpointWriter().WriteAsync(new(CheckpointKind.Task, job), CancellationToken.None);
        Assert.Same(job, f.Storage.Checkpoints.Single().Job);
    }
}
