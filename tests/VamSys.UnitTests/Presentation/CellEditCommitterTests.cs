using VamSys.Core;
using VamSys.Presentation;

namespace VamSys.UnitTests.Presentation;

public class CellEditCommitterTests
{
    static DataRow Row(params (string, string)[] fields) => new() { Fields = fields.ToDictionary(p => p.Item1, p => p.Item2) };

    static (ResourceData Data, DataRow Row, Dictionary<Guid, DataRow> Baseline) Stored()
    {
        var row = Row(("Name", "old"), ("Code", "A"));
        var data = new ResourceData();
        data.Snapshot.Add(row);
        data.Draft.Add(row);
        return (data, row, new() { [row.LocalId] = row });
    }

    // A route that becomes a jumpseat loses its fleets in the same undo step; other type changes keep them.
    [Fact]
    public void Changing_a_route_to_jumpseat_clears_its_fleets_in_the_same_step()
    {
        var row = Row(("Type", "scheduled"), ("Fleet IDs", "4,5"));
        var data = new ResourceData();
        data.Draft.Add(row);
        var edits = new CellEditCommitter();
        var jumpseat = edits.Commit(data, row, "Type", "jumpseat", new Dictionary<Guid, DataRow>()).Replacement;
        Assert.Equal("", jumpseat.Get("Fleet IDs"));
        Assert.Single(data.Undo);
        Assert.Equal("4,5", edits.Commit(data, row, "Type", "cargo", new Dictionary<Guid, DataRow>()).Replacement.Get("Fleet IDs"));
    }

    [Fact]
    public void A_commit_replaces_the_draft_row_and_leaves_the_original_untouched()
    {
        var (data, row, baseline) = Stored();
        var (replacement, _) = new CellEditCommitter().Commit(data, row, "Name", "new", baseline);
        Assert.NotSame(row, replacement);
        Assert.Equal(row.LocalId, replacement.LocalId);
        Assert.Equal("new", replacement.Get("Name"));
        Assert.Equal("old", row.Get("Name"));
        Assert.Same(replacement, data.FindDraftRow(row.LocalId));
    }

    [Fact]
    public void Typing_on_in_the_same_cell_continues_its_undo_step_and_another_cell_or_a_reset_starts_a_new_one()
    {
        var (data, row, baseline) = Stored();
        var edits = new CellEditCommitter();
        var current = edits.Commit(data, row, "Name", "n", baseline).Replacement;
        current = edits.Commit(data, current, "Name", "ne", baseline).Replacement;
        current = edits.Commit(data, current, "Name", "new", baseline).Replacement;
        Assert.Single(data.Undo);
        Assert.Equal(row.LocalId + ":Name", edits.Continuing);

        current = edits.Commit(data, current, "Code", "B", baseline).Replacement;
        Assert.Equal(2, data.Undo.Count);

        edits.ResetContinuation();
        Assert.Null(edits.Continuing);
        edits.Commit(data, current, "Code", "C", baseline);
        Assert.Equal(3, data.Undo.Count);

        // One undo returns the whole first step: every keystroke in the Name cell.
        data.UndoEdit();
        data.UndoEdit();
        data.UndoEdit();
        Assert.Equal("old", data.FindDraftRow(row.LocalId)!.Get("Name"));
    }

    [Fact]
    public void The_changed_count_moves_only_when_a_row_starts_or_stops_differing_from_its_snapshot()
    {
        var (data, row, baseline) = Stored();
        var edits = new CellEditCommitter();
        var (changed, becomesChanged) = edits.Commit(data, row, "Name", "new", baseline);
        Assert.Equal(1, becomesChanged);
        var (stillChanged, stays) = edits.Commit(data, changed, "Code", "B", baseline);
        Assert.Equal(0, stays);
        var (codeBack, _) = edits.Commit(data, stillChanged, "Code", "A", baseline);
        var (_, returns) = edits.Commit(data, codeBack, "Name", "old", baseline);
        Assert.Equal(-1, returns);
    }

    [Fact]
    public void A_row_created_here_stays_changed_whatever_is_typed()
    {
        var created = Row(("Name", "draft"));
        var data = new ResourceData();
        data.Draft.Add(created);
        Assert.Equal(0, new CellEditCommitter().Commit(data, created, "Name", "other", new Dictionary<Guid, DataRow>()).ChangedDelta);
    }
}
