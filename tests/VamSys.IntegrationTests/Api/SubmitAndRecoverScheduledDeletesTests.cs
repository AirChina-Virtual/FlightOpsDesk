using System.Text.Json;
using VamSys.Core;
using VamSys.Infrastructure;
using VamSys.TestSupport;
using static VamSys.IntegrationTests.Api.PipelineHarness;
using static VamSys.IntegrationTests.Api.StoredWorld;

namespace VamSys.IntegrationTests.Api;

/// <summary>
/// The whole path of a deletion batch: the reviewed changes are handed over through <see cref="SubmissionService"/>, saved in a real
/// database, started (only the task saved by this submission), executed by the real executor against the fake server, and an item
/// that ended Unknown is recovered by reading. The server sees each DELETE once and the recovery sends no write at all.
/// </summary>
[Collection(StoredWorldCollection.Name)]
public class SubmitAndRecoverScheduledDeletesTests
{
    /// <summary>The real store behind the application layer's storage port (the window uses an equivalent adapter).</summary>
    sealed class StoreStorage(WorkspaceStore store) : IWorkspaceStorage
    {
        public IReadOnlyList<WorkspaceListing> List() => store.List().Select(x => new WorkspaceListing(x.Id, x.Name)).ToList();
        public Workspace Load(Guid id, IWorkspaceLoadObserver? observer = null) => store.Load(id, observer);
        public void Save(Workspace workspace) => store.Save(workspace);
        public Task SaveAsync(Workspace workspace) => store.SaveAsync(workspace);
        public Task SaveChangesAsync(Workspace workspace, JournalScope scope) => store.SaveChangesAsync(workspace, scope);
        public Task<bool> TrySaveDraftRowsAsync(Workspace workspace, IReadOnlyCollection<DraftRowChanges> changes) => store.TrySaveDraftRowsAsync(workspace, changes);
        public bool JobExists(Guid workspaceId, Guid jobId) => store.JobExists(workspaceId, jobId);
        public BatchJob? LoadJob(Guid workspaceId, Guid jobId) => store.LoadJob(workspaceId, jobId);
        public IBatchCheckpointWriter CheckpointWriter(Workspace workspace) => store.CheckpointWriter(workspace);
        public void SaveConnectionSettings(Workspace workspace, string clientId, string secret, string url, bool localRouteTimes)
            => store.SaveConnectionSettings(workspace, clientId, secret, url, localRouteTimes);
        public void UpdateOperationsCredentials(Workspace workspace, string clientId, string secret) => store.UpdateOperationsCredentials(workspace, clientId, secret);
        public MaintenancePreview PreviewMaintenance(Guid workspaceId)
        {
            var p = store.PreviewMaintenance(workspaceId);
            return new(p.WorkspaceId, p.Name, p.DataDirectory, p.Revision, p.Snapshots, p.Drafts, p.Jobs, p.Conflicts, p.HistorySteps, p.CanClear);
        }
        public long StoredRevision(Guid workspaceId) => store.CountRows(workspaceId).Revision;
        public async Task<MaintenanceOutcome> BackupAndClearAsync(Guid workspaceId, long expectedRevision)
        {
            var result = await store.BackupAndClearAsync(workspaceId, expectedRevision);
            return new(result.Workspace, result.Backup.DatabasePath);
        }
    }

    sealed class Timer : IAutosaveTimer
    {
        public bool IsEnabled { get; private set; }
        public void Start() => IsEnabled = true;
        public void Stop() => IsEnabled = false;
    }

    static string RouteId(ChangeItem item) => item.Before!.Identity!.RemoteId;

    [Fact]
    public async Task Three_scheduled_route_deletes_are_submitted_started_alone_sent_once_each_and_recovered_by_reading_only()
    {
        using var s = new StoredWorld();
        // routes 10 and 12 are scheduled in the standard data; add a third
        s.Remote.AddRow("routes/13", """{"id":13,"departure_id":101,"arrival_id":102,"type":"scheduled","callsign":"CAO130","flight_number":"CA130","fleet_ids":[4],"hidden":false}""");
        using (var doc = JsonDocument.Parse(s.Remote.Rows["routes/13"].ToJsonString()))
        {
            var row = s.World.Api.FromJson(ResourceKind.Routes, doc.RootElement);
            s.W.Resources[ResourceKind.Routes].Snapshot.Add(row.Dup());
            s.W.Resources[ResourceKind.Routes].Draft.Add(row.Dup());
        }
        foreach (var id in new[] { "10", "12", "13" })
            s.World.MarkDelete(ResourceKind.Routes, id);
        s.World.MarkDelete(ResourceKind.Airports, "103");
        // an earlier submission left a task Pending (airport 103); loading and submitting something else must never start it
        var older = new BatchJob { Mode = RunMode.Online, Items = new ChangePlanner().PlanWorkspace(s.W).Where(i => i.Resource == ResourceKind.Airports).ToList() };
        Assert.Single(older.Items);
        s.W.Jobs.Add(older);
        s.Store.Save(s.W);

        var session = new WorkspaceSession(new StoreStorage(s.Store), new InteractionGate(), new Timer());
        session.Open("default");
        session.Workspace.Mode = RunMode.Online;
        Assert.Equal(older.Id, Assert.Single(session.Workspace.Jobs).Id);
        var api = s.Adapter(session.Workspace);
        var timeouts = 0;
        s.Remote.Intercept = r =>
        {
            if (r.Method != "DELETE" || !r.Path.EndsWith("/routes/12", StringComparison.Ordinal)) return null;
            timeouts++;
            throw new TimeoutException("simulated");
        };

        var service = new SubmissionService(session);
        var started = new List<BatchJob>();
        var selected = false;
        await service.SubmitAsync(RunMode.Online, true, action => action(),
            () => Task.FromResult<List<ChangeItem>?>(new ChangePlanner().PlanWorkspace(session.Workspace).Where(i => i.Resource == ResourceKind.Routes).ToList()),
            () => selected = true,
            async job =>
            {
                service.RequireExecution(job, RunMode.Online, true);
                started.Add(job);
                await new OperationsBatchExecutor(api, session.Workspace).ExecuteAsync(job, session.CheckpointWriter(), Ct);
            });

        // only the task saved by this submission was started, and the task page was selected
        Assert.True(selected);
        var job = Assert.Single(started);
        Assert.NotEqual(older.Id, job.Id);
        Assert.Equal(2, session.Workspace.Jobs.Count);
        var oldJob = session.Workspace.Jobs.Single(j => j.Id == older.Id);
        Assert.All(oldJob.Items, i => Assert.Equal(ItemState.Pending, i.State));
        // each delete was sent exactly once, the lost answer included, and the older task's delete was not
        Assert.Equal(1, timeouts);
        Assert.Equal(["DELETE routes/10", "DELETE routes/12", "DELETE routes/13"], s.Writes().Order(StringComparer.Ordinal));
        var states = job.Items.ToDictionary(RouteId, i => i.State);
        Assert.Equal(ItemState.Succeeded, states["10"]);
        Assert.Equal(ItemState.Unknown, states["12"]);
        Assert.Equal(ItemState.Succeeded, states["13"]);
        Assert.Equal(JobStatus.NeedsAttention, job.StatusCode);
        var stored = s.Store.Load(session.Workspace.Id);
        Assert.Equal(Json(job.Items.Select(i => i.State)), Json(stored.Jobs.Single(j => j.Id == job.Id).Items.Select(i => i.State)));
        Assert.All(stored.Jobs.Single(j => j.Id == older.Id).Items, i => Assert.Equal(ItemState.Pending, i.State));

        // recovery of the lost delete reads and never writes
        s.Remote.Intercept = null;
        var before = s.Remote.Server.Requests.Count;
        await new OperationsBatchExecutor(api, session.Workspace).ExecuteAsync(job, session.CheckpointWriter(), Ct);

        var recovery = s.Remote.Server.Requests.Skip(before).ToArray();
        Assert.NotEmpty(recovery);
        Assert.Contains(recovery, r => r.Method == "GET" && r.Path.EndsWith("/routes/12", StringComparison.Ordinal));
        Assert.Equal(0, recovery.Count(r => r.Method != "GET"));
        Assert.Equal(3, s.Writes().Length);
        Assert.Equal(ItemState.Unknown, job.Items.Single(i => RouteId(i) == "12").State);
        Assert.All(session.Workspace.Jobs.Single(j => j.Id == older.Id).Items, i => Assert.Equal(ItemState.Pending, i.State));
    }
}
