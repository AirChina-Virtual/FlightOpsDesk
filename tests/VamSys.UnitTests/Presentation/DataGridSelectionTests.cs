using VamSys.Core;
using VamSys.Presentation;

namespace VamSys.UnitTests.Presentation;

public class DataGridSelectionTests
{
    static DataRow Row(bool deleted = false) => new() { Fields = new Dictionary<string, string> { ["Name"] = "x", ["_delete"] = deleted ? "TRUE" : "" } };

    [Fact]
    public void Active_deleted_and_deletable_rows_are_counted_and_a_stored_deleted_row_cannot_be_deleted_again()
    {
        var active = Row();
        var storedDeleted = Row(true);
        var localDeleted = Row(true);
        var rows = new[] { active, storedDeleted, localDeleted }.ToDictionary(r => r.LocalId);
        var snapshot = new HashSet<Guid> { active.LocalId, storedDeleted.LocalId };
        var counts = new SelectionCounts();
        Assert.Equal((1, 2, 2), counts.Read(rows.Keys, id => rows.GetValueOrDefault(id), snapshot.Contains));
        Assert.False(counts.IsStale);
    }

    [Fact]
    public void A_row_missing_from_the_draft_is_not_counted()
    {
        var counts = new SelectionCounts();
        Assert.Equal((0, 0, 0), counts.Read([Guid.NewGuid()], _ => null, _ => false));
    }

    [Fact]
    public void Joining_and_leaving_rows_update_fresh_counts_without_a_recount()
    {
        var counts = new SelectionCounts();
        counts.Read([], _ => null, _ => false);
        var recounts = 0;
        DataRow? Find(Guid _) { recounts++; return null; }
        counts.Added(Row(), inSnapshot: true);
        counts.Added(Row(true), inSnapshot: false);
        counts.Added(Row(true), inSnapshot: true);
        Assert.Equal((1, 2, 2), counts.Read([Guid.NewGuid()], Find, _ => false));
        counts.Removed(Row(true), inSnapshot: false);
        Assert.Equal((1, 1, 1), counts.Read([Guid.NewGuid()], Find, _ => false));
        Assert.Equal(0, recounts);
    }

    [Fact]
    public void Stale_counts_ignore_single_changes_and_recount_on_the_next_read()
    {
        var row = Row();
        var counts = new SelectionCounts();
        counts.Read([row.LocalId], _ => row, _ => true);
        counts.Invalidate();
        Assert.True(counts.IsStale);
        counts.Added(Row(), inSnapshot: true);
        Assert.Equal((1, 0, 1), counts.Read([row.LocalId], _ => row, _ => true));
    }

    static DataTableViewModel Model(int rows, int page = 1)
    {
        var model = new DataTableViewModel();
        model.Apply(new DataQueryResult(Enumerable.Range(0, rows).Select(_ => Guid.NewGuid()).ToArray(), 0), "", 0, null);
        model.SetPage(page);
        return model;
    }

    [Fact]
    public void The_pager_offers_only_the_directions_there_are_pages_in()
    {
        var first = DataGridAvailability.For(Model(450, 1), editable: true, shownRows: 200);
        Assert.Equal((false, false, true, true, true), (first.First, first.Previous, first.Next, first.Last, first.Go));
        var middle = DataGridAvailability.For(Model(450, 2), true, 200);
        Assert.Equal((true, true, true, true), (middle.First, middle.Previous, middle.Next, middle.Last));
        var last = DataGridAvailability.For(Model(450, 3), true, 50);
        Assert.Equal((true, true, false, false), (last.First, last.Previous, last.Next, last.Last));
        var empty = DataGridAvailability.For(Model(0), true, 0);
        Assert.Equal((false, false, false, false, false, false, false), (empty.First, empty.Previous, empty.Next, empty.Last, empty.Go, empty.SelectPage, empty.SelectAll));
        Assert.True(empty.Edit);
    }

    [Fact]
    public void Selection_choices_need_rows_or_a_selection()
    {
        var model = Model(10);
        var none = DataGridAvailability.For(model, true, 10);
        Assert.Equal((true, true, false), (none.SelectPage, none.SelectAll, none.ClearSelection));
        model.Selected.Add(model.ResultIds[0]);
        Assert.True(DataGridAvailability.For(model, true, 10).ClearSelection);
    }

    [Fact]
    public void Nothing_can_be_used_while_the_window_is_not_editable()
    {
        var model = Model(450, 2);
        model.Selected.Add(model.ResultIds[0]);
        var locked = DataGridAvailability.For(model, editable: false, shownRows: 200);
        Assert.Equal(new DataGridAvailability(false, false, false, false, false, false, false, false, false), locked);
    }

    [Theory]
    [InlineData("active", 1, 0, 0, false, 0, true)]
    [InlineData("active", 0, 3, 3, false, 0, false)]
    [InlineData("deleted", 0, 1, 0, false, 0, true)]
    [InlineData("deleted", 2, 0, 2, false, 0, false)]
    [InlineData("delete", 0, 1, 1, false, 0, true)]
    [InlineData("delete", 0, 1, 0, false, 0, false)]
    [InlineData(null, 0, 0, 0, false, 0, true)]
    [InlineData(null, 0, 0, 0, true, 0, false)]
    [InlineData(null, 0, 0, 0, true, 2, true)]
    public void A_batch_command_needs_the_rows_it_acts_on_and_the_conflict_command_a_conflict(string? target, int active, int deleted, int deletable, bool conflictCommand, int conflicts, bool expected)
    {
        Assert.Equal(expected, DataGridAvailability.Command(true, target, conflictCommand, (active, deleted, deletable), conflicts));
        Assert.False(DataGridAvailability.Command(false, target, conflictCommand, (active, deleted, deletable), conflicts));
    }
}
