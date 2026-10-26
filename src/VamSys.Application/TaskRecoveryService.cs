using System.Text.Json;

namespace VamSys.Core;

// What a person answered when asked for the ID of a record a create may have made.
// Id set: check that ID (online) or keep it as a candidate (offline); VerifyNotApplied: prove by reading that the write
// did not happen; neither: closed without a decision.
public sealed record RecoveryIdAnswer(string? Id, bool VerifyNotApplied)
{
    public static readonly RecoveryIdAnswer Cancel = new(null, false);
}

// How a plan of requests reads for display: a change that depends on others shows only their number, a change that
// cannot be planned shows why.
public sealed record PlanDescription(int Dependencies, IReadOnlyList<string> Steps, MessageDescriptor? Error);

// What reviewing one Unknown item ended with. The window shows the matching message; Recover asks for the online task
// to be read back once all recovery IDs are saved.
public sealed class RecoveryOutcome
{
    RecoveryOutcome(string name, bool recover = false) { Name = name; Recover = recover; }
    public string Name { get; }
    public bool Recover { get; }
    public static readonly RecoveryOutcome Nothing = new("Nothing");
    public static readonly RecoveryOutcome RecoverNow = new("RecoverNow", true);
    public static readonly RecoveryOutcome CandidateSaved = new("CandidateSaved");
    public static readonly RecoveryOutcome NotAppliedVerified = new("NotAppliedVerified");
    public static readonly RecoveryOutcome NotAppliedUnproven = new("NotAppliedUnproven");
    public static readonly RecoveryOutcome DeletedWithoutReceipt = new("DeletedWithoutReceipt");
    public static readonly RecoveryOutcome UnchangedWithoutReceipt = new("UnchangedWithoutReceipt");
    public static readonly RecoveryOutcome ServerRecordAccepted = new("ServerRecordAccepted");
    public static readonly RecoveryOutcome ServerValueAccepted = new("ServerValueAccepted");
}

// The window's side of a recovery: dialogs, the exclusive-work state around server reads, and the refresh of a changed item.
public interface IRecoveryUi
{
    // Runs a read-only server call while the window is busy; the call does not run when the window refuses to start work.
    Task RunReadAsync(Func<CancellationToken, Task> read);
    // A stored item changed; the task page shows its saved state at once.
    void ItemChanged(BatchJob job, ChangeItem item);
    Task<RecoveryIdAnswer> AskRecoveryIdAsync(ChangeItem item, bool online, bool canVerifyNotApplied);
    Task<bool> ConfirmManualDeleteAsync(ChangeItem item);
    Task ShowStillReadableAsync(ChangeItem item, NoReceiptInspection review);
    Task<bool> ConfirmNoReceiptAsync(ChangeItem item, NoReceiptInspection review, bool deleted);
    Task ShowCannotAcceptAsync(ChangeItem item, ServerValueInspection review);
    Task<bool> ConfirmAcceptAsync(ChangeItem item, ServerValueInspection review);
    // Shows an Unknown item nothing can resolve automatically; returns true when the person asks to prove it was not applied.
    Task<bool> ShowUnknownAsync(ChangeItem item, bool canVerifyNotApplied);
    void Report(RecoveryOutcome outcome);
}

// Running online tasks and every way out of an Unknown item: continue, drop never-sent items, read a deletion back,
// prove a write did not happen, accept what the server holds now. Nothing here ever resends a write. When a
// checkpoint cannot be written the database decides: the workspace is reloaded and the session dropped.
public sealed class TaskRecoveryService(WorkspaceSession session, IOnlineGateway online)
{
    void ReloadAfterCheckpointFailure()
    {
        online.Release(session.Workspace.Id);
        session.ReloadAfterCheckpointFailure();
    }
    static ChangeItem Clone(ChangeItem item) => JsonSerializer.Deserialize<ChangeItem>(JsonSerializer.Serialize(item))!;

    public PlanDescription Describe(ChangeItem change)
    {
        try
        {
            if (change.Dependencies.Count > 0)
                return new(change.Dependencies.Count, [], null);
            return new(0, online.Session(session.Workspace).DescribePlan(change), null);
        }
        catch (Exception e) { return new(0, [], MessageErrors.Describe(e)); }
    }

    // Sends the task's pending items; Unknown items are only read. Progress is reported after each saved step.
    public async Task ExecuteAsync(BatchJob job, Action<BatchProgress> progress, CancellationToken ct)
    {
        try
        { await online.Session(session.Workspace).ExecuteAsync(job, session.CheckpointWriter(), ct, progress); }
        catch (PersistenceException)
        {
            ReloadAfterCheckpointFailure();
            throw;
        }
    }

    // Read-only check of deletions: Unknown or manually confirmed items are looked up, Pending items are not sent.
    public async Task VerifyDeletionsAsync(BatchJob job, Action<BatchProgress> progress, CancellationToken ct)
    {
        try
        { await online.Session(session.Workspace).VerifyDeletionsAsync(job, session.CheckpointWriter(), ct, progress); }
        catch (PersistenceException)
        {
            ReloadAfterCheckpointFailure();
            throw;
        }
    }

    // Local only: never-sent items of an online task can be dropped; they are saved as Failed before the task changes in memory.
    public IReadOnlyList<Guid> DiscardableItems(BatchJob job)
        => job.Mode != RunMode.Online ? [] : job.Items.Where(ApiOperationPolicy.CanDiscardUnsent).Select(i => i.Id).ToList();
    public async Task<int> DiscardUnsentAsync(BatchJob job)
    {
        try
        { return await online.DiscardUnsentAsync(session.Workspace, job, session.CheckpointWriter()); }
        catch (PersistenceException)
        {
            ReloadAfterCheckpointFailure();
            throw;
        }
    }

    // Walks the task's Unknown items one by one. Item IDs are captured once; each step re-reads the job, so items an
    // earlier step resolved or replaced are skipped or reviewed in their current form. The online task is read back once,
    // after all recovery IDs are saved, instead of once per proposal.
    public async Task ReviewUnknownAsync(BatchJob job, bool available, IRecoveryUi ui, Func<Task> recover)
    {
        if (!available || job.Mode != RunMode.Online || session.Workspace.Mode == RunMode.Demo)
            return;
        await UnknownReview.RunAsync(job, async item =>
        {
            var outcome = await ReviewItemAsync(job, item, ui);
            ui.Report(outcome);
            return outcome.Recover;
        }, recover);
    }

    // Which way out an item has depends on what is known about its write.
    public async Task<RecoveryOutcome> ReviewItemAsync(BatchJob job, ChangeItem item, IRecoveryUi ui)
    {
        var workspace = session.Workspace;
        var isOnline = workspace.Mode == RunMode.Online;
        var canVerify = isOnline && online.CanVerifyNotApplied(item);
        if (item.CanProposeRecoveryId)
        {
            var answer = await ui.AskRecoveryIdAsync(item, isOnline, canVerify);
            if (answer.VerifyNotApplied)
                return await VerifyNotAppliedAsync(job, item, ui);
            if (answer.Id is null)
                return RecoveryOutcome.Nothing;
            var candidate = Clone(item);
            candidate.ProposeRecoveryId(answer.Id);
            await session.CheckpointWriter().WriteAsync(new(CheckpointKind.Task, job, candidate), CancellationToken.None);
            job.Items[job.Items.IndexOf(item)] = candidate;
            ui.ItemChanged(job, candidate);
            return isOnline ? RecoveryOutcome.RecoverNow : RecoveryOutcome.CandidateSaved;
        }
        if (isOnline && item.Kind == ChangeKind.Delete && item.WriteAccepted && item.Resource is ResourceKind.Airports or ResourceKind.Routes)
        {
            if (!await ui.ConfirmManualDeleteAsync(item))
                return RecoveryOutcome.Nothing;
            try
            { await online.Session(workspace).ConfirmManualDeleteAsync(job, item, session.CheckpointWriter()); }
            catch (PersistenceException) { ReloadAfterCheckpointFailure(); throw; }
            ui.ItemChanged(job, item);
            return RecoveryOutcome.Nothing;
        }
        if (isOnline && online.CanConfirmWithoutReceipt(item))
            return await ReviewDeleteWithoutReceiptAsync(job, item, ui);
        if (isOnline && online.CanAcceptServerValue(item))
            return await AcceptServerValueAsync(job, item, ui);
        if (await ui.ShowUnknownAsync(item, canVerify) && canVerify)
            return await VerifyNotAppliedAsync(job, item, ui);
        return RecoveryOutcome.Nothing;
    }

    // A soft delete without a write receipt: GET once, then let the user confirm what the read shows.
    // 404 confirms the deletion; a record that reads exactly as before the change can be marked not
    // applied after a reminder; any other record stays Unknown. Nothing is ever resent.
    async Task<RecoveryOutcome> ReviewDeleteWithoutReceiptAsync(BatchJob job, ChangeItem item, IRecoveryUi ui)
    {
        var workspace = session.Workspace;
        NoReceiptInspection? review = null;
        await ui.RunReadAsync(async ct => review = await online.Session(workspace).InspectWithoutReceiptAsync(job, item, ct));
        if (review == null)
            return RecoveryOutcome.Nothing;
        if (!review.CanConfirmDeleted && !review.CanMarkUnchanged)
        {
            await ui.ShowStillReadableAsync(item, review);
            return RecoveryOutcome.Nothing;
        }
        var deleted = review.CanConfirmDeleted;
        if (!await ui.ConfirmNoReceiptAsync(item, review, deleted))
            return RecoveryOutcome.Nothing;
        var api = online.Session(workspace);
        var writer = session.CheckpointWriter();
        try
        { if (deleted) await api.ConfirmWithoutReceiptAsync(job, review, writer); else await api.MarkUnchangedAsync(job, review, writer); }
        catch (PersistenceException) { ReloadAfterCheckpointFailure(); throw; }
        ui.ItemChanged(job, item);
        return deleted ? RecoveryOutcome.DeletedWithoutReceipt : RecoveryOutcome.UnchangedWithoutReceipt;
    }

    // Read-only: GET the record and show every difference; only an explicit confirmation saves the
    // server's current value as manually confirmed. Nothing is ever resent.
    async Task<RecoveryOutcome> AcceptServerValueAsync(BatchJob job, ChangeItem item, IRecoveryUi ui)
    {
        var workspace = session.Workspace;
        ServerValueInspection? review = null;
        await ui.RunReadAsync(async ct => review = await online.Session(workspace).InspectServerValueAsync(job, item, ct));
        if (review == null)
            return RecoveryOutcome.Nothing;
        if (!review.CanAccept)
        {
            await ui.ShowCannotAcceptAsync(item, review);
            return RecoveryOutcome.Nothing;
        }
        if (!await ui.ConfirmAcceptAsync(item, review))
            return RecoveryOutcome.Nothing;
        try
        { await online.Session(workspace).AcceptServerValueAsync(job, review, session.CheckpointWriter()); }
        catch (PersistenceException) { ReloadAfterCheckpointFailure(); throw; }
        ui.ItemChanged(job, item);
        return item.Kind == ChangeKind.Delete ? RecoveryOutcome.ServerRecordAccepted : RecoveryOutcome.ServerValueAccepted;
    }

    // Read-only: a proven item becomes Failed and keeps its draft; otherwise nothing changes.
    async Task<RecoveryOutcome> VerifyNotAppliedAsync(BatchJob job, ChangeItem item, IRecoveryUi ui)
    {
        var workspace = session.Workspace;
        bool? proven = null;
        await ui.RunReadAsync(async ct =>
        {
            try
            { proven = await online.Session(workspace).ResolveNotAppliedAsync(job, item, session.CheckpointWriter(), ct); }
            catch (PersistenceException) { ReloadAfterCheckpointFailure(); throw; }
        });
        if (proven is not bool result)
            return RecoveryOutcome.Nothing;
        ui.ItemChanged(job, item);
        return result ? RecoveryOutcome.NotAppliedVerified : RecoveryOutcome.NotAppliedUnproven;
    }
}
