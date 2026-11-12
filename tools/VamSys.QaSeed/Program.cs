using System.Diagnostics;
using VamSys.Core;
using VamSys.Infrastructure;

if(args.Length >= 2 && args[0] is "--seed-data-ui")
    DataUiFixture.RequireEmptyDirectory(args[1]);
if(args.Length==2 && args[0] is "--verify-package" or "--verify-qa-package")
{PackageChecks.Verify(args[1],args[0]=="--verify-qa-package");return;}
if(args.Length==3 && args[0]=="--seed-data-ui"){DataUiFixture.Seed(args[1],int.Parse(args[2]));return;}

if(args.Length==2 && args[0]=="--verify-read-only")
{
    try { Environment.ExitCode = await ReadOnlyVerification.RunAsync(args[1]); }
    catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException or IOException)
    { Console.Error.WriteLine("Read-only check stopped: " + e.Message); Environment.ExitCode = 2; }
    return;
}
if(args.Length==2 && args[0]=="--check-contract")
{
    try { Environment.ExitCode = await ContractDrift.RunAsync(args[1]); }
    catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException or IOException)
    { Console.Error.WriteLine("Contract check could not run: " + e.Message); Environment.ExitCode = 2; }
    return;
}
if(args.Length==2 && args[0]=="--export-diagnostic-replay"){await DiagnosticReplayExport.ExportDiagnostics(args[1]);return;}

// A seed for looking at the data page by hand, package checks, diagnostic replays and the contract drift check; benchmarks are in tests/VamSys.Benchmarks.
Console.Error.WriteLine("Unknown or missing command. See tools/VamSys.QaSeed/README.md for the command-line entries.");
Environment.ExitCode = 2;
