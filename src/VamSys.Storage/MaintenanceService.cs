using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text.Json;
using VamSys.Core;
using static VamSys.Infrastructure.Sql;

namespace VamSys.Infrastructure;

public sealed record WorkspaceMaintenancePreview(Guid WorkspaceId, string Name, string DataDirectory, long Revision, int Snapshots, int Drafts, int Jobs, int Conflicts, int HistorySteps, bool CanClear);
public sealed record WorkspaceBackupReceipt(string DatabasePath, string ManifestPath, string Sha256, Guid WorkspaceId, long Revision);
public sealed record WorkspaceMaintenanceResult(Workspace Workspace, WorkspaceBackupReceipt Backup);

// Proactive "back up the whole database, then clear one workspace". Nothing is cleared unless the backup verified and the
// workspace is still at the version the user previewed; the clearing itself is one unit of work.
internal sealed class MaintenanceService(StorageContext context, UnitOfWork unit)
{
    // A rebased, succeeded or manually confirmed item is final; its in-flight step stays as evidence.
    public static bool HasUnresolvedWrites(Workspace workspace) => workspace.Jobs.Any(j => j.Items.Any(i =>
        i.State is ItemState.Running or ItemState.Unknown
        || (i.ApiSteps.Any(s => s.State == BatchStepState.InFlight) || i.CreateCompleted || i.WriteAccepted) && !(i.Rebased && i.State is ItemState.Succeeded or ItemState.ManuallyConfirmed)));

    Workspace Load(Guid id)
    {
        using var c = context.Connections.Open();
        return WorkspaceRepository.Load(c, id);
    }

    public WorkspaceMaintenancePreview Preview(Guid id)
    {
        var w = Load(id);
        return new(id, w.Name, context.Connections.DataDirectory, w.StorageRevision, w.Resources.Sum(p => p.Value.Snapshot.Count), w.Resources.Sum(p => p.Value.Draft.Count),
            w.Jobs.Count, w.Resources.Sum(p => p.Value.Conflicts.Count), w.Resources.Sum(p => p.Value.Undo.Count + p.Value.Redo.Count), !HasUnresolvedWrites(w));
    }

    public WorkspaceMaintenanceResult BackupAndClear(Guid id, long expectedRevision, CancellationToken ct)
    {
        lock (context.WriteLock)
        {
            ct.ThrowIfCancellationRequested();
            var current = Load(id);
            if (current.StorageRevision != expectedRevision)
                throw StorageErrors.Block("StorageStale");
            if (HasUnresolvedWrites(current))
                throw StorageErrors.Block("MaintenanceUnsafe");
            context.Fault("Maintenance:BeforeBackup");
            var directory = Path.Combine(context.Connections.DataDirectory, "Backups");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"workspaces-before-clear-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{id:N}-{Guid.NewGuid():N}.db");
            using (var source = context.Connections.Open())
            using (var backup = SqliteConnectionFactory.OpenBackupTarget(path))
            {
                source.BackupDatabase(backup);
                if (!Equals(Scalar(backup, null, "PRAGMA integrity_check"), "ok"))
                    throw StorageErrors.Block("MaintenanceBackupFailed");
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
                storageVersion = SchemaV4.Version,
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
            context.Fault("Maintenance:AfterBackup");
            ct.ThrowIfCancellationRequested();
            current.Resources = Enum.GetValues<ResourceKind>().ToDictionary(k => k, _ => new ResourceData());
            current.Jobs = [];
            current.VerifiedOperations = [];
            current.Mode = RunMode.Offline;
            current.ConnectedAt = null;
            var payload = RowSerializer.Capture(current);
            current.StorageRevision = unit.Commit(id, expectedRevision, "Maintenance", (c, tx) =>
            {
                // Keep the workspace identity/settings and encrypted secrets. Never delete the workspace row.
                if (WorkspaceRepository.UpdateJson(c, tx, id, payload.Json) != 1)
                    throw StorageErrors.Block("StorageInvalid");
                JobRepository.Write(c, tx, payload);
                ResourceRowRepository.Write(c, tx, payload);
            });
            return new(current, new(path, manifest, hash, id, expectedRevision));
        }
    }
}
