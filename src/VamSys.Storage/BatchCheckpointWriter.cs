using System.Diagnostics;
using VamSys.Core;
using static VamSys.Infrastructure.RowSerializer;

namespace VamSys.Infrastructure;

// Persists one execution checkpoint (task header, current record(s), optional rebase) in a single unit of work, so the
// next request is only sent after this state is durable.
internal sealed class BatchCheckpointWriter(UnitOfWork unit, Workspace workspace) : IBatchCheckpointWriter
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
        var next = await Task.Run(() => unit.Commit(workspace.Id, expected, checkpoint.Kind.ToString(), (c, tx) =>
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
