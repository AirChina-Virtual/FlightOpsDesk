namespace VamSys.Core;

// Backup and clear of the open workspace: save first so the backup holds everything, show what will go, then run it
// under the save lock. A failed backup keeps the live state; a failure after the commit is reconciled with the database.
public sealed class MaintenanceService(WorkspaceSession session)
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
}
