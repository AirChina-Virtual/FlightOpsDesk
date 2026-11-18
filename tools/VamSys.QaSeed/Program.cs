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
if(args.Length>=2 && args[0]=="--write-evidence")
{
    // --write-evidence <report.json> [--dry-run] [--max-requests N]
    var dryRun = false;
    var max = WriteEvidence.DefaultMaxRequests;
    for (var i = 2; i < args.Length; i++)
        if (args[i] == "--dry-run") dryRun = true;
        else if (args[i] == "--max-requests" && i + 1 < args.Length && int.TryParse(args[++i], out var n) && n > 0) max = n;
        else { Console.Error.WriteLine("Unknown option " + args[i] + ". Usage: --write-evidence <report.json> [--dry-run] [--max-requests N]"); Environment.ExitCode = 2; return; }
    using var stop = new CancellationTokenSource();
    using var abandon = new CancellationTokenSource();
    // First Ctrl+C: stop after the request in flight and clean up. Second: give up the cleanup as well.
    ConsoleCancelEventHandler onCancel = (_, e) =>
    {
        e.Cancel = true;
        if (!stop.IsCancellationRequested)
        { stop.Cancel(); Console.Error.WriteLine("收到 Ctrl+C：当前请求完成后停止，然后清理本轮新建的记录。再按一次 Ctrl+C 放弃清理。"); }
        else
        { abandon.Cancel(); Console.Error.WriteLine("放弃清理：没有删除的记录会列在报告和控制台里。"); }
    };
    Console.CancelKeyPress += onCancel;
    try { Environment.ExitCode = await WriteEvidence.RunAsync(args[1], new(dryRun, max), stop: stop.Token, abandonCleanup: abandon.Token); }
    catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException or IOException)
    { Console.Error.WriteLine("Write evidence could not run: " + e.Message); Environment.ExitCode = WriteEvidence.NotStarted; }
    finally { Console.CancelKeyPress -= onCancel; }
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
