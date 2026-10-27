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
    TextBlock? pageSummary, selectionSummary;
    CommandBar? dataActions;
    int dataPageBuilds, dataRowBuilds;
    readonly List<RowViewModel> pageRowPool = [];
    ResourceData? pooledData;
    // What the pooled rows' edit closures act on. The pool is rebound to another workspace or
    // resource with the same columns, and released rows are finalized late (their x:Bind event
    // sources sit in the finalizer queue), so the closures reach the data only through this.
    sealed class RowLink
    {
        public Workspace? Owner; public ResourceData? Data; public ResourceKind Kind;
        public void Detach() { Owner = null; Data = null; }
    }
    RowLink? pooledLink;
    List<string> pooledColumns = [];
    DataTableTemplates? tableTemplates;
    CellTemplateSelector? cellTemplates;
    string pooledDensity = "";

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
            state.SelectionCountsDirty = true;
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
        shownState!.SelectionCountsDirty = true;
        var replaced = ids.ToHashSet();
        foreach (var view in visible)
            if (replaced.Contains(view.Row.LocalId) && source.FindDraftRow(view.Row.LocalId) is { } row)
                view.Rebind(row);
        lastEditedCell = null;
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
            state.SelectionCountsDirty = true;
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
        if (!pooledColumns.SequenceEqual(columns) || pooledDensity != tableDensity || pooledLink is null)
        {
            pooledLink?.Detach();
            pageRowPool.Clear();
            pooledColumns = columns;
            pooledDensity = tableDensity;
            pooledLink = new();
        }
        // Rows with the same columns are rebound in place, also for another workspace or resource.
        if (!ReferenceEquals(pooledData, source))
        {
            pooledData = source;
            pooledLink.Owner = workspace;
            pooledLink.Data = source;
            pooledLink.Kind = resource;
            EnsureDataHeaders(columns);
            SyncSortColumns(sortBox?.SelectedItem as string);
        }
        var rows = new List<RowViewModel>(shownState.Model.PageSize);
        restoringDataSelection = true;
        try
        {
            table.SelectedItems.Clear();
            foreach (var id in shownState.Model.PageIds())
            {
                var row = source.FindDraftRow(id);
                if (row is null)
                    continue;
                if (rows.Count < pageRowPool.Count)
                {
                    var reused = pageRowPool[rows.Count];
                    reused.Rebind(row);
                    rows.Add(reused);
                    continue;
                }
                RowViewModel? view = null;
                var link = pooledLink!;
                bool Editable() => CanEditData() && !restoringDataSelection && ReferenceEquals(link.Owner, workspace) && link.Kind == resource
                    && view != null && ReferenceEquals(link.Data, Data) && ReferenceEquals(Data.FindDraftRow(view.Row.LocalId), view.Row) && !Schemas.Deleted(view.Row);
                var cells = columns.Select(field => new CellViewModel(field, row.Get(field), value =>
                {
                    if (!Editable() || field == "_delete")
                        return;
                    var owner = link.Owner!;
                    var data = link.Data!;
                    var current = view!.Row;
                    var currentId = current.LocalId;
                    var before = ChangePlanner.GetChangeKind(current, tableBaseline.GetValueOrDefault(currentId));
                    var key = currentId + ":" + field;
                    var newStep = lastEditedCell != key;
                    // Rows are replaced, never edited in place: the view follows the replacement.
                    var replacement = current.With(field, value);
                    data.Edit([currentId], () => data.ReplaceDraftRows([replacement]), newStep: newStep);
                    view!.ReplaceRow(replacement);
                    current = replacement;
                    lastEditedCell = key;
                    var after = ChangePlanner.GetChangeKind(current, tableBaseline.GetValueOrDefault(currentId));
                    if (changeCounts.TryGetValue(data, out var count))
                        changeCounts[data] = count + (after != null ? 1 : 0) - (before != null ? 1 : 0);
                    view?.Cells.FirstOrDefault(c => c.Field == "_delete")?.RefreshLanguage();
                    session.RecordCellEdit(owner, link.Kind, currentId);
                    Status(() => L("Text_75141EDE81"));
                })
                {
                    CanEdit = Editable,
                    IsReadOnly = CellReadOnly(row, field),
                    ReadOnlyForRow = r => CellReadOnly(r, field),
                    FleetName = () => field == "Parameter IDs" ? L("AirlineParameter") : L("ChooseFleet"),
                    ShowServerNull = () => view?.Row.Identity != null && view.Row.Get("Type") == "jumpseat" && (field is "Callsign" or "Flight Number"),
                    ServerNullLabel = () => L("JumpseatNotPreserved"),
                    Compact = tableDensity == "compact",
                    Height = UiTokens.CellHeight(tableDensity == "compact"),
                    DisplayText = () => field == "_delete" ? RowStatus(view!.Row, tableBaseline, tableConflicts)
                        : field == "Parameter IDs" ? view!.Row.Get(field) : field is "Fleet ID" or "Fleet IDs" ? FleetLabel(view!.Row.Get(field), tableFleetLabels) : "",
                    ChooseFleet = field == "Parameter IDs" ? new CommunityToolkit.Mvvm.Input.AsyncRelayCommand(() => Guard(() => ChooseParameter(view!.Row)))
                        : field is "Fleet ID" or "Fleet IDs" ? new CommunityToolkit.Mvvm.Input.AsyncRelayCommand(() => Guard(() => ChooseFleets(view!.Row))) : null
                }).ToList();
                view = new(row, cells) { CellTemplates = cellTemplates };
                rows.Add(view);
                pageRowPool.Add(view);
            }
            // Kept beyond a short page (or an empty workspace), up to one full page.
            var keep = Math.Max(rows.Count, shownState.Model.PageSize);
            if (pageRowPool.Count > keep)
                pageRowPool.RemoveRange(keep, pageRowPool.Count - keep);
            lastEditedCell = null;
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
    bool CellReadOnly(DataRow row, string field) => Schemas.Deleted(row) || workspace.Mode == RunMode.Online && row.Identity != null
        && (field == "Parameter IDs" || row.Get("Type") == "jumpseat" && (field is "Callsign" or "Flight Number"));
    string RowStatus(DataRow row, Dictionary<Guid, DataRow> baseline, HashSet<Guid> conflicts)
    {
        if (Schemas.Deleted(row))
            return L("Text_EE96DC1EBE");
        if (conflicts.Contains(row.LocalId))
            return L("ApiRowConflict");
        if (!baseline.TryGetValue(row.LocalId, out var before))
            return L("Text_4EF7F62706");
        return L(ChangePlanner.GetChangeKind(row, before) != null ? "ApiRowDraft" : "Text_20D2382F10");
    }
    string FleetLabel(string value, Dictionary<string, FleetOption> labels)
    {
        var label = string.Join("; ", FleetAssignmentService.Tokens(value).Select(t => labels.TryGetValue(t, out var option) ? option.Describe(localization) : L("Text_C221144914") + t));
        return label.Length > 0 ? label : L("Text_F274578907");
    }
    void ChangeDataPage(int target)
    {
        if (!CanEditData() || shownState is null)
            return;
        shownState.Model.SetPage(target);
        ShowDataPageRows(true);
    }
    FrameworkElement DataPager()
    {
        // The pager is kept across workspaces and resources: act on the state shown now.
        DataTableViewModel Model() => (shownState ?? CurrentDataState).Model;
        firstPage = DataButton("TableFirst", () => ChangeDataPage(1), "DataFirstPage", Symbol.Previous);
        previousPage = DataButton("TablePrevious", () => ChangeDataPage(Model().Page - 1), "DataPreviousPage", Symbol.Back);
        nextPage = DataButton("TableNext", () => ChangeDataPage(Model().Page + 1), "DataNextPage", Symbol.Forward);
        lastPage = DataButton("TableLast", () => ChangeDataPage(Model().PageCount), "DataLastPage", Symbol.Next);
        pageJump = new TextBox { Width = UiTokens.PageJumpWidth, Text = Model().Page.ToString() };
        AutomationProperties.SetAutomationId(pageJump, "DataPageJump");
        AutomationProperties.SetName(pageJump, L("TableGo"));
        void Go()
        { if (int.TryParse(pageJump.Text, out var n) && n >= 1 && n <= Model().PageCount) ChangeDataPage(n); else Status(() => L("TableInvalidPage")); }
        pageGo = DataButton("TableGo", Go, "DataGoPage", Symbol.Go);
        pageJump.KeyDown += async (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter && CanEditData()) { e.Handled = true; await UserAction(() => { Go(); return Task.CompletedTask; }); } };
        pageSize = new ComboBox { Width = UiTokens.PageSizePickerWidth, ItemsSource = new[] { 100, 200, 500 }, SelectedItem = Model().PageSize };
        AutomationProperties.SetAutomationId(pageSize, "DataPageSize");
        AutomationProperties.SetName(pageSize, L("TablePageSize"));
        // Showing another state's size is not a change of size.
        pageSize.SelectionChanged += (_, _) => { if (CanEditData() && pageSize.SelectedItem is int size && size != Model().PageSize) { Model().SetPageSize(size); ShowDataPageRows(true); } };
        pageSummary = Text(() => PageSummary());
        selectionSummary = Text(() => SelectionSummary());
        selectPage = SelectionItem("TableSelectPage", () => { restoringDataSelection = true; try { foreach (var row in visible) { Model().Selected.Add(row.Row.LocalId); if (!table!.SelectedItems.Contains(row)) table.SelectedItems.Add(row); } } finally { restoringDataSelection = false; } shownState!.SelectionCountsDirty = true; RefreshDataAvailability(); }, "DataSelectPage");
        selectAll = SelectionItem("TableSelectAll", () => { Model().SelectAll(); RestoreDataSelection(); }, "DataSelectAll");
        clearSelection = SelectionItem("TableClearSelection", () => { Model().Selected.Clear(); RestoreDataSelection(); }, "DataClearSelection");
        var menu = new MenuFlyout();
        menu.Items.Add(selectPage);
        menu.Items.Add(selectAll);
        menu.Items.Add(clearSelection);
        selectionButton = new Button { Flyout = menu }.Localize(localization, "Content", () => L("TableSelection"));
        queryCancel = new Button().Localize(localization, "Content", () => L("TableCancelQuery"));
        AutomationProperties.SetAutomationId(queryCancel, "DataQueryCancel");
        queryCancel.Click += (_, _) => { CancelDataQuery(); Status(() => L("TableQueryCanceled")); };
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
        LockDataTable(!enabled);
        if (dataActions != null)
            foreach (var command in dataActions.PrimaryCommands.Concat(dataActions.SecondaryCommands).OfType<AppBarButton>())
            {
                command.IsEnabled = enabled && ((command.Tag as string) switch { "active" => counts.Active > 0, "deleted" => counts.Deleted > 0, "delete" => counts.Deletable > 0, _ => true }) && (command.Icon is not SymbolIcon { Symbol: Symbol.Important } || Data.Conflicts.Count > 0);
                if (command.Icon is SymbolIcon { Symbol: Symbol.Important })
                    command.Label = L("ApiConflicts", ("count", Data.Conflicts.Count));
            }
        if (firstPage != null)
            firstPage.IsEnabled = enabled && model.Page > 1;
        if (previousPage != null)
            previousPage.IsEnabled = enabled && model.Page > 1;
        if (nextPage != null)
            nextPage.IsEnabled = enabled && model.Page < model.PageCount;
        if (lastPage != null)
            lastPage.IsEnabled = enabled && model.Page < model.PageCount;
        if (pageGo != null)
            pageGo.IsEnabled = enabled && model.Total > 0;
        if (pageJump != null)
        { pageJump.IsEnabled = enabled; if (pageJump.Tag is not int oldPage || oldPage != model.Page) { pageJump.Text = model.Page.ToString(); pageJump.Tag = model.Page; } }
        if (pageSize != null)
            pageSize.IsEnabled = enabled;
        if (selectionButton != null)
            selectionButton.IsEnabled = enabled;
        if (densityPicker != null)
        { densityPicker.IsEnabled = enabled; AutomationProperties.SetName(densityPicker, L("TableDensity")); }
        if (selectPage != null)
            selectPage.IsEnabled = enabled && visible.Count > 0;
        if (selectAll != null)
            selectAll.IsEnabled = enabled && model.Total > 0;
        if (clearSelection != null)
            clearSelection.IsEnabled = enabled && model.Selected.Count > 0;
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
        shownState.SelectionCountsDirty = true;
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
            if (shownState.Model.Selected.Remove(row.Row.LocalId) && !shownState.SelectionCountsDirty)
            { if (Schemas.Deleted(row.Row)) shownState.SelectedDeleted--; else shownState.SelectedActive--; if (!Schemas.Deleted(row.Row) || !tableBaseline.ContainsKey(row.Row.LocalId)) shownState.SelectedDeletable--; }
        foreach (var row in args.AddedItems.Cast<RowViewModel>())
            if (shownState.Model.Selected.Add(row.Row.LocalId) && !shownState.SelectionCountsDirty)
            { if (Schemas.Deleted(row.Row)) shownState.SelectedDeleted++; else shownState.SelectedActive++; if (!Schemas.Deleted(row.Row) || !tableBaseline.ContainsKey(row.Row.LocalId)) shownState.SelectedDeletable++; }
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
