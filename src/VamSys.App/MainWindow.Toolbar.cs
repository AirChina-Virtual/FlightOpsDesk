using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using VamSys.Core;

namespace VamSys.App;

// The data page's two bars. Above the table: search, the filter as choices in view, the sort and the view menu. Below
// them: add, the commands for the selected records, the data commands, undo and redo, and review on the right. The
// resource is chosen with tabs that carry each resource's number of pending changes.
public sealed partial class MainWindow
{
    // Columns hidden from the view menu, per resource, for this run of the program.
    readonly Dictionary<ResourceKind, HashSet<string>> hiddenColumns = [];
    // The menu commands and the selection they need ("active", "deleted", "delete"), and whether they apply here.
    readonly List<(MenuFlyoutItem Item, string? Tag, Func<bool> Applies)> menuCommands = [];
    Grid? dataActions;
    DropDownButton? batchMenu, dataMenu, viewMenu;
    SplitButton? addButton;
    Button? undoButton, redoButton, reviewButton;
    MenuFlyoutItem? conflictItem, refreshItem;

    // --- Resource tabs ---
    void InitializeResourceTabs()
    {
        ResourcePicker.Items.Clear();
        foreach (var kind in Enum.GetValues<ResourceKind>())
            ResourcePicker.Items.Add(new SelectorBarItem());
        RefreshResourceTabs();
        SelectResourceTab(resource);
    }
    void SelectResourceTab(ResourceKind kind)
    {
        if (ResourcePicker.Items.Count > (int)kind && !ReferenceEquals(ResourcePicker.SelectedItem, ResourcePicker.Items[(int)kind]))
            ResourcePicker.SelectedItem = ResourcePicker.Items[(int)kind];
    }
    // Each tab says how many records of its resource have changes to submit, once that is counted.
    void RefreshResourceTabs()
    {
        foreach (var kind in Enum.GetValues<ResourceKind>())
        {
            if (ResourcePicker.Items.Count <= (int)kind)
                return;
            var name = L(Schemas.All[kind].Name);
            var text = workspace.Resources.TryGetValue(kind, out var data) && changeCounts.TryGetValue(data, out var count) && count > 0 ? $"{name} · {count}" : name;
            var item = ResourcePicker.Items[(int)kind];
            if (item.Text != text)
            {
                item.Text = text;
                AutomationProperties.SetName(item, text);
            }
        }
        AutomationProperties.SetName(ResourcePicker, L("ResourceType"));
    }
    void ResourceChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs e)
    {
        var index = sender.SelectedItem is null ? -1 : sender.Items.IndexOf(sender.SelectedItem);
        if (!ready || index < 0 || index == (int)resource)
            return;
        if (!gate.CanSwitchContext)
        { SelectResourceTab(resource); return; }
        CancelDataQuery();
        resource = (ResourceKind)index;
        Render();
    }

    // --- Above the table ---
    UIElement BuildQueryBar(DataPageState state)
    {
        var search = new TextBox { Width = UiTokens.SearchBoxWidth }.Localize(localization, "PlaceholderText", () => L("Text_762B9F2664"));
        AutomationProperties.SetAutomationId(search, "DataSearch");
        ToolTipService.SetToolTip(search, new ToolTip().Localize(localization, "Content", () => L("SearchShortcutHint")));
        var filter = new ChoiceChips(3);
        AutomationProperties.SetAutomationId(filter.Chip(0), "DataFilterAll");
        AutomationProperties.SetAutomationId(filter.Chip(1), "DataFilterModified");
        AutomationProperties.SetAutomationId(filter.Chip(2), "DataFilterDeleted");
        var sort = new SortPicker(field => ApiOperationPolicy.FieldLabel(field, localization), key => L(key)) { ItemsSource = AllColumns() };
        searchBox = search;
        filterBox = filter;
        sortBox = sort;
        search.Text = state.Search;
        filter.SelectedIndex = state.Filter;
        if (DataSort.Parse(state.Sort).Field is { } field && sort.Items.Contains(field))
            sort.SelectedItem = state.Sort;
        RefreshFilterLabels();
        queryNotice = Text(() => "");
        queryNotice.Style = UiTokens.SecondaryTextStyle;
        queryNotice.VerticalAlignment = VerticalAlignment.Center;
        var bar = Row(search, filter.Panel, sort.Button, BuildViewMenu(), queryNotice);
        bar.Spacing = UiTokens.SpacingMedium;
        // The conditions apply by themselves: typing after a short pause, a choice at once, Enter without waiting.
        search.TextChanged += (_, _) => { RefreshQueryNotice(); QueryConditionsChanged(wait: true); };
        filter.SelectionChanged += () => { RefreshQueryNotice(); QueryConditionsChanged(wait: false); };
        sort.SelectionChanged += () =>
        {
            tableView?.ShowSort(sort.Current);
            RefreshQueryNotice();
            QueryConditionsChanged(wait: false);
        };
        search.KeyDown += async (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter && CanEditData()) { e.Handled = true; await ApplyQueryConditions(); } };
        return new ScrollViewer { Content = bar, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }
    // The filter choices; the changed one says how many records have changes, once that is counted.
    void RefreshFilterLabels()
    {
        var modified = changeCounts.TryGetValue(Data, out var count) && count > 0 ? $"{L("Text_682D211B71")} {count}" : L("Text_682D211B71");
        filterBox?.SetLabels([L("Text_5C55A67935"), modified, L("Text_549986E82C")]);
    }
    // A click on a column header: by it, then the other way round, then no sort.
    void SortByHeader(string field)
    {
        if (sortBox is null || !CanEditData())
            return;
        sortBox.SelectedItem = DataSort.Next(sortBox.SelectedItem as string, field);
    }

    // Density and the columns shown.
    DropDownButton BuildViewMenu()
    {
        var menu = new MenuFlyout();
        var button = viewMenu = new DropDownButton { Flyout = menu }.Localize(localization, "Content", () => L("TableView"));
        AutomationProperties.SetAutomationId(button, "DataView");
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();
            RadioMenuFlyoutItem Density(string key, string value)
            {
                var item = new RadioMenuFlyoutItem { Text = L(key), GroupName = "density", IsChecked = tableDensity == value };
                item.Click += async (_, _) => { if (CanEditData() && value != tableDensity) await UserAction(() => ChangeTableDensity(value)); };
                return item;
            }
            menu.Items.Add(Density("DensityComfortable", "comfortable"));
            menu.Items.Add(Density("DensityCompact", "compact"));
            menu.Items.Add(new MenuFlyoutSeparator());
            var columns = new MenuFlyoutSubItem { Text = L("TableColumns") };
            var hidden = hiddenColumns.TryGetValue(resource, out var set) ? set : [];
            var fixedColumns = new[] { "_delete", Schemas.All[resource].Key };
            foreach (var column in AllColumns().Where(c => !fixedColumns.Contains(c)))
            {
                var item = new ToggleMenuFlyoutItem { Text = ApiOperationPolicy.FieldLabel(column, localization), IsChecked = !hidden.Contains(column) };
                item.Click += (_, _) => ShowColumn(column, item.IsChecked);
                columns.Items.Add(item);
            }
            columns.Items.Add(new MenuFlyoutSeparator());
            var all = new MenuFlyoutItem { Text = L("TableColumnsAll"), IsEnabled = hidden.Count > 0 };
            all.Click += (_, _) => { hiddenColumns.Remove(resource); ShowDataPageRows(false); };
            columns.Items.Add(all);
            menu.Items.Add(columns);
        };
        return button;
    }
    void ShowColumn(string column, bool shown)
    {
        if (!CanEditData())
            return;
        if (!hiddenColumns.TryGetValue(resource, out var hidden))
            hiddenColumns[resource] = hidden = new(StringComparer.Ordinal);
        if (shown)
            hidden.Remove(column);
        else
            hidden.Add(column);
        ShowDataPageRows(false);
    }

    // --- The commands ---
    Grid BuildActionBar()
    {
        menuCommands.Clear();
        MenuFlyoutItem Item(Func<string> label, Symbol icon, Func<Task> action, string? tag = null, Func<bool>? applies = null)
        {
            var item = new MenuFlyoutItem { Icon = new SymbolIcon(icon) }.Localize(localization, "Text", label);
            item.Click += async (_, _) => { if (gate.CanInvokeCommand) await UserAction(action); };
            menuCommands.Add((item, tag, applies ?? (() => true)));
            return item;
        }

        // Add: a new record, or records from a CSV file.
        var addMenu = new MenuFlyout();
        var import = new MenuFlyoutItem { Icon = new SymbolIcon(Symbol.Import) }.Localize(localization, "Text", () => L("AddFromCsv"));
        import.Click += (_, _) => SelectPane(ShellPage.Import);
        addMenu.Items.Add(import);
        addButton = new SplitButton { Flyout = addMenu, Content = IconText(Symbol.Add, () => L("Text_0006D696D8")) };
        AutomationProperties.SetAutomationId(addButton, "DataAdd");
        addButton.Click += async (_, _) => { if (gate.CanInvokeCommand) await UserAction(async () => { if (!gate.CanAct) return; await batch.AddRowAsync(resource); Render(); }); };

        // The selected records.
        var selection = new MenuFlyout();
        selection.Items.Add(Item(() => L("Text_E306752B35"), Symbol.Edit, EditRules, "active"));
        selection.Items.Add(Item(() => L("Text_95DD1FDDFE"), Symbol.People, () => resource is ResourceKind.Aircraft or ResourceKind.Routes ? ChooseFleets(null) : Task.CompletedTask, "active",
            () => resource is ResourceKind.Aircraft or ResourceKind.Routes));
        selection.Items.Add(Item(() => L("Text_7AA882D2FE"), Symbol.Clock, () => resource == ResourceKind.Routes ? RetireSelected() : Task.CompletedTask, "active", () => resource == ResourceKind.Routes));
        selection.Items.Add(Item(() => L("RowDuplicate"), Symbol.Copy, DuplicateSelected, "active"));
        selection.Items.Add(Item(() => L("RowDetails"), Symbol.OpenPane, () => { OpenSelectedDetails(); return Task.CompletedTask; }, "active"));
        selection.Items.Add(new MenuFlyoutSeparator());
        selection.Items.Add(Item(() => L("Text_435BF8A234"), Symbol.Delete, DeleteSelected, "delete"));
        selection.Items.Add(Item(() => L("Text_A6926595CE"), Symbol.Undo, RestoreSelected, "deleted"));
        batchMenu = new DropDownButton { Flyout = selection };
        AutomationProperties.SetAutomationId(batchMenu, "DataBatchMenu");
        ToolTipService.SetToolTip(batchMenu, new ToolTip().Localize(localization, "Content", () => L("BatchOnlySelected")));

        // Loading from the server, conflicts and checks.
        var data = new MenuFlyout();
        refreshItem = Item(() => L("ApiRefresh"), Symbol.Refresh, RefreshApi);
        conflictItem = Item(() => L("ApiConflicts", ("count", Data.Conflicts.Count)), Symbol.Important, ResolveApiConflicts);
        data.Items.Add(refreshItem);
        data.Items.Add(conflictItem);
        data.Items.Add(new MenuFlyoutSeparator());
        data.Items.Add(Item(() => L("Text_41B0AF8C5C"), Symbol.Accept, Validate));
        dataMenu = new DropDownButton { Flyout = data };
        AutomationProperties.SetAutomationId(dataMenu, "DataMenu");

        Button IconButton(Symbol icon, string labelKey, string tipKey, Func<Task> action)
        {
            var button = new Button { Content = new SymbolIcon(icon), Padding = UiTokens.IconButtonPadding };
            AutomationProperties.SetName(button, L(labelKey));
            ToolTipService.SetToolTip(button, new ToolTip().Localize(localization, "Content", () => L(tipKey)));
            button.Click += async (_, _) => { if (gate.CanInvokeCommand) await UserAction(action); };
            return button;
        }
        undoButton = IconButton(Symbol.Undo, "Text_926A50B98E", "ShortcutUndo", () => MoveHistory(undo: true));
        redoButton = IconButton(Symbol.Redo, "Text_03717B6F10", "ShortcutRedo", () => MoveHistory(undo: false));

        reviewButton = new Button { Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        reviewButton.Click += (_, _) => { if (gate.CanInvokeCommand) SelectPane(ShellPage.Review); };

        var left = Row(addButton, batchMenu, dataMenu, undoButton, redoButton);
        left.Spacing = UiTokens.SpacingMedium;
        var bar = new Grid { ColumnSpacing = UiTokens.SpacingMedium };
        bar.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        bar.Children.Add(new ScrollViewer { Content = left, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Grid.SetColumn(reviewButton, 1);
        bar.Children.Add(reviewButton);
        dataActions = bar;
        RefreshCommandLabels();
        return bar;
    }
    StackPanel IconText(Symbol icon, Func<string> text)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = UiTokens.SpacingMedium };
        row.Children.Add(new SymbolIcon(icon));
        row.Children.Add(Text(text));
        return row;
    }
    // The labels that carry numbers: the selection count, the conflicts and the pending changes.
    void RefreshCommandLabels()
    {
        if (batchMenu is null || dataMenu is null || reviewButton is null)
            return;
        var selected = shownState?.Model.Selected.Count ?? 0;
        var batch = selected > 0 ? L("BatchMenuSelected", ("count", selected)) : L("BatchMenu");
        batchMenu.Content = batch;
        AutomationProperties.SetName(batchMenu, batch);
        var conflicts = Data.Conflicts.Count;
        var data = conflicts > 0 ? L("DataMenuConflicts", ("count", conflicts)) : L("DataMenu");
        dataMenu.Content = data;
        AutomationProperties.SetName(dataMenu, data);
        var pending = workspace.Resources.Values.All(changeCounts.ContainsKey) ? workspace.Resources.Values.Sum(d => changeCounts[d]) : 0;
        var review = L("Text_48A195CA7E");
        reviewButton.Content = pending > 0 ? $"{review} · {pending}" : review;
        AutomationProperties.SetName(reviewButton, review);
        if (addButton != null)
            AutomationProperties.SetName(addButton, L("Text_0006D696D8"));
        if (undoButton != null)
            AutomationProperties.SetName(undoButton, L("Text_926A50B98E"));
        if (redoButton != null)
            AutomationProperties.SetName(redoButton, L("Text_03717B6F10"));
    }
    // What the commands can do now: editing allowed, the selection, the resource, online and conflicts.
    void RefreshCommands(bool enabled, (int Active, int Deleted, int Deletable) counts)
    {
        foreach (var (item, tag, applies) in menuCommands)
        {
            var shown = applies();
            item.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
            item.IsEnabled = shown && DataGridAvailability.Command(enabled, tag, ReferenceEquals(item, conflictItem), counts, Data.Conflicts.Count)
                && (!ReferenceEquals(item, refreshItem) && !ReferenceEquals(item, conflictItem) || gate.CanRequestOnline);
        }
        if (batchMenu != null)
            batchMenu.IsEnabled = enabled && (shownState?.Model.Selected.Count ?? 0) > 0;
        foreach (var control in new Control?[] { addButton, dataMenu, undoButton, redoButton, viewMenu })
            if (control != null)
                control.IsEnabled = enabled;
        if (filterBox != null)
            filterBox.IsEnabled = enabled;
        if (sortBox != null)
            sortBox.Button.IsEnabled = enabled;
        RefreshCommandLabels();
        RefreshFilterLabels();
    }
    // For the window scripts: each selection command and whether it can run now.
    object[] BatchCommandStates() => [.. menuCommands.Where(c => c.Tag != null).Select(c => new { target = c.Tag, enabled = c.Item.IsEnabled })];

    // --- The row menu ---
    void ShowRowMenu(RowViewModel row, CellViewModel? cell, FrameworkElement target, Windows.Foundation.Point? at)
    {
        if (!CanEditData() || shownState is null)
            return;
        // As in a file list: the menu acts on the selection, and a row outside it becomes the selection.
        if (!shownState.Model.Selected.Contains(row.Row.LocalId))
        {
            shownState.Model.Selected.Clear();
            shownState.Model.Selected.Add(row.Row.LocalId);
            RestoreDataSelection();
        }
        var menu = new MenuFlyout();
        MenuFlyoutItem Add(string text, Symbol icon, Func<Task> action, bool enabled = true)
        {
            var item = new MenuFlyoutItem { Text = text, Icon = new SymbolIcon(icon), IsEnabled = enabled };
            item.Click += async (_, _) => { if (gate.CanInvokeCommand) await UserAction(action); };
            menu.Items.Add(item);
            return item;
        }
        if (cell != null && cell.Field != "_delete")
        {
            Add(L("CellCopy"), Symbol.Copy, () =>
            {
                var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
                package.SetText(cell.Value);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
                return Task.CompletedTask;
            });
            if (cell.ServerValue is { } server)
                Add(L("CellRestoreServer"), Symbol.Undo, () => { cell.Value = server; cell.EditEnded?.Invoke(false); return Task.CompletedTask; }, !cell.IsReadOnly);
            menu.Items.Add(new MenuFlyoutSeparator());
        }
        Add(L("RowDetails"), Symbol.OpenPane, () => { OpenDetails(row.Row.LocalId); return Task.CompletedTask; });
        Add(L("RowDuplicate"), Symbol.Copy, DuplicateSelected);
        if (resource is ResourceKind.Aircraft or ResourceKind.Routes)
            Add(L("Text_95DD1FDDFE"), Symbol.People, () => ChooseFleets(null));
        menu.Items.Add(new MenuFlyoutSeparator());
        if (Schemas.Deleted(row.Row))
            Add(L("Text_A6926595CE"), Symbol.Undo, RestoreSelected);
        else
            Add(L("Text_435BF8A234"), Symbol.Delete, DeleteSelected);
        if (at is { } point)
            menu.ShowAt(target, point);
        else
            menu.ShowAt(target);
    }
}
