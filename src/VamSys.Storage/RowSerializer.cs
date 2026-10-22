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
    // Tells every history that the captured steps are now durable.
    public void Acknowledge()
    { foreach (var resource in Resources) resource.History.Owner.Acknowledge(resource.History.Captured); }
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

    public static FullPayload Capture(Workspace w, bool measure = true)
    {
        var start = Stopwatch.GetTimestamp();
        var jobs = w.Jobs.Select((j, n) => new StoredJob(j.Id, n, Encode(j, true), j.Items.Select((i, k) => new StoredItem(i.Id, k, Encode(i))).ToList())).ToList();
        var resources = CaptureResources(w);
        var json = Encode(w, true);
        if (measure)
            StorageMetrics.Measured(start, [json, .. jobs.SelectMany(j => new[] { j.Json }.Concat(j.Items.Select(i => i.Json))), .. resources.SelectMany(r => new[] { r.Columns, r.Conflicts }.Concat(HistoryJson(r.History)).Concat(r.Rows.Select(row => row.Json)))]);
        return new(w.Id, w.Name, w.StorageRevision, json, jobs, resources);
    }
}

internal static class StorageMetrics
{
    public static void Measured(long started, params string?[] json)
        => PerformanceRun.Current?.Serialized(json.Where(j => j != null).Sum(j => (long)Encoding.UTF8.GetByteCount(j!)), Stopwatch.GetElapsedTime(started));
}
