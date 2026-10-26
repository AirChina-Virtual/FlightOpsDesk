using VamSys.Core;

namespace VamSys.Infrastructure;

// What a read-only GET found for a soft delete without a write receipt. Candidate is the item with the read evidence.
public sealed record NoReceiptReview(ChangeItem Item, ChangeItem Candidate, DataRow? Current, bool MatchesBefore, IReadOnlyList<ApiFieldDifference> Differences)
{
    public bool CanConfirmDeleted => Current == null;
    public bool CanMarkUnchanged => Current != null && MatchesBefore;
}

// Separate from ExecuteAsync: this path cannot submit Pending writes, even in a
// mixed job. Previously rebased/manual items receive evidence without row edits.
public sealed class OperationsDeletionVerifier(OperationsAdapter api, Workspace workspace)
{
    public async Task ConfirmManuallyAsync(BatchJob job, ChangeItem item, IBatchCheckpointWriter writer, CancellationToken ct)
    {
        if (job.Mode != RunMode.Online || workspace.Mode != RunMode.Online || !workspace.Jobs.Contains(job) || !job.Items.Contains(item) || item.State != ItemState.Unknown || !item.WriteAccepted || !IndependentApiEvidence.IsSoftDelete(item))
            throw OperationsAdapter.Block("ApiUnknown");
        var original = RequireSameRecord(item);
        ct.ThrowIfCancellationRequested();
        var candidate = JobCheckpoint.Clone(item);
        candidate.State = ItemState.ManuallyConfirmed;
        candidate.Rebased = true;
        candidate.SetMessage(Messages.Define("ApiManualResult"));
        if (candidate.DeletionVerification is { } evidence)
            candidate.DeletionVerification = evidence with { WasManuallyConfirmed = true };
        var header = JobCheckpoint.Header(job, JobStatus.NeedsAttention, Messages.Define("ApiManualResult"));
        var data = workspace.Resources[item.Resource];
        var delta = new ResourceRebaseDelta(item.Resource, item.After.LocalId, new(RowMutationKind.Delete),
            new(data.FindDraftRow(item.After.LocalId) is { } draft && Schemas.Deleted(draft) ? RowMutationKind.Delete : RowMutationKind.Keep),
            DateTimeOffset.UtcNow, true, null);
        await JobCheckpoint.WriteAsync(writer, new(CheckpointKind.Rebase, header, candidate, Delta: delta));
        JobCheckpoint.ApplyRemoval(data, delta);
        JobCheckpoint.Replace(job, item, candidate, header);
        api.Forget(item.Resource, original.RemoteId);
    }

    // The item's record belongs to this workspace's VA and the before and after rows describe the same record.
    ResourceIdentity RequireSameRecord(ChangeItem item)
    {
        if (workspace.AirlineId is not { } owner || item.Before?.Identity is not { } original || item.After.Identity is not { } target
            || original.ConnectionId != owner || target.ConnectionId != owner || original.RemoteId != target.RemoteId || item.Before!.LocalId != item.After.LocalId)
            throw OperationsAdapter.Block("ApiWrongVa");
        return original;
    }

    // A soft delete the server never answered (timeout or 5xx) has no write receipt, so neither the
    // 204/404 proof nor ConfirmManuallyAsync applies. A read-only GET decides what the user may confirm:
    // 404 marks it ManuallyConfirmed, a record that still reads exactly as before the change may be marked
    // not applied (Failed, draft kept). Evidence says so and it is never a verified operation.
    public static bool CanConfirmWithoutReceipt(ChangeItem item) => item.State == ItemState.Unknown && !item.WriteAccepted && !item.CreateCompleted && IndependentApiEvidence.IsSoftDelete(item);
    ResourceIdentity RequireNoReceipt(BatchJob job, ChangeItem item)
    {
        if (job.Mode != RunMode.Online || workspace.Mode != RunMode.Online || !workspace.Jobs.Contains(job) || !job.Items.Contains(item) || !CanConfirmWithoutReceipt(item))
            throw OperationsAdapter.Block("ApiUnknown");
        return RequireSameRecord(item);
    }

    public async Task<NoReceiptReview> InspectWithoutReceiptAsync(BatchJob job, ChangeItem item, CancellationToken ct)
    {
        var original = RequireNoReceipt(job, item);
        await api.EnsureReadConnectionAsync(ct);
        var candidate = JobCheckpoint.Clone(item);
        var found = await api.FindRow(item.Resource, item.After, original.RemoteId, ct);
        api.RecordDeletionVerification(candidate, found);
        if (candidate.ReadbackDetails is { } read)
            candidate.ReadbackDetails = read with { Phase = "Recovering" };
        var unchanged = found != null && api.Matches(candidate, found, candidate.Before!, true);
        return new(item, candidate, found, unchanged, found == null ? [] : api.BeforeDifferences(candidate, found));
    }

    public async Task<bool> ConfirmWithoutReceiptAsync(BatchJob job, ChangeItem item, IBatchCheckpointWriter writer, CancellationToken ct)
    {
        var review = await InspectWithoutReceiptAsync(job, item, ct);
        if (!review.CanConfirmDeleted)
            return false;
        await ConfirmWithoutReceiptAsync(job, review, writer);
        return true;
    }

    public async Task MarkUnchangedAsync(BatchJob job, NoReceiptReview review, IBatchCheckpointWriter writer)
    {
        var item = review.Item;
        RequireNoReceipt(job, item);
        if (!review.CanMarkUnchanged)
            throw OperationsAdapter.Block("ApiUnknown");
        var candidate = JobCheckpoint.Clone(review.Candidate);
        // Like a proven not-applied write: the step returns to prepared so the item no longer counts as unresolved.
        foreach (var step in candidate.ApiSteps.Where(s => s.State == BatchStepState.InFlight))
            step.State = BatchStepState.Prepared;
        var now = DateTimeOffset.UtcNow;
        var status = candidate.ReadbackDetails?.Status;
        candidate.DeletionVerification = new("UnchangedSinceBefore", now, item.DiagnosticDetails?.Status, status, true);
        candidate.ManualResolution = new("SoftDeleteUnchanged", now, status, false);
        candidate.State = ItemState.Failed;
        candidate.SetMessage(Messages.Define("ApiSoftDeleteUnchanged"));
        var header = JobCheckpoint.Header(job, JobStatus.NeedsAttention, Messages.Define("Text_E9008C53CF"));
        await JobCheckpoint.WriteAsync(writer, new(CheckpointKind.Task, header, candidate));
        JobCheckpoint.Replace(job, item, candidate, header);
    }

    public async Task ConfirmWithoutReceiptAsync(BatchJob job, NoReceiptReview review, IBatchCheckpointWriter writer)
    {
        var item = review.Item;
        var original = RequireNoReceipt(job, item);
        if (!review.CanConfirmDeleted)
            throw OperationsAdapter.Block("ApiUnknown");
        var candidate = JobCheckpoint.Clone(review.Candidate);
        var now = DateTimeOffset.UtcNow;
        var status = candidate.ReadbackDetails?.Status;
        candidate.DeletionVerification = new("ManualWithoutWriteReceipt", now, item.DiagnosticDetails?.Status, status, true);
        candidate.ManualResolution = new("SoftDeleteWithoutReceipt", now, status, false);
        candidate.State = ItemState.ManuallyConfirmed;
        candidate.Rebased = true;
        candidate.SetMessage(Messages.Define("ApiManualDeleteNoReceipt"));
        var header = JobCheckpoint.Header(job, JobStatus.NeedsAttention, Messages.Define("ApiManualResult"));
        var data = workspace.Resources[item.Resource];
        var delta = new ResourceRebaseDelta(item.Resource, item.After.LocalId, new(RowMutationKind.Delete),
            new(data.FindDraftRow(item.After.LocalId) is { } draft && Schemas.Deleted(draft) ? RowMutationKind.Delete : RowMutationKind.Keep), now, true, null);
        await JobCheckpoint.WriteAsync(writer, new(CheckpointKind.Rebase, header, candidate, Delta: delta));
        JobCheckpoint.ApplyRemoval(data, delta);
        JobCheckpoint.Replace(job, item, candidate, header);
        api.Forget(item.Resource, original.RemoteId);
    }

    // Reads every soft delete that is Unknown or was confirmed by hand and, where the record is provably invisible, completes it.
    public async Task VerifyAsync(BatchJob job, IBatchCheckpointWriter writer, CancellationToken ct, Action<BatchProgress>? progress = null)
    {
        if (job.Mode != RunMode.Online || workspace.Mode != RunMode.Online || !workspace.Jobs.Contains(job))
            throw OperationsAdapter.Block("TaskStartBlocked");
        await api.EnsureReadConnectionAsync(ct);
        var positions = job.Items.Select((item, index) => (item.Id, index)).ToDictionary(p => p.Id, p => p.index);
        var remaining = job.Items.Count(i => i.State != ItemState.Succeeded);
        var unknown = job.Items.Count(i => i.State == ItemState.Unknown);
        foreach (var item in job.Items.Where(IndependentApiEvidence.CanCheckDeletion).ToArray())
        {
            ct.ThrowIfCancellationRequested();
            var candidate = JobCheckpoint.Clone(item);
            var id = item.RemoteId ?? item.Before?.Identity?.RemoteId;
            if (id is null || item.Before?.Identity?.ConnectionId != workspace.AirlineId || item.After.Identity?.RemoteId != id)
                continue;
            var complete = false;
            var accessDenied = false;
            try
            {
                var found = await api.FindRow(item.Resource, item.After, id, ct);
                complete = api.RecordDeletionVerification(candidate, found);
                if (candidate.ReadbackDetails is { } read)
                    candidate.ReadbackDetails = read with { Phase = "Recovering" };
            }
            catch (Exception error) when (error is ApiResponseException or ApiAccessException or HttpRequestException or TimeoutException or System.Text.Json.JsonException)
            {
                var details = error is ApiResponseException response ? response.Details : error is ApiAccessException access ? access.Details : null;
                accessDenied = error is ApiAccessException;
                if (details != null)
                    candidate.ReadbackDetails = details with { Phase = "Recovering" };
                candidate.DeletionVerification = new("ReadFailed", DateTimeOffset.UtcNow, item.DiagnosticDetails?.Status, details?.Status,
                    item.State == ItemState.ManuallyConfirmed || item.DeletionVerification?.WasManuallyConfirmed == true);
            }
            if (complete)
            {
                candidate.State = ItemState.Succeeded;
                candidate.Rebased = true;
                candidate.SetMessage(Messages.Define("ApiDeletedInvisible"));
            }
            var remainingAfter = remaining + (candidate.State == ItemState.Succeeded ? 0 : 1) - (item.State == ItemState.Succeeded ? 0 : 1);
            var unknownAfter = unknown + (candidate.State == ItemState.Unknown ? 1 : 0) - (item.State == ItemState.Unknown ? 1 : 0);
            var staged = new BatchJob
            {
                Id = job.Id,
                Created = job.Created,
                Mode = job.Mode,
                Items = job.Items,
                StatusCode = job.StatusCode,
                Status = job.Status,
                Description = job.Description
            };
            if (remainingAfter == 0)
                staged.SetStatus(JobStatus.Completed, Messages.Define("Text_140197D868"));
            else if (unknownAfter > 0)
                staged.SetStatus(JobStatus.NeedsAttention, Messages.Define("Text_E9008C53CF"));
            if (accessDenied)
                staged.SetStatus(JobStatus.Paused, Messages.Define("ApiAuth"));
            var data = workspace.Resources[item.Resource];
            // Do not rebase a manually confirmed historical row again or disturb
            // new drafts/history. Unknown rows only remove their deletion draft.
            var remove = complete && !item.Rebased;
            ResourceRebaseDelta? delta = complete ? new(item.Resource, item.After.LocalId,
                new(remove ? RowMutationKind.Delete : RowMutationKind.Keep),
                new(remove && data.FindDraftRow(item.After.LocalId) is { } draft && Schemas.Deleted(draft) ? RowMutationKind.Delete : RowMutationKind.Keep),
                data.SnapshotAt ?? DateTimeOffset.UtcNow, remove, ApiOperationPolicy.Key(candidate)) : null;
            await JobCheckpoint.WriteAsync(writer, new(delta is null ? CheckpointKind.Task : CheckpointKind.Rebase, staged, candidate, Delta: delta));
            if (delta != null)
            {
                if (delta.Snapshot.Action == RowMutationKind.Delete)
                    data.Snapshot.RemoveAll(r => r.LocalId == delta.LocalId);
                if (delta.Draft.Action == RowMutationKind.Delete)
                    data.Draft.RemoveAll(r => r.LocalId == delta.LocalId);
                if (delta.ClearHistory)
                    data.ClearHistory(invalidateIndex: true);
                data.SnapshotAt = delta.SnapshotAt;
                if (!workspace.VerifiedOperations.Contains(delta.VerifiedOperation!))
                    workspace.VerifiedOperations.Add(delta.VerifiedOperation!);
                api.Forget(item.Resource, id);
            }
            job.Items[positions[item.Id]] = candidate;
            remaining = remainingAfter;
            unknown = unknownAfter;
            job.StatusCode = staged.StatusCode;
            job.Status = staged.Status;
            job.Description = staged.Description;
            progress?.Invoke(new(workspace.Id, job.Id, item.Id, true));
            if (accessDenied)
                break;
        }
    }
}
