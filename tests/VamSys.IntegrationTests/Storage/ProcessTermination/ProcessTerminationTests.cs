using System.Text.Json;
using VamSys.Core;
using VamSys.Infrastructure;
using VamSys.TestSupport;
using static VamSys.IntegrationTests.Storage.LegacyDatabases;
using static VamSys.IntegrationTests.Storage.ProcessTermination.ChildProcess;

namespace VamSys.IntegrationTests.Storage.ProcessTermination;

/// <summary>
/// A real operating-system process is ended at one storage barrier (no other instrumentation); the data directory is then
/// reopened. Task, step and rebase boundaries: nothing is lost and no write is repeated. Draft saves: either the whole row
/// save is there or none of it. Migrations: all or nothing, and the backup exists.
/// </summary>
[Trait("Category", "Slow")]
public class ProcessTerminationTests
{
    static void LegacyFleetSteps(ChangeItem item)
    {
        var post = new ApiStep(HttpMethod.Post, "fleet", new() { ["name"] = item.After.Get("Name"), ["code"] = item.After.Get("Type Code"), ["type"] = item.After.Get("Type (pax/cargo/...)") });
        var put = new ApiStep(HttpMethod.Put, "{created}", new() { ["max_pax"] = long.Parse(item.After.Get("Max Passengers"), System.Globalization.CultureInfo.InvariantCulture) });
        OperationsAdapter.PrepareSteps(item, [post, put]);
        item.ApiExpectedValues = OperationsAdapter.ExpectedValues([post, put]);
    }

    [Theory]
    [InlineData("Task:BeforeCommit")]
    [InlineData("Task:AfterCommit")]
    [InlineData("Step:BeforeCommit|POST|InFlight")]
    [InlineData("Step:BeforeCommit|POST|ResponseReceived")]
    [InlineData("Step:AfterCommit|POST|ResponseReceived")]
    [InlineData("Step:BeforeCommit|PUT|InFlight")]
    [InlineData("Step:AfterCommit|PUT|InFlight")]
    [InlineData("PUT:Accepted")]
    [InlineData("Step:BeforeCommit|PUT|ResponseReceived")]
    [InlineData("Rebase:BeforeCommit")]
    [InlineData("Rebase:AfterCommit")]
    public async Task Ending_the_process_at_a_task_step_or_rebase_boundary_loses_no_progress_and_repeats_no_write(string boundary)
    {
        using var dir = TempDataDirectory.Create("vamsys-proc-");
        var w = Fixture();
        LegacyFleetSteps(w.Jobs[0].Items[0]);
        var store = new WorkspaceStore(dir.Path);
        store.Save(w);
        var remote = new Remote();

        await Launch(dir.Path, w.Id, remote, m => Match(m, boundary));

        var interrupted = store.Load(w.Id);
        var item = interrupted.Jobs[0].Items[0];
        // Nothing was sent at these boundaries: a step is saved as in flight before it is sent.
        var beforeWrite = boundary is "Task:BeforeCommit" or "Task:AfterCommit" or "Step:BeforeCommit|POST|InFlight";
        if (beforeWrite)
            Assert.True(remote.Posts == 0 && remote.Puts == 0, "A write left before its in-flight step was saved");
        // The answered POST is durable and the PUT never left: recovery sends only that PUT.
        var resumable = boundary is "Step:AfterCommit|POST|ResponseReceived" or "Step:BeforeCommit|PUT|InFlight";
        var durableRebase = boundary == "Rebase:AfterCommit";
        Assert.Equal(durableRebase, item.State == ItemState.Succeeded);
        Assert.Equal(durableRebase ? 1 : 0, interrupted.Resources[ResourceKind.Fleets].Snapshot.Count);
        Assert.Equal(durableRebase ? 0 : 1, interrupted.Resources[ResourceKind.Fleets].Undo.Count);
        if (boundary.Contains("POST|ResponseReceived", StringComparison.Ordinal))
        {
            Assert.True(remote.Posts == 1 && remote.Puts == 0);
            Assert.Equal(boundary.StartsWith("Step:AfterCommit", StringComparison.Ordinal), item.CreateCompleted);
            Assert.Equal(item.CreateCompleted, item.RemoteId == "10");
        }
        if (boundary.Contains("PUT|InFlight", StringComparison.Ordinal))
        {
            Assert.True(item.RemoteId == "10" && item.CreateCompleted && remote.Puts == 0);
            Assert.Equal(boundary.StartsWith("Step:AfterCommit", StringComparison.Ordinal) ? BatchStepState.InFlight : BatchStepState.Prepared, item.ApiSteps[1].State);
        }

        var posts = remote.Posts;
        var puts = remote.Puts;
        await Launch(dir.Path, w.Id, remote);
        var recovered = store.Load(w.Id);
        item = recovered.Jobs[0].Items[0];
        if (beforeWrite)
            Assert.True(remote.Posts == 1 && remote.Puts == 1 && item.State == ItemState.Succeeded, $"POST={remote.Posts} PUT={remote.Puts} state={item.State} message={item.Message} diagnostic={item.Diagnostic} expected={JsonSerializer.Serialize(item.ApiExpectedValues)}");
        else if (resumable)
        {
            Assert.True(remote.Posts == posts && remote.Puts == puts + 1 && item.State == ItemState.Succeeded, $"Recovery did not resume only the never-sent PUT: POST={remote.Posts} PUT={remote.Puts} state={item.State} message={item.Message}");
            await Launch(dir.Path, w.Id, remote);
            Assert.True(remote.Posts == posts && remote.Puts == puts + 1, "A resumed create wrote again");
        }
        else
        {
            Assert.True(remote.Posts == posts && remote.Puts == puts, "Recovery repeated an uncertain write");
            Assert.Equal(remote.Completed ? ItemState.Succeeded : ItemState.Unknown, item.State);
            await Launch(dir.Path, w.Id, remote);
            Assert.True(remote.Posts == posts && remote.Puts == puts, "Second recovery repeated a write");
            if (remote.Created && !remote.Completed)
            {
                // A separately verified remote repair can make the existing request verifiable.
                remote.Completed = true;
                recovered = store.Load(w.Id);
                item = recovered.Jobs[0].Items[0];
                if (item.CanProposeRecoveryId)
                {
                    item.ProposeRecoveryId("10");
                    await store.CheckpointWriter(recovered).WriteAsync(new(CheckpointKind.Task, recovered.Jobs[0], item), TestContext.Current.CancellationToken);
                }
                await Launch(dir.Path, w.Id, remote);
                Assert.Equal(ItemState.Succeeded, store.Load(w.Id).Jobs[0].Items[0].State);
                Assert.True(remote.Posts == posts && remote.Puts == puts);
            }
        }

        var final = store.Load(w.Id);
        if (final.Jobs[0].Items[0].State == ItemState.Succeeded)
        {
            Assert.Equal("10", final.Resources[ResourceKind.Fleets].Snapshot.Single().Identity!.RemoteId);
            Assert.Equal("001", final.Resources[ResourceKind.Fleets].Draft.Single().Get("Unknown"));
            Assert.True(final.Resources[ResourceKind.Fleets].Undo.Count == 0 && final.VerifiedOperations.Contains("Fleets:Create"));
        }
    }

    [Theory]
    [InlineData("Draft:BeforeCommit")]
    [InlineData("Draft:AfterCommit")]
    [InlineData("")]
    public async Task Ending_the_process_during_a_draft_row_save_leaves_the_whole_row_save_or_none_of_it(string boundary)
    {
        using var dir = TempDataDirectory.Create("vamsys-proc-");
        var w = Fixture();
        var store = new WorkspaceStore(dir.Path);
        store.Save(w);
        var original = store.Load(w.Id);

        await Launch(dir.Path, w.Id, new Remote(), boundary == "" ? null : m => Match(m, boundary), "draft");

        var disk = store.Load(w.Id);
        var saved = boundary != "Draft:BeforeCommit";
        var fleets = disk.Resources[ResourceKind.Fleets];
        Assert.Equal(saved ? "Edited in child" : original.Resources[ResourceKind.Fleets].Draft[0].Get("Name"), fleets.Draft[0].Get("Name"));
        Assert.Equal(original.Resources[ResourceKind.Fleets].Undo.Count + (saved ? 1 : 0), fleets.Undo.Count);
        Assert.Equal(original.StorageRevision + (saved ? 1 : 0), disk.StorageRevision);
        // Everything else is exactly what was saved before, and the next full save still accepts the revision.
        var expected = saved ? original : disk;
        if (saved)
        {
            var od = expected.Resources[ResourceKind.Fleets];
            od.History = fleets.History.Copy();
            od.Draft[0] = od.Draft[0].With("Name", "Edited in child");
            expected.StorageRevision = disk.StorageRevision;
        }
        Assert.True(disk.Serialize() == expected.Serialize(), "Unrelated data changed");
        disk.Name = "after restart";
        store.Save(disk);
        Assert.Equal("after restart", store.Load(w.Id).Name);
    }

    [Theory]
    [InlineData("Migration:BeforeCommit", 0)]
    [InlineData("Migration:AfterCommit", 4)]
    public async Task Ending_the_process_during_the_SQLite_migration_leaves_the_old_database_or_the_finished_one_and_a_backup(string boundary, int expectedVersion)
    {
        using var dir = TempDataDirectory.Create("vamsys-proc-");
        var w = Fixture();
        using (var legacy = Legacy(dir.Path, w)) { }
        var remote = new Remote();

        await Launch(dir.Path, w.Id, remote, m => Match(m, boundary));

        using (var c = Open(dir.Path))
            Assert.Equal(expectedVersion, Convert.ToInt32(Sql(c, "PRAGMA user_version")));
        var store = new WorkspaceStore(dir.Path);
        Assert.True(store.Load(w.Id).Jobs[0].Items[0].State == ItemState.Pending && remote.Posts == 0);
        Assert.NotEmpty(Directory.GetFiles(dir.Path, "workspaces-before-v4-*.db"));
    }

    [Theory]
    [InlineData("Migration:BeforeCommit", 3)]
    [InlineData("Migration:AfterCommit", 4)]
    public async Task Ending_the_process_during_the_v3_history_migration_keeps_undo_and_redo_whole(string boundary, int expectedVersion)
    {
        using var dir = TempDataDirectory.Create("vamsys-proc-");
        FixedLegacy(dir.Path, 3);

        await Launch(dir.Path, FixedId, new Remote(), m => Match(m, boundary), "draft");

        using (var c = Open(dir.Path))
            Assert.Equal(expectedVersion, Convert.ToInt32(Sql(c, "PRAGMA user_version")));
        var store = new WorkspaceStore(dir.Path);
        var d = store.Load(FixedId).Resources[ResourceKind.Fleets];
        Assert.True(d.Undo.Count == 2 && d.Redo.Count == 2 && d.Draft[0].Get("Name") == "current");
        d.UndoEdit();
        Assert.Equal("start", d.Draft[0].Get("Name"));
        d.RedoEdit();
        d.RedoEdit();
        Assert.Equal("future1", d.Draft[0].Get("Name"));
    }
}
