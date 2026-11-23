using Microsoft.UI.Xaml;
using VamSys.Core;

namespace VamSys.App;

// Reading a workspace (at startup, when switching, after a failed checkpoint) runs on a worker; see
// WorkspaceSession.ReadAsync. While it runs the page is covered by the loading panel with its progress, and the gate
// keeps edits, navigation, the pickers, commands, work, tasks, queries and autosave waiting. The window only puts the
// new workspace in place, and renders it, on its own thread once the read has finished.
public sealed partial class MainWindow
{
    // The startup or switch now running, up to the rendered result; closing waits for it.
    Task workspaceLoad = Task.CompletedTask;
    // The first read, up to the first rendered page.
    Task startup = Task.CompletedTask;
    bool startupFailed, closeAfterLoad;
    WorkspaceLoadProgress? loadProgress;

    // The first workspace could not be read: the app shows the startup error in place of this window.
    public event Action<Exception>? StartupFailed;

    void InitializeWorkspaceLoad()
    {
        loadProgress = new(this);
        session.LoadObserver = loadProgress;
        session.LoadingChanged += ShowWorkspaceLoading;
    }

    // Shows or hides the loading panel as the gate says. A read started by the session (or a hold of the window) shows
    // it; it is hidden once no read and no hold is left.
    void ShowWorkspaceLoading(bool started)
    {
        loadProgress!.Restart();
        var loading = gate.WorkspaceLoading;
        if (started)
        {
            EditableCell.CloseHint();
            WorkspaceLoadingText.Text = L("WorkspaceLoading");
            WorkspaceLoadingDetail.Text = "";
        }
        WorkspaceLoadingPanel.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        WorkspaceLoadingRing.IsActive = loading;
        ApplyShellState();
        if (ready)
            RefreshDataAvailability();
    }

    // Keeps the window locked and the panel shown across the hop from the session's read back to the window, until the
    // window has the new workspace in place: the old page must not be usable once the session holds the new workspace.
    IDisposable HoldWorkspaceLoading()
    {
        var hold = new LoadHold(this, gate.BeginWorkspaceLoad());
        try
        { ShowWorkspaceLoading(true); }
        catch { hold.Dispose(); throw; }
        return hold;
    }
    sealed class LoadHold(MainWindow window, IDisposable scope) : IDisposable
    {
        IDisposable? scope = scope;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref scope, null) is not { } ending)
                return;
            ending.Dispose();
            window.ShowWorkspaceLoading(false);
        }
    }

    // Reads the first workspace while the window already shows the loading panel (nothing else can be used yet), then
    // shows the workspace page. A failed read is reported like any startup failure.
    async Task OpenFirstWorkspace()
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        using (HoldWorkspaceLoading())
        {
            try
            { await session.OpenAsync(L("Text_707DBE0213")); }
            catch (Exception e)
            {
                startupFailed = true;
                StartupFailed?.Invoke(e);
                return;
            }
            startupLoadMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            ReloadWorkspaces();
        }
        // No await from here on: the page is in place before any input is handled.
        ready = true;
        SelectPane(ShellPage.Workspace);
        Render();
        startupShownAt = DateTime.Now;
    }

    // Saves the current workspace and reads the chosen one; a failed save or read keeps the current one (the error is
    // shown as before) and puts the picker back on it.
    async Task SwitchWorkspace(Guid id)
    {
        CancelDataQuery();
        var switching = UserAction(async () =>
        {
            using (HoldWorkspaceLoading())
            {
                var reading = session.SwitchWorkspaceAsync(id);
                loadProgress!.Saving();
                try
                { await reading; }
                catch
                {
                    ShowCurrentWorkspaceChoice();
                    throw;
                }
            }
            // No await from here on: the new page is in place before any input is handled.
            online.Release(id);
            Render();
            ShowCurrentWorkspaceChoice();
        });
        workspaceLoad = switching;
        await switching;
    }

    // A choice in the workspace picker that cannot be acted on now is undone, so the picker always names the workspace shown.
    void ShowCurrentWorkspaceChoice()
    {
        if (WorkspacePicker.ItemsSource is not List<WorkspaceChoice> choices || choices.FirstOrDefault(x => x.Id == workspace.Id) is not { } current
            || ReferenceEquals(WorkspacePicker.SelectedItem, current))
            return;
        var wasReady = ready;
        ready = false;
        WorkspacePicker.SelectedItem = current;
        ready = wasReady;
    }

    // Closing while a workspace is read waits for the read (and the page it shows), then closes as usual. True when
    // the close may go on.
    async Task<bool> WaitForWorkspaceLoadBeforeClosing()
    {
        if (workspaceLoad.IsCompleted)
            return !startupFailed;
        if (closeAfterLoad)
            return false;
        closeAfterLoad = true;
        Status(() => L("WorkspaceLoadingCloseLater"));
        try
        { await workspaceLoad; }
        catch { }
        finally { closeAfterLoad = false; }
        return !startupFailed;
    }

    // Shows a read's progress. The store reports from the worker that reads; the newest state is kept here and shown by
    // at most one queued update at a time, and an update from a read that has ended is dropped.
    sealed class WorkspaceLoadProgress(MainWindow window) : IWorkspaceLoadObserver
    {
        int generation, stage = -1, read, total, queued;
        public void Restart() { Interlocked.Increment(ref generation); Volatile.Write(ref stage, -1); }
        // Before the read: the current workspace is being saved.
        public void Saving()
        {
            if (window.WorkspaceLoadingPanel.Visibility == Visibility.Visible && Volatile.Read(ref stage) < 0)
                window.WorkspaceLoadingDetail.Text = window.L("WorkspaceLoadingSaving");
        }
        public void Stage(WorkspaceLoadStage value) { Volatile.Write(ref stage, (int)value); Post(); }
        public void Rows(int value, int of) { Volatile.Write(ref read, value); Volatile.Write(ref total, of); Post(); }
        void Post()
        {
            if (Interlocked.Exchange(ref queued, 1) == 1)
                return;
            var posted = Volatile.Read(ref generation);
            if (!window.DispatcherQueue.TryEnqueue(() => Show(posted)))
                Volatile.Write(ref queued, 0);
        }
        void Show(int posted)
        {
            Volatile.Write(ref queued, 0);
            if (posted != Volatile.Read(ref generation) || window.WorkspaceLoadingPanel.Visibility != Visibility.Visible)
                return;
            var current = (WorkspaceLoadStage)Volatile.Read(ref stage);
            var text = current switch
            {
                WorkspaceLoadStage.Rows or WorkspaceLoadStage.Share when Volatile.Read(ref total) > 0
                    => window.L("WorkspaceLoadingRows", ("read", Volatile.Read(ref read)), ("total", Volatile.Read(ref total))),
                WorkspaceLoadStage.History or WorkspaceLoadStage.Validate or WorkspaceLoadStage.Index or WorkspaceLoadStage.Journal => window.L("WorkspaceLoadingHistory"),
                _ => null
            };
            if (text != null)
                window.WorkspaceLoadingDetail.Text = text;
        }
    }
}
