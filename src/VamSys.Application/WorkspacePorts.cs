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
    // Reads one workspace in a single read transaction. Safe on a worker thread: it opens its own connection and
    // touches nothing but the new workspace. The observer, when given, is called on that same thread.
    Workspace Load(Guid id, IWorkspaceLoadObserver? observer = null);
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

// The steps of a workspace read, in order. Rows and the history steps are read per resource, so Share, History,
// Validate and Index repeat once per resource.
public enum WorkspaceLoadStage { Open, Header, Metadata, Rows, Share, History, Validate, Index, Journal }

// Follows a workspace read: the window shows its progress, the load benchmark times and weighs each step. Called on the
// thread that reads, synchronously; an observer must not touch the workspace being read.
public interface IWorkspaceLoadObserver
{
    // A step starts (and the previous one ends).
    void Stage(WorkspaceLoadStage stage);
    // Rows read so far of all the workspace's snapshot and draft rows; reported every few thousand rows and at the end.
    void Rows(int read, int total);
}

// Preferences that belong to the person, not to a workspace.
public interface IUserSettings
{
    string LoadLanguage();
    void SaveLanguage(string language);
    string LoadTableDensity();
    void SaveTableDensity(string density);
    // The column names hidden from the view menu for one resource, as stored (HiddenColumnsPreference decides which apply).
    IReadOnlyList<string> LoadHiddenColumns(ResourceKind kind);
    void SaveHiddenColumns(ResourceKind kind, IReadOnlyCollection<string> columns);
}

// A restartable one-shot timer; the window backs it with the dispatcher.
public interface IAutosaveTimer
{
    bool IsEnabled { get; }
    void Start();
    void Stop();
}
