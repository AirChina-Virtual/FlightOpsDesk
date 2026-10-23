using VamSys.Core;
using VamSys.Infrastructure;
using VamSys.TestSupport;

namespace VamSys.IntegrationTests.Storage;

/// <summary>
/// What a journal save writes, counted by the database itself (triggers on every table) and by the store's SaveReport,
/// plus the failure paths: an injected failure, a stale copy and a workspace the journal cannot vouch for.
/// </summary>
public class JournalWriteScopeTests : IDisposable
{
    readonly TempDataDirectory dir = TempDataDirectory.Create("vamsys-scope-");
    readonly WorkspaceStore store;
    readonly Workspace w = new() { Name = "Scope", AirlineId = "7", Mode = RunMode.Online };
    ResourceData Routes => w.Resources[ResourceKind.Routes];

    public JournalWriteScopeTests()
    {
        store = new WorkspaceStore(dir.Path);
        for (var n = 0; n < 300; n++)
        {
            var row = new DataRow { Identity = new((n + 1).ToString(), "7"), Fields = new() { ["ID"] = (n + 1).ToString(), ["Callsign"] = "ACA" + n, ["Remarks"] = "" } };
            Routes.Draft.Add(row);
            Routes.Snapshot.Add(row.Copy());
        }
        Routes.Columns = ["ID", "Callsign", "Remarks"];
        w.Jobs.Add(new BatchJob { Mode = RunMode.Online, Items = [new ChangeItem { State = ItemState.Succeeded }] });
        store.Save(w);
        StoredState.InstallWriteCounters(dir.Path);
    }

    public void Dispose() => dir.Dispose();

    Dictionary<string, int> Writes() => StoredState.Writes(dir.Path);
    static int Count(Dictionary<string, int> writes, string table) => writes.GetValueOrDefault(table);

    void EditRows(int count, string value = "batch", int skip = 10)
    {
        var copies = Routes.Draft.Skip(skip).Take(count).Select(r => { var c = r.Copy(); c.Fields["Remarks"] = value; return c; }).ToArray();
        Routes.Edit(copies.Select(r => r.LocalId), () => Routes.ReplaceDraftRows(copies));
    }

    [Fact]
    public async Task A_batch_edit_of_200_rows_writes_200_rows_and_its_undo_step()
    {
        EditRows(200);
        await store.SaveChangesAsync(w);
        var report = store.LastSave!;
        Assert.Equal("Journal", report.Kind);
        Assert.Equal(200, report.RowWrites);
        Assert.Equal(0, report.RowDeletes + report.RowMoves + report.JobWrites + report.MetadataWrites);
        Assert.False(report.HeaderWritten);
        var writes = Writes();
        Assert.Equal(200, Count(writes, "resource_rows"));
        Assert.Equal(1, Count(writes, "resource_history_steps"));
        Assert.Equal(1, Count(writes, "resource_history_state"));
        Assert.False(writes.ContainsKey("workspaces") || writes.ContainsKey("batch_jobs") || writes.ContainsKey("batch_items") || writes.ContainsKey("resource_metadata"));
        Assert.True(report.JsonBytes > 0);
    }

    [Fact]
    public async Task Adding_one_row_inserts_one_row_and_updates_the_column_list()
    {
        var row = new DataRow { Fields = new() { ["ID"] = "", ["Callsign"] = "NEW", ["Remarks"] = "", ["Extra"] = "x" } };
        Routes.Edit([row.LocalId], () => Routes.Draft.Add(row), structural: true);
        Routes.Columns = Routes.Columns.Concat(row.Fields.Keys).Distinct().ToList();
        await store.SaveChangesAsync(w);
        Assert.Equal(1, store.LastSave!.RowWrites);
        var writes = Writes();
        Assert.Equal(1, Count(writes, "resource_rows"));
        Assert.Equal(1, Count(writes, "resource_metadata"));
        Assert.False(writes.ContainsKey("batch_jobs"));
        Assert.Equal(301, store.CountRows(w.Id).Resources[ResourceKind.Routes].Drafts);
    }

    [Fact]
    public async Task Removing_the_last_rows_deletes_them_without_touching_the_others()
    {
        var gone = Routes.Draft.TakeLast(3).Select(r => r.LocalId).ToArray();
        Routes.Edit(gone, () => Routes.Draft.RemoveRange(Routes.Draft.Count - 3, 3), structural: true);
        await store.SaveChangesAsync(w);
        Assert.Equal(3, store.LastSave!.RowDeletes);
        Assert.Equal(0, store.LastSave.RowMoves);
        Assert.Equal(3, Count(Writes(), "resource_rows"));
        Assert.Equal(297, store.Load(w.Id).Resources[ResourceKind.Routes].Draft.Count);
    }

    [Fact]
    public async Task Removing_a_row_near_the_end_renumbers_only_the_rows_behind_it()
    {
        var target = Routes.Draft[295];
        Routes.Edit([target.LocalId], () => Routes.Draft.Remove(target), structural: true);
        await store.SaveChangesAsync(w);
        Assert.Equal(1, store.LastSave!.RowDeletes);
        Assert.Equal(4, store.LastSave.RowMoves);
        var loaded = store.Load(w.Id);
        Assert.Equal(Routes.Draft.Select(r => r.LocalId), loaded.Resources[ResourceKind.Routes].Draft.Select(r => r.LocalId));
    }

    [Fact]
    public async Task Nothing_changed_writes_nothing_and_does_not_advance_the_revision()
    {
        var revision = StoredState.Revision(dir.Path);
        await store.SaveChangesAsync(w);
        Assert.True(store.LastSave!.WroteNothing);
        store.SaveChanges(w);
        Assert.True(store.LastSave.WroteNothing);
        Assert.Empty(Writes());
        Assert.Equal(revision, StoredState.Revision(dir.Path));
        Assert.Equal(revision, w.StorageRevision);
    }

    [Fact]
    public async Task A_header_only_save_writes_the_header_and_leaves_pending_rows_pending()
    {
        EditRows(5);
        w.ConnectedAt = DateTimeOffset.UnixEpoch;
        await store.SaveHeaderAsync(w);
        Assert.True(store.LastSave!.HeaderWritten);
        var writes = Writes();
        Assert.Equal(1, Count(writes, "workspaces"));
        Assert.False(writes.ContainsKey("resource_rows") || writes.ContainsKey("batch_jobs") || writes.ContainsKey("resource_metadata") || writes.ContainsKey("resource_history_steps"));
        Assert.Equal(DateTimeOffset.UnixEpoch, store.Load(w.Id).ConnectedAt);
        // The edit is still pending and goes out with the next full-scope save.
        await store.SaveChangesAsync(w);
        Assert.Equal(5, store.LastSave.RowWrites);
        Assert.False(store.LastSave.HeaderWritten);
    }

    [Fact]
    public async Task A_jobs_only_save_writes_the_new_job_and_no_resource_rows()
    {
        EditRows(5);
        var job = new BatchJob { Mode = RunMode.Online, Items = [.. Enumerable.Range(0, 7).Select(n => new ChangeItem { Message = "i" + n })] };
        w.Jobs.Add(job);
        await store.SaveJobsAsync(w);
        Assert.Equal(1, store.LastSave!.JobWrites);
        Assert.Equal(7, store.LastSave.ItemWrites);
        var writes = Writes();
        Assert.Equal(1, Count(writes, "batch_jobs"));
        Assert.Equal(7, Count(writes, "batch_items"));
        Assert.False(writes.ContainsKey("resource_rows") || writes.ContainsKey("workspaces"));
        Assert.True(store.JobExists(w.Id, job.Id));
        Assert.Equal(7, store.LoadJob(w.Id, job.Id)!.Items.Count);
        await store.SaveChangesAsync(w);
        Assert.Equal(5, store.LastSave.RowWrites);
        Assert.Equal(0, store.LastSave.JobWrites);
    }

    [Fact]
    public async Task A_touched_job_is_written_again_and_an_untouched_one_is_not()
    {
        var existing = w.Jobs[0];
        existing.Items[0].State = ItemState.Failed;
        await store.SaveChangesAsync(w);
        Assert.True(store.LastSave!.WroteNothing); // changed in place without a mark: outside the contract
        w.Journal().TouchJob(existing);
        await store.SaveChangesAsync(w);
        Assert.Equal(1, store.LastSave.JobWrites);
        Assert.Equal(ItemState.Failed, store.LoadJob(w.Id, existing.Id)!.Items[0].State);
        await store.SaveChangesAsync(w);
        Assert.True(store.LastSave.WroteNothing);
    }

    [Fact]
    public async Task Changing_conflicts_writes_the_metadata_row_only()
    {
        Routes.Conflicts.Add(new(Routes.Draft[0].LocalId, "Callsign", "a", "b", "c"));
        await store.SaveChangesAsync(w);
        var writes = Writes();
        Assert.Equal(1, Count(writes, "resource_metadata"));
        Assert.Equal(1, writes.Values.Sum());
        Routes.Conflicts.Clear();
        await store.SaveChangesAsync(w);
        Assert.Equal(1, Count(Writes(), "resource_metadata"));
        Assert.Empty(store.Load(w.Id).Resources[ResourceKind.Routes].Conflicts);
    }

    [Fact]
    public async Task Resolving_many_conflicts_in_memory_costs_one_save()
    {
        for (var n = 0; n < 100; n++)
            Routes.Conflicts.Add(new(Routes.Draft[n].LocalId, "Callsign", "o", "l", "r"));
        await store.SaveChangesAsync(w);
        Writes();
        var revision = w.StorageRevision;
        foreach (var conflict in Routes.Conflicts.ToArray())
        {
            var row = Routes.FindDraftRow(conflict.RowId)!;
            var copy = row.Copy();
            copy.Fields["Callsign"] = conflict.Remote!;
            Routes.Edit([row.LocalId], () => Routes.ReplaceDraftRows([copy]), newStep: false);
            Routes.Conflicts.Remove(conflict);
        }
        await store.SaveChangesAsync(w);
        Assert.Equal(revision + 1, w.StorageRevision);
        Assert.Equal(100, store.LastSave!.RowWrites);
        Assert.Equal(100, Count(Writes(), "resource_rows"));
    }

    [Fact]
    public async Task Connection_settings_write_the_header_and_the_secret_without_any_row()
    {
        store.SaveConnectionSettings(w, "client", "secret", "", true);
        var writes = Writes();
        Assert.Equal(1, Count(writes, "workspaces"));
        Assert.False(writes.ContainsKey("resource_rows") || writes.ContainsKey("batch_jobs") || writes.ContainsKey("batch_items") || writes.ContainsKey("resource_metadata"));
        Assert.Equal("client", store.Load(w.Id).ClientId);
        Assert.Equal(RunMode.Offline, w.Mode);
        Assert.Equal("client", w.ClientId);
        await store.SaveChangesAsync(w);
        Assert.True(store.LastSave!.WroteNothing);
    }

    [Fact]
    public async Task A_new_workspace_is_written_in_full_and_then_incrementally()
    {
        var fresh = new Workspace { Name = "New" };
        await store.SaveChangesAsync(fresh);
        Assert.Equal("Full", store.LastSave!.Kind);
        Assert.Equal("New", store.Load(fresh.Id).Name);
        fresh.Name = "Renamed";
        await store.SaveChangesAsync(fresh);
        Assert.Equal("Journal", store.LastSave.Kind);
        Assert.True(store.LastSave.HeaderWritten);
        Assert.Equal("Renamed", store.Load(fresh.Id).Name);
    }

    [Fact]
    public async Task A_loaded_workspace_is_trusted_and_a_workspace_changed_by_another_writer_is_saved_in_full()
    {
        var loaded = store.Load(w.Id);
        Assert.True(loaded.Journal().IsTracking(loaded.StorageRevision));
        var copy = loaded.Resources[ResourceKind.Routes].Draft[3].Copy();
        copy.Fields["Remarks"] = "loaded";
        loaded.Resources[ResourceKind.Routes].ReplaceDraftRows([copy]);
        await store.SaveChangesAsync(loaded);
        Assert.Equal("Journal", store.LastSave!.Kind);
        Assert.Equal(1, store.LastSave.RowWrites);

        // A full save of another object for the same workspace (as an API refresh does) leaves this one distrusted.
        var other = store.Load(w.Id);
        await store.SaveAsync(other);
        Assert.False(loaded.Journal().IsTracking(other.StorageRevision));
        var again = loaded.Resources[ResourceKind.Routes].Draft[4].Copy();
        again.Fields["Remarks"] = "stale";
        loaded.Resources[ResourceKind.Routes].ReplaceDraftRows([again]);
        var error = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => store.SaveChangesAsync(loaded));
        Assert.Equal("StorageStale", MessageErrors.Describe(error).Code);
    }

    [Fact]
    public async Task A_task_checkpoint_keeps_the_journal_valid_and_only_its_job_is_written_again()
    {
        var job = w.Jobs[0];
        var item = job.Items[0];
        item.State = ItemState.Running;
        await store.CheckpointWriter(w).WriteAsync(new(CheckpointKind.Task, job, item), default);
        Writes();
        await store.SaveChangesAsync(w);
        var report = store.LastSave!;
        Assert.Equal("Journal", report.Kind);
        Assert.Equal(1, report.JobWrites);
        Assert.Equal(0, report.RowWrites);
        Assert.False(Writes().ContainsKey("resource_rows"));
    }

    [Fact]
    public async Task A_rebase_checkpoint_marks_the_rows_of_its_resource_for_rewriting()
    {
        var job = w.Jobs[0];
        var item = job.Items[0];
        var updated = Routes.Draft[0].Copy();
        updated.Fields["Callsign"] = "REBASED";
        item.Resource = ResourceKind.Routes;
        item.After = updated;
        var delta = new ResourceRebaseDelta(ResourceKind.Routes, updated.LocalId, new(RowMutationKind.Upsert, updated), new(RowMutationKind.Upsert, updated), DateTimeOffset.UnixEpoch, false, null);
        item.State = ItemState.Succeeded;
        item.Rebased = true;
        Routes.Snapshot.RemoveAll(r => r.LocalId == updated.LocalId);
        Routes.Snapshot.Add(updated);
        Routes.ReplaceDraftRows([updated]);
        Routes.SnapshotAt = DateTimeOffset.UnixEpoch;
        await store.CheckpointWriter(w).WriteAsync(new(CheckpointKind.Rebase, job, item, null, delta), default);
        await store.SaveChangesAsync(w);
        Assert.Equal("Journal", store.LastSave!.Kind);
        Assert.Equal(600, store.LastSave.RowWrites); // both lists of the resource, positions repaired
        var loaded = store.Load(w.Id);
        Assert.Equal("REBASED", loaded.Resources[ResourceKind.Routes].Draft[0].Get("Callsign"));
        Assert.Equal(Routes.Snapshot.Select(r => r.LocalId), loaded.Resources[ResourceKind.Routes].Snapshot.Select(r => r.LocalId));
    }

    // ---- failure paths --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_injected_failure_leaves_database_and_memory_unchanged_and_the_next_save_writes_everything()
    {
        EditRows(20);
        var before = StoredState.Dump(dir.Path, withRevision: true);
        var memory = w.Serialize();
        var revision = w.StorageRevision;
        store.Barrier = point => { if (point == "Journal:BeforeCommit") throw new IOException("injected"); };
        await Assert.ThrowsAsync<IOException>(() => store.SaveChangesAsync(w));
        store.Barrier = null;
        Assert.Equal(before, StoredState.Dump(dir.Path, withRevision: true));
        Assert.Equal(memory, w.Serialize());
        Assert.Equal(revision, w.StorageRevision);
        // The edit is still there to be written, and the next save is a complete one.
        await store.SaveChangesAsync(w);
        Assert.Equal("Full", store.LastSave!.Kind);
        Assert.Equal(store.Load(w.Id).Serialize(), w.Serialize());
        Assert.True(w.Journal().IsTracking(w.StorageRevision));
        // And after that the journal works again.
        EditRows(2, "again", skip: 100);
        await store.SaveChangesAsync(w);
        Assert.Equal("Journal", store.LastSave.Kind);
        Assert.Equal(2, store.LastSave.RowWrites);
    }

    [Fact]
    public async Task A_failure_after_the_commit_is_reported_and_the_stale_copy_cannot_overwrite_the_database()
    {
        EditRows(3);
        store.Barrier = point => { if (point == "Journal:AfterCommit") throw new IOException("injected"); };
        await Assert.ThrowsAsync<IOException>(() => store.SaveChangesAsync(w));
        store.Barrier = null;
        // The database moved on (the commit happened) but this copy never learned the new revision.
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => store.SaveChangesAsync(w));
        var persisted = store.Load(w.Id);
        Assert.Equal("batch", persisted.Resources[ResourceKind.Routes].Draft[10].Get("Remarks"));
    }

    [Fact]
    public async Task A_stale_copy_is_rejected_by_a_journal_save_and_nothing_is_written()
    {
        var first = store.Load(w.Id);
        var second = store.Load(w.Id);
        var copy = first.Resources[ResourceKind.Routes].Draft[0].Copy();
        copy.Fields["Remarks"] = "first";
        first.Resources[ResourceKind.Routes].ReplaceDraftRows([copy]);
        await store.SaveChangesAsync(first);

        var other = second.Resources[ResourceKind.Routes].Draft[1].Copy();
        other.Fields["Remarks"] = "second";
        second.Resources[ResourceKind.Routes].ReplaceDraftRows([other]);
        var before = StoredState.Dump(dir.Path, withRevision: true);
        var error = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => store.SaveChangesAsync(second));
        Assert.Equal("StorageStale", MessageErrors.Describe(error).Code);
        Assert.Equal(before, StoredState.Dump(dir.Path, withRevision: true));
        Assert.Equal("first", store.Load(w.Id).Resources[ResourceKind.Routes].Draft[0].Get("Remarks"));
        Assert.Equal("", store.Load(w.Id).Resources[ResourceKind.Routes].Draft[1].Get("Remarks"));
    }

    [Fact]
    public async Task A_database_that_does_not_hold_what_the_journal_assumes_is_repaired_by_a_full_save()
    {
        // Someone removed a stored row behind the journal's back (revision untouched): the incremental write finds no row to update.
        using (var c = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = Path.Combine(dir.Path, "workspaces.db"), Pooling = false }.ToString()))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "DELETE FROM resource_rows WHERE resource=$r AND collection=1 AND local_id=$id";
            cmd.Parameters.AddWithValue("$r", (int)ResourceKind.Routes);
            cmd.Parameters.AddWithValue("$id", Routes.Draft[10].LocalId.ToString());
            cmd.ExecuteNonQuery();
        }
        EditRows(1);
        await store.SaveChangesAsync(w);
        Assert.Equal("Full", store.LastSave!.Kind);
        Assert.Equal(store.Load(w.Id).Serialize(), w.Serialize());
    }

    // ---- targeted reads -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Targeted_reads_agree_with_a_full_load()
    {
        Routes.Conflicts.Add(new(Routes.Draft[0].LocalId, "Callsign", "a", "b", "c"));
        EditRows(3);
        w.Resources[ResourceKind.Fleets].Draft.Add(new DataRow { Fields = new() { ["ID"] = "1" } });
        w.Jobs.Add(new BatchJob { Items = [new ChangeItem(), new ChangeItem()] });
        await store.SaveChangesAsync(w);
        var loaded = store.Load(w.Id);
        var counts = store.CountRows(w.Id);
        Assert.Equal(w.Name, counts.Name);
        Assert.Equal(loaded.StorageRevision, counts.Revision);
        Assert.Equal(loaded.Jobs.Count, counts.Jobs);
        foreach (var (kind, data) in loaded.Resources)
        {
            var c = counts.Resources[kind];
            Assert.Equal((data.Snapshot.Count, data.Draft.Count, data.Conflicts.Count, data.Undo.Count + data.Redo.Count), (c.Snapshots, c.Drafts, c.Conflicts, c.HistorySteps));
        }
        foreach (var job in loaded.Jobs)
        {
            Assert.True(store.JobExists(w.Id, job.Id));
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(job), System.Text.Json.JsonSerializer.Serialize(store.LoadJob(w.Id, job.Id)));
        }
        Assert.False(store.JobExists(w.Id, Guid.NewGuid()));
        Assert.False(store.JobExists(Guid.NewGuid(), loaded.Jobs[0].Id));
        Assert.Null(store.LoadJob(w.Id, Guid.NewGuid()));
        Assert.Throws<KeyNotFoundException>(() => store.CountRows(Guid.NewGuid()));
        var preview = store.PreviewMaintenance(w.Id);
        Assert.Equal((counts.Snapshots, counts.Drafts, counts.Jobs, counts.Conflicts, counts.HistorySteps, counts.Revision, true),
            (preview.Snapshots, preview.Drafts, preview.Jobs, preview.Conflicts, preview.HistorySteps, preview.Revision, preview.CanClear));
    }
}
