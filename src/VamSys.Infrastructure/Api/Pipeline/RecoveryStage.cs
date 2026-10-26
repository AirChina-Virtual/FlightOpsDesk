using VamSys.Core;
using Phase = VamSys.Infrastructure.OperationsBatchExecutor.ExecutionPhase;

namespace VamSys.Infrastructure;

// What happens to an item that was interrupted (Running, Unknown, a create that was answered, a write that was accepted).
// It is read-only: the server is asked what became of the write and the item is settled from the answer. An interrupted
// request is never repeated; the one exception is the later steps of a legacy two-step create whose POST was answered.
internal sealed class RecoveryStage(JobRun run, WriteStage write)
{
    public async Task RunAsync(ItemRun r)
    {
        var item = r.Item;
        var api = run.Api;
        var found = r.Id == null ? null : await api.FindResult(item, r.Id, run.Ct);
        if (item.Kind != ChangeKind.Delete && found != null && api.Matches(item, found, item.After, false))
            await run.Completion.CompleteAsync(item, found);
        else if (IndependentApiEvidence.IsSoftDelete(item) && api.RecordDeletionVerification(item, found))
            await run.Completion.CompleteAsync(item, null);
        else if (item.Kind == ChangeKind.Delete && found == null && r.Id != null && item.Resource is ResourceKind.Fleets or ResourceKind.Aircraft or ResourceKind.Routings)
            await run.Completion.CompleteAsync(item, null);
        else if (OperationsAdapter.CanResumeCreate(item) && found != null && api.MatchesCreated(item, found))
        {
            await write.ResumeAsync(r, api.ExecutionPlan(item));
            r.Phase = Phase.Verifying;
            var resumed = await api.FindResult(item, item.RemoteId, run.Ct);
            if (resumed != null && api.Matches(item, resumed, item.After, false))
                await run.Completion.CompleteAsync(item, resumed);
            else
            { item.State = ItemState.Unknown; api.RecordReadback(item, resumed); item.SetMessage(Messages.Define("ApiReadbackMismatch")); }
        }
        else
        { item.State = ItemState.Unknown; api.RecordReadback(item, found); item.SetMessage(Messages.Define("ApiUnknown")); }
    }
}
