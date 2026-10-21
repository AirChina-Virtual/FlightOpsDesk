using VamSys.Core;

namespace VamSys.App;

// This handoff is shared by online and demo submission. Only the job saved by this
// explicit submission is started; loading a workspace never drains Pending jobs.
public static class TaskSubmission
{
    public static async Task SubmitAsync(Workspace owner, RunMode mode,
        Func<Func<Task>, Task> localOperation, Func<Task<List<ChangeItem>?>> confirm,
        Func<Task> save, Func<BatchJob, bool> isDurable, Action selectTasks, Func<Workspace> currentWorkspace,
        Func<BatchJob, Task> execute)
    {
        if (mode is not (RunMode.Online or RunMode.Demo))
            throw new ArgumentOutOfRangeException(nameof(mode));
        var airline = owner.AirlineId;
        void CheckOwner()
        {
            if (!ReferenceEquals(owner, currentWorkspace()) || owner.Mode != mode || owner.AirlineId != airline)
                throw StartBlocked();
        }

        BatchJob? saved = null;
        await localOperation(async () =>
        {
            CheckOwner();
            var items = await confirm();
            if (items is not { Count: > 0 })
                return;
            CheckOwner();
            var job = new BatchJob { Mode = mode, Items = items };
            owner.Jobs.Add(job);
            try
            { await save(); }
            catch
            {
                // An unsaved job must not linger for a later save to persist. A save that
                // committed before failing keeps the job, matching storage.
                if (!isDurable(job))
                    owner.Jobs.Remove(job);
                throw;
            }
            // Do not acquire a startup intent until the Pending job is durable.
            saved = job;
            CheckOwner();
            // Select the task page while still locked, before LocalOperation's
            // final render could start another review worker and take the lock.
            selectTasks();
        });
        if (saved is null)
            return;
        CheckOwner();
        await execute(saved);
    }

    public static void RequireExecution(Workspace workspace, BatchJob job, RunMode mode, bool available)
    {
        if (!available || workspace.Mode != mode || job.Mode != mode || !workspace.Jobs.Contains(job))
            throw StartBlocked();
    }

    static InvalidOperationException StartBlocked()
    {
        var message = Messages.Define("TaskStartBlocked");
        return MessageErrors.Attach(new InvalidOperationException(message), message);
    }
}
