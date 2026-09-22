using VamSys.Core;
namespace VamSys.App;
public sealed partial class MainWindow
{
    readonly WindowCloseController closeState=new();
    int interactiveOperations,dialogs;
    void FreezeClosing(bool frozen)
    {
        autosave.Stop();
        Navigation.IsEnabled=!frozen;WorkspacePicker.IsEnabled=!frozen;AppTitleBar.IsPaneToggleButtonVisible=!frozen;
        BusyRing.IsActive=frozen;
        if(frozen)Status(()=>L("CloseSaving"));
        else SaveSoon();
    }
    async Task UserAction(Func<Task> action)
    {
        if(!closeState.IsOpen)return;
        interactiveOperations++;
        try {await Guard(action);} finally {interactiveOperations--;}
    }
}