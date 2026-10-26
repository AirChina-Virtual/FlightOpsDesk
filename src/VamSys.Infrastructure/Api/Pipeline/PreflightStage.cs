using VamSys.Core;

namespace VamSys.Infrastructure;

// First stage of an item that has not been sent: everything that can stop a write before it exists.
// CheckLocal needs no request; CheckRemote reads the server (the record as it is now, what still depends on it,
// the airline parameters, the move target). The duplicate stage runs between them, as the requests always did.
internal sealed class PreflightStage(JobRun run)
{
    // The item's own conflicts and its references to rows that are created earlier in the same job.
    public void CheckLocal(ChangeItem item)
    {
        if (run.Workspace.Resources[item.Resource].Conflicts.Any(c => c.RowId == item.After.LocalId))
            throw OperationsAdapter.Block("ApiConflict");
        foreach (var field in Schemas.ReferenceFields(item.Resource))
        {
            var tokens = item.After.Get(field).Split(',', StringSplitOptions.TrimEntries);
            if (!tokens.Any(LocalReference.IsLocal))
                continue;
            var resolved = tokens.Select(t => LocalReference.IsLocal(t) ? (LocalReference.TryParse(t, out var localId) ? run.Items.ByLocalId(localId).RemoteId : null) ?? throw OperationsAdapter.Block("ApiRefreshFirst") : t);
            var joined = string.Join(',', resolved);
            item.After = item.After.With(field, joined);
            item.Fields[field] = new(FieldIntent.Set, joined);
        }
    }

    // The record must still read as it did when the change was planned (otherwise Conflict), and nothing may depend
    // on a record that is about to be deleted. Returns true when the item was settled here (Conflict, saved).
    public async Task<bool> CheckRemoteAsync(ItemRun r)
    {
        var item = r.Item;
        var api = run.Api;
        if (item.Before != null)
        {
            if (PerformanceRun.Current is { } conflictMetrics)
                conflictMetrics.Phase = "ConflictCheck";
            var current = await api.FindRow(item.Resource, item.Before, r.Id, run.Ct);
            api.RecordPreflight(item, current);
            if (current == null || !api.Matches(item, current, item.Before, true))
            { item.State = ItemState.Conflict; item.SetMessage(Messages.Define("ApiConflict")); await run.Sink.SaveAsync(); return true; }
        }
        if (item.Kind == ChangeKind.Delete)
        {
            if (PerformanceRun.Current is { } dependencyMetrics)
                dependencyMetrics.Phase = "DependencyCheck";
            if (await api.HasDeletionDependenciesAsync(item, run.Ct))
                throw OperationsAdapter.Block("ApiDependencies");
        }
        await api.PreflightParametersAsync(item, run.Ct);
        await api.PreflightMoveAsync(item, run.Ct);
        return false;
    }
}
