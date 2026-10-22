using System.Diagnostics;
using VamSys.Core;
using static VamSys.Infrastructure.RowSerializer;

namespace VamSys.Infrastructure;

// Whole-workspace saves and the cell-edit save. The payload is always captured (serialized) before the unit of work
// starts, so a serialization failure cannot leave a half-written transaction, and histories are acknowledged only after
// the commit succeeded.
internal sealed class WorkspaceWriter(UnitOfWork unit)
{
    public long SavePayload(FullPayload p, byte[]? secret = null)
        => unit.Commit(p.Id, p.Revision, "Full", (c, tx) =>
        {
            WorkspaceRepository.Upsert(c, tx, p);
            JobRepository.Write(c, tx, p);
            ResourceRowRepository.Write(c, tx, p);
            if (secret != null)
                SecretStore.Upsert(c, tx, p.Id, secret);
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
        w.StorageRevision = await Task.Run(() => unit.Commit(w.Id, expected, "Draft", (c, tx) =>
        {
            ResourceRowRepository.UpdateDraftRows(c, tx, workspace, rows);
            foreach (var h in history)
                HistoryRepository.Write(c, tx, w.Id, h.Kind, h.Payload);
        }));
        foreach (var h in history)
            h.Payload.Owner.Acknowledge(h.Payload.Captured);
        return true;
    }
}
