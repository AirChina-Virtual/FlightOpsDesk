using VamSys.Core;

namespace VamSys.Presentation;

// The data effect of typing into a table cell. Rows are replaced, never edited in place. Typing on in the same cell
// continues its undo step; another cell, or a continuation reset (a render, a rebind, a page change), starts a new one.
public sealed class CellEditCommitter
{
    string? continuing;

    // The cell typing would continue, if any ("rowId:field").
    public string? Continuing => continuing;
    public void ResetContinuation() => continuing = null;

    // Replaces the row with the new value and returns the replacement, and how the resource's count of changed rows
    // moves (+1 when the row becomes changed, -1 when it returns to its snapshot value, otherwise 0).
    public (DataRow Replacement, int ChangedDelta) Commit(ResourceData data, DataRow current, string field, string value, IReadOnlyDictionary<Guid, DataRow> baseline)
    {
        var id = current.LocalId;
        var before = ChangePlanner.GetChangeKind(current, baseline.GetValueOrDefault(id));
        var key = id + ":" + field;
        var replacement = current.With(field, value);
        // A route that becomes a jumpseat loses its fleets in the same step (the server drops a jumpseat's fleets).
        if (field == "Type" && value == "jumpseat" && current.Get("Fleet IDs") != "")
            replacement = replacement.With("Fleet IDs", "");
        data.Edit([id], () => data.ReplaceDraftRows([replacement]), newStep: continuing != key);
        continuing = key;
        var after = ChangePlanner.GetChangeKind(replacement, baseline.GetValueOrDefault(id));
        return (replacement, (after != null ? 1 : 0) - (before != null ? 1 : 0));
    }
}
