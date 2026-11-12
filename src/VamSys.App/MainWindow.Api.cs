using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VamSys.Core;

namespace VamSys.App;

public sealed partial class MainWindow
{
    async Task ConnectApi()
    {
        if (!gate.CanRequestOnline)
            return;
        await Work(async () =>
        {
            running = new();
            try
            {
                var parameters = await connection.ConnectAsync(running.Token);
                Status(() => L("ApiConnected", ("id", workspace.AirlineId ?? "?")) + (parameters ? "" : "\n" + L("ApiParametersUnavailable")));
            }
            finally { running.Dispose(); running = null; }
        });
    }
    Task RefreshApi() => RefreshApi(null);
    // E5: only the page's resource and what it refers to.
    Task RefreshResourceApi() => RefreshApi(resource);
    async Task RefreshApi(ResourceKind? only)
    {
        if (!gate.CanRequestOnline)
            return;
        if (!connection.CanRefresh())
        { SelectPane(ShellPage.Settings); Status(() => L("ApiConnectFirst")); return; }
        await Work(async () =>
        {
            running = new();
            try
            {
                var parameters = await connection.RefreshAsync((kind, count) => Status(() => L("ApiLoading", ("resource", L(Schemas.All[kind].Name)), ("count", count))), running.Token, only);
                Status(() => L("ApiRefreshed", ("count", workspace.Resources.Sum(p => p.Value.Conflicts.Count))) + (parameters ? "" : "\n" + L("ApiParametersUnavailable")));
            }
            finally { running.Dispose(); running = null; }
        });
    }
    Task ResolveApiConflicts() => ResolveConflicts([resource]);
    Task ResolveWorkspaceConflicts() => ResolveConflicts(Enum.GetValues<ResourceKind>());
    Task ResolveConflicts(IEnumerable<ResourceKind> kinds) => LocalOperation(() => conflicts.ResolveAsync(kinds, ChooseConflict));
    async Task<ConflictChoice> ChooseConflict(ConflictPrompt prompt)
    {
        var (kind, row, conflict) = (prompt.Kind, prompt.Row, prompt.Conflict);
        var choice = await Dialogs.ShowAsync(new MessageDialogViewModel
        {
            Title = L("ApiConflict"),
            Message = L(Schemas.All[kind].Name) + " · " + Schemas.Key(kind, row) + " · " + conflict.Field,
            Detail = L("ApiConflictValues", ("old", conflict.Original ?? "∅"), ("local", conflict.Local ?? "∅"), ("remote", conflict.Remote ?? "∅")),
            PrimaryLabel = L("ApiKeepLocal"),
            SecondaryLabel = L("ApiUseRemote"),
            CanPrimary = prompt.CanKeepLocal
        });
        return choice switch { DialogResult.Primary => ConflictChoice.KeepLocal, DialogResult.Secondary => ConflictChoice.UseRemote, _ => ConflictChoice.Stop };
    }
    string ApiReview(ChangeItem change)
    {
        var text = $"{L(Schemas.All[change.Resource].Name)} · {EnumText(change.Kind)} · {Schemas.Key(change.Resource, change.After)}\n" +
            string.Join("; ", change.Fields.Select(f => $"{ApiOperationPolicy.FieldLabel(f.Key, localization)}: {change.Before?.Get(f.Key) ?? "∅"} → {f.Value.Value}"));
        var plan = recovery.Describe(change);
        if (plan.Dependencies > 0)
            return text + "\n" + L("ApiDependent", ("count", plan.Dependencies));
        if (plan.Error is { } error)
            return text + "\n" + L(error);
        return text + "\n" + string.Join(" → ", plan.Steps);
    }
    Task StartApi() => SubmitWorkspace(RunMode.Online);
    async Task ExecuteApi(BatchJob job)
    {
        submission.RequireExecution(job, RunMode.Online, gate.CanRunTask);
        await Work(async () =>
        {
            running = new();
            try
            {
                SelectPane(ShellPage.Tasks);
                Render();
                await recovery.ExecuteAsync(job, NotifyTask, running.Token);
                Status(() => JobText(job));
                // 3.2: the sample limit held changes of the operations this task just proved; offer to continue with them.
                // Planned on a worker: the window is still locked by this work, so nothing edits the workspace meanwhile.
                var remaining = await Task.Run(() => reviews.RemainingAfter(job));
                if (remaining > 0 && Tasks.TryGet(job.Id, out var card))
                {
                    card.Remaining = remaining;
                    Status(() => L("TaskContinueReady", ("count", remaining)));
                }
            }
            finally
            {
                running.Dispose();
                running = null;
            }
        });
    }
    async Task ReviewUnknown(BatchJob job)
    {
        await recovery.ReviewUnknownAsync(job, gate.CanAct, new RecoveryDialogs(this), () => ExecuteApi(job));
        Render();
    }

    // The task recovery dialogs and the window state around the server reads they need.
    sealed class RecoveryDialogs(MainWindow owner) : IRecoveryUi
    {
        public Task RunReadAsync(Func<CancellationToken, Task> read) => owner.Work(async () =>
        {
            owner.running = new();
            try
            { await read(owner.running.Token); }
            finally { owner.running.Dispose(); owner.running = null; }
        });
        public void ItemChanged(BatchJob job, ChangeItem item) => owner.NotifyTask(new(owner.workspace.Id, job.Id, item.Id, true));
        public async Task<RecoveryIdAnswer> AskRecoveryIdAsync(ChangeItem item, bool online, bool canVerifyNotApplied)
        {
            var notApplied = canVerifyNotApplied ? owner.L("ApiVerifyNotApplied") : null;
            string Help() => notApplied == null ? "" : "\n" + owner.L("ApiNotAppliedHelp");
            // E1: records the program found but could not bind by itself are listed, and the first is filled in.
            var found = item.FoundCreateCandidates is { Count: > 0 } ids ? ids : null;
            var dialog = new TextInputDialogViewModel
            {
                Title = owner.L("ApiUnknownReview"),
                Paragraphs = found == null ? [owner.ApiReview(item), owner.L("ApiRecoverId") + Help()]
                    : [owner.ApiReview(item), owner.L("ApiCreateCandidateList", ("ids", string.Join(", ", found))), owner.L("ApiRecoverId") + Help()],
                InputHeader = "Remote ID",
                Text = item.EditableRecoveryId ?? found?[0] ?? "",
                PrimaryLabel = owner.L(online ? "ApiCheckRemote" : "ApiSaveCandidate"),
                SecondaryLabel = notApplied
            };
            return await owner.Dialogs.ShowAsync(dialog) switch
            {
                DialogResult.Secondary => new(null, true),
                DialogResult.Primary => new(dialog.Text, false),
                _ => RecoveryIdAnswer.Cancel
            };
        }
        // 1.6: one dialog for a whole group: what the situation is, one line per item (with the differences a read showed),
        // and "confirm all" or "one by one".
        public async Task<RecoveryGroupAnswer> ConfirmGroupAsync(RecoveryGroup group, IReadOnlyList<RecoveryGroupEntry> entries)
        {
            var dialog = new ListDialogViewModel
            {
                Title = owner.L("ApiUnknownReview"),
                Header = [owner.L("RecoveryGroup_" + group, ("count", entries.Count))],
                Items = entries.Select(e => $"{owner.L(Schemas.All[e.Item.Resource].Name)} · {Schemas.Key(e.Item.Resource, e.Item.After)}"
                    + (e.Differences.Count == 0 ? "" : "\n" + Differences(e.Differences))).ToList(),
                PrimaryLabel = owner.L(group == RecoveryGroup.VerifyNotApplied ? "RecoveryGroupVerifyAll" : "RecoveryGroupConfirmAll", ("count", entries.Count)),
                SecondaryLabel = owner.L("RecoveryGroupOneByOne"),
                Height = DialogHeight.ReviewDetail,
                Selectable = false
            };
            return await owner.Dialogs.ShowAsync(dialog) switch
            {
                DialogResult.Primary => RecoveryGroupAnswer.All,
                DialogResult.Secondary => RecoveryGroupAnswer.OneByOne,
                _ => RecoveryGroupAnswer.Cancel
            };
        }
        public void ReportGroup(RecoveryGroup group, int done, int total)
            => owner.Status(() => owner.L(group == RecoveryGroup.VerifyNotApplied ? "RecoveryGroupVerified" : "RecoveryGroupDone", ("done", done), ("total", total)));
        public Task<bool> ConfirmManualDeleteAsync(ChangeItem item)
            => owner.ConfirmMessage(owner.L("ApiUnknownReview"), owner.L("ApiManualDelete", ("id", item.RemoteId ?? item.After.Identity?.RemoteId)), owner.L("ApiManualConfirm"));
        static string Differences(IReadOnlyList<ApiFieldDifference> differences) => string.Join("\n", differences.Select(d => $"{d.Field}: {d.Expected ?? "∅"} → {d.Actual ?? "∅"}"));
        public Task ShowStillReadableAsync(ChangeItem item, NoReceiptInspection review)
        {
            var id = item.RemoteId ?? item.After.Identity?.RemoteId;
            var differences = Differences(review.Differences);
            return owner.ConfirmMessage(owner.L("ApiUnknownReview"), owner.L("ApiNoReceiptStillReadable", ("id", id)) + "\n" + differences, owner.L("Text_3FD47EDCE4"));
        }
        public Task<bool> ConfirmNoReceiptAsync(ChangeItem item, NoReceiptInspection review, bool deleted)
        {
            var id = item.RemoteId ?? item.After.Identity?.RemoteId;
            return owner.ConfirmMessage(owner.L("ApiUnknownReview"), owner.L(deleted ? "ApiConfirmNoReceipt" : "ApiSoftDeleteUnchangedHelp", ("id", id)), owner.L(deleted ? "ApiManualConfirm" : "ApiMarkNotApplied"));
        }
        public Task ShowCannotAcceptAsync(ChangeItem item, ServerValueInspection review)
            => owner.ConfirmMessage(owner.L("ApiUnknownReview"), owner.L(review.HasCurrent ? "ApiAcceptNowMatches" : "ApiAcceptMissing") + "\n" + owner.ApiReview(item), owner.L("Text_3FD47EDCE4"));
        public Task<bool> ConfirmAcceptAsync(ChangeItem item, ServerValueInspection review)
        {
            var differences = Differences(review.Differences);
            string Help() => item.Kind == ChangeKind.Delete ? owner.L("ApiAcceptDeleteHelp", ("id", review.CurrentRemoteId)) : owner.L("ApiAcceptServerHelp");
            return owner.ConfirmMessage(owner.L("ApiUnknownReview"), owner.ApiReview(item) + "\n" + Help() + "\n" + differences, owner.L("ApiAcceptServerValue"));
        }
        public async Task<bool> ShowUnknownAsync(ChangeItem item, bool canVerifyNotApplied)
        {
            if (canVerifyNotApplied)
                return await owner.ConfirmMessage(owner.L("ApiUnknownReview"), owner.L("ApiUnknown") + "\n" + owner.ApiReview(item) + "\n" + owner.L("ApiNotAppliedHelp"), owner.L("ApiVerifyNotApplied"));
            await owner.ConfirmMessage(owner.L("ApiUnknownReview"), owner.L("ApiUnknown") + "\n" + owner.ApiReview(item), owner.L("Text_3FD47EDCE4"));
            return false;
        }
        public void Report(RecoveryOutcome outcome)
        {
            if (outcome == RecoveryOutcome.CandidateSaved)
                owner.Status(() => owner.L("ApiCandidateSaved"));
            else if (outcome == RecoveryOutcome.NotAppliedVerified || outcome == RecoveryOutcome.NotAppliedUnproven)
                owner.Status(() => owner.L(outcome == RecoveryOutcome.NotAppliedVerified ? "ApiNotAppliedVerified" : "ApiNotAppliedUnproven"));
            else if (outcome == RecoveryOutcome.DeletedWithoutReceipt || outcome == RecoveryOutcome.UnchangedWithoutReceipt)
                owner.Status(() => owner.L(outcome == RecoveryOutcome.DeletedWithoutReceipt ? "ApiManualDeleteNoReceipt" : "ApiSoftDeleteUnchanged"));
            else if (outcome == RecoveryOutcome.ServerRecordAccepted || outcome == RecoveryOutcome.ServerValueAccepted)
                owner.Status(() => owner.L(outcome == RecoveryOutcome.ServerRecordAccepted ? "ApiAcceptedServerRecord" : "ApiAcceptedServerValue"));
        }
    }
}