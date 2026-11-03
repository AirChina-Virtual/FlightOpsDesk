using System.Diagnostics;
using System.Text;
using System.Text.Json;
using VamSys.Core;
using VamSys.Infrastructure;
using static VamSys.TestSupport.StorageScenarios;

// Task and rebase checkpoint cost for 1 and 20,000 rows, with and without undo history (--benchmark-storage).
static class StorageBenchmark
{
    public static async Task Benchmark(string? output)
    {
        var template=Fixture().Serialize();var results=new List<object>();long? expected=null;
        foreach(var(rows,undo)in new[]{(1,0),(20000,0),(20000,3)})
        {
            var w=Workspace.Deserialize(template);var data=w.Resources[ResourceKind.Routes];
            data.Draft=Enumerable.Range(0,rows).Select(i=>new DataRow{Fields = new Dictionary<string, string> {["ID"]=i.ToString(),["Callsign"]="ACA"+i,["Remarks"]="性能"}}).ToList();
            data.Snapshot=data.Draft.Select(r=>r.Dup()).ToList();
            for(var n=0;n<undo;n++){var row=data.Draft[n%data.Draft.Count];data.Edit([row.LocalId],()=>data.ReplaceDraftRows([row.With("Remarks","history "+n)]));}
            var dir=Temp();var store=new WorkspaceStore(dir);store.Save(w);
            using var c=Open(dir);var json=(string)Sql(c,"SELECT json FROM workspaces")!;
            Sql(c,"CREATE TRIGGER forbid_resources BEFORE UPDATE ON workspaces BEGIN SELECT RAISE(ABORT,'resource write'); END");
            var item=w.Jobs[0].Items[0];item.State=ItemState.Running;
            var before=GC.GetTotalAllocatedBytes(true);var timer=Stopwatch.StartNew();
            long bytes;double elapsed;long allocated;
            using(var run=PerformanceRun.Begin(w.Id,w.Jobs[0].Id))
            {
                for(var n=0;n<20;n++)await store.CheckpointWriter(w).WriteAsync(new(CheckpointKind.Task,w.Jobs[0],item),default);
                elapsed=timer.Elapsed.TotalMilliseconds;allocated=GC.GetTotalAllocatedBytes(true)-before;bytes=run.JsonBytes;
                Check(run.SaveSuccesses==20&&run.Checkpoints["Task"]==20);
                expected??=bytes;Check(expected==bytes,"Resource size changed task serialization payload");
                var actual=Encoding.UTF8.GetByteCount((string)Sql(c,"SELECT json FROM batch_jobs")!)+Encoding.UTF8.GetByteCount((string)Sql(c,"SELECT json FROM batch_items")!);
                Check(bytes==20L*actual,"Measured bytes must equal stored header and item payloads");
            }
            Check((string)Sql(c,"SELECT json FROM workspaces")! ==json);Sql(c,"DROP TRIGGER forbid_resources");
            var staged=Workspace.Deserialize(w.Serialize());var changed=staged.Jobs[0].Items[0];changed.State=ItemState.Succeeded;staged.Resources[ResourceKind.Fleets].ClearHistory();
            before=GC.GetTotalAllocatedBytes(true);timer.Restart();
            using var rebase=PerformanceRun.Begin(w.Id,w.Jobs[0].Id);
            await store.CheckpointWriter(w).WriteAsync(new(CheckpointKind.Rebase,staged.Jobs[0],changed,staged),default);
            var rebaseMs=timer.Elapsed.TotalMilliseconds;var rebaseAllocation=GC.GetTotalAllocatedBytes(true)-before;
            results.Add(new{rows,undo,updates=20,bytes,elapsedMs=elapsed,allocatedBytes=allocated,resourceUpdates=0,rebaseBytes=rebase.JsonBytes,rebaseMs,rebaseAllocatedBytes=rebaseAllocation,rebaseTransactions=rebase.SaveSuccesses});
            Console.WriteLine($"  v3 rows={rows} undo={undo}: task bytes={bytes}, {elapsed:F1} ms; rebase bytes={rebase.JsonBytes}, {rebaseMs:F1} ms");
        }
        if(output!=null){Directory.CreateDirectory(output);await File.WriteAllTextAsync(Path.Combine(output,"storage-benchmark.json"),JsonSerializer.Serialize(results,new JsonSerializerOptions{WriteIndented=true}));}
    }
}
