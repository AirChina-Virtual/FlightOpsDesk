using System.Diagnostics;
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
DataRow Row(params (string, string)[] fields) => new() { Fields = fields.ToDictionary(p => p.Item1, p => p.Item2) };
if(args.Length==2 && args[0]=="--benchmark-v3"){await StorageV3Scenarios.Benchmark(args[1]);return;}
if(args.Length==1 && args[0]=="--storage"){await StorageScenarios.Run(Test);Console.WriteLine($"{passed} storage scenarios passed.");return;}
if(args.Length==2 && args[0]=="--benchmark-storage"){await StorageScenarios.Benchmark(args[1]);return;}
if(args.Length==1 && args[0]=="--benchmark-journal-jobs"){await JournalBenchmark.JobsCost();return;}
if(args.Length==3 && args[0]=="--benchmark-journal"){await JournalBenchmark.Run(args[1],args[2]);return;}
if(args.Length==2 && args[0]=="--benchmark-core"){CorePerformanceBenchmark.Run(args[1]);return;}
if(args.Length==2 && args[0]=="--benchmark-review"){CorePerformanceBenchmark.RunReview(args[1]);return;}
if(args.Length==2 && args[0]=="--benchmark-core-load"){CorePerformanceBenchmark.RunLoad(args[1]);return;}
if(args.Length==3 && args[0]=="--seed-data-ui"){DataUiFixture.Seed(args[1],int.Parse(args[2]));return;}
if (args.Length == 2 && args[0] == "--seed-recovery-ui")
{
    var w=new Workspace { Name="QA recovery candidates",Mode=RunMode.Online,AirlineId="7" };
    var item=new ChangeItem { Resource=ResourceKind.Airports,Kind=ChangeKind.Create,State=ItemState.Unknown,RemoteId="102",
        After=Row(("ICAO/IATA","EGLL"),("Name","Hub")),Fields=new(){["ICAO/IATA"]=new(FieldIntent.Set,"EGLL"),["Name"]=new(FieldIntent.Set,"Hub")} };
    var pendingAirport=new ChangeItem { Resource=ResourceKind.Airports,Kind=ChangeKind.Create,State=ItemState.Pending,After=Row(("ICAO/IATA","EGLL")) };
    pendingAirport.SetMessage(Messages.Define("ApiAirportCreatePending"));
    var pendingFleet=new ChangeItem { Resource=ResourceKind.Fleets,Kind=ChangeKind.Create,State=ItemState.Pending,After=Row(("Name","Pending fleet")) };
    pendingFleet.SetMessage(Messages.Define("ApiIdentityPending"));
    w.Resources[ResourceKind.Airports].Draft.Add(item.After.Dup());w.Jobs.Add(new(){Mode=RunMode.Online,Items=[item,pendingAirport,pendingFleet]});
    new WorkspaceStore(args[1]).Save(w);Console.WriteLine("Seeded recovery UI without credentials");return;
}
if (args.Length == 2 && args[0] == "--seed-ui")
{
    var w = new Workspace { Name = "QA 20000 航线", Mode = RunMode.Demo };
    CsvAdapter.Import(ResourceKind.Airports,w.Resources[ResourceKind.Airports],[Row(("ICAO/IATA","ZBAA"),("Name","北京")),Row(("ICAO/IATA","ZSPD"),("Name","上海"))],true);
    CsvAdapter.Import(ResourceKind.Routes,w.Resources[ResourceKind.Routes],Enumerable.Range(0,20000).Select(i=>Row(("ID",i.ToString()),("Departure Airport (ICAO/IATA)","ZBAA"),("Arrival Airport (ICAO/IATA)","ZSPD"),("Type","jumpseat"),("Tags","性能测试"))).ToList(),true);
    new WorkspaceStore(args[1]).Save(w); Console.WriteLine("Seeded isolated UI workspace"); return;
}

if(args.Length==2 && args[0]=="--export-diagnostic-replay"){await DiagnosticReplayExport.ExportDiagnostics(args[1]);return;}
if(args.Length==2 && args[0]=="--benchmark-performance") {await PerformanceScenarios.Benchmark(args[1]);return;}

await StorageScenarios.Run(Test);
Summary("scenarios ");

