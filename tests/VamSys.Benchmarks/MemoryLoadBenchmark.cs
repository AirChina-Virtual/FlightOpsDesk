using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using VamSys.Core;
using VamSys.Infrastructure;

// --benchmark-memory-load <file.json>: the retained managed heap of a loaded workspace and the blocking full collection it
// costs. Seeds the 20 000- and 50 000-route workspaces of the window baseline (DataUiFixture.Seed: every route field
// filled, 20 history steps), loads each from the database several times and reports per load: the managed heap the
// workspace keeps alive (after a full collection, with and without it), the load time, the duration of a blocking full
// collection with the workspace alive (what WinUI's ReferenceTrackerHost asks for), how many draft rows are the snapshot
// row object and how many field-name string instances the rows hold.
static class MemoryLoadBenchmark
{
    static long Retained()
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        return GC.GetTotalMemory(false);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static (Workspace W, double LoadMs) Load(string directory, Guid id)
    {
        var start = Stopwatch.GetTimestamp();
        var w = new WorkspaceStore(directory).Load(id);
        return (w, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
    }

    public static void Run(string path)
    {
        const int loads = 5;
        var cases = new List<object>();
        foreach (var count in new[] { 20000, 50000 })
        {
            var directory = Path.Combine(Path.GetTempPath(), "vamsys-memory-load-" + Guid.NewGuid().ToString("N"));
            try
            {
                DataUiFixture.Seed(directory, count);
                var id = new WorkspaceStore(directory).List().Single().Id;
                Load(directory, id); // warm-up: JSON metadata, SQLite, statics
                var samples = new List<object>();
                for (var n = 0; n < loads; n++)
                {
                    var before = Retained();
                    var (w, loadMs) = Load(directory, id);
                    var retained = Retained() - before;
                    var gc = Stopwatch.GetTimestamp();
                    GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
                    var fullGcMs = Stopwatch.GetElapsedTime(gc).TotalMilliseconds;
                    var rows = w.Resources.Values.Sum(d => d.Snapshot.Count + d.Draft.Count);
                    var shared = w.Resources.Values.Sum(d => { var snapshot = d.Snapshot.ToHashSet<DataRow>(ReferenceEqualityComparer.Instance); return d.Draft.Count(snapshot.Contains); });
                    var names = new HashSet<string>(ReferenceEqualityComparer.Instance);
                    foreach (var data in w.Resources.Values)
                        foreach (var row in data.Snapshot.Concat(data.Draft))
                            foreach (var key in row.Fields.Keys)
                                names.Add(key);
                    GC.KeepAlive(w);
                    samples.Add(new { RetainedMiB = retained / 1048576.0, LoadMs = loadMs, FullGcMs = fullGcMs, Rows = rows, SharedDraftRows = shared, FieldNameInstances = names.Count });
                    Console.WriteLine($"{count,6:N0} routes, load {n + 1}: retained {retained / 1048576.0,6:F1} MiB, load {loadMs,6:F0} ms, full GC {fullGcMs,5:F1} ms, shared {shared:N0}, name instances {names.Count:N0}");
                }
                cases.Add(new { Routes = count, Samples = samples });
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                try { Directory.Delete(directory, true); } catch (IOException) { }
            }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(new { Runtime = Environment.Version.ToString(), Server = System.Runtime.GCSettings.IsServerGC, Cases = cases }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
