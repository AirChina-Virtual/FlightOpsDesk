using System.IO.Pipes;
using System.Net;
using System.Text.Json;
using VamSys.Core;
using VamSys.Infrastructure;
using VamSys.ProcessChild;
using VamSys.TestSupport;

// Usage: VamSys.ProcessChild <storage|draft> <data directory> <pipe name> <workspace id>
// Only storage barriers pause the child (the parent ends it there); nothing else is instrumented.
if (args.Length != 4 || args[0] is not ("storage" or "draft"))
{
    Console.Error.WriteLine("Usage: VamSys.ProcessChild <storage|draft> <directory> <pipe> <workspace id>");
    return 2;
}
using var pipe = new NamedPipeClientStream(".", args[2], PipeDirection.InOut, PipeOptions.Asynchronous);
await pipe.ConnectAsync(15000);
using var reader = new StreamReader(pipe);
using var writer = new StreamWriter(pipe) { AutoFlush = true };
if (args[0] == "storage")
    await RunTask(args, reader, writer);
else
    await RunDraftSave(args, reader, writer);
return 0;

// Executes the workspace's first job against the parent's fake remote, reporting each storage barrier with the item's state.
static async Task RunTask(string[] args, StreamReader reader, StreamWriter writer)
{
    Workspace? w = null;
    void Barrier(string point)
    {
        var i = w?.Jobs[0].Items[0];
        var detail = i == null ? "migration" : JsonSerializer.Serialize(new { state = i.State.ToString(), i.RemoteId, i.CreateCompleted, steps = i.ApiSteps.Select(s => new { method = s.Method, state = s.State.ToString(), s.Path }).ToArray() });
        writer.WriteLine(JsonSerializer.Serialize(new Wire("barrier", Point: point, Body: detail)));
        if (reader.ReadLine() == null)
            throw new IOException("Parent closed pipe");
    }
    using var lease = new DataDirectoryLease(args[1]);
    var store = new WorkspaceStore(args[1], Barrier);
    w = store.Load(Guid.Parse(args[3]));
    var clock = new TestTime();
    using var client = new HttpClient(new PipeHttp(reader, writer));
    var api = new OperationsAdapter(new OperationsTransport(client, OperationsAdapter.BaseUri, "process-test", new TokenProvider(_ => Task.FromResult(new AccessToken("test", clock.GetUtcNow().AddHours(1))), clock), new(clock)), w);
    await clock.Drive(new OperationsBatchExecutor(api, w).ExecuteAsync(w.Jobs[0], store.CheckpointWriter(w), default));
    await writer.WriteLineAsync(JsonSerializer.Serialize(new Wire("done")));
}

// Edits one draft cell (adding an undo step) and saves only that row, pausing at each commit barrier.
// Also the way to open a data directory that needs a migration: the barrier fires inside the store constructor.
static async Task RunDraftSave(string[] args, StreamReader reader, StreamWriter writer)
{
    void Barrier(string point)
    {
        writer.WriteLine(JsonSerializer.Serialize(new Wire("barrier", Point: point, Body: "{}")));
        if (reader.ReadLine() == null)
            throw new IOException("Parent closed pipe");
    }
    using var lease = new DataDirectoryLease(args[1]);
    var store = new WorkspaceStore(args[1], Barrier);
    var w = store.Load(Guid.Parse(args[3]));
    var data = w.Resources[ResourceKind.Fleets];
    var row = data.Draft[0];
    data.Edit([row.LocalId], () => data.ReplaceDraftRows([row.With("Name", "Edited in child")]));
    var edits = new DraftEdits();
    edits.Cell(w, ResourceKind.Fleets, row.LocalId);
    if (!await store.TrySaveDraftRowsAsync(w, edits.Changes))
        throw new InvalidOperationException("Row save fell back");
    await writer.WriteLineAsync(JsonSerializer.Serialize(new Wire("done")));
}

sealed class PipeHttp(StreamReader reader, StreamWriter writer) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        await writer.WriteLineAsync(JsonSerializer.Serialize(new Wire("request", Method: request.Method.Method, Path: request.RequestUri!.AbsolutePath, Body: request.Content == null ? null : await request.Content.ReadAsStringAsync(ct))));
        var reply = JsonSerializer.Deserialize<Wire>((await reader.ReadLineAsync(ct))!)!;
        return new((HttpStatusCode)reply.Code) { Content = new StringContent(reply.Body ?? "{}") };
    }
}
