using VamSys.Core;

namespace VamSys.Infrastructure;

// Gives a stopped online job's never-sent items a way out without contacting the server. They become
// Failed, not Skipped: every executor, including older releases, skips Failed items, while Resume
// processes Skipped items again. Their drafts stay, so the changes can be reviewed and submitted again.
public static class OperationsPendingDiscard
{
    public static async Task<int> DiscardAsync(Workspace workspace, BatchJob job, IBatchCheckpointWriter writer)
    {
        if (job.Mode != RunMode.Online || !workspace.Jobs.Contains(job))
            throw OperationsAdapter.Block("ApiUnknown");
        var positions = new List<int>();
        var candidates = new List<ChangeItem>();
        for (var n = 0; n < job.Items.Count; n++)
        {
            if (!ApiOperationPolicy.CanDiscardUnsent(job.Items[n]))
                continue;
            var candidate = JobCheckpoint.Clone(job.Items[n]);
            candidate.State = ItemState.Failed;
            candidate.SetMessage(Messages.Define("ApiDiscardedUnsent"));
            positions.Add(n);
            candidates.Add(candidate);
        }
        if (candidates.Count == 0)
            return 0;
        var header = JobCheckpoint.Header(job, JobStatus.NeedsAttention, Messages.Define("Text_E9008C53CF"));
        await JobCheckpoint.WriteAsync(writer, new(CheckpointKind.Task, header, Items: candidates));
        for (var n = 0; n < positions.Count; n++)
            job.Items[positions[n]] = candidates[n];
        job.StatusCode = header.StatusCode;
        job.Status = header.Status;
        job.Description = header.Description;
        return candidates.Count;
    }
}

// Gives an Unknown write a way out without ever writing: once the read-only proof holds, the item
// is marked Failed. Its draft stays, so the change can be reviewed and submitted again.
public sealed class OperationsUnknownResolver(OperationsAdapter api, Workspace workspace)
{
    // Accepted, created or soft-deleted writes cannot be disproved by reading.
    public static bool CanCheck(ChangeItem item) => item.State == ItemState.Unknown && !item.CreateCompleted && !item.WriteAccepted
        && !IndependentApiEvidence.IsSoftDelete(item);

    public async Task<bool> ResolveNotAppliedAsync(BatchJob job, ChangeItem item, IBatchCheckpointWriter writer, CancellationToken ct)
    {
        if (job.Mode != RunMode.Online || workspace.Mode != RunMode.Online || !workspace.Jobs.Contains(job) || !job.Items.Contains(item) || !CanCheck(item))
            throw OperationsAdapter.Block("ApiUnknown");
        await api.EnsureReadConnectionAsync(ct);
        var candidate = JobCheckpoint.Clone(item);
        if (!await api.ProveNotAppliedAsync(candidate, ct))
            return false;
        foreach (var step in candidate.ApiSteps.Where(s => s.State == BatchStepState.InFlight))
            step.State = BatchStepState.Prepared;
        candidate.State = ItemState.Failed;
        candidate.SetMessage(Messages.Define("ApiNotAppliedVerified"));
        var header = JobCheckpoint.Header(job, JobStatus.NeedsAttention, Messages.Define("Text_E9008C53CF"));
        await JobCheckpoint.WriteAsync(writer, new(CheckpointKind.Task, header, candidate));
        JobCheckpoint.Replace(job, item, candidate, header);
        return true;
    }
}
