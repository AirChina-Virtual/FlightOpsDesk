using VamSys.Core;

namespace VamSys.UnitTests.Application;

// A scripted server connection: every call is recorded, results are set by the test, and any call can throw.
public sealed class FakeOnlineSession : IOnlineSession
{
    public readonly List<string> Calls = [];
    public string? AirlineId { get; set; }
    public bool Identifies = true;
    public Exception? IdentifyError, ParametersError, RefreshError, ExecuteError, VerifyError, NotAppliedError, ManualError;
    public IReadOnlyList<AirlineParameter> Parameters = [];
    public Func<Workspace, Task>? Stage;
    public Func<BatchJob, IBatchCheckpointWriter, Task>? OnExecute;
    public bool? NotApplied = true;
    public NoReceiptInspection? NoReceipt;
    public ServerValueInspection? ServerValue;
    public readonly List<(ResourceKind Kind, int Count)> Progress = [];
    public Func<ChangeItem, IReadOnlyList<string>> Plan = _ => ["POST /thing"];
    public Exception? PreviewError;

    public Task<bool> IdentifyAirlineAsync(CancellationToken ct)
    {
        Calls.Add("identify");
        if (IdentifyError != null) throw IdentifyError;
        if (Identifies) AirlineId = "7";
        return Task.FromResult(Identifies);
    }
    public async Task RefreshSnapshotsAsync(Func<Workspace, Task> persist, CancellationToken ct, Action<ResourceKind, int>? progress)
    {
        Calls.Add("refresh");
        if (RefreshError != null) throw RefreshError;
        progress?.Invoke(ResourceKind.Airports, 3);
        if (Stage != null) await persist(new Workspace());
    }
    public Task<IReadOnlyList<AirlineParameter>> ReadParametersAsync(CancellationToken ct, bool force)
    {
        Calls.Add("parameters:" + force);
        if (ParametersError != null) throw ParametersError;
        return Task.FromResult(Parameters);
    }
    public void InvalidateParameters() => Calls.Add("invalidate");
    public void PreviewPlan(ChangeItem change)
    {
        Calls.Add("preview");
        if (PreviewError != null) throw PreviewError;
    }
    public IReadOnlyList<string> DescribePlan(ChangeItem change) => Plan(change);
    public async Task ExecuteAsync(BatchJob job, IBatchCheckpointWriter writer, CancellationToken ct, Action<BatchProgress> progress)
    {
        Calls.Add("execute");
        if (ExecuteError != null) throw ExecuteError;
        if (OnExecute != null) await OnExecute(job, writer);
    }
    public Task VerifyDeletionsAsync(BatchJob job, IBatchCheckpointWriter writer, CancellationToken ct, Action<BatchProgress> progress)
    {
        Calls.Add("verify");
        return VerifyError != null ? throw VerifyError : Task.CompletedTask;
    }
    public Task ConfirmManualDeleteAsync(BatchJob job, ChangeItem item, IBatchCheckpointWriter writer)
    {
        Calls.Add("manual");
        return ManualError != null ? throw ManualError : Task.CompletedTask;
    }
    public Task<NoReceiptInspection> InspectWithoutReceiptAsync(BatchJob job, ChangeItem item, CancellationToken ct)
    {
        Calls.Add("inspect-no-receipt");
        return Task.FromResult(NoReceipt!);
    }
    public Task ConfirmWithoutReceiptAsync(BatchJob job, NoReceiptInspection review, IBatchCheckpointWriter writer)
    { Calls.Add("confirm-no-receipt"); return Task.CompletedTask; }
    public Task MarkUnchangedAsync(BatchJob job, NoReceiptInspection review, IBatchCheckpointWriter writer)
    { Calls.Add("mark-unchanged"); return Task.CompletedTask; }
    public Task<ServerValueInspection> InspectServerValueAsync(BatchJob job, ChangeItem item, CancellationToken ct)
    {
        Calls.Add("inspect-server");
        return Task.FromResult(ServerValue!);
    }
    public Task AcceptServerValueAsync(BatchJob job, ServerValueInspection review, IBatchCheckpointWriter writer)
    { Calls.Add("accept"); return Task.CompletedTask; }
    public Task<bool> ResolveNotAppliedAsync(BatchJob job, ChangeItem item, IBatchCheckpointWriter writer, CancellationToken ct)
    {
        Calls.Add("not-applied");
        if (NotAppliedError != null) throw NotAppliedError;
        return Task.FromResult(NotApplied ?? false);
    }
}

public sealed class FakeOnline : IOnlineGateway
{
    public readonly Dictionary<Guid, FakeOnlineSession> Sessions = [];
    public readonly List<Guid> Released = [];
    public bool NoCredentials;
    public string ContractHash { get; set; } = "hash-1";
    public string EndpointUrl => "https://example.test/api/";
    public bool VerifyNotApplied, NoReceiptAllowed, ServerValueAllowed;
    public int Discarded = 2;
    public Exception? DiscardError;

    public FakeOnlineSession For(Workspace workspace)
    {
        if (!Sessions.TryGetValue(workspace.Id, out var session))
            Sessions[workspace.Id] = session = new();
        return session;
    }
    public IOnlineSession Session(Workspace workspace)
    {
        if (NoCredentials)
            throw ApplicationBlocks.Block("ApiConnectFirst");
        return For(workspace);
    }
    public IOnlineSession? Connected(Guid workspaceId) => Sessions.TryGetValue(workspaceId, out var s) && s.AirlineId != null ? s : null;
    public void Release(Guid workspaceId) { Released.Add(workspaceId); Sessions.Remove(workspaceId); }
    public Task<int> DiscardUnsentAsync(Workspace workspace, BatchJob job, IBatchCheckpointWriter writer)
        => DiscardError != null ? throw DiscardError : Task.FromResult(Discarded);
    public bool CanVerifyNotApplied(ChangeItem item) => VerifyNotApplied;
    public bool CanConfirmWithoutReceipt(ChangeItem item) => NoReceiptAllowed;
    public bool CanAcceptServerValue(ChangeItem item) => ServerValueAllowed;
}
