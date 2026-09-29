using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using VamSys.Core;
using VamSys.Infrastructure;
using static StorageScenarios;

// The reference implementation deliberately uses the pre-v4 full-snapshot algorithm.
static class HistoryScenarios
{
    static void Check(bool value,string message="History assertion failed")=>StorageScenarios.Check(value,message);
    sealed class Oracle
    {
        public List<DataRow> Draft=[];
        public List<List<DataRow>> Undo=[],Redo=[];
        public void Checkpoint(){Undo.Add(Clone(Draft));if(Undo.Count>20)Undo.RemoveAt(0);Redo.Clear();}
        public void Back(){if(Undo.Count==0)return;Redo.Add(Draft);Draft=Undo[^1];Undo.RemoveAt(Undo.Count-1);}
        public void Forward(){if(Redo.Count==0)return;Undo.Add(Draft);Draft=Redo[^1];Redo.RemoveAt(Redo.Count-1);}
    }
    static List<DataRow> Clone(IEnumerable<DataRow> rows)=>rows.Select(r=>r.Copy()).ToList();
    // Canonical comparison is independent of the new RowImage equality and dictionary iteration order.
    static string Rows(IEnumerable<DataRow> rows)=>JsonSerializer.Serialize(rows.Select(r=>new {r.LocalId,r.Identity,r.RawApiJson,Fields=r.Fields.OrderBy(p=>p.Key,StringComparer.Ordinal)}));
    static void Match(ResourceData data,Oracle oracle,string context)
    {
        Check(Rows(data.Draft)==Rows(oracle.Draft),context+": draft");
        Check(data.Undo.Count==oracle.Undo.Count&&data.Redo.Count==oracle.Redo.Count,context+": stacks");
        var probe=data.ShallowCopy();
        for(var n=oracle.Undo.Count-1;n>=0;n--){probe.UndoEdit();Check(Rows(probe.Draft)==Rows(oracle.Undo[n]),context+": undo "+n);}
        probe=data.ShallowCopy();
        for(var n=oracle.Redo.Count-1;n>=0;n--){probe.RedoEdit();Check(Rows(probe.Draft)==Rows(oracle.Redo[n]),context+": redo "+n);}
    }
    internal static string LegacyJson(Workspace w)
    {
        var root=JsonNode.Parse(w.Serialize())!;
        foreach(var(kind,data)in w.Resources)
        {
            var undo=new List<List<DataRow>>();var redo=new List<List<DataRow>>();
            var probe=data.ShallowCopy();while(probe.Undo.Count>0){probe.UndoEdit();undo.Insert(0,Clone(probe.Draft));}
            probe=data.ShallowCopy();while(probe.Redo.Count>0){probe.RedoEdit();redo.Insert(0,Clone(probe.Draft));}
            var node=root["Resources"]![kind.ToString()]!.AsObject();node.Remove("History");
            node["Undo"]=JsonSerializer.SerializeToNode(undo);node["Redo"]=JsonSerializer.SerializeToNode(redo);
        }
        return root.ToJsonString();
    }
    static async Task Randomized(int seed)
    {
        var random=new Random(seed);var oracle=new Oracle();var w=new Workspace();var data=w.Resources[ResourceKind.Fleets];
        var dir=Temp();var store=new WorkspaceStore(dir);store.Save(w);
        for(var turn=0;turn<1000;turn++)
        {
            var op=random.Next(10);var checkpoint=op!=2;
            if(op==7){data.UndoEdit();oracle.Back();}
            else if(op==8){data.RedoEdit();oracle.Forward();}
            else
            {
                if(checkpoint)oracle.Checkpoint();
                if(oracle.Draft.Count==0||op==3)
                {
                    var row=new DataRow{Fields=new(){["Name"]="row "+turn,["empty"]=""},Identity=new(turn.ToString(),"7"),RawApiJson="{\"raw\":"+turn+"}"};
                    var position=random.Next(oracle.Draft.Count+1);
                    data.Edit([row.LocalId],()=>data.Draft.Insert(position,row.Copy()),newStep:checkpoint,structural:true);oracle.Draft.Insert(position,row.Copy());
                }
                else if(op==4)
                {
                    var position=random.Next(oracle.Draft.Count);var id=oracle.Draft[position].LocalId;
                    data.Edit([id],()=>data.Draft.RemoveAt(position),structural:true);oracle.Draft.RemoveAt(position);
                }
                else if(op==5)
                {data.Edit([],()=>data.Draft.Reverse(),structural:true);oracle.Draft.Reverse();}
                else if(op==6){data.Edit([],()=>{});}
                else
                {
                    var position=random.Next(oracle.Draft.Count);var id=oracle.Draft[position].LocalId;
                    var field=turn%2==0?"Name":"empty";var value=turn%7==0?"":"值"+turn;
                    void Change(DataRow row){if(turn%11==0)row.Fields.Remove(field);else row.Fields[field]=value;if(turn%13==0){row.Identity=null;row.RawApiJson=null;}}
                    data.Edit([id],()=>Change(data.Draft[position]),newStep:checkpoint);Change(oracle.Draft[position]);
                }
            }
            Match(data,oracle,$"seed {seed} turn {turn} op {op}");
            if(turn%29==0)
            {
                await store.SaveAsync(w);w=store.Load(w.Id);data=w.Resources[ResourceKind.Fleets];Match(data,oracle,"reload");
                w=Workspace.Deserialize(w.Serialize());data=w.Resources[ResourceKind.Fleets];Match(data,oracle,"JSON roundtrip");
                // The old store also reconstructed dictionaries on reload (including deleted-key slots).
                oracle.Draft=Clone(oracle.Draft);oracle.Undo=oracle.Undo.Select(Clone).ToList();oracle.Redo=oracle.Redo.Select(Clone).ToList();
            }
        }
    }
    public static async Task Run(Func<string,Func<Task>,Task> test)
    {
        foreach(var seed in new[]{73,20260929,9871})await test("v4 full-snapshot oracle: 1000 mixed edits, seed "+seed,()=>Randomized(seed));
        await test("v4 same-cell continuation persists updated step After and preserves immutable save snapshots",async()=>{
            foreach(var next in new[]{"same","other","undo","close","failure"})
            {
                var dir=Temp();var store=new WorkspaceStore(dir);var w=Fixture();var d=w.Resources[ResourceKind.Fleets];var row=d.Draft[0];var initial=row.Get("Name");store.Save(w);
                d.Edit([row.LocalId],()=>row.Fields["Name"]="first");var edits=new DraftEdits();edits.Cell(w,ResourceKind.Fleets,row.LocalId,true);var taken=edits.Take();
                using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();
                store.Barrier=p=>{if(p=="Draft:BeforeCommit"){entered.Set();if(!release.Wait(10000))throw new TimeoutException();if(next=="failure")throw new IOException("injected");}};
                var saving=store.TrySaveDraftRowsAsync(w,taken.Changes);Check(entered.Wait(10000));
                try
                {
                    if(next=="undo")d.UndoEdit();
                    else {d.Edit([row.LocalId],()=>row.Fields[next=="other"?"extra":"Name"]="second",newStep:next=="other");edits.Cell(w,ResourceKind.Fleets,row.LocalId,next=="other");}
                }
                finally{release.Set();}
                if(next=="failure")
                {try{await saving;throw new Exception("failure not raised");}catch(IOException){}edits.Restore(taken);Check(edits.RequiresFullSave);}
                else
                {
                    Check(await saving);var first=store.Load(w.Id);Check(first.Resources[ResourceKind.Fleets].Draft[0].Get("Name")=="first","save captured later mutation");
                    first.Resources[ResourceKind.Fleets].UndoEdit();Check(first.Resources[ResourceKind.Fleets].Draft[0].Get("Name")==initial);
                }
                store.Barrier=null;
                if(next is "undo" or "close" or "failure")await store.SaveAsync(w);else Check(await store.TrySaveDraftRowsAsync(w,edits.Take().Changes));
                var disk=store.Load(w.Id);Check(disk.Serialize()==w.Serialize(),"later edit lost: "+next);
                if(next=="same"){var dd=disk.Resources[ResourceKind.Fleets];dd.UndoEdit();Check(dd.Draft[0].Get("Name")==initial);dd.RedoEdit();Check(dd.Draft[0].Get("Name")=="second");}
            }
        });
        await test("v4 validates complete patches atomically and keeps shallow history copies independent",()=>{
            var d=new ResourceData{Draft=[new(){Fields=new(){["Name"]="old"}},new(){Fields=new(){["Name"]="second"}}]};
            var ids=d.Draft.Select(r=>r.LocalId).ToArray();d.Edit(ids,()=>{foreach(var row in d.Draft)row.Fields["Name"]="new";});
            var copy=d.ShallowCopy();d.Edit([ids[0]],()=>d.Draft[0].Fields["Name"]="later",newStep:false);
            Check(copy.Undo[0].Rows[0].After!.Fields["Name"]=="new","shared step mutated");
            var before=Rows(d.Draft);d.Draft[1].Fields["Name"]="unexpected";before=Rows(d.Draft);
            try{d.UndoEdit();throw new Exception("invalid patch accepted");}catch(InvalidDataException){}
            Check(Rows(d.Draft)==before&&d.Undo.Count==1&&d.Redo.Count==0,"partial patch applied");return Task.CompletedTask;
        });
        await test("v4 conflict deletion amends both undo and redo boundaries without a new step",()=>{
            var d=new ResourceData {Draft=[new(){Fields=new(){["Name"]="A"}},new(){Fields=new(){["Name"]="B"}}]};var oracle=new Oracle{Draft=Clone(d.Draft)};
            foreach(var value in new[]{"first","second"}){oracle.Checkpoint();d.Edit([d.Draft[0].LocalId],()=>d.Draft[0].Fields["Name"]=value);oracle.Draft[0].Fields["Name"]=value;}
            d.UndoEdit();oracle.Back();var id=d.Draft[1].LocalId;d.Edit([id],()=>d.Draft.RemoveAt(1),newStep:false,structural:true);oracle.Draft.RemoveAt(1);
            Match(d,oracle,"conflict delete");d.RedoEdit();oracle.Forward();Match(d,oracle,"redo after conflict delete");return Task.CompletedTask;
        });
        await test("v4 undo and redo retain captured unknown CSV field order",()=>{
            var row=new DataRow{Fields=new(){["Name"]="old",["Z unknown"]="z",["A unknown"]="a",["empty"]=""}};var d=new ResourceData{Draft=[row]};
            var before=JsonSerializer.Serialize(row);d.Edit([row.LocalId],()=>{row.Fields["Name"]="new";row.Fields.Remove("empty");row.Fields["B unknown"]="b";});var after=JsonSerializer.Serialize(row);
            d.UndoEdit();Check(JsonSerializer.Serialize(d.Draft[0])==before,"undo changed field order");d.RedoEdit();Check(JsonSerializer.Serialize(d.Draft[0])==after,"redo changed field order");return Task.CompletedTask;
        });
        await test("v4 JSON accepts legacy histories and rejects unknown or ambiguous versions",()=>{
            var w=Fixture();var d=w.Resources[ResourceKind.Fleets];var r=d.Draft[0];
            d.Edit([r.LocalId],()=>r.Fields["Name"]="A");d.Edit([r.LocalId],()=>r.Fields["Name"]="B");d.UndoEdit();
            var old=LegacyJson(w);var converted=Workspace.Deserialize(old);Check(!converted.Serialize().Contains("\"Undo\":[["));
            var oracle=new Oracle{Draft=Clone(d.Draft),Undo=JsonNode.Parse(old)!["Resources"]!["Fleets"]!["Undo"]!.Deserialize<List<List<DataRow>>>()!,Redo=JsonNode.Parse(old)!["Resources"]!["Fleets"]!["Redo"]!.Deserialize<List<List<DataRow>>>()!};
            Match(converted.Resources[ResourceKind.Fleets],oracle,"legacy JSON");
            foreach(var ambiguous in new[]{false,true})
            {
                var node=JsonNode.Parse(w.Serialize())!;var resource=node["Resources"]!["Fleets"]!;
                if(ambiguous)resource["Undo"]=new JsonArray();else resource["History"]!["Version"]=99;
                try{Workspace.Deserialize(node.ToJsonString());throw new Exception("unknown history accepted");}catch(JsonException){}
            }
            return Task.CompletedTask;
        });
        await MigrationTests(test);
        await test("v4 real cell edit history bounds, touched rows, eviction and redo deletion",()=>Benchmark("artifacts/step5-performance"));
        await test("v4 batch history retains only K changed rows and reports structural order cost",()=>{
            var results=new List<object>();
            foreach(var k in new[]{1,10,100})
            {
                var d=new ResourceData{Draft=Enumerable.Range(0,20000).Select(n=>new DataRow{Fields=new(){["Name"]="row"}}).ToList()};
                var chosen=d.Draft.Take(k).ToArray();d.FindDraftRow(chosen[0].LocalId);var memory=GC.GetTotalMemory(true);
                d.Edit(chosen.Select(r=>r.LocalId),()=>{foreach(var row in chosen)row.Fields["Name"]="changed";});
                var retained=GC.GetTotalMemory(true)-memory;var step=d.Undo.Single();var json=JsonSerializer.Serialize(step);
                Check(step.Rows.Length==k&&step.BeforeOrder==null&&step.AfterOrder==null);results.Add(new{k,jsonBytes=System.Text.Encoding.UTF8.GetByteCount(json),retainedManagedDeltaBytes=retained,rowSnapshots=2*k});
                var added=new DataRow();d.Edit([added.LocalId],()=>d.Draft.Insert(1,added),structural:true);step=d.Undo.Last();
                Check(step.Rows.Length==1&&step.BeforeOrder!.Value.Length==20000&&step.AfterOrder!.Value.Length==20001);
                results.Add(new{k=0,structural=true,orderGuids=40001,rowSnapshots=1,jsonBytes=System.Text.Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(step))});
            }
            File.WriteAllText("artifacts/step5-performance/batch.json",JsonSerializer.Serialize(results,new JsonSerializerOptions{WriteIndented=true}));return Task.CompletedTask;
        });
    }

    // Fixed old DDL and old full-snapshot JSON; no v4 store or history serializer creates these fixtures.
    internal static readonly Guid FixedId=Guid.Parse("c02a7c69-04d6-4ab8-8fb9-dc06e91a47ce");
    static string FixedRow(string value)=>"{\"LocalId\":\"11111111-1111-1111-1111-111111111111\",\"Identity\":{\"RemoteId\":\"9\",\"ConnectionId\":\"7\",\"ParentFleetId\":null},\"RawApiJson\":\"{raw}\",\"Fields\":{\"Name\":\""+value+"\",\"empty\":\"\"}}";
    internal static void FixedLegacy(string dir,int version)
    {
        using var c=Open(dir);
        Sql(c,"PRAGMA journal_mode=WAL;CREATE TABLE workspaces(id TEXT PRIMARY KEY,name TEXT NOT NULL,json TEXT NOT NULL"+(version>=2?",format_version INTEGER NOT NULL":"")+");CREATE TABLE secrets(id TEXT PRIMARY KEY,value BLOB NOT NULL);CREATE TABLE app_settings(key TEXT PRIMARY KEY,value TEXT NOT NULL);");
        var resources=new JsonObject();
        foreach(var kind in Enum.GetValues<ResourceKind>())resources[kind.ToString()]=JsonNode.Parse(kind==ResourceKind.Fleets?"{\"Snapshot\":[],\"Draft\":["+FixedRow("current")+"],\"Columns\":[\"Name\"],\"SnapshotAt\":null,\"Conflicts\":[],\"Undo\":[["+FixedRow("start")+"],["+FixedRow("start")+"]],\"Redo\":[["+FixedRow("future2")+"],["+FixedRow("future1")+"]]}":"{\"Snapshot\":[],\"Draft\":[],\"Columns\":[],\"SnapshotAt\":null,\"Conflicts\":[],\"Undo\":[],\"Redo\":[]}");
        var root=new JsonObject{["Id"]=FixedId.ToString(),["Name"]="fixed legacy",["Mode"]=0,["AirlineId"]="7",["VerifiedOperations"]=new JsonArray("Fleets:Create"),["Resources"]=resources.DeepClone(),["Jobs"]=new JsonArray()};
        if(version>=2)
        {
            root.Remove("Jobs");Sql(c,"CREATE TABLE workspace_revisions(workspace_id TEXT PRIMARY KEY,revision INTEGER NOT NULL);CREATE TABLE batch_jobs(workspace_id TEXT,id TEXT,ordinal INTEGER,json TEXT,PRIMARY KEY(workspace_id,id));CREATE TABLE batch_items(workspace_id TEXT,job_id TEXT,id TEXT,ordinal INTEGER,json TEXT,PRIMARY KEY(workspace_id,job_id,id));");
            Sql(c,"INSERT INTO workspace_revisions VALUES($id,7)",("$id",FixedId.ToString()));
        }
        if(version==3)
        {
            root.Remove("Resources");
            Sql(c,"CREATE TABLE resource_metadata(workspace_id TEXT,resource INTEGER,columns_json TEXT,conflicts_json TEXT,snapshot_at TEXT,PRIMARY KEY(workspace_id,resource));CREATE TABLE resource_rows(workspace_id TEXT,resource INTEGER,collection INTEGER,local_id TEXT,ordinal INTEGER,json TEXT,PRIMARY KEY(workspace_id,resource,collection,local_id));CREATE UNIQUE INDEX resource_row_order ON resource_rows(workspace_id,resource,collection,ordinal);CREATE TABLE resource_history(workspace_id TEXT,resource INTEGER,undo_json TEXT,redo_json TEXT,PRIMARY KEY(workspace_id,resource));");
            foreach(var kind in Enum.GetValues<ResourceKind>())
            {
                var data=resources[kind.ToString()]!;
                Sql(c,"INSERT INTO resource_metadata VALUES($w,$r,$cols,'[]',NULL)",("$w",FixedId.ToString()),("$r",(int)kind),("$cols",data["Columns"]!.ToJsonString()));
                Sql(c,"INSERT INTO resource_history VALUES($w,$r,$u,$d)",("$w",FixedId.ToString()),("$r",(int)kind),("$u",data["Undo"]!.ToJsonString()),("$d",data["Redo"]!.ToJsonString()));
            }
            Sql(c,"INSERT INTO resource_rows VALUES($w,1,1,'11111111-1111-1111-1111-111111111111',0,$j)",("$w",FixedId.ToString()),("$j",FixedRow("current")));
        }
        Sql(c,"INSERT INTO workspaces VALUES($id,'fixed legacy',$j"+(version>=2?","+version:"")+")",("$id",FixedId.ToString()),("$j",root.ToJsonString()));
        Sql(c,"INSERT INTO secrets VALUES($id,X'010203');INSERT INTO app_settings VALUES('language','en-US');PRAGMA user_version="+version,("$id",FixedId.ToString()));
        if(version>=2)Sql(c,$"CREATE TRIGGER workspace_format_update BEFORE UPDATE ON workspaces WHEN NEW.format_version!={version} BEGIN SELECT RAISE(ABORT,'old format');END;CREATE TRIGGER workspace_format_insert BEFORE INSERT ON workspaces WHEN NEW.format_version!={version} BEGIN SELECT RAISE(ABORT,'old format');END;");
    }
    static async Task MigrationTests(Func<string,Func<Task>,Task> test)
    {
        foreach(var version in new[]{0,1,2,3})await test("v4 fixed legacy migration preserves all undo/redo nodes: v"+version,()=>{
            var dir=Temp();FixedLegacy(dir,version);var before=GC.GetTotalAllocatedBytes(true);var timer=Stopwatch.StartNew();var store=new WorkspaceStore(dir);
            var migrationMs=timer.Elapsed.TotalMilliseconds;var allocated=GC.GetTotalAllocatedBytes(true)-before;
            Directory.CreateDirectory("artifacts/step5-performance");File.WriteAllText($"artifacts/step5-performance/migration-v{version}.json",JsonSerializer.Serialize(new{version,migrationMs,allocatedBytes=allocated,backupBytes=new FileInfo(store.MigrationBackup!).Length}));
            var w=store.Load(FixedId);var d=w.Resources[ResourceKind.Fleets];
            Check(d.Undo.Count==2&&d.Redo.Count==2&&w.StorageRevision==(version>=2?7:0)&&w.AirlineId=="7"&&w.VerifiedOperations.Single()=="Fleets:Create");
            d.UndoEdit();Check(d.Draft[0].Get("Name")=="start");d.UndoEdit();Check(d.Draft[0].Get("Name")=="start");
            foreach(var value in new[]{"start","current","future1","future2"}){d.RedoEdit();Check(d.Draft[0].Get("Name")==value,"redo cursor conversion");}
            Check(d.Draft[0].Identity!.RemoteId=="9"&&d.Draft[0].RawApiJson=="{raw}"&&d.Draft[0].Fields.ContainsKey("empty"));
            using var c=Open(dir);Check(Convert.ToInt32(Sql(c,"PRAGMA user_version"))==4&&Convert.ToInt32(Sql(c,"SELECT count(*) FROM sqlite_master WHERE name='resource_history'"))==0);
            Check(Convert.ToInt32(Sql(c,"SELECT length(value) FROM secrets"))==3&&store.LoadLanguage()=="en-US");
            using var backup=new Microsoft.Data.Sqlite.SqliteConnection("Data Source="+store.MigrationBackup);backup.Open();Check(Convert.ToInt32(Sql(backup,"PRAGMA user_version"))==version&&(string)Sql(backup,"PRAGMA integrity_check")! =="ok");
            Check(new WorkspaceStore(dir).MigrationBackup==null&&Directory.GetFiles(dir,"workspaces-before-v4-*.db").Length==1);return Task.CompletedTask;
        });
        foreach(var invalid in new[]{"json","duplicate","missing"})await test("v4 rejects damaged v3 history atomically: "+invalid,()=>{
            var dir=Temp();FixedLegacy(dir,3);using var c=Open(dir);var original=(string)Sql(c,"SELECT json FROM workspaces")!;
            if(invalid=="missing")Sql(c,"DELETE FROM resource_history WHERE resource=1");
            else Sql(c,"UPDATE resource_history SET undo_json=$j WHERE resource=1",("$j",invalid=="json"?"broken":"[["+FixedRow("x")+","+FixedRow("x")+"]]"));
            var rejected=false;try{new WorkspaceStore(dir);}catch(Exception e)when(e is JsonException or ArgumentException or InvalidOperationException or InvalidDataException){rejected=true;}
            Check(rejected&&Convert.ToInt32(Sql(c,"PRAGMA user_version"))==3&&(string)Sql(c,"SELECT json FROM workspaces")! ==original);
            Check(Convert.ToInt32(Sql(c,"SELECT count(*) FROM sqlite_master WHERE name='resource_history_steps'"))==0);return Task.CompletedTask;
        });
        if(File.Exists("artifacts/step5-v3-baseline/tests/VamSys.Tests.dll"))
        await test("actual v3 binary refuses v4 database without changing it",async()=>{
            var baseline=Path.GetFullPath("artifacts/step5-v3-baseline/tests/VamSys.Tests.dll");Check(File.Exists(baseline),"v3 baseline missing");
            var dir=Temp();var store=new WorkspaceStore(dir);var w=Fixture();store.Save(w);using(var c=Open(dir))Sql(c,"PRAGMA wal_checkpoint(TRUNCATE)");
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            var path=Path.Combine(dir,"workspaces.db");var before=File.ReadAllBytes(path);
            var info=new ProcessStartInfo(Path.GetFullPath(".tools/dotnet/dotnet.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true,RedirectStandardOutput=true};
            foreach(var arg in new[]{baseline,"--assert-close-ui",dir})info.ArgumentList.Add(arg);
            using var process=Process.Start(info)!;var error=process.StandardError.ReadToEndAsync();var output=process.StandardOutput.ReadToEndAsync();await process.WaitForExitAsync();
            var log=await error+await output;Check(process.ExitCode!=0&&log.Contains("WorkspaceStore.Initialize(String directory)")&&log.Contains("WorkspaceStore.Schema.cs:line 15"),log);
            Check(before.SequenceEqual(File.ReadAllBytes(path)),"old reader changed v4 database");
        });
    }
    public static async Task Benchmark(string directory)
    {
        Directory.CreateDirectory(directory);var results=new List<object>();
        foreach(var count in new[]{1,1000,20000})
        {
            var w=new Workspace();var d=w.Resources[ResourceKind.Fleets];d.Draft=Enumerable.Range(0,count).Select(n=>new DataRow{Fields=new(){["Name"]="row",["ID"]=n.ToString()}}).ToList();
            var dir=Temp();var store=new WorkspaceStore(dir);store.Save(w);d.FindDraftRow(d.Draft[0].LocalId);
            var samples=new List<double>();var allocations=new List<long>();var bytes=new List<long>();
            using var c=Open(dir);Sql(c,"CREATE TABLE touched(tbl TEXT,id TEXT);CREATE TRIGGER row_u AFTER UPDATE ON resource_rows BEGIN INSERT INTO touched VALUES('row',NEW.local_id);END;CREATE TRIGGER step_i AFTER INSERT ON resource_history_steps BEGIN INSERT INTO touched VALUES('insert',NEW.step_id);END;CREATE TRIGGER step_u AFTER UPDATE ON resource_history_steps BEGIN INSERT INTO touched VALUES('update',NEW.step_id);END;CREATE TRIGGER step_d AFTER DELETE ON resource_history_steps BEGIN INSERT INTO touched VALUES('delete',OLD.step_id);END;CREATE TRIGGER head_u AFTER UPDATE ON resource_history_state BEGIN INSERT INTO touched VALUES('head','');END;CREATE TRIGGER revision_u AFTER UPDATE ON workspace_revisions BEGIN INSERT INTO touched VALUES('revision','');END;");
            for(var n=0;n<30;n++)
            {
                Sql(c,"DELETE FROM touched");var before=GC.GetTotalAllocatedBytes(true);using var metrics=PerformanceRun.Begin(w.Id,Guid.NewGuid());var watch=Stopwatch.StartNew();
                var row=d.Draft[n%count];var evicted=d.Undo.Count==20?d.Undo[0].StepId:(Guid?)null;
                d.Edit([row.LocalId],()=>row.Fields["Name"]="edit"+n);var edits=new DraftEdits();edits.Cell(w,ResourceKind.Fleets,row.LocalId,true);Check(await store.TrySaveDraftRowsAsync(w,edits.Changes));
                var ms=watch.Elapsed.TotalMilliseconds;var allocation=GC.GetTotalAllocatedBytes(true)-before;
                Check(metrics.JsonBytes<=8192,"history payload grew with draft size");
                Check(Convert.ToInt32(Sql(c,"SELECT count(*) FROM touched"))==(evicted==null?4:5),"wrong write scope");
                Check(Convert.ToInt32(Sql(c,"SELECT count(*) FROM touched WHERE tbl='update'"))==0,"unchanged step rewritten");
                if(evicted!=null)Check((string)Sql(c,"SELECT id FROM touched WHERE tbl='delete'")! ==evicted.ToString());
                Check(d.Undo.Sum(s=>s.Rows.Sum(r=>(r.Before==null?0:1)+(r.After==null?0:1)))<=40&&d.Undo.All(s=>s.BeforeOrder==null&&s.AfterOrder==null));
                if(n>=10){samples.Add(ms);allocations.Add(allocation);bytes.Add(metrics.JsonBytes);}
            }
            var expected=d.Draft[0].Get("Name");var disk=store.Load(w.Id);Check(disk.Serialize()==w.Serialize());
            d.UndoEdit();d.UndoEdit();store.Save(w);var discarded=d.Redo.Select(s=>s.StepId).ToArray();
            var current=d.Draft[0];d.Edit([current.LocalId],()=>current.Fields["Name"]="branch");var pending=new DraftEdits();pending.Cell(w,ResourceKind.Fleets,current.LocalId,true);Check(await store.TrySaveDraftRowsAsync(w,pending.Changes));
            foreach(var id in discarded)Check(Convert.ToInt32(Sql(c,"SELECT count(*) FROM resource_history_steps WHERE step_id=$id",("$id",id.ToString())))==0,"discarded redo still stored");
            Check(store.Load(w.Id).Resources[ResourceKind.Fleets].Redo.Count==0);
            samples.Sort();var p95=samples[(int)Math.Ceiling(samples.Count*.95)-1];var median=(samples[9]+samples[10])/2;
            results.Add(new{rows=count,samples,medianMs=median,p95Ms=p95,allocatedBytes=allocations,jsonBytes=bytes,retainedRowSnapshots=40});
            Console.WriteLine($"  v4 rows={count}: median {median:F2} ms, P95 {p95:F2} ms, max JSON {bytes.Max()} B");
            Check(p95<=100,"single-cell edit + durable save exceeds P95 100ms");
        }
        await File.WriteAllTextAsync(Path.Combine(directory,"history.json"),JsonSerializer.Serialize(results,new JsonSerializerOptions{WriteIndented=true}));
    }
}
