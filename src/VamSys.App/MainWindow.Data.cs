using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using VamSys.Core;

namespace VamSys.App;

public sealed partial class MainWindow
{
    Workspace? dataPageOwner;
    ResourceKind dataPageResource;
    bool restoringDataSelection;
    CancellationTokenSource? dataQueryCancellation;
    long dataQueryGeneration;
    Task dataQueryTask = Task.CompletedTask;
    readonly Dictionary<ResourceData, int> changeCounts = [];
    Dictionary<Guid, DataRow> tableBaseline = [];
    HashSet<Guid> tableConflicts = [];
    Dictionary<string, FleetOption> tableFleetLabels = new(StringComparer.OrdinalIgnoreCase);
    Button? firstPage, previousPage, nextPage, lastPage, pageGo, queryCancel, selectionButton;
    MenuFlyoutItem? selectPage, selectAll, clearSelection;
    TextBox? pageJump;
    ComboBox? pageSize;
    // The table control (QA builds only for now) and its footer model; null when the window builds the table in code.
    bool useNewDataGrid;
    DataGridFooterViewModel? dataFooter;
    TextBox? DataPageJumpBox => pageJump ?? (shownDataPage as Grid)?.Children.OfType<DataGrid>().FirstOrDefault()?.PageJump;
    ComboBox? DataPageSizeBox => pageSize ?? (shownDataPage as Grid)?.Children.OfType<DataGrid>().FirstOrDefault()?.PageSize;
    TextBlock? pageSummary, selectionSummary;
    CommandBar? dataActions;
    int dataPageBuilds, dataRowBuilds;
    DataGridViewModel? pageRows;
    DataGridViewModel PageRows => pageRows ??= new(new DataGridHost(this), cellEdits, localization);
    DataTableTemplates? tableTemplates;
    CellTemplateSelector? cellTemplates;
    // The pooled cells read the shown workspace, resource and baseline through this when they are used.
    sealed class DataGridHost(MainWindow window) : IDataGridHost
    {
        public bool CanEdit => window.CanEditData() && !window.restoringDataSelection;
        public Workspace Workspace => window.workspace;
        public ResourceKind Resource => window.resource;
        public ResourceData Data => window.Data;
        public IReadOnlyDictionary<Guid, DataRow> Baseline => window.tableBaseline;
        public IReadOnlySet<Guid> Conflicts => window.tableConflicts;
        public IReadOnlyDictionary<string, FleetOption> FleetLabels => window.tableFleetLabels;
        public object? CellTemplates => window.cellTemplates;
        public double CellHeight(bool compact) => UiTokens.CellHeight(compact);
        public void CellEdited(Workspace owner, ResourceKind kind, ResourceData data, Guid rowId, int changedDelta)
        {
            if (window.changeCounts.TryGetValue(data, out var count))
                window.changeCounts[data] = count + changedDelta;
            window.session.RecordCellEdit(owner, kind, rowId);
            window.Status(() => window.L("Text_75141EDE81"));
        }
        public Task ChooseFleets(DataRow row) => window.Guard(() => window.ChooseFleets(row));
        public Task ChooseParameter(DataRow row) => window.Guard(() => window.ChooseParameter(row));
    }

    UIElement GetDataPage()
    {
        if (ReferenceEquals(dataPageOwner, workspace) && dataPageResource == resource && shownDataPage != null)
        {
            RefreshDataAvailability();
            if (CurrentDataState.Model.IsDirty && gate.CanLoadOnShow)
                StartDataQuery();
            return shownDataPage;
        }
        CancelDataQuery();
        dataPageOwner = workspace;
        dataPageResource = resource;
        visible = [];
        UIElement content;
        if (shownDataPage is null)
        { dataPageBuilds++; content = BuildDataPage(); }
        else
        { content = shownDataPage; RebindDataPage(); }
        var state = CurrentDataState;
        if (!state.Model.IsDirty && state.Baseline != null)
        {
            tableBaseline = state.Baseline;
            tableConflicts = state.Conflicts;
            tableFleetLabels = state.FleetLabels;
            ShowDataPageRows(false);
            RestoreDataOffsets(state);
        }
        else if (gate.CanLoadOnBuild)
            StartDataQuery();
        RefreshDataAvailability();
        return content;
    }
    // The page's controls are kept across workspaces and resources and only their content changes.
    // WinUI keeps about 10 MiB of native memory for every discarded data page, so rebuilding the
    // page on each workspace or resource switch made the process grow without bound.
    void RebindDataPage()
    {
        var state = CurrentDataState;
        ClearDataRows();
        shownState = state;
        if (searchBox != null)
            searchBox.Text = state.Search;
        if (filterBox != null)
            filterBox.SelectedIndex = state.Filter;
        SyncSortColumns(state.Sort);
        EnsureDataHeaders(DisplayColumns());
        if (fleetCommand != null)
            fleetCommand.Visibility = resource is ResourceKind.Aircraft or ResourceKind.Routes ? Visibility.Visible : Visibility.Collapsed;
        if (retireCommand != null)
            retireCommand.Visibility = resource == ResourceKind.Routes ? Visibility.Visible : Visibility.Collapsed;
        if (pageSize != null)
            pageSize.SelectedItem = state.Model.PageSize;
        if (pageJump != null)
        { pageJump.Text = state.Model.Page.ToString(); pageJump.Tag = state.Model.Page; }
        dataFooter?.ShowPage(state.Model.Page);
    }
    // Removing the rows must not read as the user deselecting them.
    void ClearDataRows()
    {
        visible = [];
        if (table is null)
            return;
        restoringDataSelection = true;
        try
        { table.SelectedItems.Clear(); SyncShownRows([]); }
        finally { restoringDataSelection = false; }
    }
    // A new ItemsSource makes WinUI create every row's controls again and keep native memory for the
    // discarded ones, so pooled rows are rebound in place whenever the row count stays the same.
    // An observable collection would also recycle them when the count changes, but any observable
    // source doubled the layout time of paging and language changes (P95 10 → 20 ms, 25 → 56 ms).
    void SyncShownRows(List<RowViewModel> rows)
    {
        if (table is null)
            return;
        if (table.ItemsSource is not List<RowViewModel> old || old.Count != rows.Count || rows.Count > 0 && !ReferenceEquals(old[0], rows[0]))
            table.ItemsSource = rows;
    }
    List<string> headerColumns = [];
    void EnsureDataHeaders(List<string> columns)
    {
        if (table is null || table.Header != null && headerColumns.SequenceEqual(columns))
            return;
        headerColumns = columns;
        table.Header = BuildDataHeaders(columns);
    }
    void SyncSortColumns(string? selected)
    {
        if (sortBox is null)
            return;
        sortBox.ItemsSource = Data.Columns.Count > 0 ? Data.Columns : Schemas.All[resource].Columns.ToList();
        if (selected != null && sortBox.Items.Contains(selected))
            sortBox.SelectedItem = selected;
        else
            sortBox.SelectedIndex = -1;
    }
    void StartDataQuery()
    {
        var state = CurrentDataState;
        dataQueryTask = UserAction(() => LoadRowsAsync(state.AppliedSearch, state.AppliedFilter, state.AppliedSort));
    }
    void InvalidateDataViews()
    {
        changeCounts.Clear();
        foreach (var state in dataStates.Values)
        {
            state.Counts.Invalidate();
            state.Model.Invalidate();
            state.Baseline = null;
            state.Conflicts = [];
            state.FleetLabels = new(StringComparer.OrdinalIgnoreCase);
        }
    }
    // The shown page's applied result and baseline still describe the current resource.
    bool DataViewCurrent() => shownState != null && ReferenceEquals(shownState, CurrentDataState) && ReferenceEquals(PageHost.Content, shownDataPage)
        && ReferenceEquals(dataPageOwner, workspace) && dataPageResource == resource && !shownState.Model.IsDirty && shownState.Baseline != null;
    int CountChanged(ResourceData source, IEnumerable<Guid> ids)
        => ids.Count(id => source.FindDraftRow(id) is { } row && ChangePlanner.GetChangeKind(row, tableBaseline.GetValueOrDefault(id)) != null);
    // In-place replacements change no row's existence or position: rebind the shown rows among them
    // and adjust the counts; result membership and order are re-evaluated at the next applied query.
    void RefreshReplacedRows(ResourceData source, IReadOnlyCollection<Guid> ids, int changedBefore)
    {
        if (changeCounts.TryGetValue(source, out var count))
            changeCounts[source] = count - changedBefore + CountChanged(source, ids);
        shownState!.Counts.Invalidate();
        var replaced = ids.ToHashSet();
        foreach (var view in visible)
            if (replaced.Contains(view.Row.LocalId) && source.FindDraftRow(view.Row.LocalId) is { } row)
                view.Rebind(row);
        cellEdits.ResetContinuation();
    }
    int CountChanges(ResourceData data)
    {
        if (changeCounts.TryGetValue(data, out var count))
            return count;
        var baseline = data.Snapshot.ToDictionary(r => r.LocalId);
        count = 0;
        foreach (var row in data.Draft)
            if (ChangePlanner.GetChangeKind(row, baseline.GetValueOrDefault(row.LocalId)) != null)
                count++;
        return changeCounts[data] = count;
    }
    void CancelDataQuery()
    {
        dataQueryGeneration++;
        dataQueryCancellation?.Cancel();
        dataQueryCancellation = null;
        gate.SetQuerying(false);
        BusyRing.IsActive = gate.ShowsWorkProgress;
        RefreshDataAvailability();
    }
    async Task LoadRowsAsync(string search, int filter, string? sort)
    {
        if (!gate.CanStartQuery || table is null)
            return;
        CancelDataQuery();
        var cancellation = new CancellationTokenSource();
        dataQueryCancellation = cancellation;
        var token = cancellation.Token;
        var generation = dataQueryGeneration;
        var owner = workspace;
        var kind = resource;
        var source = Data;
        var state = CurrentDataState;
        gate.SetQuerying(true);
        BusyRing.IsActive = true;
        RefreshDataAvailability();
        try
        {
            // Always yield so page construction and the progress indicator can finish.
            await Task.Yield();
            token.ThrowIfCancellationRequested();
            var baseline = source.Snapshot.ToDictionary(r => r.LocalId);
            var inputs = new List<DataQueryRow>(source.Draft.Count);
            var slice = Stopwatch.StartNew();
            for (var n = 0; n < source.Draft.Count; n++)
            {
                token.ThrowIfCancellationRequested();
                var row = source.Draft[n];
                inputs.Add(new(row.LocalId, search.Length == 0 ? [] : row.Fields.Values.ToImmutableArray(), sort is null ? "" : row.Get(sort),
                    ChangePlanner.GetChangeKind(row, baseline.GetValueOrDefault(row.LocalId)) != null, Schemas.Deleted(row)));
                if (slice.ElapsedMilliseconds >= 8)
                {
                    Status(() => L("TableLoading"));
                    await Task.Yield();
                    slice.Restart();
                }
            }
            var result = await Task.Run(() => DataTableViewModel.Query(inputs, search, filter, sort != null, token), token);
            token.ThrowIfCancellationRequested();
            if (generation != dataQueryGeneration || !ReferenceEquals(owner, workspace) || kind != resource || page != "data")
                return;
            tableBaseline = baseline;
            tableConflicts = source.Conflicts.Select(c => c.RowId).ToHashSet();
            tableFleetLabels = batch.FleetOptions(owner).ToDictionary(o => o.Token, StringComparer.OrdinalIgnoreCase);
            state.Baseline = tableBaseline;
            state.Conflicts = tableConflicts;
            state.FleetLabels = tableFleetLabels;
            state.Model.Apply(result, search, filter, sort);
            state.Counts.Invalidate();
            state.AppliedSearch = search;
            state.AppliedFilter = filter;
            state.AppliedSort = sort;
            changeCounts[source] = result.ModifiedCount;
            ShowDataPageRows(false);
            RestoreDataOffsets(state);
            Status(() => L("TableReady", ("count", result.RowIds.Length)));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            cancellation.Dispose();
            if (generation == dataQueryGeneration)
            {
                dataQueryCancellation = null;
                gate.SetQuerying(false);
                BusyRing.IsActive = gate.ShowsWorkProgress;
                RefreshDataAvailability();
            }
        }
    }
    bool CanEditData() => gate.CanEdit;
    void ShowDataPageRows(bool resetVertical)
    {
        if (table is null || shownState is null)
            return;
        var source = Data;
        var columns = DisplayColumns();
        // Rows with the same columns are rebound in place, also for another workspace or resource.
        if (PageRows.Bind(columns, tableDensity, workspace, source, resource))
        {
            EnsureDataHeaders(columns);
            SyncSortColumns(sortBox?.SelectedItem as string);
        }
        restoringDataSelection = true;
        try
        {
            table.SelectedItems.Clear();
            var rows = PageRows.Fill(shownState.Model.PageIds(), source, shownState.Model.PageSize);
            visible = rows;
            dataRowBuilds++;
            SyncShownRows(rows);
            foreach (var row in rows)
            if (shownState.Model.Selected.Contains(row.Row.LocalId))
                table.SelectedItems.Add(row);
            RootGrid.UpdateLayout();
        }
        finally { restoringDataSelection = false; }
        if (resetVertical)
        { shownState.Vertical = 0; FindVisual<ScrollViewer>(table)?.ChangeView(null, 0, null, true); }
        RefreshDataAvailability();
    }
    void ChangeDataPage(int target)
    {
        if (!CanEditData() || shownState is null)
            return;
        shownState.Model.SetPage(target);
        ShowDataPageRows(true);
    }
    // The pager is kept across workspaces and resources: act on the state shown now.
    DataTableViewModel PagerModel() => (shownState ?? CurrentDataState).Model;
    void GoToTypedPage(string text)
    { if (int.TryParse(text, out var n) && n >= 1 && n <= PagerModel().PageCount) ChangeDataPage(n); else Status(() => L("TableInvalidPage")); }
    // Showing another state's size is not a change of size.
    void ChangePageSize(int size)
    { if (CanEditData() && size != PagerModel().PageSize) { PagerModel().SetPageSize(size); ShowDataPageRows(true); } }
    void SelectShownPage()
    {
        restoringDataSelection = true;
        try { foreach (var row in visible) { PagerModel().Selected.Add(row.Row.LocalId); if (!table!.SelectedItems.Contains(row)) table.SelectedItems.Add(row); } }
        finally { restoringDataSelection = false; }
        shownState!.Counts.Invalidate();
        RefreshDataAvailability();
    }
    void SelectAllResults() { PagerModel().SelectAll(); RestoreDataSelection(); }
    void ClearDataSelection() { PagerModel().Selected.Clear(); RestoreDataSelection(); }
    void CancelQueryByUser() { CancelDataQuery(); Status(() => L("TableQueryCanceled")); }
    DataGridActions DataGridControlActions() => new(
        CanEdit: CanEditData,
        Run: async action => { if (CanEditData()) await UserAction(() => { action(); return Task.CompletedTask; }); },
        FirstPage: () => ChangeDataPage(1),
        PreviousPage: () => ChangeDataPage(PagerModel().Page - 1),
        NextPage: () => ChangeDataPage(PagerModel().Page + 1),
        LastPage: () => ChangeDataPage(PagerModel().PageCount),
        GoToPage: GoToTypedPage,
        ChangePageSize: ChangePageSize,
        SelectPage: SelectShownPage,
        SelectAll: SelectAllResults,
        ClearSelection: ClearDataSelection,
        CancelQuery: CancelQueryByUser);
    FrameworkElement DataPager()
    {
        DataTableViewModel Model() => PagerModel();
        firstPage = DataButton("TableFirst", () => ChangeDataPage(1), "DataFirstPage", Symbol.Previous);
        previousPage = DataButton("TablePrevious", () => ChangeDataPage(Model().Page - 1), "DataPreviousPage", Symbol.Back);
        nextPage = DataButton("TableNext", () => ChangeDataPage(Model().Page + 1), "DataNextPage", Symbol.Forward);
        lastPage = DataButton("TableLast", () => ChangeDataPage(Model().PageCount), "DataLastPage", Symbol.Next);
        pageJump = new TextBox { Width = UiTokens.PageJumpWidth, Text = Model().Page.ToString() };
        AutomationProperties.SetAutomationId(pageJump, "DataPageJump");
        AutomationProperties.SetName(pageJump, L("TableGo"));
        void Go() => GoToTypedPage(pageJump.Text);
        pageGo = DataButton("TableGo", Go, "DataGoPage", Symbol.Go);
        pageJump.KeyDown += async (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter && CanEditData()) { e.Handled = true; await UserAction(() => { Go(); return Task.CompletedTask; }); } };
        pageSize = new ComboBox { Width = UiTokens.PageSizePickerWidth, ItemsSource = DataGridFooterViewModel.PageSizes, SelectedItem = Model().PageSize };
        AutomationProperties.SetAutomationId(pageSize, "DataPageSize");
        AutomationProperties.SetName(pageSize, L("TablePageSize"));
        pageSize.SelectionChanged += (_, _) => { if (pageSize.SelectedItem is int size) ChangePageSize(size); };
        pageSummary = Text(() => PageSummary());
        selectionSummary = Text(() => SelectionSummary());
        selectPage = SelectionItem("TableSelectPage", SelectShownPage, "DataSelectPage");
        selectAll = SelectionItem("TableSelectAll", SelectAllResults, "DataSelectAll");
        clearSelection = SelectionItem("TableClearSelection", ClearDataSelection, "DataClearSelection");
        var menu = new MenuFlyout();
        menu.Items.Add(selectPage);
        menu.Items.Add(selectAll);
        menu.Items.Add(clearSelection);
        selectionButton = new Button { Flyout = menu }.Localize(localization, "Content", () => L("TableSelection"));
        queryCancel = new Button().Localize(localization, "Content", () => L("TableCancelQuery"));
        AutomationProperties.SetAutomationId(queryCancel, "DataQueryCancel");
        queryCancel.Click += (_, _) => CancelQueryByUser();
        var selected = Row(selectionButton, selectionSummary, queryCancel);
        selected.Spacing = UiTokens.SpacingMedium;
        ToolTipService.SetToolTip(selectionSummary, new ToolTip().Localize(localization, "Content", () => L("BatchOnlySelected")));
        var navigation = Row(firstPage, previousPage, nextPage, lastPage, pageJump, pageGo, pageSize, pageSummary);
        navigation.Spacing = UiTokens.SpacingSmall;
        var selectionHost = Actions(selected);
        var navigationHost = Actions(navigation);
        var footer = new Grid { RowSpacing = UiTokens.SpacingTight, ColumnSpacing = UiTokens.SpacingMedium };
        footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        footer.RowDefinitions.Add(new() { Height = GridLength.Auto });
        footer.RowDefinitions.Add(new() { Height = GridLength.Auto });
        footer.Children.Add(selectionHost);
        footer.Children.Add(navigationHost);
        void Arrange()
        {
            var wide = footer.ActualWidth >= Math.Max(800, selected.DesiredSize.Width + navigation.DesiredSize.Width + 8);
            Grid.SetColumn(navigationHost, wide ? 1 : 0);
            Grid.SetRow(navigationHost, wide ? 0 : 1);
            Grid.SetColumnSpan(navigationHost, wide ? 1 : 2);
            Grid.SetColumnSpan(selectionHost, wide ? 1 : 2);
            navigationHost.HorizontalAlignment = wide ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
        }
        footer.SizeChanged += (_, _) => Arrange();
        Arrange();
        return footer;
    }
    MenuFlyoutItem SelectionItem(string key, Action action, string id)
    {
        var item = new MenuFlyoutItem().Localize(localization, "Text", () => L(key));
        AutomationProperties.SetAutomationId(item, id);
        item.Click += async (_, _) => { if (CanEditData()) await UserAction(() => { action(); return Task.CompletedTask; }); };
        return item;
    }
    Button DataButton(string key, Action action, string id, Symbol icon)
    {
        var button = new Button { Content = new SymbolIcon(icon), Padding = UiTokens.IconButtonPadding, Tag = key };
        AutomationProperties.SetAutomationId(button, id);
        AutomationProperties.SetName(button, L(key));
        ToolTipService.SetToolTip(button, new ToolTip().Localize(localization, "Content", () => L(key)));
        button.Click += async (_, _) => { if (CanEditData()) await UserAction(() => { action(); return Task.CompletedTask; }); };
        return button;
    }
    string PageSummary() => shownState is null ? "" : L("TablePageSummary", ("page", shownState.Model.Page), ("pages", shownState.Model.PageCount), ("first", shownState.Model.First), ("last", shownState.Model.Last), ("total", shownState.Model.Total));
    string SelectionSummary() => shownState is null ? "" : L("TableSelectionSummary", ("total", shownState.Model.Selected.Count), ("page", visible.Count(r => shownState.Model.Selected.Contains(r.Row.LocalId)))) + " · " + L("BatchSelectionHint");
    void RefreshDataAvailability()
    {
        if (shownState is null)
            return;
        var enabled = CanEditData();
        var model = shownState.Model;
        var counts = SelectionCounts();
        var can = DataGridAvailability.For(model, enabled, visible.Count);
        LockDataTable(!enabled);
        if (dataActions != null)
            foreach (var command in dataActions.PrimaryCommands.Concat(dataActions.SecondaryCommands).OfType<AppBarButton>())
            {
                var conflictCommand = command.Icon is SymbolIcon { Symbol: Symbol.Important };
                command.IsEnabled = DataGridAvailability.Command(enabled, command.Tag as string, conflictCommand, counts, Data.Conflicts.Count);
                if (conflictCommand)
                    command.Label = L("ApiConflicts", ("count", Data.Conflicts.Count));
            }
        if (firstPage != null)
            firstPage.IsEnabled = can.First;
        if (previousPage != null)
            previousPage.IsEnabled = can.Previous;
        if (nextPage != null)
            nextPage.IsEnabled = can.Next;
        if (lastPage != null)
            lastPage.IsEnabled = can.Last;
        if (pageGo != null)
            pageGo.IsEnabled = can.Go;
        if (pageJump != null)
        { pageJump.IsEnabled = can.Edit; if (pageJump.Tag is not int oldPage || oldPage != model.Page) { pageJump.Text = model.Page.ToString(); pageJump.Tag = model.Page; } }
        if (pageSize != null)
            pageSize.IsEnabled = can.Edit;
        if (selectionButton != null)
            selectionButton.IsEnabled = can.Edit;
        if (densityPicker != null)
        { densityPicker.IsEnabled = can.Edit; AutomationProperties.SetName(densityPicker, L("TableDensity")); }
        if (selectPage != null)
            selectPage.IsEnabled = can.SelectPage;
        if (selectAll != null)
            selectAll.IsEnabled = can.SelectAll;
        if (clearSelection != null)
            clearSelection.IsEnabled = can.ClearSelection;
        if (queryCancel != null)
            queryCancel.Visibility = gate.QueryInProgress ? Visibility.Visible : Visibility.Collapsed;
        if (pageSummary != null)
        {
            pageSummary.Text = $"{model.Page} / {model.PageCount}";
            ToolTipService.SetToolTip(pageSummary, PageSummary());
            AutomationProperties.SetName(pageSummary, PageSummary());
        }
        if (selectionSummary != null)
            selectionSummary.Text = SelectionSummary();
        foreach (var button in new[] { firstPage, previousPage, nextPage, lastPage, pageGo })
            if (button?.Tag is string key)
                AutomationProperties.SetName(button, L(key));
        // Named once when built, these kept the previous language for screen readers.
        if (pageJump != null)
            AutomationProperties.SetName(pageJump, L("TableGo"));
        if (pageSize != null)
            AutomationProperties.SetName(pageSize, L("TablePageSize"));
        dataFooter?.Apply(can, model, gate.QueryInProgress, PageSummary(), SelectionSummary());
        RefreshQueryNotice();
    }
    // Not IsEnabled: it cascades to every realized cell, and WinUI queues one IsEnabledChanged
    // event per control and drains one per dispatcher tick. Every query toggled it, so the queue
    // only grew, and closing the window then spent minutes draining it before the process exited.
    void LockDataTable(bool locked)
    {
        if (table is null || table.IsHitTestVisible != locked)
            return;
        table.IsHitTestVisible = !locked;
        table.Opacity = locked ? UiTokens.LockedTableOpacity : 1;
        if (!locked || table.XamlRoot is null)
            return;
        for (var focused = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(table.XamlRoot) as DependencyObject; focused != null; focused = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(focused))
            if (ReferenceEquals(focused, table))
            { if (searchBox?.Focus(FocusState.Programmatic) != true) dataActions?.Focus(FocusState.Programmatic); return; }
    }
    void RestoreDataSelection()
    {
        if (table is null || shownState is null)
            return;
        shownState.Counts.Invalidate();
        restoringDataSelection = true;
        try
        { table.SelectedItems.Clear(); foreach (var row in visible) if (shownState.Model.Selected.Contains(row.Row.LocalId)) table.SelectedItems.Add(row); }
        finally { restoringDataSelection = false; }
        RefreshDataAvailability();
    }
    void DataSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (restoringDataSelection || shownState is null)
            return;
        foreach (var row in args.RemovedItems.Cast<RowViewModel>())
            if (shownState.Model.Selected.Remove(row.Row.LocalId))
                shownState.Counts.Removed(row.Row, tableBaseline.ContainsKey(row.Row.LocalId));
        foreach (var row in args.AddedItems.Cast<RowViewModel>())
            if (shownState.Model.Selected.Add(row.Row.LocalId))
                shownState.Counts.Added(row.Row, tableBaseline.ContainsKey(row.Row.LocalId));
        RefreshDataAvailability();
    }
    void RestoreDataOffsets(DataPageState state)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!ReferenceEquals(shownState, state) || table is null)
                return;
            FindVisual<ScrollViewer>(table)?.ChangeView(null, state.Vertical, null, true);
            horizontalTable?.ChangeView(state.Horizontal, null, null, true);
        });
    }
    List<DataRow> SelectedRows() => shownState?.Model.Selected.Select(Data.FindDraftRow).OfType<DataRow>().ToList() ?? [];
    List<DataRow> FilteredRows() => shownState?.Model.ResultIds.Select(Data.FindDraftRow).OfType<DataRow>().ToList() ?? [];
}
