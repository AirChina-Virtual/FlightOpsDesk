namespace VamSys.Core;

// Hands a reviewed set of changes over to execution. The same handoff serves online and demo submission: the person
// confirms the list, the Pending task is saved (and found again in storage), the task page is selected before the
// window unlocks, and only the task saved by this explicit submission is started. Loading a workspace never starts
// a Pending task.
public sealed class SubmissionService(WorkspaceSession session)
{
    // Nothing happens unless the window may be edited and the workspace is in the mode being submitted.
    // localOperation runs the handoff with the window locked; confirm shows the review and returns the exact items
    // reviewed (null or empty: the person declined); selectTasks shows the task page; execute starts the saved task.
    public async Task SubmitAsync(RunMode mode, bool canEdit, Func<Func<Task>, Task> localOperation,
        Func<Task<List<ChangeItem>?>> confirm, Action selectTasks, Func<BatchJob, Task> execute)
    {
        if (!canEdit || session.Workspace.Mode != mode)
            return;
        await TaskSubmission.SubmitAsync(session.Workspace, mode, localOperation, confirm, session.SaveJobsAsync,
            session.SavedJobIsDurable, selectTasks, () => session.Workspace, execute);
    }

    // A stored task can only be started in the mode and workspace it belongs to, while the window is available.
    public void RequireExecution(BatchJob job, RunMode mode, bool available) => TaskSubmission.RequireExecution(session.Workspace, job, mode, available);
}
