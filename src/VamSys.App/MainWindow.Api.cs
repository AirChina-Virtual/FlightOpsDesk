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
    async Task RefreshApi()
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
                var parameters = await connection.RefreshAsync((kind, count) => Status(() => L("ApiLoading", ("resource", L(Schemas.All[kind].Name)), ("count", count))), running.Token);
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
        var content = Stack(Text(() => L(Schemas.All[kind].Name) + " · " + Schemas.Key(kind, row) + " · " + conflict.Field),
            Text(() => L("ApiConflictValues", ("old", conflict.Original ?? "∅"), ("local", conflict.Local ?? "∅"), ("remote", conflict.Remote ?? "∅"))));
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = L("ApiConflict"),
            Content = content,
            PrimaryButtonText = L("ApiKeepLocal"),
            SecondaryButtonText = L("ApiUseRemote"),
            CloseButtonText = L("Text_2CD0F3BE87"),
            DefaultButton = ContentDialogButton.Close,
            IsPrimaryButtonEnabled = prompt.CanKeepLocal
        };
        ContentDialogResult choice;
        using (gate.BeginDialog())
            choice = await dialog.ShowAsync();
        return choice == ContentDialogResult.None ? ConflictChoice.Stop : choice == ContentDialogResult.Secondary ? ConflictChoice.UseRemote : ConflictChoice.KeepLocal;
    }    string ApiReview(ChangeItem change)
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
            var id = new TextBox { Header = "Remote ID", Text = item.EditableRecoveryId ?? "" };
            var choice = await owner.Choose(owner.L("ApiUnknownReview"), Stack(owner.Text(() => owner.ApiReview(item)), owner.Text(() => owner.L("ApiRecoverId") + Help()), id), owner.L(online ? "ApiCheckRemote" : "ApiSaveCandidate"), notApplied);
            return choice == ContentDialogResult.Secondary ? new(null, true) : choice == ContentDialogResult.Primary ? new(id.Text, false) : RecoveryIdAnswer.Cancel;
        }
        public Task<bool> ConfirmManualDeleteAsync(ChangeItem item)
            => owner.Confirm(owner.L("ApiUnknownReview"), owner.Text(() => owner.L("ApiManualDelete", ("id", item.RemoteId ?? item.After.Identity?.RemoteId))), owner.L("ApiManualConfirm"));
        static string Differences(IReadOnlyList<ApiFieldDifference> differences) => string.Join("\n", differences.Select(d => $"{d.Field}: {d.Expected ?? "∅"} → {d.Actual ?? "∅"}"));
        public Task ShowStillReadableAsync(ChangeItem item, NoReceiptInspection review)
        {
            var id = item.RemoteId ?? item.After.Identity?.RemoteId;
            var differences = Differences(review.Differences);
            return owner.Confirm(owner.L("ApiUnknownReview"), owner.Text(() => owner.L("ApiNoReceiptStillReadable", ("id", id)) + "\n" + differences), owner.L("Text_3FD47EDCE4"));
        }
        public Task<bool> ConfirmNoReceiptAsync(ChangeItem item, NoReceiptInspection review, bool deleted)
        {
            var id = item.RemoteId ?? item.After.Identity?.RemoteId;
            return owner.Confirm(owner.L("ApiUnknownReview"), owner.Text(() => owner.L(deleted ? "ApiConfirmNoReceipt" : "ApiSoftDeleteUnchangedHelp", ("id", id))), owner.L(deleted ? "ApiManualConfirm" : "ApiMarkNotApplied"));
        }
        public Task ShowCannotAcceptAsync(ChangeItem item, ServerValueInspection review)
            => owner.Confirm(owner.L("ApiUnknownReview"), owner.Text(() => owner.L(review.HasCurrent ? "ApiAcceptNowMatches" : "ApiAcceptMissing") + "\n" + owner.ApiReview(item)), owner.L("Text_3FD47EDCE4"));
        public Task<bool> ConfirmAcceptAsync(ChangeItem item, ServerValueInspection review)
        {
            var differences = Differences(review.Differences);
            string Help() => item.Kind == ChangeKind.Delete ? owner.L("ApiAcceptDeleteHelp", ("id", review.CurrentRemoteId)) : owner.L("ApiAcceptServerHelp");
            return owner.Confirm(owner.L("ApiUnknownReview"), owner.Text(() => owner.ApiReview(item) + "\n" + Help() + "\n" + differences), owner.L("ApiAcceptServerValue"));
        }
        public async Task<bool> ShowUnknownAsync(ChangeItem item, bool canVerifyNotApplied)
        {
            if (canVerifyNotApplied)
                return await owner.Confirm(owner.L("ApiUnknownReview"), owner.Text(() => owner.L("ApiUnknown") + "\n" + owner.ApiReview(item) + "\n" + owner.L("ApiNotAppliedHelp")), owner.L("ApiVerifyNotApplied"));
            await owner.Confirm(owner.L("ApiUnknownReview"), owner.Text(() => owner.L("ApiUnknown") + "\n" + owner.ApiReview(item)), owner.L("Text_3FD47EDCE4"));
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