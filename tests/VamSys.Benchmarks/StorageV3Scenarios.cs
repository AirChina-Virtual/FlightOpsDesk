using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using VamSys.Core;
using VamSys.Infrastructure;
using static VamSys.TestSupport.StorageScenarios;

static class StorageV3Scenarios
{
    static void Assert(bool value,string text="v3 assertion failed")=>StorageScenarios.Check(value,text);
    public static ResourceRebaseDelta Delta(ChangeItem item)=>new(item.Resource,item.After.LocalId,new(RowMutationKind.Upsert,item.After.Dup()),new(RowMutationKind.Upsert,item.After.Dup()),DateTimeOffset.UnixEpoch,true,"Fleets:Create");
    public static async Task Benchmark(string? directory)
    {
        var template=Fixture().Serialize();long? expected=null;var results=new List<object>();
        foreach(var(rows,undo,same)in new[]{(1,0,false),(20000,0,false),(20000,3,false),(20000,3,true)})
        {
            var w=Workspace.Deserialize(template);var kind=same?ResourceKind.Fleets:ResourceKind.Routes;var data=w.Resources[kind];
            for(var n=0;n<rows;n++){var row=new DataRow{Fields = new Dictionary<string, string> {["Name"]="Row "+n,["ID"]=n.ToString()}};data.Draft.Add(row);data.Snapshot.Add(row.Dup());}
            for(var n=0;n<undo;n++){var row=data.Draft[n%data.Draft.Count];data.Edit([row.LocalId],()=>data.ReplaceDraftRows([row.With("Name","history "+n)]));}
            var dir=Temp();var store=new WorkspaceStore(dir);store.Save(w);var item=w.Jobs[0].Items[0];item.State=ItemState.Succeeded;
            using var c=Open(dir);
            Sql(c,"CREATE TABLE touched(resource INTEGER,local_id TEXT); CREATE TRIGGER row_i AFTER INSERT ON resource_rows BEGIN INSERT INTO touched VALUES(NEW.resource,NEW.local_id); END; CREATE TRIGGER row_u AFTER UPDATE ON resource_rows BEGIN INSERT INTO touched VALUES(NEW.resource,NEW.local_id); END; CREATE TRIGGER row_d AFTER DELETE ON resource_rows BEGIN INSERT INTO touched VALUES(OLD.resource,OLD.local_id); END;");
            var before=GC.GetTotalAllocatedBytes(true);var watch=Stopwatch.StartNew();using var metrics=PerformanceRun.Begin(w.Id,w.Jobs[0].Id);
            await store.CheckpointWriter(w).WriteAsync(new(CheckpointKind.Rebase,w.Jobs[0],item,Delta:Delta(item)),default);
            var ms=watch.Elapsed.TotalMilliseconds;var allocated=GC.GetTotalAllocatedBytes(true)-before;
            expected??=metrics.JsonBytes;Assert(metrics.JsonBytes==expected,"Delta serialized unrelated resources");
            Assert(Convert.ToInt32(Sql(c,"SELECT count(*) FROM touched WHERE resource!=$r OR local_id!=$id",("$r",(int)item.Resource),("$id",item.After.LocalId.ToString())))==0);
            var touched=Convert.ToInt32(Sql(c,"SELECT count(*) FROM touched"));Assert(touched==2&&metrics.SaveSuccesses==1);
            var disk=store.Load(w.Id);Assert(disk.Resources[ResourceKind.Fleets].Undo.Count==0);
            if(!same)Assert(disk.Resources[kind].Undo.Count==undo);
            results.Add(new{rows,undo,sameResource=same,bytes=metrics.JsonBytes,ms,allocatedBytes=allocated,transactions=metrics.SaveSuccesses,touchedRows=touched});
            Console.WriteLine($"  v3 delta rows={rows}, undo={undo}, same={same}: {metrics.JsonBytes} B, {ms:F2} ms, {allocated} allocated, {touched} rows");
        }
        if(directory!=null){Directory.CreateDirectory(directory);File.WriteAllText(Path.Combine(directory,"v3-rebase.json"),JsonSerializer.Serialize(results,new JsonSerializerOptions{WriteIndented=true}));}
    }
}
