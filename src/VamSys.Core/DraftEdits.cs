namespace VamSys.Core;

public sealed record DraftRowChanges(ResourceKind Resource, IReadOnlyCollection<Guid> Rows, HistoryChanges? History);

// Draft cell edits not yet saved. The storage keeps the last saved state, so after a successful save of these
// rows the database again equals memory. Any doubt (another workspace, a failed save, nothing recorded) asks for
// a full save instead of a row save.
public sealed class DraftEdits
{
    readonly Dictionary<ResourceKind, HashSet<Guid>> rows = [];
    readonly HashSet<ResourceKind> history = [];
    public Workspace? Owner { get; private set; }
    public bool RequiresFullSave { get; private set; }

    // A value edited in place; checkpointed when the edit also added an undo step.
    public void Cell(Workspace workspace, ResourceKind kind, Guid row, bool checkpointed)
    {
        if (Owner != null && !ReferenceEquals(Owner, workspace)) RequiresFullSave = true;
        Owner = workspace;
        if (!rows.TryGetValue(kind, out var ids)) rows[kind] = ids = [];
        ids.Add(row);
        // Continuations change the same step's After image, even after a previous autosave.
        history.Add(kind);
    }
    public bool CanSaveRows(Workspace workspace) => !RequiresFullSave && ReferenceEquals(Owner, workspace) && (rows.Count > 0 || history.Count > 0);
    public IReadOnlyCollection<DraftRowChanges> Changes => rows.Keys.Union(history)
        .Select(k => new DraftRowChanges(k, rows.TryGetValue(k, out var ids) ? ids.ToArray() : [], Owner?.Resources[k].History.Changes())).ToArray();

    // Hands the recorded edits to a save; edits made while it runs are recorded afresh.
    public DraftEdits Take()
    {
        var taken = new DraftEdits { Owner = Owner, RequiresFullSave = RequiresFullSave };
        foreach (var (kind, ids) in rows) taken.rows[kind] = ids;
        taken.history.UnionWith(history);
        rows.Clear(); history.Clear(); Owner = null; RequiresFullSave = false;
        return taken;
    }
    // After a failed save nothing about the database is assumed; the next save writes everything.
    public void Restore(DraftEdits failed)
    {
        foreach (var (kind, ids) in failed.rows)
        { if (!rows.TryGetValue(kind, out var mine)) rows[kind] = mine = []; mine.UnionWith(ids); }
        history.UnionWith(failed.history);
        Owner ??= failed.Owner;
        RequiresFullSave = true;
    }
}
