using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using VamSys.Core;

namespace VamSys.App;

// The data page's two bars. Above the table: search, the filter as choices in view, the sort and the view menu. Below
// them: add, the commands for the selected records, the data commands, undo and redo, and review on the right. The
// resource is chosen with tabs that carry each resource's number of pending changes.
public sealed partial class MainWindow
{
    // Columns hidden from the view menu, per resource, for this run of the program.
    readonly Dictionary<ResourceKind, HashSet<string>> hiddenColumns = [];
    // The commands and the selection they need ("active", "deleted", "delete"), and whether they apply here.
    readonly List<(Control Item, string? Tag, Func<bool> Applies)> menuCommands = [];
    Grid? dataActions;
    DropDownButton? viewMenu, moreMenu;
    SplitButton? addButton, refreshButton;
    Button? undoButton, redoButton, reviewButton, conflictButton, validateButton, selectAllButton, clearSelectionButton;
    MenuFlyoutItem? refreshResourceItem;
    // The bar shows the everyday commands, or (with records selected) the commands for the selection.
    StackPanel? idleCommands, selectionCommands;
    TextBlock? selectionCount;
    // Buttons whose content is an icon and a label: their automation name follows the label.
    readonly List<(Button Button, Func<string> Label)> namedCommands = [];

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
        namedCommands.Clear();
        T Track<T>(T item, string? tag, Func<bool>? applies = null) where T : Control
        {
            menuCommands.Add((item, tag, applies ?? (() => true)));
            return item;
        }
        MenuFlyoutItem Item(Func<string> label, Symbol icon, Func<Task> action, string? tag = null, Func<bool>? applies = null)
        {
            var item = new MenuFlyoutItem { Icon = new SymbolIcon(icon) }.Localize(localization, "Text", label);
            item.Click += async (_, _) => { if (gate.CanInvokeCommand) await UserAction(action); };
            return Track(item, tag, applies);
        }
        Button Command(Func<string> label, Symbol icon, Func<Task> action, string automationId, string? tag = null, Func<bool>? applies = null)
        {
            var button = new Button { Content = IconText(icon, label) };
            AutomationProperties.SetAutomationId(button, automationId);
            namedCommands.Add((button, label));
            button.Click += async (_, _) => { if (gate.CanInvokeCommand) await UserAction(action); };
            return Track(button, tag, applies);
        }
        bool Fleets() => resource is ResourceKind.Aircraft or ResourceKind.Routes;
        bool Routes() => resource == ResourceKind.Routes;

        // Add: a new record, or records from a CSV file.
        var addMenu = new MenuFlyout();
        var import = new MenuFlyoutItem { Icon = new SymbolIcon(Symbol.Import) }.Localize(localization, "Text", () => L("AddFromCsv"));
        import.Click += (_, _) => SelectPane(ShellPage.Import);
        addMenu.Items.Add(import);
        addButton = new SplitButton { Flyout = addMenu, Content = IconText(Symbol.Add, () => L("Text_0006D696D8")) };
        AutomationProperties.SetAutomationId(addButton, "DataAdd");
        addButton.Click += async (_, _) => { if (gate.CanInvokeCommand) await UserAction(async () => { if (!gate.CanAct) return; await batch.AddRowAsync(resource); Render(); }); };

        // Loading from the server (everything, or only this resource and what it refers to), checks and conflicts.
        var refreshMenu = new MenuFlyout();
        refreshResourceItem = Item(() => L("ApiRefreshResource", ("resource", L(Schemas.All[resource].Name))), Symbol.Sync, RefreshResourceApi);
        refreshMenu.Items.Add(refreshResourceItem);
        refreshButton = Track(new SplitButton { Flyout = refreshMenu, Content = IconText(Symbol.Refresh, () => L("ApiRefresh")) }, null);
        AutomationProperties.SetAutomationId(refreshButton, "DataRefresh");
        refreshButton.Click += async (_, _) => { if (gate.CanInvokeCommand) await UserAction(RefreshApi); };
        validateButton = Command(() => L("Text_41B0AF8C5C"), Symbol.Accept, Validate, "DataValidate");
        conflictButton = Command(() => L("ApiConflicts", ("count", Data.Conflicts.Count)), Symbol.Important, ResolveApiConflicts, "DataConflicts");
        idleCommands = Row(addButton, refreshButton, validateButton, conflictButton);
        idleCommands.Spacing = UiTokens.SpacingMedium;

        // The selected records: the count, the commands for them, more of them in a menu, and leaving the selection.
        selectionCount = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        AutomationProperties.SetAutomationId(selectionCount, "DataSelectionCount");
        var edit = Command(() => L("Text_E306752B35"), Symbol.Edit, EditRules, "DataBatchRule", "active");
        var fleets = Command(() => L("Text_95DD1FDDFE"), Symbol.People, () => Fleets() ? ChooseFleets(null) : Task.CompletedTask, "DataChooseFleets", "active", Fleets);
        var duplicate = Command(() => L("RowDuplicate"), Symbol.Copy, DuplicateSelected, "DataDuplicate", "active");
        var delete = Command(() => L("Text_435BF8A234"), Symbol.Delete, DeleteSelected, "DataDelete", "delete");
        var more = new MenuFlyout();
        more.Items.Add(Item(() => L("Text_7AA882D2FE"), Symbol.Clock, () => Routes() ? RetireSelected() : Task.CompletedTask, "active", Routes));
        more.Items.Add(Item(() => L("RowDetails"), Symbol.OpenPane, () => { OpenSelectedDetails(); return Task.CompletedTask; }, "active"));
        more.Items.Add(Item(() => L("Text_A6926595CE"), Symbol.Undo, RestoreSelected, "deleted"));
        moreMenu = new DropDownButton { Content = new SymbolIcon(Symbol.More), Flyout = more, Padding = UiTokens.IconButtonPadding };
        AutomationProperties.SetAutomationId(moreMenu, "DataMoreMenu");
        AutomationProperties.SetName(moreMenu, L("DataMoreActions"));
        ToolTipService.SetToolTip(moreMenu, new ToolTip().Localize(localization, "Content", () => L("DataMoreActions")));
        selectAllButton = new Button();
        AutomationProperties.SetAutomationId(selectAllButton, "DataSelectAll");
        selectAllButton.Click += async (_, _) => { if (gate.CanInvokeCommand) await UserAction(() => { SelectAllResults(); return Task.CompletedTask; }); };
        clearSelectionButton = new Button { Content = IconText(Symbol.Cancel, () => L("TableClearSelection")) };
        namedCommands.Add((clearSelectionButton, () => L("TableClearSelection")));
        AutomationProperties.SetAutomationId(clearSelectionButton, "DataClearSelection");
        clearSelectionButton.Click += async (_, _) => { if (gate.CanInvokeCommand) await UserAction(() => { ClearDataSelection(); return Task.CompletedTask; }); };
        selectionCommands = Row(selectionCount, edit, fleets, duplicate, delete, moreMenu, selectAllButton, clearSelectionButton);
        selectionCommands.Spacing = UiTokens.SpacingMedium;
        selectionCommands.Visibility = Visibility.Collapsed;

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

        var left = Row(idleCommands, selectionCommands, undoButton, redoButton);
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
        if (reviewButton is null || selectionCount is null || selectAllButton is null)
            return;
        var model = shownState?.Model;
        var selected = model?.Selected.Count ?? 0;
        selectionCount.Text = L("DataSelectedCount", ("count", selected));
        var total = model?.Total ?? 0;
        var all = L("DataSelectAllResults", ("count", total));
        selectAllButton.Content = all;
        AutomationProperties.SetName(selectAllButton, all);
        var pending = workspace.Resources.Values.All(changeCounts.ContainsKey) ? workspace.Resources.Values.Sum(d => changeCounts[d]) : 0;
        var review = L("Text_48A195CA7E");
        reviewButton.Content = pending > 0 ? $"{review} · {pending}" : review;
        AutomationProperties.SetName(reviewButton, review);
        if (addButton != null)
            AutomationProperties.SetName(addButton, L("Text_0006D696D8"));
        if (refreshButton != null)
            AutomationProperties.SetName(refreshButton, L("ApiRefresh"));
        if (undoButton != null)
            AutomationProperties.SetName(undoButton, L("Text_926A50B98E"));
        if (redoButton != null)
            AutomationProperties.SetName(redoButton, L("Text_03717B6F10"));
        if (moreMenu != null)
            AutomationProperties.SetName(moreMenu, L("DataMoreActions"));
        foreach (var (button, label) in namedCommands)
            AutomationProperties.SetName(button, label());
    }
    // What the commands can do now: editing allowed, the selection, the resource, online and conflicts. With records
    // selected the bar shows the commands for them; "select all results" appears once the whole page is selected.
    void RefreshCommands(bool enabled, (int Active, int Deleted, int Deletable) counts)
    {
        var model = shownState?.Model;
        var selected = model?.Selected.Count ?? 0;
        foreach (var (item, tag, applies) in menuCommands)
        {
            var shown = applies();
            item.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
            var online = !ReferenceEquals(item, refreshButton) && !ReferenceEquals(item, refreshResourceItem) && !ReferenceEquals(item, conflictButton) || gate.CanRequestOnline;
            item.IsEnabled = shown && online && (tag is null && !ReferenceEquals(item, conflictButton) ? enabled
                : DataGridAvailability.Command(enabled, tag, ReferenceEquals(item, conflictButton), counts, Data.Conflicts.Count));
        }
        if (conflictButton != null)
            conflictButton.Visibility = Data.Conflicts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        // The label names the page's resource, which changes without a language change.
        if (refreshResourceItem != null)
            refreshResourceItem.Text = L("ApiRefreshResource", ("resource", L(Schemas.All[resource].Name)));
        if (idleCommands != null && selectionCommands != null)
        {
            var hiding = selected > 0 ? idleCommands : selectionCommands;
            // Hiding the focused button would send the focus on to the search box, which then takes Ctrl+Z and typing:
            // the focus goes to the table (or, while it is locked, to undo) first.
            if (hiding.Visibility == Visibility.Visible && FocusManager.GetFocusedElement(RootGrid.XamlRoot) is DependencyObject focused && Within(focused, hiding)
                && tableView?.Table.Focus(FocusState.Programmatic) != true)
                undoButton?.Focus(FocusState.Programmatic);
            idleCommands.Visibility = selected > 0 ? Visibility.Collapsed : Visibility.Visible;
            selectionCommands.Visibility = selected > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        var pageSelected = model != null && visible.Count > 0 && visible.All(r => model.Selected.Contains(r.Row.LocalId));
        if (selectAllButton != null && model != null)
        {
            selectAllButton.Visibility = pageSelected && selected < model.Total ? Visibility.Visible : Visibility.Collapsed;
            selectAllButton.IsEnabled = enabled;
        }
        if (clearSelectionButton != null)
            clearSelectionButton.IsEnabled = enabled;
        if (moreMenu != null)
            moreMenu.IsEnabled = enabled && selected > 0;
        tableView?.ShowPageSelection(model is null || visible.Count == 0 ? false : pageSelected ? true : visible.Any(r => model.Selected.Contains(r.Row.LocalId)) ? null : false, enabled && visible.Count > 0);
        foreach (var control in new Control?[] { addButton, undoButton, redoButton, viewMenu })
            if (control != null)
                control.IsEnabled = enabled;
        if (filterBox != null)
            filterBox.IsEnabled = enabled;
        if (sortBox != null)
            sortBox.Button.IsEnabled = enabled;
        RefreshCommandLabels();
        RefreshFilterLabels();
    }
    static bool Within(DependencyObject element, DependencyObject ancestor)
    {
        for (var node = element; node != null; node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node))
            if (ReferenceEquals(node, ancestor))
                return true;
        return false;
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
