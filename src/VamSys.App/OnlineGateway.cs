using System.Text.Json;
using VamSys.Core;
using VamSys.Infrastructure;

namespace VamSys.App;

// Connects the application layer's online ports to the operations adapter, its transport and the stored credentials.
public sealed class OnlineGateway(WorkspaceStoreAdapter storage) : IOnlineGateway
{
    readonly Dictionary<Guid, (HttpClient Client, OperationsSession Session)> sessions = [];

    public string ContractHash => ApiContracts.Sha256;
    public string EndpointUrl => OperationsAdapter.BaseUri.AbsoluteUri;

    public IOnlineSession Session(Workspace workspace)
    {
        if (sessions.TryGetValue(workspace.Id, out var existing))
            return existing.Session;
        var raw = storage.LoadSecret(workspace.Id) ?? throw ApplicationBlocks.Block("ApiConnectFirst");
        var credentials = JsonSerializer.Deserialize<OperationsCredentials>(raw) ?? throw ApplicationBlocks.Block("ApiConnectFirst");
        var client = OperationsTransport.CreateHttpClient();
        // Conservative global pool also covers clients whose airline is not identified yet.
        var transport = new OperationsTransport(client, OperationsAdapter.BaseUri, "operations-shared", TokenProvider.ClientCredentials(client, credentials));
        var session = new OperationsSession(new OperationsAdapter(transport, workspace), workspace);
        sessions[workspace.Id] = (client, session);
        return session;
    }
    public IOnlineSession? Connected(Guid workspaceId)
        => sessions.TryGetValue(workspaceId, out var existing) && existing.Session.AirlineId != null ? existing.Session : null;
    public void Release(Guid workspaceId)
    {
        if (sessions.Remove(workspaceId, out var existing))
            existing.Client.Dispose();
    }

    public Task<int> DiscardUnsentAsync(Workspace workspace, BatchJob job, IBatchCheckpointWriter writer) => OperationsPendingDiscard.DiscardAsync(workspace, job, writer);
    public bool CanVerifyNotApplied(ChangeItem item) => OperationsUnknownResolver.CanCheck(item);
    public bool CanConfirmWithoutReceipt(ChangeItem item) => OperationsDeletionVerifier.CanConfirmWithoutReceipt(item);
    public bool CanAcceptServerValue(ChangeItem item) => OperationsServerValueResolver.CanAccept(item);

    sealed class OperationsSession(OperationsAdapter api, Workspace workspace) : IOnlineSession
    {
        public string? AirlineId => api.SessionAirlineId;
        public Task<bool> IdentifyAirlineAsync(CancellationToken ct) => api.IdentifyAirlineAsync(ct);
        public Task RefreshSnapshotsAsync(Func<Workspace, Task> persist, CancellationToken ct, Action<ResourceKind, int>? progress, IEnumerable<ResourceKind>? resources)
            => api.RefreshSnapshotsAsync(persist, ct, progress, resources);
        public async Task<IReadOnlyList<AirlineParameter>> ReadParametersAsync(CancellationToken ct, bool force) => await api.ReadParametersAsync(ct, force);
        public void InvalidateParameters() => api.InvalidateParameters();
        public void PreviewPlan(ChangeItem change) => api.PreviewPlan(change);
        public IReadOnlyList<string> DeniedFleets(ChangeItem change) => api.HasParameterMetadata ? api.DeniedFleets(change).Select(f => f.Token).ToList() : [];
        public IReadOnlyList<string> DescribePlan(ChangeItem change) => api.Plan(change).Select(s => $"{s.Method} {s.Path}").ToList();

        public async Task ExecuteAsync(BatchJob job, IBatchCheckpointWriter writer, CancellationToken ct, Action<BatchProgress> progress)
        {
            using var performance = PerformanceRun.Begin(workspace.Id, job.Id);
            using var cancellation = ct.Register(performance.MarkCancellation);
            var outcome = "Failed";
            try
            {
                await new OperationsBatchExecutor(api, workspace).ExecuteAsync(job, writer, ct, progress: progress);
                outcome = job.StatusCode.ToString();
            }
            finally
            {
                performance.RecordItems(job.Items);
                performance.Finish(outcome);
                performance.TryWrite(Path.Combine(AppPaths.DataDirectory, "performance"));
            }
        }
        public Task VerifyDeletionsAsync(BatchJob job, IBatchCheckpointWriter writer, CancellationToken ct, Action<BatchProgress> progress)
            => new OperationsDeletionVerifier(api, workspace).VerifyAsync(job, writer, ct, progress);
        public Task ConfirmManualDeleteAsync(BatchJob job, ChangeItem item, IBatchCheckpointWriter writer)
            => new OperationsDeletionVerifier(api, workspace).ConfirmManuallyAsync(job, item, writer, CancellationToken.None);
        public async Task<NoReceiptInspection> InspectWithoutReceiptAsync(BatchJob job, ChangeItem item, CancellationToken ct)
        {
            var review = await new OperationsDeletionVerifier(api, workspace).InspectWithoutReceiptAsync(job, item, ct);
            return new(review.CanConfirmDeleted, review.CanMarkUnchanged, review.Differences, review);
        }
        public Task ConfirmWithoutReceiptAsync(BatchJob job, NoReceiptInspection review, IBatchCheckpointWriter writer)
            => new OperationsDeletionVerifier(api, workspace).ConfirmWithoutReceiptAsync(job, (NoReceiptReview)review.Handle, writer);
        public Task MarkUnchangedAsync(BatchJob job, NoReceiptInspection review, IBatchCheckpointWriter writer)
            => new OperationsDeletionVerifier(api, workspace).MarkUnchangedAsync(job, (NoReceiptReview)review.Handle, writer);
        public async Task<ServerValueInspection> InspectServerValueAsync(BatchJob job, ChangeItem item, CancellationToken ct)
        {
            var review = await new OperationsServerValueResolver(api, workspace).InspectAsync(job, item, ct);
            return new(review.CanAccept, review.Current != null, review.Current?.Identity?.RemoteId, review.Differences, review);
        }
        public Task AcceptServerValueAsync(BatchJob job, ServerValueInspection review, IBatchCheckpointWriter writer)
            => new OperationsServerValueResolver(api, workspace).AcceptAsync(job, (ServerValueReview)review.Handle, writer);
        public Task<bool> ResolveNotAppliedAsync(BatchJob job, ChangeItem item, IBatchCheckpointWriter writer, CancellationToken ct)
            => new OperationsUnknownResolver(api, workspace).ResolveNotAppliedAsync(job, item, writer, ct);
    }
}
