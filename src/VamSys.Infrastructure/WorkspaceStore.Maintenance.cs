using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text.Json;
using VamSys.Core;

namespace VamSys.Infrastructure;

public sealed record WorkspaceMaintenancePreview(Guid WorkspaceId, string Name, string DataDirectory, long Revision, int Snapshots, int Drafts, int Jobs, int Conflicts, int HistorySteps, bool CanClear);
public sealed record WorkspaceBackupReceipt(string DatabasePath, string ManifestPath, string Sha256, Guid WorkspaceId, long Revision);
public sealed record WorkspaceMaintenanceResult(Workspace Workspace, WorkspaceBackupReceipt Backup);

public sealed partial class WorkspaceStore
{
    public string DataDirectory => Path.GetDirectoryName(new SqliteConnectionStringBuilder(connectionString).DataSource)!;
    // A rebased, succeeded or manually confirmed item is final; its in-flight step stays as evidence.
    public static bool HasUnresolvedWrites(Workspace workspace) => workspace.Jobs.Any(j => j.Items.Any(i =>
        i.State is ItemState.Running or ItemState.Unknown
        || (i.ApiSteps.Any(s => s.State == BatchStepState.InFlight) || i.CreateCompleted || i.WriteAccepted) && !(i.Rebased && i.State is ItemState.Succeeded or ItemState.ManuallyConfirmed)));
    public WorkspaceMaintenancePreview PreviewMaintenance(Guid id)
    {
        var w = Load(id);
        return new(id, w.Name, DataDirectory, w.StorageRevision, w.Resources.Sum(p => p.Value.Snapshot.Count), w.Resources.Sum(p => p.Value.Draft.Count),
            w.Jobs.Count, w.Resources.Sum(p => p.Value.Conflicts.Count), w.Resources.Sum(p => p.Value.Undo.Count + p.Value.Redo.Count), !HasUnresolvedWrites(w));
    }
    public Task<WorkspaceMaintenanceResult> BackupAndClearAsync(Guid id, long expectedRevision, CancellationToken ct = default)
        => Task.Run(() => BackupAndClear(id, expectedRevision, ct), ct);
    WorkspaceMaintenanceResult BackupAndClear(Guid id, long expectedRevision, CancellationToken ct)
    {
        lock (writeLock)
        {
            ct.ThrowIfCancellationRequested();
            var current = Load(id);
            if (current.StorageRevision != expectedRevision)
                throw OperationsAdapter.Block("StorageStale");
            if (HasUnresolvedWrites(current))
                throw OperationsAdapter.Block("MaintenanceUnsafe");
            OnStorageBarrier("Maintenance:BeforeBackup");
            var directory = Path.Combine(DataDirectory, "Backups");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"workspaces-before-clear-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{id:N}-{Guid.NewGuid():N}.db");
            using (var source = Open())
            using (var backup = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
            {
                backup.Open();
                source.BackupDatabase(backup);
                if (!Equals(Scalar(backup, null, "PRAGMA integrity_check"), "ok"))
                    throw OperationsAdapter.Block("MaintenanceBackupFailed");
                Execute(backup, null, "PRAGMA wal_checkpoint(TRUNCATE)");
            }
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
            var manifest = path + ".json";
            File.WriteAllText(manifest, JsonSerializer.Serialize(new
            {
                formatVersion = 1,
                createdAtUtc = DateTimeOffset.UtcNow,
                workspaceId = id,
                workspaceName = current.Name,
                storageVersion = FormatVersion,
                revision = expectedRevision,
                databaseSha256 = hash,
                scope = "Full local database backup; only the selected workspace is cleared",
                snapshotRows = current.Resources.Sum(p => p.Value.Snapshot.Count),
                draftRows = current.Resources.Sum(p => p.Value.Draft.Count),
                jobs = current.Jobs.Count,
                conflicts = current.Resources.Sum(p => p.Value.Conflicts.Count),
                historySteps = current.Resources.Sum(p => p.Value.Undo.Count + p.Value.Redo.Count),
                remoteRollbackSupported = false
            }, new JsonSerializerOptions { WriteIndented = true }));
            OnStorageBarrier("Maintenance:AfterBackup");
            ct.ThrowIfCancellationRequested();
            current.Resources = Enum.GetValues<ResourceKind>().ToDictionary(k => k, _ => new ResourceData());
            current.Jobs = [];
            current.VerifiedOperations = [];
            current.Mode = RunMode.Offline;
            current.ConnectedAt = null;
            var payload = Capture(current);
            current.StorageRevision = Commit(id, expectedRevision, "Maintenance", (c, tx) =>
            {
                // Keep the workspace identity/settings and encrypted secrets. Never delete the workspace row.
                if (Execute(c, tx, "UPDATE workspaces SET json=$json WHERE id=$id", ("$json", payload.Json), ("$id", id.ToString())) != 1)
                    throw OperationsAdapter.Block("StorageInvalid");
                WriteJobs(c, tx, payload);
                WriteResources(c, tx, payload);
            });
            return new(current, new(path, manifest, hash, id, expectedRevision));
        }
    }
}
