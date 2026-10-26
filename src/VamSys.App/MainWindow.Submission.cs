using VamSys.Core;

namespace VamSys.App;

public sealed partial class MainWindow
{
    async Task SubmitWorkspace(RunMode mode)
    {
        if (!CanEditData() || workspace.Mode != mode)
            return;
        await TaskSubmission.SubmitAsync(workspace, mode, LocalOperation,
            () => ConfirmWorkspaceSubmission(mode), session.SaveJobsAsync, session.SavedJobIsDurable, SelectTasksForSubmission,
            () => workspace, job => mode == RunMode.Online ? ExecuteApi(job) : ExecuteDemo(job));
    }

    void SelectTasksForSubmission()
    {
        // Navigate ignores SelectionChanged while LocalOperation owns the lock.
        // Set the page explicitly so its final Render cannot rebuild ReviewPage.
        page = "tasks";
        reviews.Invalidate(forget: false);
        Navigation.SelectedItem = Navigation.MenuItems[4];
    }
}
