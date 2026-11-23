using VamSys.Core;

namespace VamSys.UnitTests.Application;

// An in-memory database: workspaces are kept as their stored JSON, so a load returns what the last save wrote and never
// the live object. Every call is recorded, and any call can be made to fail or wait.
public sealed class FakeStorage : IWorkspaceStorage, IUserSettings
{
    public readonly Dictionary<Guid, string> Stored = [];
    public readonly Dictionary<Guid, BatchJob> StoredJobs = [];
    public readonly List<string> Calls = [];
    public readonly List<IReadOnlyCollection<DraftRowChanges>> RowSaves = [];
    public readonly List<(Guid Id, string Client, string Secret)> Credentials = [];
    public Func<Task>? BeforeSave;
    public Func<Exception?>? FailLoad;
    public bool FailJobExists, RowSaveAccepted = true, FailSaves;
    public MaintenancePreview? Preview;
    public Func<Task<MaintenanceOutcome>>? BackupResult;
    public long Revision;
    public List<BatchCheckpoint> Checkpoints = [];
    public string Language = "zh-CN", Density = "comfortable";

    public Workspace Seed(string name = "stored", RunMode mode = RunMode.Offline)
    {
        var w = new Workspace { Name = name, Mode = mode };
        Stored[w.Id] = w.Serialize();
        return w;
    }
    public Workspace Read(Guid id) => Workspace.Deserialize(Stored[id]);

    public IReadOnlyList<WorkspaceListing> List() => Stored.Select(p => new WorkspaceListing(p.Key, Workspace.Deserialize(p.Value).Name)).ToList();
    // Runs on whatever thread the session loads on; BeforeLoad lets a test hold the read there.
    public Action? BeforeLoad;
    public Workspace Load(Guid id, IWorkspaceLoadObserver? observer = null)
    {
        lock (Calls) Calls.Add("load");
        BeforeLoad?.Invoke();
        observer?.Stage(WorkspaceLoadStage.Rows);
        observer?.Rows(0, 0);
        if (FailLoad?.Invoke() is { } error)
            throw error;
        return Workspace.Deserialize(Stored[id]);
    }
    public void Save(Workspace workspace) { Calls.Add("save"); Stored[workspace.Id] = workspace.Serialize(); }
    public async Task SaveAsync(Workspace workspace)
    {
        Calls.Add("saveAll");
        if (BeforeSave != null) await BeforeSave();
        if (FailSaves) throw new IOException("injected");
        Stored[workspace.Id] = workspace.Serialize();
    }
    public async Task SaveChangesAsync(Workspace workspace, JournalScope scope)
    {
        Calls.Add("journal:" + scope);
        if (BeforeSave != null) await BeforeSave();
        if (FailSaves) throw new IOException("injected");
        Stored[workspace.Id] = workspace.Serialize();
    }
    public async Task<bool> TrySaveDraftRowsAsync(Workspace workspace, IReadOnlyCollection<DraftRowChanges> changes)
    {
        Calls.Add("rows");
        if (BeforeSave != null) await BeforeSave();
        if (FailSaves) throw new IOException("injected");
        if (!RowSaveAccepted)
            return false;
        RowSaves.Add(changes);
        Stored[workspace.Id] = workspace.Serialize();
        return true;
    }
    public bool JobExists(Guid workspaceId, Guid jobId)
    {
        if (FailJobExists) throw new IOException("injected");
        return Read(workspaceId).Jobs.Any(j => j.Id == jobId);
    }
    public BatchJob? LoadJob(Guid workspaceId, Guid jobId) => Read(workspaceId).Jobs.FirstOrDefault(j => j.Id == jobId);
    public IBatchCheckpointWriter CheckpointWriter(Workspace workspace) => new Writer(this);
    sealed class Writer(FakeStorage owner) : IBatchCheckpointWriter
    {
        public Task WriteAsync(BatchCheckpoint checkpoint, CancellationToken ct) { owner.Checkpoints.Add(checkpoint); return Task.CompletedTask; }
    }
    // What the store keeps of a typed client ID (the real store normalizes it).
    public Func<string, string> StoredClientId = id => id;
    public void SaveConnectionSettings(Workspace workspace, string clientId, string secret, string url, bool localRouteTimes)
    {
        Calls.Add("settings");
        workspace.ClientId = StoredClientId(clientId);
        workspace.LocalRouteTimes = localRouteTimes;
        Credentials.Add((workspace.Id, clientId, secret));
    }
    public void UpdateOperationsCredentials(Workspace workspace, string clientId, string secret)
    {
        Calls.Add("credentials");
        workspace.ClientId = StoredClientId(clientId);
        Credentials.Add((workspace.Id, clientId, secret));
    }
    public MaintenancePreview PreviewMaintenance(Guid workspaceId) => Preview ?? throw new InvalidOperationException("no preview");
    public long StoredRevision(Guid workspaceId) => Revision;
    public Task<MaintenanceOutcome> BackupAndClearAsync(Guid workspaceId, long expectedRevision)
    {
        Calls.Add("backup:" + expectedRevision);
        return BackupResult!();
    }

    public string LoadLanguage() => Language;
    public void SaveLanguage(string language) => Language = language;
    public string LoadTableDensity() => Density;
    public void SaveTableDensity(string density) => Density = density;
    public readonly Dictionary<ResourceKind, List<string>> HiddenColumns = [];
    public bool FailHiddenColumns;
    public IReadOnlyList<string> LoadHiddenColumns(ResourceKind kind) => HiddenColumns.TryGetValue(kind, out var columns) ? columns.ToList() : [];
    public void SaveHiddenColumns(ResourceKind kind, IReadOnlyCollection<string> columns)
    {
        if (FailHiddenColumns)
            throw new IOException("injected");
        HiddenColumns[kind] = columns.ToList();
    }
}

public sealed class FakeTimer : IAutosaveTimer
{
    public bool IsEnabled { get; private set; }
    public int Starts, Stops;
    public void Start() { IsEnabled = true; Starts++; }
    public void Stop() { IsEnabled = false; Stops++; }
}
