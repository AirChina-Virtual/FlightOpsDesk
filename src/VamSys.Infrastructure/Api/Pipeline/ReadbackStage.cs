using VamSys.Core;
using Phase = VamSys.Infrastructure.OperationsBatchExecutor.ExecutionPhase;

namespace VamSys.Infrastructure;

// Fourth stage: a write is only believed after a GET shows it. Creates and updates must read back with the values
// that were sent; hard deletes must read 404; soft deletes (airports, routes) need the full 204 + 404 proof.
// Whatever does not verify stays Unknown with its evidence, and is never repeated.
//
// G3: a read straight after the write can still show the old record (server caching or lag). When the first read does
// not show the write, the record is read once more after SecondReadDelay; only the second read decides, and the first
// is kept as FirstReadbackDetails. Soft deletes are not read twice: their proof is the 204 and the 404 together.
internal sealed class ReadbackStage(JobRun run)
{
    internal static readonly TimeSpan SecondReadDelay = TimeSpan.FromSeconds(5);

    public async Task RunAsync(ItemRun r)
    {
        var item = r.Item;
        var api = run.Api;
        r.Phase = Phase.Verifying;
        if (PerformanceRun.Current is { } verifyMetrics)
            verifyMetrics.Phase = "Verifying";
        var verified = await api.FindResult(item, item.RemoteId, run.Ct);
        if (!Shows(item, verified))
        {
            var (details, differences) = (item.ReadbackDetails, item.VerificationDifferences);
            api.RecordReadback(item, verified);
            item.FirstReadbackDetails = item.ReadbackDetails;
            await api.WaitAsync(SecondReadDelay, run.Ct);
            verified = await api.FindResult(item, item.RemoteId, run.Ct);
            // Both reads are kept: the second is the evidence the outcome rests on. A delete records its evidence below
            // only when it is not proven, so its first read leaves nothing else behind.
            if (item.Kind == ChangeKind.Delete)
                (item.ReadbackDetails, item.VerificationDifferences) = (details, differences);
            else
                api.RecordReadback(item, verified);
        }
        if (item.Kind == ChangeKind.Delete)
        {
            if (IndependentApiEvidence.IsSoftDelete(item) && api.RecordDeletionVerification(item, verified))
                await run.Completion.CompleteAsync(item, null);
            else if (verified == null && item.Resource is ResourceKind.Fleets or ResourceKind.Aircraft or ResourceKind.Routings)
                await run.Completion.CompleteAsync(item, null);
            else
            { item.State = ItemState.Unknown; api.RecordReadback(item, verified); item.SetMessage(Messages.Define("ApiSoftDeleteEvidence", ("writeStatus", item.DiagnosticDetails?.Status.ToString() ?? "?"), ("status", verified == null ? 404 : 200))); }
        }
        else if (verified != null && api.Matches(item, verified, item.After, false))
            await run.Completion.CompleteAsync(item, verified);
        else
        { item.State = ItemState.Unknown; api.RecordReadback(item, verified); item.SetMessage(Messages.Define("ApiReadbackMismatch")); }
    }

    // Whether a read-back shows the write. A soft delete always counts as shown here: it is decided by its full proof.
    bool Shows(ChangeItem item, DataRow? verified) => item.Kind == ChangeKind.Delete
        ? IndependentApiEvidence.IsSoftDelete(item) || verified == null
        : verified != null && run.Api.Matches(item, verified, item.After, false);
}
