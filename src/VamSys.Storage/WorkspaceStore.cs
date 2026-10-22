using Microsoft.Data.Sqlite;
using VamSys.Core;

namespace VamSys.Infrastructure;

// The public face of local storage. Every method delegates to one repository or service; callers and tests only see this type.
public sealed partial class WorkspaceStore
{
    public const int FormatVersion = SchemaV4.Version;
    readonly StorageContext context;
    readonly SettingsRepository settings;
    readonly SecretStore secrets;
    readonly UnitOfWork unit;
    readonly WorkspaceWriter writer;
    readonly MaintenanceService maintenance;
#if VAMSYS_TEST_SUPPORT
    public WorkspaceStore(string directory) : this(directory,null) {}
    internal WorkspaceStore(string directory,Action<string>? barrier)
#else
    public WorkspaceStore(string directory)
#endif
    {
        context = new(directory);
        settings = new(context);
        secrets = new(context);
        unit = new(context);
        writer = new(unit);
        maintenance = new(context, unit);
#if VAMSYS_TEST_SUPPORT
        Barrier=barrier;
#endif
        lock (context.WriteLock)
            Upgrade();
    }
    // Path of the backup made when this open upgraded an older database; null when no upgrade was needed.
    public string? MigrationBackup { get; private set; }
    void Upgrade()
    {
        var runner = new MigrationRunner(context);
        try
        {
            using var c = Open();
            runner.Run(c);
        }
        finally { MigrationBackup = runner.Backup; }
    }
    SqliteConnection Open() => context.Connections.Open();

    public string LoadLanguage() => settings.LoadLanguage();
    public void SaveLanguage(string language) => settings.SaveLanguage(language);
    public string LoadTableDensity() => settings.LoadTableDensity();
    public void SaveTableDensity(string density) => settings.SaveTableDensity(density);

    public List<(Guid Id, string Name)> List()
    {
        using var c = Open();
        return WorkspaceRepository.List(c);
    }
    public Workspace Load(Guid id)
    {
        using var c = Open();
        return WorkspaceRepository.Load(c, id);
    }

    public void Save(Workspace w) => writer.Save(w);
    public Task SaveAsync(Workspace w) => writer.SaveAsync(w);
    public void SaveJson(Guid id, string name, string json) => writer.SaveJson(id, name, json);
    public static string SerializeForSave(Workspace w) => w.Serialize();
    public Task<bool> TrySaveDraftRowsAsync(Workspace w, IReadOnlyCollection<DraftRowChanges> changes) => writer.TrySaveDraftRowsAsync(w, changes);
    public IBatchCheckpointWriter CheckpointWriter(Workspace w) => new BatchCheckpointWriter(unit, w);
    internal long SavePayload(FullPayload p, byte[]? secret = null) => writer.SavePayload(p, secret);

    public string DataDirectory => context.Connections.DataDirectory;
    public static bool HasUnresolvedWrites(Workspace workspace) => MaintenanceService.HasUnresolvedWrites(workspace);
    public WorkspaceMaintenancePreview PreviewMaintenance(Guid id) => maintenance.Preview(id);
    public Task<WorkspaceMaintenanceResult> BackupAndClearAsync(Guid id, long expectedRevision, CancellationToken ct = default)
        => Task.Run(() => maintenance.BackupAndClear(id, expectedRevision, ct), ct);

    public void SaveSecret(Guid id, string secret) => secrets.Save(id, secret);
    public string? LoadSecret(Guid id) => secrets.Load(id);
    public void UpdateOperationsCredentials(Workspace workspace, string clientId, string secret)
        => SaveConnection(workspace, clientId, secret, true, null, null);
    public void SaveConnectionSettings(Workspace workspace, string clientId, string secret, string url, bool localRouteTimes)
    {
        if (ApiOperationPolicy.HasOpenOnlineItems(workspace))
            throw StorageErrors.Block("ApiPendingJob");
        url = url.Trim();
        if (url != "" && (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo != ""))
            throw StorageErrors.Block("ApiInvalid", "URL");
        SaveConnection(workspace, clientId, secret, false, url, localRouteTimes);
    }
    void SaveConnection(Workspace workspace, string clientId, string secret, bool requireNewSecret, string? url, bool? localRouteTimes)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException();
        clientId = clientId.Trim();
        var credentials = secrets.ResolveCredentials(workspace.Id, clientId, secret, requireNewSecret);
        var staged = Workspace.Deserialize(workspace.Serialize());
        staged.ClientId = clientId;
        staged.Mode = RunMode.Offline;
        staged.ConnectedAt = null;
        if (url != null)
            staged.InstanceUrl = url;
        if (localRouteTimes.HasValue)
            staged.LocalRouteTimes = localRouteTimes.Value;
        var encrypted = credentials == null ? null : SecretStore.Protect(credentials, workspace.Id);
        workspace.StorageRevision = SavePayload(RowSerializer.Capture(staged), encrypted);
        foreach (var data in workspace.Resources.Values)
            data.History.Acknowledge(data.History.Capture());
        workspace.ClientId = staged.ClientId;
        workspace.Mode = RunMode.Offline;
        workspace.ConnectedAt = null;
        workspace.InstanceUrl = staged.InstanceUrl;
        workspace.LocalRouteTimes = staged.LocalRouteTimes;
    }
}
