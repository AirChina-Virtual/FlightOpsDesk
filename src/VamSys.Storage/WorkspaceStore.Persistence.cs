using Microsoft.Data.Sqlite;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using VamSys.Core;
using static VamSys.Infrastructure.Sql;
using static VamSys.Infrastructure.RowSerializer;

namespace VamSys.Infrastructure;

public sealed partial class WorkspaceStore
{
    long Commit(Guid id, long expected, string kind, Action<SqliteConnection, SqliteTransaction> write, bool allowNew = false)
    {
        lock (writeLock)
        {
            var metrics = PerformanceRun.Current;
            metrics?.Saving();
            metrics?.Checkpoint(kind);
            var start = Stopwatch.GetTimestamp();
            var committed = false;
            try
            {
                using var c = Open();
                using var tx = c.BeginTransaction();
                var revision = WorkspaceRepository.ReadRevision(c, tx, id);
                if (revision == null ? !allowNew || expected != 0 : Convert.ToInt64(revision) != expected)
                    throw StorageErrors.Block("StorageStale");
                write(c, tx);
                var next = expected + 1;
                WorkspaceRepository.WriteRevision(c, tx, id, next);
                context.Fault(kind + ":BeforeCommit");
                tx.Commit();
                committed = true;
                metrics?.Saved(true, Stopwatch.GetElapsedTime(start));
                context.Fault(kind + ":AfterCommit");
                return next;
            }
            catch { if (!committed) metrics?.Saved(false, Stopwatch.GetElapsedTime(start)); throw; }
        }
    }
    internal long SavePayload(FullPayload p, byte[]? secret = null)
        => Commit(p.Id, p.Revision, "Full", (c, tx) =>
        {
            WorkspaceRepository.Upsert(c, tx, p);
            JobRepository.Write(c, tx, p);
            ResourceRowRepository.Write(c, tx, p);
            if (secret != null)
                Execute(c, tx, "INSERT INTO secrets(id,value) VALUES($id,$s) ON CONFLICT(id) DO UPDATE SET value=$s", ("$id", p.Id.ToString()), ("$s", secret));
        }, true);
    public void Save(Workspace w) { var p = Capture(w); w.StorageRevision = SavePayload(p); p.Acknowledge(); }
    public async Task SaveAsync(Workspace w)
    { var p = Capture(w); w.StorageRevision = await Task.Run(() => SavePayload(p)); p.Acknowledge(); }
    // Compatibility entry point for callers exchanging a complete workspace JSON.
    public void SaveJson(Guid id, string name, string json)
    {
        var w = Workspace.Deserialize(json);
        if (w.Id != id || w.Name != name)
            throw StorageErrors.Block("StorageInvalid");
        Save(w);
    }
    public static string SerializeForSave(Workspace w) => w.Serialize();
    // Saves edited draft cells without rewriting unrelated rows, resources or jobs. Only valid when the database
    // holds the workspace at w.StorageRevision and the edits changed field values in place (no added, removed or
    // reordered rows, and no column, conflict or snapshot changes). Returns false when a row is gone from memory;
    // the caller then falls back to a full save.
    public async Task<bool> TrySaveDraftRowsAsync(Workspace w, IReadOnlyCollection<DraftRowChanges> changes)
    {
        var start = Stopwatch.GetTimestamp();
        var rows = new List<(int Kind, string Id, string Json)>();
        var history = new List<(ResourceKind Kind, StoredHistory Payload)>();
        foreach (var change in changes)
        {
            var data = w.Resources[change.Resource];
            if (change.Rows.Count > 0)
            {
                foreach (var id in change.Rows)
                {
                    if (data.FindDraftRow(id) is not { } row)
                        return false;
                    rows.Add(((int)change.Resource, id.ToString(), Encode(row)));
                }
            }
            history.Add((change.Resource, CaptureHistory(data.History, changes: change.History)));
        }
        StorageMetrics.Measured(start, [.. rows.Select(r => r.Json), .. history.SelectMany(h => HistoryJson(h.Payload))]);
        var workspace = w.Id.ToString();
        var expected = w.StorageRevision;
        w.StorageRevision = await Task.Run(() => Commit(w.Id, expected, "Draft", (c, tx) =>
        {
            ResourceRowRepository.UpdateDraftRows(c, tx, workspace, rows);
            foreach (var h in history)
                HistoryRepository.Write(c, tx, w.Id, h.Kind, h.Payload);
        }));
        foreach (var h in history)
            h.Payload.Owner.Acknowledge(h.Payload.Captured);
        return true;
    }
    public IBatchCheckpointWriter CheckpointWriter(Workspace w) => new Writer(this, w);
    sealed class Writer(WorkspaceStore store, Workspace workspace) : IBatchCheckpointWriter
    {
        public async Task WriteAsync(BatchCheckpoint checkpoint, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var start = Stopwatch.GetTimestamp();
            var expected = workspace.StorageRevision;
            var header = Encode(checkpoint.Job, true);
            var item = checkpoint.Item == null ? null : Encode(checkpoint.Item);
            var items = checkpoint.Items?.Select(i => (Id: i.Id.ToString(), Json: Encode(i))).ToList() ?? [];
            var delta = checkpoint.Delta;
            var full = checkpoint.RebasedWorkspace == null ? null : Capture(checkpoint.RebasedWorkspace);
            if ((checkpoint.Kind == CheckpointKind.Rebase) != (delta != null || full != null) || delta != null && full != null || checkpoint.Kind == CheckpointKind.Rebase && item == null || full != null && full.Id != workspace.Id)
                throw StorageErrors.Block("StorageInvalid");
            if (checkpoint.Items != null && (checkpoint.Kind != CheckpointKind.Task || item != null || items.Select(i => i.Id).Distinct().Count() != items.Count))
                throw StorageErrors.Block("StorageInvalid");
            if (delta != null && (checkpoint.Item!.Resource != delta.Resource || checkpoint.Item.After.LocalId != delta.LocalId || new[] { delta.Snapshot, delta.Draft }.Any(m => m.Action == RowMutationKind.Upsert ? (m.Row == null || m.Row.LocalId != delta.LocalId) : m.Row != null)))
                throw StorageErrors.Block("StorageInvalid");
            string? snapshot = delta?.Snapshot.Row is { } sr ? Encode(sr) : null, draft = delta?.Draft.Row is { } dr ? Encode(dr) : null;
            string? root = null;
            if (delta?.VerifiedOperation != null)
            {
                var staged = workspace.StageResource(delta.Resource, workspace.Resources[delta.Resource]);
                if (!staged.VerifiedOperations.Contains(delta.VerifiedOperation))
                    staged.VerifiedOperations.Add(delta.VerifiedOperation);
                root = Encode(staged, true);
            }
            StorageMetrics.Measured(start, [header, item, root, snapshot, draft, .. items.Select(i => i.Json)]);
            var w = workspace.Id.ToString();
            var j = checkpoint.Job.Id.ToString();
            var i = checkpoint.Item?.Id.ToString();
            var next = await Task.Run(() => store.Commit(workspace.Id, expected, checkpoint.Kind.ToString(), (c, tx) =>
            {
                if (JobRepository.UpdateHeader(c, tx, w, j, header) != 1)
                    throw StorageErrors.Block("StorageInvalid");
                if (item != null && JobRepository.UpdateItem(c, tx, w, j, i!, item) != 1)
                    throw StorageErrors.Block("StorageInvalid");
                foreach (var (id, json) in items)
                    if (JobRepository.UpdateItem(c, tx, w, j, id, json) != 1)
                        throw StorageErrors.Block("StorageInvalid");
                if (full != null)
                { WorkspaceRepository.UpdateNameAndJson(c, tx, workspace.Id, full.Name, full.Json); ResourceRowRepository.Write(c, tx, full); }
                if (delta != null)
                { ResourceRowRepository.WriteDelta(c, tx, workspace.Id, delta, snapshot, draft); if (root != null) WorkspaceRepository.UpdateJson(c, tx, workspace.Id, root); }
            }));
            workspace.StorageRevision = next;
            if (full != null)
                full.Acknowledge();
        }
    }
}
