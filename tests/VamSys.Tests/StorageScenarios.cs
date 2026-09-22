using Microsoft.Data.Sqlite;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using VamSys.Core;
using VamSys.Infrastructure;

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
        var r=new DataRow{Fields=new(){["Name"]="New",["Type Code"]="B738",["Type (pax/cargo/...)"]="pax",["Max Passengers"]="180",["Unknown"]="001"}};
        w.Resources[ResourceKind.Fleets].Draft.Add(r);
        w.Resources[ResourceKind.Fleets].Checkpoint();
        w.Jobs.Add(new(){Mode=RunMode.Online,Items=new ChangePlanner().Plan(ResourceKind.Fleets,w.Resources[ResourceKind.Fleets])});
        return w;
    }
    internal static SqliteConnection Legacy(string dir,params Workspace[] workspaces)
    {
        var c=Open(dir);Sql(c,"PRAGMA journal_mode=WAL; CREATE TABLE workspaces(id TEXT PRIMARY KEY,name TEXT NOT NULL,json TEXT NOT NULL); CREATE TABLE secrets(id TEXT PRIMARY KEY,value BLOB NOT NULL); CREATE TABLE app_settings(key TEXT PRIMARY KEY,value TEXT NOT NULL);");
        foreach(var w in workspaces)
        {
            var node=System.Text.Json.Nodes.JsonNode.Parse(w.Serialize())!;
            node.AsObject().Remove("StorageRevision");
            foreach(var j in node["Jobs"]!.AsArray())foreach(var i in j!["Items"]!.AsArray())i!.AsObject().Remove("ApiSteps");
            Sql(c,"INSERT INTO workspaces VALUES($id,$name,$json)",("$id",w.Id.ToString()),("$name",w.Name),("$json",node.ToJsonString()));
        }
        Sql(c,"INSERT INTO app_settings VALUES('language','en-US')");
        return c;
    }
    static async Task Throws(Func<Task> action,string? code=null)
    {try{await action();}catch(Exception e){if(code!=null)Check(MessageErrors.Describe(e).Code==code,e.ToString());return;}throw new Exception("Expected failure");}
    public static async Task Run(Func<string,Func<Task>,Task> test)
    {
        await test("v3 migration backs up committed WAL and preserves full business format and ordering",async()=>{
            var dir=Temp();var a=Fixture();var b=Fixture();b.Name="Second";var item=a.Jobs[0].Items[0];
            item.State=ItemState.Unknown;item.RemoteId="102";item.ProposeRecoveryId("103");item.Message="历史原文";a.Jobs[0].Status="旧状态";
            a.Jobs.Add(new(){Items=[new(){Message="second item"},new(){Message="third item"}]});
            using var legacy=Legacy(dir,a,b);
            Sql(legacy,"INSERT INTO secrets VALUES($id,$value)",("$id",a.Id.ToString()),("$value",new byte[]{1,2,3}));
            var store=new WorkspaceStore(dir);Check(store.MigrationBackup!=null);
            using(var backup=new SqliteConnection("Data Source="+store.MigrationBackup))
            {
                backup.Open();Check((string)Sql(backup,"PRAGMA integrity_check")! =="ok");
                Check(Convert.ToInt32(Sql(backup,"SELECT count(*) FROM workspaces"))==2);
                Check(Convert.ToInt32(Sql(backup,"PRAGMA user_version"))==0);
                var old=Workspace.Deserialize((string)Sql(backup,"SELECT json FROM workspaces WHERE id=$id",("$id",a.Id.ToString()))!);
                Check(old.Jobs[0].Items[0].RecoveryCandidateId=="103");
            }
            var loaded=store.Load(a.Id);
            Check(loaded.Jobs.Select(j=>j.Id).SequenceEqual(a.Jobs.Select(j=>j.Id)));
            Check(loaded.Jobs[1].Items.Select(i=>i.Id).SequenceEqual(a.Jobs[1].Items.Select(i=>i.Id)));
            Check(loaded.Jobs[0].Items[0].RecoveryCandidates.Count==2&&loaded.Jobs[0].Items[0].Message=="历史原文");
            Check(loaded.Resources[ResourceKind.Fleets].Undo.Count==1&&loaded.Resources[ResourceKind.Fleets].Draft[0].Get("Unknown")=="001");
            Check(Workspace.Deserialize(loaded.Serialize()).Jobs.Count==2&&store.LoadLanguage()=="en-US");
            using var c=Open(dir);Check(!JsonDocument.Parse((string)Sql(c,"SELECT json FROM workspaces LIMIT 1")!).RootElement.TryGetProperty("Jobs",out _));
            Check(Convert.ToInt32(Sql(c,"SELECT length(value) FROM secrets"))==3);
            await Throws(()=>{Sql(c,"INSERT INTO workspaces VALUES('old','old','{}')");return Task.CompletedTask;});
            Check(new WorkspaceStore(dir).MigrationBackup==null);
        });
        foreach(var corruption in new[]{"json","duplicate","barrier"})
        await test("v3 migration rolls back every workspace on "+corruption,async()=>{
            var dir=Temp();var a=Fixture();var b=Fixture();
            if(corruption=="duplicate")b.Jobs[0].Items.Add(b.Jobs[0].Items[0]);
            using var c=Legacy(dir,a,b);
            if(corruption=="json")Sql(c,"UPDATE workspaces SET json='broken' WHERE id=$id",("$id",b.Id.ToString()));
            await Throws(()=>{new WorkspaceStore(dir,point=>{if(corruption=="barrier"&&point=="Migration:BeforeCommit")throw new IOException("injected");});return Task.CompletedTask;});
            Check(Convert.ToInt32(Sql(c,"PRAGMA user_version"))==0);
            Check(Convert.ToInt32(Sql(c,"SELECT count(*) FROM pragma_table_info('workspaces')"))==3);
            Check(Convert.ToInt32(Sql(c,"SELECT count(*) FROM sqlite_master WHERE name='batch_items'"))==0);
            Check(Directory.GetFiles(dir,"workspaces-before-v3-*.db").Length==1);
            Sql(c,"UPDATE workspaces SET json=$json WHERE id=$id",("$id",b.Id.ToString()),("$json",FixtureWithId(b.Id).Serialize()));
            Check(new WorkspaceStore(dir).Load(a.Id).Jobs.Count==1);
        });
        await test("v3 rejects future formats and limits instances by data directory",async()=>{
            var dir=Temp();new WorkspaceStore(dir);using var c=Open(dir);Sql(c,"PRAGMA user_version=4");
            await Throws(()=>{new WorkspaceStore(dir);return Task.CompletedTask;},"StorageNewer");
            Check(Convert.ToInt32(Sql(c,"PRAGMA user_version"))==4);
            using(var lease=new DataDirectoryLease(dir))
            {
                await Throws(()=>{using var other=new DataDirectoryLease(dir);return Task.CompletedTask;},"StorageInUse");
                using var separate=new DataDirectoryLease(Temp());
            }
            using var reopened=new DataDirectoryLease(dir);
        });
        await test("v3 task and candidate checkpoints never write resource JSON and reject stale full saves",async()=>{
            var dir=Temp();var store=new WorkspaceStore(dir);var w=Fixture();w.Jobs[0].Items.Add(new(){Message="untouched"});store.Save(w);
            var stale=store.Load(w.Id);using var c=Open(dir);var original=(string)Sql(c,"SELECT json FROM workspaces")!;
            var other=(string)Sql(c,"SELECT json FROM batch_items WHERE ordinal=1")!;
            Sql(c,"CREATE TRIGGER forbid_resources BEFORE UPDATE ON workspaces BEGIN SELECT RAISE(ABORT,'resource write'); END");
            var item=w.Jobs[0].Items[0];item.State=ItemState.Unknown;item.ProposeRecoveryId("10");
            await store.CheckpointWriter(w).WriteAsync(new(CheckpointKind.Task,w.Jobs[0],item),default);
            Check(store.Load(w.Id).Jobs[0].Items[0].RecoveryCandidateId=="10");
            Check((string)Sql(c,"SELECT json FROM workspaces")! ==original&&(string)Sql(c,"SELECT json FROM batch_items WHERE ordinal=1")! ==other);
            Sql(c,"DROP TRIGGER forbid_resources");
            await Throws(()=>store.SaveAsync(stale),"StorageStale");
            Check(store.Load(w.Id).Jobs[0].Items[0].State==ItemState.Unknown);
            var revision=w.StorageRevision;
            await Throws(()=>store.CheckpointWriter(w).WriteAsync(new(CheckpointKind.Task,new BatchJob(),item),default),"StorageInvalid");
            Check(store.Load(w.Id).StorageRevision==revision);
        });
        foreach(var point in new[]{"Rebase:BeforeCommit","Rebase:AfterCommit"})
        await test("v3 rebase transaction has atomic snapshots and task status at "+point,async()=>{
            var dir=Temp();var store=new WorkspaceStore(dir);var w=Fixture();store.Save(w);
            var staged=Workspace.Deserialize(w.Serialize());var item=staged.Jobs[0].Items[0];
            item.State=ItemState.Succeeded;item.RemoteId="10";item.Rebased=true;
            staged.Resources[ResourceKind.Fleets].Snapshot=[item.After.Copy()];staged.Resources[ResourceKind.Fleets].Undo.Clear();staged.VerifiedOperations.Add("Fleets:Create");
            store.Barrier=p=>{if(p==point)throw new IOException("fault");};
            using var run=PerformanceRun.Begin(w.Id,w.Jobs[0].Id);
            await Throws(()=>store.CheckpointWriter(w).WriteAsync(new(CheckpointKind.Rebase,staged.Jobs[0],item,staged),default));
            var loaded=store.Load(w.Id);var committed=point.EndsWith("AfterCommit");
            Check((loaded.Jobs[0].Items[0].State==ItemState.Succeeded)==committed);
            Check(loaded.Resources[ResourceKind.Fleets].Snapshot.Count==(committed?1:0));
            Check(loaded.Resources[ResourceKind.Fleets].Undo.Count==(committed?0:1));
            Check(loaded.VerifiedOperations.Contains("Fleets:Create")==committed);
            Check(w.Jobs[0].Items[0].State==ItemState.Pending&&w.Resources[ResourceKind.Fleets].Undo.Count==1);
            Check(run.SaveSuccesses==(committed?1:0)&&run.SaveFailures==(committed?0:1));
        });
        await test("v3 state payload size is independent of 20k resources and three undo snapshots",async()=>{await Benchmark(null);});
        await test("Seventh audit fixed-seed differential index regression 6000 mutations / 66000 queries",()=>{
            Differential();return Task.CompletedTask;
        });
        await test("Executor checkpoint failures stop writes and apply rebase only after commit",async()=>{
            foreach(var fault in new[]{"id","rebase"})
            {
                var dir=Temp();var store=new WorkspaceStore(dir);var w=Fixture();store.Save(w);var item=w.Jobs[0].Items[0];
                var clock=new TestTime();var handler=new AuditHttp();var puts=0;
                const string record="""{"id":10,"airline_id":7,"name":"New","code":"B738","type":"pax","max_pax":180}""";
                handler.Next=(r,ct)=>{
                    if(r.Method==System.Net.Http.HttpMethod.Post)return new(System.Net.HttpStatusCode.Created){Content=new StringContent(record)};
                    if(r.Method==System.Net.Http.HttpMethod.Put){puts++;return new(System.Net.HttpStatusCode.OK){Content=new StringContent("{}")};}
                    return new(System.Net.HttpStatusCode.OK){Content=new StringContent(r.RequestUri!.AbsolutePath.EndsWith("/10")?"{\"data\":"+record+"}":"""{"data":[{"id":4,"airline_id":7,"name":"Existing","code":"B738","type":"pax"}],"meta":{"next_cursor_url":null}}""")};
                };
                var api=new OperationsAdapter(new OperationsTransport(new HttpClient(handler),OperationsAdapter.BaseUri,"failure",new TokenProvider(_=>Task.FromResult(new AccessToken("test",clock.GetUtcNow().AddHours(1))),clock),new(clock)),w);
                store.Barrier=p=>{if(fault=="id"?p=="Step:BeforeCommit"&&item.CreateCompleted:p=="Rebase:BeforeCommit")throw new IOException("injected");};
                try {await clock.Drive(new OperationsBatchExecutor(api,w).ExecuteAsync(w.Jobs[0],store.CheckpointWriter(w),default));throw new Exception("Expected persistence stop");}
                catch(PersistenceException){}
                Check(puts==(fault=="id"?0:1));
                Check(item.State!=ItemState.Succeeded&&w.Resources[ResourceKind.Fleets].Snapshot.Count==0&&w.Resources[ResourceKind.Fleets].Undo.Count==1&&w.VerifiedOperations.Count==0);
                var disk=store.Load(w.Id);Check(disk.Jobs[0].Items[0].CreateCompleted==(fault=="rebase"));
                Check(disk.Jobs[0].Items[0].ApiSteps.Count==2&&disk.Jobs[0].Items[0].ApiExpectedValues!["max_pax"].GetInt32()==180);
            }
        });
        await test("v3 recovery persists Unknown after timeout, cancellation, auth, HTTP and invalid JSON",async()=>{
            foreach(var fault in new[]{"timeout","cancel","401","403","429","503","json"})
            {
                var dir=Temp();var store=new WorkspaceStore(dir);var w=Fixture();var item=w.Jobs[0].Items[0];item.State=ItemState.Unknown;item.RemoteId="10";store.Save(w);
                for(var attempt=0;attempt<2;attempt++)
                {
                    w=store.Load(w.Id);var clock=new TestTime();var handler=new AuditHttp();using var cancel=new CancellationTokenSource();
                    handler.Next=(r,ct)=>{
                        Check(r.Method==HttpMethod.Get,"Unknown recovery wrote");
                        if(fault=="timeout")throw new TimeoutException();
                        if(fault=="cancel"){cancel.Cancel();throw new OperationCanceledException(cancel.Token);}
                        return new(fault=="json"?System.Net.HttpStatusCode.OK:(System.Net.HttpStatusCode)int.Parse(fault)){Content=new StringContent("invalid JSON")};
                    };
                    var api=new OperationsAdapter(new OperationsTransport(new HttpClient(handler),OperationsAdapter.BaseUri,"recovery",new TokenProvider(_=>Task.FromResult(new AccessToken("test",clock.GetUtcNow().AddHours(1))),clock),new(clock)),w);
                    await clock.Drive(new OperationsBatchExecutor(api,w).ExecuteAsync(w.Jobs[0],store.CheckpointWriter(w),cancel.Token));
                    var disk=store.Load(w.Id);Check(disk.Jobs[0].Items[0].State==ItemState.Unknown&&handler.Methods.All(m=>m==HttpMethod.Get));
                    if(fault is "401" or "403")Check(disk.Jobs[0].StatusCode==JobStatus.Paused);
                    if(fault=="cancel")Check(disk.Jobs[0].StatusCode==JobStatus.Canceled);
                }
            }
        });
        await StorageProcessScenarios.Run(test);
    }
    static Workspace FixtureWithId(Guid id){var w=Fixture();w.Id=id;return w;}
    public static async Task Benchmark(string? output)
    {
        var template=Fixture().Serialize();var results=new List<object>();long? expected=null;
        foreach(var(rows,undo)in new[]{(1,0),(20000,0),(20000,3)})
        {
            var w=Workspace.Deserialize(template);var data=w.Resources[ResourceKind.Routes];
            data.Draft=Enumerable.Range(0,rows).Select(i=>new DataRow{Fields=new(){["ID"]=i.ToString(),["Callsign"]="ACA"+i,["Remarks"]="性能"}}).ToList();
            data.Snapshot=data.Draft.Select(r=>r.Copy()).ToList();
            for(var n=0;n<undo;n++)data.Checkpoint();
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
            var staged=Workspace.Deserialize(w.Serialize());var changed=staged.Jobs[0].Items[0];changed.State=ItemState.Succeeded;staged.Resources[ResourceKind.Fleets].Undo.Clear();
            before=GC.GetTotalAllocatedBytes(true);timer.Restart();
            using var rebase=PerformanceRun.Begin(w.Id,w.Jobs[0].Id);
            await store.CheckpointWriter(w).WriteAsync(new(CheckpointKind.Rebase,staged.Jobs[0],changed,staged),default);
            var rebaseMs=timer.Elapsed.TotalMilliseconds;var rebaseAllocation=GC.GetTotalAllocatedBytes(true)-before;
            results.Add(new{rows,undo,updates=20,bytes,elapsedMs=elapsed,allocatedBytes=allocated,resourceUpdates=0,rebaseBytes=rebase.JsonBytes,rebaseMs,rebaseAllocatedBytes=rebaseAllocation,rebaseTransactions=rebase.SaveSuccesses});
            Console.WriteLine($"  v3 rows={rows} undo={undo}: task bytes={bytes}, {elapsed:F1} ms; rebase bytes={rebase.JsonBytes}, {rebaseMs:F1} ms");
        }
        if(output!=null){Directory.CreateDirectory(output);await File.WriteAllTextAsync(Path.Combine(output,"storage-benchmark.json"),JsonSerializer.Serialize(results,new JsonSerializerOptions{WriteIndented=true}));}
    }
    static void Differential()
    {
        var rng=new Random(20260922);var names=new[]{"Same","SAME","Renamed","old","Old","A|B","A\nB","中文","007","7",""};
        foreach(var kind in new[]{ResourceKind.Fleets,ResourceKind.Aircraft,ResourceKind.Airports})
        {
            var index=new CandidateIndex();var baseline=new Dictionary<string,string[]>();
            for(var step=0;step<2000;step++)
            {
                var id=rng.Next(1,101).ToString();
                if(rng.Next(4)==0){index.Remove(id);baseline.Remove(id);}
                else
                {
                    string[] keys=kind==ResourceKind.Airports?[names[rng.Next(names.Length)],names[rng.Next(names.Length)]]:[names[rng.Next(names.Length)]];
                    var row=new DataRow{Identity=new(id,"7"),Fields=new(){["Name"]=keys[0]}};
                    index.Put(row,keys.Select(n=>new CandidateSignature(kind,n)));baseline[id]=keys.ToArray();row.Fields["Name"]="mutated after put";
                }
                foreach(var name in names)
                {
                    var expected=baseline.Where(p=>p.Value.Any(n=>StringComparer.OrdinalIgnoreCase.Equals(n,name))).Select(p=>p.Key).ToHashSet();
                    var actual=index.Find(new(kind,name)).ToList();
                    Check(!actual.Any(r=>r.Get("Name")=="mutated after put")&&actual.Count==actual.Select(r=>r.Identity!.RemoteId).Distinct().Count()&&expected.SetEquals(actual.Select(r=>r.Identity!.RemoteId)),$"Differential {kind} step {step}");
                }
            }
        }
    }
}