using VamSys.Core;
using Phase = VamSys.Infrastructure.OperationsBatchExecutor.ExecutionPhase;

namespace VamSys.Infrastructure;

// What happens to an item that was interrupted (Running, Unknown, a create that was answered, a write that was accepted).
// It is read-only: the server is asked what became of the write and the item is settled from the answer. An interrupted
// request is never repeated; the one exception is the later steps of a legacy two-step create whose POST was answered.
// A two-step create whose POST answer was lost gets its record bound, never its later steps (UnknownResolver.CanBindLostCreate).
internal sealed class RecoveryStage(JobRun run, WriteStage write)
{
    public async Task RunAsync(ItemRun r)
    {
        var item = r.Item;
        var api = run.Api;
        // E1: a create whose answer was lost and that has no id yet: look for the record it made.
        if (r.Id == null && CreatedRecordFinder.CanSearch(item))
        {
            var created = await api.FindCreatedAsync(item, run.Ct);
            item.FoundCreateCandidates = created;
            if (created.Count == 1)
            {
                item.ProposeRecoveryId(created[0], "Automatic");
                r.Id = item.EditableRecoveryId;
            }
        }
        var found = r.Id == null ? null : await api.FindResult(item, r.Id, run.Ct);
        if (item.Kind != ChangeKind.Delete && found != null && api.Matches(item, found, item.After, false))
            await run.Completion.CompleteAsync(item, found);
        else if (IndependentApiEvidence.IsSoftDelete(item) && api.RecordDeletionVerification(item, found))
            await run.Completion.CompleteAsync(item, null);
        else if (IndependentApiEvidence.IsSoftDelete(item) && r.Id != null && api.RecordDeletionWithoutReceipt(item, found))
            await run.Completion.CompleteAsync(item, null, verifiedOperation: false);
        else if (item.Kind == ChangeKind.Delete && found == null && r.Id != null && item.Resource == ResourceKind.Aircraft && !item.WriteAccepted
            && await api.FindAircraftElsewhereAsync(item, r.Id, run.Ct) is var elsewhere && (!elsewhere.Checked || elsewhere.Fleet != null))
        {
            // G7: not gone, only not under its old fleet (or no registration to look for): it stays for the user.
            item.State = ItemState.Unknown;
            api.RecordReadback(item, null);
            item.SetMessage(elsewhere.Fleet is { } fleet
                ? Messages.Define("ApiAircraftElsewhere", ("registration", item.Before!.Get("Registration")), ("fleet", fleet))
                : Messages.Define("ApiUnknown"));
        }
        else if (item.Kind == ChangeKind.Delete && found == null && r.Id != null && item.Resource is ResourceKind.Fleets or ResourceKind.Aircraft or ResourceKind.Routings)
            await run.Completion.CompleteAsync(item, null);
        else if (UnknownResolver.CanBindLostCreate(item) && found != null && api.MatchesCreated(item, found))
        {
            // The record the POST made, found automatically or by the id the user entered, carries every value the POST
            // sent. It is bound so the user can accept it as the server holds it; the later steps are never sent for it.
            item.RemoteId = found.Identity!.RemoteId;
            item.After = item.After.WithIdentity(found.Identity);
            item.RecoveryCandidateId = null;
            item.CreateFoundWithoutAnswer = true;
            item.State = ItemState.Unknown;
            api.RecordReadback(item, found);
            item.SetMessage(Messages.Define("ApiCreateFoundFollowUpsNotSent", ("id", item.RemoteId)));
        }
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
        {
            item.State = ItemState.Unknown;
            api.RecordReadback(item, found);
            item.SetMessage(r.Id == null && item.FoundCreateCandidates is { Count: > 1 } candidates
                ? Messages.Define("ApiCreateCandidates", ("count", candidates.Count)) : Messages.Define("ApiUnknown"));
        }
    }
}
