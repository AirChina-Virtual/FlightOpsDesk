using System.Text.Json;
using VamSys.Core;
using VamSys.Infrastructure;
using VamSys.TestSupport;
using static VamSys.IntegrationTests.Storage.LegacyDatabases;

namespace VamSys.IntegrationTests.Storage;

/// <summary>
/// Execution checkpoints against a real database: a task checkpoint writes only task rows, a rebase (whole staged workspace
/// or one row delta) is one atomic transaction, stale copies are refused, and the payload of a checkpoint does not grow with
/// the size of the resources.
/// </summary>
public class StorageCheckpointTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;
    const string ForbidResourceWrites = "CREATE TRIGGER forbid_resources BEFORE UPDATE ON workspaces BEGIN SELECT RAISE(ABORT,'resource write'); END";

    static ResourceRebaseDelta Delta(ChangeItem item)
        => new(item.Resource, item.After.LocalId, new(RowMutationKind.Upsert, item.After.Dup()), new(RowMutationKind.Upsert, item.After.Dup()), DateTimeOffset.UnixEpoch, true, "Fleets:Create");

    [Fact]
    public async Task A_task_checkpoint_never_writes_resource_JSON_and_a_stale_copy_or_a_foreign_job_is_refused()
    {
        using var dir = TempDataDirectory.Create("vamsys-ckpt-");
        var store = new WorkspaceStore(dir.Path);
        var w = Fixture();
        w.Jobs[0].Items.Add(new() { Message = "untouched" });
        store.Save(w);
        var stale = store.Load(w.Id);
        using var c = Open(dir.Path);
        var original = Text(c, "SELECT json FROM workspaces");
        var other = Text(c, "SELECT json FROM batch_items WHERE ordinal=1");
        Sql(c, ForbidResourceWrites);
        var item = w.Jobs[0].Items[0];
        item.State = ItemState.Unknown;
        item.ProposeRecoveryId("10");

        await store.CheckpointWriter(w).WriteAsync(new(CheckpointKind.Task, w.Jobs[0], item), Ct);
        Assert.Equal("10", store.Load(w.Id).Jobs[0].Items[0].RecoveryCandidateId);
        Assert.Equal(original, Text(c, "SELECT json FROM workspaces"));
        Assert.Equal(other, Text(c, "SELECT json FROM batch_items WHERE ordinal=1"));

        Sql(c, "DROP TRIGGER forbid_resources");
        var error = await Assert.ThrowsAnyAsync<Exception>(() => store.SaveAsync(stale));
        Assert.Equal("StorageStale", MessageErrors.Describe(error).Code);
        Assert.Equal(ItemState.Unknown, store.Load(w.Id).Jobs[0].Items[0].State);

        var revision = w.StorageRevision;
        error = await Assert.ThrowsAnyAsync<Exception>(() => store.CheckpointWriter(w).WriteAsync(new(CheckpointKind.Task, new BatchJob(), item), Ct));
        Assert.Equal("StorageInvalid", MessageErrors.Describe(error).Code);
        Assert.Equal(revision, store.Load(w.Id).StorageRevision);
    }

    [Theory]
    [InlineData("Rebase:BeforeCommit")]
    [InlineData("Rebase:AfterCommit")]
    public async Task A_rebase_of_a_staged_workspace_is_one_transaction_with_the_task_status(string point)
    {
        using var dir = TempDataDirectory.Create("vamsys-ckpt-");
        var store = new WorkspaceStore(dir.Path);
        var w = Fixture();
        store.Save(w);
        var staged = Workspace.Deserialize(w.Serialize());
        var item = staged.Jobs[0].Items[0];
        item.State = ItemState.Succeeded;
        item.RemoteId = "10";
        item.Rebased = true;
        staged.Resources[ResourceKind.Fleets].Snapshot = [item.After.Dup()];
        staged.Resources[ResourceKind.Fleets].ClearHistory();
        staged.VerifiedOperations.Add("Fleets:Create");
        store.Barrier = p => { if (p == point) throw new IOException("fault"); };
        using var run = PerformanceRun.Begin(w.Id, w.Jobs[0].Id);

        await Assert.ThrowsAnyAsync<Exception>(() => store.CheckpointWriter(w).WriteAsync(new(CheckpointKind.Rebase, staged.Jobs[0], item, staged), Ct));

        var loaded = store.Load(w.Id);
        var committed = point.EndsWith("AfterCommit", StringComparison.Ordinal);
        Assert.Equal(committed, loaded.Jobs[0].Items[0].State == ItemState.Succeeded);
        Assert.Equal(committed ? 1 : 0, loaded.Resources[ResourceKind.Fleets].Snapshot.Count);
        Assert.Equal(committed ? 0 : 1, loaded.Resources[ResourceKind.Fleets].Undo.Count);
        Assert.Equal(committed, loaded.VerifiedOperations.Contains("Fleets:Create"));
        // the caller's own workspace is never changed by the checkpoint
        Assert.Equal(ItemState.Pending, w.Jobs[0].Items[0].State);
        Assert.Single(w.Resources[ResourceKind.Fleets].Undo);
        Assert.Equal((committed ? 1 : 0, committed ? 0 : 1), (run.SaveSuccesses, run.SaveFailures));
    }

    [Theory]
    [InlineData("Rebase:BeforeCommit")]
    [InlineData("Rebase:AfterCommit")]
    public async Task A_rebase_of_one_row_is_one_transaction_with_the_row_the_history_and_the_verified_operation(string point)
    {
        using var dir = TempDataDirectory.Create("vamsys-ckpt-");
        var store = new WorkspaceStore(dir.Path);
        var w = Fixture();
        store.Save(w);
        var item = JsonSerializer.Deserialize<ChangeItem>(JsonSerializer.Serialize(w.Jobs[0].Items[0]))!;
        item.State = ItemState.Succeeded;
        item.Rebased = true;
        store.Barrier = p => { if (p == point) throw new IOException("fault"); };

        await Assert.ThrowsAsync<IOException>(() => store.CheckpointWriter(w).WriteAsync(new(CheckpointKind.Rebase, w.Jobs[0], item, Delta: Delta(item)), Ct));

        var disk = store.Load(w.Id);
        var committed = point.EndsWith("AfterCommit", StringComparison.Ordinal);
        Assert.Equal(committed, disk.Jobs[0].Items[0].State == ItemState.Succeeded);
        Assert.Equal(committed ? 1 : 0, disk.Resources[ResourceKind.Fleets].Snapshot.Count);
        Assert.Equal(committed ? 0 : 1, disk.Resources[ResourceKind.Fleets].Undo.Count);
        Assert.Equal(committed, disk.VerifiedOperations.Contains("Fleets:Create"));
        Assert.Single(w.Resources[ResourceKind.Fleets].Undo);
    }

    [Fact]
    public async Task A_row_delta_keeps_the_stored_order_deletes_only_its_row_and_cannot_be_overwritten_by_a_stale_full_save()
    {
        using var dir = TempDataDirectory.Create("vamsys-ckpt-");
        var store = new WorkspaceStore(dir.Path);
        var w = Fixture();
        var data = w.Resources[ResourceKind.Fleets];
        var first = data.Draft[0];
        var other = new DataRow { Fields = new Dictionary<string, string> { ["Name"] = "other" } };
        data.Snapshot = [first.Dup(), other.Dup()];
        data.Draft.Add(other);
        store.Save(w);
        var stale = store.Load(w.Id);
        var item = w.Jobs[0].Items[0];
        item.State = ItemState.Succeeded;

        await store.CheckpointWriter(w).WriteAsync(new(CheckpointKind.Rebase, w.Jobs[0], item, Delta: Delta(item)), Ct);

        var disk = store.Load(w.Id);
        Assert.Equal([other.LocalId, first.LocalId], disk.Resources[ResourceKind.Fleets].Snapshot.Select(r => r.LocalId)); // the upserted snapshot row moves to the end
        Assert.Equal([first.LocalId, other.LocalId], disk.Resources[ResourceKind.Fleets].Draft.Select(r => r.LocalId));
        var error = Assert.ThrowsAny<InvalidOperationException>(() => store.Save(stale));
        Assert.Equal("StorageStale", MessageErrors.Describe(error).Code);

        var delete = new ResourceRebaseDelta(item.Resource, first.LocalId, new(RowMutationKind.Delete), new(RowMutationKind.Delete), DateTimeOffset.UnixEpoch, true, null);
        await store.CheckpointWriter(w).WriteAsync(new(CheckpointKind.Rebase, w.Jobs[0], item, Delta: delete), Ct);
        Assert.Equal(other.LocalId, store.Load(w.Id).Resources[ResourceKind.Fleets].Draft.Single().LocalId);
    }

    // ---- payload size ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_task_or_rebase_checkpoint_serializes_the_same_bytes_whatever_the_size_of_the_resources_and_their_history()
    {
        var template = Fixture().Serialize();
        long? expected = null;
        foreach (var (rows, undo) in new[] { (1, 0), (20000, 0), (20000, 3) })
        {
            using var dir = TempDataDirectory.Create("vamsys-ckpt-");
            var w = Workspace.Deserialize(template);
            var data = w.Resources[ResourceKind.Routes];
            data.Draft = Enumerable.Range(0, rows).Select(i => new DataRow { Fields = new Dictionary<string, string> { ["ID"] = i.ToString(), ["Callsign"] = "ACA" + i, ["Remarks"] = "性能" } }).ToList();
            data.Snapshot = data.Draft.Select(r => r.Dup()).ToList();
            for (var n = 0; n < undo; n++)
            {
                var row = data.Draft[n % data.Draft.Count];
                data.Edit([row.LocalId], () => data.ReplaceDraftRows([row.With("Remarks", "history " + n)]));
            }
            var store = new WorkspaceStore(dir.Path);
            store.Save(w);
            using var c = Open(dir.Path);
            var json = Text(c, "SELECT json FROM workspaces");
            Sql(c, ForbidResourceWrites);
            var item = w.Jobs[0].Items[0];
            item.State = ItemState.Running;
            using (var run = PerformanceRun.Begin(w.Id, w.Jobs[0].Id))
            {
                for (var n = 0; n < 20; n++)
                    await store.CheckpointWriter(w).WriteAsync(new(CheckpointKind.Task, w.Jobs[0], item), Ct);
                Assert.Equal(20, run.SaveSuccesses);
                Assert.Equal(20, run.Checkpoints["Task"]);
                expected ??= run.JsonBytes;
                Assert.InRange(run.JsonBytes, expected.Value - 40, expected.Value + 40); // resource size does not change the task payload (a shorter time stamp may save a byte per checkpoint)
                var stored = System.Text.Encoding.UTF8.GetByteCount(Text(c, "SELECT json FROM batch_jobs")) + System.Text.Encoding.UTF8.GetByteCount(Text(c, "SELECT json FROM batch_items"));
                Assert.Equal(20L * stored, run.JsonBytes); // measured bytes are exactly the stored header and item payloads
            }
            Assert.Equal(json, Text(c, "SELECT json FROM workspaces"));
            Sql(c, "DROP TRIGGER forbid_resources");

            var staged = Workspace.Deserialize(w.Serialize());
            var changed = staged.Jobs[0].Items[0];
            changed.State = ItemState.Succeeded;
            staged.Resources[ResourceKind.Fleets].ClearHistory();
            using var rebase = PerformanceRun.Begin(w.Id, w.Jobs[0].Id);
            await store.CheckpointWriter(w).WriteAsync(new(CheckpointKind.Rebase, staged.Jobs[0], changed, staged), Ct);
            Assert.Equal(1, rebase.SaveSuccesses);
        }
    }

    [Theory]
    [InlineData(1, 0, false)]
    [InlineData(20000, 0, false)]
    [InlineData(20000, 3, false)]
    [InlineData(20000, 3, true)]
    public async Task A_row_delta_serializes_one_row_and_touches_exactly_two_stored_rows_whatever_the_unrelated_rows_and_history(int rows, int undo, bool sameResource)
    {
        using var dir = TempDataDirectory.Create("vamsys-ckpt-");
        var w = Workspace.Deserialize(Fixture().Serialize());
        var kind = sameResource ? ResourceKind.Fleets : ResourceKind.Routes;
        var data = w.Resources[kind];
        for (var n = 0; n < rows; n++)
        {
            var row = new DataRow { Fields = new Dictionary<string, string> { ["Name"] = "Row " + n, ["ID"] = n.ToString() } };
            data.Draft.Add(row);
            data.Snapshot.Add(row.Dup());
        }
        for (var n = 0; n < undo; n++)
        {
            var row = data.Draft[n % data.Draft.Count];
            data.Edit([row.LocalId], () => data.ReplaceDraftRows([row.With("Name", "history " + n)]));
        }
        var store = new WorkspaceStore(dir.Path);
        store.Save(w);
        var item = w.Jobs[0].Items[0];
        item.State = ItemState.Succeeded;
        using var c = Open(dir.Path);
        Sql(c, "CREATE TABLE touched(resource INTEGER,local_id TEXT); CREATE TRIGGER row_i AFTER INSERT ON resource_rows BEGIN INSERT INTO touched VALUES(NEW.resource,NEW.local_id); END; CREATE TRIGGER row_u AFTER UPDATE ON resource_rows BEGIN INSERT INTO touched VALUES(NEW.resource,NEW.local_id); END; CREATE TRIGGER row_d AFTER DELETE ON resource_rows BEGIN INSERT INTO touched VALUES(OLD.resource,OLD.local_id); END;");
        using var metrics = PerformanceRun.Begin(w.Id, w.Jobs[0].Id);

        await store.CheckpointWriter(w).WriteAsync(new(CheckpointKind.Rebase, w.Jobs[0], item, Delta: Delta(item)), Ct);

        Assert.True(metrics.JsonBytes < 16384, "the delta serialized unrelated resources: " + metrics.JsonBytes);
        Assert.Equal(0, Number(c, "SELECT count(*) FROM touched WHERE resource!=$r OR local_id!=$id", ("$r", (int)item.Resource), ("$id", item.After.LocalId.ToString())));
        Assert.Equal(2, Number(c, "SELECT count(*) FROM touched")); // the snapshot row and the draft row
        Assert.Equal(1, metrics.SaveSuccesses);
        var disk = store.Load(w.Id);
        Assert.Empty(disk.Resources[ResourceKind.Fleets].Undo);
        if (!sameResource)
            Assert.Equal(undo, disk.Resources[kind].Undo.Count);
    }

    [Fact]
    public async Task The_serialized_size_of_a_row_delta_is_the_same_for_every_resource_size()
    {
        var sizes = new List<long>();
        foreach (var (rows, undo, same) in new[] { (1, 0, false), (20000, 0, false), (20000, 3, false), (20000, 3, true) })
        {
            using var dir = TempDataDirectory.Create("vamsys-ckpt-");
            var w = Workspace.Deserialize(Fixture().Serialize());
            var data = w.Resources[same ? ResourceKind.Fleets : ResourceKind.Routes];
            for (var n = 0; n < rows; n++)
            {
                var row = new DataRow { Fields = new Dictionary<string, string> { ["Name"] = "Row " + n, ["ID"] = n.ToString() } };
                data.Draft.Add(row);
                data.Snapshot.Add(row.Dup());
            }
            for (var n = 0; n < undo; n++)
            {
                var row = data.Draft[n % data.Draft.Count];
                data.Edit([row.LocalId], () => data.ReplaceDraftRows([row.With("Name", "history " + n)]));
            }
            var store = new WorkspaceStore(dir.Path);
            store.Save(w);
            var item = w.Jobs[0].Items[0];
            item.State = ItemState.Succeeded;
            using var metrics = PerformanceRun.Begin(w.Id, w.Jobs[0].Id);
            await store.CheckpointWriter(w).WriteAsync(new(CheckpointKind.Rebase, w.Jobs[0], item, Delta: Delta(item)), Ct);
            sizes.Add(metrics.JsonBytes);
        }
        // A time stamp that ends in zeros is written one character shorter, so allow a few bytes; a copy of the list would be megabytes.
        Assert.True(sizes.All(s => Math.Abs(s - sizes[0]) <= 8), string.Join(",", sizes));
    }
}
