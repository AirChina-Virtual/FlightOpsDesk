using VamSys.Core;
namespace VamSys.App;

public sealed partial class MainWindow
{
    void FreezeClosing(bool frozen)
    {
        session.CancelAutosave();
        CancelDataQuery();
        RefreshDataAvailability();
        Navigation.IsEnabled = !frozen;
        WorkspacePicker.IsEnabled = !frozen;
        AppTitleBar.IsPaneToggleButtonVisible = !frozen;
        BusyRing.IsActive = frozen;
        if (frozen)
            Status(() => L("CloseSaving"));
        else
            SaveSoon();
    }
    async Task UserAction(Func<Task> action)
    {
        using var running = gate.TryBeginUserAction();
        if (running is null)
            return;
        await Guard(action);
    }
}
