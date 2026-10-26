using VamSys.Core;

namespace VamSys.App;

public sealed partial class MainWindow
{
    Task SubmitWorkspace(RunMode mode)
        => submission.SubmitAsync(mode, gate.CanEdit, LocalOperation, () => ConfirmWorkspaceSubmission(mode), SelectTasksForSubmission,
            job => mode == RunMode.Online ? ExecuteApi(job) : ExecuteDemo(job));

    void SelectTasksForSubmission()
    {
        // Navigate ignores SelectionChanged while LocalOperation owns the lock.
        // Set the page explicitly so its final Render cannot rebuild ReviewPage.
        page = "tasks";
        reviews.Invalidate(forget: false);
        Navigation.SelectedItem = Navigation.MenuItems[4];
    }
}
