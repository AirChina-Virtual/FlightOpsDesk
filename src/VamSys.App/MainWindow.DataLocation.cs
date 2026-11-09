using VamSys.Core;
using VamSys.Infrastructure;
using Windows.Storage.Pickers;

namespace VamSys.App;

public sealed partial class MainWindow
{
    // Set once the data location changed and the window closed; the application then starts the program again.
    public bool RestartRequested { get; private set; }

    async Task ChooseDataLocation()
    {
        if (!gate.CanAct || AppPaths.ManagedByEnvironment)
            return;
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        if (await picker.PickSingleFolderAsync() is { } folder)
            await ChangeDataLocation(folder.Path, exact: false);
    }

    Task UseDefaultDataLocation() => ChangeDataLocation(AppPaths.DefaultDirectory, exact: true);

    void OpenDataFolder() => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppPaths.DataDirectory) { UseShellExecute = true });

    async Task ChangeDataLocation(string chosen, bool exact)
    {
        if (!gate.CanAct || AppPaths.ManagedByEnvironment)
            return;
        var state = AppPaths.Chosen;
        var current = AppPaths.DataDirectory;
        var plan = await Task.Run(() => DataLocationPlanner.Plan(chosen, current, state.Retire, storage.Inner.DataBytes(), exact));
        var target = plan.Target;
        if (plan.Decision is not (DataLocationDecision.CopyHere or DataLocationDecision.UseExisting))
        {
            var refusal = "DataLocation" + plan.Decision;
            StatusError(() => L(refusal, ("path", target)));
            return;
        }
        var copy = plan.Decision == DataLocationDecision.CopyHere;
        var title = L("DataLocationChange");
        var message = copy ? L("DataLocationCopyConfirm", ("from", current), ("to", target)) : L("DataLocationExistingConfirm", ("to", target));
        if (!await ConfirmMessage(title, message, L(copy ? "DataLocationCopyAction" : "DataLocationExistingAction"), scrolls: true))
            return;
        // Only this action may still be running: the save below freezes the window as closing does.
        if (gate.WorkInProgress || gate.LocalOperationActive || gate.RunningUserActions > 1 || gate.OpenDialogs > 0)
        {
            StatusError(() => L("CloseBlocked"));
            return;
        }
        var isDefault = string.Equals(Path.GetFullPath(target), Path.GetFullPath(AppPaths.DefaultDirectory), StringComparison.OrdinalIgnoreCase);
        var next = new DataLocationState(isDefault ? null : target, copy ? [.. state.Retire, current] : state.Retire);
        var closed = await gate.Closing.RequestAsync(false, async () =>
        {
            await session.SaveForCloseAsync();
            if (copy)
            {
                Status(() => L("DataLocationCopying"));
                await storage.Inner.CopyDataToAsync(target);
            }
            else
                await Task.Run(() => WorkspaceStore.RequireOpenable(target));
            // The switch itself: until this file is replaced the next start still opens the current directory.
            DataLocationFile.Write(AppPaths.LocationFile, next);
        }, FreezeClosing);
        if (closed)
        {
            RestartRequested = true;
            Close();
        }
    }
}
