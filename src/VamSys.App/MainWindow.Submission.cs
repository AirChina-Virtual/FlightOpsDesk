using VamSys.Core;

namespace VamSys.App;

public sealed partial class MainWindow
{
    Task SubmitWorkspace(RunMode mode)
        => submission.SubmitAsync(mode, gate.CanEdit, LocalOperation, () => ConfirmWorkspaceSubmission(mode), SelectTasksForSubmission,
            job => mode == RunMode.Online ? ExecuteApi(job) : ExecuteDemo(job));

    void SelectTasksForSubmission()
    {
        // The shell switches before the unlock: Navigate ignores the pane selection while LocalOperation owns the lock,
        // and the final Render must not rebuild ReviewPage.
        shell.ShowTasksBeforeUnlock();
        reviews.Invalidate(forget: false);
        SelectPane(ShellPage.Tasks);
    }
}
