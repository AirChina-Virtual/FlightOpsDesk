using System.Diagnostics;
using Microsoft.Data.Sqlite;
using VamSys.Infrastructure;
using VamSys.TestSupport;
using static VamSys.IntegrationTests.Storage.LegacyDatabases;

namespace VamSys.IntegrationTests.Storage.ProcessTermination;

/// <summary>
/// The real v3 program (kept under artifacts/step5-v3-baseline, not in the repository) must refuse a v4 database and leave it
/// byte for byte as it was. It can only be started as a process, so it needs that folder; without it the test is skipped.
/// </summary>
[Trait("Category", "Slow")]
public class V3BinaryRefusalTests
{
    const string BaselineDll = "artifacts/step5-v3-baseline/tests/VamSys.Tests.dll";

    static string? FindBaseline()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "VamSys.slnx")))
            {
                var candidate = Path.Combine(dir.FullName, BaselineDll);
                return File.Exists(candidate) ? candidate : null;
            }
        return null;
    }

    [Fact]
    public async Task The_actual_v3_binary_refuses_a_v4_database_without_changing_it()
    {
        var baseline = FindBaseline();
        Assert.SkipUnless(baseline != null, "Needs the real v3 program in " + BaselineDll + " (not part of the repository)");
        using var dir = TempDataDirectory.Create("vamsys-v3-");
        var store = new WorkspaceStore(dir.Path);
        store.Save(Fixture());
        using (var c = Open(dir.Path))
            Sql(c, "PRAGMA wal_checkpoint(TRUNCATE)");
        // Only this database's pool: other tests may be using SQLite in parallel.
        using (var pooled = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(dir.Path, "workspaces.db") }.ToString()))
            SqliteConnection.ClearPool(pooled);
        var path = Path.Combine(dir.Path, "workspaces.db");
        var before = File.ReadAllBytes(path);

        var info = new ProcessStartInfo(ChildProcess.DotnetHost()) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var arg in new[] { baseline!, "--assert-close-ui", dir.Path })
            info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        { await process.WaitForExitAsync(timeout.Token); }
        finally
        {
            if (!process.HasExited)
            { process.Kill(true); await process.WaitForExitAsync(); }
        }

        var log = await error + await output;
        Assert.True(process.ExitCode != 0 && log.Contains("WorkspaceStore.Initialize(String directory)") && log.Contains("WorkspaceStore.Schema.cs:line 15"), log);
        Assert.True(before.SequenceEqual(File.ReadAllBytes(path)), "old reader changed v4 database");
    }
}
