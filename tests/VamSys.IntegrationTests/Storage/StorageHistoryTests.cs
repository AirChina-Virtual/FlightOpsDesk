using System.Diagnostics;
using System.Text.Json;
using VamSys.Core;
using VamSys.Infrastructure;
using VamSys.TestSupport;
using static VamSys.IntegrationTests.Storage.LegacyDatabases;

namespace VamSys.IntegrationTests.Storage;

/// <summary>
/// Edit history through the real store: a full-snapshot model (the algorithm before the step-based history) is kept beside the
/// workspace and compared after every edit, after saving and reloading, and after a JSON round trip; a same-cell continuation
/// is stored with its updated step while a running save keeps an immutable snapshot; a single-cell edit with its durable save
/// writes a bounded set of rows and steps whatever the size of the list.
/// </summary>
public class StorageHistoryTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    sealed class Oracle
    {
        public List<DataRow> Draft = [];
        public List<List<DataRow>> Undo = [], Redo = [];
        public void Checkpoint() { Undo.Add(Clone(Draft)); if (Undo.Count > 20) Undo.RemoveAt(0); Redo.Clear(); }
        public void Back() { if (Undo.Count == 0) return; Redo.Add(Draft); Draft = Undo[^1]; Undo.RemoveAt(Undo.Count - 1); }
        public void Forward() { if (Redo.Count == 0) return; Undo.Add(Draft); Draft = Redo[^1]; Redo.RemoveAt(Redo.Count - 1); }
    }

    static List<DataRow> Clone(IEnumerable<DataRow> rows) => rows.Select(r => r.Dup()).ToList();

    // Canonical comparison is independent of the RowImage equality and of dictionary iteration order.
    static string Rows(IEnumerable<DataRow> rows) => JsonSerializer.Serialize(rows.Select(r => new { r.LocalId, r.Identity, r.RawApiJson, Fields = r.Fields.OrderBy(p => p.Key, StringComparer.Ordinal) }));

    static void Match(ResourceData data, Oracle oracle, string context)
    {
        Assert.True(Rows(data.Draft) == Rows(oracle.Draft), context + ": draft");
        Assert.True(data.Undo.Count == oracle.Undo.Count && data.Redo.Count == oracle.Redo.Count, context + ": stacks");
        var probe = data.ShallowCopy();
        for (var n = oracle.Undo.Count - 1; n >= 0; n--)
        { probe.UndoEdit(); Assert.True(Rows(probe.Draft) == Rows(oracle.Undo[n]), context + ": undo " + n); }
        probe = data.ShallowCopy();
        for (var n = oracle.Redo.Count - 1; n >= 0; n--)
        { probe.RedoEdit(); Assert.True(Rows(probe.Draft) == Rows(oracle.Redo[n]), context + ": redo " + n); }
    }

    [Theory]
    [InlineData(73)]
    [InlineData(20260929)]
    [InlineData(9871)]
    public async Task A_full_snapshot_oracle_agrees_through_1000_mixed_edits_with_saves_reloads_and_JSON_round_trips(int seed)
    {
        using var dir = TempDataDirectory.Create("vamsys-hist-");
        var random = new Random(seed);
        var oracle = new Oracle();
        var w = new Workspace();
        var data = w.Resources[ResourceKind.Fleets];
        var store = new WorkspaceStore(dir.Path);
        store.Save(w);
        for (var turn = 0; turn < 1000; turn++)
        {
            var op = random.Next(10);
            var checkpoint = op != 2;
            if (op == 7) { data.UndoEdit(); oracle.Back(); }
            else if (op == 8) { data.RedoEdit(); oracle.Forward(); }
            else
            {
                if (checkpoint)
                    oracle.Checkpoint();
                if (oracle.Draft.Count == 0 || op == 3)
                {
                    var row = new DataRow { Fields = new Dictionary<string, string> { ["Name"] = "row " + turn, ["empty"] = "" }, Identity = new(turn.ToString(), "7"), RawApiJson = "{\"raw\":" + turn + "}" };
                    var position = random.Next(oracle.Draft.Count + 1);
                    data.Edit([row.LocalId], () => data.Draft.Insert(position, row.Dup()), newStep: checkpoint, structural: true);
                    oracle.Draft.Insert(position, row.Dup());
                }
                else if (op == 4)
                {
                    var position = random.Next(oracle.Draft.Count);
                    var id = oracle.Draft[position].LocalId;
                    data.Edit([id], () => data.Draft.RemoveAt(position), structural: true);
                    oracle.Draft.RemoveAt(position);
                }
                else if (op == 5)
                { data.Edit([], () => data.Draft.Reverse(), structural: true); oracle.Draft.Reverse(); }
                else if (op == 6)
                { data.Edit([], () => { }); }
                else
                {
                    var position = random.Next(oracle.Draft.Count);
                    var id = oracle.Draft[position].LocalId;
                    var field = turn % 2 == 0 ? "Name" : "empty";
                    var value = turn % 7 == 0 ? "" : "值" + turn;
                    void Change(List<DataRow> rows)
                    {
                        var b = rows[position].ToBuilder();
                        if (turn % 11 == 0) b.Remove(field); else b[field] = value;
                        if (turn % 13 == 0) { b.Identity = null; b.RawApiJson = null; }
                        rows[position] = b.ToRow();
                    }
                    data.Edit([id], () => Change(data.Draft), newStep: checkpoint);
                    Change(oracle.Draft);
                }
            }
            Match(data, oracle, $"seed {seed} turn {turn} op {op}");
            if (turn % 29 == 0)
            {
                await store.SaveAsync(w);
                w = store.Load(w.Id);
                data = w.Resources[ResourceKind.Fleets];
                Match(data, oracle, "reload");
                w = Workspace.Deserialize(w.Serialize());
                data = w.Resources[ResourceKind.Fleets];
                Match(data, oracle, "JSON roundtrip");
                // The old store also reconstructed dictionaries on reload (including deleted-key slots).
                oracle.Draft = Clone(oracle.Draft);
                oracle.Undo = oracle.Undo.Select(Clone).ToList();
                oracle.Redo = oracle.Redo.Select(Clone).ToList();
            }
        }
    }

    [Theory]
    [InlineData("same")]
    [InlineData("other")]
    [InlineData("undo")]
    [InlineData("close")]
    [InlineData("failure")]
    public async Task A_same_cell_continuation_is_stored_with_its_updated_step_and_a_running_save_keeps_an_immutable_snapshot(string next)
    {
        using var dir = TempDataDirectory.Create("vamsys-hist-");
        var store = new WorkspaceStore(dir.Path);
        var w = Fixture();
        var d = w.Resources[ResourceKind.Fleets];
        var row = d.Draft[0];
        var initial = row.Get("Name");
        store.Save(w);
        d.Edit([row.LocalId], () => d.ReplaceDraftRows([row.With("Name", "first")]));
        var edits = new DraftEdits();
        edits.Cell(w, ResourceKind.Fleets, row.LocalId);
        var taken = edits.Take();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        store.Barrier = p =>
        {
            if (p == "Draft:BeforeCommit")
            {
                entered.Set();
                if (!release.Wait(10000)) throw new TimeoutException();
                if (next == "failure") throw new IOException("injected");
            }
        };
        var saving = store.TrySaveDraftRowsAsync(w, taken.Changes);
        Assert.True(entered.Wait(10000, Ct));
        try
        {
            if (next == "undo")
                d.UndoEdit();
            else
            {
                d.Edit([row.LocalId], () => d.ReplaceDraftRows([row.With(next == "other" ? "extra" : "Name", "second")]), newStep: next == "other");
                edits.Cell(w, ResourceKind.Fleets, row.LocalId);
            }
        }
        finally { release.Set(); }
        if (next == "failure")
        {
            await Assert.ThrowsAsync<IOException>(() => saving);
            edits.Restore(taken);
            Assert.True(edits.RequiresFullSave);
        }
        else
        {
            Assert.True(await saving);
            var first = store.Load(w.Id);
            Assert.Equal("first", first.Resources[ResourceKind.Fleets].Draft[0].Get("Name")); // the save did not capture the later mutation
            first.Resources[ResourceKind.Fleets].UndoEdit();
            Assert.Equal(initial, first.Resources[ResourceKind.Fleets].Draft[0].Get("Name"));
        }
        store.Barrier = null;
        if (next is "undo" or "close" or "failure")
            await store.SaveAsync(w);
        else
            Assert.True(await store.TrySaveDraftRowsAsync(w, edits.Take().Changes));
        var disk = store.Load(w.Id);
        Assert.True(disk.Serialize() == w.Serialize(), "a later edit was lost: " + next);
        if (next == "same")
        {
            var dd = disk.Resources[ResourceKind.Fleets];
            dd.UndoEdit();
            Assert.Equal(initial, dd.Draft[0].Get("Name"));
            dd.RedoEdit();
            Assert.Equal("second", dd.Draft[0].Get("Name"));
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1000)]
    [InlineData(20000)]
    public async Task A_single_cell_edit_with_its_durable_save_writes_a_bounded_set_whatever_the_list_size_and_evicts_and_discards_steps(int count)
    {
        using var dir = TempDataDirectory.Create("vamsys-hist-");
        var w = new Workspace();
        var d = w.Resources[ResourceKind.Fleets];
        d.Draft = Enumerable.Range(0, count).Select(n => new DataRow { Fields = new Dictionary<string, string> { ["Name"] = "row", ["ID"] = n.ToString() } }).ToList();
        var store = new WorkspaceStore(dir.Path);
        store.Save(w);
        d.FindDraftRow(d.Draft[0].LocalId);
        var samples = new List<double>();
        using var c = Open(dir.Path);
        Sql(c, "CREATE TABLE touched(tbl TEXT,id TEXT);CREATE TRIGGER row_u AFTER UPDATE ON resource_rows BEGIN INSERT INTO touched VALUES('row',NEW.local_id);END;CREATE TRIGGER step_i AFTER INSERT ON resource_history_steps BEGIN INSERT INTO touched VALUES('insert',NEW.step_id);END;CREATE TRIGGER step_u AFTER UPDATE ON resource_history_steps BEGIN INSERT INTO touched VALUES('update',NEW.step_id);END;CREATE TRIGGER step_d AFTER DELETE ON resource_history_steps BEGIN INSERT INTO touched VALUES('delete',OLD.step_id);END;CREATE TRIGGER head_u AFTER UPDATE ON resource_history_state BEGIN INSERT INTO touched VALUES('head','');END;CREATE TRIGGER revision_u AFTER UPDATE ON workspace_revisions BEGIN INSERT INTO touched VALUES('revision','');END;");
        for (var n = 0; n < 30; n++)
        {
            Sql(c, "DELETE FROM touched");
            using var metrics = PerformanceRun.Begin(w.Id, Guid.NewGuid());
            var watch = Stopwatch.StartNew();
            var row = d.Draft[n % count];
            var evicted = d.Undo.Count == 20 ? d.Undo[0].StepId : (Guid?)null;
            d.Edit([row.LocalId], () => d.ReplaceDraftRows([row.With("Name", "edit" + n)]));
            var edits = new DraftEdits();
            edits.Cell(w, ResourceKind.Fleets, row.LocalId);
            Assert.True(await store.TrySaveDraftRowsAsync(w, edits.Changes));
            if (n >= 10)
                samples.Add(watch.Elapsed.TotalMilliseconds);
            Assert.True(metrics.JsonBytes <= 8192, "the history payload grew with the draft size");
            Assert.Equal(evicted == null ? 4 : 5, Number(c, "SELECT count(*) FROM touched")); // row, new step, head, revision (+ the evicted step)
            Assert.Equal(0, Number(c, "SELECT count(*) FROM touched WHERE tbl='update'")); // an unchanged step is never rewritten
            if (evicted != null)
                Assert.Equal(evicted.ToString(), Text(c, "SELECT id FROM touched WHERE tbl='delete'"));
            Assert.True(d.Undo.Sum(s => s.Rows.Sum(r => (r.Before == null ? 0 : 1) + (r.After == null ? 0 : 1))) <= 40 && d.Undo.All(s => s.BeforeOrder == null && s.AfterOrder == null), "a step keeps more than its changed row images");
        }
        var disk = store.Load(w.Id);
        Assert.Equal(w.Serialize(), disk.Serialize());
        d.UndoEdit();
        d.UndoEdit();
        store.Save(w);
        var discarded = d.Redo.Select(s => s.StepId).ToArray();
        var current = d.Draft[0];
        d.Edit([current.LocalId], () => d.ReplaceDraftRows([current.With("Name", "branch")]));
        var pending = new DraftEdits();
        pending.Cell(w, ResourceKind.Fleets, current.LocalId);
        Assert.True(await store.TrySaveDraftRowsAsync(w, pending.Changes));
        foreach (var id in discarded)
            Assert.Equal(0, Number(c, "SELECT count(*) FROM resource_history_steps WHERE step_id=$id", ("$id", id.ToString())));
        Assert.Empty(store.Load(w.Id).Resources[ResourceKind.Fleets].Redo);
        samples.Sort();
        var p95 = samples[(int)Math.Ceiling(samples.Count * .95) - 1];
        Assert.True(p95 <= 500, $"single-cell edit with its durable save is slow: P95 {p95:F1} ms (the budget is 100 ms on a quiet machine; 500 ms here so that a busy test machine does not fail it)");
    }
}
