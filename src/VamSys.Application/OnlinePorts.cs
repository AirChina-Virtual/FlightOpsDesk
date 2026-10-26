namespace VamSys.Core;

// What a read-only GET found for a soft delete the server never answered. Handle is the adapter's own record of it and
// goes back to the adapter when the person confirms.
public sealed record NoReceiptInspection(bool CanConfirmDeleted, bool CanMarkUnchanged, IReadOnlyList<ApiFieldDifference> Differences, object Handle);
// What a read-only GET found for an accepted write whose read-back keeps differing.
public sealed record ServerValueInspection(bool CanAccept, bool HasCurrent, string? CurrentRemoteId, IReadOnlyList<ApiFieldDifference> Differences, object Handle);

// The server connection of one workspace. The infrastructure project implements it (through an adapter in the app);
// the application layer never sees HTTP.
public interface IOnlineSession
{
    string? AirlineId { get; }
    Task<bool> IdentifyAirlineAsync(CancellationToken ct);
    Task RefreshSnapshotsAsync(Func<Workspace, Task> persist, CancellationToken ct, Action<ResourceKind, int>? progress);
    Task<IReadOnlyList<AirlineParameter>> ReadParametersAsync(CancellationToken ct, bool force);
    void InvalidateParameters();
    // Builds the requests of a change without sending anything; throws when the change cannot be sent.
    void PreviewPlan(ChangeItem change);
    // The method and path of each request a change would send.
    IReadOnlyList<string> DescribePlan(ChangeItem change);
    Task ExecuteAsync(BatchJob job, IBatchCheckpointWriter writer, CancellationToken ct, Action<BatchProgress> progress);
    Task VerifyDeletionsAsync(BatchJob job, IBatchCheckpointWriter writer, CancellationToken ct, Action<BatchProgress> progress);
    Task ConfirmManualDeleteAsync(BatchJob job, ChangeItem item, IBatchCheckpointWriter writer);
    Task<NoReceiptInspection> InspectWithoutReceiptAsync(BatchJob job, ChangeItem item, CancellationToken ct);
    Task ConfirmWithoutReceiptAsync(BatchJob job, NoReceiptInspection review, IBatchCheckpointWriter writer);
    Task MarkUnchangedAsync(BatchJob job, NoReceiptInspection review, IBatchCheckpointWriter writer);
    Task<ServerValueInspection> InspectServerValueAsync(BatchJob job, ChangeItem item, CancellationToken ct);
    Task AcceptServerValueAsync(BatchJob job, ServerValueInspection review, IBatchCheckpointWriter writer);
    Task<bool> ResolveNotAppliedAsync(BatchJob job, ChangeItem item, IBatchCheckpointWriter writer, CancellationToken ct);
}

// The connections of all workspaces opened in this run, one session each.
public interface IOnlineGateway
{
    // The session of a workspace, created from its stored credentials; throws "connect first" when there are none.
    IOnlineSession Session(Workspace workspace);
    // The existing session of a workspace that has identified its airline; null otherwise.
    IOnlineSession? Connected(Guid workspaceId);
    // Drops the session of a workspace (new credentials, a reload, a switch).
    void Release(Guid workspaceId);
    string ContractHash { get; }
    // Which recovery a stored Unknown item can take, decided from the item alone.
    bool CanVerifyNotApplied(ChangeItem item);
    bool CanConfirmWithoutReceipt(ChangeItem item);
    bool CanAcceptServerValue(ChangeItem item);
}
