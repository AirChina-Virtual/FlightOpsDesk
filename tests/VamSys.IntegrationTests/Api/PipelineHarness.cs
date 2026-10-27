using VamSys.Core;
using VamSys.Infrastructure;
using VamSys.IntegrationTests.Golden.Requests;
using VamSys.TestSupport;

namespace VamSys.IntegrationTests.Api;

/// <summary>Helpers for the execution pipeline tests: run a job over a <see cref="GoldenWorld"/> and look at what happened.</summary>
internal static class PipelineHarness
{
    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Every request the fake server saw, as "METHOD path" relative to the operations base (no query).</summary>
    public static string[] Calls(GoldenWorld w) => w.Remote.Server.Requests.Select(r => r.Method + " " + r.Path[FakeOperationsServer.OperationsPrefix.Length..]).ToArray();

    public static int Count(GoldenWorld w, string method) => w.Remote.Server.Requests.Count(r => r.Method == method);

    public static BatchJob NewJob(GoldenWorld w)
    {
        var job = new BatchJob { Mode = RunMode.Online, Items = new ChangePlanner().PlanWorkspace(w.Workspace) };
        w.Workspace.Jobs.Add(job);
        return job;
    }

    public static Task Execute(GoldenWorld w, BatchJob job, Func<Task>? persist = null, CancellationToken? ct = null)
        => new OperationsBatchExecutor(w.Api, w.Workspace).ExecuteAsync(job, persist ?? (() => Task.CompletedTask), ct ?? Ct);

    public static Task Execute(GoldenWorld w, BatchJob job, IBatchCheckpointWriter writer, Action? committed = null, Action<BatchProgress>? progress = null, CancellationToken? ct = null)
        => new OperationsBatchExecutor(w.Api, w.Workspace).ExecuteAsync(job, writer, ct ?? Ct, committed, progress);

    public static FakeResponse Status(int status) => FakeResponse.WithStatus(status, "{}");
    public static FakeResponse RateLimited() => new((System.Net.HttpStatusCode)429, "{}", new Dictionary<string, string> { ["Retry-After"] = "30" });

    /// <summary>The fleet row as the planner would see it in the draft.</summary>
    public static DataRow Draft(GoldenWorld w, ResourceKind kind, string remoteId) => w.Workspace.Resources[kind].Draft.Single(r => r.Identity?.RemoteId == remoteId);

    public static string Code(ChangeItem item) => item.Description?.Code ?? "";

    /// <summary>The program started again (C11): the workspace comes back from its saved form and a new adapter, without memory of the run, works on it.</summary>
    public static (Workspace Workspace, OperationsAdapter Api) Restarted(GoldenWorld w, string saved)
    {
        var restored = Workspace.Deserialize(saved);
        var requests = InstantTime.Requests();
        var tokens = new TokenProvider(_ => Task.FromResult(new AccessToken("golden-token", requests.Clock.GetUtcNow().AddHours(1))), requests.Clock);
        return (restored, new OperationsAdapter(new OperationsTransport(w.Remote.Server.CreateClient(), OperationsAdapter.BaseUri, "golden-va", tokens, requests), restored));
    }

    /// <summary>A writer that records each checkpoint with the number of requests the server had seen when it was written.</summary>
    public sealed class RecordingWriter(GoldenWorld world) : IBatchCheckpointWriter
    {
        public readonly List<(CheckpointKind Kind, int Requests, ItemState? State, BatchCheckpoint Checkpoint)> Written = [];
        public Func<BatchCheckpoint, Exception?>? Failure { get; set; }

        public Task WriteAsync(BatchCheckpoint checkpoint, CancellationToken ct)
        {
            if (Failure?.Invoke(checkpoint) is { } error)
                return Task.FromException(error);
            Written.Add((checkpoint.Kind, world.Remote.Server.Requests.Count, checkpoint.Item?.State, checkpoint));
            return Task.CompletedTask;
        }
    }
}
