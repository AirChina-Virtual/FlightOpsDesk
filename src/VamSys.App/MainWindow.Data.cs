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
    bool querying, restoringDataSelection;
    CancellationTokenSource? dataQueryCancellation;
    long dataQueryGeneration;
    Task dataQueryTask = Task.CompletedTask;
    readonly Dictionary<ResourceData, int> changeCounts = [];
    Dictionary<Guid, DataRow> tableBaseline = [];
    HashSet<Guid> tableConflicts = [];
    Dictionary<string, FleetOption> tableFleetLabels = new(StringComparer.OrdinalIgnoreCase);
    Button? firstPage, previousPage, nextPage, lastPage, pageGo, selectPage, selectAll, clearSelection, queryCancel;
    TextBox? pageJump;
    ComboBox? pageSize;
    TextBlock? pageSummary, selectionSummary;
    CommandBar? dataActions;
    int dataPageBuilds, dataRowBuilds;
    readonly List<RowViewModel> pageRowPool = [];
    ResourceData? pooledData;
    List<string> pooledColumns = [];
    readonly CellTemplateSelector cellTemplates = new();

    UIElement GetDataPage()
    {
        if (ReferenceEquals(dataPageOwner, workspace) && dataPageResource == resource && shownDataPage != null)
        {
            RefreshDataAvailability();
            if (CurrentDataState.Model.IsDirty && !busy && !querying) StartDataQuery();
            return shownDataPage;
        }
        CancelDataQuery(); dataPageOwner = workspace; dataPageResource = resource;
        visible = []; dataPageBuilds++;
        var content = BuildDataPage();
        var state = CurrentDataState;
        if (!state.Model.IsDirty && state.Baseline != null)
        {
            tableBaseline = state.Baseline; tableConflicts = state.Conflicts; tableFleetLabels = state.FleetLabels;
            ShowDataPageRows(false); RestoreDataOffsets(state);
        }
        else if (!busy) StartDataQuery();
        RefreshDataAvailability();
        return content;
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
            state.Model.Invalidate(); state.Baseline = null; state.Conflicts = []; state.FleetLabels = new(StringComparer.OrdinalIgnoreCase);
        }
    }
    int CountChanges(ResourceData data)
    {
        if (changeCounts.TryGetValue(data, out var count)) return count;
        var baseline = data.Snapshot.ToDictionary(r => r.LocalId); count = 0;
        foreach (var row in data.Draft)
            if (ChangePlanner.GetChangeKind(row, baseline.GetValueOrDefault(row.LocalId)) != null) count++;
        return changeCounts[data] = count;
    }
    void CancelDataQuery()
    {
        dataQueryGeneration++; dataQueryCancellation?.Cancel(); dataQueryCancellation = null;
        querying = false; BusyRing.IsActive = busy;
        RefreshDataAvailability();
    }
    async Task LoadRowsAsync(string search, int filter, string? sort)
    {
        if (!closeState.IsOpen || busy || table is null) return;
        CancelDataQuery();
        var cancellation = new CancellationTokenSource(); dataQueryCancellation = cancellation;
        var token = cancellation.Token; var generation = dataQueryGeneration;
        var owner = workspace; var kind = resource; var source = Data; var state = CurrentDataState;
        querying = true; BusyRing.IsActive = true; RefreshDataAvailability();
        try
        {
            // Always yield so page construction and the progress indicator can finish.
            await Task.Yield(); token.ThrowIfCancellationRequested();
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
                    Status(() => L("TableLoading")); await Task.Yield(); slice.Restart();
                }
            }
            var result = await Task.Run(() => DataTableViewModel.Query(inputs, search, filter, sort != null, token), token);
            token.ThrowIfCancellationRequested();
            if (generation != dataQueryGeneration || !ReferenceEquals(owner, workspace) || kind != resource || page != "data") return;
            tableBaseline = baseline; tableConflicts = source.Conflicts.Select(c => c.RowId).ToHashSet();
            tableFleetLabels = fleets.Options(owner).ToDictionary(o => o.Token, StringComparer.OrdinalIgnoreCase);
            state.Baseline = tableBaseline; state.Conflicts = tableConflicts; state.FleetLabels = tableFleetLabels;
            state.Model.Apply(result, search, filter, sort);
            state.AppliedSearch = search; state.AppliedFilter = filter; state.AppliedSort = sort;
            changeCounts[source] = result.ModifiedCount;
            ShowDataPageRows(false); RestoreDataOffsets(state);
            Status(() => L("TableReady", ("count", result.RowIds.Length)));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            cancellation.Dispose();
            if (generation == dataQueryGeneration)
            {
                dataQueryCancellation = null; querying = false; BusyRing.IsActive = busy; RefreshDataAvailability();
            }
        }
    }
    bool CanEditData() => closeState.IsOpen && !busy && !querying && !storageBlocked;
    void ShowDataPageRows(bool resetVertical)
    {
        if (table is null || shownState is null) return;
        var owner = workspace; var kind = resource; var source = Data; var columns = DisplayColumns();
        if (!ReferenceEquals(pooledData, source) || !pooledColumns.SequenceEqual(columns))
        {
            pageRowPool.Clear(); pooledData = source; pooledColumns = columns;
            var headers=Row();headers.Spacing=0;headers.Margin=new Thickness(44,0,0,0);
            foreach(var field in columns)headers.Children.Add(new TextBlock{Width=new CellViewModel(field,"",_=>{}).Width,Margin=new Thickness(0,0,6,0),Text=field,TextTrimming=TextTrimming.CharacterEllipsis});
            table.Header=headers;
            if(sortBox!=null){var selected=sortBox.SelectedItem;sortBox.ItemsSource=Data.Columns.Count>0?Data.Columns:Schemas.All[resource].Columns.ToList();if(selected!=null&&sortBox.Items.Contains(selected))sortBox.SelectedItem=selected;}
        }
        var rows = new List<RowViewModel>(shownState.Model.PageSize);
        restoringDataSelection = true;
        try
        {
        table.SelectedItems.Clear();
        foreach (var id in shownState.Model.PageIds())
        {
            var row = source.FindDraftRow(id); if (row is null) continue;
            if (rows.Count < pageRowPool.Count)
            {
                var reused = pageRowPool[rows.Count]; reused.Rebind(row); rows.Add(reused); continue;
            }
            RowViewModel? view = null;
            bool Editable() => CanEditData() && !restoringDataSelection && ReferenceEquals(owner, workspace) && kind == resource
                && view != null && ReferenceEquals(source, Data) && ReferenceEquals(source.FindDraftRow(view.Row.LocalId), view.Row) && !Schemas.Deleted(view.Row);
            var cells = columns.Select(field => new CellViewModel(field, row.Get(field), value =>
            {
                if (!Editable() || field == "_delete") return;
                var current = view!.Row; var currentId=current.LocalId;
                var before = ChangePlanner.GetChangeKind(current, tableBaseline.GetValueOrDefault(currentId));
                var key = currentId + ":" + field; var newStep = lastEditedCell != key;
                source.Edit([currentId], () => current.Fields[field] = value, newStep:newStep); lastEditedCell = key;
                var after = ChangePlanner.GetChangeKind(current, tableBaseline.GetValueOrDefault(currentId));
                if (changeCounts.TryGetValue(source, out var count)) changeCounts[source] = count + (after != null ? 1 : 0) - (before != null ? 1 : 0);
                view?.Cells.FirstOrDefault(c => c.Field == "_delete")?.RefreshLanguage();
                edits.Cell(owner, kind, currentId, newStep); SaveSoon(); Status(() => L("Text_75141EDE81"));
            })
            {
                CanEdit = Editable, IsReadOnly = Schemas.Deleted(row), FleetName = () => L("ChooseFleet"),
                DisplayText = () => field == "_delete" ? RowStatus(view!.Row, tableBaseline, tableConflicts)
                    : field is "Fleet ID" or "Fleet IDs" ? FleetLabel(view!.Row.Get(field), tableFleetLabels) : "",
                ChooseFleet = field is "Fleet ID" or "Fleet IDs" ? new CommunityToolkit.Mvvm.Input.AsyncRelayCommand(() => Guard(() => ChooseFleets(view!.Row))) : null
            }).ToList();
            view = new(row, cells) { CellTemplates = cellTemplates }; rows.Add(view); pageRowPool.Add(view);
        }
        if(pageRowPool.Count>rows.Count)pageRowPool.RemoveRange(rows.Count,pageRowPool.Count-rows.Count);
        lastEditedCell = null; visible = rows; dataRowBuilds++;
        if (table.ItemsSource is not List<RowViewModel> old || old.Count != rows.Count || rows.Count > 0 && !ReferenceEquals(old[0],rows[0])) table.ItemsSource=rows;
        foreach (var row in rows) if (shownState.Model.Selected.Contains(row.Row.LocalId)) table.SelectedItems.Add(row);
        RootGrid.UpdateLayout();
        }
        finally { restoringDataSelection = false; }
        if (resetVertical) { shownState.Vertical = 0; FindVisual<ScrollViewer>(table)?.ChangeView(null, 0, null, true); }
        RefreshDataAvailability();
    }
    string RowStatus(DataRow row, Dictionary<Guid, DataRow> baseline, HashSet<Guid> conflicts)
    {
        if (Schemas.Deleted(row)) return L("Text_EE96DC1EBE");
        if (conflicts.Contains(row.LocalId)) return L("ApiRowConflict");
        if (!baseline.TryGetValue(row.LocalId, out var before)) return L("Text_4EF7F62706");
        return L(ChangePlanner.GetChangeKind(row, before) != null ? "ApiRowDraft" : "Text_20D2382F10");
    }
    string FleetLabel(string value, Dictionary<string, FleetOption> labels)
    {
        var label = string.Join("; ", FleetAssignmentService.Tokens(value).Select(t => labels.TryGetValue(t, out var option) ? option.Describe(localization) : L("Text_C221144914") + t));
        return label.Length > 0 ? label : L("Text_F274578907");
    }
    void ChangeDataPage(int target)
    {
        if (!CanEditData() || shownState is null) return;
        shownState.Model.SetPage(target); ShowDataPageRows(true);
    }
    FrameworkElement DataPager()
    {
        var model = CurrentDataState.Model;
        firstPage = DataButton("TableFirst", () => ChangeDataPage(1), "DataFirstPage");
        previousPage = DataButton("TablePrevious", () => ChangeDataPage(model.Page - 1), "DataPreviousPage");
        nextPage = DataButton("TableNext", () => ChangeDataPage(model.Page + 1), "DataNextPage");
        lastPage = DataButton("TableLast", () => ChangeDataPage(model.PageCount), "DataLastPage");
        pageJump = new TextBox { Width = 58, Text = model.Page.ToString() };
        AutomationProperties.SetAutomationId(pageJump, "DataPageJump");
        pageGo = DataButton("TableGo", () => { if (int.TryParse(pageJump.Text, out var number) && number >= 1 && number <= model.PageCount) ChangeDataPage(number); else Status(() => L("TableInvalidPage")); }, "DataGoPage");
        pageSize = new ComboBox { Width = 85, ItemsSource = new[] {100,200,500}, SelectedItem = model.PageSize };
        AutomationProperties.SetAutomationId(pageSize, "DataPageSize");
        pageSize.SelectionChanged += (_, _) => { if (CanEditData() && pageSize.SelectedItem is int size) { model.SetPageSize(size); ShowDataPageRows(true); } };
        pageSummary = Text(() => PageSummary()); selectionSummary = Text(() => SelectionSummary());
        selectPage = DataButton("TableSelectPage", () => { foreach (var row in visible) if (!table!.SelectedItems.Contains(row)) table.SelectedItems.Add(row); }, "DataSelectPage");
        selectAll = DataButton("TableSelectAll", () => { model.SelectAll(); RestoreDataSelection(); }, "DataSelectAll");
        clearSelection = DataButton("TableClearSelection", () => { model.Selected.Clear(); RestoreDataSelection(); }, "DataClearSelection");
        queryCancel = new Button().Localize(localization, "Content", () => L("TableCancelQuery"));
        AutomationProperties.SetAutomationId(queryCancel, "DataQueryCancel");
        queryCancel.Click += (_, _) => { CancelDataQuery(); Status(() => L("TableQueryCanceled")); };
        return Stack(Actions(firstPage,previousPage,nextPage,lastPage,pageJump,pageGo,Text(()=>L("TablePageSize")),pageSize,pageSummary),
            Actions(selectPage,selectAll,clearSelection,selectionSummary,queryCancel));
    }
    Button DataButton(string key, Action action, string id)
    {
        var button = new Button().Localize(localization,"Content",()=>L(key)); AutomationProperties.SetAutomationId(button,id);
        button.Click += async (_,_) => { if (CanEditData()) await UserAction(()=> { action(); return Task.CompletedTask; }); };
        return button;
    }
    string PageSummary() => shownState is null ? "" : L("TablePageSummary",("page",shownState.Model.Page),("pages",shownState.Model.PageCount),("first",shownState.Model.First),("last",shownState.Model.Last),("total",shownState.Model.Total));
    string SelectionSummary() => shownState is null ? "" : L("TableSelectionSummary",("total",shownState.Model.Selected.Count),("page",visible.Count(r=>shownState.Model.Selected.Contains(r.Row.LocalId))));
    void RefreshDataAvailability()
    {
        if (shownState is null) return;
        var enabled = CanEditData(); var model = shownState.Model;
        if (table != null) table.IsEnabled = enabled;
        if (dataActions != null)
            foreach (var command in dataActions.PrimaryCommands.Concat(dataActions.SecondaryCommands).OfType<AppBarButton>())
            {
                command.IsEnabled = enabled && (command.Icon is not SymbolIcon { Symbol: Symbol.Important } || Data.Conflicts.Count > 0);
                if(command.Icon is SymbolIcon { Symbol: Symbol.Important })command.Label=L("ApiConflicts",("count",Data.Conflicts.Count));
            }
        if (firstPage != null) firstPage.IsEnabled = enabled && model.Page > 1;
        if (previousPage != null) previousPage.IsEnabled = enabled && model.Page > 1;
        if (nextPage != null) nextPage.IsEnabled = enabled && model.Page < model.PageCount;
        if (lastPage != null) lastPage.IsEnabled = enabled && model.Page < model.PageCount;
        if (pageGo != null) pageGo.IsEnabled = enabled && model.Total > 0;
        if (pageJump != null) { pageJump.IsEnabled = enabled; pageJump.Text = model.Page.ToString(); }
        if (pageSize != null) pageSize.IsEnabled = enabled;
        if (selectPage != null) selectPage.IsEnabled = enabled && visible.Count > 0;
        if (selectAll != null) selectAll.IsEnabled = enabled && model.Total > 0;
        if (clearSelection != null) clearSelection.IsEnabled = enabled && model.Selected.Count > 0;
        if (queryCancel != null) queryCancel.Visibility = querying ? Visibility.Visible : Visibility.Collapsed;
        if (pageSummary != null) pageSummary.Text = PageSummary();
        if (selectionSummary != null) selectionSummary.Text = SelectionSummary();
    }
    void RestoreDataSelection()
    {
        if (table is null || shownState is null) return;
        restoringDataSelection = true;
        try { table.SelectedItems.Clear(); foreach (var row in visible) if (shownState.Model.Selected.Contains(row.Row.LocalId)) table.SelectedItems.Add(row); }
        finally { restoringDataSelection = false; }
        RefreshDataAvailability();
    }
    void DataSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (restoringDataSelection || shownState is null) return;
        foreach (var row in args.RemovedItems.Cast<RowViewModel>()) shownState.Model.Selected.Remove(row.Row.LocalId);
        foreach (var row in args.AddedItems.Cast<RowViewModel>()) shownState.Model.Selected.Add(row.Row.LocalId);
        RefreshDataAvailability();
    }
    void RestoreDataOffsets(DataPageState state)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!ReferenceEquals(shownState, state) || table is null) return;
            FindVisual<ScrollViewer>(table)?.ChangeView(null, state.Vertical, null, true);
            horizontalTable?.ChangeView(state.Horizontal, null, null, true);
        });
    }
    List<DataRow> SelectedRows() => shownState?.Model.Selected.Select(Data.FindDraftRow).OfType<DataRow>().ToList() ?? [];
    List<DataRow> FilteredRows() => shownState?.Model.ResultIds.Select(Data.FindDraftRow).OfType<DataRow>().ToList() ?? [];
}
