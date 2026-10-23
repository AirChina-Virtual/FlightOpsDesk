using System.Diagnostics;
using System.Net;
using System.Text;
using VamSys.Core;
using VamSys.Infrastructure;

if(args.Length >= 2 && args[0] is "--seed-storage-ui" or "--seed-task-ui" or "--seed-data-ui" or "--seed-recovery-ui" or "--seed-ui")
    DataUiFixture.RequireEmptyDirectory(args[1]);
if(args.Length==2 && args[0] is "--verify-package" or "--verify-qa-package")
{PackageChecks.Verify(args[1],args[0]=="--verify-qa-package");return;}
if(args.Length==4 && args[0]=="--storage-child"){await StorageProcessScenarios.Child(args);return;}
if(args.Length==4 && args[0]=="--draft-child"){await StorageProcessScenarios.DraftChild(args);return;}
if(args.Length==3 && args[0]=="--seed-storage-ui")
{
    Directory.CreateDirectory(args[1]);var w=StorageScenarios.Fixture();using var db=StorageScenarios.Legacy(args[1],w);
    if(args[2]=="future")StorageScenarios.Sql(db,"PRAGMA user_version=5");
    Console.WriteLine("Seeded isolated storage UI");return;
}
if(args.Length==2 && args[0]=="--seed-task-ui")
{
    var w=StorageScenarios.Fixture();w.Jobs.Clear();w.Mode=RunMode.Offline;
    for(var n=0;n<1000;n++)w.Jobs.Add(new(){Created=DateTimeOffset.UnixEpoch.AddMinutes(n),Mode=RunMode.Online,Items=Enumerable.Range(0,20).Select(k=>new ChangeItem{State=ItemState.Succeeded}).ToList()});
    w.Jobs.Add(new(){Created=DateTimeOffset.UtcNow,Mode=RunMode.Online,Items=Enumerable.Range(0,20000).Select(k=>new ChangeItem{State=ItemState.Pending}).ToList()});
    var taskStore=new WorkspaceStore(args[1]);taskStore.Save(w);taskStore.SaveLanguage("en-US");Console.WriteLine("Seeded 1000 histories / 20000 history items + 20000 active items");return;
}
if(args.Length==2 && args[0]=="--assert-close-ui")
{
    var store=new WorkspaceStore(args[1]);var w=store.Load(store.List()[0].Id);
    if(w.Resources[ResourceKind.Fleets].Draft[0].Get("Name")!="accepted after failure")throw new Exception("Closing retry did not persist draft");
    Console.WriteLine("PASS restart reads the last accepted draft after failed close and retry");return;
}
if(args.Length==3 && args[0]=="--assert-autosave-ui")
{
    var store=new WorkspaceStore(args[1]);var w=store.Load(store.List()[0].Id);var name=w.Resources[ResourceKind.Fleets].Draft[0].Get("Name");
    if(name!=args[2])throw new Exception($"Autosaved draft lost after forced exit: '{name}'");
    Console.WriteLine("PASS restart reads the autosaved row after a forced exit");return;
}
if(args.Length==4 && args[0]=="--assert-history-ui")
{
    var store=new WorkspaceStore(args[1]);var w=store.Load(store.List().First(w=>w.Name!="QA other").Id);var d=w.Resources[ResourceKind.Fleets];
    if(d.Draft[0].Get("Name")!=args[2])throw new Exception("Current history value lost");
    d.UndoEdit();if(d.Draft[0].Get("Name")!=args[3])throw new Exception("Undo boundary lost");d.RedoEdit();if(d.Draft[0].Get("Name")!=args[2])throw new Exception("Redo boundary lost");
    Console.WriteLine("PASS restart restores complete last history step");return;
}
int passed = 0;
var suite = Stopwatch.StartNew(); var durations = new List<(string Name, TimeSpan Elapsed)>();
async Task Test(string name, Func<Task> test)
{
    var started = Stopwatch.GetTimestamp();
    await test(); var elapsed = Stopwatch.GetElapsedTime(started); durations.Add((name, elapsed));
    // Wall-clock waits belong in virtual time; slow scenarios are reported so they stay visible.
    Console.WriteLine("PASS " + name + (elapsed >= TimeSpan.FromSeconds(1) ? $" ({elapsed.TotalSeconds:0.0} s)" : "")); passed++;
}
void Summary(string label)
{
    Console.WriteLine($"  Duration: {suite.Elapsed.TotalSeconds:0.0} s; slowest: " + string.Join("; ", durations.OrderByDescending(d => d.Elapsed).Take(5).Select(d => $"{d.Name} {d.Elapsed.TotalSeconds:0.0} s")));
    Console.WriteLine($"{passed} {label}passed.");
}
Task Sync(Action a) { a(); return Task.CompletedTask; }
void Check(bool condition, string message = "Assertion failed") { if (!condition) throw new Exception(message); }
DataRow Row(params (string, string)[] fields) => new() { Fields = fields.ToDictionary(p => p.Item1, p => p.Item2) };
if(args.Length==1 && args[0]=="--v3"){await StorageV3Scenarios.Run(Test);await TaskViewModelScenarios.Run(Test);Console.WriteLine($"{passed} v3 scenarios passed.");return;}
if(args.Length==1 && args[0]=="--history"){await HistoryScenarios.Run(Test);Summary("history ");return;}
if(args.Length==2 && args[0]=="--benchmark-v3"){await StorageV3Scenarios.Benchmark(args[1]);return;}
if(args.Length==1 && args[0]=="--storage"){await StorageScenarios.Run(Test);await DraftSaveScenarios.Run(Test);Console.WriteLine($"{passed} storage scenarios passed.");return;}
if(args.Length==2 && args[0]=="--benchmark-storage"){await StorageScenarios.Benchmark(args[1]);return;}
if(args.Length==1 && args[0]=="--benchmark-journal-jobs"){await JournalBenchmark.JobsCost();return;}
if(args.Length==3 && args[0]=="--benchmark-journal"){await JournalBenchmark.Run(args[1],args[2]);return;}
if(args.Length==2 && args[0]=="--benchmark-core"){CorePerformanceBenchmark.Run(args[1]);return;}
if(args.Length==2 && args[0]=="--benchmark-review"){CorePerformanceBenchmark.RunReview(args[1]);return;}
if(args.Length==2 && args[0]=="--benchmark-core-load"){CorePerformanceBenchmark.RunLoad(args[1]);return;}
if(args.Length==1 && args[0]=="--ui-interactions"){await UiInteractionScenarios.Run(Test);Summary("UI interactions ");return;}
if(args.Length==1 && args[0]=="--task-submission"){await TaskSubmissionScenarios.Run(Test);Summary("task submission ");return;}
if(args.Length==1 && args[0]=="--verified-scope"){await VerifiedScopeScenarios.Run(Test);Summary("verified scope ");return;}
if(args.Length==1 && args[0]=="--unblock"){await UnblockScenarios.Run(Test);await UnblockScenarios.RunSoftDeletes(Test);await UnblockScenarios.RunRefresh(Test);Summary("unblock ");return;}
if(args.Length==1 && args[0]=="--data-table"){await DataTableScenarios.Run(Test);Summary("data table ");return;}
if(args.Length==3 && args[0]=="--seed-data-ui"){DataUiFixture.Seed(args[1],int.Parse(args[2]));return;}
var csv = new CsvAdapter(); var planner = new ChangePlanner();
if (args.Length == 2 && args[0] == "--seed-recovery-ui")
{
    var w=new Workspace { Name="QA recovery candidates",Mode=RunMode.Online,AirlineId="7" };
    var item=new ChangeItem { Resource=ResourceKind.Airports,Kind=ChangeKind.Create,State=ItemState.Unknown,RemoteId="102",
        After=Row(("ICAO/IATA","EGLL"),("Name","Hub")),Fields=new(){["ICAO/IATA"]=new(FieldIntent.Set,"EGLL"),["Name"]=new(FieldIntent.Set,"Hub")} };
    var pendingAirport=new ChangeItem { Resource=ResourceKind.Airports,Kind=ChangeKind.Create,State=ItemState.Pending,After=Row(("ICAO/IATA","EGLL")) };
    pendingAirport.SetMessage(Messages.Define("ApiAirportCreatePending"));
    var pendingFleet=new ChangeItem { Resource=ResourceKind.Fleets,Kind=ChangeKind.Create,State=ItemState.Pending,After=Row(("Name","Pending fleet")) };
    pendingFleet.SetMessage(Messages.Define("ApiIdentityPending"));
    w.Resources[ResourceKind.Airports].Draft.Add(item.After.Copy());w.Jobs.Add(new(){Mode=RunMode.Online,Items=[item,pendingAirport,pendingFleet]});
    new WorkspaceStore(args[1]).Save(w);Console.WriteLine("Seeded recovery UI without credentials");return;
}
if (args.Length == 2 && args[0] == "--seed-ui")
{
    var w = new Workspace { Name = "QA 20000 航线", Mode = RunMode.Demo };
    CsvAdapter.Import(ResourceKind.Airports,w.Resources[ResourceKind.Airports],[Row(("ICAO/IATA","ZBAA"),("Name","北京")),Row(("ICAO/IATA","ZSPD"),("Name","上海"))],true);
    CsvAdapter.Import(ResourceKind.Routes,w.Resources[ResourceKind.Routes],Enumerable.Range(0,20000).Select(i=>Row(("ID",i.ToString()),("Departure Airport (ICAO/IATA)","ZBAA"),("Arrival Airport (ICAO/IATA)","ZSPD"),("Type","jumpseat"),("Tags","性能测试"))).ToList(),true);
    new WorkspaceStore(args[1]).Save(w); Console.WriteLine("Seeded isolated UI workspace"); return;
}

if(args.Length==1 && args[0]=="--performance") {await PerformanceScenarios.Run(Test); await CandidateIndexScenarios.Run(Test);Console.WriteLine($"{passed} performance scenarios passed.");return;}
if(args.Length==1 && args[0]=="--indexes") {await CandidateIndexScenarios.Run(Test);Console.WriteLine($"{passed} index scenarios passed.");return;}
if(args.Length==1 && args[0]=="--live-compatibility"){await LiveCompatibilityScenarios.Run(Test);Summary("live compatibility ");return;}
if(args.Length==2 && args[0]=="--export-diagnostic-replay"){await LiveCompatibilityScenarios.ExportDiagnostics(args[1]);return;}
if(args.Length==2 && args[0]=="--benchmark-performance") {await PerformanceScenarios.Benchmark(args[1]);return;}

await Test("SQLite workspace isolation, persisted undo, and DPAPI", () => Sync(() =>
{
    var folder = Path.Combine(Path.GetTempPath(),"vamsys-test-"+Guid.NewGuid());
    var store = new WorkspaceStore(folder); var a = new Workspace { Name = "A" }; var b = new Workspace { Name = "B" };
    a.Resources[ResourceKind.Airports].Draft.Add(Row(("ICAO/IATA","ZBAA"))); a.Resources[ResourceKind.Airports].Edit([],()=>{}); store.Save(a); store.Save(b);
    Check(store.Load(a.Id).Resources[ResourceKind.Airports].Undo.Count == 1); Check(store.Load(b.Id).Resources[ResourceKind.Airports].Draft.Count == 0);
    if (OperatingSystem.IsWindows()) { store.SaveSecret(a.Id,"secret"); Check(store.LoadSecret(a.Id)=="secret"); Check(store.LoadSecret(b.Id)==null); }
}));
await Test("Batch success and resume do not duplicate writes", async () =>
{
    var w = new Workspace(); var before = Row(("ICAO/IATA","ZBAA"),("Name","old")); CsvAdapter.Import(ResourceKind.Airports,w.Resources[ResourceKind.Airports],[before],true);
    var fake = new CountingService(new DemoService(w)); w.Resources[ResourceKind.Airports].Draft[0].Fields["Name"]="new";
    var job = new BatchJob { Items = planner.Plan(ResourceKind.Airports,w.Resources[ResourceKind.Airports]) };
    var engine = new BatchExecutor(fake,fake); await engine.ExecuteAsync(job,()=>Task.CompletedTask,CancellationToken.None); await engine.ExecuteAsync(job,()=>Task.CompletedTask,CancellationToken.None);
    Check(fake.Writes==1); Check(job.Items[0].State==ItemState.Succeeded);
});
await Test("Timeout after create stays unknown and never blindly retries", async () =>
{
    var fake = new CountingService(new DemoService(new Workspace())) { TimeoutWrites = true };
    var item = new ChangeItem { Resource=ResourceKind.Fleets,Kind=ChangeKind.Create,After=Row(("Name","new")) }; var job=new BatchJob { Items=[item] };
    var engine = new BatchExecutor(fake,fake); await engine.ExecuteAsync(job,()=>Task.CompletedTask,CancellationToken.None); await engine.ExecuteAsync(job,()=>Task.CompletedTask,CancellationToken.None);
    Check(item.State==ItemState.Unknown); Check(fake.Writes==1);
});
await Test("Remote conflict blocks overwrite", async () =>
{
    var w = new Workspace(); CsvAdapter.Import(ResourceKind.Airports,w.Resources[ResourceKind.Airports],[Row(("ICAO/IATA","ZBAA"),("Name","remote"))],true);
    var fake=new CountingService(new DemoService(w)); var item=new ChangeItem { Resource=ResourceKind.Airports,Kind=ChangeKind.Update,Before=Row(("ICAO/IATA","ZBAA"),("Name","old")),After=Row(("ICAO/IATA","ZBAA"),("Name","new")) };
    await new BatchExecutor(fake,fake).ExecuteAsync(new BatchJob { Items=[item] },()=>Task.CompletedTask,CancellationToken.None); Check(item.State==ItemState.Conflict && fake.Writes==0);
});
await Test("Cross-resource creates resolve returned IDs before dependent writes", async () =>
{
    var w=new Workspace(); var fleet=Row(("Name","New fleet")); var plane=Row(("Name","New plane"),("Fleet ID","local:"+fleet.LocalId));
    w.Resources[ResourceKind.Fleets].Draft.Add(fleet); w.Resources[ResourceKind.Aircraft].Draft.Add(plane);
    var job=new BatchJob { Items=planner.PlanWorkspace(w) }; var fake=new CountingService(new DemoService(w));
    await new BatchExecutor(fake,fake).ExecuteAsync(job,()=>Task.CompletedTask,CancellationToken.None);
    Check(job.Items.All(i=>i.State==ItemState.Succeeded));
    Check(job.Items.Single(i=>i.Resource==ResourceKind.Aircraft).After.Get("Fleet ID")==job.Items.Single(i=>i.Resource==ResourceKind.Fleets).RemoteId);
});
await Test("Persist failure stops all writes", async () =>
{
    var fake=new CountingService(new DemoService(new Workspace())); var job=new BatchJob { Items=[new ChangeItem { Resource=ResourceKind.Fleets,Kind=ChangeKind.Create,After=Row(("Name","x")) }] };
    try { await new BatchExecutor(fake,fake).ExecuteAsync(job,()=>throw new IOException("disk"),CancellationToken.None); throw new Exception("Persist failure swallowed"); } catch(PersistenceException) { }
    Check(fake.Writes==0);
});
await Test("Cancellation stops pending writes, failed dependency skips child", async () =>
{
    var parent=new ChangeItem { Resource=ResourceKind.Fleets,State=ItemState.Failed }; var child=new ChangeItem { Resource=ResourceKind.Aircraft,Dependencies=[parent.Id] };
    var fake=new CountingService(new DemoService(new Workspace())); var job=new BatchJob { Items=[parent,child] };
    await new BatchExecutor(fake,fake).ExecuteAsync(job,()=>Task.CompletedTask,CancellationToken.None); Check(child.State==ItemState.Skipped && fake.Writes==0);
    var pending=new ChangeItem { Resource=ResourceKind.Fleets }; var cancelled=new BatchJob { Items=[pending] };
    await new BatchExecutor(fake,fake).ExecuteAsync(cancelled,()=>Task.CompletedTask,new CancellationToken(true)); Check(pending.State==ItemState.Pending && fake.Writes==0);
});
await Test("401/403 pauses batch without sending later rows", async () =>
{
    var fake=new AccessDeniedService(); var job=new BatchJob { Items=[new ChangeItem { Resource=ResourceKind.Fleets },new ChangeItem { Resource=ResourceKind.Fleets }] };
    await new BatchExecutor(fake,fake).ExecuteAsync(job,()=>Task.CompletedTask,CancellationToken.None); Check(fake.Calls==1 && job.Status.Contains("暂停"));
});
await Test("Recovered create with persisted remote ID verifies without repeat", async () =>
{
    var w=new Workspace(); var after=Row(("ID",""),("Name","new")); var item=new ChangeItem { Resource=ResourceKind.Fleets,Kind=ChangeKind.Create,After=after,State=ItemState.Running,RemoteId="123" };
    var remote=after.Copy(); remote.Fields["ID"]="123"; w.Resources[ResourceKind.Fleets].Snapshot.Add(remote);
    var fake=new CountingService(new DemoService(w)); await new BatchExecutor(fake,fake).ExecuteAsync(new BatchJob { Items=[item] },()=>Task.CompletedTask,CancellationToken.None);
    Check(item.State==ItemState.Succeeded && fake.Writes==0);
});
await Test("Partial failure permits independent work; repaired dependency resumes child only", async () =>
{
    var fake=new CountingService(new DemoService(new Workspace())) { FailOnce=true };
    var parent=new ChangeItem { Resource=ResourceKind.Fleets,Kind=ChangeKind.Create,After=Row(("Name","parent")) };
    var child=new ChangeItem { Resource=ResourceKind.Aircraft,Kind=ChangeKind.Create,After=Row(("Name","child")),Dependencies=[parent.Id] };
    var independent=new ChangeItem { Resource=ResourceKind.Routes,Kind=ChangeKind.Create,After=Row(("Name","independent")) };
    var job=new BatchJob { Items=[parent,child,independent] }; var engine=new BatchExecutor(fake,fake);
    await engine.ExecuteAsync(job,()=>Task.CompletedTask,CancellationToken.None);
    Check(parent.State==ItemState.Failed && child.State==ItemState.Skipped && independent.State==ItemState.Succeeded);
    parent.State=ItemState.Pending; await engine.ExecuteAsync(job,()=>Task.CompletedTask,CancellationToken.None);
    Check(job.Items.All(i=>i.State==ItemState.Succeeded) && fake.Writes==4);
});
await Test("Documented capabilities available; legacy unverified adapter rejects writes", async () =>
{
    Check(ApiContracts.Capabilities.All(c=>c.CanRead && c.CanWrite));
    foreach(var kind in Enum.GetValues<ResourceKind>())
    {
        try { await new UnverifiedOperationsAdapter().WriteAsync(new ChangeItem { Resource=kind,Kind=ChangeKind.Delete },CancellationToken.None); throw new Exception("Unverified write accepted"); }
        catch(NotSupportedException) { }
    }
});
await Test("Language preference persists in the store and an invalid value falls back to Chinese", () => Sync(() =>
{
    var dir = Path.Combine(Path.GetTempPath(), "vamsys-language-" + Guid.NewGuid()); var s = new WorkspaceStore(dir);
    Check(s.LoadLanguage() == "zh-CN"); s.SaveLanguage("en-US"); Check(new WorkspaceStore(dir).LoadLanguage() == "en-US");
    s.SaveLanguage("invalid"); Check(s.LoadLanguage() == "zh-CN");
}));
await Test("Language switch during execution does not resend writes or change task state", async () =>
{
    var job=new BatchJob{Items=[new ChangeItem{Resource=ResourceKind.Fleets,Kind=ChangeKind.Create,After=Row(("Name","fleet"))}]};
    var loc=new LocalizationService();var fake=new CountingService(new DemoService(new Workspace()));
    await new BatchExecutor(fake,fake).ExecuteAsync(job,()=>{ loc.SetLanguage(loc.Language=="en-US"?"zh-CN":"en-US"); return Task.CompletedTask; },CancellationToken.None);
    Check(fake.Writes==1 && job.StatusCode==JobStatus.Completed && job.Description?.Code=="Text_140197D868");
    var restored=Workspace.Deserialize(new Workspace{Jobs=[job]}.Serialize()).Jobs.Single(); loc.SetLanguage("en-US");
    Check(loc.Format(restored.Description!)=="Completed and verified" && loc.Format(restored.Items[0].Description!)=="Verified by read-back");
    await new BatchExecutor(fake,fake).ExecuteAsync(restored,()=>Task.CompletedTask,CancellationToken.None); Check(fake.Writes==1);
});
await ApiScenarios.Run(Test);
await LiveCompatibilityScenarios.Run(Test);
await AuditScenarios.Run(Test);
await ContractScenarios.Run(Test);
await ReauditScenarios.Run(Test);
await LifecycleScenarios.Run(Test);
await CredentialSaveScenarios.Run(Test);
await PerformanceScenarios.Run(Test); await CandidateIndexScenarios.Run(Test);
await StorageScenarios.Run(Test);
await StorageV3Scenarios.Run(Test);await TaskViewModelScenarios.Run(Test);
await DraftSaveScenarios.Run(Test);
await HistoryScenarios.Run(Test);
await DataTableScenarios.Run(Test);
await UiInteractionScenarios.Run(Test);
await TaskSubmissionScenarios.Run(Test);
await VerifiedScopeScenarios.Run(Test);
await UnblockScenarios.Run(Test);await UnblockScenarios.RunSoftDeletes(Test);await UnblockScenarios.RunRefresh(Test);
Summary("scenarios ");

sealed class CountingService(DemoService inner) : IResourceReader,IResourceWriter
{
    public int Writes; public bool TimeoutWrites; public bool FailOnce;
    public IAsyncEnumerable<DataRow> ReadAllAsync(ResourceKind k,CancellationToken ct)=>inner.ReadAllAsync(k,ct);
    public Task<DataRow?> FindAsync(ResourceKind k,string id,CancellationToken ct)=>inner.FindAsync(k,id,ct);
    public Task<string?> WriteAsync(ChangeItem c,CancellationToken ct) { Writes++; if(FailOnce) { FailOnce=false; throw new FormatException("invalid"); } if(TimeoutWrites) throw new TimeoutException(); return inner.WriteAsync(c,ct); }
}
sealed class FakeHttp : HttpMessageHandler
{
    public int Calls;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
    {
        Calls++;
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content=new StringContent(Calls==1 ? "{\"data\":[{\"id\":1}],\"meta\":{\"next_cursor_url\":\"https://example.test/routes?cursor=2\"}}" : "{\"data\":[{\"id\":2}],\"meta\":{\"next_cursor_url\":null}}") });
    }
}
sealed class AccessDeniedService : IResourceReader,IResourceWriter
{
    public int Calls;
    public async IAsyncEnumerable<DataRow> ReadAllAsync(ResourceKind k,[System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct) { await Task.CompletedTask; yield break; }
    public Task<DataRow?> FindAsync(ResourceKind k,string id,CancellationToken ct)=>throw new ApiAccessException("denied");
    public Task<string?> WriteAsync(ChangeItem c,CancellationToken ct) { Calls++; throw new ApiAccessException("denied"); }
}
