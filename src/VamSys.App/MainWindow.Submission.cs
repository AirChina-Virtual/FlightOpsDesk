using VamSys.Core;

namespace VamSys.App;

public sealed partial class MainWindow
{
    async Task SubmitWorkspace(RunMode mode)
    {
        if (!CanEditData() || workspace.Mode != mode) return;
        await TaskSubmission.SubmitAsync(workspace, mode, LocalOperation,
            () => ConfirmWorkspaceSubmission(mode), () => Save(dataChanged: false), SavedJobIsDurable, SelectTasksForSubmission,
            () => workspace, job => mode == RunMode.Online ? ExecuteApi(job) : ExecuteDemo(job));
    }

    bool SavedJobIsDurable(BatchJob job)
    {
        // If storage cannot be read, keep the job and block further saves until reload.
        try { return store.Load(workspace.Id).Jobs.Any(j => j.Id == job.Id); }
        catch { storageBlocked = true; return true; }
    }

    void SelectTasksForSubmission()
    {
        // Navigate ignores SelectionChanged while LocalOperation owns the lock.
        // Set the page explicitly so its final Render cannot rebuild ReviewPage.
        page = "tasks";
        reviewGeneration++;
        Navigation.SelectedItem = Navigation.MenuItems[4];
    }
}
