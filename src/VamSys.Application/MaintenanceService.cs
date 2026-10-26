namespace VamSys.Core;

// The read-only airline parameters, and backup and clear of the open workspace: save first so the backup holds everything, show what will go, then run it
// under the save lock. A failed backup keeps the live state; a failure after the commit is reconciled with the database.
public sealed class MaintenanceService(WorkspaceSession session, IOnlineGateway online)
{
    // Saves the workspace and returns what clearing would remove. Throws when clearing is not safe
    // (unresolved writes or unfinished tasks).
    public async Task<MaintenancePreview> PrepareBackupAsync()
    {
        await session.SaveAsync();
        var preview = session.Storage.PreviewMaintenance(session.Workspace.Id);
        if (!preview.CanClear)
            throw ApplicationBlocks.Block("MaintenanceUnsafe");
        return preview;
    }

    // Backs up and clears the workspace the preview describes. workspaceReplaced runs with the workspace id whenever the
    // open workspace was replaced by a freshly loaded one, so the caller can drop sessions and views built for the old one.
    public async Task<MaintenanceOutcome> BackupAndClearAsync(MaintenancePreview preview, Action<Guid> workspaceReplaced)
    {
        var owner = preview.WorkspaceId;
        await session.SaveLock.WaitAsync();
        try
        {
            var result = await session.Storage.BackupAndClearAsync(owner, preview.Revision);
            session.ReplaceWorkspace(result.Workspace);
            workspaceReplaced(owner);
            return result;
        }
        catch
        {
            try
            {
                // Reconcile only an ambiguous post-commit failure. A failed backup/transaction keeps live state intact.
                if (session.Storage.StoredRevision(owner) != preview.Revision)
                {
                    var persisted = session.Storage.Load(owner);
                    persisted.Mode = RunMode.Offline;
                    session.ReplaceWorkspace(persisted);
                    workspaceReplaced(owner);
                }
            }
            catch { session.Gate.BlockStorage(); }
            throw;
        }
        finally { session.SaveLock.Release(); }
    }

    // Reads the airline parameters again; returns how many there are.
    public async Task<int> LoadParametersAsync()
    {
        if (session.Workspace.Mode != RunMode.Online)
            throw ApplicationBlocks.Block("ApiConnectFirst");
        var parameters = await online.Session(session.Workspace).ReadParametersAsync(CancellationToken.None, true);
        return parameters.Count;
    }

    // The parameters a new fleet may use. Only a new row can choose one, and only while online.
    public async Task<IReadOnlyList<AirlineParameter>> ChoosableParametersAsync(DataRow row)
    {
        if (row.Identity != null)
            throw ApplicationBlocks.Block("ApiParameterCreateOnly");
        if (session.Workspace.Mode != RunMode.Online)
            throw ApplicationBlocks.Block("ApiConnectFirst");
        var parameters = await online.Session(session.Workspace).ReadParametersAsync(CancellationToken.None, true);
        var choices = parameters.Where(p => p.Approved && !p.Restricted).ToList();
        if (choices.Count == 0)
            throw ApplicationBlocks.Block("ApiParameterRequired");
        return choices;
    }

    // Puts the chosen parameter on the row and saves. False when the row already had it.
    public async Task<bool> AssignParameterAsync(ResourceKind kind, DataRow row, string id)
    {
        if (id == row.Get("Parameter IDs"))
            return false;
        var data = session.Workspace.Resources[kind];
        var replacement = row.With("Parameter IDs", id);
        data.Edit([row.LocalId], () => data.ReplaceDraftRows([replacement]));
        await session.SaveAsync();
        return true;
    }
}
