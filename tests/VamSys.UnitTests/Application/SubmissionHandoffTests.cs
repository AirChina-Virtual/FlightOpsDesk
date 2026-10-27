using VamSys.Core;

namespace VamSys.UnitTests.Application;

// The window's side of the handoff: the lock, the render after unlock (which rebuilds the review and takes the lock
// again) and the availability checks the execute callback makes through RequireExecution.
public class SubmissionHandoffTests
{
    sealed class Shell
    {
        public readonly FakeStorage Storage = new();
        public readonly WorkspaceSession Session;
        public readonly SubmissionService Service;
        public readonly List<string> Events = [];
        public readonly BatchJob History;
        public bool Locked, Busy, Querying, Open = true, SkipSelection;
        public string Page = "review";
        public int Starts, ReviewWorkers;
        public Action? AfterUnlock;
        public List<ChangeItem> Reviewed = [new() { Resource = ResourceKind.Routes, Kind = ChangeKind.Delete }];

        public Shell(RunMode mode)
        {
            Storage.Seed("va", mode);
            Session = new(Storage, new InteractionGate(), new FakeTimer());
            Session.Open("default");
            Session.Workspace.Mode = mode;
            Session.Workspace.AirlineId = "7";
            History = new() { Mode = mode, Items = [new() { Resource = ResourceKind.Routes, Kind = ChangeKind.Delete }] };
            Session.Workspace.Jobs.Add(History);
            Service = new(Session);
        }

        async Task LocalOperation(Func<Task> action)
        {
            if (Locked || Busy || !Open || Querying)
                return;
            Locked = true;
            Events.Add("lock");
            try { await action(); }
            finally
            {
                Locked = false;
                Events.Add("unlock");
                // As in the window, rebuilding the review right after unlocking takes its read lock again.
                Events.Add("render:" + Page);
                if (Page == "review") { ReviewWorkers++; Locked = true; }
                AfterUnlock?.Invoke();
            }
        }

        public Task Submit() => Service.SubmitAsync(Session.Workspace.Mode, true, LocalOperation,
            () => Task.FromResult<List<ChangeItem>?>(Reviewed),
            () =>
            {
                Assert.True(Locked, "The task page must be selected before the lock is released");
                Assert.Equal(Session.Workspace.Jobs.Count, Storage.Read(Session.Workspace.Id).Jobs.Count);
                Events.Add("select:tasks");
                if (!SkipSelection) Page = "tasks";
            },
            job =>
            {
                Service.RequireExecution(job, Session.Workspace.Mode, Open && !Busy && !Locked && !Querying);
                Starts++;
                Events.Add("execute");
                Assert.Same(Reviewed, job.Items);
                return Task.CompletedTask;
            });
    }

    static string? Code(Exception e) => MessageErrors.Describe(e).Code;

    [Theory]
    [InlineData(RunMode.Online)]
    [InlineData(RunMode.Demo)]
    public async Task The_task_page_is_selected_before_unlock_and_render_and_the_saved_task_starts_once(RunMode mode)
    {
        var shell = new Shell(mode);
        await shell.Submit();
        Assert.Equal(["lock", "select:tasks", "unlock", "render:tasks", "execute"], shell.Events);
        Assert.Equal(1, shell.Starts);
        Assert.Equal(0, shell.ReviewWorkers);
        var stored = shell.Storage.Read(shell.Session.Workspace.Id).Jobs.Single(j => j.Id != shell.History.Id);
        Assert.Equal(JobStatus.Pending, stored.StatusCode);
        Assert.All(stored.Items, i => Assert.Equal(ItemState.Pending, i.State));
    }

    [Fact]
    public async Task A_review_render_that_takes_the_lock_again_rejects_the_start_and_unlocking_never_retries_it()
    {
        var shell = new Shell(RunMode.Online) { SkipSelection = true };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(shell.Submit);
        Assert.Equal("TaskStartBlocked", Code(error));
        Assert.Equal(1, shell.ReviewWorkers);
        Assert.Equal(0, shell.Starts);

        shell.Locked = false;
        Assert.Equal(0, shell.Starts);
        Assert.All(shell.Storage.Read(shell.Session.Workspace.Id).Jobs, j => Assert.All(j.Items, i => Assert.Equal(ItemState.Pending, i.State)));
    }

    [Theory]
    [InlineData("lock")]
    [InlineData("busy")]
    [InlineData("query")]
    [InlineData("closed")]
    public async Task A_window_that_became_unavailable_during_the_handoff_blocks_the_saved_task(string change)
    {
        var shell = new Shell(RunMode.Online);
        shell.AfterUnlock = () =>
        {
            if (change == "lock") shell.Locked = true;
            if (change == "busy") shell.Busy = true;
            if (change == "query") shell.Querying = true;
            if (change == "closed") shell.Open = false;
        };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(shell.Submit);
        Assert.Equal("TaskStartBlocked", Code(error));
        Assert.Equal(0, shell.Starts);
        Assert.Equal(2, shell.Storage.Read(shell.Session.Workspace.Id).Jobs.Count);
    }

    [Fact]
    public async Task A_declined_local_operation_reviews_and_submits_nothing()
    {
        var shell = new Shell(RunMode.Online) { Busy = true };
        await shell.Submit();
        Assert.Empty(shell.Events);
        Assert.Equal(0, shell.Starts);
        Assert.Single(shell.Session.Workspace.Jobs);
    }

    [Fact]
    public void Start_reasons_have_text_in_both_languages()
    {
        foreach (var language in new[] { "zh-CN", "en-US" })
        {
            var text = new LocalizationService();
            text.SetLanguage(language);
            Assert.DoesNotContain("[TaskStartBlocked]", text.Get("TaskStartBlocked"), StringComparison.Ordinal);
        }
    }
}
