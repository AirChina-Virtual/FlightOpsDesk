using VamSys.Core;
using VamSys.UnitTests.Domain;
using static VamSys.UnitTests.Domain.TestRows;

namespace VamSys.UnitTests.Application;

/// <summary>
/// F06 / I05: which unsaved cell edits exist and whether a row-level save is allowed. Any doubt asks for a full save.
/// The writes themselves are storage (R3).
/// </summary>
public class DraftEditsTests
{
    static Workspace WorkspaceWithFleet(out Guid row)
    {
        var w = new Workspace();
        CsvAdapter.Import(ResourceKind.Fleets, w.Resources[ResourceKind.Fleets], [Row(("ID", "1"), ("Name", "A"))], true);
        row = w.Resources[ResourceKind.Fleets].Draft[0].LocalId;
        return w;
    }

    [Fact]
    public void Nothing_recorded_means_nothing_to_save()
    {
        var edits = new DraftEdits();
        var w = WorkspaceWithFleet(out _);

        Assert.False(edits.CanSaveRows(w));
        Assert.Empty(edits.Changes);
        Assert.Null(edits.Owner);
        Assert.False(edits.RequiresFullSave);
    }

    [Fact]
    public void A_cell_edit_is_recorded_per_resource_with_the_history_to_save_with_it()
    {
        var w = WorkspaceWithFleet(out var row);
        var data = w.Resources[ResourceKind.Fleets];
        data.Change(row, "Name", "B");
        var edits = new DraftEdits();

        edits.Cell(w, ResourceKind.Fleets, row);

        Assert.True(edits.CanSaveRows(w));
        Assert.Same(w, edits.Owner);
        var change = Assert.Single(edits.Changes);
        Assert.Equal(ResourceKind.Fleets, change.Resource);
        Assert.Equal([row], change.Rows);
        Assert.Single(change.History!.Upserts);
    }

    [Fact]
    public void Several_edits_of_one_resource_are_merged_and_each_row_is_listed_once()
    {
        var w = WorkspaceWithFleet(out var first);
        var second = Guid.NewGuid();
        var edits = new DraftEdits();

        edits.Cell(w, ResourceKind.Fleets, first);
        edits.Cell(w, ResourceKind.Fleets, first);
        edits.Rows(w, ResourceKind.Fleets, [first, second]);

        Assert.Equal(new[] { first, second }.Order(), Assert.Single(edits.Changes).Rows.Order());
    }

    [Fact]
    public void An_undo_or_redo_of_replaced_rows_records_their_history_too()
    {
        var w = WorkspaceWithFleet(out var row);
        var edits = new DraftEdits();

        edits.Rows(w, ResourceKind.Aircraft, []);

        Assert.True(edits.CanSaveRows(w));
        var change = Assert.Single(edits.Changes);
        Assert.Equal(ResourceKind.Aircraft, change.Resource);
        Assert.Empty(change.Rows);
        Assert.NotNull(change.History);
        Assert.NotEqual(Guid.Empty, row);
    }

    [Fact]
    public void An_edit_in_another_workspace_forces_a_full_save_and_never_a_row_save()
    {
        var a = WorkspaceWithFleet(out var row);
        var b = WorkspaceWithFleet(out _);
        var edits = new DraftEdits();
        edits.Cell(a, ResourceKind.Fleets, row);

        edits.Cell(b, ResourceKind.Fleets, row);

        Assert.True(edits.RequiresFullSave);
        Assert.False(edits.CanSaveRows(a));
        Assert.False(edits.CanSaveRows(b));
    }

    [Fact]
    public void Row_saves_are_only_allowed_for_the_workspace_that_was_edited()
    {
        var a = WorkspaceWithFleet(out var row);
        var edits = new DraftEdits();
        edits.Cell(a, ResourceKind.Fleets, row);

        Assert.True(edits.CanSaveRows(a));
        Assert.False(edits.CanSaveRows(WorkspaceWithFleet(out _)));
    }

    [Fact]
    public void Take_hands_over_the_record_and_leaves_an_empty_one_for_edits_made_during_the_save()
    {
        var w = WorkspaceWithFleet(out var row);
        var edits = new DraftEdits();
        edits.Cell(w, ResourceKind.Fleets, row);

        var taken = edits.Take();

        Assert.True(taken.CanSaveRows(w));
        Assert.Equal([row], Assert.Single(taken.Changes).Rows);
        Assert.False(edits.CanSaveRows(w));
        Assert.Empty(edits.Changes);
        Assert.Null(edits.Owner);

        var other = Guid.NewGuid();
        edits.Cell(w, ResourceKind.Fleets, other);
        Assert.Equal([other], Assert.Single(edits.Changes).Rows);
        Assert.Equal([row], Assert.Single(taken.Changes).Rows);
    }

    [Fact]
    public void Take_keeps_the_need_for_a_full_save_with_the_taken_record_and_resets_it_here()
    {
        var a = WorkspaceWithFleet(out var row);
        var edits = new DraftEdits();
        edits.Cell(a, ResourceKind.Fleets, row);
        edits.Cell(WorkspaceWithFleet(out _), ResourceKind.Fleets, row);

        var taken = edits.Take();

        Assert.True(taken.RequiresFullSave);
        Assert.False(edits.RequiresFullSave);
    }

    [Fact]
    public void After_a_failed_save_the_rows_come_back_and_the_next_save_must_write_everything()
    {
        var w = WorkspaceWithFleet(out var row);
        var edits = new DraftEdits();
        edits.Cell(w, ResourceKind.Fleets, row);
        var taken = edits.Take();
        var during = Guid.NewGuid();
        edits.Cell(w, ResourceKind.Fleets, during);

        edits.Restore(taken);

        Assert.True(edits.RequiresFullSave);
        Assert.False(edits.CanSaveRows(w));
        Assert.Same(w, edits.Owner);
        Assert.Equal(new[] { row, during }.Order(), Assert.Single(edits.Changes).Rows.Order());
    }

    [Fact]
    public void Restore_into_an_empty_record_takes_over_the_failed_owner()
    {
        var w = WorkspaceWithFleet(out var row);
        var edits = new DraftEdits();
        edits.Cell(w, ResourceKind.Fleets, row);
        var taken = edits.Take();

        edits.Restore(taken);

        Assert.Same(w, edits.Owner);
        Assert.True(edits.RequiresFullSave);
    }

    [Fact]
    public void A_restored_hand_over_with_edits_in_two_resources_is_retried_in_full_and_the_flag_is_handed_on_once()
    {
        var w = new Workspace();
        var fleet = Guid.NewGuid();
        var edits = new DraftEdits();
        edits.Cell(w, ResourceKind.Fleets, fleet);
        var taken = edits.Take();

        Assert.True(taken.CanSaveRows(w));
        Assert.Equal(fleet, Assert.Single(Assert.Single(taken.Changes).Rows));
        Assert.Empty(Assert.Single(taken.Changes).History!.Upserts);

        edits.Cell(w, ResourceKind.Routes, Guid.NewGuid());
        edits.Restore(taken);

        Assert.True(edits.RequiresFullSave);
        Assert.False(edits.CanSaveRows(w));
        Assert.Equal(2, edits.Changes.Count);

        var retried = edits.Take();

        Assert.True(retried.RequiresFullSave);
        Assert.False(edits.RequiresFullSave);
        Assert.Empty(edits.Changes);
    }
}