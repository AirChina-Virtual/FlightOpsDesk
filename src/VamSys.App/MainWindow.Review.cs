using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VamSys.Core;

namespace VamSys.App;

public sealed partial class MainWindow
{
    // The review page being shown, if any; it is rebuilt each time the page is opened.
    ReviewPage? shownReviewPage;
    IReadOnlyList<ReviewRowViewModel> reviewRows => shownReviewPage?.Model.Rows ?? [];
    WorkspaceReviewPlan? reviewPlan => reviews.Shown?.Plan;

    WorkspaceReviewPlan BuildReviewPlan() => reviews.Build();
    sealed class WindowWorkerLock(MainWindow owner) : IWorkerLock
    {
        public Task<T> RunAsync<T>(Func<T> work) => owner.WhileLocked(work);
    }
    // Locks edits and navigation while a worker reads the workspace. Nested locks keep the outermost
    // state: an enclosing LocalOperation stays locked when this one ends.
    async Task<T> WhileLocked<T>(Func<T> work)
    {
        var scope = gate.BeginReviewLock();
        ApplyShellState();
        if (scope.TookLocal)
            RefreshDataAvailability();
        try
        { return await Task.Run(work); }
        finally
        {
            scope.Dispose();
            ApplyShellState();
            if (scope.ReleasedLocal)
                RefreshDataAvailability();
        }
    }

    string ReviewChangeText(ChangeItem item) => ReviewTexts.ChangeText(PlannedChange.Wrap(item), localization);

    Task reviewLoad = Task.CompletedTask;
    // The page appears at once; the plan is built on a worker and the page filled when it is ready.
    UIElement BuildReviewPage()
    {
        var owner = workspace;
        var model = new ReviewViewModel(owner, localization);
        var page = shownReviewPage = new ReviewPage(model, new ReviewPageActions(
            Invoke: async action => { if (gate.CanInvokeCommand) await UserAction(action); },
            ShowIssues: async () => { await ConfirmList(L("ReviewIssues", ("count", model.AllBlockers.Count)), model.AllBlockers, L("Text_3FD47EDCE4")); },
            Submit: owner.Mode == RunMode.Demo ? StartDemo : StartApi,
            ResolveConflicts: ResolveWorkspaceConflicts));
        var generation = reviews.BeginLoad();
        var airline = owner.AirlineId;
        var mode = owner.Mode;
        reviewLoad = UserAction(async () =>
        {
            var plan = await WhileLocked(reviews.Capture());
            if (!reviews.IsCurrent(generation) || !ReferenceEquals(owner, workspace) || !ReferenceEquals(PageHost.Content, page))
                return;
            reviews.Show(plan, owner.Id, airline, mode);
            var session = mode == RunMode.Online ? online.Connected(owner.Id) : null;
            model.Show(plan, CanEditData(), gate.CanResolveConflicts, kind => UserAction(() => ExportCsv(kind)),
                session == null ? null : entry => session.DeniedFleets(entry.Change),
                session == null ? null : (entry, fleets) => UserAction(() => RemoveUnapprovedFleets(entry.Planned.After.LocalId, fleets)));
        });
        return page;
    }

    // 2.3 / 2.4: removes a route's unapproved fleets from its draft (one undoable edit) and shows the review again.
    async Task RemoveUnapprovedFleets(Guid localId, IReadOnlyList<string> fleets)
    {
        if (!gate.CanInvokeCommand || !CanEditData())
            return;
        var removed = await batch.RemoveRouteFleetsAsync(localId, fleets);
        reviews.Invalidate();
        if (shell.Page == ShellPage.Review)
            Render();
        else
            SelectPane(ShellPage.Review);
        Status(() => L("ReviewFleetsRemoved", ("count", removed)));
    }

    // 2.5: a route's fleets that are not approved for its prefixes, from the airline parameters already read. Empty when
    // offline, when the parameters are not known, or when a fleet cannot be resolved (the row checks report that).
    IReadOnlyList<string> UnapprovedFleets(DataRow route)
    {
        if (workspace.Mode != RunMode.Online || online.Connected(workspace.Id) is not { } session)
            return [];
        try
        { return session.DeniedFleets(new ChangeItem { Resource = ResourceKind.Routes, Kind = ChangeKind.Update, After = route }); }
        catch (InvalidOperationException) { return []; }
    }

    // The shown plan when the live workspace still yields the same review; otherwise the page is refreshed.
    async Task<WorkspaceReviewPlan?> CurrentReviewPlan(RunMode mode)
    {
        var check = await reviews.CheckCurrentAsync(mode, new WindowWorkerLock(this));
        if (check.Changed)
        {
            Render();
            Status(() => L("ReviewChanged"));
            return null;
        }
        if (check.Plan is null)
            Render();
        return check.Plan;
    }
    async Task<List<ChangeItem>?> ConfirmWorkspaceSubmission(RunMode mode)
    {
        if (await CurrentReviewPlan(mode) is not { } shown)
            return null;
        // Use the exact objects shown on the page, including their dependency IDs.
        var items = shown.SubmitItems;
        var submit = L(mode == RunMode.Demo ? "ReviewDemoSubmit" : "ReviewSubmit");
        if (!await ConfirmList(submit, items.Select(i => $"{L(Schemas.All[i.Resource].Name)} · {ReviewChangeText(i)}").ToArray(), submit,
            [L("ApiSubmitScope", ("va", workspace.Name), ("id", workspace.AirlineId ?? "?"), ("count", items.Count))], DialogHeight.ReviewDetail, selectable: false))
            return null;
        var deletes = items.Where(i => i.Kind == ChangeKind.Delete).ToArray();
        if (deletes.Length > 0 && !await ConfirmList(L("Text_5D6042B30E"), deletes.Select(i => $"{L(Schemas.All[i.Resource].Name)}: {Schemas.Key(i.Resource, i.After)}").ToArray(), L("Confirm"),
            [L("ApiDeleteConfirm", ("count", deletes.Length))]))
            return null;
        return items;
    }
}
