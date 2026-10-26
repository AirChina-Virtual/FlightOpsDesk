namespace VamSys.Core;

// How a person settled one conflict between their draft and the server's value. Not an enum: new states would have to
// be added to the persisted-enum inventory, and these are never stored.
public sealed class ConflictChoice
{
    ConflictChoice(string name) => Name = name;
    public string Name { get; }
    // Keep the draft value; the conflict is closed.
    public static readonly ConflictChoice KeepLocal = new("KeepLocal");
    // Take the server's value (or drop the row when the whole record was deleted there).
    public static readonly ConflictChoice UseRemote = new("UseRemote");
    // Stop here; conflicts settled so far stay settled.
    public static readonly ConflictChoice Stop = new("Stop");
}

public sealed record ConflictPrompt(ResourceKind Kind, DataRow Row, MergeConflict Conflict)
{
    // A conflict on the whole record ("*") cannot keep the local value.
    public bool CanKeepLocal => Conflict.Field != "*";
}

// Settling the conflicts a snapshot refresh left. Every choice is applied in memory at once; the changes are saved a
// single time when the dialogs end or are cancelled.
public sealed class ConflictService(WorkspaceSession session)
{
    public async Task ResolveAsync(IEnumerable<ResourceKind> kinds, Func<ConflictPrompt, Task<ConflictChoice>> choose)
    {
        var changed = false;
        try
        { await ResolveInMemoryAsync(kinds, choose, () => changed = true); }
        finally
        { if (changed) await session.SaveAsync(); }
    }

    async Task ResolveInMemoryAsync(IEnumerable<ResourceKind> kinds, Func<ConflictPrompt, Task<ConflictChoice>> choose, Action markChanged)
    {
        foreach (var kind in kinds)
        {
            var data = session.Workspace.Resources[kind];
            foreach (var conflict in data.Conflicts.ToArray())
            {
                var row = data.FindDraftRow(conflict.RowId);
                if (row is null)
                { data.Conflicts.Remove(conflict); markChanged(); continue; }
                var choice = await choose(new(kind, row, conflict));
                if (choice == ConflictChoice.Stop)
                    return;
                if (choice == ConflictChoice.UseRemote)
                {
                    var replacement = conflict.Field != "*" ? row.With(conflict.Field, conflict.Remote ?? "") : row;
                    data.Edit([row.LocalId], () => { if (conflict.Field == "*") data.Draft.Remove(row); else data.ReplaceDraftRows([replacement]); }, newStep: false, structural: conflict.Field == "*");
                }
                data.Conflicts.Remove(conflict);
                markChanged();
            }
        }
    }
}
