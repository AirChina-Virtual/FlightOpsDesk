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
    static void WriteJobs(SqliteConnection c, SqliteTransaction tx, FullPayload p)
    {
        Execute(c, tx, "DELETE FROM batch_jobs WHERE workspace_id=$w", ("$w", p.Id.ToString()));
        foreach (var job in p.Jobs)
        {
            Execute(c, tx, "INSERT INTO batch_jobs(workspace_id,id,ordinal,json) VALUES($w,$j,$n,$json)", ("$w", p.Id.ToString()), ("$j", job.Id.ToString()), ("$n", job.Ordinal), ("$json", job.Json));
            foreach (var item in job.Items)
                Execute(c, tx, "INSERT INTO batch_items(workspace_id,job_id,id,ordinal,json) VALUES($w,$j,$i,$n,$json)", ("$w", p.Id.ToString()), ("$j", job.Id.ToString()), ("$i", item.Id.ToString()), ("$n", item.Ordinal), ("$json", item.Json));
        }
    }
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
                var revision = Scalar(c, tx, "SELECT revision FROM workspace_revisions WHERE workspace_id=$w", ("$w", id.ToString()));
                if (revision == null ? !allowNew || expected != 0 : Convert.ToInt64(revision) != expected)
                    throw StorageErrors.Block("StorageStale");
                write(c, tx);
                var next = expected + 1;
                Execute(c, tx, "INSERT INTO workspace_revisions(workspace_id,revision) VALUES($w,$r) ON CONFLICT(workspace_id) DO UPDATE SET revision=$r", ("$w", id.ToString()), ("$r", next));
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
            Execute(c, tx, "INSERT INTO workspaces(id,name,json,format_version) VALUES($id,$name,$json,4) ON CONFLICT(id) DO UPDATE SET name=$name,json=$json,format_version=4",
                ("$id", p.Id.ToString()), ("$name", p.Name), ("$json", p.Json));
            WriteJobs(c, tx, p);
            WriteResources(c, tx, p);
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
            using var update = Command(c, tx, "UPDATE resource_rows SET json=$json WHERE workspace_id=$w AND resource=$r AND collection=1 AND local_id=$id", ("$w", workspace), ("$r", 0), ("$id", ""), ("$json", ""));
            update.Prepare();
            foreach (var row in rows)
            {
                update.Parameters["$r"].Value = row.Kind;
                update.Parameters["$id"].Value = row.Id;
                update.Parameters["$json"].Value = row.Json;
                if (update.ExecuteNonQuery() != 1)
                    throw StorageErrors.Block("StorageInvalid");
            }
            foreach (var h in history)
                WriteHistory(c, tx, w.Id, h.Kind, h.Payload);
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
                if (Execute(c, tx, "UPDATE batch_jobs SET json=$json WHERE workspace_id=$w AND id=$j", ("$json", header), ("$w", w), ("$j", j)) != 1)
                    throw StorageErrors.Block("StorageInvalid");
                if (item != null && Execute(c, tx, "UPDATE batch_items SET json=$json WHERE workspace_id=$w AND job_id=$j AND id=$i", ("$json", item), ("$w", w), ("$j", j), ("$i", i)) != 1)
                    throw StorageErrors.Block("StorageInvalid");
                foreach (var (id, json) in items)
                    if (Execute(c, tx, "UPDATE batch_items SET json=$json WHERE workspace_id=$w AND job_id=$j AND id=$i", ("$json", json), ("$w", w), ("$j", j), ("$i", id)) != 1)
                        throw StorageErrors.Block("StorageInvalid");
                if (full != null)
                { Execute(c, tx, "UPDATE workspaces SET name=$name,json=$json WHERE id=$w", ("$name", full.Name), ("$json", full.Json), ("$w", w)); WriteResources(c, tx, full); }
                if (delta != null)
                { WriteDelta(c, tx, workspace.Id, delta, snapshot, draft); if (root != null) Execute(c, tx, "UPDATE workspaces SET json=$json WHERE id=$w", ("$json", root), ("$w", w)); }
            }));
            workspace.StorageRevision = next;
            if (full != null)
                full.Acknowledge();
        }
    }
}
