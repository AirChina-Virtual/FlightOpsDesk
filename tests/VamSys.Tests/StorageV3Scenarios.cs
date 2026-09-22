using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using VamSys.Core;
using VamSys.Infrastructure;
using static StorageScenarios;

static class StorageV3Scenarios
{
    static void Assert(bool value,string text="v3 assertion failed")=>StorageScenarios.Check(value,text);
    static void V2(string dir,Workspace w)
    {
        using var c=Legacy(dir,w);
        Sql(c,"ALTER TABLE workspaces ADD COLUMN format_version INTEGER DEFAULT 2; CREATE TABLE workspace_revisions(workspace_id TEXT PRIMARY KEY,revision INTEGER); CREATE TABLE batch_jobs(workspace_id TEXT,id TEXT,ordinal INTEGER,json TEXT,PRIMARY KEY(workspace_id,id)); CREATE TABLE batch_items(workspace_id TEXT,job_id TEXT,id TEXT,ordinal INTEGER,json TEXT,PRIMARY KEY(workspace_id,job_id,id)); PRAGMA user_version=2;");
        var root=JsonNode.Parse(w.Serialize())!;root.AsObject().Remove("Jobs");
        Sql(c,"UPDATE workspaces SET json=$j",("$j",root.ToJsonString()));
        Sql(c,"INSERT INTO workspace_revisions VALUES($w,7)",("$w",w.Id.ToString()));
        foreach(var(j,n)in w.Jobs.Select((j,n)=>(j,n)))
        {
            var header=JsonNode.Parse(JsonSerializer.Serialize(j))!;header.AsObject().Remove("Items");
            Sql(c,"INSERT INTO batch_jobs VALUES($w,$id,$n,$j)",("$w",w.Id.ToString()),("$id",j.Id.ToString()),("$n",n),("$j",header.ToJsonString()));
            foreach(var(i,k)in j.Items.Select((i,k)=>(i,k)))Sql(c,"INSERT INTO batch_items VALUES($w,$job,$id,$n,$j)",("$w",w.Id.ToString()),("$job",j.Id.ToString()),("$id",i.Id.ToString()),("$n",k),("$j",JsonSerializer.Serialize(i)));
        }
    }
    public static ResourceRebaseDelta Delta(ChangeItem item)=>new(item.Resource,item.After.LocalId,new(RowMutationKind.Upsert,item.After.Copy()),new(RowMutationKind.Upsert,item.After.Copy()),DateTimeOffset.UnixEpoch,true,"Fleets:Create");
    public static async Task Run(Func<string,Func<Task>,Task> test)
    {
        await test("Closing freezes before await, rejects repeated close and restores interaction after failure",async()=>{
            var gate=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var close=new WindowCloseController();var calls=0;var frozen=false;
            var saving=close.RequestAsync(false,async()=>{calls++;await gate.Task;},value=>frozen=value);
            Assert(frozen&&!close.IsOpen&&!saving.IsCompleted);
            Assert(!await close.RequestAsync(false,()=>{calls++;return Task.CompletedTask;},_=>{}));
            gate.SetException(new IOException("save failed"));try{await saving;throw new Exception("Expected failure");}catch(IOException){}
            Assert(close.IsOpen&&!frozen&&calls==1);
            Assert(!await close.RequestAsync(true,()=>{calls++;return Task.CompletedTask;},_=>{}));
            Assert(await close.RequestAsync(false,()=>Task.CompletedTask,value=>frozen=value)&&close.State==WindowCloseState.Closed);
        });
        await test("Delayed actual SQLite closing snapshot forbids later mutation and persists last accepted edit",async()=>{
            var dir=Temp();var store=new WorkspaceStore(dir);var w=Fixture();store.Save(w);var row=w.Resources[ResourceKind.Fleets].Draft[0];
            using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();
            store.Barrier=p=>{if(p=="Full:BeforeCommit"){entered.Set();if(!release.Wait(10000))throw new TimeoutException();}};
            var close=new WindowCloseController();row.Fields["Name"]="accepted";
            var closing=close.RequestAsync(false,()=>store.SaveAsync(w),_=>{});
            Assert(entered.Wait(10000));if(close.IsOpen)row.Fields["Name"]="must not enter";
            release.Set();Assert(await closing);Assert(store.Load(w.Id).Resources[ResourceKind.Fleets].Draft[0].Get("Name")=="accepted");
        });
        foreach(var corrupt in new[]{false,true})
        await test("v2 to v3 keeps task revision and validates duplicate local IDs: "+corrupt,()=>{
            var dir=Temp();var w=Fixture();w.Jobs[0].Items[0].State=ItemState.Unknown;w.Jobs[0].Items[0].ProposeRecoveryId("10");
            if(corrupt)w.Resources[ResourceKind.Fleets].Draft.Add(w.Resources[ResourceKind.Fleets].Draft[0].Copy());
            V2(dir,w);
            if(corrupt)
            {
                try{new WorkspaceStore(dir);throw new Exception("duplicate accepted");}catch(Microsoft.Data.Sqlite.SqliteException){}
                using var c=Open(dir);Assert(Convert.ToInt32(Sql(c,"PRAGMA user_version"))==2);Assert(Convert.ToInt32(Sql(c,"SELECT count(*) FROM sqlite_master WHERE name='resource_rows'"))==0);
            }
            else
            {
                var store=new WorkspaceStore(dir);var loaded=store.Load(w.Id);
                Assert(loaded.StorageRevision==7&&loaded.Jobs[0].Items[0].RecoveryCandidateId=="10"&&loaded.Resources[ResourceKind.Fleets].Undo.Count==1);
                using var c=Open(dir);
                try {Sql(c,"UPDATE workspaces SET format_version=2,json='{}'");throw new Exception("old write accepted");}catch(Microsoft.Data.Sqlite.SqliteException){}
                Assert(store.Load(w.Id).StorageRevision==7);
                using var backup=new Microsoft.Data.Sqlite.SqliteConnection("Data Source="+store.MigrationBackup);backup.Open();Assert(Convert.ToInt32(Sql(backup,"PRAGMA user_version"))==2);
            }
            return Task.CompletedTask;
        });
        foreach(var phase in new[]{"Rebase:BeforeCommit","Rebase:AfterCommit"})
        await test("v3 row delta atomic transaction "+phase,async()=>{
            var dir=Temp();var store=new WorkspaceStore(dir);var w=Fixture();store.Save(w);
            var item=JsonSerializer.Deserialize<ChangeItem>(JsonSerializer.Serialize(w.Jobs[0].Items[0]))!;item.State=ItemState.Succeeded;item.Rebased=true;
            store.Barrier=p=>{if(p==phase)throw new IOException("fault");};
            try {await store.CheckpointWriter(w).WriteAsync(new(CheckpointKind.Rebase,w.Jobs[0],item,Delta:Delta(item)),default);throw new Exception("Expected fault");}catch(IOException){}
            var disk=store.Load(w.Id);var yes=phase.EndsWith("AfterCommit");
            Assert((disk.Jobs[0].Items[0].State==ItemState.Succeeded)==yes);
            Assert(disk.Resources[ResourceKind.Fleets].Snapshot.Count==(yes?1:0)&&disk.Resources[ResourceKind.Fleets].Undo.Count==(yes?0:1));
            Assert(disk.VerifiedOperations.Contains("Fleets:Create")==yes&&w.Resources[ResourceKind.Fleets].Undo.Count==1);
        });
        await test("v3 row order, targeted deletion and stale full snapshot rejection",async()=>{
            var dir=Temp();var store=new WorkspaceStore(dir);var w=Fixture();var data=w.Resources[ResourceKind.Fleets];
            var first=data.Draft[0];var other=new DataRow{Fields=new(){["Name"]="other"}};
            data.Snapshot=[first.Copy(),other.Copy()];data.Draft.Add(other);store.Save(w);var stale=store.Load(w.Id);
            var item=w.Jobs[0].Items[0];item.State=ItemState.Succeeded;
            await store.CheckpointWriter(w).WriteAsync(new(CheckpointKind.Rebase,w.Jobs[0],item,Delta:Delta(item)),default);
            var disk=store.Load(w.Id);Assert(disk.Resources[ResourceKind.Fleets].Snapshot.Select(r=>r.LocalId).SequenceEqual([other.LocalId,first.LocalId]));
            Assert(disk.Resources[ResourceKind.Fleets].Draft.Select(r=>r.LocalId).SequenceEqual([first.LocalId,other.LocalId]));
            try {store.Save(stale);throw new Exception("stale accepted");}catch(InvalidOperationException e){Assert(MessageErrors.Describe(e).Code=="StorageStale");}
            await store.CheckpointWriter(w).WriteAsync(new(CheckpointKind.Rebase,w.Jobs[0],item,Delta:new(item.Resource,first.LocalId,new(RowMutationKind.Delete),new(RowMutationKind.Delete),DateTimeOffset.UnixEpoch,true,null)),default);
            Assert(store.Load(w.Id).Resources[ResourceKind.Fleets].Draft.Single().LocalId==other.LocalId);
        });
        await test("v3 delta payload and updated row set independent of unrelated rows and undo",()=>Benchmark(null));
    }
    public static async Task Benchmark(string? directory)
    {
        var template=Fixture().Serialize();long? expected=null;var results=new List<object>();
        foreach(var(rows,undo,same)in new[]{(1,0,false),(20000,0,false),(20000,3,false),(20000,3,true)})
        {
            var w=Workspace.Deserialize(template);var kind=same?ResourceKind.Fleets:ResourceKind.Routes;var data=w.Resources[kind];
            for(var n=0;n<rows;n++){var row=new DataRow{Fields=new(){["Name"]="Row "+n,["ID"]=n.ToString()}};data.Draft.Add(row);data.Snapshot.Add(row.Copy());}
            for(var n=0;n<undo;n++)data.Checkpoint();
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