using System.Diagnostics;
using System.Text.Json;
using VamSys.App;
using VamSys.Core;

// This identical harness runs against both the saved pre-change source and the new source.
static class CorePerformanceBenchmark
{
    public static ResourceData Fixture(int count)
    {
        var data = new ResourceData();
        for (var n = 0; n < count; n++)
        {
            var row = new DataRow.Builder();
            foreach (var field in Schemas.All[ResourceKind.Routes].Columns) row[field] = field == "_delete" ? "FALSE" : field + " " + n;
            row["ID"] = n.ToString(); row["Type"] = "jumpseat";
            var built = row.ToRow(); data.Draft.Add(built); data.Snapshot.Add(built.Dup());
        }
        return data;
    }
    public static void Run(string path)
    {
        var cases = new List<object>(); var planner = new ChangePlanner();
        foreach (var count in new[] { 1000, 20000, 50000 })
        {
            foreach (var percent in new[] { 0, 1, 10, 100 })
            {
                var data = Fixture(count);
                for (var n = 0; n < count * percent / 100; n++) data.Draft[n] = data.Draft[n].With("Remarks", "edited");
                cases.Add(new { Name="plan", Rows=count, Percent=percent, K=0, Metrics=Measure(() => planner.Plan(ResourceKind.Routes, data)) });
            }
            var indexed = Fixture(count); var id = indexed.Draft[0].LocalId;
            cases.Add(new { Name="cold-index", Rows=count, Percent=0, K=1, Metrics=Measure(() => { indexed.InvalidateDraftIndex(); indexed.FindDraftRow(id); }) });
            cases.Add(new { Name="warm-index", Rows=count, Percent=0, K=100, Metrics=Measure(() => { for (var n=0;n<100;n++) indexed.FindDraftRow(id); }) });
            foreach (var k in new[] { 1, 10, 100 })
            {
                var data = Fixture(count); var ids = data.Draft.Take(k).Select(r => r.LocalId).ToArray();
                data.Edit(ids, () => { data.ReplaceDraftRows(ids.Select(key => data.FindDraftRow(key)!.With("Remarks", "edited")).ToArray()); });
                data.FindDraftRow(ids[0]);
                cases.Add(new { Name="undo-redo", Rows=count, Percent=0, K=k, Metrics=Measure(() => { data.UndoEdit(); data.RedoEdit(); }) });
                var assignment = new FleetAssignmentService(); var toggle = false;
                cases.Add(new { Name="replace", Rows=count, Percent=0, K=k, Metrics=Measure(() => {
                    toggle = !toggle;
                    var rows = ids.Select(key => { var row=data.FindDraftRow(key)!.With("Remarks",toggle?"A":"B"); return row; }).ToList();
                    assignment.Apply(data, new("Remarks", rows, []));
                }) });
            }
            var history = Fixture(count);
            for (var n=0;n<20;n++) { var row=history.Draft[n]; history.Edit([row.LocalId],()=>history.ReplaceDraftRows([row.With("Remarks","edited")])); }
            cases.Add(new { Name="validate-20", Rows=count, Percent=0, K=20, Metrics=Measure(()=>history.History.Validate(history)) });
            var structural = Fixture(count); var added = new DataRow();
            structural.Edit([added.LocalId],()=>structural.Draft.Add(added),structural:true);
            cases.Add(new { Name="structural-undo-redo", Rows=count, Percent=0, K=1, Metrics=Measure(()=>{structural.UndoEdit();structural.RedoEdit();}) });
            Console.WriteLine($"Measured {count:N0} rows");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(new { Runtime=Environment.Version.ToString(), Architecture=System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(), Warmups=5, Samples=20, Cases=cases }, new JsonSerializerOptions { WriteIndented=true }));
        Console.WriteLine("Core benchmark: " + path);
    }
    public static void RunLoad(string path)
    {
        var cases = new List<object>();
        foreach (var count in new[] {1000,20000,50000})
        {
            var history = Fixture(count);
            for(var n=0;n<20;n++){var row=history.Draft[n];history.Edit([row.LocalId],()=>history.ReplaceDraftRows([row.With("Remarks","edited")]));}
            var json = JsonSerializer.Serialize(history);
            cases.Add(new{Name="load-20",Rows=count,Percent=0,K=20,JsonBytes=System.Text.Encoding.UTF8.GetByteCount(json),Metrics=Measure(()=>{
                var restored=JsonSerializer.Deserialize<ResourceData>(json)!;
                if(restored.Draft.Count!=count||restored.Undo.Count!=20)throw new InvalidDataException("Loaded history changed");
            })});
            Console.WriteLine($"Measured full JSON load with 20 steps: {count:N0} rows");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path,JsonSerializer.Serialize(new{Runtime=Environment.Version.ToString(),Warmups=5,Samples=20,Cases=cases},new JsonSerializerOptions{WriteIndented=true}));
    }
    // A Demo-style workspace shaped like the UI review benchmark: complete route rows, 90% updated (Remarks),
    // 5% deleted and 5% new, so that the planner's three change kinds are all present. `changes` is the plan size.
    public static Workspace ReviewWorkspace(int changes, RunMode mode)
    {
        var workspace = new Workspace { Name = "review", Mode = mode };
        DataRow Row(params (string Field, string Value)[] fields) { var row = new DataRow.Builder(); foreach (var (f, v) in fields) row[f] = v; return row.ToRow(); }
        CsvAdapter.Import(ResourceKind.Airports, workspace.Resources[ResourceKind.Airports], [Row(("ICAO/IATA", "ZBAA"), ("Name", "Beijing")), Row(("ICAO/IATA", "ZSPD"), ("Name", "Shanghai"))], true);
        CsvAdapter.Import(ResourceKind.Fleets, workspace.Resources[ResourceKind.Fleets], [Row(("ID", "1"), ("Name", "Boeing 737-800"), ("Type Code", "B738"), ("Type (pax/cargo/...)", "pax"), ("Max Passengers", "189"))], true);
        DataRow Route(int n)
        {
            var row = new DataRow.Builder();
            foreach (var field in Schemas.All[ResourceKind.Routes].Columns) row[field] = "";
            foreach (var (field, value) in new (string, string)[] { ("ID", n.ToString()), ("Departure Airport (ICAO/IATA)", "ZBAA"), ("Arrival Airport (ICAO/IATA)", "ZSPD"), ("Type", "scheduled"), ("Callsign", "QA" + n.ToString("D5")), ("Flight Number", n.ToString("D6")), ("Fleet IDs", "1"), ("Start Date", "2026-10-01 00:00:00"), ("End Date", "2027-10-01 00:00:00"), ("Departure Time (HH:MM)", "10:00"), ("Arrival Time (HH:MM)", "12:00"), ("Remarks", "row " + n), ("Tags", "performance"), ("Is Hidden", "FALSE"), ("Altitude", "33000"), ("Cost Index", "AUTO"), ("_delete", "FALSE") })
                row[field] = value;
            return row.ToRow();
        }
        var created = changes / 20; var deleted = changes / 20; var existing = changes - created;
        var rows = Enumerable.Range(0, existing).Select(Route).ToList();
        var data = workspace.Resources[ResourceKind.Routes];
        CsvAdapter.Import(ResourceKind.Routes, data, rows, true);
        var drafts = new List<DataRow>(data.Draft.Count + created);
        for (var n = 0; n < data.Draft.Count; n++)
        {
            var row = data.Draft[n];
            drafts.Add(n < existing - deleted ? row.With("Remarks", "review " + n) : row.With("_delete", "TRUE"));
        }
        for (var n = 0; n < created; n++) { var row = Route(existing + n).With("ID", ""); drafts.Add(row); }
        data.Draft = drafts;
        return workspace;
    }
    // Everything MainWindow.Review.cs does in the background: blockers, validation, plan and content key.
    public static void RunReview(string path)
    {
        var planner = new ChangePlanner(); var cases = new List<object>();
        foreach (var count in new[] { 1000, 10000, 50000 })
            foreach (var mode in new[] { RunMode.Demo, RunMode.Online })
            {
                var w = ReviewWorkspace(count, mode); var verified = mode == RunMode.Online ? new HashSet<string>(StringComparer.Ordinal) { "Routes:Update", "Routes:Create", "Routes:Delete" } : [];
                Action<ChangeItem>? preview = mode == RunMode.Online ? _ => { } : null;
                WorkspaceReviewPlan Build() => ReviewPlanBuilder.BuildForWorkspace(planner, w, mode, mode == RunMode.Online, verified, preview);
                var plan = Build();
                var summary = $"{plan.Entries.Count} entries, {plan.Entries.Count(e => e.Disposition == ReviewDisposition.Ready)} ready, {plan.Blockers.Count} blockers";
                var key = plan.ContentKey();
                if (plan.Blockers.Count > 0) Console.WriteLine("  first blocker: " + plan.Blockers[0].Code + " " + plan.Blockers[0].ToString());
                var samples = count >= 50000 ? 5 : 11; var warmups = count >= 50000 ? 1 : 3;
                for (var n = 0; n < warmups; n++) Build();
                var times = new List<double>(); var bytes = new List<long>();
                for (var n = 0; n < samples; n++)
                {
                    GC.Collect(); var allocated = GC.GetAllocatedBytesForCurrentThread(); var started = Stopwatch.GetTimestamp();
                    var built = Build();
                    times.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds); bytes.Add(GC.GetAllocatedBytesForCurrentThread() - allocated);
                    if (built.ContentKey() != key) throw new InvalidOperationException("Review content key is not stable");
                }
                times.Sort(); bytes.Sort();
                cases.Add(new { Mode = mode.ToString(), Changes = count, Summary = summary, MedianMs = Math.Round(times[times.Count / 2], 1), MedianAllocatedMiB = Math.Round(bytes[bytes.Count / 2] / 1048576.0, 2), MedianAllocatedBytes = bytes[bytes.Count / 2], Key = key });
                Console.WriteLine($"{mode,-6} {count,6:N0} changes: {times[times.Count / 2],8:F1} ms  {bytes[bytes.Count / 2] / 1048576.0,8:F2} MiB   ({summary})");
            }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(new { Runtime = Environment.Version.ToString(), Cases = cases }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("Review benchmark: " + path);
    }
    static object Measure(Action action)
    {
        for (var n=0;n<5;n++) action();
        var samples = new List<object>(); var times = new List<double>(); var allocations = new List<long>();
        var gcBefore = Enumerable.Range(0,3).Select(GC.CollectionCount).ToArray();
        for (var n=0;n<20;n++)
        {
            var allocated=GC.GetAllocatedBytesForCurrentThread(); var started=Stopwatch.GetTimestamp();
            action(); var elapsed=Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var bytes=GC.GetAllocatedBytesForCurrentThread()-allocated;
            samples.Add(new { Milliseconds=elapsed, AllocatedBytes=bytes }); times.Add(elapsed); allocations.Add(bytes);
        }
        times.Sort(); allocations.Sort();
        return new { MedianMs=times[10], P95Ms=times[18], MedianAllocatedBytes=allocations[10], GcCollections=Enumerable.Range(0,3).Select(g=>GC.CollectionCount(g)-gcBefore[g]).ToArray(), Samples=samples };
    }
}
