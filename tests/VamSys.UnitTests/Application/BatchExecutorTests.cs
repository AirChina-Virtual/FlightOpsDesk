using VamSys.Core;
using VamSys.Infrastructure;

namespace VamSys.UnitTests.Application;

/// <summary>
/// The generic executor driven by the in-memory demo server: every write is counted, so "no repeat" and "no blind retry"
/// are asserted on the number of writes the server actually received.
/// </summary>
public class BatchExecutorTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static DataRow Row(params (string, string)[] fields) => new() { Fields = fields.ToDictionary(p => p.Item1, p => p.Item2) };

    sealed class CountingService(DemoService inner) : IResourceReader, IResourceWriter
    {
        public int Writes;
        public bool TimeoutWrites, FailOnce;
        public IAsyncEnumerable<DataRow> ReadAllAsync(ResourceKind k, CancellationToken ct) => inner.ReadAllAsync(k, ct);
        public Task<DataRow?> FindAsync(ResourceKind k, string id, CancellationToken ct) => inner.FindAsync(k, id, ct);
        public Task<string?> WriteAsync(ChangeItem c, CancellationToken ct)
        {
            Writes++;
            if (FailOnce) { FailOnce = false; throw new FormatException("invalid"); }
            if (TimeoutWrites) throw new TimeoutException();
            return inner.WriteAsync(c, ct);
        }
    }

    sealed class AccessDeniedService : IResourceReader, IResourceWriter
    {
        public int Calls;
        public async IAsyncEnumerable<DataRow> ReadAllAsync(ResourceKind k, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct) { await Task.CompletedTask; yield break; }
        public Task<DataRow?> FindAsync(ResourceKind k, string id, CancellationToken ct) => throw new ApiAccessException("denied");
        public Task<string?> WriteAsync(ChangeItem c, CancellationToken ct) { Calls++; throw new ApiAccessException("denied"); }
    }

    static Task Run(CountingService s, BatchJob job, CancellationToken ct, Func<Task>? persist = null)
        => new BatchExecutor(s, s).ExecuteAsync(job, persist ?? (() => Task.CompletedTask), ct);

    [Fact]
    public async Task A_successful_batch_writes_once_and_running_the_finished_job_again_sends_nothing()
    {
        var w = new Workspace();
        CsvAdapter.Import(ResourceKind.Airports, w.Resources[ResourceKind.Airports], [Row(("ICAO/IATA", "ZBAA"), ("Name", "old"))], true);
        var draft = w.Resources[ResourceKind.Airports].Draft;
        draft[0] = draft[0].With("Name", "new");
        var server = new CountingService(new DemoService(w));
        var job = new BatchJob { Items = new ChangePlanner().Plan(ResourceKind.Airports, w.Resources[ResourceKind.Airports]) };

        await Run(server, job, Ct);
        await Run(server, job, Ct);

        Assert.Equal(1, server.Writes);
        Assert.Equal(ItemState.Succeeded, job.Items[0].State);
        Assert.Equal(JobStatus.Completed, job.StatusCode);
    }

    [Fact]
    public async Task A_timed_out_create_stays_unknown_and_running_again_does_not_resend_it()
    {
        var server = new CountingService(new DemoService(new Workspace())) { TimeoutWrites = true };
        var item = new ChangeItem { Resource = ResourceKind.Fleets, Kind = ChangeKind.Create, After = Row(("Name", "new")) };
        var job = new BatchJob { Items = [item] };

        await Run(server, job, Ct);
        await Run(server, job, Ct);

        Assert.Equal(ItemState.Unknown, item.State);
        Assert.Equal(1, server.Writes);
        Assert.Equal(JobStatus.NeedsAttention, job.StatusCode);
    }

    [Fact]
    public async Task A_remote_change_since_planning_marks_a_conflict_and_nothing_is_written()
    {
        var w = new Workspace();
        CsvAdapter.Import(ResourceKind.Airports, w.Resources[ResourceKind.Airports], [Row(("ICAO/IATA", "ZBAA"), ("Name", "remote"))], true);
        var server = new CountingService(new DemoService(w));
        var item = new ChangeItem
        {
            Resource = ResourceKind.Airports, Kind = ChangeKind.Update,
            Before = Row(("ICAO/IATA", "ZBAA"), ("Name", "old")), After = Row(("ICAO/IATA", "ZBAA"), ("Name", "new"))
        };

        await Run(server, new BatchJob { Items = [item] }, Ct);

        Assert.Equal(ItemState.Conflict, item.State);
        Assert.Equal(0, server.Writes);
    }

    [Fact]
    public async Task A_dependent_create_is_written_with_the_ID_the_server_returned_for_its_parent()
    {
        var w = new Workspace();
        var fleet = Row(("Name", "New fleet"));
        var plane = Row(("Name", "New plane"), ("Fleet ID", "local:" + fleet.LocalId));
        w.Resources[ResourceKind.Fleets].Draft.Add(fleet);
        w.Resources[ResourceKind.Aircraft].Draft.Add(plane);
        var job = new BatchJob { Items = new ChangePlanner().PlanWorkspace(w) };
        var server = new CountingService(new DemoService(w));

        await Run(server, job, Ct);

        Assert.All(job.Items, i => Assert.Equal(ItemState.Succeeded, i.State));
        var parent = job.Items.Single(i => i.Resource == ResourceKind.Fleets);
        var child = job.Items.Single(i => i.Resource == ResourceKind.Aircraft);
        Assert.False(string.IsNullOrEmpty(parent.RemoteId));
        Assert.Equal(parent.RemoteId, child.After.Get("Fleet ID"));
        Assert.Equal(2, server.Writes);
    }

    [Fact]
    public async Task A_failing_progress_save_surfaces_as_a_persistence_error_and_no_write_is_sent()
    {
        var server = new CountingService(new DemoService(new Workspace()));
        var job = new BatchJob { Items = [new ChangeItem { Resource = ResourceKind.Fleets, Kind = ChangeKind.Create, After = Row(("Name", "x")) }] };

        await Assert.ThrowsAsync<PersistenceException>(() => Run(server, job, Ct, () => throw new IOException("disk")));

        Assert.Equal(0, server.Writes);
    }

    [Fact]
    public async Task A_failed_parent_skips_its_child_and_a_cancelled_run_leaves_pending_items_untouched()
    {
        var parent = new ChangeItem { Resource = ResourceKind.Fleets, State = ItemState.Failed };
        var child = new ChangeItem { Resource = ResourceKind.Aircraft, Dependencies = [parent.Id] };
        var server = new CountingService(new DemoService(new Workspace()));

        await Run(server, new BatchJob { Items = [parent, child] }, Ct);

        Assert.Equal(ItemState.Skipped, child.State);
        Assert.Equal(0, server.Writes);

        var pending = new ChangeItem { Resource = ResourceKind.Fleets };
        var cancelled = new BatchJob { Items = [pending] };
        await Run(server, cancelled, new CancellationToken(true));

        Assert.Equal(ItemState.Pending, pending.State);
        Assert.Equal(JobStatus.Canceled, cancelled.StatusCode);
        Assert.Equal(0, server.Writes);
    }

    [Fact]
    public async Task Access_denied_pauses_the_batch_and_the_later_rows_are_never_sent()
    {
        var denied = new AccessDeniedService();
        var job = new BatchJob { Items = [new ChangeItem { Resource = ResourceKind.Fleets }, new ChangeItem { Resource = ResourceKind.Fleets }] };

        await new BatchExecutor(denied, denied).ExecuteAsync(job, () => Task.CompletedTask, Ct);

        Assert.Equal(1, denied.Calls);
        Assert.Equal(JobStatus.Paused, job.StatusCode);
    }

    [Fact]
    public async Task A_create_recovered_with_its_stored_remote_ID_is_verified_by_reading_and_not_written_again()
    {
        var w = new Workspace();
        var after = Row(("ID", ""), ("Name", "new"));
        var item = new ChangeItem { Resource = ResourceKind.Fleets, Kind = ChangeKind.Create, After = after, State = ItemState.Running, RemoteId = "123" };
        w.Resources[ResourceKind.Fleets].Snapshot.Add(after.With("ID", "123"));
        var server = new CountingService(new DemoService(w));

        await Run(server, new BatchJob { Items = [item] }, Ct);

        Assert.Equal(ItemState.Succeeded, item.State);
        Assert.Equal(0, server.Writes);
    }

    [Fact]
    public async Task A_failure_lets_independent_rows_finish_and_after_repair_only_the_skipped_child_is_sent()
    {
        var server = new CountingService(new DemoService(new Workspace())) { FailOnce = true };
        var parent = new ChangeItem { Resource = ResourceKind.Fleets, Kind = ChangeKind.Create, After = Row(("Name", "parent")) };
        var child = new ChangeItem { Resource = ResourceKind.Aircraft, Kind = ChangeKind.Create, After = Row(("Name", "child")), Dependencies = [parent.Id] };
        var independent = new ChangeItem { Resource = ResourceKind.Routes, Kind = ChangeKind.Create, After = Row(("Name", "independent")) };
        var job = new BatchJob { Items = [parent, child, independent] };

        await Run(server, job, Ct);

        Assert.Equal(ItemState.Failed, parent.State);
        Assert.Equal(ItemState.Skipped, child.State);
        Assert.Equal(ItemState.Succeeded, independent.State);
        Assert.Equal(2, server.Writes); // the failed parent and the independent row; the child was never sent

        parent.State = ItemState.Pending;
        await Run(server, job, Ct);

        Assert.All(job.Items, i => Assert.Equal(ItemState.Succeeded, i.State));
        Assert.Equal(4, server.Writes); // the repaired parent and its child; the independent row is not sent again
    }

    [Fact]
    public async Task Switching_language_during_execution_neither_resends_nor_changes_the_task_and_the_texts_follow_the_language()
    {
        var job = new BatchJob { Items = [new ChangeItem { Resource = ResourceKind.Fleets, Kind = ChangeKind.Create, After = Row(("Name", "fleet")) }] };
        var loc = new LocalizationService();
        var server = new CountingService(new DemoService(new Workspace()));

        await new BatchExecutor(server, server).ExecuteAsync(job,
            () => { loc.SetLanguage(loc.Language == "en-US" ? "zh-CN" : "en-US"); return Task.CompletedTask; }, Ct);

        Assert.Equal(1, server.Writes);
        Assert.Equal(JobStatus.Completed, job.StatusCode);
        Assert.Equal("Text_140197D868", job.Description?.Code);

        var restored = Workspace.Deserialize(new Workspace { Jobs = [job] }.Serialize()).Jobs.Single();
        loc.SetLanguage("en-US");
        Assert.Equal("Completed and verified", loc.Format(restored.Description!));
        Assert.Equal("Verified by read-back", loc.Format(restored.Items[0].Description!));

        await Run(server, restored, Ct);

        Assert.Equal(1, server.Writes);
    }
}


