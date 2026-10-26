using VamSys.Core;

namespace VamSys.App;

public sealed partial class MainWindow
{
    async Task VerifyTaskDeletions(BatchJob job)
    {
        submission.RequireExecution(job, RunMode.Online, gate.CanRunTask);
        await Work(async () =>
        {
            running = new();
            try
            {
                Navigation.SelectedItem = Navigation.MenuItems[4];
                Render();
                await recovery.VerifyDeletionsAsync(job, NotifyTask, running.Token);
                Status(() => L("ApiDeleteCheckDone"));
            }
            finally { running.Dispose(); running = null; }
        });
    }
}