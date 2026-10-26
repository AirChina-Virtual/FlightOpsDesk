namespace VamSys.Core;

// The single place that knows what the window may do right now. It replaces the separate flags the window used to
// combine by hand (busy, local operation, query, running user actions, open dialogs, closing, blocked storage).
// Every state is entered through a scope that is disposed when the state ends, so the combinations stay consistent.
public sealed class InteractionGate
{
    int interactiveOperations, dialogs, reviewLocks;
    bool busy, local, reviewLockOwnsLocal, querying;

    public InteractionGate(WindowCloseController? closing = null) => Closing = closing ?? new();

    public WindowCloseController Closing { get; }

    // State, for display and diagnostics. Decisions go through the queries below.
    public bool IsOpen => Closing.IsOpen;
    public bool WorkInProgress => busy;
    public bool LocalOperationActive => local;
    public bool QueryInProgress => querying;
    public bool StorageBlocked { get; private set; }
    public int RunningUserActions => interactiveOperations;
    public int OpenDialogs => dialogs;

    // Cell edits, paging, selection, batch commands and submission need a quiet window with usable storage.
    public bool CanEdit => IsOpen && !busy && !local && !querying && !StorageBlocked;
    // The navigation pane ignores a selection while a local operation holds the lock.
    public bool CanNavigate => IsOpen && !local;
    // Workspace and resource pickers.
    public bool CanSwitchContext => IsOpen && !busy && !local;
    // An exclusive piece of work (connect, refresh, execute, import, validate).
    public bool CanStartWork => IsOpen && !busy && !local;
    // A new data query is only started in the same situation as other work.
    public bool CanStartQuery => CanStartWork;
    // A button or command may run its action unless a query or local operation is under way.
    public bool CanInvokeCommand => !querying && !local;
    // The page-level guard of most commands: the window is open and no exclusive work is running.
    public bool CanAct => IsOpen && !busy;
    // Executing, resuming or verifying a stored task.
    public bool CanRunTask => IsOpen && !busy && !local && !querying;
    // A user action (and the status it reports) only runs while the window is open.
    public bool CanStartUserAction => IsOpen;
    // Controls that follow the window's openness once nothing holds them locked.
    public bool ControlsEnabled => IsOpen;
    // Connection and refresh requests are dropped while exclusive work runs, whether or not the window is closing.
    public bool CanRequestOnline => !busy;
    // The data page loads rows on show unless something else is busy.
    public bool CanLoadOnShow => !busy && !querying;
    public bool CanLoadOnBuild => !busy;
    // Resolving conflicts from the review page.
    public bool CanResolveConflicts => !busy && !local;
    // The progress ring shows for exclusive work, a query, or a worker that still reads the workspace.
    public bool ShowsProgress => busy || querying || reviewLocks > 0;
    // The ring after exclusive work or a query ends in the data page.
    public bool ShowsWorkProgress => busy;
    // The window cannot start closing while any of these runs; the user is told to wait.
    public bool CloseBlockedByActivity => busy || local || interactiveOperations > 0 || dialogs > 0;
    // Nothing runs at all (QA state reports).
    public bool IsIdle => interactiveOperations == 0 && !busy && !local && !querying && dialogs == 0;

    // The window has saved and closed: the next close event goes through untouched.
    public bool HasClosed => Closing.State == WindowCloseState.Closed;

    // Storage cannot be trusted any more (a reload failed): saves stop until the program is restarted.
    public void BlockStorage() => StorageBlocked = true;
    public void ThrowIfStorageBlocked()
    {
        if (StorageBlocked)
            throw ApplicationBlocks.Block("StorageReload");
    }

    // Starts exclusive work. Null when the window may not start any; throws when storage is blocked.
    public IDisposable? TryBeginWork()
    {
        if (!CanStartWork)
            return null;
        ThrowIfStorageBlocked();
        busy = true;
        return new Scope(() => busy = false);
    }
    // Starts a local operation (modal commands). Null unless the window may be edited.
    public LocalScope? TryBeginLocal()
    {
        if (!CanEdit)
            return null;
        local = true;
        return new LocalScope(this);
    }
    // A worker reads the workspace while edits and navigation are locked. Nested locks keep the outermost state:
    // a local operation that encloses a lock stays locked until the lock ends, and then the lock unlocks it.
    public ReviewLockScope BeginReviewLock()
    {
        var tookLocal = false;
        if (reviewLocks++ == 0 && !local)
        {
            reviewLockOwnsLocal = true;
            local = true;
            tookLocal = true;
        }
        return new ReviewLockScope(this, tookLocal);
    }
    public IDisposable? TryBeginUserAction()
    {
        if (!IsOpen)
            return null;
        interactiveOperations++;
        return new Scope(() => interactiveOperations--);
    }
    public IDisposable BeginDialog()
    {
        dialogs++;
        return new Scope(() => dialogs--);
    }
    public void SetQuerying(bool value) => querying = value;

    sealed class Scope(Action end) : IDisposable
    {
        Action? end = end;
        public void Dispose() { var action = end; end = null; action?.Invoke(); }
    }

    public sealed class ReviewLockScope : IDisposable
    {
        InteractionGate? gate;
        internal ReviewLockScope(InteractionGate gate, bool tookLocal) { this.gate = gate; TookLocal = tookLocal; }
        // True when this lock started the local state (no operation enclosed it).
        public bool TookLocal { get; }
        // True when ending this lock ended the local state.
        public bool ReleasedLocal { get; private set; }
        public void Dispose()
        {
            var owner = gate;
            gate = null;
            if (owner is null)
                return;
            if (--owner.reviewLocks == 0 && owner.reviewLockOwnsLocal)
            {
                owner.reviewLockOwnsLocal = false;
                owner.local = false;
                ReleasedLocal = true;
            }
        }
    }

    public sealed class LocalScope : IDisposable
    {
        InteractionGate? gate;
        internal LocalScope(InteractionGate gate) => this.gate = gate;
        // True when this operation ended the local state; false when a review worker still reads the workspace
        // and will unlock when it finishes.
        public bool Released { get; private set; }
        public void Dispose()
        {
            var owner = gate;
            gate = null;
            if (owner is null)
                return;
            if (owner.reviewLocks > 0)
                owner.reviewLockOwnsLocal = true;
            else
            {
                owner.local = false;
                Released = true;
            }
        }
    }
}

// The errors the application layer raises for a blocked action. They carry the same message codes and text the
// window showed before, so the localized reason does not change.
public static class ApplicationBlocks
{
    public static InvalidOperationException Block(string key, string? field = null)
        => MessageErrors.Attach(new InvalidOperationException(Messages.Define(key, ("field", field))), Messages.Define(key, ("field", field)));
}
