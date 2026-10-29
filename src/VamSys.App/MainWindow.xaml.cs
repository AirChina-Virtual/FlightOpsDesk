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
    readonly WorkspaceStoreAdapter storage = WorkspaceStoreAdapter.OpenDefault();
    ResourceKind resource = ResourceKind.Routes;
    readonly InteractionGate gate = new();
    readonly ShellViewModel shell;
    readonly DispatcherAutosaveTimer autosave = new(WorkspaceSession.AutosaveInterval);
    readonly WorkspaceSession session;
    readonly OnlineGateway online;
    readonly MaintenanceService maintenance;
    readonly ConnectionService connection;
    readonly ConflictService conflicts;
    readonly ImportExportService importExport;
    readonly BatchEditService batch;
    readonly ReviewService reviews;
    readonly SubmissionService submission;
    readonly TaskRecoveryService recovery;
    readonly DemoRunner demo;
    readonly DiagnosticsService diagnostics;
    Workspace workspace => session.Workspace;
    IUserSettings settings => storage;
    string page => shell.Page.Tag();
    bool ready;
    readonly CellEditCommitter cellEdits = new();
    ListView? table; List<RowViewModel> visible = []; AppBarButton? fleetCommand, retireCommand;
    CancellationTokenSource? running;
    ResourceData Data => workspace.Resources[resource];

    public MainWindow()
    {
        InitializeComponent();
        shell = new(gate);
        session = new(storage, gate, autosave);
        session.DataInvalidated += InvalidateDataViews;
        online = new(storage);
        maintenance = new(session, online);
        connection = new(session, online);
        conflicts = new(session);
        importExport = new(session, online);
        batch = new(session);
        reviews = new(session, online);
        submission = new(session);
        recovery = new(session, online);
        demo = new(session, DemoEngine.Create);
        diagnostics = new(session, ApiDiagnosticExport.Serialize);
        InitializeLanguage();
        tableDensity = settings.LoadTableDensity();
        tableTemplates = new();
        cellTemplates = new(tableTemplates);
        ChooseDataGrid(ref useNewDataGrid);
        BodyGrid.SizeChanged += (_, _) => UpdateDataHeaderLayout();
        ConfigureVerificationTheme();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "FlightOpsDesk.ico"));
        var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary).WorkArea;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(Math.Min(1400, area.Width), Math.Min(900, area.Height)));
        RootGrid.ActualThemeChanged += (_, _) => UpdateCaptionTheme();
        RootGrid.Loaded += (_, _) => UpdateCaptionTheme();
        RootGrid.SizeChanged += (_, _) => { WorkspacePicker.Width = RootGrid.ActualWidth < 800 ? 140 : 230; AppTitleBar.Subtitle = RootGrid.ActualWidth < 1100 ? "" : L("Text_9750099F9F"); };
        session.Open(L("Text_707DBE0213"));
        ResourcePicker.ItemsSource = Enum.GetValues<ResourceKind>().Select(k => L(Schemas.All[k].Name)).ToList();
        ResourcePicker.SelectedIndex = (int)resource;
        ReloadWorkspaces();
        autosave.Tick += async (_, _) => await Guard(async () => { await session.AutosaveElapsedAsync(); if (!gate.WorkInProgress) Status(() => L("Text_11584EAAA5")); });
        AppWindow.Closing += async (_, e) =>
        {
            if (gate.HasClosed)
                return;
            e.Cancel = true;
            if (!gate.IsOpen)
                return;
            if (gate.CloseBlockedByActivity)
            { Status(() => L("CloseBlocked")); return; }
            try
            {
                var closed = await gate.Closing.RequestAsync(false, session.SaveForCloseAsync, FreezeClosing);
                if (closed)
                    Close();
            }
            catch { Status(() => L("Text_7AF24B0E8E")); }
        };
        ready = true;
        SelectPane(ShellPage.Workspace);
        Render();
        InitializeVerification();
    }
    partial void ConfigureVerificationTheme();
    partial void InitializeVerification();
    // Only the QA build implements this (VAMSYS_QA_DATAGRID=new); the product build keeps the table built in code.
    partial void ChooseDataGrid(ref bool useNew);
    partial void ObserveDialog(ContentDialog dialog, bool showing);
    void Status(Func<string> message) { statusText = message; StatusLabel.Text = message(); }
    void ToggleNavigation(TitleBar sender, object args) => Navigation.IsPaneOpen = !Navigation.IsPaneOpen;
    // Selects the pane item; its SelectionChanged asks the shell, which applies the gate.
    void SelectPane(ShellPage target) => Navigation.SelectedItem = Navigation.MenuItems[target.PaneIndex()];
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
        bool wasReady = ready;
        ready = false;
        WorkspacePicker.ItemsSource = session.ListWorkspaces().Select(x => new WorkspaceChoice(x.Id, x.Name)).ToList();
        WorkspacePicker.SelectedItem = ((List<WorkspaceChoice>)WorkspacePicker.ItemsSource).First(x => x.Id == workspace.Id);
        ready = wasReady;
    }
    Task Save(bool dataChanged = true, JournalScope scope = JournalScope.All) => session.SaveAsync(dataChanged, scope);
    Task SaveEdits() => session.SaveEditsAsync();
    void SaveSoon() => session.ScheduleAutosave();
    async Task Guard(Func<Task> operation)
    {
        try
        { await operation(); }
        catch (Exception e) { var description = MessageErrors.Describe(e); Status(() => L("Text_AEB8A69DBE") + L(description)); }
    }
    async Task Work(Func<Task> operation)
    {
        var work = gate.TryBeginWork();
        if (work is null)
            return;
        var pendingSave = session.TakeAutosavePending();
        BusyRing.IsActive = true;
        WorkspacePicker.IsEnabled = false;
        ResourcePicker.IsEnabled = false;
        try
        { if (pendingSave) await SaveEdits(); await operation(); }
        finally { work.Dispose(); BusyRing.IsActive = false; WorkspacePicker.IsEnabled = true; ResourcePicker.IsEnabled = true; Render(); }
    }
    Button Button(Func<string> label, Func<Task> action, bool enabled = true)
    {
        var b = new Button { IsEnabled = enabled }.Localize(localization, "Content", () => label());
        b.Click += async (_, _) => { if (gate.CanInvokeCommand) await UserAction(action); };
        return b;
    }
    AppBarButton Command(Func<string> label, Symbol icon, Func<Task> action, bool enabled = true)
    {
        var button = new AppBarButton { Icon = new SymbolIcon(icon), IsEnabled = enabled }.Localize(localization, "Label", () => label());
        button.Click += async (_, _) => { if (gate.CanInvokeCommand) await UserAction(action); };
        return button;
    }
    static StackPanel Stack(params UIElement[] children) { var s = new StackPanel { Spacing = UiTokens.SpacingLarge }; foreach (var c in children) s.Children.Add(c); return s; }
    static StackPanel Row(params UIElement[] children) { var s = Stack(children); s.Orientation = Orientation.Horizontal; return s; }
    TextBlock Text(Func<string> text, double? size = null) => new TextBlock { FontSize = size ?? UiTokens.BodyFontSize, TextWrapping = TextWrapping.Wrap }.Localize(localization, "Text", () => text());
    static ScrollViewer Scroll(UIElement content) => new() { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    static ScrollViewer Actions(params UIElement[] controls) => new() { Content = Row(controls), HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
    async Task<bool> Confirm(string title, UIElement content, string? primary = null)
    {
        var d = new ContentDialog { XamlRoot = Content.XamlRoot, Title = title, Content = content, PrimaryButtonText = primary ?? L("Confirm"), CloseButtonText = L("Text_2CD0F3BE87"), DefaultButton = ContentDialogButton.Close };
        using var open = gate.BeginDialog();
        ObserveDialog(d, true);
        try
        { return await d.ShowAsync() == ContentDialogResult.Primary; }
        finally { ObserveDialog(d, false); }
    }
    // A secondary action is shown only when its label is given.
    async Task<ContentDialogResult> Choose(string title, UIElement content, string primary, string? secondary)
    {
        var d = new ContentDialog { XamlRoot = Content.XamlRoot, Title = title, Content = content, PrimaryButtonText = primary, SecondaryButtonText = secondary ?? "", CloseButtonText = L("Text_2CD0F3BE87"), DefaultButton = ContentDialogButton.Close };
        using var open = gate.BeginDialog();
        ObserveDialog(d, true);
        try
        { return await d.ShowAsync(); }
        finally { ObserveDialog(d, false); }
    }
    void Navigate(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (!ready || !gate.CanNavigate)
            return;
        CancelDataQuery();
        shell.TryNavigate(ShellPages.FromTag((args.SelectedItem as NavigationViewItem)?.Tag?.ToString()));
        Render();
    }
    async void WorkspaceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || !gate.CanSwitchContext || WorkspacePicker.SelectedItem is not WorkspaceChoice choice || choice.Id == workspace.Id)
            return;
        await SwitchWorkspace(choice.Id);
    }
    async Task SwitchWorkspace(Guid id)
    {
        CancelDataQuery();
        await UserAction(async () => { await session.SwitchWorkspaceAsync(id); online.Release(id); Render(); });
    }
    void ResourceChanged(object sender, SelectionChangedEventArgs e)
    { if (!ready || !gate.CanSwitchContext) return; CancelDataQuery(); resource = (ResourceKind)ResourcePicker.SelectedIndex; Render(); }
    void Render()
    {
        if (!ready)
            return;
        CapturePageState();
        CaptureTaskState();
        ReleaseWorkspaceViews();
        cellEdits.ResetContinuation();
        ResourceBar.Visibility = shell.ShowsResourceBar ? Visibility.Visible : Visibility.Collapsed;
        RefreshShell();
        PageHost.Content = shell.Page switch
        {
            ShellPage.Data => DataPage(),
            ShellPage.Import => BuildImportPage(),
            ShellPage.Review => BuildReviewPage(),
            ShellPage.Tasks => BuildTasksPage(),
            ShellPage.Settings => GetSettingsPage(),
            _ => BuildWorkspacePage()
        };
        UpdateDataHeaderLayout();
    }
    // Rebuilt on every render; the shown one is refreshed when the language changes.
    WorkspacePage? shownWorkspacePage;
    UIElement BuildWorkspacePage() => shownWorkspacePage = new WorkspacePage(
        new WorkspaceViewModel(workspace, localization, CountChanges),
        new WorkspacePageActions(
            Invoke: async action => { if (gate.CanInvokeCommand) await UserAction(action); },
            NewWorkspace: NewWorkspace,
            CreateDemo: CreateDemoWorkspace,
            OpenResource: kind => { resource = kind; ResourcePicker.SelectedIndex = (int)resource; SelectPane(ShellPage.Data); }));
    async Task NewWorkspace()
    {
        if (!gate.CanAct)
            return;
        var name = new TextBox { Text = L("Text_A818AFCD80"), MaxLength = 80 }.Localize(localization, "Header", () => L("Text_8DCC576817"));
        if (await Confirm(L("Text_CDD0362664"), name) && !string.IsNullOrWhiteSpace(name.Text))
        { await session.ReplaceWithNewAsync(new Workspace { Name = name.Text.Trim() }); ReloadWorkspaces(); Render(); }
    }
    async Task CreateDemoWorkspace()
    {
        if (!gate.CanAct)
            return;
        await session.ReplaceWithNewAsync(DemoWorkspace());
        ReloadWorkspaces();
        Render();
    }
    UIElement DataPage() => GetDataPage();
    UIElement BuildDataPage()
    {
        var state = CurrentDataState;
        shownState = state;
        var grid = new Grid { RowSpacing = UiTokens.SpacingSmall };
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var search = new TextBox { Width = UiTokens.SearchBoxWidth }.Localize(localization, "PlaceholderText", () => L("Text_762B9F2664"));
        var filter = new ComboBox { Width = UiTokens.FilterPickerWidth, SelectedIndex = 0 }.Localize(localization, "ItemsSource", () => new[] { L("Text_5C55A67935"), L("Text_682D211B71"), L("Text_549986E82C") });
        var sort = new ComboBox { Width = UiTokens.SortPickerWidth, ItemsSource = Data.Columns.Count > 0 ? Data.Columns : Schemas.All[resource].Columns.ToList(), SelectedIndex = -1 }
            .Localize(localization, "PlaceholderText", () => L("TableSort"));
        searchBox = search;
        filterBox = filter;
        sortBox = sort;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(search, "DataSearch");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(filter, "DataFilter");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(sort, "DataSort");
        search.Text = state.Search;
        filter.SelectedIndex = state.Filter;
        if (state.Sort != null && sort.Items.Contains(state.Sort))
            sort.SelectedItem = state.Sort;
        var apply = Button(() => L("Text_0DE280B4DF"), () => LoadRowsAsync(search.Text, filter.SelectedIndex, sort.SelectedItem?.ToString()));
        queryNotice = Text(() => "");
        var toolbar = Row(search, filter, sort, queryNotice, apply, DensityPicker());
        toolbar.Spacing = UiTokens.SpacingMedium;
        search.TextChanged += (_, _) => RefreshQueryNotice();
        filter.SelectionChanged += (_, _) => RefreshQueryNotice();
        sort.SelectionChanged += (_, _) => RefreshQueryNotice();
        search.KeyDown += async (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter && CanEditData()) { e.Handled = true; await UserAction(() => LoadRowsAsync(search.Text, filter.SelectedIndex, sort.SelectedItem?.ToString())); } };
        grid.Children.Add(new ScrollViewer { Content = toolbar, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled });
        var actions = new CommandBar { DefaultLabelPosition = CommandBarDefaultLabelPosition.Right, HorizontalContentAlignment = HorizontalAlignment.Left, IsDynamicOverflowEnabled = true };
        dataActions = actions;
        actions.PrimaryCommands.Add(Command(() => L("ApiRefresh"), Symbol.Refresh, RefreshApi, gate.CanRequestOnline));
        actions.PrimaryCommands.Add(BatchCommand(() => L("Text_E306752B35"), Symbol.Edit, EditRules, "active"));
        actions.PrimaryCommands.Add(Command(() => L("Text_48A195CA7E"), Symbol.View, () => { SelectPane(ShellPage.Review); return Task.CompletedTask; }));
        actions.PrimaryCommands.Add(Command(() => L("ApiConflicts", ("count", Data.Conflicts.Count)), Symbol.Important, ResolveApiConflicts, gate.CanRequestOnline && Data.Conflicts.Count > 0));
        actions.PrimaryCommands.Add(Command(() => L("Text_0006D696D8"), Symbol.Add, async () => { if (!gate.CanAct) return; await batch.AddRowAsync(resource); Render(); }));
        // Kept for every resource and shown only where it applies, since the page outlives a resource switch.
        fleetCommand = BatchCommand(() => L("Text_95DD1FDDFE"), Symbol.Edit, () => resource is ResourceKind.Aircraft or ResourceKind.Routes ? ChooseFleets(null) : Task.CompletedTask, "active");
        fleetCommand.Visibility = resource is ResourceKind.Aircraft or ResourceKind.Routes ? Visibility.Visible : Visibility.Collapsed;
        actions.PrimaryCommands.Add(fleetCommand);
        actions.PrimaryCommands.Add(BatchCommand(() => L("Text_435BF8A234"), Symbol.Delete, DeleteSelected, "delete"));
        actions.SecondaryCommands.Add(BatchCommand(() => L("Text_A6926595CE"), Symbol.Undo, RestoreSelected, "deleted"));
        retireCommand = BatchCommand(() => L("Text_7AA882D2FE"), Symbol.Clock, () => resource == ResourceKind.Routes ? RetireSelected() : Task.CompletedTask, "active");
        retireCommand.Visibility = resource == ResourceKind.Routes ? Visibility.Visible : Visibility.Collapsed;
        actions.SecondaryCommands.Add(retireCommand);
        actions.SecondaryCommands.Add(Command(() => L("Text_926A50B98E"), Symbol.Undo, () => MoveHistory(undo: true)));
        actions.SecondaryCommands.Add(Command(() => L("Text_03717B6F10"), Symbol.Redo, () => MoveHistory(undo: false)));
        actions.SecondaryCommands.Add(Command(() => L("Text_41B0AF8C5C"), Symbol.Accept, Validate));
        Grid.SetRow(actions, 1);
        grid.Children.Add(actions);
        DataGrid? control = null;
        if (useNewDataGrid)
        {
            control = new DataGrid(dataFooter = new(localization), DataGridControlActions());
            table = control.Table;
        }
        else
            table = new ListView { SelectionMode = ListViewSelectionMode.Multiple, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(table, "DataTable");
        // A locked table takes no pointer input; keyboard focus cannot enter it either.
        var lockTarget = table;
        table.GettingFocus += (_, e) => { if (!lockTarget.IsHitTestVisible) e.TryCancel(); };
        table.SelectionChanged += DataSelectionChanged;
        table.ItemTemplate = tableTemplates!.Template("RowTemplate");
        table.ItemContainerTransitions = new Microsoft.UI.Xaml.Media.Animation.TransitionCollection();
        StyleDataTable(table);
        EnsureDataHeaders(DisplayColumns());
        shownDataPage = grid;
        if (control != null)
        {
            horizontalTable = control.Horizontal;
            Grid.SetRow(control, 2);
            Grid.SetRowSpan(control, 2);
            grid.Children.Add(control);
        }
        else
        {
            var horizontal = new ScrollViewer { Content = table, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollMode = ScrollMode.Disabled };
            horizontalTable = horizontal;
            Grid.SetRow(horizontal, 2);
            grid.Children.Add(horizontal);
            var pager = DataPager();
            Grid.SetRow(pager, 3);
            grid.Children.Add(pager);
        }
        table.Loaded += (_, _) => { if (shownState != null) RestoreDataOffsets(shownState); };
        return grid;
    }
    // A step that only replaced rows saves those rows and the moved history step; adding, removing
    // or reordering rows still takes a full save. Nothing to move changes nothing and saves nothing.
    // Replaced rows keep the applied query order, as cell edits do; only those rows are refreshed.
    async Task MoveHistory(bool undo)
    {
        if (!gate.CanAct)
            return;
        var kind = resource;
        var source = Data;
        var stack = undo ? source.Undo : source.Redo;
        var current = DataViewCurrent();
        var changedBefore = current && stack.Count > 0 ? CountChanged(source, stack[^1].Rows.Select(r => r.LocalId)) : 0;
        var move = batch.MoveHistory(kind, undo);
        if (move.Moved)
        {
            if (move.ReplacedRows is { } rows)
            {
                if (current)
                    RefreshReplacedRows(source, rows, changedBefore);
                else
                    InvalidateDataViews();
            }
            await batch.SaveMoveAsync(kind, move);
        }
        Render();
        Status(() => L(undo ? "Text_E5CE954248" : "Text_1F00D27BEE"));
    }
    List<string> DisplayColumns()
    {
        var leading = new List<string> { "_delete", Schemas.All[resource].Key };
        if (resource is ResourceKind.Aircraft or ResourceKind.Routes)
            leading.Add(FleetAssignmentService.Field(resource));
        return leading.Concat(Data.Columns.Count == 0 ? Schemas.All[resource].Columns : Data.Columns).Distinct().ToList();
    }
    // Rebuilt on every render; the shown one is refreshed when the language changes.
    ImportPage? shownImportPage;
    UIElement BuildImportPage() => shownImportPage = new ImportPage(new ImportViewModel(localization),
        new ImportPageActions(Invoke: async action => { if (gate.CanInvokeCommand) await UserAction(action); }, ImportCsv: ImportCsv, Refresh: RefreshApi));
    Task StartDemo() => SubmitWorkspace(RunMode.Demo);
    async Task ExecuteDemo(BatchJob job)
    {
        submission.RequireExecution(job, RunMode.Demo, gate.CanRunTask);
        await Work(async () =>
        {
            running = new CancellationTokenSource();
            await demo.RunAsync(job, running.Token, () => { SelectPane(ShellPage.Tasks); Render(); },
                item => NotifyTask(new(workspace.Id, job.Id, item?.Id, job.StatusCode != JobStatus.Running)));
            foreach (var item in job.Items)
                NotifyTask(new(workspace.Id, job.Id, item.Id));
            FlushTasks();
            running.Dispose();
            running = null;
            Render();
            Status(() => L("Text_B64A075682") + JobText(job));
        });
    }
    Workspace DemoWorkspace() => DemoWorkspaceFactory.Create(L("DemoWorkspace", ("time", DateTime.Now.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture))));
}
static class StringExtensions
{
    public static string Truncate(this string value, int max) => value.Length <= max ? value : value[..max] + "…";
}
