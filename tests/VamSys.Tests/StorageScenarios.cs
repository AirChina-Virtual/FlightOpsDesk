using Microsoft.Data.Sqlite;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VamSys.Core;
using VamSys.Infrastructure;

// Shared storage fixtures and helpers of the legacy runner (used by the process-termination scenarios, the window scripts and the
// benchmark entry points). Every other storage scenario now lives in tests/VamSys.IntegrationTests/Storage.
static class StorageScenarios
{
    internal static void Check(bool ok,string message="Storage assertion failed"){if(!ok)throw new Exception(message);}
    internal static string Temp(){var p=Path.Combine(Path.GetTempPath(),"vamsys-v3-"+Guid.NewGuid());Directory.CreateDirectory(p);return p;}
    internal static SqliteConnection Open(string dir){var c=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(dir,"workspaces.db"),Pooling=false}.ToString());c.Open();return c;}
    internal static object? Sql(SqliteConnection c,string sql,params (string,object)[] values)
    {using var cmd=c.CreateCommand();cmd.CommandText=sql;foreach(var(k,v)in values)cmd.Parameters.AddWithValue(k,v);return cmd.ExecuteScalar();}
    internal static Workspace Fixture()
    {
        var w=new Workspace{Name="Storage fixture",AirlineId="7",Mode=RunMode.Online};
        var r=new DataRow{Fields = new Dictionary<string, string> {["Name"]="New",["Type Code"]="B738",["Type (pax/cargo/...)"]="pax",["Max Passengers"]="180",["Unknown"]="001"}};
        w.Resources[ResourceKind.Fleets].Draft.Add(r);
        w.Resources[ResourceKind.Fleets].Edit([],()=>{});
        w.Jobs.Add(new(){Mode=RunMode.Online,Items=new ChangePlanner().Plan(ResourceKind.Fleets,w.Resources[ResourceKind.Fleets])});
        return w;
    }
    internal static void LegacyFleetSteps(ChangeItem item)
    {
        var post=new ApiStep(HttpMethod.Post,"fleet",new(){["name"]=item.After.Get("Name"),["code"]=item.After.Get("Type Code"),["type"]=item.After.Get("Type (pax/cargo/...)")});
        var put=new ApiStep(HttpMethod.Put,"{created}",new(){["max_pax"]=long.Parse(item.After.Get("Max Passengers"),System.Globalization.CultureInfo.InvariantCulture)});
        OperationsAdapter.PrepareSteps(item,[post,put]);item.ApiExpectedValues=OperationsAdapter.ExpectedValues([post,put]);
    }
    internal static SqliteConnection Legacy(string dir,params Workspace[] workspaces)
    {
        var c=Open(dir);Sql(c,"PRAGMA journal_mode=WAL; CREATE TABLE workspaces(id TEXT PRIMARY KEY,name TEXT NOT NULL,json TEXT NOT NULL); CREATE TABLE secrets(id TEXT PRIMARY KEY,value BLOB NOT NULL); CREATE TABLE app_settings(key TEXT PRIMARY KEY,value TEXT NOT NULL);");
        foreach(var w in workspaces)
        {
            var node=System.Text.Json.Nodes.JsonNode.Parse(LegacyJson(w))!;
            node.AsObject().Remove("StorageRevision");
            foreach(var j in node["Jobs"]!.AsArray())foreach(var i in j!["Items"]!.AsArray())i!.AsObject().Remove("ApiSteps");
            Sql(c,"INSERT INTO workspaces VALUES($id,$name,$json)",("$id",w.Id.ToString()),("$name",w.Name),("$json",node.ToJsonString()));
        }
        Sql(c,"INSERT INTO app_settings VALUES('language','en-US')");
        return c;
    }
    static List<DataRow> Clone(IEnumerable<DataRow> rows)=>rows.Select(r=>r.Dup()).ToList();
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
    public static async Task Run(Func<string,Func<Task>,Task> test)
    {
        await V3BinaryRefusal(test);
        await StorageProcessScenarios.Run(test);
    }
    // Needs the real v3 program kept in artifacts/step5-v3-baseline; it can only be started as a process, so it stays here.
    static async Task V3BinaryRefusal(Func<string,Func<Task>,Task> test)
    {
        if(!File.Exists("artifacts/step5-v3-baseline/tests/VamSys.Tests.dll"))return;
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
