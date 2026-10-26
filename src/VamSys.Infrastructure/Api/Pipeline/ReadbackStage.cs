using VamSys.Core;
using Phase = VamSys.Infrastructure.OperationsBatchExecutor.ExecutionPhase;

namespace VamSys.Infrastructure;

// Fourth stage: a write is only believed after a GET shows it. Creates and updates must read back with the values
// that were sent; hard deletes must read 404; soft deletes (airports, routes) need the full 204 + 404 proof.
// Whatever does not verify stays Unknown with its evidence, and is never repeated.
internal sealed class ReadbackStage(JobRun run)
{
    public async Task RunAsync(ItemRun r)
    {
        var item = r.Item;
        var api = run.Api;
        r.Phase = Phase.Verifying;
        if (PerformanceRun.Current is { } verifyMetrics)
            verifyMetrics.Phase = "Verifying";
        var verified = await api.FindResult(item, item.RemoteId, run.Ct);
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
}
