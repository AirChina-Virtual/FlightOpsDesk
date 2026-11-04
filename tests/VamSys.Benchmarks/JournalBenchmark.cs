using Microsoft.Data.Sqlite;
using System.Diagnostics;
using System.Text.Json;
using VamSys.Core;
using VamSys.Infrastructure;

// Headless save-cost benchmark for the change journal: a 50k-row workspace and the saves the application makes after
// batch edits, adding a row, conflict resolution, connecting, submitting a task and switching workspace.
// mode "full" saves the way the application did before the journal (every call site rewrites the whole workspace);
// mode "journal" uses the journal-based entry points.
static class JournalBenchmark
{
    const int Rows = 50000;
    const string Touched = "CREATE TABLE touched(tbl TEXT);"
        + "CREATE TRIGGER row_i AFTER INSERT ON resource_rows BEGIN INSERT INTO touched VALUES('row'); END;"
        + "CREATE TRIGGER row_u AFTER UPDATE ON resource_rows BEGIN INSERT INTO touched VALUES('row'); END;"
        + "CREATE TRIGGER row_d AFTER DELETE ON resource_rows BEGIN INSERT INTO touched VALUES('row'); END;"
        + "CREATE TRIGGER job_i AFTER INSERT ON batch_jobs BEGIN INSERT INTO touched VALUES('job'); END;"
        + "CREATE TRIGGER job_u AFTER UPDATE ON batch_jobs BEGIN INSERT INTO touched VALUES('job'); END;"
        + "CREATE TRIGGER job_d AFTER DELETE ON batch_jobs BEGIN INSERT INTO touched VALUES('job'); END;"
        + "CREATE TRIGGER item_i AFTER INSERT ON batch_items BEGIN INSERT INTO touched VALUES('item'); END;"
        + "CREATE TRIGGER item_u AFTER UPDATE ON batch_items BEGIN INSERT INTO touched VALUES('item'); END;"
        + "CREATE TRIGGER item_d AFTER DELETE ON batch_items BEGIN INSERT INTO touched VALUES('item'); END;"
        + "CREATE TRIGGER step_i AFTER INSERT ON resource_history_steps BEGIN INSERT INTO touched VALUES('step'); END;"
        + "CREATE TRIGGER step_u AFTER UPDATE ON resource_history_steps BEGIN INSERT INTO touched VALUES('step'); END;"
        + "CREATE TRIGGER step_d AFTER DELETE ON resource_history_steps BEGIN INSERT INTO touched VALUES('step'); END;"
        + "CREATE TRIGGER meta_i AFTER INSERT ON resource_metadata BEGIN INSERT INTO touched VALUES('meta'); END;"
        + "CREATE TRIGGER meta_u AFTER UPDATE ON resource_metadata BEGIN INSERT INTO touched VALUES('meta'); END;"
        + "CREATE TRIGGER root_u AFTER UPDATE ON workspaces BEGIN INSERT INTO touched VALUES('header'); END;";

    sealed record Result(string Case, int Saves, double Milliseconds, long JsonBytes, int RowWrites, int JobWrites, int MetadataWrites, int StepWrites, int HeaderWrites);

    static Workspace Build()
    {
        var w = new Workspace { Name = "Benchmark", AirlineId = "7", Mode = RunMode.Online };
        var data = w.Resources[ResourceKind.Routes];
        for (var n = 0; n < Rows; n++)
        {
            var row = new DataRow { Identity = new((n + 1).ToString(), "7"), Fields = new Dictionary<string, string> { ["ID"] = (n + 1).ToString(), ["Flight Number"] = "AC" + n, ["Callsign"] = "ACA" + n, ["Remarks"] = "benchmark", ["Tags"] = "" } };
            data.Snapshot.Add(row.Dup());
            data.Draft.Add(row);
        }
        data.Columns = [.. data.Draft[0].Fields.Keys];
        data.SnapshotAt = DateTimeOffset.UtcNow;
        // 100 open conflicts on the first 100 rows.
        for (var n = 0; n < 100; n++)
            data.Conflicts.Add(new(data.Draft[n].LocalId, "Callsign", "ACA" + n, "LOCAL" + n, "REMOTE" + n));
        w.Jobs.Add(new BatchJob { Mode = RunMode.Online, Items = [new ChangeItem { Message = "existing", State = ItemState.Succeeded }] });
        return w;
    }

    static async Task<Result> Measure(string name, string directory, Workspace w, int saves, Func<Task> action, bool counting)
    {
        long bytes; double ms; int saved;
        using var run = PerformanceRun.Begin(w.Id, Guid.NewGuid());
        var watch = Stopwatch.StartNew();
        await action();
        ms = watch.Elapsed.TotalMilliseconds;
        bytes = run.JsonBytes;
        saved = (int)run.SaveSuccesses;
        int Count(string tbl) { using var c = StorageScenarios.Open(directory); return Convert.ToInt32(StorageScenarios.Sql(c, "SELECT count(*) FROM touched WHERE tbl=$t", ("$t", tbl))); }
        var result = counting ? new Result(name, saved, ms, bytes, Count("row"), Count("job") + Count("item"), Count("meta"), Count("step"), Count("header")) : new Result(name, saved, ms, bytes, -1, -1, -1, -1, -1);
        if (counting)
        { using var c = StorageScenarios.Open(directory); StorageScenarios.Sql(c, "DELETE FROM touched"); }
        Console.WriteLine($"    {(counting ? "count" : "time ")} {name}: saves={result.Saves} {result.Milliseconds:F1} ms {result.JsonBytes:N0} B rows={result.RowWrites} jobs/items={result.JobWrites} meta={result.MetadataWrites} steps={result.StepWrites} header={result.HeaderWrites}");
        return result;
    }

    // Cost of the job safety net: 1,000 history jobs x 20 items plus one active job of 20,000 items (the shape of DataUiFixture.SeedTasks).
    public static async Task JobsCost()
    {
        var dir = StorageScenarios.Temp();
        var store = new WorkspaceStore(dir);
        var w = new Workspace { Name = "Jobs", AirlineId = "7", Mode = RunMode.Online };
        ChangeItem Item(int n) => new() { Kind = ChangeKind.Update, State = ItemState.Succeeded, RemoteId = n.ToString(), Message = "done " + n, After = new DataRow { Fields = new Dictionary<string, string> { ["ID"] = n.ToString(), ["Name"] = "n" + n } } };
        for (var j = 0; j < 1000; j++)
            w.Jobs.Add(new BatchJob { Mode = RunMode.Online, Items = [.. Enumerable.Range(0, 20).Select(Item)] });
        w.Jobs.Add(new BatchJob { Mode = RunMode.Online, Items = [.. Enumerable.Range(0, 20000).Select(Item)] });
        store.Save(w);
        async Task<double> Median(Func<Task> action)
        {
            var times = new List<double>();
            for (var n = 0; n < 9; n++)
            { var watch = Stopwatch.StartNew(); await action(); times.Add(watch.Elapsed.TotalMilliseconds); }
            times.Sort();
            return times[times.Count / 2];
        }
        var empty = await Median(() => store.SaveChangesAsync(w));
        var n2 = 0;
        var header = await Median(() => { w.ConnectedAt = DateTimeOffset.UnixEpoch.AddSeconds(++n2); return store.SaveHeaderAsync(w); });
        var serialize = await Median(() => { foreach (var job in w.Jobs) _ = JsonSerializer.Serialize(job); return Task.CompletedTask; });
        Console.WriteLine($"jobs 1000x20 + 1x20000: empty-journal save {empty:F1} ms, header-only save {header:F1} ms; (serializing every job once, for comparison: {serialize:F0} ms)");
    }

    // Each case runs twice on fresh copies: once for time and bytes, once with write-counting triggers installed.
    public static async Task Run(string mode, string output)
    {
        var results = new List<Result>();
        foreach (var name in new[] { "batch edit 200 rows", "add one row", "resolve 100 conflicts", "header-only save (connect)", "job submit save", "switch-away save, nothing changed", "connection settings save" })
            foreach (var counting in new[] { false, true })
            {
                var dir = StorageScenarios.Temp();
                var store = new WorkspaceStore(dir);
                var w = Build();
                store.Save(w);
                if (counting)
                    using (var c = StorageScenarios.Open(dir))
                        StorageScenarios.Sql(c, Touched);
                var saver = Saver.For(mode, store, w);
                var data = w.Resources[ResourceKind.Routes];
                // Warm up the code paths once (JIT, statement cache) with a tiny save so the case measures the save itself.
                var warm=data.Draft[Rows - 1].With("Remarks","warm");
                data.Edit([warm.LocalId], () => data.ReplaceDraftRows([warm]));
                await saver.Rows();
                if (counting)
                    using (var c = StorageScenarios.Open(dir))
                        StorageScenarios.Sql(c, "DELETE FROM touched");
                Result result;
                switch (name)
                {
                    case "batch edit 200 rows":
                    {
                        var copies = data.Draft.Skip(1000).Take(200).Select(r => { var c=r.With("Remarks","batch edited"); return c; }).ToArray();
                        data.Edit(copies.Select(r => r.LocalId), () => data.ReplaceDraftRows(copies));
                        result = await Measure(name, dir, w, 1, saver.Rows, counting);
                        break;
                    }
                    case "add one row":
                    {
                        var row = new DataRow { Fields = new Dictionary<string, string> { ["ID"] = "", ["Flight Number"] = "NEW", ["Callsign"] = "", ["Remarks"] = "", ["Tags"] = "" } };
                        data.Edit([row.LocalId], () => data.Draft.Add(row), structural: true);
                        data.Columns = data.Columns.Concat(row.Fields.Keys).Distinct().ToList();
                        result = await Measure(name, dir, w, 1, saver.Rows, counting);
                        break;
                    }
                    case "resolve 100 conflicts":
                    {
                        result = await Measure(name, dir, w, mode == "full" ? 100 : 1, async () =>
                        {
                            foreach (var conflict in data.Conflicts.ToArray())
                            {
                                var row = data.FindDraftRow(conflict.RowId)!;
                                var replacement=row.With(conflict.Field,conflict.Remote ?? "");
                                data.Edit([row.LocalId], () => data.ReplaceDraftRows([replacement]), newStep: false);
                                data.Conflicts.Remove(conflict);
                                // Before the journal the dialog saved after every conflict; now it saves when it ends.
                                if (mode == "full")
                                    await saver.Rows();
                            }
                            if (mode != "full")
                                await saver.Rows();
                        }, counting);
                        break;
                    }
                    case "header-only save (connect)":
                    {
                        w.ConnectedAt = DateTimeOffset.UtcNow;
                        w.Mode = RunMode.Online;
                        result = await Measure(name, dir, w, 1, saver.Header, counting);
                        break;
                    }
                    case "job submit save":
                    {
                        w.Jobs.Add(new BatchJob { Mode = RunMode.Online, Items = [.. Enumerable.Range(0, 200).Select(n => new ChangeItem { After = data.Draft[n].Dup(), Message = "item " + n })] });
                        result = await Measure(name, dir, w, 1, saver.Jobs, counting);
                        break;
                    }
                    case "switch-away save, nothing changed":
                        result = await Measure(name, dir, w, 1, saver.Rows, counting);
                        break;
                    default:
                    {
                        result = await Measure(name, dir, w, 1, () => { store.SaveConnectionSettings(w, "client", "secret", "", true); return Task.CompletedTask; }, counting);
                        break;
                    }
                }
                if (counting)
                    results[^1] = results[^1] with { RowWrites = result.RowWrites, JobWrites = result.JobWrites, MetadataWrites = result.MetadataWrites, StepWrites = result.StepWrites, HeaderWrites = result.HeaderWrites };
                else
                    results.Add(result);
            }
        Console.WriteLine($"mode={mode}");
        foreach (var r in results)
            Console.WriteLine($"  {r.Case,-36} saves={r.Saves,3} {r.Milliseconds,9:F1} ms {r.JsonBytes,12:N0} B  rows={r.RowWrites,6} jobs/items={r.JobWrites,4} meta={r.MetadataWrites,2} steps={r.StepWrites,2} header={r.HeaderWrites}");
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, $"journal-benchmark-{mode}.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
    }

    // The save calls of one benchmark workspace.
    sealed class Saver(Func<Task> rows, Func<Task> header, Func<Task> jobs)
    {
        public Task Rows() => rows();
        public Task Header() => header();
        public Task Jobs() => jobs();
        public static Saver For(string mode, WorkspaceStore store, Workspace w)
            => mode switch
            {
                "full" => new(() => store.SaveAsync(w), () => store.SaveAsync(w), () => store.SaveAsync(w)),
                "journal" => new(() => store.SaveChangesAsync(w), () => store.SaveHeaderAsync(w), () => store.SaveJobsAsync(w)),
                _ => throw new ArgumentException("unknown mode " + mode),
            };
    }
}
