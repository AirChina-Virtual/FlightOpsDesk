using VamSys.Core;

namespace VamSys.UnitTests.Application;

public class SubmissionServiceTests
{
    sealed class Fixture
    {
        public readonly FakeStorage Storage = new();
        public readonly WorkspaceSession Session;
        public readonly SubmissionService Service;
        public readonly List<string> Events = [];
        public readonly List<BatchJob> Started = [];
        public List<ChangeItem>? Reviewed = [new() { Resource = ResourceKind.Airports, Kind = ChangeKind.Create }];
        public Action? AfterSelect;
        public Fixture(RunMode mode = RunMode.Demo)
        {
            Storage.Seed("va", mode);
            Session = new(Storage, new InteractionGate(), new FakeTimer());
            Session.Open("default");
            Session.Workspace.Mode = mode;
            Session.Workspace.AirlineId = "7";
            Service = new(Session);
        }
        public RunMode Mode => Session.Workspace.Mode;
        public Task Submit(bool canEdit = true, RunMode? mode = null) => Service.SubmitAsync(mode ?? Mode, canEdit, Lock,
            () => { Events.Add("confirm"); return Task.FromResult(Reviewed); },
            () => { Events.Add("select"); AfterSelect?.Invoke(); },
            job => { Events.Add("execute"); Started.Add(job); return Task.CompletedTask; });
        async Task Lock(Func<Task> action)
        {
            Events.Add("lock");
            try { await action(); }
            finally { Events.Add("unlock"); }
        }
    }

    static string? Code(Exception e) => MessageErrors.Describe(e).Code;

    [Theory]
    [InlineData(RunMode.Online)]
    [InlineData(RunMode.Demo)]
    public async Task A_confirmed_submission_saves_a_pending_task_selects_the_task_page_then_starts_exactly_that_task(RunMode mode)
    {
        var f = new Fixture(mode);
        await f.Submit();
        Assert.Equal(["lock", "confirm", "select", "unlock", "execute"], f.Events);
        var job = Assert.Single(f.Session.Workspace.Jobs);
        Assert.Same(job, Assert.Single(f.Started));
        Assert.Equal(mode, job.Mode);
        Assert.Same(f.Reviewed, job.Items);
        Assert.Equal("journal:Jobs", f.Storage.Calls.Last());
        Assert.Single(f.Storage.Read(f.Session.Workspace.Id).Jobs);
        Assert.Equal(JobStatus.Pending, f.Storage.Read(f.Session.Workspace.Id).Jobs[0].StatusCode);
    }

    [Fact]
    public async Task Existing_pending_tasks_are_never_started_by_a_new_submission()
    {
        var f = new Fixture();
        var history = new BatchJob { Mode = RunMode.Demo, Items = [new() { Resource = ResourceKind.Routes, Kind = ChangeKind.Delete }] };
        f.Session.Workspace.Jobs.Add(history);
        await f.Submit();
        Assert.DoesNotContain(history, f.Started);
        Assert.Equal(ItemState.Pending, history.Items[0].State);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_declined_or_empty_review_creates_no_task_and_starts_nothing(bool empty)
    {
        var f = new Fixture { Reviewed = empty ? [] : null };
        await f.Submit();
        Assert.Empty(f.Session.Workspace.Jobs);
        Assert.Empty(f.Started);
        Assert.DoesNotContain("select", f.Events);
        Assert.DoesNotContain(f.Storage.Calls, c => c == "journal:Jobs");
    }

    [Fact]
    public async Task A_window_that_may_not_be_edited_or_a_workspace_in_another_mode_does_nothing_at_all()
    {
        var f = new Fixture();
        await f.Submit(canEdit: false);
        await f.Submit(mode: RunMode.Online);
        Assert.Empty(f.Events);
        Assert.Empty(f.Session.Workspace.Jobs);
    }

    [Fact]
    public async Task A_failed_save_removes_the_unsaved_task_from_memory_and_starts_nothing()
    {
        var f = new Fixture();
        f.Storage.FailSaves = true;
        await Assert.ThrowsAsync<IOException>(() => f.Submit());
        Assert.Empty(f.Session.Workspace.Jobs);
        Assert.Empty(f.Started);
        Assert.DoesNotContain("select", f.Events);
        Assert.Empty(f.Storage.Read(f.Session.Workspace.Id).Jobs);
        // A later save must not write the failed submission.
        f.Storage.FailSaves = false;
        await f.Session.SaveAsync();
        Assert.Empty(f.Storage.Read(f.Session.Workspace.Id).Jobs);
    }

    [Fact]
    public async Task A_save_that_committed_before_failing_keeps_the_durable_task_but_does_not_start_it()
    {
        var f = new Fixture();
        f.Storage.BeforeSave = () =>
        {
            f.Storage.Stored[f.Session.Workspace.Id] = f.Session.Workspace.Serialize();
            throw new IOException("failed after commit");
        };
        await Assert.ThrowsAsync<IOException>(() => f.Submit());
        Assert.Single(f.Session.Workspace.Jobs);
        Assert.Empty(f.Started);
        Assert.DoesNotContain("select", f.Events);
    }

    [Fact]
    public async Task Unreadable_storage_keeps_the_task_and_blocks_further_saves()
    {
        var f = new Fixture();
        f.Storage.BeforeSave = () => throw new IOException("failed");
        f.Storage.FailJobExists = true;
        await Assert.ThrowsAsync<IOException>(() => f.Submit());
        Assert.Single(f.Session.Workspace.Jobs);
        Assert.True(f.Session.Gate.StorageBlocked);
    }

    [Theory]
    [InlineData("instance")]
    [InlineData("airline")]
    [InlineData("mode")]
    public async Task A_change_of_workspace_airline_or_mode_during_the_handoff_blocks_the_start(string change)
    {
        var f = new Fixture(RunMode.Online);
        f.AfterSelect = () =>
        {
            if (change == "airline") f.Session.Workspace.AirlineId = "8";
            if (change == "mode") f.Session.Workspace.Mode = RunMode.Offline;
            if (change == "instance") f.Session.ReloadAfterCheckpointFailure();
        };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => f.Submit());
        Assert.Equal("TaskStartBlocked", Code(error));
        Assert.Empty(f.Started);
    }

    [Fact]
    public async Task Starting_a_stored_task_needs_the_window_the_mode_and_the_task_to_belong_together()
    {
        var f = new Fixture(RunMode.Online);
        var job = new BatchJob { Mode = RunMode.Online };
        f.Session.Workspace.Jobs.Add(job);
        f.Service.RequireExecution(job, RunMode.Online, true);
        foreach (var (candidate, mode, available) in new[]
        {
            (job, RunMode.Online, false),
            (new BatchJob { Mode = RunMode.Online }, RunMode.Online, true),
            (job, RunMode.Demo, true),
            (new BatchJob { Mode = RunMode.Demo }, RunMode.Online, true)
        })
            Assert.Equal("TaskStartBlocked", Code(Assert.Throws<InvalidOperationException>(() => f.Service.RequireExecution(candidate, mode, available))));
        await Task.CompletedTask;
    }
}
