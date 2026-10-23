using System.Diagnostics;
using VamSys.Core;
using static VamSys.Infrastructure.RowSerializer;

namespace VamSys.Infrastructure;

// Whole-workspace saves, journal saves and the cell-edit save. The payload is always captured (serialized) before the
// unit of work starts, so a serialization failure cannot leave a half-written transaction, and histories and the journal
// are acknowledged only after the commit succeeded.
internal sealed class WorkspaceWriter(UnitOfWork unit)
{
    // The most recent successful save through this writer.
    public SaveReport? LastSave { get; private set; }

    public long SavePayload(FullPayload p, byte[]? secret = null)
        => unit.Commit(p.Id, p.Revision, "Full", (c, tx) =>
        {
            WorkspaceRepository.Upsert(c, tx, p);
            JobRepository.Write(c, tx, p);
            ResourceRowRepository.Write(c, tx, p);
            if (secret != null)
                SecretStore.Upsert(c, tx, p.Id, secret);
        }, true);

    static SaveReport FullReport(FullPayload p) => new("Full", p.Resources.Sum(r => r.Rows.Count), 0, 0, p.Resources.Count, p.Resources.Sum(r => r.History.Steps.Count), p.Jobs.Count, p.Jobs.Sum(j => j.Items.Count), true, p.Bytes);

    // After a full save the journal equals what was captured with the payload.
    void Finish(Workspace w, FullPayload p, JournalCapture capture, long revision)
    {
        w.StorageRevision = revision;
        p.Acknowledge();
        w.Journal().Adopt(capture, revision);
        LastSave = FullReport(p);
    }

    public void Save(Workspace w)
    {
        var p = Capture(w);
        var capture = w.Journal().Capture(w, HeaderKey(w));
        Finish(w, p, capture, SavePayload(p));
    }
    public async Task SaveAsync(Workspace w)
    {
        var p = Capture(w);
        var capture = w.Journal().Capture(w, HeaderKey(w));
        Finish(w, p, capture, await Task.Run(() => SavePayload(p)));
    }

    // Compatibility entry point for callers exchanging a complete workspace JSON.
    public void SaveJson(Guid id, string name, string json)
    {
        var w = Workspace.Deserialize(json);
        if (w.Id != id || w.Name != name)
            throw StorageErrors.Block("StorageInvalid");
        Save(w);
    }

    // Journal save: writes only what changed since the database was last synchronised with the workspace. `header` is
    // the workspace whose header is stored (a staged copy when connection settings change); `secret` is stored in the
    // same transaction. When the journal cannot be trusted, or the incremental write does not fit the database, the
    // whole workspace is written instead. On any failure the journal asks for a full save next time.
    public void SaveChanges(Workspace w, JournalScope scope = JournalScope.All, Workspace? header = null, byte[]? secret = null)
        => SaveChangesCore(w, scope, header ?? w, secret, background: false).GetAwaiter().GetResult();
    public Task SaveChangesAsync(Workspace w, JournalScope scope = JournalScope.All, Workspace? header = null, byte[]? secret = null)
        => SaveChangesCore(w, scope, header ?? w, secret, background: true);

    async Task SaveChangesCore(Workspace w, JournalScope scope, Workspace header, byte[]? secret, bool background)
    {
        var plan = w.Journal().Plan(w, HeaderKey(header), w.StorageRevision, scope);
        if (plan == null)
        { await FullFallback(w, header, secret, background); return; }
        var changes = CaptureChanges(w, plan, header);
        if (changes.IsEmpty && secret == null)
        {
            LastSave = new("Journal", 0, 0, 0, 0, 0, 0, 0, false, 0);
            return;
        }
        var expected = w.StorageRevision;
        SaveReport? report = null;
        long next;
        try
        {
            long Commit() => unit.Commit(w.Id, expected, "Journal", (c, tx) =>
            {
                report = JournalRepository.Write(c, tx, w.Id, changes);
                if (secret != null)
                    SecretStore.Upsert(c, tx, w.Id, secret);
            });
            next = background ? await Task.Run(Commit) : Commit();
        }
        catch (InvalidOperationException e) when (MessageErrors.Describe(e).Code == "StorageInvalid")
        {
            // The database is not what the journal assumed: repeat as a full save.
            w.Journal().RequireFullSave();
            await FullFallback(w, header, secret, background);
            return;
        }
        catch
        {
            w.Journal().RequireFullSave();
            throw;
        }
        w.StorageRevision = next;
        changes.Acknowledge();
        w.Journal().Accept(plan, next);
        LastSave = report! with { JsonBytes = changes.Bytes };
    }

    async Task FullFallback(Workspace w, Workspace header, byte[]? secret, bool background)
    {
        var p = Capture(header);
        var capture = w.Journal().Capture(w, HeaderKey(header));
        var revision = background ? await Task.Run(() => SavePayload(p, secret)) : SavePayload(p, secret);
        Finish(w, p, capture, revision);
    }

    // Saves edited draft cells without rewriting unrelated rows, resources or jobs. Only valid when the database
    // holds the workspace at w.StorageRevision and the edits changed field values in place (no added, removed or
    // reordered rows, and no column, conflict or snapshot changes). Returns false when a row is gone from memory;
    // the caller then falls back to a full save.
    public async Task<bool> TrySaveDraftRowsAsync(Workspace w, IReadOnlyCollection<DraftRowChanges> changes)
    {
        var start = Stopwatch.GetTimestamp();
        var rows = new List<(int Kind, string Id, string Json)>();
        var written = new List<(ResourceKind Kind, DataRow Row)>();
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
                    written.Add((change.Resource, row));
                }
            }
            history.Add((change.Resource, CaptureHistory(data.History, changes: change.History)));
        }
        var bytes = StorageMetrics.Measured(start, [.. rows.Select(r => r.Json), .. history.SelectMany(h => HistoryJson(h.Payload))]);
        var workspace = w.Id.ToString();
        var expected = w.StorageRevision;
        var next = await Task.Run(() => unit.Commit(w.Id, expected, "Draft", (c, tx) =>
        {
            ResourceRowRepository.UpdateDraftRows(c, tx, workspace, rows);
            foreach (var h in history)
                HistoryRepository.Write(c, tx, w.Id, h.Kind, h.Payload);
        }));
        w.StorageRevision = next;
        foreach (var h in history)
            h.Payload.Owner.Acknowledge(h.Payload.Captured);
        // The rows were rewritten at their positions, so the journal's idea of the database moves with them.
        if (w.Journal().IsTracking(expected))
        {
            foreach (var group in written.GroupBy(r => r.Kind))
                w.Journal().AcceptDraftRows(group.Key, [.. group.Select(r => r.Row)]);
            w.Journal().AfterExternalWrite(expected, next);
        }
        LastSave = new("Draft", rows.Count, 0, 0, 0, history.Sum(h => h.Payload.Steps.Count + h.Payload.Deletes.Length), 0, 0, false, bytes);
        return true;
    }
}
