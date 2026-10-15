using System.Diagnostics;
using System.Text.Json;
using VamSys.Core;

// This identical harness runs against both the saved pre-change source and the new source.
static class CorePerformanceBenchmark
{
    public static ResourceData Fixture(int count)
    {
        var data = new ResourceData();
        for (var n = 0; n < count; n++)
        {
            var row = new DataRow();
            foreach (var field in Schemas.All[ResourceKind.Routes].Columns) row.Fields[field] = field == "_delete" ? "FALSE" : field + " " + n;
            row.Fields["ID"] = n.ToString(); row.Fields["Type"] = "jumpseat";
            data.Draft.Add(row); data.Snapshot.Add(row.Copy());
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
                for (var n = 0; n < count * percent / 100; n++) data.Draft[n].Fields["Remarks"] = "edited";
                cases.Add(new { Name="plan", Rows=count, Percent=percent, K=0, Metrics=Measure(() => planner.Plan(ResourceKind.Routes, data)) });
            }
            var indexed = Fixture(count); var id = indexed.Draft[0].LocalId;
            cases.Add(new { Name="cold-index", Rows=count, Percent=0, K=1, Metrics=Measure(() => { indexed.InvalidateDraftIndex(); indexed.FindDraftRow(id); }) });
            cases.Add(new { Name="warm-index", Rows=count, Percent=0, K=100, Metrics=Measure(() => { for (var n=0;n<100;n++) indexed.FindDraftRow(id); }) });
            foreach (var k in new[] { 1, 10, 100 })
            {
                var data = Fixture(count); var ids = data.Draft.Take(k).Select(r => r.LocalId).ToArray();
                data.Edit(ids, () => { foreach (var key in ids) data.FindDraftRow(key)!.Fields["Remarks"] = "edited"; });
                data.FindDraftRow(ids[0]);
                cases.Add(new { Name="undo-redo", Rows=count, Percent=0, K=k, Metrics=Measure(() => { data.UndoEdit(); data.RedoEdit(); }) });
                var assignment = new FleetAssignmentService(); var toggle = false;
                cases.Add(new { Name="replace", Rows=count, Percent=0, K=k, Metrics=Measure(() => {
                    toggle = !toggle;
                    var rows = ids.Select(key => { var row=data.FindDraftRow(key)!.Copy(); row.Fields["Remarks"]=toggle?"A":"B"; return row; }).ToList();
                    assignment.Apply(data, new("Remarks", rows, []));
                }) });
            }
            var history = Fixture(count);
            for (var n=0;n<20;n++) { var row=history.Draft[n]; history.Edit([row.LocalId],()=>row.Fields["Remarks"]="edited"); }
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
            for(var n=0;n<20;n++){var row=history.Draft[n];history.Edit([row.LocalId],()=>row.Fields["Remarks"]="edited");}
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
