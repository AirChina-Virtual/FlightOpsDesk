using VamSys.Core;
using VamSys.Infrastructure;

namespace VamSys.App;

// Connects the application layer's storage ports to the SQLite workspace store. The window and the application
// services only see the interfaces.
public sealed class WorkspaceStoreAdapter(WorkspaceStore store) : IWorkspaceStorage, IUserSettings
{
    public static WorkspaceStoreAdapter OpenDefault() => new(new WorkspaceStore(AppPaths.DataDirectory));

    // The store itself, for the QA build's fault injection and for adapters of the online side.
    internal WorkspaceStore Inner => store;

    public IReadOnlyList<WorkspaceListing> List() => store.List().Select(x => new WorkspaceListing(x.Id, x.Name)).ToList();
    public Workspace Load(Guid id, IWorkspaceLoadObserver? observer = null) => store.Load(id, observer);
    public void Save(Workspace workspace) => store.Save(workspace);
    public Task SaveAsync(Workspace workspace) => store.SaveAsync(workspace);
    public Task SaveChangesAsync(Workspace workspace, JournalScope scope) => store.SaveChangesAsync(workspace, scope);
    public Task<bool> TrySaveDraftRowsAsync(Workspace workspace, IReadOnlyCollection<DraftRowChanges> changes) => store.TrySaveDraftRowsAsync(workspace, changes);
    public bool JobExists(Guid workspaceId, Guid jobId) => store.JobExists(workspaceId, jobId);
    public BatchJob? LoadJob(Guid workspaceId, Guid jobId) => store.LoadJob(workspaceId, jobId);
    public IBatchCheckpointWriter CheckpointWriter(Workspace workspace) => store.CheckpointWriter(workspace);
    public void SaveConnectionSettings(Workspace workspace, string clientId, string secret, string url, bool localRouteTimes)
        => store.SaveConnectionSettings(workspace, clientId, secret, url, localRouteTimes);
    public void UpdateOperationsCredentials(Workspace workspace, string clientId, string secret)
        => store.UpdateOperationsCredentials(workspace, clientId, secret);
    public MaintenancePreview PreviewMaintenance(Guid workspaceId)
    {
        var p = store.PreviewMaintenance(workspaceId);
        return new(p.WorkspaceId, p.Name, p.DataDirectory, p.Revision, p.Snapshots, p.Drafts, p.Jobs, p.Conflicts, p.HistorySteps, p.CanClear);
    }
    public long StoredRevision(Guid workspaceId) => store.CountRows(workspaceId).Revision;
    public async Task<MaintenanceOutcome> BackupAndClearAsync(Guid workspaceId, long expectedRevision)
    {
        var result = await store.BackupAndClearAsync(workspaceId, expectedRevision);
        return new(result.Workspace, result.Backup.DatabasePath);
    }

    public string? LoadSecret(Guid workspaceId) => store.LoadSecret(workspaceId);

    public string LoadLanguage() => store.LoadLanguage();
    public void SaveLanguage(string language) => store.SaveLanguage(language);
    public string LoadTableDensity() => store.LoadTableDensity();
    public void SaveTableDensity(string density) => store.SaveTableDensity(density);
    public IReadOnlyList<string> LoadHiddenColumns(ResourceKind kind) => store.LoadHiddenColumns(kind);
    public void SaveHiddenColumns(ResourceKind kind, IReadOnlyCollection<string> columns) => store.SaveHiddenColumns(kind, columns);
}

// The autosave quiet-period timer, backed by the dispatcher.
public sealed class DispatcherAutosaveTimer : IAutosaveTimer
{
    readonly Microsoft.UI.Xaml.DispatcherTimer timer;
    public DispatcherAutosaveTimer(TimeSpan interval) => timer = new() { Interval = interval };
    public bool IsEnabled => timer.IsEnabled;
    public void Start() => timer.Start();
    public void Stop() => timer.Stop();
    public event EventHandler<object> Tick { add => timer.Tick += value; remove => timer.Tick -= value; }
}
