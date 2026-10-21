using VamSys.Core;

namespace VamSys.Infrastructure;

// What a read-only GET found for an accepted write. Candidate is the item as it would be saved.
public sealed record ServerValueReview(ChangeItem Item, ChangeItem Candidate, DataRow? Current, IReadOnlyList<ApiFieldDifference> Differences, bool Matches)
{
    public bool CanAccept => Current != null && !Matches;
}

// Gives an accepted write whose read-back keeps differing a way out without ever writing: the user
// accepts the record as the server holds it now. The item becomes ManuallyConfirmed (never a verified
// operation), the snapshot becomes the server's value, and draft values that did not take effect stay
// in the draft as conflicts. An accepted hard delete whose record is still readable keeps the record.
public sealed class OperationsServerValueResolver(OperationsAdapter api, Workspace workspace)
{
    // Soft deletes have their own manual path; a move is read at the target fleet only.
    public static bool CanAccept(ChangeItem item) => item.State == ItemState.Unknown && (item.WriteAccepted || item.CreateCompleted)
        && !IndependentApiEvidence.IsSoftDelete(item) && !IndependentApiEvidence.IsMove(item) && Id(item) != null;
    static string? Id(ChangeItem item) => item.RemoteId ?? item.Before?.Identity?.RemoteId;

    void Require(BatchJob job, ChangeItem item)
    {
        if (job.Mode != RunMode.Online || workspace.Mode != RunMode.Online || !workspace.Jobs.Contains(job) || !job.Items.Contains(item) || !CanAccept(item))
            throw OperationsAdapter.Block("ApiUnknown");
    }

    public async Task<ServerValueReview> InspectAsync(BatchJob job, ChangeItem item, CancellationToken ct)
    {
        Require(job, item);
        await api.EnsureReadConnectionAsync(ct);
        var candidate = System.Text.Json.JsonSerializer.Deserialize<ChangeItem>(System.Text.Json.JsonSerializer.Serialize(item))!;
        var current = await api.FindResult(candidate, Id(candidate), ct);
        api.RecordReadback(candidate, current);
        if (candidate.ReadbackDetails is { } read) candidate.ReadbackDetails = read with { Phase = "Recovering" };
        if (current != null && item.Kind == ChangeKind.Delete) candidate.VerificationDifferences.Add(new("record", "deleted", current.Identity!.RemoteId));
        var matches = current != null && item.Kind != ChangeKind.Delete && api.Matches(candidate, current, candidate.After, false);
        return new(item, candidate, current, candidate.VerificationDifferences, matches);
    }

    public async Task AcceptAsync(BatchJob job, ServerValueReview review, IBatchCheckpointWriter writer)
    {
        var item = review.Item;
        Require(job, item);
        if (!review.CanAccept) throw OperationsAdapter.Block("ApiUnknown");
        var current = review.Current!;
        var candidate = System.Text.Json.JsonSerializer.Deserialize<ChangeItem>(System.Text.Json.JsonSerializer.Serialize(review.Candidate))!;
        candidate.State = ItemState.ManuallyConfirmed; candidate.Rebased = true; candidate.RecoveryCandidateId = null;
        candidate.RemoteId = current.Identity!.RemoteId; candidate.After.Identity = current.Identity;
        candidate.ManualResolution = new("AcceptServerValue", DateTimeOffset.UtcNow, candidate.ReadbackDetails?.Status, true);
        candidate.SetMessage(Messages.Define(item.Kind == ChangeKind.Delete ? "ApiAcceptedServerRecord" : "ApiAcceptedServerValue"));
        var data = workspace.Resources[item.Resource];
        var local = item.After.LocalId;
        var snapshot = current.Copy(); snapshot.LocalId = local;
        var baseline = data.Snapshot.FirstOrDefault(r => r.LocalId == local) ?? item.Before;
        var draft = data.FindDraftRow(local);
        var conflicts = new List<MergeConflict>();
        var rebased = Rebase(item, draft, baseline, snapshot, conflicts);
        var allConflicts = data.Conflicts.Where(c => c.RowId != local).Concat(conflicts).ToList();
        var header = new BatchJob { Id = job.Id, Created = job.Created, Mode = job.Mode, Items = job.Items };
        header.SetStatus(JobStatus.NeedsAttention, Messages.Define("ApiManualResult"));
        var delta = new ResourceRebaseDelta(item.Resource, local, new(RowMutationKind.Upsert, snapshot), new(RowMutationKind.Upsert, rebased),
            DateTimeOffset.UtcNow, true, null, allConflicts);
        try { await writer.WriteAsync(new(CheckpointKind.Rebase, header, candidate, Delta: delta), CancellationToken.None); }
        catch (Exception error) { throw new PersistenceException(error); }
        data.Snapshot.RemoveAll(r => r.LocalId == local); data.Snapshot.Add(snapshot.Copy());
        if (draft != null) data.ReplaceDraftRows([rebased.Copy()]); else data.Draft.Add(rebased.Copy());
        data.Conflicts = allConflicts; data.ClearHistory(invalidateIndex: draft == null); data.SnapshotAt = delta.SnapshotAt;
        job.Items[job.Items.IndexOf(item)] = candidate;
        job.StatusCode = header.StatusCode; job.Status = header.Status; job.Description = header.Description;
    }

    // The draft follows the server except where the user's values differ from it: values sent by this
    // write, or edited locally since the snapshot, are kept and listed as conflicts. A delete keeps the
    // record as it is now and drops the deletion marker.
    static DataRow Rebase(ChangeItem item, DataRow? draft, DataRow? baseline, DataRow server, List<MergeConflict> conflicts)
    {
        var result = server.Copy();
        if (draft == null) return result;
        foreach (var (field, mine) in draft.Fields)
        {
            if (!server.Fields.TryGetValue(field, out var theirs)) { result.Fields[field] = mine; continue; }
            if (item.Kind == ChangeKind.Delete || field == "ID" || OperationsAdapter.Equivalent(field, mine, theirs)) continue;
            var original = baseline?.Get(field);
            if (item.Fields.ContainsKey(field) || baseline == null || mine != original)
            { result.Fields[field] = mine; conflicts.Add(new(draft.LocalId, field, original, mine, theirs)); }
        }
        return result;
    }
}
