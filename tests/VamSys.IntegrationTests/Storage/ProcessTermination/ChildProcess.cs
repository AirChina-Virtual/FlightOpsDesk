using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using VamSys.ProcessChild;

namespace VamSys.IntegrationTests.Storage.ProcessTermination;

/// <summary>
/// The parent side of the real-termination tests: starts VamSys.ProcessChild, plays the remote server over a named pipe and
/// ends the child (Process.Kill) at the first message that matches the requested boundary.
/// </summary>
internal static class ChildProcess
{
    /// <summary>A remote that remembers what it was asked to do: POST creates fleet 10, PUT completes it, GET reads it back.</summary>
    internal sealed class Remote
    {
        public int Posts, Puts;
        public bool Created, Completed;
        const string Existing = """{"id":4,"airline_id":7,"name":"Existing","code":"B738","type":"pax"}""";
        public string Record => """{"id":10,"airline_id":7,"name":"New","code":"B738","type":"pax","max_pax":""" + (Completed ? "180" : "0") + "}";

        public Wire Respond(Wire request)
        {
            if (request.Method == "POST") { Posts++; Created = true; return new("response", Body: Record, Code: 201); }
            if (request.Method == "PUT") { Puts++; Completed = true; return new("response", Body: "{\"data\":" + Record + "}"); }
            Assert.True(request.Method == "GET", "Unexpected write method");
            if (request.Path!.Split('?')[0].EndsWith("/10", StringComparison.Ordinal))
                return Created ? new("response", Body: "{\"data\":" + Record + "}") : new("response", Body: "{}", Code: 404);
            return new("response", Body: "{\"data\":[" + Existing + (Created ? "," + Record : "") + "],\"meta\":{\"next_cursor_url\":null}}");
        }
    }

    /// <summary>The child's path: copied next to the test assembly by the build (see VamSys.IntegrationTests.csproj).</summary>
    static string ChildDll => Path.Combine(AppContext.BaseDirectory, "ProcessChild", "VamSys.ProcessChild.dll");

    // Not Environment.ProcessPath: the test host is an apphost. DOTNET_HOST_PATH is set by the dotnet CLI; otherwise the dotnet
    // that hosts this process sits three levels above the runtime directory (<root>/shared/Microsoft.NETCore.App/<version>).
    internal static string DotnetHost()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrEmpty(fromEnvironment) && File.Exists(fromEnvironment))
            return fromEnvironment;
        var runtime = RuntimeEnvironment.GetRuntimeDirectory().TrimEnd(Path.DirectorySeparatorChar);
        var root = Path.GetFullPath(Path.Combine(runtime, "..", "..", ".."));
        var host = Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        Assert.True(File.Exists(host), "dotnet host not found at " + host);
        return host;
    }

    /// <summary>Runs the child to completion, or ends it at the first message for which <paramref name="killAt"/> is true.</summary>
    internal static async Task Launch(string directory, Guid id, Remote remote, Func<Wire, bool>? killAt = null, string mode = "storage")
    {
        var name = "vamsys-test-" + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var psi = new ProcessStartInfo(DotnetHost()) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { ChildDll, mode, directory, name, id.ToString() })
            psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        bool killed = false, done = false;
        try
        {
            await pipe.WaitForConnectionAsync(timeout.Token);
            using var reader = new StreamReader(pipe);
            using var writer = new StreamWriter(pipe) { AutoFlush = true };
            while (await reader.ReadLineAsync(timeout.Token) is string line)
            {
                var message = JsonSerializer.Deserialize<Wire>(line)!;
                if (killAt?.Invoke(message) == true)
                { process.Kill(true); await process.WaitForExitAsync(timeout.Token); killed = true; break; }
                if (message.Type == "done") { done = true; break; }
                var reply = message.Type == "request" ? remote.Respond(message) : new Wire("ack");
                if (message.Type == "request" && killAt?.Invoke(new Wire("accepted", Method: message.Method)) == true)
                { process.Kill(true); await process.WaitForExitAsync(timeout.Token); killed = true; break; }
                await writer.WriteLineAsync(JsonSerializer.Serialize(reply));
            }
            await process.WaitForExitAsync(timeout.Token);
            Assert.True(killAt != null ? killed : done && process.ExitCode == 0, "Child failed: " + await stdout + await stderr);
            if (killed)
                await WaitUntilReleasedAsync(directory, timeout.Token);
        }
        finally
        {
            if (!process.HasExited)
            { process.Kill(true); await process.WaitForExitAsync(); }
        }
    }

    /// <summary>
    /// After a kill the process has exited, but on Windows its handles on the database files (or a scanner's) can outlive that
    /// for a moment; SQLite then reports "disk I/O error" while recovering the journal (seen once on a GitHub runner,
    /// 2026-11-09). A restarted program cannot start before the old one is gone either, so the checks wait until every
    /// database file can be opened exclusively. A file still held after the timeout fails the test by name.
    /// </summary>
    static async Task WaitUntilReleasedAsync(string directory, CancellationToken ct)
    {
        // The test's own setup may have left pooled connections (WorkspaceStore uses the default pooled string) on this one
        // database; they are not the child's, so close them first. Never ClearAllPools: other tests run in parallel.
        using (var pooled = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "workspaces.db") }.ToString()))
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(pooled);
        foreach (var file in Directory.GetFiles(directory, "workspaces.db*"))
            while (true)
            {
                try
                {
                    using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                    break;
                }
                catch (FileNotFoundException) { break; }
                catch (IOException) when (!ct.IsCancellationRequested)
                {
                    try { await Task.Delay(50, ct); }
                    catch (OperationCanceledException) { Assert.Fail("Still held after the child was killed: " + file); }
                }
            }
    }

    /// <summary>
    /// Boundaries: "Task:BeforeCommit", "Step:AfterCommit|POST|ResponseReceived" (barrier, method, step state), "PUT:Accepted"
    /// (the fake remote has accepted the PUT but the child has not heard back), "Rebase:*", "Draft:*", "Migration:*".
    /// </summary>
    internal static bool Match(Wire wire, string boundary)
    {
        if (boundary == "PUT:Accepted")
            return wire.Type == "accepted" && wire.Method == "PUT";
        if (wire.Type != "barrier")
            return false;
        if (boundary.StartsWith("Migration", StringComparison.Ordinal) || boundary.StartsWith("Rebase", StringComparison.Ordinal) || boundary.StartsWith("Draft", StringComparison.Ordinal))
            return wire.Point == boundary;
        using var body = JsonDocument.Parse(wire.Body!);
        var root = body.RootElement;
        if (boundary.StartsWith("Task", StringComparison.Ordinal))
            return wire.Point == boundary && root.GetProperty("state").GetString() == "Running";
        if (wire.Point != boundary.Split('|')[0])
            return false;
        var steps = root.GetProperty("steps");
        var method = boundary.Split('|')[1];
        var state = boundary.Split('|')[2];
        return steps.EnumerateArray().Any(s => s.GetProperty("method").GetString() == method && s.GetProperty("state").GetString() == state);
    }
}
