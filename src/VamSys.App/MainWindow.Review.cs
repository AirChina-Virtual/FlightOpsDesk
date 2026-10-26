using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Markup;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VamSys.Core;

namespace VamSys.App;

public sealed partial class MainWindow
{
    IReadOnlyList<ReviewDisplayRow> reviewRows = [];
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
        if (scope.TookLocal)
        {
            Navigation.IsEnabled = WorkspacePicker.IsEnabled = ResourcePicker.IsEnabled = PageHost.IsEnabled = false;
            BusyRing.IsActive = true;
            RefreshDataAvailability();
        }
        try
        { return await Task.Run(work); }
        finally
        {
            scope.Dispose();
            if (scope.ReleasedLocal)
            {
                Navigation.IsEnabled = WorkspacePicker.IsEnabled = ResourcePicker.IsEnabled = PageHost.IsEnabled = gate.ControlsEnabled;
                BusyRing.IsActive = gate.ShowsProgress;
                RefreshDataAvailability();
            }
        }
    }

    string ReviewChangeText(ChangeItem item) => ReviewChangeText(PlannedChange.Wrap(item));
    // Reads the reviewed rows directly: showing the page does not build the full items.
    string ReviewChangeText(PlannedChange item) => $"{EnumText(item.Kind)} · {Schemas.Key(item.Resource, item.After)}\n" +
        (item.Kind == ChangeKind.Delete ? L("Text_5ABD08A949") : string.Join("；", item.Fields().Select(f => $"{ApiOperationPolicy.FieldLabel(f.Key, localization)}: {item.Before?.Get(f.Key) ?? "∅"} → {f.Value.Value}")));

    Task reviewLoad = Task.CompletedTask;
    // The page appears at once; the plan is built on a worker and the page filled when it is ready.
    UIElement ReviewPage()
    {
        var host = new Grid();
        var waiting = Row(new ProgressRing { IsActive = true, Width = 20, Height = 20 }, Text(() => L("ReviewPreparing")));
        host.Children.Add(waiting);
        var generation = reviews.BeginLoad();
        var owner = workspace;
        var airline = workspace.AirlineId;
        var mode = workspace.Mode;
        reviewLoad = UserAction(async () =>
        {
            var plan = await WhileLocked(reviews.Capture());
            if (!reviews.IsCurrent(generation) || !ReferenceEquals(owner, workspace) || !ReferenceEquals(PageHost.Content, host))
                return;
            reviews.Show(plan, owner.Id, airline, mode);
            host.Children.Clear();
            host.Children.Add(ReviewContent(plan));
        });
        return host;
    }
    UIElement ReviewContent(WorkspaceReviewPlan plan)
    {
        var conflicts = workspace.Resources.Sum(p => p.Value.Conflicts.Count);
        var header = Stack(Text(() => L("ReviewWorkspace"), 18), Text(() => L("ApiTarget", ("va", workspace.Name), ("id", workspace.AirlineId ?? "?"))),
            Text(() => L("ReviewCounts", ("total", plan.Entries.Count), ("ready", plan.SubmitCount),
                ("deferred", plan.Entries.Count(e => e.Disposition == ReviewDisposition.Deferred)),
                ("blocked", plan.Entries.Count(e => e.Disposition == ReviewDisposition.Blocked)))),
            Text(() => L("ReviewMode", ("mode", EnumText(workspace.Mode)))));
        if (workspace.Mode == RunMode.Offline)
            header.Children.Add(Text(() => L("ReviewConnect")));
        foreach (var blocker in plan.Blockers.Take(3))
            header.Children.Add(Text(() => L(blocker)));
        if (plan.Blockers.Count > 3)
            header.Children.Add(Button(() => L("ReviewIssues", ("count", plan.Blockers.Count)), async () =>
                { await Confirm(L("ReviewIssues", ("count", plan.Blockers.Count)), new ListView { MaxHeight = 350, ItemsSource = plan.Blockers.Select(L).ToArray() }, L("Text_3FD47EDCE4")); }));
        header.Children.Add(Actions(
            Button(() => L(workspace.Mode == RunMode.Demo ? "ReviewDemoSubmit" : "ReviewSubmit"), workspace.Mode == RunMode.Demo ? StartDemo : StartApi,
                CanEditData() && plan.SubmitCount > 0 && workspace.Mode != RunMode.Offline),
            Button(() => L("ApiConflicts", ("count", conflicts)), ResolveWorkspaceConflicts, gate.CanResolveConflicts && conflicts > 0)));
        var rows = new List<ReviewDisplayRow>();
        foreach (var kind in Enum.GetValues<ResourceKind>())
        {
            var entries = plan.Entries.Where(e => e.Planned.Resource == kind).ToArray();
            if (entries.Length == 0)
                continue;
            rows.Add(new(this, kind, null));
            rows.AddRange(entries.Select(e => new ReviewDisplayRow(this, kind, e)));
        }
        if (rows.Count == 0)
            header.Children.Add(Text(() => L("Text_90484F47B3")));
        reviewRows = rows;
        var list = new ListView
        {
            SelectionMode = ListViewSelectionMode.None,
            ItemsSource = rows,
            ItemTemplateSelector = new ReviewTemplateSelector(),
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetAutomationId(list, "WorkspaceReview");
        var grid = new Grid { RowSpacing = 10 };
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        grid.Children.Add(header);
        Grid.SetRow(list, 1);
        grid.Children.Add(list);
        return grid;
    }
    sealed class ReviewDisplayRow(MainWindow owner, ResourceKind resource, ReviewEntry? entry) : ObservableObject
    {
        public ReviewEntry? Entry { get; } = entry;
        public string Heading => owner.L(Schemas.All[resource].Name);
        public string ExportLabel => owner.L("ReviewExport", ("resource", Heading));
        public string State => Entry is null ? "" : owner.L("ReviewState_" + Entry.Disposition);
        public string Body => Entry is null ? "" : owner.ReviewChangeText(Entry.Planned);
        public string Reason => Entry?.Reason is null ? "" : owner.L(Entry.Reason);
        public IAsyncRelayCommand? ExportCommand { get; } = entry is null ? new AsyncRelayCommand(() => owner.UserAction(() => owner.ExportCsv(resource))) : null;
        public void RefreshLanguage()
        {
            foreach (var name in new[] { nameof(Heading), nameof(ExportLabel), nameof(State), nameof(Body), nameof(Reason) })
                OnPropertyChanged(name);
        }
    }
    sealed class ReviewTemplateSelector : DataTemplateSelector
    {
        readonly DataTemplate header = Parse("""
            <StackPanel Spacing="6" Margin="0,12,0,6">
              <TextBlock Text="{Binding Heading}" FontSize="20"/>
              <Button Content="{Binding ExportLabel}" Command="{Binding ExportCommand}" HorizontalAlignment="Left"/>
            </StackPanel>
            """);
        readonly DataTemplate change = Parse("""
            <StackPanel Spacing="4" Margin="0,4,0,8">
              <TextBlock Text="{Binding State}" FontSize="14" FontWeight="SemiBold"/>
              <TextBlock Text="{Binding Body}" TextWrapping="Wrap"/>
              <TextBlock Text="{Binding Reason}" TextWrapping="Wrap"/>
            </StackPanel>
            """);
        static DataTemplate Parse(string content) => (DataTemplate)XamlReader.Load("<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\">" + content + "</DataTemplate>");
        protected override DataTemplate SelectTemplateCore(object item) => item is ReviewDisplayRow { Entry: null } ? header : change;
        protected override DataTemplate SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);
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
        var content = new ListView
        {
            MaxHeight = 400,
            SelectionMode = ListViewSelectionMode.None,
            Header = Text(() => L("ApiSubmitScope", ("va", workspace.Name), ("id", workspace.AirlineId ?? "?"), ("count", items.Count))),
            ItemsSource = items.Select(i => $"{L(Schemas.All[i.Resource].Name)} · {ReviewChangeText(i)}").ToArray()
        };
        if (!await Confirm(L(mode == RunMode.Demo ? "ReviewDemoSubmit" : "ReviewSubmit"), content, L(mode == RunMode.Demo ? "ReviewDemoSubmit" : "ReviewSubmit")))
            return null;
        var deletes = items.Where(i => i.Kind == ChangeKind.Delete).ToArray();
        if (deletes.Length > 0 && !await Confirm(L("Text_5D6042B30E"), new ListView
        {
            MaxHeight = 350,
            Header = Text(() => L("ApiDeleteConfirm", ("count", deletes.Length))),
            ItemsSource = deletes.Select(i => $"{L(Schemas.All[i.Resource].Name)}: {Schemas.Key(i.Resource, i.After)}").ToArray()
        }, L("Confirm")))
            return null;
        return items;
    }
}
