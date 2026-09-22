using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using System.Text;
using VamSys.Core;
using VamSys.Infrastructure;
using Windows.Storage.Pickers;

namespace VamSys.App;

public sealed partial class MainWindow : Window
{
    readonly WorkspaceStore store = new(Environment.GetEnvironmentVariable("VAMSYS_DATA_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VamSysBatch"));
    readonly CsvAdapter csv = new(); readonly ChangePlanner planner = new();
    readonly FleetAssignmentService fleets = new(); readonly DeletionService deletions = new();
    Workspace workspace = new(); ResourceKind resource = ResourceKind.Routes;
    string page = "workspace"; bool ready, busy, storageBlocked; string? lastEditedCell;
    ListView? table; List<RowViewModel> visible = [];
    CancellationTokenSource? running;
    readonly SemaphoreSlim saveGate = new(1);
    readonly DispatcherTimer autosave = new() { Interval = TimeSpan.FromMilliseconds(700) };
    ResourceData Data => workspace.Resources[resource];

    public MainWindow()
    {
        InitializeComponent(); InitializeLanguage();
#if UI_VERIFICATION
        // Isolated QA builds only; production publishing has no theme override.
        RootGrid.RequestedTheme = Environment.GetEnvironmentVariable("VAMSYS_QA_THEME") == "light" ? ElementTheme.Light : ElementTheme.Dark;
#endif
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary).WorkArea;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(Math.Min(1400, area.Width), Math.Min(900, area.Height)));
        RootGrid.ActualThemeChanged += (_, _) => UpdateCaptionTheme();
        RootGrid.Loaded += (_, _) => UpdateCaptionTheme();
        RootGrid.SizeChanged += (_, _) => { WorkspacePicker.Width = RootGrid.ActualWidth < 800 ? 140 : 230; AppTitleBar.Subtitle = RootGrid.ActualWidth < 1100 ? "" : L("Text_9750099F9F"); };
        if (store.List().Count == 0) { workspace.Name = L("Text_707DBE0213"); store.Save(workspace); }
        workspace = store.Load(store.List()[0].Id);
        if (workspace.Mode == RunMode.Online) workspace.Mode = RunMode.Offline;
        ResourcePicker.ItemsSource = Enum.GetValues<ResourceKind>().Select(k => L(Schemas.All[k].Name)).ToList();
        ResourcePicker.SelectedIndex = (int)resource;
        ReloadWorkspaces();
        autosave.Tick += async (_, _) => { autosave.Stop(); await Guard(async () => { await Save(); if (!busy) Status(() => L("Text_11584EAAA5")); }); };
        AppWindow.Closing += async (_, e) =>
        {
            if(closeState.State==WindowCloseState.Closed)return;
            e.Cancel=true;
            if(!closeState.IsOpen)return;
            if(busy||interactiveOperations>0||dialogs>0){Status(()=>L("CloseBlocked"));return;}
            try {
                var closed=await closeState.RequestAsync(false,async()=>{
                    if(!storageBlocked)await Save();
                },FreezeClosing);
                if(closed)Close();
            } catch {Status(()=>L("Text_7AF24B0E8E"));}
        };
        ready = true; Navigation.SelectedItem = Navigation.MenuItems[0]; Render();
#if UI_VERIFICATION
        InitializeVerification();
#endif
    }
    void Status(Func<string> message) { statusText = message; StatusLabel.Text = message(); }
    void ToggleNavigation(TitleBar sender, object args) => Navigation.IsPaneOpen = !Navigation.IsPaneOpen;
    void UpdateCaptionTheme()
    {
        var caption = AppWindow.TitleBar;
        caption.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        caption.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        caption.ButtonForegroundColor = new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast
            ? new Windows.UI.ViewManagement.UISettings().GetColorValue(Windows.UI.ViewManagement.UIColorType.Foreground)
            : RootGrid.ActualTheme == ElementTheme.Light ? Microsoft.UI.Colors.Black : Microsoft.UI.Colors.White;
    }
    void ReloadWorkspaces()
    {
        bool wasReady = ready; ready = false;
        WorkspacePicker.ItemsSource = store.List().Select(x => new WorkspaceChoice(x.Id, x.Name)).ToList();
        WorkspacePicker.SelectedItem = ((List<WorkspaceChoice>)WorkspacePicker.ItemsSource).First(x => x.Id == workspace.Id);
        ready = wasReady;
    }
    async Task Save()
    {
        await saveGate.WaitAsync();
        try { if(storageBlocked) throw OperationsAdapter.Block("StorageReload"); await store.SaveAsync(workspace); }
        finally { saveGate.Release(); }
    }
    void SaveSoon() { if(!closeState.IsOpen)return; autosave.Stop(); autosave.Start(); }
    async Task Guard(Func<Task> operation)
    {
        try { await operation(); }
        catch (Exception e) { var description = MessageErrors.Describe(e); Status(() => L("Text_AEB8A69DBE") + L(description)); }
    }
    async Task Work(Func<Task> operation)
    {
        if (!closeState.IsOpen || busy || !closeState.IsOpen) return;
        if(storageBlocked) throw OperationsAdapter.Block("StorageReload");
        var pendingSave=autosave.IsEnabled;autosave.Stop();
        busy = true; BusyRing.IsActive = true; WorkspacePicker.IsEnabled = false; ResourcePicker.IsEnabled = false;
        try { if(pendingSave) await Save(); await operation(); }
        finally { busy = false; BusyRing.IsActive = false; WorkspacePicker.IsEnabled = true; ResourcePicker.IsEnabled = true; Render(); }
    }
    Button Button(Func<string> label, Func<Task> action, bool enabled = true)
    {
        var b = new Button { IsEnabled = enabled }.Localize(localization, "Content", () => label()); b.Click += async (_, _) => await UserAction(action); return b;
    }
    AppBarButton Command(Func<string> label, Symbol icon, Func<Task> action, bool enabled = true)
    {
        var button = new AppBarButton { Icon = new SymbolIcon(icon), IsEnabled = enabled }.Localize(localization, "Label", () => label());
        button.Click += async (_, _) => await UserAction(action); return button;
    }
    static StackPanel Stack(params UIElement[] children) { var s = new StackPanel { Spacing = 12 }; foreach (var c in children) s.Children.Add(c); return s; }
    static StackPanel Row(params UIElement[] children) { var s = Stack(children); s.Orientation = Orientation.Horizontal; return s; }
    TextBlock Text(Func<string> text, double size = 14) => new TextBlock { FontSize = size, TextWrapping = TextWrapping.Wrap }.Localize(localization, "Text", () => text());
    static ScrollViewer Scroll(UIElement content) => new() { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    static ScrollViewer Actions(params UIElement[] controls) => new() { Content = Row(controls), HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
    async Task<bool> Confirm(string title, UIElement content, string? primary = null)
    {
        var d = new ContentDialog { XamlRoot = Content.XamlRoot, Title = title, Content = content, PrimaryButtonText = primary ?? L("Confirm"), CloseButtonText = L("Text_2CD0F3BE87"), DefaultButton = ContentDialogButton.Close };
        dialogs++; try { return await d.ShowAsync() == ContentDialogResult.Primary; } finally { dialogs--; }
    }
    void Navigate(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (!ready || !closeState.IsOpen) return; page = (args.SelectedItem as NavigationViewItem)?.Tag?.ToString() ?? "workspace"; Render();
    }
    async void WorkspaceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || !closeState.IsOpen || busy || WorkspacePicker.SelectedItem is not WorkspaceChoice choice || choice.Id == workspace.Id) return;
        await UserAction(async () => { autosave.Stop(); await Save(); workspace = store.Load(choice.Id); if (apiSessions.Remove(choice.Id, out var previous)) previous.Client.Dispose(); if (workspace.Mode == RunMode.Online) workspace.Mode = RunMode.Offline; Render(); });
    }
    void ResourceChanged(object sender, SelectionChangedEventArgs e)
    { if (!ready || !closeState.IsOpen || busy) return; resource = (ResourceKind)ResourcePicker.SelectedIndex; Render(); }
    void Render()
    {
        if (!ready) return;
        CapturePageState();CaptureTaskState();
        lastEditedCell = null;
        ModeBadge.Text = workspace.Mode switch { RunMode.Demo => L("Text_692F811146"), RunMode.Online => L("Text_C4C1F8C6F4"), _ => L("Text_6C352EE0B0") };
        ResourceBar.Visibility = page is "data" or "import" or "review" ? Visibility.Visible : Visibility.Collapsed;
        SnapshotLabel.Text = L("Text_B5B62EA34A", ("arg0", Data.Draft.Count), ("arg1", (Data.SnapshotAt?.ToLocalTime().ToString("g") ?? L("Text_C4CF69F358"))));
        PageTitle.Text = page switch { "data" => L("Text_9BB882C622"), "import" => L("Text_D52C17622D"), "review" => L("Text_10CCC8AAA2"), "tasks" => L("Text_00B514C36A"), "settings" => L("Text_C2B958136D"), _ => L("Text_3797982942") };
        RefreshShell();
        PageHost.Content = page switch { "data" => DataPage(), "import" => ImportPage(), "review" => ReviewPage(), "tasks" => TasksPage(), "settings" => GetSettingsPage(), _ => WorkspacePage() };
    }
    UIElement WorkspacePage()
    {
        var cards = new StackPanel { Spacing = 12 };
        foreach (var (kind, data) in workspace.Resources)
        {
            var captured = kind;
            cards.Children.Add(new Border { Padding = new Thickness(18), CornerRadius = new CornerRadius(8), Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"], Child = Row(Text(() => L(Schemas.All[kind].Name), 20), Text(() => L("Text_B9B43D4DAA", ("arg0", data.Draft.Count), ("arg1", planner.Plan(kind, data).Count))), Button(() => L("Text_C771248E51"), () => { resource = captured; ResourcePicker.SelectedIndex = (int)resource; Navigation.SelectedItem = Navigation.MenuItems[1]; return Task.CompletedTask; })) });
        }
        return Scroll(Stack(Text(() => L("Text_2C33D5F7DE")),
            Row(Button(() => L("Text_946897D107"), async () =>
            {
                if (!closeState.IsOpen || busy || !closeState.IsOpen) return; var name = new TextBox { Text = L("Text_A818AFCD80"), MaxLength = 80 }.Localize(localization, "Header", () => L("Text_8DCC576817"));
                if (await Confirm(L("Text_CDD0362664"), name) && !string.IsNullOrWhiteSpace(name.Text))
                { await Save(); workspace = new Workspace { Name = name.Text.Trim() }; await Save(); ReloadWorkspaces(); Render(); }
            }), Button(() => L("Text_57D4E0340D"), async () =>
            {
                if (!closeState.IsOpen || busy || !closeState.IsOpen) return; await Save(); workspace = DemoWorkspace(); await Save(); ReloadWorkspaces(); Render();
            })), cards,
            Text(() => L("Text_8B0B7AD880"))));
    }
    UIElement DataPage()
    {
        var state = CurrentDataState; shownState = state;
        var grid = new Grid { RowSpacing = 10 };
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); grid.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        var search = new TextBox { Width = 220 }.Localize(localization, "PlaceholderText", () => L("Text_762B9F2664"));
        var filter = new ComboBox { Width = 140, SelectedIndex = 0 }.Localize(localization, "ItemsSource", () => new[] { L("Text_5C55A67935"), L("Text_682D211B71"), L("Text_549986E82C") });
        var sort = new ComboBox { Width = 190, ItemsSource = Data.Columns.Count > 0 ? Data.Columns : Schemas.All[resource].Columns.ToList(), SelectedIndex = 0 };
        searchBox = search; filterBox = filter; sortBox = sort;
        search.Text = state.Search; filter.SelectedIndex = state.Filter;
        if (state.Sort != null && sort.Items.Contains(state.Sort)) sort.SelectedItem = state.Sort;
        var toolbar = Row(search, filter, sort, Button(() => L("Text_0DE280B4DF"), () => { LoadRows(search.Text, filter.SelectedIndex, sort.SelectedItem?.ToString()); return Task.CompletedTask; }));
        grid.Children.Add(new ScrollViewer { Content = toolbar, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled });
        var actions = new CommandBar { DefaultLabelPosition = CommandBarDefaultLabelPosition.Right, HorizontalContentAlignment = HorizontalAlignment.Left, IsDynamicOverflowEnabled = true };
        actions.PrimaryCommands.Add(Command(() => L("ApiRefresh"), Symbol.Refresh, RefreshApi, !busy));
        actions.PrimaryCommands.Add(Command(() => L("Text_E306752B35"), Symbol.Edit, EditRules));
        actions.PrimaryCommands.Add(Command(() => L("Text_48A195CA7E"), Symbol.View, () => { Navigation.SelectedItem = Navigation.MenuItems[3]; return Task.CompletedTask; }));
        actions.PrimaryCommands.Add(Command(() => L("ApiConflicts", ("count", Data.Conflicts.Count)), Symbol.Important, ResolveApiConflicts, !busy && Data.Conflicts.Count > 0));
        actions.PrimaryCommands.Add(Command(() => L("Text_0006D696D8"), Symbol.Add, async () => { if (!closeState.IsOpen || busy || !closeState.IsOpen) return; Data.Checkpoint(); var r = new DataRow(); foreach (var f in Schemas.All[resource].Columns) r.Fields[f] = f == "_delete" ? "FALSE" : ""; Data.Draft.Add(r); Data.Columns = Data.Columns.Concat(r.Fields.Keys).Distinct().ToList(); await Save(); Render(); }));
        if (resource is ResourceKind.Aircraft or ResourceKind.Routes) actions.PrimaryCommands.Add(Command(() => L("Text_95DD1FDDFE"), Symbol.Edit, () => ChooseFleets(null)));
        actions.PrimaryCommands.Add(Command(() => L("Text_435BF8A234"), Symbol.Delete, DeleteSelected));
        actions.PrimaryCommands.Add(Command(() => L("Text_A6926595CE"), Symbol.Undo, RestoreSelected));
        if (resource == ResourceKind.Routes) actions.SecondaryCommands.Add(Command(() => L("Text_7AA882D2FE"), Symbol.Clock, RetireSelected));
        actions.SecondaryCommands.Add(Command(() => L("Text_926A50B98E"), Symbol.Undo, async () => { if (!closeState.IsOpen || busy || !closeState.IsOpen) return; Data.UndoEdit(); await Save(); Render(); Status(() => L("Text_E5CE954248")); }));
        actions.SecondaryCommands.Add(Command(() => L("Text_03717B6F10"), Symbol.Redo, async () => { if (!closeState.IsOpen || busy || !closeState.IsOpen) return; Data.RedoEdit(); await Save(); Render(); Status(() => L("Text_1F00D27BEE")); }));
        actions.SecondaryCommands.Add(Command(() => L("Text_41B0AF8C5C"), Symbol.Accept, Validate));
        Grid.SetRow(actions, 1); grid.Children.Add(actions);
        table = new ListView { SelectionMode = ListViewSelectionMode.Multiple, HorizontalContentAlignment = HorizontalAlignment.Stretch, IsEnabled = !busy };
        table.ItemTemplate = (DataTemplate)XamlReader.Load("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
              <ItemsControl ItemsSource="{Binding Cells}">
                <ItemsControl.ItemsPanel><ItemsPanelTemplate><StackPanel Orientation="Horizontal"/></ItemsPanelTemplate></ItemsControl.ItemsPanel>
                <ItemsControl.ItemTemplate><DataTemplate>
                  <Grid Width="{Binding Width}" Margin="0,3,6,3">
                    <TextBox Visibility="{Binding EditorVisibility}" IsReadOnly="{Binding IsReadOnly}" Text="{Binding Value, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}" ToolTipService.ToolTip="{Binding Field}" AutomationProperties.Name="{Binding Field}"/>
                    <Button Visibility="{Binding FleetVisibility}" IsEnabled="{Binding IsEnabled}" Command="{Binding ChooseFleet}" HorizontalAlignment="Stretch" HorizontalContentAlignment="Left" ToolTipService.ToolTip="{Binding DisplayValue}" AutomationProperties.Name="{Binding FleetAutomationName}">
                      <TextBlock Text="{Binding DisplayValue}" TextTrimming="CharacterEllipsis"/>
                    </Button>
                    <TextBlock Visibility="{Binding StatusVisibility}" Text="{Binding DisplayValue}" VerticalAlignment="Center" Margin="8,0"/>
                  </Grid>
                </DataTemplate></ItemsControl.ItemTemplate>
              </ItemsControl>
            </DataTemplate>
            """);
        var headers = Row(); headers.Spacing = 0; headers.Margin = new Thickness(44, 0, 0, 0); foreach (var f in DisplayColumns()) headers.Children.Add(new TextBlock { Width = new CellViewModel(f, "", _ => {}).Width, Margin = new Thickness(0, 0, 6, 0), TextTrimming = TextTrimming.CharacterEllipsis }.Localize(localization, "Text", () => f));
        table.Header = headers;
        var horizontal = new ScrollViewer { Content = table, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollMode = ScrollMode.Disabled };
        horizontalTable = horizontal; shownDataPage = grid;
        Grid.SetRow(horizontal, 2); grid.Children.Add(horizontal); LoadRows(state.AppliedSearch, state.AppliedFilter, state.AppliedSort);
        RestoreTableState(table, horizontal, state); return grid;
    }
    List<string> DisplayColumns()
    {
        var leading = new List<string> { "_delete", Schemas.All[resource].Key };
        if (resource is ResourceKind.Aircraft or ResourceKind.Routes) leading.Add(FleetAssignmentService.Field(resource));
        return leading.Concat(Data.Columns.Count == 0 ? Schemas.All[resource].Columns : Data.Columns).Distinct().ToList();
    }
    void LoadRows(string search, int filter, string? sort)
    {
        if (table is null) return;
        if (shownState != null) { shownState.AppliedSearch = search; shownState.AppliedFilter = filter; shownState.AppliedSort = sort; }
        var columns = DisplayColumns(); var changed = filter == 1 ? planner.Plan(resource, Data).Select(i => i.After.LocalId).ToHashSet() : null;
        IEnumerable<DataRow> rows = Data.Draft.Where(r => (search == "" || r.Fields.Values.Any(v => v.Contains(search, StringComparison.OrdinalIgnoreCase))) && (filter != 1 || changed!.Contains(r.LocalId)) && (filter != 2 || Schemas.Deleted(r)));
        if (sort != null) rows = rows.OrderBy(r => r.Get(sort), StringComparer.OrdinalIgnoreCase);
        var existingIds = Data.Snapshot.Select(r => r.LocalId).ToHashSet();
        var modifiedIds = planner.Plan(resource, Data).Select(i => i.After.LocalId).ToHashSet();
        var conflictIds = Data.Conflicts.Select(c => c.RowId).ToHashSet();
        var fleetLabels = fleets.Options(workspace).ToDictionary(o => o.Token, StringComparer.OrdinalIgnoreCase);
        visible = rows.Select(r => new RowViewModel(r, columns.Select(f => new CellViewModel(f, r.Get(f), value =>
        {
            if (!closeState.IsOpen || busy || Schemas.Deleted(r) || f == "_delete") return;
            var cellKey = r.LocalId + ":" + f;
            if (lastEditedCell != cellKey) { Data.Checkpoint(); lastEditedCell = cellKey; }
            r.Fields[f] = value; SaveSoon(); Status(() => L("Text_75141EDE81"));
        })
        {
            CanEdit=()=>closeState.IsOpen&&!busy, IsReadOnly = Schemas.Deleted(r), FleetName = () => L("ChooseFleet"),
            DisplayText = () => f == "_delete" ? (Schemas.Deleted(r) ? L("Text_EE96DC1EBE") : conflictIds.Contains(r.LocalId) ? L("ApiRowConflict") : modifiedIds.Contains(r.LocalId) && existingIds.Contains(r.LocalId) ? L("ApiRowDraft") : existingIds.Contains(r.LocalId) ? L("Text_20D2382F10") : L("Text_4EF7F62706"))
                : f is "Fleet ID" or "Fleet IDs" ? (string.Join("; ", FleetAssignmentService.Tokens(r.Get(f)).Select(t => fleetLabels.TryGetValue(t, out var option) ? option.Describe(localization) : L("Text_C221144914") + t)) is var label && label.Length > 0 ? label : L("Text_F274578907")) : "",
            ChooseFleet = f is "Fleet ID" or "Fleet IDs" ? new CommunityToolkit.Mvvm.Input.AsyncRelayCommand(() => Guard(() => ChooseFleets(r))) : null
        }).ToList())).ToList(); table.ItemsSource = visible;
    }
    List<DataRow> SelectedRows() => table?.SelectedItems.Cast<RowViewModel>().Select(v => v.Row).ToList() ?? [];
    async Task DeleteSelected()
    {
        if (!closeState.IsOpen || busy || !closeState.IsOpen) return; var selected = SelectedRows(); if (selected.Count == 0) { Status(() => L("Text_BAB47FB928")); return; }
        var plan = deletions.Preview(workspace, resource, selected.Select(r => r.LocalId));
        if (plan.Issues.Count > 0) { await Confirm(L("Text_FD6D0919A9"), new ScrollViewer { MaxHeight = 350, Content = Text(() => string.Join("\n", plan.Issues.Take(100).Select(i => L(i.Description)))) }, L("Text_3FD47EDCE4")); return; }
        if (plan.RemoveDrafts.Count + plan.MarkExisting.Count == 0) { Status(() => L("Text_EEAAAF2F48")); return; }
        if (!await Confirm(L("Text_948725D00E"), Stack(Text(() => L("Text_0360D81E1F", ("arg0", plan.RemoveDrafts.Count), ("arg1", plan.MarkExisting.Count))), Text(() => string.Join("\n", selected.Take(8).Select(r => $"{Schemas.Key(resource, r)} {r.Get("Name")}{r.Get("Flight Number")}"))), Text(() => L("Text_0E38068B45"))), L("Text_A3EA3C17B4"))) return;
        deletions.Apply(workspace, plan); await Save(); Render(); Status(() => L("Text_2C040F22AA", ("arg0", plan.RemoveDrafts.Count), ("arg1", plan.MarkExisting.Count)));
    }
    async Task RestoreSelected()
    {
        if (!closeState.IsOpen || busy || !closeState.IsOpen) return; var selected = SelectedRows();
        if (selected.Count == 0) { Status(() => L("Text_785FDE2811")); return; }
        deletions.Restore(Data, selected.Select(r => r.LocalId)); await Save(); Render(); Status(() => L("Text_FABC892562"));
    }
    async Task EditRules()
    {
        if (!closeState.IsOpen || busy || !closeState.IsOpen) return; var selected = SelectedRows(); var rows = (selected.Count > 0 ? selected : visible.Select(v => v.Row).ToList()).Where(r => !Schemas.Deleted(r)).ToList();
        if (rows.Count == 0) { Status(() => L("Text_4BB22EC98B")); return; }
        var field = new ComboBox { ItemsSource = DisplayColumns().Where(f => f is not "_delete" and not "Fleet ID" and not "Fleet IDs").ToList(), SelectedIndex = 0, MinWidth = 330 }.Localize(localization, "Header", () => L("Text_86F623E5CC"));
        var kind = new ComboBox { ItemsSource = new[] { L("Text_A30D395090"), L("Text_5B7341C2D6"), L("Text_795CBFF909"), L("Text_6FE8FC1819"), L("Text_FAC7E021A1"), L("Text_E79453E164"), L("Text_6797F1B017") }, SelectedIndex = 0 }.Localize(localization, "Header", () => L("Text_ED31FBB483")).Localize(localization, "ItemsSource", () => new[] { L("Text_A30D395090"), L("Text_5B7341C2D6"), L("Text_795CBFF909"), L("Text_6FE8FC1819"), L("Text_FAC7E021A1"), L("Text_E79453E164"), L("Text_6797F1B017") });
        var value = new TextBox { }.Localize(localization, "Header", () => L("Text_DD5ABC95B1")); var find = new TextBox { }.Localize(localization, "Header", () => L("Text_8FAB339859"));
        if (!await Confirm(L("Text_E306752B35"), Stack(Text(() => L("Text_3733B88A89", ("arg0", (selected.Count > 0 ? L("Text_A737FD72B0") : L("Text_CD02A81FB5"))), ("arg1", rows.Count))), field, kind, find, value), L("Text_13D61FEA9F"))) return;
        var ruleKind = (RuleKind)kind.SelectedIndex;
        if (ruleKind == RuleKind.Retire && resource != ResourceKind.Routes) throw new ArgumentException(L("Text_CA75B5F2F0"));
        var rule = new EditRule(ruleKind, field.SelectedItem!.ToString()!, value.Text, find.Text);
        var preview = await Task.Run(() => RuleEngine.Preview(rows, rule));
        var targetField = ruleKind == RuleKind.Retire ? "End Date" : rule.Field;
        var samples = preview.Take(8).Select((r, i) => $"{rows[i].Get(Schemas.All[resource].Key)}：{rows[i].Get(targetField)} → {r.Get(targetField)}");
        if (!await Confirm(L("Text_AD86791376"), Stack(Text(() => L("Text_42F23885A7", ("arg0", rows.Count))), Text(() => string.Join("\n", samples))), L("Text_2354C137CE"))) return;
        Data.Checkpoint(); var map = preview.ToDictionary(r => r.LocalId);
        for (int i = 0; i < Data.Draft.Count; i++) if (map.TryGetValue(Data.Draft[i].LocalId, out var r)) Data.Draft[i] = r;
        if (!Data.Columns.Contains(targetField)) Data.Columns.Add(targetField);
        await Save(); Render(); Status(() => L("Text_13292B3343", ("arg0", rows.Count)));
    }
    async Task Validate()
    {
        if (workspace.Mode == RunMode.Online)
        {
            var messages = new List<string>();
            foreach (var item in planner.PlanWorkspace(workspace).Where(c => c.Resource == resource))
                try { Api().PreviewPlan(item); }
                catch (Exception e) { messages.Add(L(MessageErrors.Describe(e))); }
            if (Data.Conflicts.Count > 0) messages.Add(L("ApiConflict"));
            await Confirm(L("Text_38833A8579"), Scroll(Text(() => messages.Count == 0 ? L("Text_AA9F647A82") : string.Join("\n", messages))), L("Text_3FD47EDCE4")); return;
        }
        await Work(async () =>
        {
            var issues = await Task.Run(() => Schemas.Validate(workspace, resource));
            Status(() => issues.Count == 0 ? L("Text_AA9F647A82") : L("Text_8EEF5FE8E6", ("arg0", issues.Count)));
            if (issues.Count > 0) await Confirm(L("Text_38833A8579"), Scroll(Text(() => string.Join("\n", issues.Take(100).Select(i => L("Text_7560F012B2", ("arg0", Data.Draft.FindIndex(r => r.LocalId == i.RowId) + 1), ("arg1", i.Field), ("arg2", L(i.Description))))))), L("Text_3FD47EDCE4"));
        });
    }
    UIElement ImportPage() => Stack(Text(() => L("Text_D030C51D78"), 16),
        Text(() => L("Text_3618171B0A")),
        Button(() => L("Text_5A2BBDEA04"), ImportCsv), Button(() => L("ApiRefresh"), RefreshApi));
    async Task ImportCsv()
    {
        if (!closeState.IsOpen || busy || !closeState.IsOpen) return;
        var picker = new FileOpenPicker(); picker.FileTypeFilter.Add(".csv"); WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSingleFileAsync(); if (file is null) return;
        await Work(async () =>
        {
            var parsed = await Task.Run(async () => csv.Parse(await File.ReadAllTextAsync(file.Path, Encoding.UTF8)));
            var mapping = new StackPanel { Spacing = 8 }; var boxes = new Dictionary<string, TextBox>();
            foreach (var h in parsed.Headers) { var box = new TextBox { Header = h, Text = h }; boxes[h] = box; mapping.Children.Add(box); }
            var baseline = new CheckBox { IsChecked = workspace.Mode != RunMode.Online && Data.SnapshotAt is null, IsEnabled = workspace.Mode != RunMode.Online }.Localize(localization, "Content", () => L("Text_B91DE02936"));
            var panel = Stack(Text(() => L("Text_42F0F930C9", ("arg0", parsed.Rows.Count))), baseline,
                new ScrollViewer { Content = mapping, MaxHeight = 280 }, Text(() => L("Text_793A76BFEA") + string.Join("\n", parsed.Rows.Take(3).Select(r => string.Join(" | ", r).Truncate(240)))));
            if (!await Confirm(L("Text_E0FDE3C5CB"), panel, L("Text_C9B2630A18"))) return;
            if (baseline.IsChecked == true && Data.Draft.Count > 0 && !await Confirm(L("Text_885B15B2F5"), Text(() => L("Text_5B5C523423")), L("Text_131B13AA26"))) return;
            var rows = csv.Map(parsed, boxes.ToDictionary(p => p.Key, p => p.Value.Text.Trim()));
            var duplicates = rows.Where(r => Schemas.Key(resource, r) != "").GroupBy(r => Schemas.Key(resource, r)).Any(g => g.Count() > 1);
            if (duplicates) throw new FormatException(L("Text_3BCC8B750F"));
            CsvAdapter.Import(resource, Data, rows, baseline.IsChecked == true); await Save();
            var issues = await Task.Run(() => Schemas.Validate(workspace, resource));
            Render(); Status(() => L("Text_D84A9B1171", ("arg0", rows.Count), ("arg1", issues.Count)));
        });
    }
    UIElement ReviewPage()
    {
        var changes = planner.Plan(resource, Data);
        var list = new ListView { SelectionMode = ListViewSelectionMode.None }.Localize(localization, "ItemsSource", () => changes.Select(i => $"{EnumText(i.Kind)} · {Schemas.Key(resource, i.After)}\n" + (i.Kind == ChangeKind.Delete ? L("Text_5ABD08A949") : string.Join("；", i.Fields.Select(f => $"{f.Key}: {i.Before?.Get(f.Key) ?? "∅"} → {f.Value.Value}")))).ToList());
        var grid = new Grid { RowSpacing = 12 }; grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); grid.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(Stack(Text(() => L("Text_4478B3A156", ("arg0", changes.Count), ("arg1", changes.Count(i => i.Kind == ChangeKind.Create)), ("arg2", changes.Count(i => i.Kind == ChangeKind.Update)), ("arg3", changes.Count(i => i.Kind == ChangeKind.Delete)))),
            Text(() => L("ApiTarget", ("va", workspace.Name), ("id", workspace.AirlineId ?? "?"))),
            Actions(Button(() => L("ApiSubmit"), StartApi, workspace.Mode == RunMode.Online && !busy), Button(() => L("ApiConflicts", ("count", Data.Conflicts.Count)), ResolveApiConflicts, !busy && Data.Conflicts.Count > 0), Button(() => L("Text_ABED06FAA0"), ExportCsv), Button(() => L("Text_498F64E2E2"), StartDemo, workspace.Mode == RunMode.Demo))));
        Grid.SetRow(list, 1); grid.Children.Add(list); return grid;
    }
    async Task<bool> ValidateForSubmit()
    {
        var issues = await Task.Run(() => Schemas.Validate(workspace, resource));
        if (issues.Count == 0) return true;
        await Confirm(L("Text_75843F167F"), Scroll(Text(() => string.Join("\n", issues.Take(60).Select(i => L("Text_7560F012B2", ("arg0", Data.Draft.FindIndex(r => r.LocalId == i.RowId) + 1), ("arg1", i.Field), ("arg2", L(i.Description))))))), L("Text_3FD47EDCE4")); return false;
    }
    async Task ExportCsv()
    {
        if (!closeState.IsOpen || busy || !closeState.IsOpen) return; if (!await ValidateForSubmit()) return;
        var changes = planner.Plan(resource, Data); if (changes.Count == 0) { Status(() => L("Text_90484F47B3")); return; }
        if (changes.Any(i => i.Kind == ChangeKind.Delete) && !await Confirm(L("Text_5D6042B30E"), Text(() => L("Text_B83678AC3A", ("arg0", changes.Count(i => i.Kind == ChangeKind.Delete)))), L("Text_6B20B383BC"))) return;
        var picker = new FolderPicker(); picker.FileTypeFilter.Add("*"); WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var folder = await picker.PickSingleFolderAsync(); if (folder is null) return;
        await Work(async () =>
        {
            var parts = await Task.Run(() => csv.Export(resource, changes));
            var prefix = $"{resource}-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}";
            for (int i = 0; i < parts.Count; i++) await File.WriteAllBytesAsync(Path.Combine(folder.Path, $"{prefix}-{i + 1}.csv"), parts[i]);
            workspace.Jobs.Add(new BatchJob { Mode = RunMode.Offline, Items = changes, Description = Messages.Define("Text_D964132351", ("arg0", parts.Count), ("arg1", folder.Path)), StatusCode = JobStatus.AwaitingImport, Status = Messages.Define("Text_D964132351", ("arg0", parts.Count), ("arg1", folder.Path)) });
            await Save(); Status(() => L("Text_B680DD9FD1", ("arg0", parts.Count)));
        });
    }
    async Task StartDemo()
    {
        if (!closeState.IsOpen || busy || workspace.Mode != RunMode.Demo) return;
        var issues = await Task.Run(() => workspace.Resources.Where(p => p.Value.Draft.Count > 0).SelectMany(p => Schemas.Validate(workspace, p.Key)).ToList());
        if (issues.Count > 0) { await Confirm(L("Text_78E06D4B43"), Scroll(Text(() => string.Join("\n", issues.Take(60).Select(i => $"{i.Field}：{ItemText(i)}")))), L("Text_3FD47EDCE4")); return; }
        var changes = planner.PlanWorkspace(workspace); if (changes.Count == 0) return;
        if (!await Confirm(L("Text_008D68D9F9"), new ListView { MaxHeight = 440, Header = Text(() => L("Text_5581164B03", ("arg0", changes.Count))), ItemsSource = changes.Select(i => $"{L(Schemas.All[i.Resource].Name)} · {EnumText(i.Kind)} · {Schemas.Key(i.Resource, i.After)}\n" + string.Join("；", i.Fields.Select(f => $"{f.Key}: {i.Before?.Get(f.Key) ?? "∅"} → {f.Value.Value}"))).ToList() }, L("Text_48B3938B8C"))) return;
        var job = new BatchJob { Mode = RunMode.Demo, Items = changes }; workspace.Jobs.Add(job); await Save(); await ExecuteDemo(job);
    }
    async Task ExecuteDemo(BatchJob job)
    {
        if (!closeState.IsOpen || busy || workspace.Mode != RunMode.Demo || job.Mode != RunMode.Demo) return;
        await Work(async () =>
        {
            running = new CancellationTokenSource(); var demo = new DemoService(workspace);
            foreach (var history in workspace.Jobs.Where(j => j.Mode == RunMode.Demo)) demo.RestoreSuccessful(history);
            Navigation.SelectedItem = Navigation.MenuItems[4]; Render();
            await new BatchExecutor(demo, demo).ExecuteAsync(job, async () => { await Save(); }, running.Token, item=>NotifyTask(new(workspace.Id,job.Id,item?.Id,job.StatusCode!=JobStatus.Running)));
            // Rebase only verified successful rows, keeping failures and unrelated drafts intact.
            foreach (var item in job.Items.Where(i => i.State == ItemState.Succeeded && !i.Rebased))
            {
                var data = workspace.Resources[item.Resource];
                data.Snapshot.RemoveAll(r => r.LocalId == item.After.LocalId);
                if (item.Kind == ChangeKind.Delete) data.Draft.RemoveAll(r => r.LocalId == item.After.LocalId && Schemas.Deleted(r));
                else
                {
                    var updated = item.After.Copy(); if (item.RemoteId != null) updated.Fields[Schemas.All[item.Resource].Key] = item.RemoteId;
                    data.Snapshot.Add(updated.Copy()); var index = data.Draft.FindIndex(r => r.LocalId == updated.LocalId);
                    if (index >= 0)
                    {
                        // Preserve edits made after a recovered job was submitted; only rebase identity/references.
                        var current = data.Draft[index];
                        current.Fields[Schemas.All[item.Resource].Key] = updated.Get(Schemas.All[item.Resource].Key);
                        foreach (var field in Schemas.ReferenceFields(item.Resource))
                            if (current.Get(field).Contains("local:", StringComparison.OrdinalIgnoreCase)) current.Fields[field] = updated.Get(field);
                    }
                }
                item.Rebased = true; data.SnapshotAt = DateTimeOffset.UtcNow; data.Undo.Clear(); data.Redo.Clear();
            }
            await Save(); foreach(var item in job.Items)NotifyTask(new(workspace.Id,job.Id,item.Id));FlushTasks(); running.Dispose(); running = null; Render(); Status(() => L("Text_B64A075682") + JobText(job));
        });
    }
    UIElement SettingsPage()
    {
        var url = new TextBox { Text = OperationsAdapter.BaseUri.AbsoluteUri, IsReadOnly = true, MaxWidth = 650, HorizontalAlignment = HorizontalAlignment.Stretch }.Localize(localization, "Header", () => L("Text_0251B89662"));
        var clientId = new TextBox { Header = "Client ID", Text = workspace.ClientId ?? "", MaxWidth = 650, HorizontalAlignment = HorizontalAlignment.Stretch };
        var secret = new PasswordBox { MaxWidth = 650, HorizontalAlignment = HorizontalAlignment.Stretch }.Localize(localization, "Header", () => L("Text_86032C09C5"));
        var local = new CheckBox { IsChecked = workspace.LocalRouteTimes }.Localize(localization, "Content", () => L("Text_92BD49D337"));
        return Scroll(Stack(LanguagePicker(), Text(() => L("ApiSettings"), 20), Text(() => L("ApiSettingsHelp")), url, clientId, secret, local,
            Button(() => L("Text_760143E568"), async () =>
            {
                if (!closeState.IsOpen || busy || !closeState.IsOpen) return;
                await saveGate.WaitAsync();
                try { store.SaveConnectionSettings(workspace,clientId.Text,secret.Password,url.Text,local.IsChecked==true); }
                finally { saveGate.Release(); }
                clientId.Text=workspace.ClientId ?? ""; secret.Password="";
                if(apiSessions.Remove(workspace.Id,out var previous)) previous.Client.Dispose();
                Status(()=>L("Text_DEADA9F3CC"));
            }), Button(() => L("ApiUpdateCredentials"), async () =>
            {
                if(!closeState.IsOpen || busy) return;
                await saveGate.WaitAsync();
                try { store.UpdateOperationsCredentials(workspace,clientId.Text,secret.Password); }
                finally { saveGate.Release(); }
                clientId.Text=workspace.ClientId ?? ""; secret.Password="";
                if(apiSessions.Remove(workspace.Id,out var previous)) previous.Client.Dispose();
                Status(()=>L("ApiCredentialsUpdated"));
            }, !busy), Text(()=>L("ApiCredentialsHelp")), Button(() => L("ApiConnect"), ConnectApi), Button(() => L("ApiRefresh"), RefreshApi),
            Text(() => L("ApiCapability")), Text(() => L("ApiVerified", ("operations", string.Join(", ", workspace.VerifiedOperations)))),
            Text(() => L("ApiTarget", ("va", workspace.Name), ("id", workspace.AirlineId ?? "?")))));
    }
    Workspace DemoWorkspace()
    {
        var w = new Workspace { Name = L("DemoWorkspace", ("time", DateTime.Now.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture))), Mode = RunMode.Demo };
        void Add(ResourceKind k, params (string, string)[] fields)
        {
            var r = new DataRow { Fields = fields.ToDictionary(p => p.Item1, p => p.Item2) }; r.Fields["_delete"] = "FALSE";
            w.Resources[k].Draft.Add(r); w.Resources[k].Snapshot.Add(r.Copy()); w.Resources[k].Columns = w.Resources[k].Columns.Concat(r.Fields.Keys).Distinct().ToList(); w.Resources[k].SnapshotAt = DateTimeOffset.UtcNow;
        }
        Add(ResourceKind.Airports, ("ICAO/IATA", "ZBAA"), ("Name", "北京首都")); Add(ResourceKind.Airports, ("ICAO/IATA", "ZSPD"), ("Name", "上海浦东"));
        Add(ResourceKind.Fleets, ("ID", "1"), ("Name", "Boeing 737-800"), ("Type Code", "B738"), ("Type (pax/cargo/...)", "pax"), ("Max Passengers", "189"));
        Add(ResourceKind.Fleets, ("ID", "2"), ("Name", "Airbus A320"), ("Type Code", "A320"), ("Type (pax/cargo/...)", "pax"), ("Max Passengers", "180"));
        Add(ResourceKind.Aircraft, ("ID", "10"), ("Name", "Boeing 737-800"), ("Registration", "B-DEMO"), ("Fleet ID", "1"));
        Add(ResourceKind.Routings, ("ID", "20"), ("Departure Airport (ICAO/IATA)", "ZBAA"), ("Arrival Airport (ICAO/IATA)", "ZSPD"), ("Route String", "DCT"));
        for (int i = 0; i < 12; i++) Add(ResourceKind.Routes, ("ID", (100 + i).ToString()), ("Departure Airport (ICAO/IATA)", i % 2 == 0 ? "ZBAA" : "ZSPD"), ("Arrival Airport (ICAO/IATA)", i % 2 == 0 ? "ZSPD" : "ZBAA"), ("Type", "scheduled"), ("Callsign", "DEM" + (100 + i)), ("Flight Number", "DM" + (100 + i)), ("Fleet IDs", "1"), ("Departure Time (HH:MM)", $"{6 + i:00}:30"), ("Arrival Time (HH:MM)", $"{8 + i:00}:45"), ("Tags", "演示"));
        return w;
    }
}
static class StringExtensions
{
    public static string Truncate(this string value, int max) => value.Length <= max ? value : value[..max] + "…";
}
