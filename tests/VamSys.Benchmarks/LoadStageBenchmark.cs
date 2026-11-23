using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using VamSys.Core;
using VamSys.Infrastructure;

// --benchmark-load-stages <file.json>: where a workspace read spends its time and allocations. Seeds the 20 000- and
// 50 000-route workspaces of the window baseline (DataUiFixture.Seed), then per size:
// - loads it several times through WorkspaceStore.Load with a stage observer: time and bytes allocated on the reading
//   thread for each step (open, header and jobs, metadata, rows, sharing unchanged rows, history read, history
//   validation, draft index, journal baseline), and the retained managed heap of the loaded workspace;
// - splits the row step into reading the JSON text from SQLite and turning it into rows, by doing each alone;
// - samples allocations by type during one load (the runtime's AllocationTick event, about one sample per 100 KB);
// - counts the row values that are equal strings held as separate instances (what sharing equal values would save).
// (2026-11-23, stage 7 item 3: workspace load.)
static class LoadStageBenchmark
{
    const int Loads = 5;

    sealed class StageClock : IWorkspaceLoadObserver
    {
        readonly long start = Stopwatch.GetTimestamp(), startBytes = GC.GetAllocatedBytesForCurrentThread();
        long at, bytes;
        WorkspaceLoadStage? current;
        public readonly Dictionary<WorkspaceLoadStage, (double Ms, long Bytes)> Totals = [];
        public int RowsRead, Reports;
        public StageClock() { at = start; bytes = startBytes; }
        void Close()
        {
            var now = Stopwatch.GetTimestamp(); var allocated = GC.GetAllocatedBytesForCurrentThread();
            if (current is { } stage)
            {
                var (ms, b) = Totals.GetValueOrDefault(stage);
                Totals[stage] = (ms + Stopwatch.GetElapsedTime(at, now).TotalMilliseconds, b + allocated - bytes);
            }
            at = now; bytes = allocated;
        }
        public void Stage(WorkspaceLoadStage stage) { Close(); current = stage; }
        public void Rows(int read, int total) { RowsRead = read; Reports++; }
        public (double Ms, long Bytes) Finish() { Close(); current = null; return (Stopwatch.GetElapsedTime(start).TotalMilliseconds, GC.GetAllocatedBytesForCurrentThread() - startBytes); }
    }

    // AllocationTick samples (GC keyword, verbose): type name and bytes of each sample, summed per type.
    sealed class AllocationSampler : EventListener
    {
        public readonly Dictionary<string, long> ByType = [];
        public bool Recording;
        protected override void OnEventSourceCreated(EventSource source)
        {
            if (source.Name == "Microsoft-Windows-DotNETRuntime")
                EnableEvents(source, EventLevel.Verbose, (EventKeywords)0x1);
        }
        protected override void OnEventWritten(EventWrittenEventArgs e)
        {
            if (!Recording || e.EventName is not { } name || !name.StartsWith("GCAllocationTick", StringComparison.Ordinal) || e.Payload is null)
                return;
            var type = e.PayloadNames!.IndexOf("TypeName") is var t and >= 0 ? e.Payload[t] as string ?? "?" : "?";
            var amount = e.PayloadNames!.IndexOf("AllocationAmount64") is var a and >= 0 ? Convert.ToInt64(e.Payload[a]) : 0;
            lock (ByType) ByType[type] = ByType.GetValueOrDefault(type) + amount;
        }
    }

    static long Retained()
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        return GC.GetTotalMemory(false);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static (Workspace W, StageClock Clock, double Ms, long Bytes) Load(WorkspaceStore store, Guid id)
    {
        var clock = new StageClock();
        var w = store.Load(id, clock);
        var (ms, bytes) = clock.Finish();
        return (w, clock, ms, bytes);
    }

    // The row step alone: SQLite reading the JSON text, then the same text turned into rows.
    // The row as R0 (bd6c831) declared it: settable properties, the deserialized dictionary kept as it is.
    sealed class R0Row
    {
        public Guid LocalId { get; set; } = Guid.NewGuid();
        public ResourceIdentity? Identity { get; set; }
        public string? RawApiJson { get; set; }
        public Dictionary<string, string> Fields { get; set; } = new(StringComparer.Ordinal);
    }

    static (double SqlMs, long SqlBytes, double JsonMs, long JsonBytes, double R0Ms, long R0Bytes) SplitRows(string directory, Guid id)
    {
        var texts = new List<string>(110_000);
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "workspaces.db") }.ToString());
        c.Open();
        using var tx = c.BeginTransaction(deferred: true);
        var bytes = GC.GetAllocatedBytesForCurrentThread(); var start = Stopwatch.GetTimestamp();
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT resource,collection,json,ordinal FROM resource_rows WHERE workspace_id=$w ORDER BY resource,collection,ordinal";
            cmd.Parameters.AddWithValue("$w", id.ToString());
            using var r = cmd.ExecuteReader();
            while (r.Read()) { _ = r.GetInt32(0); _ = r.GetInt32(1); _ = r.GetInt32(3); texts.Add(r.GetString(2)); }
        }
        var sqlMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds; var sqlBytes = GC.GetAllocatedBytesForCurrentThread() - bytes;
        var rows = new List<DataRow>(texts.Count);
        bytes = GC.GetAllocatedBytesForCurrentThread(); start = Stopwatch.GetTimestamp();
        using (FieldNames.BeginScope())
            foreach (var text in texts)
                rows.Add(RowSerializer.Decode<DataRow>(text)!);
        var jsonMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds; var jsonBytes = GC.GetAllocatedBytesForCurrentThread() - bytes;
        GC.KeepAlive(rows);
        var r0 = new List<R0Row>(texts.Count);
        bytes = GC.GetAllocatedBytesForCurrentThread(); start = Stopwatch.GetTimestamp();
        foreach (var text in texts)
            r0.Add(JsonSerializer.Deserialize<R0Row>(text)!);
        var r0Ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds; var r0Bytes = GC.GetAllocatedBytesForCurrentThread() - bytes;
        GC.KeepAlive(r0);
        return (sqlMs, sqlBytes, jsonMs, jsonBytes, r0Ms, r0Bytes);
    }

    // Row values held as separate instances of an equal string: what one shared instance per value would save.
    static object Duplicates(Workspace w)
    {
        static long Size(string s) => (22 + 2L * s.Length + 7) / 8 * 8;
        var seen = new HashSet<string>(ReferenceEqualityComparer.Instance);
        var byValue = new Dictionary<string, int>(StringComparer.Ordinal);
        long all = 0, duplicates = 0; var instances = 0;
        foreach (var data in w.Resources.Values)
            foreach (var row in data.Snapshot.Concat(data.Draft))
                foreach (var pair in row.FieldEntries())
                {
                    var value = pair.Value;
                    if (!seen.Add(value))
                        continue;
                    instances++; all += Size(value);
                    if (byValue.TryGetValue(value, out var n)) { byValue[value] = n + 1; duplicates += Size(value); }
                    else byValue[value] = 1;
                }
        return new { ValueInstances = instances, DistinctValues = byValue.Count, ValueMiB = all / 1048576.0, DuplicateMiB = duplicates / 1048576.0 };
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static object Sample(WorkspaceStore store, string directory, Guid id, int count, int n)
    {
        var before = Retained();
        var (w, clock, ms, bytes) = Load(store, id);
        var retained = Retained() - before;
        GC.KeepAlive(w);
        var split = SplitRows(directory, id);
        Console.WriteLine($"{count,6:N0} routes, load {n + 1}: {ms,6:F0} ms, {bytes / 1048576.0,6:F1} MiB allocated, {retained / 1048576.0,5:F1} MiB retained; "
            + string.Join(", ", clock.Totals.OrderBy(p => p.Key).Select(p => $"{p.Key} {p.Value.Ms:F0}/{p.Value.Bytes / 1048576.0:F0}"))
            + $"; rows alone: SQLite {split.SqlMs:F0} ms/{split.SqlBytes / 1048576.0:F0} MiB, JSON {split.JsonMs:F0} ms/{split.JsonBytes / 1048576.0:F0} MiB, R0-shaped JSON {split.R0Ms:F0} ms/{split.R0Bytes / 1048576.0:F0} MiB");
        return new
        {
            LoadMs = ms, AllocatedMiB = bytes / 1048576.0, RetainedMiB = retained / 1048576.0, Rows = clock.RowsRead, ProgressReports = clock.Reports,
            Stages = clock.Totals.OrderBy(p => p.Key).ToDictionary(p => p.Key.ToString(), p => new { p.Value.Ms, MiB = p.Value.Bytes / 1048576.0 }),
            RowsAlone = new { split.SqlMs, SqlMiB = split.SqlBytes / 1048576.0, split.JsonMs, JsonMiB = split.JsonBytes / 1048576.0, R0ShapedJsonMs = split.R0Ms, R0ShapedJsonMiB = split.R0Bytes / 1048576.0 }
        };
    }

    public static void Run(string path)
    {
        var cases = new List<object>();
        foreach (var count in new[] { 20000, 50000 })
        {
            var directory = Path.Combine(Path.GetTempPath(), "vamsys-load-stages-" + Guid.NewGuid().ToString("N"));
            try
            {
                DataUiFixture.Seed(directory, count);
                var store = new WorkspaceStore(directory);
                var id = store.List().Single().Id;
                Load(store, id); // warm-up: JSON metadata, SQLite, statics
                SplitRows(directory, id);
                var samples = new List<object>();
                for (var n = 0; n < Loads; n++)
                {
                    samples.Add(Sample(store, directory, id, count, n));
                }
                Workspace loaded;
                Dictionary<string, long> types;
                using (var sampler = new AllocationSampler())
                {
                    Retained();
                    sampler.Recording = true;
                    loaded = store.Load(id);
                    sampler.Recording = false;
                    lock (sampler.ByType) types = new(sampler.ByType);
                }
                var byType = types.OrderByDescending(p => p.Value).Take(30).ToDictionary(p => p.Key, p => p.Value / 1048576.0);
                cases.Add(new { Routes = count, Samples = samples, SampledMiBByType = byType, SampledMiB = types.Values.Sum() / 1048576.0, Values = Duplicates(loaded) });
            }
            finally
            {
                using (var pooled = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "workspaces.db") }.ToString()))
                    SqliteConnection.ClearPool(pooled);
                try { Directory.Delete(directory, true); } catch (IOException) { }
            }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(new { Runtime = Environment.Version.ToString(), Server = System.Runtime.GCSettings.IsServerGC, Cases = cases }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
