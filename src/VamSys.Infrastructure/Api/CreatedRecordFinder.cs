using System.Globalization;
using System.Text.Json;
using VamSys.Core;

namespace VamSys.Infrastructure;

// E1 + G5: finds the record a create made when its answer was lost, so the program can bind it instead of asking the
// user for its id. Read-only. A record counts only when all of these hold:
//   new        it was not among the candidates read just before the POST (PreCreateCandidateIds)
//   the same   it is a duplicate of the change by the resource's rule and carries every value the create sent
//   in time    its created_at falls inside the POST's request window (± 60 s), moved by the server clock's offset
// Exactly one such record is bound (as an automatic proposal, then verified by the normal recovery read); none or more
// than one are only listed for the user. A create saved before PreCreateCandidateIds was kept for every create cannot be
// told apart from older records, so it is not searched.
internal sealed class CreatedRecordFinder(ApiContext ctx, DuplicateFinder finder, ReadbackVerifier readback)
{
    public static bool CanSearch(ChangeItem item) => item.CanProposeRecoveryId && item.RecoveryCandidateId == null && item.RemoteId == null
        && item.PreCreateCandidateIds != null && Post(item)?.StartedAt != null;

    static BatchStep? Post(ChangeItem item) => item.ApiSteps.FirstOrDefault(s => s.Method == "POST");

    public async Task<List<string>> FindAsync(ChangeItem item, CancellationToken ct)
    {
        var post = Post(item)!;
        var start = post.StartedAt!.Value;
        var end = post.FinishedAt ?? post.DeadlineAt ?? start + ctx.Transport.RequestTimeout;
        var found = new List<string>();
        TimeSpan? shift = null;
        await foreach (var row in finder.ReadCandidatesAsync(item, new CandidateQuerySession(), ct))
        {
            shift ??= ReadbackVerifier.ServerClockOffset(ctx.Transport.LastResponse);
            var id = row.Identity!.RemoteId;
            if (item.PreCreateCandidateIds!.Contains(id) || found.Contains(id) || !finder.IsDuplicate(item, row) || !readback.ContentMatches(item, row))
                continue;
            if (CreatedAt(row) is not { } created)
                continue;
            var margin = ReadbackVerifier.RecoveryWindowMargin;
            if (created >= start + shift.Value - margin && created <= end + shift.Value + margin)
                found.Add(id);
        }
        return found;
    }

    static DateTimeOffset? CreatedAt(DataRow row)
    {
        if (row.RawApiJson is not string json)
            return null;
        using var raw = JsonDocument.Parse(json);
        return raw.RootElement.TryGetProperty("created_at", out var value) && value.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var created) ? created : null;
    }
}
