using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using VamSys.Core;
using static VamSys.UnitTests.Domain.TestRows;

namespace VamSys.UnitTests.Domain;

/// <summary>
/// F07: undo / redo (20 steps, same-cell continuation merges, row order restored, replaced-row moves) and the persisted
/// shape of the history (I05, I08: legacy full-snapshot histories still load; unknown versions are refused).
/// Every edit here replaces rows (see TestRows), so the tests hold in Debug (frozen rows) and Release alike.
/// </summary>
public class EditHistoryTests
{
    static ResourceData Fleets(int count)
    {
        var rows = Enumerable.Range(0, count).Select(i => Row(("ID", i.ToString()), ("Name", "row " + i), ("Remarks", "r"))).ToList();
        return new() { Snapshot = rows.Select(r => r.Dup()).ToList(), Draft = rows };
    }

    static string[] Names(ResourceData data) => [.. data.Draft.Select(r => r.Get("Name"))];

    // ---- undo / redo basics ----

    [Fact]
    public void Undo_and_redo_walk_back_and_forth_through_cell_edits()
    {
        var data = Fleets(2);
        var id = data.Draft[0].LocalId;
        data.Change(id, "Name", "one");
        data.Change(id, "Name", "two");

        Assert.Equal(["two", "row 1"], Names(data));
        Assert.True(data.UndoEdit().Moved);
        Assert.Equal(["one", "row 1"], Names(data));
        Assert.True(data.UndoEdit().Moved);
        Assert.Equal(["row 0", "row 1"], Names(data));
        Assert.False(data.UndoEdit().Moved);
        Assert.True(data.RedoEdit().Moved);
        Assert.True(data.RedoEdit().Moved);
        Assert.Equal(["two", "row 1"], Names(data));
        Assert.False(data.RedoEdit().Moved);
    }

    [Fact]
    public void A_new_edit_after_undo_discards_the_redo_steps()
    {
        var data = Fleets(1);
        var id = data.Draft[0].LocalId;
        data.Change(id, "Name", "a");
        data.Change(id, "Name", "b");
        data.UndoEdit();
        Assert.Single(data.Redo);

        data.Change(id, "Name", "c");

        Assert.Empty(data.Redo);
        Assert.False(data.RedoEdit().Moved);
        Assert.Equal(["c"], Names(data));
    }

    [Fact]
    public void History_keeps_at_most_twenty_steps_and_drops_the_oldest()
    {
        var data = Fleets(1);
        var id = data.Draft[0].LocalId;
        for (var i = 1; i <= 25; i++)
            data.Change(id, "Name", "v" + i);

        Assert.Equal(20, data.Undo.Count);
        for (var i = 0; i < 20; i++)
            Assert.True(data.UndoEdit().Moved);
        Assert.Equal(["v5"], Names(data));
        Assert.False(data.UndoEdit().Moved);
    }

    [Fact]
    public void Typing_in_the_same_cell_continues_the_last_step_so_one_undo_reverts_it_all()
    {
        var data = Fleets(1);
        var id = data.Draft[0].LocalId;
        data.Change(id, "Name", "a");
        data.Change(id, "Name", "ab", newStep: false);
        data.Change(id, "Name", "abc", newStep: false);

        Assert.Single(data.Undo);
        data.UndoEdit();
        Assert.Equal(["row 0"], Names(data));
        data.RedoEdit();
        Assert.Equal(["abc"], Names(data));
    }

    [Fact]
    public void A_continuation_with_nothing_to_continue_changes_the_row_but_records_no_step()
    {
        var data = Fleets(1);

        data.Change(data.Draft[0].LocalId, "Name", "x", newStep: false);

        Assert.Empty(data.Undo);
        Assert.Equal(["x"], Names(data));
    }

    [Fact]
    public void An_edit_that_throws_records_no_step()
    {
        var data = Fleets(1);

        Assert.Throws<InvalidOperationException>(() => data.Edit([data.Draft[0].LocalId], () => throw new InvalidOperationException("boom")));

        Assert.Empty(data.Undo);
    }

    [Fact]
    public void An_edit_that_changes_nothing_adds_an_empty_step_and_undoing_it_changes_nothing()
    {
        var data = Fleets(2);
        var before = Snapshot(data.Draft);

        data.Edit([], () => { });

        Assert.Single(data.Undo);
        Assert.Empty(data.Undo[0].Rows);
        data.UndoEdit();
        Assert.Equal(before, Snapshot(data.Draft));
    }

    // ---- row order and structure ----

    [Theory]
    [InlineData("reverse")]
    [InlineData("insert-first")]
    [InlineData("insert-middle")]
    [InlineData("insert-last")]
    [InlineData("delete-first")]
    [InlineData("delete-middle")]
    [InlineData("delete-last")]
    public void Undo_restores_the_exact_row_order_and_redo_reapplies_it(string change)
    {
        var data = Fleets(4);
        var original = data.Draft.Select(r => r.LocalId).ToArray();
        var added = Row(("ID", "new"), ("Name", "added"));
        switch (change)
        {
            case "reverse": data.Edit([], () => data.Draft.Reverse(), structural: true); break;
            case "insert-first": data.Edit([added.LocalId], () => data.Draft.Insert(0, added), structural: true); break;
            case "insert-middle": data.Edit([added.LocalId], () => data.Draft.Insert(2, added), structural: true); break;
            case "insert-last": data.Edit([added.LocalId], () => data.Draft.Add(added), structural: true); break;
            case "delete-first": data.Edit([original[0]], () => data.Draft.RemoveAt(0), structural: true); break;
            case "delete-middle": data.Edit([original[2]], () => data.Draft.RemoveAt(2), structural: true); break;
            default: data.Edit([original[3]], () => data.Draft.RemoveAt(3), structural: true); break;
        }
        var changed = data.Draft.Select(r => r.LocalId).ToArray();
        var changedRows = Snapshot(data.Draft);
        Assert.NotEqual(original, changed);

        var undo = data.UndoEdit();
        Assert.True(undo.Moved);
        Assert.Null(undo.ReplacedRows);
        Assert.Equal(original, data.Draft.Select(r => r.LocalId));
        Assert.Equal("row 0", data.Draft.First(r => r.LocalId == original[0]).Get("Name"));

        Assert.True(data.RedoEdit().Moved);
        Assert.Equal(changed, data.Draft.Select(r => r.LocalId));
        Assert.Equal(changedRows, Snapshot(data.Draft));
    }

    [Fact]
    public void Undo_after_a_reverse_and_a_cell_edit_unwinds_each_in_order()
    {
        var data = Fleets(3);
        data.Edit([], () => data.Draft.Reverse(), structural: true);
        data.Change(data.Draft[0].LocalId, "Name", "edited");

        data.UndoEdit();
        Assert.Equal(["row 2", "row 1", "row 0"], Names(data));
        data.UndoEdit();
        Assert.Equal(["row 0", "row 1", "row 2"], Names(data));
    }

    [Fact]
    public void A_row_replaced_in_place_reports_its_id_and_a_structural_step_reports_none()
    {
        var data = Fleets(3);
        var a = data.Draft[0].LocalId;
        var b = data.Draft[2].LocalId;
        data.Edit([a, b], () => data.ReplaceDraftRows([data.Draft[0].With("Name", "A"), data.Draft[2].With("Name", "B")]));

        var undo = data.UndoEdit();

        Assert.True(undo.Moved);
        Assert.Equal(new[] { a, b }.Order(), undo.ReplacedRows!.Order());
        Assert.Equal([], data.RedoEdit().ReplacedRows!.Except([a, b]));
    }

    [Fact]
    public void Nothing_to_undo_is_reported_as_not_moved_with_no_rows()
    {
        var move = Fleets(1).UndoEdit();

        Assert.False(move.Moved);
        Assert.Empty(move.ReplacedRows!);
    }

    [Fact]
    public void Removing_a_row_without_a_new_step_amends_both_neighbouring_steps()
    {
        // A row that disappears on refresh must not break the stacks: undo brings it back, redo removes it again.
        var data = Fleets(2);
        var first = data.Draft[0].LocalId;
        data.Change(first, "Name", "first");
        data.Change(first, "Name", "second");
        data.UndoEdit();
        var second = data.Draft[1].LocalId;

        data.Edit([second], () => data.Draft.RemoveAt(1), newStep: false, structural: true);

        Assert.Equal(["first"], Names(data));
        Assert.Single(data.Undo);
        Assert.Single(data.Redo);
        data.UndoEdit();
        Assert.Equal(["row 0", "row 1"], Names(data));
        data.RedoEdit();
        Assert.Equal(["first"], Names(data));
        data.RedoEdit();
        Assert.Equal(["second", "row 1"], Names(data));
    }

    // ---- randomized comparison with a full-snapshot model ----

    sealed class Model
    {
        public List<DataRow> Draft = [];
        public List<List<DataRow>> Undo = [], Redo = [];
        public static List<DataRow> Clone(IEnumerable<DataRow> rows) => rows.Select(r => r.Dup()).ToList();
        public void Checkpoint() { Undo.Add(Clone(Draft)); if (Undo.Count > 20) Undo.RemoveAt(0); Redo.Clear(); }
        public void Back() { if (Undo.Count == 0) return; Redo.Add(Draft); Draft = Undo[^1]; Undo.RemoveAt(Undo.Count - 1); }
        public void Forward() { if (Redo.Count == 0) return; Undo.Add(Draft); Draft = Redo[^1]; Redo.RemoveAt(Redo.Count - 1); }
    }

    static void AssertMatches(ResourceData data, Model model, string context)
    {
        Assert.True(Snapshot(model.Draft) == Snapshot(data.Draft), context + ": draft");
        Assert.True(data.Undo.Count == model.Undo.Count && data.Redo.Count == model.Redo.Count, context + ": stacks");
        var probe = data.ShallowCopy();
        for (var n = model.Undo.Count - 1; n >= 0; n--)
        { probe.UndoEdit(); Assert.True(Snapshot(model.Undo[n]) == Snapshot(probe.Draft), context + ": undo " + n); }
        probe = data.ShallowCopy();
        for (var n = model.Redo.Count - 1; n >= 0; n--)
        { probe.RedoEdit(); Assert.True(Snapshot(model.Redo[n]) == Snapshot(probe.Draft), context + ": redo " + n); }
    }

    // The history is stored as per-row deltas; the model stores whole drafts per step. Both must agree after any mix of edits.
    [Theory]
    [InlineData(73)]
    [InlineData(20260929)]
    [InlineData(9871)]
    public void Delta_history_agrees_with_a_full_snapshot_model_across_mixed_edits_and_json_round_trips(int seed)
    {
        var random = new Random(seed);
        var model = new Model();
        var workspace = new Workspace();
        var data = workspace.Resources[ResourceKind.Fleets];
        for (var turn = 0; turn < 400; turn++)
        {
            var op = random.Next(10);
            var checkpoint = op != 2;
            if (op == 7) { data.UndoEdit(); model.Back(); }
            else if (op == 8) { data.RedoEdit(); model.Forward(); }
            else
            {
                if (checkpoint)
                    model.Checkpoint();
                if (model.Draft.Count == 0 || op == 3)
                {
                    var row = new DataRow { Fields = new Dictionary<string, string> { ["Name"] = "row " + turn, ["empty"] = "" }, Identity = new(turn.ToString(), "7"), RawApiJson = "{\"raw\":" + turn + "}" };
                    var position = random.Next(model.Draft.Count + 1);
                    data.Edit([row.LocalId], () => data.Draft.Insert(position, row.Dup()), newStep: checkpoint, structural: true);
                    model.Draft.Insert(position, row.Dup());
                }
                else if (op == 4)
                {
                    var position = random.Next(model.Draft.Count);
                    data.Edit([model.Draft[position].LocalId], () => data.Draft.RemoveAt(position), structural: true);
                    model.Draft.RemoveAt(position);
                }
                else if (op == 5)
                { data.Edit([], () => data.Draft.Reverse(), structural: true); model.Draft.Reverse(); }
                else if (op == 6)
                    data.Edit([], () => { });
                else
                {
                    var position = random.Next(model.Draft.Count);
                    var id = model.Draft[position].LocalId;
                    var field = turn % 2 == 0 ? "Name" : "empty";
                    var value = turn % 7 == 0 ? "" : "值" + turn;
                    DataRow Mutated(DataRow row)
                    {
                        var copy = turn % 11 == 0 ? row.Without(field) : row.With(field, value);
                        return turn % 13 == 0 ? copy.WithIdentity(null).WithRawApiJson(null) : copy;
                    }
                    data.Edit([id], () => data.ReplaceDraftRows([Mutated(data.Draft[position])]), newStep: checkpoint);
                    model.Draft[position] = Mutated(model.Draft[position]);
                }
            }
            AssertMatches(data, model, $"seed {seed} turn {turn} op {op}");
            if (turn % 29 == 0)
            {
                workspace = Workspace.Deserialize(workspace.Serialize());
                data = workspace.Resources[ResourceKind.Fleets];
                AssertMatches(data, model, $"seed {seed} turn {turn} after JSON round trip");
            }
        }
    }

    // ---- damaged or drifted history ----

    [Fact]
    public void A_step_that_no_longer_matches_the_draft_is_refused_without_applying_any_of_it()
    {
        var data = Fleets(2);
        var ids = data.Draft.Select(r => r.LocalId).ToArray();
        data.Edit(ids, () => data.ReplaceDraftRows(data.Draft.Select(r => r.With("Name", "new")).ToList()));
        data.Draft[1] = data.Draft[1].With("Name", "drifted outside the history");
        var before = Snapshot(data.Draft);

        Assert.Throws<InvalidDataException>(() => data.UndoEdit());

        Assert.Equal(before, Snapshot(data.Draft));
        Assert.Single(data.Undo);
        Assert.Empty(data.Redo);
    }

    [Fact]
    public void A_damaged_row_image_or_row_order_is_refused_before_any_row_or_stack_changes()
    {
        var data = Fleets(3);
        var ids = data.Draft.Take(2).Select(r => r.LocalId).ToArray();
        data.Edit(ids, () => data.ReplaceDraftRows(ids.Select(id => data.FindDraftRow(id)!.With("Remarks", "new")).ToList()));
        var step = data.Undo[0];
        var edits = step.Rows.ToArray();
        edits[1] = edits[1] with { Before = edits[1].Before! with { FieldOrder = ImmutableArray.Create("missing") } };
        data.History = EditHistory.From(new([step with { Rows = [.. edits] }], []));
        var before = Snapshot(data.Draft);
        var list = data.Draft;

        Assert.Throws<InvalidDataException>(() => data.UndoEdit());
        Assert.Equal(before, Snapshot(data.Draft));
        Assert.Same(list, data.Draft);
        Assert.Single(data.Undo);
        Assert.Empty(data.Redo);

        var structure = Fleets(3);
        var added = Row(("ID", "x"));
        structure.Edit([added.LocalId], () => structure.Draft.Add(added), structural: true);
        var order = structure.Undo[0].BeforeOrder!.Value.ToArray();
        order[1] = order[0];
        structure.History = EditHistory.From(new([structure.Undo[0] with { BeforeOrder = [.. order] }], []));
        var original = Snapshot(structure.Draft);
        var originalList = structure.Draft;

        Assert.Throws<InvalidDataException>(() => structure.UndoEdit());
        Assert.Equal(original, Snapshot(structure.Draft));
        Assert.Same(originalList, structure.Draft);
        Assert.Single(structure.Undo);
        Assert.Empty(structure.Redo);
    }

    [Fact]
    public void Validate_replays_both_directions_on_a_probe_and_leaves_the_live_rows_alone()
    {
        var data = Fleets(300);
        for (var i = 0; i < 20; i++)
            data.Change(data.Draft[i].LocalId, "Remarks", "edit " + i);
        for (var i = 0; i < 5; i++)
            data.UndoEdit();
        var rows = data.Draft.ToArray();
        var before = Snapshot(data.Draft);

        data.History.Validate(data);

        Assert.Equal(before, Snapshot(data.Draft));
        Assert.True(rows.Zip(data.Draft).All(p => ReferenceEquals(p.First, p.Second)));
    }

    [Fact]
    public void Validate_refuses_too_many_duplicate_or_unnumbered_steps()
    {
        var data = Fleets(1);
        data.Change(data.Draft[0].LocalId, "Name", "x");
        var step = data.Undo[0];

        Assert.Throws<InvalidDataException>(() => EditHistory.From(new([step, step], [])).Validate(data));
        Assert.Throws<InvalidDataException>(() => EditHistory.From(new([step with { StepId = Guid.Empty }], [])).Validate(data));
        Assert.Throws<InvalidDataException>(() => EditHistory.From(new([.. Enumerable.Range(0, 21).Select(_ => step with { StepId = Guid.NewGuid() })], [])).Validate(data));
    }

    // ---- copies, field order, step shape ----

    [Fact]
    public void A_shallow_copy_keeps_its_own_lists_and_steps_when_the_original_continues_editing()
    {
        var data = Fleets(2);
        var id = data.Draft[0].LocalId;
        data.Change(id, "Name", "new");
        var copy = data.ShallowCopy();

        data.Change(id, "Name", "later", newStep: false);
        data.Edit([], () => data.Draft.Reverse(), structural: true);

        Assert.Equal("new", copy.Undo[0].Rows[0].After!.Fields["Name"]);
        Assert.Equal(["new", "row 1"], Names(copy));
        Assert.Single(copy.Undo);
    }

    [Fact]
    public void Undo_and_redo_restore_the_captured_field_order_including_unknown_columns()
    {
        var row = Row(("Name", "old"), ("Z unknown", "z"), ("A unknown", "a"), ("empty", ""));
        var data = new ResourceData { Draft = [row] };
        var edited = row.With("Name", "new").Without("empty").With("B unknown", "b");
        data.Edit([row.LocalId], () => data.ReplaceDraftRows([edited]));
        var afterJson = JsonSerializer.Serialize(data.Draft[0]);

        data.UndoEdit();
        Assert.Equal(["Name", "Z unknown", "A unknown", "empty"], data.Draft[0].Fields.Keys);
        data.RedoEdit();
        Assert.Equal(afterJson, JsonSerializer.Serialize(data.Draft[0]));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(100)]
    public void A_batch_edit_keeps_only_the_changed_rows_and_a_structural_edit_adds_the_row_order(int changed)
    {
        var data = Fleets(2000);
        var chosen = data.Draft.Take(changed).ToArray();

        data.Edit(chosen.Select(r => r.LocalId), () => data.ReplaceDraftRows(chosen.Select(r => r.With("Name", "changed")).ToList()));
        var step = data.Undo.Single();

        Assert.Equal(changed, step.Rows.Length);
        Assert.Null(step.BeforeOrder);
        Assert.Null(step.AfterOrder);

        var added = Row(("ID", "added"));
        data.Edit([added.LocalId], () => data.Draft.Insert(1, added), structural: true);
        step = data.Undo.Last();
        Assert.Single(step.Rows);
        Assert.Equal(2000, step.BeforeOrder!.Value.Length);
        Assert.Equal(2001, step.AfterOrder!.Value.Length);
    }

    // ---- row position index (replacement keeps order; structure rebuilds) ----

    [Fact]
    public void Row_positions_stay_right_through_replacements_structural_undo_redo_and_reassignment()
    {
        var data = Fleets(2000);
        var list = data.Draft;
        var first = data.Draft[0];
        var last = data.Draft[^1];
        data.FindDraftRow(first.LocalId);
        var replacement = first.With("Remarks", "replacement");

        data.Edit([first.LocalId], () => data.ReplaceDraftRows([replacement]));
        Assert.Same(replacement, data.FindDraftRow(first.LocalId));
        data.UndoEdit();
        Assert.Equal("r", data.FindDraftRow(first.LocalId)!.Get("Remarks"));
        data.RedoEdit();
        Assert.Equal("replacement", data.FindDraftRow(first.LocalId)!.Get("Remarks"));
        Assert.Same(list, data.Draft);
        Assert.Same(last, data.Draft[^1]);

        var added = new DataRow();
        data.Edit([added.LocalId], () => data.Draft.Insert(0, added), structural: true);
        Assert.Equal(1, data.FindDraftPosition(first.LocalId));
        Assert.Same(added, data.FindDraftRow(added.LocalId));
        data.UndoEdit();
        Assert.Equal(0, data.FindDraftPosition(first.LocalId));
        Assert.Null(data.FindDraftRow(added.LocalId));
        data.RedoEdit();
        Assert.Equal(1, data.FindDraftPosition(first.LocalId));

        data.Draft = data.Draft.AsEnumerable().Reverse().ToList();
        Assert.Equal(data.Draft.Count - 1, data.FindDraftPosition(added.LocalId));
        Assert.Equal(-1, data.FindDraftPosition(Guid.NewGuid()));
    }

    [Fact]
    public void Replacing_rows_ignores_ids_that_are_not_in_the_draft_and_refuses_duplicates_without_partial_changes()
    {
        var data = Fleets(3);
        var before = Snapshot(data.Draft);
        var stranger = Row(("Name", "stranger"));

        data.ReplaceDraftRows([stranger]);
        Assert.Equal(before, Snapshot(data.Draft));

        var replacement = data.Draft[0].With("Name", "twice");
        var other = data.Draft[1].With("Name", "other");
        Assert.Throws<ArgumentException>(() => data.ReplaceDraftRows([other, replacement, replacement]));
        Assert.Equal(before, Snapshot(data.Draft));
    }

    // ---- change tracking for incremental saves ----

    [Fact]
    public void Changes_lists_exactly_what_was_not_acknowledged_yet()
    {
        var data = Fleets(2);
        var id = data.Draft[0].LocalId;
        data.Change(id, "Name", "a");
        var first = data.Undo[0].StepId;

        var changes = data.History.Changes();
        Assert.Equal([first], changes.Upserts.Select(s => s.StepId));
        Assert.Empty(changes.Deletes);
        Assert.True(changes.OrderChanged);

        data.History.Acknowledge(changes.Captured);
        var quiet = data.History.Changes();
        Assert.Empty(quiet.Upserts);
        Assert.Empty(quiet.Deletes);
        Assert.False(quiet.OrderChanged);

        data.Change(id, "Name", "ab", newStep: false);
        var amended = data.History.Changes();
        Assert.Equal([first], amended.Upserts.Select(s => s.StepId));
        Assert.False(amended.OrderChanged);
        data.History.Acknowledge(amended.Captured);

        data.UndoEdit();
        var moved = data.History.Changes();
        Assert.Empty(moved.Upserts);
        Assert.True(moved.OrderChanged);
        data.History.Acknowledge(moved.Captured);

        data.Change(id, "Name", "other branch");
        var branch = data.History.Changes();
        Assert.Equal([first], branch.Deletes);
        Assert.Single(branch.Upserts);
    }

    [Fact]
    public void Changes_reports_the_step_that_fell_off_the_twenty_step_limit_as_deleted()
    {
        var data = Fleets(1);
        var id = data.Draft[0].LocalId;
        for (var i = 0; i < 20; i++)
            data.Change(id, "Name", "v" + i);
        var oldest = data.Undo[0].StepId;
        data.History.Acknowledge(data.History.Changes().Captured);

        data.Change(id, "Name", "one more");

        Assert.Equal([oldest], data.History.Changes().Deletes);
    }

    // ---- persisted shape (I08) ----

    [Fact]
    public void A_workspace_round_trip_keeps_both_stacks_and_the_cells_they_restore()
    {
        var workspace = new Workspace();
        workspace.Resources[ResourceKind.Fleets] = Fleets(3);
        var data = workspace.Resources[ResourceKind.Fleets];
        data.Change(data.Draft[0].LocalId, "Name", "a");
        data.Change(data.Draft[1].LocalId, "Name", "b");
        data.Edit([], () => data.Draft.Reverse(), structural: true);
        data.UndoEdit();

        var restored = Workspace.Deserialize(workspace.Serialize()).Resources[ResourceKind.Fleets];

        Assert.Equal(data.Undo.Count, restored.Undo.Count);
        Assert.Equal(data.Redo.Count, restored.Redo.Count);
        Assert.Equal(Snapshot(data.Draft), Snapshot(restored.Draft));
        restored.RedoEdit();
        data.RedoEdit();
        Assert.Equal(Snapshot(data.Draft), Snapshot(restored.Draft));
        while (restored.UndoEdit().Moved)
            data.UndoEdit();
        Assert.Equal(Snapshot(data.Draft), Snapshot(restored.Draft));
    }

    // The format before v4 stored a whole draft per step. Old workspaces must still load, step for step.
    static string LegacyJson(Workspace workspace)
    {
        var root = JsonNode.Parse(workspace.Serialize())!;
        foreach (var (kind, data) in workspace.Resources)
        {
            var undo = new List<List<DataRow>>();
            var redo = new List<List<DataRow>>();
            var probe = data.ShallowCopy();
            while (probe.Undo.Count > 0) { probe.UndoEdit(); undo.Insert(0, Model.Clone(probe.Draft)); }
            probe = data.ShallowCopy();
            while (probe.Redo.Count > 0) { probe.RedoEdit(); redo.Insert(0, Model.Clone(probe.Draft)); }
            var node = root["Resources"]![kind.ToString()]!.AsObject();
            node.Remove("History");
            node["Undo"] = JsonSerializer.SerializeToNode(undo);
            node["Redo"] = JsonSerializer.SerializeToNode(redo);
        }
        return root.ToJsonString();
    }

    [Fact]
    public void Legacy_full_snapshot_histories_convert_to_the_same_undo_and_redo_states()
    {
        var workspace = new Workspace();
        workspace.Resources[ResourceKind.Fleets] = Fleets(2);
        var data = workspace.Resources[ResourceKind.Fleets];
        var id = data.Draft[0].LocalId;
        data.Change(id, "Name", "A");
        data.Change(id, "Name", "B");
        data.Edit([], () => data.Draft.Reverse(), structural: true);
        data.UndoEdit();
        var legacy = LegacyJson(workspace);

        var converted = Workspace.Deserialize(legacy);
        var restored = converted.Resources[ResourceKind.Fleets];

        Assert.DoesNotContain("\"Undo\":[[", converted.Serialize(), StringComparison.Ordinal);
        Assert.Equal(data.Undo.Count, restored.Undo.Count);
        Assert.Equal(data.Redo.Count, restored.Redo.Count);
        var original = data.ShallowCopy();
        var probe = restored.ShallowCopy();
        while (original.UndoEdit().Moved)
        { Assert.True(probe.UndoEdit().Moved); Assert.Equal(Snapshot(original.Draft), Snapshot(probe.Draft)); }
        original = data.ShallowCopy();
        probe = restored.ShallowCopy();
        while (original.RedoEdit().Moved)
        { Assert.True(probe.RedoEdit().Moved); Assert.Equal(Snapshot(original.Draft), Snapshot(probe.Draft)); }
    }

    [Theory]
    [InlineData("future version")]
    [InlineData("both formats at once")]
    public void An_unknown_history_version_or_an_ambiguous_mix_of_formats_is_refused(string problem)
    {
        var workspace = new Workspace();
        workspace.Resources[ResourceKind.Fleets] = Fleets(1);
        workspace.Resources[ResourceKind.Fleets].Change(workspace.Resources[ResourceKind.Fleets].Draft[0].LocalId, "Name", "x");
        var node = JsonNode.Parse(workspace.Serialize())!;
        var resource = node["Resources"]!["Fleets"]!;
        if (problem == "both formats at once")
            resource["Undo"] = new JsonArray();
        else
            resource["History"]!["Version"] = 99;

        Assert.ThrowsAny<JsonException>(() => Workspace.Deserialize(node.ToJsonString()));
    }
}
