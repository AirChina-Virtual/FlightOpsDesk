using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using VamSys.Core;

namespace VamSys.Infrastructure;

// A workspace as it is written to the database: JSON text already produced, nothing left to fail inside the transaction.
internal sealed record StoredItem(Guid Id, int Ordinal, string Json);
internal sealed record StoredJob(Guid Id, int Ordinal, string Json, List<StoredItem> Items);
internal sealed record StoredRow(int Collection, Guid Id, int Ordinal, string Json);
internal sealed record StoredHistory(EditHistory Owner, HistorySnapshot Captured, string Undo, string Redo, List<(Guid Id, string Json)> Steps, ImmutableArray<Guid> Deletes, bool OrderChanged);
internal sealed record StoredResource(ResourceKind Kind, string Columns, string Conflicts, string? SnapshotAt, StoredHistory History, List<StoredRow> Rows);
internal sealed record FullPayload(Guid Id, string Name, long Revision, string Json, List<StoredJob> Jobs, List<StoredResource> Resources)
{
    // JSON bytes produced for this payload.
    public long Bytes { get; init; }
    // Tells every history that the captured steps are now durable.
    public void Acknowledge()
    { foreach (var resource in Resources) resource.History.Owner.Acknowledge(resource.History.Captured); }
}

// The JSON of one journal save, produced on the calling thread before the transaction starts (like FullPayload).
internal sealed record StoredPlacement(string Id, int Ordinal, string Json);
internal sealed record StoredRowList(int Collection, ChangeMode Mode, List<string> Deleted, List<(string Id, int Ordinal)> Moved, List<StoredPlacement> Replaced, List<StoredPlacement> Added, List<StoredPlacement> All);
internal sealed record StoredMetadata(string Columns, string Conflicts, string? SnapshotAt);
internal sealed record StoredResourceChange(ResourceKind Kind, List<StoredRowList> Lists, StoredMetadata? Metadata, StoredHistory? History);
internal sealed record StoredChanges(string? Name, string? HeaderJson, ChangeMode JobsMode, List<StoredJob> InsertedJobs, List<StoredJob> RewrittenJobs, List<StoredJob> AllJobs, List<StoredResourceChange> Resources)
{
    public long Bytes { get; init; }
    public bool IsEmpty => HeaderJson == null && JobsMode == ChangeMode.Unchanged && Resources.Count == 0;
    // Acknowledges every captured history once the changes are durable.
    public void Acknowledge()
    { foreach (var resource in Resources) if (resource.History is { } h) h.Owner.Acknowledge(h.Captured); }
}

// Row, job, item and history-step JSON. The text produced here is the database format; it is checked byte for byte by the storage goldens.
internal static class RowSerializer
{
    static readonly JsonSerializerOptions withoutCollections = CreateOptions();
    static JsonSerializerOptions CreateOptions()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            if (info.Type != typeof(Workspace) && info.Type != typeof(BatchJob))
                return;
            var property = info.Properties.Single(p => p.Name == (info.Type == typeof(Workspace) ? "Jobs" : "Items"));
            info.Properties.Remove(property);
            if (info.Type == typeof(Workspace))
                info.Properties.Remove(info.Properties.Single(p => p.Name == "Resources"));
        });
        return new() { TypeInfoResolver = resolver };
    }
    // header: Workspace without Jobs/Resources, BatchJob without Items.
    public static string Encode<T>(T value, bool header = false) => JsonSerializer.Serialize(value, header ? withoutCollections : null);
    public static T? Decode<T>(string json) => JsonSerializer.Deserialize<T>(json);

    public static StoredHistory CaptureHistory(EditHistory history, bool full = false, HistoryChanges? changes = null)
    {
        var delta = changes ?? history.Changes();
        var state = delta.Captured;
        return new(history, state, Encode(state.Undo.Select(s => s.StepId)), Encode(state.Redo.Select(s => s.StepId)),
            (full ? state.Undo.Concat(state.Redo) : delta.Upserts).Select(s => (s.StepId, Encode(s))).ToList(), delta.Deletes, full || delta.OrderChanged);
    }
    public static IEnumerable<string> HistoryJson(StoredHistory h) => h.Steps.Select(s => s.Json).Concat(h.OrderChanged ? [h.Undo, h.Redo] : []);
    public static List<StoredResource> CaptureResources(Workspace w) => w.Resources.Select(p => new StoredResource(p.Key, Encode(p.Value.Columns), Encode(p.Value.Conflicts), p.Value.SnapshotAt?.ToString("O"), CaptureHistory(p.Value.History, full: true),
        p.Value.Snapshot.Select((r, n) => new StoredRow(0, r.LocalId, n, Encode(r))).Concat(p.Value.Draft.Select((r, n) => new StoredRow(1, r.LocalId, n, Encode(r)))).ToList())).ToList();

    // Identifies the header content. StorageRevision is left out: it changes with every save without being a change.
    static readonly System.Text.RegularExpressions.Regex revisionField = new("\"StorageRevision\":-?\\d+", System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.Compiled);
    public static string HeaderKey(Workspace w) => revisionField.Replace(Encode(w, true), "\"StorageRevision\":0");

    static StoredJob StoreJob(BatchJob job, int ordinal) => new(job.Id, ordinal, Encode(job, true), job.Items.Select((i, k) => new StoredItem(i.Id, k, Encode(i))).ToList());
    static StoredRowList StoreRows(int collection, RowListChange change) => new(collection, change.Mode,
        [.. change.Deleted.Select(id => id.ToString())], [.. change.Moved.Select(m => (m.Id.ToString(), m.Ordinal))],
        [.. change.Replaced.Select(p => new StoredPlacement(p.Row.LocalId.ToString(), p.Ordinal, Encode(p.Row)))],
        [.. change.Added.Select(p => new StoredPlacement(p.Row.LocalId.ToString(), p.Ordinal, Encode(p.Row)))],
        [.. change.All.Select((r, n) => new StoredPlacement(r.LocalId.ToString(), n, Encode(r)))]);

    // Serializes a journal plan. `header` supplies the header JSON (the workspace itself, or a staged copy with new connection settings).
    public static StoredChanges CaptureChanges(Workspace w, JournalPlan plan, Workspace header)
    {
        var start = Stopwatch.GetTimestamp();
        var json = plan.HeaderChanged ? Encode(header, true) : null;
        var jobs = plan.Jobs;
        var inserted = jobs.Inserted.Select(p => StoreJob(p.Job, p.Ordinal)).ToList();
        var rewritten = jobs.Rewritten.Select(p => StoreJob(p.Job, p.Ordinal)).ToList();
        var all = jobs.All.Select(StoreJob).ToList();
        var resources = new List<StoredResourceChange>();
        var byKind = plan.Resources.ToDictionary(r => r.Kind);
        if (plan.Scope.HasFlag(JournalScope.Resources))
            foreach (var (kind, data) in w.Resources)
            {
                var rows = byKind.GetValueOrDefault(kind);
                var history = data.History.Changes();
                StoredHistory? stored = history.Upserts.Length > 0 || history.Deletes.Length > 0 || history.OrderChanged ? CaptureHistory(data.History, changes: history) : null;
                if (rows == null && stored == null)
                    continue;
                var lists = new List<StoredRowList>();
                if (rows != null && !rows.Snapshot.IsEmpty)
                    lists.Add(StoreRows(0, rows.Snapshot));
                if (rows != null && !rows.Draft.IsEmpty)
                    lists.Add(StoreRows(1, rows.Draft));
                var metadata = rows is { MetadataChanged: true } ? new StoredMetadata(Encode(rows.Columns.ToList()), Encode(rows.Conflicts.ToList()), rows.SnapshotAt?.ToString("O")) : null;
                resources.Add(new(kind, lists, metadata, stored));
            }
        var bytes = StorageMetrics.Measured(start, [json, .. inserted.Concat(rewritten).Concat(all).SelectMany(j => new[] { j.Json }.Concat(j.Items.Select(i => i.Json))),
            .. resources.SelectMany(r => r.Lists.SelectMany(l => l.Replaced.Concat(l.Added).Concat(l.All).Select(p => p.Json)).Concat(r.Metadata == null ? [] : [r.Metadata.Columns, r.Metadata.Conflicts]).Concat(r.History == null ? [] : HistoryJson(r.History)))]);
        return new StoredChanges(plan.HeaderChanged ? header.Name : null, json, jobs.Mode, inserted, rewritten, all, resources) { Bytes = bytes };
    }

    public static FullPayload Capture(Workspace w, bool measure = true)
    {
        var start = Stopwatch.GetTimestamp();
        var jobs = w.Jobs.Select((j, n) => new StoredJob(j.Id, n, Encode(j, true), j.Items.Select((i, k) => new StoredItem(i.Id, k, Encode(i))).ToList())).ToList();
        var resources = CaptureResources(w);
        var json = Encode(w, true);
        long bytes = 0;
        if (measure)
            bytes = StorageMetrics.Measured(start, [json, .. jobs.SelectMany(j => new[] { j.Json }.Concat(j.Items.Select(i => i.Json))), .. resources.SelectMany(r => new[] { r.Columns, r.Conflicts }.Concat(HistoryJson(r.History)).Concat(r.Rows.Select(row => row.Json)))]);
        return new(w.Id, w.Name, w.StorageRevision, json, jobs, resources) { Bytes = bytes };
    }
}

internal static class StorageMetrics
{
    public static long Measured(long started, params string?[] json)
    {
        var bytes = json.Where(j => j != null).Sum(j => (long)Encoding.UTF8.GetByteCount(j!));
        PerformanceRun.Current?.Serialized(bytes, Stopwatch.GetElapsedTime(started));
        return bytes;
    }
}
