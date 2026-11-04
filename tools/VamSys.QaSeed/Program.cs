using System.Diagnostics;
using VamSys.Core;
using VamSys.Infrastructure;

if(args.Length >= 2 && args[0] is "--seed-data-ui")
    DataUiFixture.RequireEmptyDirectory(args[1]);
if(args.Length==2 && args[0] is "--verify-package" or "--verify-qa-package")
{PackageChecks.Verify(args[1],args[0]=="--verify-qa-package");return;}
if(args.Length==3 && args[0]=="--seed-data-ui"){DataUiFixture.Seed(args[1],int.Parse(args[2]));return;}

if(args.Length==2 && args[0]=="--export-diagnostic-replay"){await DiagnosticReplayExport.ExportDiagnostics(args[1]);return;}

// A seed for looking at the data page by hand, package checks and diagnostic replays; benchmarks are in tests/VamSys.Benchmarks.
Console.Error.WriteLine("Unknown or missing command. See tools/VamSys.QaSeed/README.md for the command-line entries.");
Environment.ExitCode = 2;
