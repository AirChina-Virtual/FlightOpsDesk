using VamSys.Core;

namespace VamSys.Infrastructure;

// Read-only answers about writes whose outcome is not known. Nothing here writes.
internal sealed class UnknownResolver(ApiContext ctx, DuplicateFinder finder, ReadbackVerifier readback)
{
    // A legacy two-step create whose POST was answered and whose later steps never reached the
    // server may finish them. The record is never posted again.
    public static bool CanResumeCreate(ChangeItem item) => item.Kind == ChangeKind.Create && item.CreateCompleted && !item.WriteAccepted
        && item.RemoteId != null && item.ApiSteps.Count > 1 && item.ApiSteps[0].State == BatchStepState.ResponseReceived
        && item.ApiSteps.Skip(1).All(s => s.State == BatchStepState.Prepared);

    // A create whose POST left but whose answer was lost, so its later steps never left (only an aircraft with internal
    // remarks today). The record the POST made can be found and bound by the values the POST sent (ReadbackVerifier
    // .MatchesCreated), but its later steps are never sent: without the POST's answer the program never had the id
    // from the server, so the item stays Unknown until the user accepts the server's value. A one-step create is
    // never one of these: its POST values are all its values.
    public static bool CanBindLostCreate(ChangeItem item) => item.Kind == ChangeKind.Create && !item.CreateCompleted && !item.WriteAccepted
        && item.ApiSteps.Count > 1 && item.ApiSteps[0].Method == "POST" && item.ApiSteps[0].State == BatchStepState.InFlight
        && item.ApiSteps.Skip(1).All(s => s.State == BatchStepState.Prepared);

    // Read-only proof that an Unknown write did not take effect: a create has no remote record
    // matching its duplicate rule; an update or hard delete still reads exactly as before.
    public async Task<bool> ProveNotAppliedAsync(ChangeItem item, CancellationToken ct)
    {
        if (item.Kind == ChangeKind.Create)
        {
            await foreach (var existing in finder.ReadCandidatesAsync(item, new CandidateQuerySession(), ct))
                if (finder.IsDuplicate(item, existing))
                    return false;
            return true;
        }
        var id = item.RemoteId ?? item.Before?.Identity?.RemoteId;
        if (item.Before is null || id is null)
            return false;
        var current = await ctx.Reader.FindRowAsync(item.Resource, item.Before, id, ct);
        readback.RecordReadback(item, current);
        return current != null && readback.Matches(item, current, item.Before, true);
    }
}
