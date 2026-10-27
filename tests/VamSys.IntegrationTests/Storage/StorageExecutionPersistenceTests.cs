using VamSys.Core;
using VamSys.Infrastructure;
using VamSys.IntegrationTests.Golden.Requests;
using VamSys.TestSupport;
using static VamSys.IntegrationTests.Api.PipelineHarness;

namespace VamSys.IntegrationTests.Storage;

/// <summary>
/// The executor writing its checkpoints into a real database (not a recording writer): a checkpoint that cannot be written stops
/// the run before the next request and leaves memory unchanged until the commit; an item whose outcome is unknown is still
/// Unknown on disk after every kind of failed recovery read, and recovery never writes.
/// </summary>
public class StorageExecutionPersistenceTests
{
    static GoldenWorld NewFleetWorld(out BatchJob job, WorkspaceStore store)
    {
        var w = new GoldenWorld();
        w.New(ResourceKind.Fleets, ("Name", "New fleet"), ("Type Code", "B738"), ("Type (pax/cargo/...)", "pax"), ("Parameter IDs", "1"));
        job = NewJob(w);
        store.Save(w.Workspace);
        return w;
    }

    [Theory]
    [InlineData("id")]
    [InlineData("rebase")]
    public async Task A_checkpoint_that_cannot_be_written_stops_the_run_and_the_rebase_is_applied_to_memory_only_after_its_commit(string fault)
    {
        using var dir = TempDataDirectory.Create("vamsys-exec-");
        var store = new WorkspaceStore(dir.Path);
        var w = NewFleetWorld(out var job, store);
        var item = job.Items.Single();
        var data = w.Workspace.Resources[ResourceKind.Fleets];
        var (snapshots, undo, verified) = (data.Snapshot.Count, data.Undo.Count, w.Workspace.VerifiedOperations.Count);
        store.Barrier = p => { if (fault == "id" ? p == "Step:BeforeCommit" && item.CreateCompleted : p == "Rebase:BeforeCommit") throw new IOException("injected"); };

        await Assert.ThrowsAsync<PersistenceException>(() => Execute(w, job, store.CheckpointWriter(w.Workspace)));

        Assert.Equal(0, Count(w, "PUT"));
        Assert.Equal(1, Count(w, "POST"));
        Assert.NotEqual(ItemState.Succeeded, item.State);
        Assert.Equal((snapshots, undo, verified), (data.Snapshot.Count, data.Undo.Count, w.Workspace.VerifiedOperations.Count)); // memory follows only a durable rebase
        var disk = store.Load(w.Workspace.Id);
        Assert.Equal(fault == "rebase", disk.Jobs[0].Items[0].CreateCompleted);
        Assert.NotEmpty(disk.Jobs[0].Items[0].ApiSteps);
        Assert.NotEqual(ItemState.Succeeded, disk.Jobs[0].Items[0].State);
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("cancel")]
    [InlineData("401")]
    [InlineData("403")]
    [InlineData("429")]
    [InlineData("503")]
    [InlineData("json")]
    public async Task An_unknown_item_stays_unknown_on_disk_after_a_failed_recovery_read_and_recovery_only_reads(string fault)
    {
        using var dir = TempDataDirectory.Create("vamsys-exec-");
        var store = new WorkspaceStore(dir.Path);
        var w = new GoldenWorld();
        w.Remote.Intercept = r => r.Method == "PUT" ? Status(500) : null;
        w.Edit(ResourceKind.Fleets, "4", ("Name", "renamed"));
        var job = NewJob(w);
        store.Save(w.Workspace);
        await Execute(w, job, store.CheckpointWriter(w.Workspace));
        Assert.Equal(ItemState.Unknown, store.Load(w.Workspace.Id).Jobs[0].Items[0].State);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var cancel = new CancellationTokenSource();
            var before = w.Remote.Server.Requests.Count;
            w.Remote.Intercept = r =>
            {
                if (fault == "timeout") throw new TimeoutException();
                if (fault == "cancel") { cancel.Cancel(); throw new OperationCanceledException(cancel.Token); }
                return fault == "json" ? FakeResponse.Ok("invalid JSON") : FakeResponse.WithStatus(int.Parse(fault), "invalid JSON");
            };

            await Execute(w, job, store.CheckpointWriter(w.Workspace), ct: cancel.Token);

            var disk = store.Load(w.Workspace.Id);
            Assert.Equal(ItemState.Unknown, disk.Jobs[0].Items[0].State);
            Assert.All(w.Remote.Server.Requests.Skip(before), r => Assert.Equal("GET", r.Method));
            if (fault is "401" or "403")
                Assert.Equal(JobStatus.Paused, disk.Jobs[0].StatusCode);
            if (fault == "cancel")
                Assert.Equal(JobStatus.Canceled, disk.Jobs[0].StatusCode);
        }
        Assert.Equal(1, Count(w, "PUT"));
    }
}
