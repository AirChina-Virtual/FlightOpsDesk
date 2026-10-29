using VamSys.Core;
using VamSys.Presentation;

namespace VamSys.UnitTests.Presentation;

public class UserSelectionTests
{
    static DataRow Row(bool deleted = false) => new() { Fields = new Dictionary<string, string> { ["_delete"] = deleted ? "TRUE" : "" } };

    static (DataTableViewModel Model, SelectionCounts Counts, Dictionary<Guid, DataRow> Rows) Page(params DataRow[] rows)
    {
        var model = new DataTableViewModel();
        model.Apply(new DataQueryResult(rows.Select(r => r.LocalId).ToArray(), 0), "", 0, null);
        var counts = new SelectionCounts();
        var all = rows.ToDictionary(r => r.LocalId);
        counts.Read(model.Selected, id => all.GetValueOrDefault(id), _ => true);
        return (model, counts, all);
    }

    [Fact]
    public void Rows_the_user_selects_and_deselects_move_the_selection_and_its_counts()
    {
        var active = Row();
        var deleted = Row(deleted: true);
        var created = Row(deleted: true);
        var (model, counts, rows) = Page(active, deleted, created);
        bool InSnapshot(Guid id) => id != created.LocalId;

        UserSelection.Apply(model, counts, [active, deleted, created], [], InSnapshot);
        Assert.Equal(3, model.Selected.Count);
        Assert.False(counts.IsStale);
        Assert.Equal((1, 2, 2), counts.Read(model.Selected, id => rows.GetValueOrDefault(id), InSnapshot));

        UserSelection.Apply(model, counts, [], [deleted], InSnapshot);
        Assert.Equal((1, 1, 2), counts.Read(model.Selected, id => rows.GetValueOrDefault(id), InSnapshot));
        Assert.DoesNotContain(deleted.LocalId, model.Selected);
    }

    [Fact]
    public void A_row_already_selected_or_already_deselected_does_not_count_twice()
    {
        var row = Row();
        var (model, counts, rows) = Page(row);
        UserSelection.Apply(model, counts, [row], [], _ => true);
        UserSelection.Apply(model, counts, [row], [], _ => true);
        Assert.Equal((1, 0, 1), counts.Read(model.Selected, id => rows.GetValueOrDefault(id), _ => true));
        UserSelection.Apply(model, counts, [], [row], _ => true);
        UserSelection.Apply(model, counts, [], [row], _ => true);
        Assert.Equal((0, 0, 0), counts.Read(model.Selected, id => rows.GetValueOrDefault(id), _ => true));
    }

    [Fact]
    public void Leaving_rows_are_handled_before_joining_ones()
    {
        // A row reported as both removed and added (a re-selection in one change) ends up selected.
        var row = Row();
        var (model, counts, rows) = Page(row);
        UserSelection.Apply(model, counts, [row], [row], _ => true);
        Assert.Contains(row.LocalId, model.Selected);
        Assert.Equal((1, 0, 1), counts.Read(model.Selected, id => rows.GetValueOrDefault(id), _ => true));
    }

    [Fact]
    public void Selecting_the_shown_page_adds_its_rows_and_recounts()
    {
        var first = Row();
        var second = Row(deleted: true);
        var (model, counts, rows) = Page(first, second);
        UserSelection.SelectShown(model, counts, [first.LocalId, second.LocalId]);
        Assert.True(counts.IsStale);
        Assert.Equal((1, 1, 1), counts.Read(model.Selected, id => rows.GetValueOrDefault(id), _ => true));
    }

    [Fact]
    public void A_density_change_keeps_the_anchor_row_at_its_height_and_never_scrolls_above_the_top()
    {
        Assert.Equal(32 + 10 * 24 - 15, DataGridScroll.AnchoredOffset(10, 15, 32, 24, 400));
        Assert.Equal(0, DataGridScroll.AnchoredOffset(0, 50, 32, 24, 400));
        Assert.Equal(400, DataGridScroll.AnchoredOffset(-1, 0, 32, 24, 400));
    }
}
