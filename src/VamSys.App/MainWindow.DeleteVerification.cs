using VamSys.Core;
using VamSys.Infrastructure;

namespace VamSys.App;

public sealed partial class MainWindow
{
    async Task VerifyTaskDeletions(BatchJob job)
    {
        TaskSubmission.RequireExecution(workspace, job, RunMode.Online, closeState.IsOpen && !busy && !localOperation && !querying);
        await Work(async () =>
        {
            running = new();
            try
            {
                Navigation.SelectedItem = Navigation.MenuItems[4];
                Render();
                await new OperationsDeletionVerifier(Api(), workspace).VerifyAsync(job,
                    new AppCheckpointWriter(this, store.CheckpointWriter(workspace)), running.Token, NotifyTask);
                Status(() => L("ApiDeleteCheckDone"));
            }
            catch (PersistenceException)
            {
                ReloadAfterCheckpointFailure();
                throw;
            }
            finally { running.Dispose(); running = null; }
        });
    }
}
