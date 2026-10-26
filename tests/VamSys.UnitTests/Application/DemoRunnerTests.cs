using VamSys.Core;
using VamSys.Infrastructure;

namespace VamSys.UnitTests.Application;

public class DemoRunnerTests
{
    sealed class Fixture
    {
        public readonly FakeStorage Storage = new();
        public readonly WorkspaceSession Session;
        public readonly DemoRunner Runner;
        public readonly List<string> Events = [];
        public Fixture()
        {
            var demo = DemoWorkspaceFactory.Create("demo");
            Storage.Stored[demo.Id] = demo.Serialize();
            Session = new(Storage, new InteractionGate(), new FakeTimer());
            Session.Open("default");
            Runner = new(Session, (workspace, jobs) =>
            {
                var service = new DemoService(workspace);
                foreach (var history in jobs)
                    service.RestoreSuccessful(history);
                return (service, service);
            });
        }
        public Workspace W => Session.Workspace;
        public BatchJob NewJob()
        {
            var job = new BatchJob { Mode = RunMode.Demo, Items = new ChangePlanner().PlanWorkspace(W) };
            W.Jobs.Add(job);
            return job;
        }
        public Task Run(BatchJob job, CancellationToken ct = default) => Runner.RunAsync(job, ct, () => Events.Add("before"), item => Events.Add("progress"));
    }

    [Fact]
    public void The_demo_workspace_has_the_sample_records_already_on_the_server()
    {
        var w = DemoWorkspaceFactory.Create("demo 12:00");
        Assert.Equal("demo 12:00", w.Name);
        Assert.Equal(RunMode.Demo, w.Mode);
        Assert.Equal(2, w.Resources[ResourceKind.Airports].Draft.Count);
        Assert.Equal(2, w.Resources[ResourceKind.Fleets].Draft.Count);
        Assert.Single(w.Resources[ResourceKind.Aircraft].Draft);
        Assert.Single(w.Resources[ResourceKind.Routings].Draft);
        Assert.Equal(12, w.Resources[ResourceKind.Routes].Draft.Count);
        foreach (var data in w.Resources.Values)
        {
            Assert.Equal(data.Draft.Count, data.Snapshot.Count);
            Assert.Empty(data.Conflicts);
        }
        Assert.Empty(new ChangePlanner().PlanWorkspace(w));
    }

    [Fact]
    public async Task A_successful_run_rebases_the_changed_rows_onto_the_snapshot_and_saves()
    {
        var f = new Fixture();
        var routes = f.W.Resources[ResourceKind.Routes];
        var row = routes.Draft[0];
        routes.ReplaceDraftRows([row.With("Callsign", "CHANGED")]);
        var job = f.NewJob();
        Assert.Single(job.Items);
        await f.Run(job);
        Assert.Equal(JobStatus.Completed, job.StatusCode);
        Assert.All(job.Items, i => { Assert.Equal(ItemState.Succeeded, i.State); Assert.True(i.Rebased); });
        Assert.Equal("CHANGED", routes.Snapshot.Single(r => r.LocalId == row.LocalId).Get("Callsign"));
        Assert.Equal("CHANGED", routes.FindDraftRow(row.LocalId)!.Get("Callsign"));
        Assert.Empty(new ChangePlanner().PlanWorkspace(f.W));
        Assert.Equal("journal:All", f.Storage.Calls.Last());
        Assert.Contains("journal:Jobs", f.Storage.Calls);
        Assert.Equal(JobStatus.Completed, f.Storage.Read(f.W.Id).Jobs.Single().StatusCode);
    }

    [Fact]
    public async Task The_window_is_prepared_once_before_the_first_item_and_told_after_every_saved_step()
    {
        var f = new Fixture();
        f.W.Resources[ResourceKind.Routes].ReplaceDraftRows([f.W.Resources[ResourceKind.Routes].Draft[0].With("Callsign", "X")]);
        f.W.Resources[ResourceKind.Routes].ReplaceDraftRows([f.W.Resources[ResourceKind.Routes].Draft[1].With("Callsign", "Y")]);
        await f.Run(f.NewJob());
        Assert.Equal("before", f.Events[0]);
        Assert.Equal(1, f.Events.Count(e => e == "before"));
        Assert.True(f.Events.Count(e => e == "progress") >= 2);
    }

    [Fact]
    public async Task A_new_record_gets_the_identity_the_simulated_server_assigned_in_snapshot_and_draft()
    {
        var f = new Fixture();
        var airports = f.W.Resources[ResourceKind.Airports];
        var created = new DataRow { Fields = new Dictionary<string, string> { ["ICAO/IATA"] = "EGLL", ["Name"] = "London", ["_delete"] = "FALSE" } };
        airports.Draft.Add(created);
        var job = f.NewJob();
        await f.Run(job);
        var item = Assert.Single(job.Items);
        Assert.Equal(ItemState.Succeeded, item.State);
        Assert.NotNull(item.RemoteId);
        Assert.Contains(airports.Snapshot, r => r.LocalId == created.LocalId);
        Assert.NotNull(airports.FindDraftRow(created.LocalId));
    }

    [Fact]
    public async Task A_deleted_record_leaves_the_snapshot_and_the_draft()
    {
        var f = new Fixture();
        var routes = f.W.Resources[ResourceKind.Routes];
        var row = routes.Draft[0];
        routes.ReplaceDraftRows([row.With("_delete", "TRUE")]);
        var job = f.NewJob();
        await f.Run(job);
        Assert.Equal(ItemState.Succeeded, Assert.Single(job.Items).State);
        Assert.DoesNotContain(routes.Snapshot, r => r.LocalId == row.LocalId);
        Assert.Null(routes.FindDraftRow(row.LocalId));
    }

    [Fact]
    public async Task Edits_made_after_the_task_was_planned_stay_in_the_draft()
    {
        var f = new Fixture();
        var routes = f.W.Resources[ResourceKind.Routes];
        var row = routes.Draft[0];
        routes.ReplaceDraftRows([row.With("Callsign", "PLANNED")]);
        var job = f.NewJob();
        routes.ReplaceDraftRows([routes.Draft[0].With("Callsign", "LATER")]);
        await f.Run(job);
        Assert.Equal("PLANNED", routes.Snapshot.Single(r => r.LocalId == row.LocalId).Get("Callsign"));
        Assert.Equal("LATER", routes.FindDraftRow(row.LocalId)!.Get("Callsign"));
    }

    [Fact]
    public async Task A_cancelled_run_stops_and_changes_neither_snapshot_nor_draft()
    {
        var f = new Fixture();
        var routes = f.W.Resources[ResourceKind.Routes];
        var row = routes.Draft[0];
        routes.ReplaceDraftRows([row.With("Callsign", "CHANGED")]);
        var original = routes.Snapshot.Single(r => r.LocalId == row.LocalId).Get("Callsign");
        var job = f.NewJob();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await f.Run(job, cancellation.Token);
        Assert.Equal(JobStatus.Canceled, job.StatusCode);
        Assert.Equal(original, routes.Snapshot.Single(r => r.LocalId == row.LocalId).Get("Callsign"));
        Assert.All(job.Items, i => Assert.False(i.Rebased));
    }

    [Fact]
    public async Task A_failed_task_save_stops_the_run_before_any_row_is_rebased()
    {
        var f = new Fixture();
        var routes = f.W.Resources[ResourceKind.Routes];
        var row = routes.Draft[0];
        routes.ReplaceDraftRows([row.With("Callsign", "CHANGED")]);
        var original = routes.Snapshot.Single(r => r.LocalId == row.LocalId).Get("Callsign");
        var job = f.NewJob();
        f.Storage.FailSaves = true;
        await Assert.ThrowsAsync<PersistenceException>(() => f.Run(job));
        Assert.Equal(original, routes.Snapshot.Single(r => r.LocalId == row.LocalId).Get("Callsign"));
        Assert.All(job.Items, i => Assert.False(i.Rebased));
    }

    [Fact]
    public async Task The_simulated_server_is_told_about_every_demo_task_of_the_workspace_and_no_online_task()
    {
        var f = new Fixture();
        var seen = new List<IReadOnlyList<BatchJob>>();
        var runner = new DemoRunner(f.Session, (workspace, jobs) =>
        {
            seen.Add(jobs);
            var service = new DemoService(workspace);
            return (service, service);
        });
        var earlier = new BatchJob { Mode = RunMode.Demo };
        var online = new BatchJob { Mode = RunMode.Online };
        f.W.Jobs.AddRange([earlier, online]);
        var job = f.NewJob();
        await runner.RunAsync(job, CancellationToken.None, () => { }, _ => { });
        Assert.Equal([earlier, job], Assert.Single(seen));
    }

    [Fact]
    public async Task Failed_items_go_back_to_pending_and_only_the_task_is_saved()
    {
        var f = new Fixture();
        var job = new BatchJob { Mode = RunMode.Demo, Items = [new ChangeItem { State = ItemState.Failed }, new ChangeItem { State = ItemState.Succeeded }, new ChangeItem { State = ItemState.Failed }] };
        f.W.Jobs.Add(job);
        var reset = await f.Runner.ResetFailedAsync(job);
        Assert.Equal(2, reset.Count);
        Assert.Equal([ItemState.Pending, ItemState.Succeeded, ItemState.Pending], job.Items.Select(i => i.State));
        Assert.Equal("journal:Jobs", f.Storage.Calls.Last());
        Assert.Equal(ItemState.Pending, f.Storage.Read(f.W.Id).Jobs.Single().Items[0].State);
    }

    [Fact]
    public async Task A_failed_save_while_resetting_surfaces_and_the_database_keeps_the_failed_state()
    {
        var f = new Fixture();
        var job = new BatchJob { Mode = RunMode.Demo, Items = [new ChangeItem { State = ItemState.Failed }] };
        f.W.Jobs.Add(job);
        f.Storage.Stored[f.W.Id] = f.W.Serialize();
        f.Storage.FailSaves = true;
        await Assert.ThrowsAsync<IOException>(() => f.Runner.ResetFailedAsync(job));
        Assert.Equal(ItemState.Failed, f.Storage.Read(f.W.Id).Jobs.Single().Items[0].State);
    }
}
