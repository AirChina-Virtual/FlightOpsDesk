using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Text.Json;
using VamSys.Core;
using VamSys.Infrastructure;

namespace VamSys.App;

public sealed partial class MainWindow
{
    readonly Dictionary<Guid, (HttpClient Client, OperationsAdapter Api)> apiSessions = [];
    OperationsAdapter Api()
    {
        if (apiSessions.TryGetValue(workspace.Id, out var session))
            return session.Api;
        var raw = storage.LoadSecret(workspace.Id) ?? throw OperationsAdapter.Block("ApiConnectFirst");
        var credentials = JsonSerializer.Deserialize<OperationsCredentials>(raw) ?? throw OperationsAdapter.Block("ApiConnectFirst");
        var client = OperationsTransport.CreateHttpClient();
        // Conservative global pool also covers clients whose airline is not identified yet.
        var transport = new OperationsTransport(client, OperationsAdapter.BaseUri, "operations-shared", TokenProvider.ClientCredentials(client, credentials));
        var api = new OperationsAdapter(transport, workspace);
        apiSessions[workspace.Id] = (client, api);
        return api;
    }
    async Task ConnectApi()
    {
        if (!gate.CanRequestOnline)
            return;
        await Work(async () =>
        {
            running = new();
            try
            {
                // A documented list call checks the token, permissions and airline identity.
                if (!await Api().IdentifyAirlineAsync(running.Token))
                    throw OperationsAdapter.Block("ApiConnectFirst");
                workspace.ConnectedAt = DateTimeOffset.UtcNow;
                workspace.Mode = RunMode.Online;
                if (workspace.ContractHash != ApiContracts.Sha256)
                    workspace.VerifiedOperations.Clear();
                workspace.ContractHash = ApiContracts.Sha256;
                await Save(dataChanged: false, JournalScope.Header);
                var parameters = await ReadOptionalParameters(running.Token);
                Status(() => L("ApiConnected", ("id", workspace.AirlineId ?? "?")) + (parameters ? "" : "\n" + L("ApiParametersUnavailable")));
            }
            finally { running.Dispose(); running = null; }
        });
    }
    async Task RefreshApi()
    {
        if (!gate.CanRequestOnline)
            return;
        if (workspace.Mode != RunMode.Online)
        { Navigation.SelectedItem = Navigation.MenuItems[5]; Status(() => L("ApiConnectFirst")); return; }
        if (ApiOperationPolicy.HasOpenOnlineItems(workspace))
            throw OperationsAdapter.Block("ApiPendingJob");
        await Work(async () =>
        {
            running = new();
            try
            {
                var api = Api();
                await api.RefreshSnapshotsAsync(async staged =>
                {
                    await session.SaveStagedAsync(staged);
                }, running.Token, (kind, count) => Status(() => L("ApiLoading", ("resource", L(Schemas.All[kind].Name)), ("count", count))));
                var parameters = await ReadOptionalParameters(running.Token);
                Status(() => L("ApiRefreshed", ("count", workspace.Resources.Sum(p => p.Value.Conflicts.Count))) + (parameters ? "" : "\n" + L("ApiParametersUnavailable")));
            }
            finally { running.Dispose(); running = null; }
        });
    }
    Task ResolveApiConflicts() => ResolveConflicts([resource]);
    Task ResolveWorkspaceConflicts() => ResolveConflicts(Enum.GetValues<ResourceKind>());
    // Every choice is applied in memory at once; the changes are saved a single time when the dialogs end or are cancelled.
    Task ResolveConflicts(IEnumerable<ResourceKind> kinds) => LocalOperation(async () =>
    {
        var changed = false;
        try
        { await ResolveConflictsInMemory(kinds, () => changed = true); }
        finally
        { if (changed) await Save(); }
    });
    async Task ResolveConflictsInMemory(IEnumerable<ResourceKind> kinds, Action markChanged)
    {
        foreach (var kind in kinds)
        {
            var data = workspace.Resources[kind];
            foreach (var conflict in data.Conflicts.ToArray())
            {
                var row = data.FindDraftRow(conflict.RowId);
                if (row is null)
                { data.Conflicts.Remove(conflict); markChanged(); continue; }
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
                    IsPrimaryButtonEnabled = conflict.Field != "*"
                };
                ContentDialogResult choice;
                using (gate.BeginDialog())
                    choice = await dialog.ShowAsync();
                if (choice == ContentDialogResult.None)
                    return;
                if (choice == ContentDialogResult.Secondary)
                {
                    var replacement = conflict.Field != "*" ? row.With(conflict.Field, conflict.Remote ?? "") : row;
                    data.Edit([row.LocalId], () => { if (conflict.Field == "*") data.Draft.Remove(row); else data.ReplaceDraftRows([replacement]); }, newStep: false, structural: conflict.Field == "*");
                }
                data.Conflicts.Remove(conflict);
                markChanged();
            }
        }
    }
    string ApiReview(ChangeItem change)
    {
        var text = $"{L(Schemas.All[change.Resource].Name)} · {EnumText(change.Kind)} · {Schemas.Key(change.Resource, change.After)}\n" +
            string.Join("; ", change.Fields.Select(f => $"{ApiOperationPolicy.FieldLabel(f.Key, localization)}: {change.Before?.Get(f.Key) ?? "∅"} → {f.Value.Value}"));
        try
        {
            if (change.Dependencies.Count > 0)
                return text + "\n" + L("ApiDependent", ("count", change.Dependencies.Count));
            return text + "\n" + string.Join(" → ", Api().Plan(change).Select(s => $"{s.Method} {s.Path}"));
        }
        catch (Exception e) { return text + "\n" + L(MessageErrors.Describe(e)); }
    }
    Task StartApi() => SubmitWorkspace(RunMode.Online);
    async Task ExecuteApi(BatchJob job)
    {
        TaskSubmission.RequireExecution(workspace, job, RunMode.Online,
            gate.CanRunTask);
        await Work(async () =>
        {
            running = new();
            using var performance = PerformanceRun.Begin(workspace.Id, job.Id);
            using var cancellation = running.Token.Register(performance.MarkCancellation);
            var outcome = "Failed";
            try
            {
                Navigation.SelectedItem = Navigation.MenuItems[4];
                Render();
                await new OperationsBatchExecutor(Api(), workspace).ExecuteAsync(job, session.CheckpointWriter(), running.Token, progress: NotifyTask);
                outcome = job.StatusCode.ToString();
                Status(() => JobText(job));
            }
            catch (PersistenceException)
            {
                ReloadAfterCheckpointFailure();
                throw;
            }
            finally
            {
                performance.Finish(outcome);
                var directory = AppPaths.DataDirectory;
                performance.TryWrite(Path.Combine(directory, "performance"));
                running.Dispose();
                running = null;
            }
        });
    }
    void ReloadAfterCheckpointFailure()
    {
        if (apiSessions.Remove(workspace.Id, out var failed))
            failed.Client.Dispose();
        session.ReloadAfterCheckpointFailure();
    }
    async Task ReviewUnknown(BatchJob job)
    {
        if (!gate.CanAct || job.Mode != RunMode.Online || workspace.Mode == RunMode.Demo)
            return;
        await UnknownReview.RunAsync(job, item => ReviewUnknownItem(job, item), () => ExecuteApi(job));
        Render();
    }
    // Returns true when a recovery ID was saved and the online job should be read back.
    async Task<bool> ReviewUnknownItem(BatchJob job, ChangeItem item)
    {
        var notApplied = workspace.Mode == RunMode.Online && OperationsUnknownResolver.CanCheck(item) ? L("ApiVerifyNotApplied") : null;
        string Help() => notApplied == null ? "" : "\n" + L("ApiNotAppliedHelp");
        if (item.CanProposeRecoveryId)
        {
            var id = new TextBox { Header = "Remote ID", Text = item.EditableRecoveryId ?? "" };
            var choice = await Choose(L("ApiUnknownReview"), Stack(Text(() => ApiReview(item)), Text(() => L("ApiRecoverId") + Help()), id), L(workspace.Mode == RunMode.Online ? "ApiCheckRemote" : "ApiSaveCandidate"), notApplied);
            if (choice == ContentDialogResult.Secondary)
            { await VerifyNotApplied(job, item); return false; }
            if (choice != ContentDialogResult.Primary)
                return false;
            var candidate = JsonSerializer.Deserialize<ChangeItem>(JsonSerializer.Serialize(item))!;
            candidate.ProposeRecoveryId(id.Text);
            await session.CheckpointWriter().WriteAsync(new(CheckpointKind.Task, job, candidate), CancellationToken.None);
            job.Items[job.Items.IndexOf(item)] = candidate;
            NotifyTask(new(workspace.Id, job.Id, candidate.Id, true));
            if (workspace.Mode == RunMode.Online)
                return true;
            Status(() => L("ApiCandidateSaved"));
            return false;
        }
        if (workspace.Mode == RunMode.Online && item.Kind == ChangeKind.Delete && item.WriteAccepted && item.Resource is ResourceKind.Airports or ResourceKind.Routes)
        {
            if (!await Confirm(L("ApiUnknownReview"), Text(() => L("ApiManualDelete", ("id", item.RemoteId ?? item.After.Identity?.RemoteId))), L("ApiManualConfirm")))
                return false;
            try
            { await new OperationsDeletionVerifier(Api(), workspace).ConfirmManuallyAsync(job, item, session.CheckpointWriter(), CancellationToken.None); }
            catch (PersistenceException) { ReloadAfterCheckpointFailure(); throw; }
            NotifyTask(new(workspace.Id, job.Id, item.Id, true));
            return false;
        }
        if (workspace.Mode == RunMode.Online && OperationsDeletionVerifier.CanConfirmWithoutReceipt(item))
        {
            await ReviewDeleteWithoutReceipt(job, item);
            return false;
        }
        if (workspace.Mode == RunMode.Online && OperationsServerValueResolver.CanAccept(item))
        {
            await AcceptServerValue(job, item);
            return false;
        }
        if (notApplied != null)
        {
            if (await Confirm(L("ApiUnknownReview"), Text(() => L("ApiUnknown") + "\n" + ApiReview(item) + Help()), notApplied))
                await VerifyNotApplied(job, item);
            return false;
        }
        await Confirm(L("ApiUnknownReview"), Text(() => L("ApiUnknown") + "\n" + ApiReview(item)), L("Text_3FD47EDCE4"));
        return false;
    }
    // A soft delete without a write receipt: GET once, then let the user confirm what the read shows.
    // 404 confirms the deletion; a record that reads exactly as before the change can be marked not
    // applied after a reminder; any other record stays Unknown. Nothing is ever resent.
    async Task ReviewDeleteWithoutReceipt(BatchJob job, ChangeItem item)
    {
        var verifier = new OperationsDeletionVerifier(Api(), workspace);
        NoReceiptReview? review = null;
        await Work(async () =>
        {
            running = new();
            try
            { review = await verifier.InspectWithoutReceiptAsync(job, item, running.Token); }
            finally { running.Dispose(); running = null; }
        });
        if (review == null)
            return;
        var id = item.RemoteId ?? item.After.Identity?.RemoteId;
        if (!review.CanConfirmDeleted && !review.CanMarkUnchanged)
        {
            var differences = string.Join("\n", review.Differences.Select(d => $"{d.Field}: {d.Expected ?? "∅"} → {d.Actual ?? "∅"}"));
            await Confirm(L("ApiUnknownReview"), Text(() => L("ApiNoReceiptStillReadable", ("id", id)) + "\n" + differences), L("Text_3FD47EDCE4"));
            return;
        }
        var deleted = review.CanConfirmDeleted;
        if (!await Confirm(L("ApiUnknownReview"), Text(() => L(deleted ? "ApiConfirmNoReceipt" : "ApiSoftDeleteUnchangedHelp", ("id", id))), L(deleted ? "ApiManualConfirm" : "ApiMarkNotApplied")))
            return;
        var writer = session.CheckpointWriter();
        try
        { if (deleted) await verifier.ConfirmWithoutReceiptAsync(job, review, writer); else await verifier.MarkUnchangedAsync(job, review, writer); }
        catch (PersistenceException) { ReloadAfterCheckpointFailure(); throw; }
        NotifyTask(new(workspace.Id, job.Id, item.Id, true));
        Status(() => L(deleted ? "ApiManualDeleteNoReceipt" : "ApiSoftDeleteUnchanged"));
    }
    // Read-only: GET the record and show every difference; only an explicit confirmation saves the
    // server's current value as manually confirmed. Nothing is ever resent.
    async Task AcceptServerValue(BatchJob job, ChangeItem item)
    {
        var resolver = new OperationsServerValueResolver(Api(), workspace);
        ServerValueReview? review = null;
        await Work(async () =>
        {
            running = new();
            try
            { review = await resolver.InspectAsync(job, item, running.Token); }
            finally { running.Dispose(); running = null; }
        });
        if (review == null)
            return;
        if (!review.CanAccept)
        {
            await Confirm(L("ApiUnknownReview"), Text(() => L(review.Current == null ? "ApiAcceptMissing" : "ApiAcceptNowMatches") + "\n" + ApiReview(item)), L("Text_3FD47EDCE4"));
            return;
        }
        var differences = string.Join("\n", review.Differences.Select(d => $"{d.Field}: {d.Expected ?? "∅"} → {d.Actual ?? "∅"}"));
        string Help() => item.Kind == ChangeKind.Delete ? L("ApiAcceptDeleteHelp", ("id", review.Current!.Identity!.RemoteId)) : L("ApiAcceptServerHelp");
        if (!await Confirm(L("ApiUnknownReview"), Text(() => ApiReview(item) + "\n" + Help() + "\n" + differences), L("ApiAcceptServerValue")))
            return;
        try
        { await resolver.AcceptAsync(job, review, session.CheckpointWriter()); }
        catch (PersistenceException) { ReloadAfterCheckpointFailure(); throw; }
        NotifyTask(new(workspace.Id, job.Id, item.Id, true));
        Status(() => L(item.Kind == ChangeKind.Delete ? "ApiAcceptedServerRecord" : "ApiAcceptedServerValue"));
    }
    // Read-only: a proven item becomes Failed and keeps its draft; otherwise nothing changes.
    async Task VerifyNotApplied(BatchJob job, ChangeItem item)
    {
        bool? proven = null;
        await Work(async () =>
        {
            running = new();
            try
            { proven = await new OperationsUnknownResolver(Api(), workspace).ResolveNotAppliedAsync(job, item, session.CheckpointWriter(), running.Token); }
            catch (PersistenceException) { ReloadAfterCheckpointFailure(); throw; }
            finally { running.Dispose(); running = null; }
        });
        if (proven is not bool result)
            return;
        NotifyTask(new(workspace.Id, job.Id, item.Id, true));
        Status(() => L(result ? "ApiNotAppliedVerified" : "ApiNotAppliedUnproven"));
    }
}
