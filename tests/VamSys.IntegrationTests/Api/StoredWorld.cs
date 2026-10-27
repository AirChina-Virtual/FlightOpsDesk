using System.Text.Json;
using VamSys.Core;
using VamSys.Infrastructure;
using VamSys.IntegrationTests.Golden.Requests;
using VamSys.TestSupport;
using static VamSys.IntegrationTests.Api.PipelineHarness;

namespace VamSys.IntegrationTests.Api;

/// <summary>
/// Disposing a data directory clears the process-wide SQLite connection pool, which would break any other test using a pooled
/// connection at that moment; the tests that own a database run alone.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class StoredWorldCollection
{
    public const string Name = "Stored worlds";
}

/// <summary>
/// A <see cref="GoldenWorld"/> whose checkpoints go into a real database in an isolated directory, so a test can look at what
/// is on disk after every step. Used by the recovery tests: nothing here is scripted except the server's answers.
/// </summary>
internal sealed class StoredWorld : IDisposable
{
    readonly TempDataDirectory dir = TempDataDirectory.Create("vamsys-recovery-");
    public readonly GoldenWorld World = new();
    public readonly WorkspaceStore Store;

    public StoredWorld() => Store = new WorkspaceStore(dir.Path);

    public Workspace W => World.Workspace;
    public FakeRemote Remote => World.Remote;
    public IBatchCheckpointWriter Writer => Store.CheckpointWriter(W);
    public Workspace Disk => Store.Load(W.Id);

    /// <summary>Plan everything that was edited into one online task and save it as Pending.</summary>
    public BatchJob Submit()
    {
        var job = NewJob(World);
        Store.Save(W);
        return job;
    }

    public Task Run(BatchJob job, IBatchCheckpointWriter? writer = null, OperationsAdapter? api = null)
        => new OperationsBatchExecutor(api ?? World.Api, W).ExecuteAsync(job, writer ?? Writer, Ct);

    /// <summary>An adapter over the same fake server for another in-memory copy of the workspace (for example one loaded from disk).</summary>
    public OperationsAdapter Adapter(Workspace workspace, Func<TimeProvider, TokenProvider>? tokens = null)
    {
        var requests = InstantTime.Requests();
        var provider = tokens?.Invoke(requests.Clock) ?? new TokenProvider(_ => Task.FromResult(new AccessToken("recovery-token", requests.Clock.GetUtcNow().AddHours(1))), requests.Clock);
        var api = new OperationsAdapter(new OperationsTransport(Remote.Server.CreateClient(), OperationsAdapter.BaseUri, "golden-va", provider, requests), workspace);
        // Reading any airport names the airline of the session, which routes (they carry no airline_id) rely on; the session did that when it connected.
        using var airport = JsonDocument.Parse(Remote.Rows["airports/101"].ToJsonString());
        api.FromJson(ResourceKind.Airports, airport.RootElement);
        return api;
    }

    /// <summary>Requests that are not reads, in arrival order.</summary>
    public string[] Writes() => Remote.Server.Requests.Where(r => r.Method != "GET").Select(r => r.Method + " " + r.Path[FakeOperationsServer.OperationsPrefix.Length..]).ToArray();

    public static string Json(object? value) => JsonSerializer.Serialize(value);

    /// <summary>
    /// A route DELETE the server answers 204 without removing the record: an accepted write (it has a receipt) whose record
    /// can still be read, so it is Unknown. The scripted answer is hit exactly once, which this asserts.
    /// </summary>
    public static async Task<StoredWorld> AcceptedSoftDelete()
    {
        var s = new StoredWorld();
        var answered = 0;
        s.Remote.Intercept = r => r.Method == "DELETE" ? (++answered > 0 ? new FakeResponse(System.Net.HttpStatusCode.NoContent, null) : null) : null;
        s.World.MarkDelete(ResourceKind.Routes, "10");
        var job = s.Submit();
        await s.Run(job);
        s.Remote.Intercept = null;
        Assert.Equal(1, answered);
        var item = job.Items.Single();
        Assert.Equal(ItemState.Unknown, item.State);
        Assert.True(item.WriteAccepted);
        Assert.Equal("StillReadable", item.DeletionVerification!.Outcome);
        return s;
    }
    public void Dispose() => dir.Dispose();
}

/// <summary>Passes checkpoints to the real writer except those the test wants to fail; Hits proves the failure was reached.</summary>
internal sealed class FailingWriter(IBatchCheckpointWriter inner, Func<BatchCheckpoint, bool> fail) : IBatchCheckpointWriter
{
    public int Hits { get; private set; }

    public Task WriteAsync(BatchCheckpoint checkpoint, CancellationToken ct)
    {
        if (!fail(checkpoint))
            return inner.WriteAsync(checkpoint, ct);
        Hits++;
        throw new IOException("Injected checkpoint failure");
    }
}

internal static class Expect
{
    /// <summary>The action is refused with the given message code (not merely with any error).</summary>
    public static async Task BlockedAsync(Func<Task> action, string code)
    {
        var error = await Assert.ThrowsAnyAsync<InvalidOperationException>(action);
        Assert.Equal(code, MessageErrors.Describe(error).Code);
    }
}
