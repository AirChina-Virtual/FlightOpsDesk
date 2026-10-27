using VamSys.Core;
using VamSys.Infrastructure;
using VamSys.TestSupport;
using static VamSys.IntegrationTests.Storage.LegacyDatabases;

namespace VamSys.IntegrationTests.Storage;

/// <summary>
/// The row-level save used by autosave and by undo/redo of replaced rows (WorkspaceStore.TrySaveDraftRowsAsync), counted by
/// triggers on the database: only the edited rows and, when a step was added, that resource's history are written, and the
/// database equals memory afterwards. Also the close path: the snapshot taken when closing starts is the last accepted edit.
/// </summary>
public class StorageRowSaveTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;
    const string Touched = "CREATE TABLE touched(tbl TEXT,resource INTEGER,local_id TEXT);"
        + "CREATE TRIGGER row_i AFTER INSERT ON resource_rows BEGIN INSERT INTO touched VALUES('rows',NEW.resource,NEW.local_id); END;"
        + "CREATE TRIGGER row_u AFTER UPDATE ON resource_rows BEGIN INSERT INTO touched VALUES('rows',NEW.resource,NEW.local_id); END;"
        + "CREATE TRIGGER row_d AFTER DELETE ON resource_rows BEGIN INSERT INTO touched VALUES('rows',OLD.resource,OLD.local_id); END;"
        + "CREATE TRIGGER history_u AFTER UPDATE ON resource_history_state BEGIN INSERT INTO touched VALUES('history',NEW.resource,NULL); END;"
        + "CREATE TRIGGER step_i AFTER INSERT ON resource_history_steps BEGIN INSERT INTO touched VALUES('step',NEW.resource,NEW.step_id); END;"
        + "CREATE TRIGGER step_u AFTER UPDATE ON resource_history_steps BEGIN INSERT INTO touched VALUES('step',NEW.resource,NEW.step_id); END;"
        + "CREATE TRIGGER step_d AFTER DELETE ON resource_history_steps BEGIN INSERT INTO touched VALUES('step',OLD.resource,OLD.step_id); END;"
        + "CREATE TRIGGER meta_u AFTER UPDATE ON resource_metadata BEGIN INSERT INTO touched VALUES('meta',NEW.resource,NULL); END;"
        + "CREATE TRIGGER jobs_u AFTER UPDATE ON batch_jobs BEGIN INSERT INTO touched VALUES('jobs',NULL,NULL); END;"
        + "CREATE TRIGGER jobs_d AFTER DELETE ON batch_jobs BEGIN INSERT INTO touched VALUES('jobs',NULL,NULL); END;"
        + "CREATE TRIGGER root_u AFTER UPDATE ON workspaces BEGIN INSERT INTO touched VALUES('root',NULL,NULL); END;";

    static Workspace Large(int rows, int undo)
    {
        var w = Fixture();
        var data = w.Resources[ResourceKind.Routes];
        for (var n = 0; n < rows; n++)
        {
            var row = new DataRow { Fields = new Dictionary<string, string> { ["ID"] = n.ToString(), ["Flight Number"] = "AC" + n, ["Callsign"] = "ACA123" } };
            data.Snapshot.Add(row.Dup());
            data.Draft.Add(row);
        }
        for (var n = 0; n < undo; n++)
        {
            var row = data.Draft[n % rows];
            data.Edit([row.LocalId], () => data.ReplaceDraftRows([row.With("Callsign", "HISTORY" + n)]));
        }
        return w;
    }

    [Fact]
    public async Task Edited_cells_save_only_their_rows_and_when_a_step_was_added_that_resources_history_and_the_reload_equals_memory()
    {
        using var dir = TempDataDirectory.Create("vamsys-rows-");
        var store = new WorkspaceStore(dir.Path);
        var w = Large(20000, 19);
        store.Save(w);
        using (var c = Open(dir.Path))
            Sql(c, Touched);
        var data = w.Resources[ResourceKind.Routes];
        var edits = new DraftEdits();
        data.Edit([data.Draft[10].LocalId], () => data.ReplaceDraftRows([data.Draft[10].With("Flight Number", "EDIT1")]), newStep: false);
        edits.Cell(w, ResourceKind.Routes, data.Draft[10].LocalId);
        data.Edit([data.Draft[19999].LocalId], () => data.ReplaceDraftRows([data.Draft[19999].With("Callsign", "EDIT2")]), newStep: false);
        edits.Cell(w, ResourceKind.Routes, data.Draft[19999].LocalId);
        long rowBytes;
        using (var run = PerformanceRun.Begin(w.Id, Guid.NewGuid()))
        {
            Assert.True(await store.TrySaveDraftRowsAsync(w, edits.Changes));
            rowBytes = run.JsonBytes;
            Assert.Equal(1, run.Checkpoints["Draft"]);
        }
        using (var c = Open(dir.Path))
        {
            Assert.Equal(3, Number(c, "SELECT count(*) FROM touched")); // two rows and the revision-bearing history state
            Assert.Equal(2, Number(c, "SELECT count(*) FROM touched WHERE tbl='rows' AND resource=$r AND local_id IN ($a,$b)", ("$r", (int)ResourceKind.Routes), ("$a", data.Draft[10].LocalId.ToString()), ("$b", data.Draft[19999].LocalId.ToString())));
            Sql(c, "DELETE FROM touched");
        }
        Assert.Equal(w.Serialize(), store.Load(w.Id).Serialize());

        // A new undo step writes that resource's history too, and nothing else.
        data.Edit([data.Draft[5].LocalId], () => data.ReplaceDraftRows([data.Draft[5].With("Flight Number", "EDIT3")]));
        edits.Take();
        edits.Cell(w, ResourceKind.Routes, data.Draft[5].LocalId);
        long historyBytes;
        using (var run = PerformanceRun.Begin(w.Id, Guid.NewGuid()))
        {
            Assert.True(await store.TrySaveDraftRowsAsync(w, edits.Changes));
            historyBytes = run.JsonBytes;
        }
        using (var c = Open(dir.Path))
        {
            Assert.Equal(3, Number(c, "SELECT count(*) FROM touched"));
            Assert.Equal(1, Number(c, "SELECT count(*) FROM touched WHERE tbl='history' AND resource=$r", ("$r", (int)ResourceKind.Routes)));
        }
        var disk = store.Load(w.Id);
        Assert.Equal(w.Serialize(), disk.Serialize());
        Assert.Equal(20, disk.Resources[ResourceKind.Routes].Undo.Count);
        Assert.True(rowBytes < 4096 && historyBytes < 8192, $"the row save serialized unrelated data: {rowBytes} B / {historyBytes} B");
    }

    [Fact]
    public async Task Undo_and_redo_of_replaced_rows_save_only_those_rows_and_the_stack_position_while_structural_steps_ask_for_a_full_save()
    {
        using var dir = TempDataDirectory.Create("vamsys-rows-");
        var store = new WorkspaceStore(dir.Path);
        var w = Large(20000, 19);
        store.Save(w);
        using (var c = Open(dir.Path))
            Sql(c, Touched);
        var data = w.Resources[ResourceKind.Routes];
        var edits = new DraftEdits();
        foreach (var undo in new[] { true, true, false })
        {
            var move = undo ? data.UndoEdit() : data.RedoEdit();
            Assert.True(move.Moved && move.ReplacedRows is { Count: 1 }, "a replaced-row step must report its row");
            edits.Rows(w, ResourceKind.Routes, move.ReplacedRows!);
            var taken = edits.Take();
            long bytes;
            using (var run = PerformanceRun.Begin(w.Id, Guid.NewGuid()))
            {
                Assert.True(await store.TrySaveDraftRowsAsync(w, taken.Changes), "row save refused");
                bytes = run.JsonBytes;
                Assert.Equal(1, run.Checkpoints["Draft"]);
                Assert.False(run.Checkpoints.ContainsKey("Full"));
            }
            using (var c = Open(dir.Path))
            {
                Assert.Equal(2, Number(c, "SELECT count(*) FROM touched")); // one row and the undo/redo position
                Assert.Equal(1, Number(c, "SELECT count(*) FROM touched WHERE tbl='rows' AND local_id=$id", ("$id", move.ReplacedRows![0].ToString())));
                Assert.Equal(1, Number(c, "SELECT count(*) FROM touched WHERE tbl='history'"));
                Sql(c, "DELETE FROM touched");
            }
            Assert.True(bytes < 4096, "undo/redo serialized unrelated data");
            Assert.Equal(w.Serialize(), store.Load(w.Id).Serialize());
        }
        Assert.Equal((18, 1), (data.Undo.Count, data.Redo.Count));

        // A new row is structural: no replaced rows, so the caller saves everything.
        var added = new DataRow { Fields = new Dictionary<string, string> { ["ID"] = "new" } };
        data.Edit([added.LocalId], () => data.Draft.Add(added), structural: true);
        var structural = data.UndoEdit();
        Assert.True(structural.Moved);
        Assert.Null(structural.ReplacedRows);
        data.RedoEdit();
        store.Save(w);
        while (data.Redo.Count > 0)
            data.RedoEdit();
        var nothing = data.RedoEdit();
        Assert.False(nothing.Moved);
        Assert.Empty(nothing.ReplacedRows!);
    }

    [Fact]
    public async Task A_row_save_refuses_a_stale_revision_and_a_missing_row_without_writing()
    {
        using var dir = TempDataDirectory.Create("vamsys-rows-");
        var store = new WorkspaceStore(dir.Path);
        var w = Fixture();
        store.Save(w);
        var other = store.Load(w.Id);
        other.Name = "changed elsewhere";
        store.Save(other);
        var fleetDraft = w.Resources[ResourceKind.Fleets].Draft;
        var row = fleetDraft[0].With("Name", "stale edit");
        fleetDraft[0] = row;
        var edits = new DraftEdits();
        edits.Cell(w, ResourceKind.Fleets, row.LocalId);
        var revision = w.StorageRevision;

        var error = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => store.TrySaveDraftRowsAsync(w, edits.Changes));
        Assert.Equal("StorageStale", MessageErrors.Describe(error).Code);
        Assert.Equal(revision, w.StorageRevision);
        Assert.NotEqual("stale edit", store.Load(w.Id).Resources[ResourceKind.Fleets].Draft[0].Get("Name"));

        var fresh = store.Load(w.Id);
        var before = fresh.Serialize();
        var missing = new DraftEdits();
        missing.Cell(fresh, ResourceKind.Fleets, Guid.NewGuid());
        Assert.False(await store.TrySaveDraftRowsAsync(fresh, missing.Changes));
        Assert.Equal(before, store.Load(w.Id).Serialize());
    }

    [Fact]
    public async Task Closing_freezes_the_edits_before_the_database_is_written_and_what_was_accepted_last_is_what_is_stored()
    {
        using var dir = TempDataDirectory.Create("vamsys-rows-");
        var store = new WorkspaceStore(dir.Path);
        var w = Fixture();
        store.Save(w);
        var row = w.Resources[ResourceKind.Fleets].Draft[0];
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        store.Barrier = p => { if (p == "Full:BeforeCommit") { entered.Set(); if (!release.Wait(10000)) throw new TimeoutException(); } };
        var close = new WindowCloseController();
        var fleetDraft = w.Resources[ResourceKind.Fleets].Draft;
        fleetDraft[0] = row.With("Name", "accepted");

        var closing = close.RequestAsync(false, () => store.SaveAsync(w), _ => { });

        Assert.True(entered.Wait(10000, Ct));
        if (close.IsOpen)
            fleetDraft[0] = fleetDraft[0].With("Name", "must not enter"); // the window is frozen, so this edit cannot happen
        release.Set();
        Assert.True(await closing);
        Assert.Equal("accepted", store.Load(w.Id).Resources[ResourceKind.Fleets].Draft[0].Get("Name"));
    }
}
