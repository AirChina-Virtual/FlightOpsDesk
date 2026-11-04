using System.Diagnostics;
using VamSys.Core;
using VamSys.Infrastructure;

if(args.Length >= 2 && args[0] is "--seed-data-ui" or "--seed-ui")
    DataUiFixture.RequireEmptyDirectory(args[1]);
if(args.Length==2 && args[0] is "--verify-package" or "--verify-qa-package")
{PackageChecks.Verify(args[1],args[0]=="--verify-qa-package");return;}
DataRow Row(params (string, string)[] fields) => new() { Fields = fields.ToDictionary(p => p.Item1, p => p.Item2) };
if(args.Length==3 && args[0]=="--seed-data-ui"){DataUiFixture.Seed(args[1],int.Parse(args[2]));return;}
if (args.Length == 2 && args[0] == "--seed-ui")
{
    var w = new Workspace { Name = "QA 20000 航线", Mode = RunMode.Demo };
    CsvAdapter.Import(ResourceKind.Airports,w.Resources[ResourceKind.Airports],[Row(("ICAO/IATA","ZBAA"),("Name","北京")),Row(("ICAO/IATA","ZSPD"),("Name","上海"))],true);
    CsvAdapter.Import(ResourceKind.Routes,w.Resources[ResourceKind.Routes],Enumerable.Range(0,20000).Select(i=>Row(("ID",i.ToString()),("Departure Airport (ICAO/IATA)","ZBAA"),("Arrival Airport (ICAO/IATA)","ZSPD"),("Type","jumpseat"),("Tags","性能测试"))).ToList(),true);
    new WorkspaceStore(args[1]).Save(w); Console.WriteLine("Seeded isolated UI workspace"); return;
}

if(args.Length==2 && args[0]=="--export-diagnostic-replay"){await DiagnosticReplayExport.ExportDiagnostics(args[1]);return;}

// Seeds, package checks and diagnostic replays for the remaining window scripts and the packaging script; benchmarks are in tests/VamSys.Benchmarks.
Console.Error.WriteLine("Unknown or missing command. See tools/VamSys.QaSeed/README.md for the command-line entries.");
Environment.ExitCode = 2;
