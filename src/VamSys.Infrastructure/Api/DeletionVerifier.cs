using VamSys.Core;

namespace VamSys.Infrastructure;

// The proof that a soft delete took effect. Airports and routes are soft-deleted: DELETE answers 204 and the record
// then reads 404. The proof is only complete when the item's saved evidence is consistent end to end (same VA, same id,
// the one saved DELETE step, the 204 response, and a later 404 read that falls after it). Anything less is not proof.
internal sealed class DeletionVerifier(ApiContext ctx, ReadbackVerifier readback)
{
    public bool Record(ChangeItem item, DataRow? current)
    {
        readback.RecordReadback(item, current);
        var id = item.RemoteId ?? item.Before?.Identity?.RemoteId;
        var relative = id is null ? null : ResourcePaths.Collection(item.Resource) + "/" + id;
        var absolute = relative is null ? null : OperationsAdapter.BaseUri.AbsolutePath + relative;
        var write = item.DiagnosticDetails;
        var read = item.ReadbackDetails;
        var step = item.ApiSteps.Count == 1 ? item.ApiSteps[0] : null;
        var proof = IndependentApiEvidence.Covers(item, ApiContracts.Sha256) && IndependentApiEvidence.IsSoftDelete(item)
            && item.WriteAccepted && item.Before?.Identity is { } identity && identity.ConnectionId == ctx.ConnectionId
            && identity.RemoteId == id && item.Before.LocalId == item.After.LocalId && item.After.Identity?.RemoteId == id && item.After.Identity.ConnectionId == ctx.ConnectionId
            && ctx.SessionAirlineId == ctx.ConnectionId
            && step is { Method: "DELETE", State: BatchStepState.ResponseReceived } && step.Path == relative && step.Body.Count == 0
            && write is { Method: "DELETE", Status: 204 } && write.Path == absolute
            && step.StartedAt is not null && step.FinishedAt is not null && step.FinishedAt >= step.StartedAt
            && write.StartedAt >= step.StartedAt && write.FinishedAt >= write.StartedAt && write.FinishedAt <= step.FinishedAt
            && read is { Method: "GET", Status: 404 } && read.Path == absolute && read.StartedAt >= write.FinishedAt;
        var outcome = proof && current is null ? "ApiInvisible" : current is not null ? "StillReadable" : "MissingWriteEvidence";
        item.DeletionVerification = new(outcome, ctx.Transport.UtcNow, write?.Status, read?.Status,
            item.State == ItemState.ManuallyConfirmed || item.DeletionVerification?.WasManuallyConfirmed == true);
        if (proof && current is null)
            item.VerificationDifferences = [];
        return proof && current is null;
    }

    public const string WithoutReceipt = "AutomaticWithoutWriteReceipt";

    // D3: a soft delete whose DELETE was sent but never answered (timeout or 5xx, so no 204), and that the recovery GET
    // just read as 404. The program completes it, but it is not the 204 + 404 proof: the evidence says "without write
    // receipt" and the operation is not counted as verified. Everything else about the record must still agree (same VA,
    // same id, the one saved DELETE step, a 404 read after the request started). Call after Record, which saves the read.
    public bool RecordWithoutReceipt(ChangeItem item, DataRow? current)
    {
        if (current != null || item.WriteAccepted || item.CreateCompleted || !IndependentApiEvidence.IsSoftDelete(item))
            return false;
        var id = item.RemoteId ?? item.Before?.Identity?.RemoteId;
        var relative = id is null ? null : ResourcePaths.Collection(item.Resource) + "/" + id;
        var absolute = relative is null ? null : OperationsAdapter.BaseUri.AbsolutePath + relative;
        var read = item.ReadbackDetails;
        var step = item.ApiSteps.Count == 1 ? item.ApiSteps[0] : null;
        var evidence = item.Before?.Identity is { } identity && identity.ConnectionId == ctx.ConnectionId && identity.RemoteId == id
            && item.Before.LocalId == item.After.LocalId && item.After.Identity?.RemoteId == id && item.After.Identity.ConnectionId == ctx.ConnectionId
            && ctx.SessionAirlineId == ctx.ConnectionId
            && step is { Method: "DELETE", State: BatchStepState.InFlight or BatchStepState.ResponseReceived } && step.Path == relative && step.StartedAt is not null
            && item.DiagnosticDetails?.Status is not 204
            && read is { Method: "GET", Status: 404 } && read.Path == absolute && read.StartedAt >= step.StartedAt;
        if (!evidence)
            return false;
        item.DeletionVerification = new(WithoutReceipt, ctx.Transport.UtcNow, item.DiagnosticDetails?.Status, read!.Status, false);
        item.VerificationDifferences = [];
        return true;
    }
}
