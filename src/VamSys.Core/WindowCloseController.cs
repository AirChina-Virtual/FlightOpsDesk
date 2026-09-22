namespace VamSys.Core;

public enum WindowCloseState { Open, Saving, Closed }
public sealed class WindowCloseController
{
    public WindowCloseState State { get; private set; }
    public bool IsOpen => State==WindowCloseState.Open;
    public async Task<bool> RequestAsync(bool blocked,Func<Task> save,Action<bool> freeze)
    {
        if(State==WindowCloseState.Closed)return true;
        if(!IsOpen||blocked)return false;
        State=WindowCloseState.Saving;freeze(true);
        try {await save();State=WindowCloseState.Closed;return true;}
        catch {State=WindowCloseState.Open;freeze(false);throw;}
    }
}