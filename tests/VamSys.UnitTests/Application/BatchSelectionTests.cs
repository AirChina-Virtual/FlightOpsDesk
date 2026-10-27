using VamSys.Core;

namespace VamSys.UnitTests.Application;

// The captured selection and the previews built from it, without the service around them.
public class BatchSelectionTests
{
    static DataRow Row(params (string, string)[] values) => new() { Fields = values.ToDictionary(p => p.Item1, p => p.Item2) };

    [Fact]
    public void Batch_rules_require_a_selection_and_skip_deleted_rows_without_touching_unselected_rows()
    {
        var normal = Row(("Name", "old"));
        var deleted = Row(("Name", "deleted"), ("_delete", "TRUE"));
        var unselected = Row(("Name", "untouched"));
        var data = new ResourceData { Draft = [normal, deleted, unselected] };

        var empty = BatchEditPreview.Rule(BatchSelection.Capture([]), new(RuleKind.Set, "Name", "new"));
        empty.Apply(data);
        Assert.Empty(data.Undo);
        Assert.Equal("old", normal.Get("Name"));

        var preview = BatchEditPreview.Rule(BatchSelection.Capture([normal, deleted]), new(RuleKind.Set, "Name", "new"));
        Assert.Equal(2, preview.Selection.Selected.Count);
        Assert.Equal(1, preview.Selection.Skipped);
        Assert.Single(preview.Changes);

        preview.Apply(data);
        Assert.Equal("new", data.FindDraftRow(normal.LocalId)!.Get("Name"));
        Assert.Equal("deleted", deleted.Get("Name"));
        Assert.Equal("untouched", unselected.Get("Name"));

        data.UndoEdit();
        Assert.Equal("old", data.FindDraftRow(normal.LocalId)!.Get("Name"));
    }

    [Fact]
    public void Deletion_removes_marked_new_drafts_and_skips_already_marked_existing_rows()
    {
        var draft = Row(("Name", "new"), ("_delete", "TRUE"));
        var deleted = Row(("ID", "1"), ("_delete", "TRUE"));
        var active = Row(("ID", "2"));
        var w = new Workspace();
        var data = w.Resources[ResourceKind.Aircraft];
        data.Draft = [draft, deleted, active];
        data.Snapshot = [deleted.Dup(), active.Dup()];

        var selection = BatchSelection.ForDeletion(data.Draft, data.Snapshot);
        Assert.Equal(2, selection.Eligible.Count);
        Assert.Equal(1, selection.Skipped);

        var service = new DeletionService();
        var plan = service.Preview(w, ResourceKind.Aircraft, selection.Eligible.Select(r => r.LocalId));
        Assert.Equal(draft.LocalId, plan.RemoveDrafts.Single());
        Assert.Equal(active.LocalId, plan.MarkExisting.Single());

        service.Apply(w, plan);
        Assert.Null(data.FindDraftRow(draft.LocalId));
        Assert.All(data.Draft, r => Assert.True(Schemas.Deleted(r)));

        data.UndoEdit();
        Assert.Equal(3, data.Draft.Count);
        Assert.False(Schemas.Deleted(data.FindDraftRow(active.LocalId)!));
    }

    [Fact]
    public void No_op_batch_previews_do_not_create_history_and_partial_no_ops_are_counted()
    {
        var a = Row(("Name", "same"));
        var b = Row(("Name", "old"));
        var data = new ResourceData { Draft = [a, b] };

        var preview = BatchEditPreview.Rule(BatchSelection.Capture([a]), new(RuleKind.Set, "Name", "same"));
        preview.Apply(data);
        Assert.Equal(1, preview.Unchanged);
        Assert.Empty(data.Undo);

        preview = BatchEditPreview.Rule(BatchSelection.Capture([a, b]), new(RuleKind.Set, "Name", "same"));
        Assert.Equal(1, preview.Unchanged);
        Assert.Single(preview.Changes);
        Assert.Equal("old", b.Get("Name"));

        preview.Apply(data);
        Assert.Single(data.Undo);
        Assert.All(data.Draft, r => Assert.Equal("same", r.Get("Name")));
    }

    [Fact]
    public void Restore_selects_only_deleted_records_and_a_retirement_preview_keeps_its_captured_date()
    {
        var a = Row(("End Date", ""));
        var b = Row(("_delete", "TRUE"));
        var restore = BatchSelection.Capture([a, b], restore: true);
        Assert.Single(restore.Eligible);
        Assert.Same(b, restore.Eligible[0]);
        Assert.Equal(1, restore.Skipped);

        var data = new ResourceData { Draft = [a] };
        var preview = BatchEditPreview.Rule(BatchSelection.Capture([a]), new(RuleKind.Retire, "Name", ""));
        var date = preview.Changes.Single().Get("End Date");
        preview.Apply(data);
        Assert.Equal(19, date.Length);
        Assert.Equal(date, data.Draft[0].Get("End Date"));
    }
}
