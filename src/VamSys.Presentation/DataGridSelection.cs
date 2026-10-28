using VamSys.Core;

namespace VamSys.Presentation;

// How many selected rows the batch commands can act on: active rows, deleted rows, and rows that can be deleted
// (every active row, and a deleted row only while it is not in the snapshot, i.e. created here and then removed).
// The counts are kept up to date as rows join or leave the selection; after a change that may affect any selected row
// they are marked stale and recounted the next time they are read.
public sealed class SelectionCounts
{
    bool stale = true;
    int active, deleted, deletable;

    public bool IsStale => stale;
    public void Invalidate() => stale = true;

    // Ignored while stale: the next read recounts everything.
    public void Added(DataRow row, bool inSnapshot) => Apply(row, inSnapshot, 1);
    public void Removed(DataRow row, bool inSnapshot) => Apply(row, inSnapshot, -1);

    void Apply(DataRow row, bool inSnapshot, int step)
    {
        if (stale)
            return;
        if (Schemas.Deleted(row))
            deleted += step;
        else
            active += step;
        if (!Schemas.Deleted(row) || !inSnapshot)
            deletable += step;
    }

    public (int Active, int Deleted, int Deletable) Read(IEnumerable<Guid> selected, Func<Guid, DataRow?> find, Func<Guid, bool> inSnapshot)
    {
        if (stale)
        {
            active = deleted = deletable = 0;
            stale = false;
            foreach (var id in selected)
                if (find(id) is { } row)
                    Apply(row, inSnapshot(id), 1);
        }
        return (active, deleted, deletable);
    }
}

// Which data page controls may be used now. Everything needs an editable window; the pager also needs somewhere to go.
public sealed record DataGridAvailability(bool First, bool Previous, bool Next, bool Last, bool Go, bool Edit, bool SelectPage, bool SelectAll, bool ClearSelection)
{
    public static DataGridAvailability For(DataTableViewModel model, bool editable, int shownRows) => new(
        First: editable && model.Page > 1,
        Previous: editable && model.Page > 1,
        Next: editable && model.Page < model.PageCount,
        Last: editable && model.Page < model.PageCount,
        Go: editable && model.Total > 0,
        Edit: editable,
        SelectPage: editable && shownRows > 0,
        SelectAll: editable && model.Total > 0,
        ClearSelection: editable && model.Selected.Count > 0);

    // A batch command acts on the selected active rows, deleted rows or deletable rows; the conflict command needs a conflict.
    public static bool Command(bool editable, string? target, bool isConflictCommand, (int Active, int Deleted, int Deletable) counts, int conflicts)
        => editable && target switch { "active" => counts.Active > 0, "deleted" => counts.Deleted > 0, "delete" => counts.Deletable > 0, _ => true }
            && (!isConflictCommand || conflicts > 0);
}
