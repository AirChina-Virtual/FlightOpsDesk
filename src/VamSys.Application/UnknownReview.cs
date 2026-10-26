namespace VamSys.Core;

// Item IDs are captured once; each step re-reads the job, so items an earlier step resolved
// or replaced are skipped or reviewed in their current form. Recovery runs once, after all
// recovery IDs are saved, instead of re-running the whole job per proposal.
public static class UnknownReview
{
    public static async Task RunAsync(BatchJob job, Func<ChangeItem, Task<bool>> review, Func<Task> recover)
    {
        var recovery = false;
        foreach (var id in job.Items.Where(i => i.State == ItemState.Unknown).Select(i => i.Id).ToList())
        {
            if (job.Items.FirstOrDefault(i => i.Id == id) is not { State: ItemState.Unknown } item)
                continue;
            recovery |= await review(item);
        }
        if (recovery)
            await recover();
    }
}
