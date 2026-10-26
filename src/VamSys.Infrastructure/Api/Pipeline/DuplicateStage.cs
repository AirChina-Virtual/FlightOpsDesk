using VamSys.Core;

namespace VamSys.Infrastructure;

// Second stage of a create: it must not create what already exists, and must not guess while another write's
// outcome is unknown. Candidates are read completely before they are trusted (see DuplicateFinder).
internal sealed class DuplicateStage(JobRun run)
{
    // Returns true when the item was settled here (it must wait; Pending, saved).
    public async Task<bool> RunAsync(ItemRun r)
    {
        var item = r.Item;
        var api = run.Api;
        // Another item's outcome is unknown and could be this very record: wait instead of creating it twice.
        if (api.CandidateBlock(item, run.Queries) is string reason)
        { item.State = ItemState.Pending; item.SetMessage(Messages.Define(reason)); await run.Sink.SaveAsync(); return true; }
        if (PerformanceRun.Current is { } duplicateMetrics)
            duplicateMetrics.Phase = "DuplicateCheck";
        // A created jumpseat comes back without callsign or flight number, so a recovered one is recognised as new
        // by the ids that existed before the request.
        if (ApiOperationPolicy.IsJumpseatCreate(item))
            item.PreCreateCandidateIds = [];
        await foreach (var existing in api.ReadDuplicateCandidatesAsync(item, run.Queries, run.Ct))
        {
            PerformanceRun.Current?.Candidate();
            item.PreCreateCandidateIds?.Add(existing.Identity!.RemoteId);
            if (api.IsDuplicate(item, existing))
                throw OperationsAdapter.Block("ApiDuplicate");
        }
        if (api.SessionAirlineId == null)
            throw OperationsAdapter.Block("ApiConnectFirst");
        return false;
    }
}
