namespace VamSys.Core;

public sealed record WorkspaceListing(Guid Id, string Name);

// What a backup-and-clear shows before it runs; the revision guards against clearing a database that changed since.
public sealed record MaintenancePreview(Guid WorkspaceId, string Name, string DataDirectory, long Revision, int Snapshots, int Drafts, int Jobs, int Conflicts, int HistorySteps, bool CanClear);
public sealed record MaintenanceOutcome(Workspace Workspace, string BackupDatabasePath);

// Everything the application layer needs from the database. The storage project implements it (through an adapter in
// the app); the application layer never sees SQLite.
public interface IWorkspaceStorage
{
    IReadOnlyList<WorkspaceListing> List();
    Workspace Load(Guid id);
    // Writes the whole workspace (a workspace that was never saved, or a staged snapshot refresh).
    void Save(Workspace workspace);
    Task SaveAsync(Workspace workspace);
    // The journal save: writes only what changed since the database last matched the workspace.
    Task SaveChangesAsync(Workspace workspace, JournalScope scope);
    // Writes only the edited rows and history steps; false when the database cannot take a row save.
    Task<bool> TrySaveDraftRowsAsync(Workspace workspace, IReadOnlyCollection<DraftRowChanges> changes);
    bool JobExists(Guid workspaceId, Guid jobId);
    BatchJob? LoadJob(Guid workspaceId, Guid jobId);
    IBatchCheckpointWriter CheckpointWriter(Workspace workspace);
    void SaveConnectionSettings(Workspace workspace, string clientId, string secret, string url, bool localRouteTimes);
    void UpdateOperationsCredentials(Workspace workspace, string clientId, string secret);
    MaintenancePreview PreviewMaintenance(Guid workspaceId);
    long StoredRevision(Guid workspaceId);
    Task<MaintenanceOutcome> BackupAndClearAsync(Guid workspaceId, long expectedRevision);
}

// Preferences that belong to the person, not to a workspace.
public interface IUserSettings
{
    string LoadLanguage();
    void SaveLanguage(string language);
    string LoadTableDensity();
    void SaveTableDensity(string density);
}

// A restartable one-shot timer; the window backs it with the dispatcher.
public interface IAutosaveTimer
{
    bool IsEnabled { get; }
    void Start();
    void Stop();
}
