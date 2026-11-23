namespace VamSys.Core;

// Holds the open workspace and every way of saving it: the save lock, the cell edits not yet saved, the quiet-period
// autosave, and the switch to another workspace. All changes to the database go through here, so a save always
// writes what the journal says changed and a failed save leaves memory and the pending edits as they were.
public sealed class WorkspaceSession
{
    public static readonly TimeSpan AutosaveInterval = TimeSpan.FromMilliseconds(700);

    readonly IWorkspaceStorage storage;
    readonly InteractionGate gate;
    readonly IAutosaveTimer autosave;
    readonly SemaphoreSlim saveLock = new(1);
    Workspace workspace = new();

    public WorkspaceSession(IWorkspaceStorage storage, InteractionGate gate, IAutosaveTimer autosave)
    {
        this.storage = storage;
        this.gate = gate;
        this.autosave = autosave;
    }

    public Workspace Workspace => workspace;
    // Cell edits not yet saved. Every other change is followed by a journal save, which also covers these.
    public DraftEdits Edits { get; } = new();
    public InteractionGate Gate => gate;
    // Raised before a save that may change rows, so views built from the old rows can be dropped.
    public event Action? DataInvalidated;
    // Raised when a workspace read starts (true) and when it has ended (false), on the thread that started it. The
    // read itself runs on a worker; see ReadAsync.
    public event Action<bool>? LoadingChanged;
    // Told the steps and row count of every read, on the worker that reads (the window shows them as progress).
    public IWorkspaceLoadObserver? LoadObserver { get; set; }

    internal IWorkspaceStorage Storage => storage;
    internal SemaphoreSlim SaveLock => saveLock;
    internal void ReplaceWorkspace(Workspace replacement) => workspace = replacement;

    public IReadOnlyList<WorkspaceListing> ListWorkspaces() => storage.List();

    // Opens the first stored workspace, creating one when the database is empty. A workspace never starts online:
    // the connection has to be made again. The database is read on a worker; see ReadAsync.
    public async Task OpenAsync(string defaultName)
    {
        using var loading = BeginLoad();
        var opened = await ReadAsync(observer =>
        {
            if (storage.List().Count == 0)
                storage.Save(new Workspace { Name = defaultName });
            return storage.Load(storage.List()[0].Id, observer);
        });
        if (opened.Mode == RunMode.Online)
            opened.Mode = RunMode.Offline;
        workspace = opened;
    }
    // The same, waiting for it: for tests and tools without a UI thread.
    public void Open(string defaultName) => OpenAsync(defaultName).GetAwaiter().GetResult();

    // A workspace read. It runs on a worker so the window keeps drawing (and shows the progress), while the caller's
    // load scope keeps everything that could change or save the current workspace waiting. It holds the save lock, so
    // no save or task checkpoint runs at the same time either. The store opens its own connection for the read and
    // touches nothing but the workspace it builds, which nobody else sees until the caller puts it in place on its own
    // thread. A failed read throws there and leaves the current workspace as it was.
    async Task<Workspace> ReadAsync(Func<IWorkspaceLoadObserver?, Workspace> read)
    {
        await saveLock.WaitAsync();
        try
        {
            var observer = LoadObserver;
            return await Task.Run(() => read(observer));
        }
        finally { saveLock.Release(); }
    }
    // Locks the window for a read (see InteractionGate.WorkspaceLoading) and tells it when the read starts and ends.
    IDisposable BeginLoad()
    {
        autosave.Stop();
        var scope = new LoadScope(this);
        try
        { LoadingChanged?.Invoke(true); }
        catch { scope.Dispose(); throw; }
        return scope;
    }
    sealed class LoadScope(WorkspaceSession owner) : IDisposable
    {
        IDisposable? scope = owner.gate.BeginWorkspaceLoad();
        public void Dispose()
        {
            if (Interlocked.Exchange(ref scope, null) is not { } ending)
                return;
            ending.Dispose();
            owner.LoadingChanged?.Invoke(false);
        }
    }

    // The journal save writes only what changed since the database last matched the workspace (nothing at all when
    // nothing changed). dataChanged: false only for saves that record jobs or connection state and leave every row
    // untouched; such saves pass a narrower scope, which leaves other pending changes (and the recorded cell edits)
    // for later.
    public async Task SaveAsync(bool dataChanged = true, JournalScope scope = JournalScope.All)
    {
        if (dataChanged)
            DataInvalidated?.Invoke();
        await saveLock.WaitAsync();
        var taken = scope == JournalScope.All ? Edits.Take() : null;
        try
        { gate.ThrowIfStorageBlocked(); await storage.SaveChangesAsync(workspace, scope); }
        catch { if (taken != null) Edits.Restore(taken); throw; }
        finally { saveLock.Release(); }
    }
    public Task SaveHeaderAsync() => SaveAsync(false, JournalScope.Header);
    public Task SaveJobsAsync() => SaveAsync(false, JournalScope.Jobs);

    // Autosave writes the edited rows and changed history steps, including same-cell continuations.
    public async Task SaveEditsAsync()
    {
        await saveLock.WaitAsync();
        var taken = Edits.Take();
        try
        {
            gate.ThrowIfStorageBlocked();
            if (!taken.CanSaveRows(workspace) || !await storage.TrySaveDraftRowsAsync(workspace, taken.Changes))
                await storage.SaveChangesAsync(workspace, JournalScope.All);
        }
        catch { Edits.Restore(taken); throw; }
        finally { saveLock.Release(); }
    }

    // The window is closing: save everything unless storage can no longer be trusted.
    public async Task SaveForCloseAsync()
    {
        if (!gate.StorageBlocked)
            await SaveAsync();
    }

    // A cell edit: remember the row for the next row save and save after a quiet period.
    public void RecordCellEdit(Workspace owner, ResourceKind kind, Guid row)
    {
        Edits.Cell(owner, kind, row);
        ScheduleAutosave();
    }
    // Rows replaced by an undo or redo are saved at once with the moved history step.
    public async Task SaveReplacedRowsAsync(ResourceKind kind, IReadOnlyCollection<Guid> rows)
    {
        Edits.Rows(workspace, kind, rows);
        autosave.Stop();
        await SaveEditsAsync();
    }

    public void ScheduleAutosave()
    {
        if (!gate.CanStartUserAction || gate.WorkspaceLoading)
            return;
        autosave.Stop();
        autosave.Start();
    }
    public void CancelAutosave() => autosave.Stop();
    // Stops the timer and tells whether a save was waiting for it.
    public bool TakeAutosavePending()
    {
        var pending = autosave.IsEnabled;
        autosave.Stop();
        return pending;
    }
    // Saves the edits a timer was still waiting to save.
    public async Task FlushAutosaveAsync()
    {
        if (TakeAutosavePending())
            await SaveEditsAsync();
    }
    // The quiet period ended. Never during a workspace read (the timer is stopped when one starts; this is the guard).
    public Task AutosaveElapsedAsync()
    {
        autosave.Stop();
        return gate.WorkspaceLoading ? Task.CompletedTask : SaveEditsAsync();
    }

    // Saves the current workspace and opens another; a failed save or read keeps the current one. The window stays
    // locked from the save to the end of the read.
    public async Task SwitchWorkspaceAsync(Guid id)
    {
        using var loading = BeginLoad();
        await SaveAsync();
        var opened = await ReadAsync(observer => storage.Load(id, observer));
        if (opened.Mode == RunMode.Online)
            opened.Mode = RunMode.Offline;
        workspace = opened;
    }
    // Saves the current workspace, makes the given one current and saves it, so it exists in the database.
    public async Task ReplaceWithNewAsync(Workspace created)
    {
        await SaveAsync();
        workspace = created;
        await SaveAsync();
    }

    // After a checkpoint could not be written the database decides: reload it, and when even that fails stop all saves.
    public async Task ReloadAfterCheckpointFailureAsync()
    {
        using var loading = BeginLoad();
        var id = workspace.Id;
        try
        {
            var reloaded = await ReadAsync(observer => storage.Load(id, observer));
            reloaded.Mode = RunMode.Offline;
            workspace = reloaded;
        }
        catch { gate.BlockStorage(); }
    }

    // If storage cannot be read, keep the job and block further saves until reload.
    public bool SavedJobIsDurable(BatchJob job)
    {
        try
        { return storage.JobExists(workspace.Id, job.Id); }
        catch { gate.BlockStorage(); return true; }
    }
    public BatchJob? LoadJob(Guid jobId) => storage.LoadJob(workspace.Id, jobId);

    // Writes checkpoints for the current workspace, one at a time with every other save.
    public IBatchCheckpointWriter CheckpointWriter() => new GatedCheckpointWriter(this, storage.CheckpointWriter(workspace));

    public async Task SaveConnectionSettingsAsync(string clientId, string secret, string url, bool localRouteTimes)
    {
        await saveLock.WaitAsync();
        try
        { storage.SaveConnectionSettings(workspace, clientId, secret, url, localRouteTimes); }
        finally { saveLock.Release(); }
    }
    public async Task UpdateCredentialsAsync(string clientId, string secret)
    {
        await saveLock.WaitAsync();
        try
        { storage.UpdateOperationsCredentials(workspace, clientId, secret); }
        finally { saveLock.Release(); }
    }
    // A refreshed snapshot staged by the server read replaces what the database holds.
    public async Task SaveStagedAsync(Workspace staged)
    {
        await saveLock.WaitAsync();
        try
        { await storage.SaveAsync(staged); }
        finally { saveLock.Release(); }
    }

    sealed class GatedCheckpointWriter(WorkspaceSession owner, IBatchCheckpointWriter inner) : IBatchCheckpointWriter
    {
        public async Task WriteAsync(BatchCheckpoint checkpoint, CancellationToken ct)
        {
            await owner.saveLock.WaitAsync(ct);
            try
            { owner.gate.ThrowIfStorageBlocked(); await inner.WriteAsync(checkpoint, ct); }
            finally { owner.saveLock.Release(); }
        }
    }
}
