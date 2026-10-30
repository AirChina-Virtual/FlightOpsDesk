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
    // The table control on the data page, kept across workspaces and resources, and its footer model.
    DataGrid? dataGrid;
    DataGridFooterViewModel? dataFooter;
    DataTableView? tableView => dataGrid?.View;
    TextBox? DataPageJumpBox => dataGrid?.PageJump;
    ComboBox? DataPageSizeBox => dataGrid?.PageSize;
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
        dataFooter?.ShowPage(state.Model.Page);
    }
    void ClearDataRows()
    {
        visible = [];
        tableView?.ClearRows();
    }
    void EnsureDataHeaders(List<string> columns) => tableView?.ShowHeaders(columns);
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
        ApplyShellState();
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
        ApplyShellState();
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
                ApplyShellState();
                RefreshDataAvailability();
            }
        }
    }
    bool CanEditData() => gate.CanEdit;
    void ShowDataPageRows(bool resetVertical)
    {
        if (tableView is null || shownState is null)
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
            tableView.ClearSelection();
            var rows = PageRows.Fill(shownState.Model.PageIds(), source, shownState.Model.PageSize);
            visible = rows;
            dataRowBuilds++;
            var selected = shownState.Model.Selected;
            tableView.ShowRows(rows, row => selected.Contains(row.Row.LocalId));
            RootGrid.UpdateLayout();
        }
        finally { restoringDataSelection = false; }
        if (resetVertical)
        { shownState.Vertical = 0; tableView.ScrollTo(0, null); }
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
        UserSelection.SelectShown(PagerModel(), shownState!.Counts, visible.Select(r => r.Row.LocalId));
        tableView!.Select(visible);
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
        if (densityPicker != null)
        { densityPicker.IsEnabled = can.Edit; AutomationProperties.SetName(densityPicker, L("TableDensity")); }
        dataFooter?.Apply(can, model, gate.QueryInProgress, PageSummary(), SelectionSummary());
        RefreshQueryNotice();
    }
    // Focus that was in the table moves to the search box, or the commands.
    void LockDataTable(bool locked)
    {
        if (tableView?.Lock(locked) == true && searchBox?.Focus(FocusState.Programmatic) != true)
            dataActions?.Focus(FocusState.Programmatic);
    }
    void RestoreDataSelection()
    {
        if (tableView is null || shownState is null)
            return;
        shownState.Counts.Invalidate();
        var selected = shownState.Model.Selected;
        tableView.ApplySelection(visible, row => selected.Contains(row.Row.LocalId));
        RefreshDataAvailability();
    }
    void DataSelectionChanged(List<RowViewModel> added, List<RowViewModel> removed)
    {
        if (restoringDataSelection || shownState is null)
            return;
        UserSelection.Apply(shownState.Model, shownState.Counts, added.Select(r => r.Row), removed.Select(r => r.Row), tableBaseline.ContainsKey);
        RefreshDataAvailability();
    }
    void RestoreDataOffsets(DataPageState state)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!ReferenceEquals(shownState, state) || tableView is null)
                return;
            tableView.ScrollTo(state.Vertical, state.Horizontal);
        });
    }
    List<DataRow> SelectedRows() => shownState?.Model.Selected.Select(Data.FindDraftRow).OfType<DataRow>().ToList() ?? [];
    List<DataRow> FilteredRows() => shownState?.Model.ResultIds.Select(Data.FindDraftRow).OfType<DataRow>().ToList() ?? [];
}
