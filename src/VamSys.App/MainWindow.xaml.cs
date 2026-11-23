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
    ListView? table => tableView?.Table;
    List<RowViewModel> visible = [];
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
        BodyGrid.SizeChanged += (_, _) => UpdateDataHeaderLayout();
        CreateVerification(ref verification);
        verification?.ConfigureTheme();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "FlightOpsDesk.ico"));
        var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary).WorkArea;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(Math.Min(1400, area.Width), Math.Min(900, area.Height)));
        RootGrid.ActualThemeChanged += (_, _) => UpdateCaptionTheme();
        RootGrid.Loaded += (_, _) => UpdateCaptionTheme();
        RootGrid.SizeChanged += (_, _) => { WorkspacePicker.Width = RootGrid.ActualWidth < 800 ? 140 : 230; AppTitleBar.Subtitle = RootGrid.ActualWidth < 1100 ? "" : L("Text_9750099F9F"); };
        InitializeWorkspaceLoad();
        InitializeResourceTabs();
        autosave.Tick += async (_, _) => await Guard(async () => { await session.AutosaveElapsedAsync(); if (!gate.WorkInProgress) Status(() => L("Text_11584EAAA5")); });
        AppWindow.Closing += async (_, e) =>
        {
            // After a failed startup the app replaces this window with the startup error.
            if (gate.HasClosed || startupFailed)
                return;
            e.Cancel = true;
            if (!gate.IsOpen || !await WaitForWorkspaceLoadBeforeClosing())
                return;
            if (gate.CloseBlockedByActivity || gate.WorkspaceLoading)
            { Status(() => L("CloseBlocked")); return; }
            try
            {
                var closed = await gate.Closing.RequestAsync(false, session.SaveForCloseAsync, FreezeClosing);
                if (closed)
                    Close();
            }
            catch { Status(() => L("Text_7AF24B0E8E")); }
        };
        AddShortcuts();
        // The window appears with the loading panel; the workspace page follows once the first read is in place.
        startup = workspaceLoad = OpenFirstWorkspace();
        verification?.Start();
    }
    // The first workspace read: how long it took and when its page was shown (for the QA startup measurement).
    double startupLoadMilliseconds;
    DateTime? startupShownAt;
    // What the QA build attaches to the window: a theme override, its command pipe and storage barrier, and the dialog
    // it may close. Only the QA build implements CreateVerification; the product build attaches nothing.
    interface IWindowVerification
    {
        void ConfigureTheme();
        void Start();
        void ObserveDialog(ContentDialog dialog, bool showing);
    }
    IWindowVerification? verification;
    partial void CreateVerification(ref IWindowVerification? hooks);
    void Status(Func<string> message) => ShowStatus(message, false);
    void StatusError(Func<string> message) => ShowStatus(message, true);
    void ShowStatus(Func<string> message, bool error)
    {
        statusText = message;
        StatusLabel.Text = message();
        StatusIcon.Visibility = error ? Visibility.Visible : Visibility.Collapsed;
        StatusLabel.Style = error ? UiTokens.StatusErrorTextStyle : UiTokens.StatusTextStyle;
    }
    void ToggleNavigation(TitleBar sender, object args) => Navigation.IsPaneOpen = !Navigation.IsPaneOpen;
    // Settings sits at the foot of the pane, apart from the pages of the working flow.
    NavigationViewItem PaneItem(ShellPage page) => (NavigationViewItem)(page == ShellPage.Settings ? Navigation.FooterMenuItems[0] : Navigation.MenuItems[page.PaneIndex()]);
    // Selects the pane item; its SelectionChanged asks the shell, which applies the gate.
    void SelectPane(ShellPage target) => Navigation.SelectedItem = PaneItem(target);
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
        catch (Exception e) { var description = MessageErrors.Describe(e); StatusError(() => L("Text_AEB8A69DBE") + L(description)); }
    }
    async Task Work(Func<Task> operation)
    {
        var work = gate.TryBeginWork();
        if (work is null)
            return;
        var pendingSave = session.TakeAutosavePending();
        ApplyShellState();
        try
        { if (pendingSave) await SaveEdits(); await operation(); }
        finally { work.Dispose(); ApplyShellState(); Render(); }
    }
    // The shell's controls follow the gate: navigation, the workspace and resource pickers, the page, the pane toggle
    // and the progress ring. Called whenever work, a local operation, a worker lock, a query or closing starts or ends.
    void ApplyShellState()
    {
        Navigation.IsEnabled = gate.NavigationEnabled;
        WorkspacePicker.IsEnabled = ResourcePicker.IsEnabled = gate.ContextPickersEnabled;
        PageHost.IsEnabled = gate.PageEnabled;
        AppTitleBar.IsPaneToggleButtonVisible = gate.PaneToggleVisible;
        BusyRing.IsActive = gate.ShowsActivity;
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
    // Every dialog opens through this, with its own model and view.
    IDialogService? dialogs;
    IDialogService Dialogs => dialogs ??= new DialogHost(this);
    // Shows a dialog model in a ContentDialog. A secondary action is shown only when the model names one; Cancel is
    // always offered and is the default button. The window takes no other input while it is open.
    sealed class DialogHost(MainWindow window) : IDialogService
    {
        public async Task<DialogResult> ShowAsync(DialogViewModel dialog)
        {
            var d = new ContentDialog
            {
                XamlRoot = window.Content.XamlRoot,
                Title = dialog.Title,
                Content = DialogViews.For(dialog),
                PrimaryButtonText = dialog.PrimaryLabel,
                SecondaryButtonText = dialog.SecondaryLabel ?? "",
                CloseButtonText = window.L("Text_2CD0F3BE87"),
                DefaultButton = ContentDialogButton.Close,
                IsPrimaryButtonEnabled = dialog.CanPrimary
            };
            using var open = window.gate.BeginDialog();
            window.verification?.ObserveDialog(d, true);
            try
            {
                return await d.ShowAsync() switch
                {
                    ContentDialogResult.Primary => DialogResult.Primary,
                    ContentDialogResult.Secondary => DialogResult.Secondary,
                    _ => DialogResult.Closed
                };
            }
            finally { window.verification?.ObserveDialog(d, false); }
        }
    }
    Task<bool> Confirm(DialogViewModel dialog) => Dialogs.ConfirmAsync(dialog);
    // Shorthands for the plain dialogs.
    Task<bool> ConfirmMessage(string title, string message, string primary, bool scrolls = false, DialogHeight height = DialogHeight.Unbounded)
        => Confirm(new MessageDialogViewModel { Title = title, Message = message, PrimaryLabel = primary, Scrolls = scrolls, Height = height });
    Task<bool> ConfirmList(string title, IReadOnlyList<string> items, string primary, IReadOnlyList<string>? header = null, DialogHeight height = DialogHeight.List, bool selectable = true)
        => Confirm(new ListDialogViewModel { Title = title, Items = items, PrimaryLabel = primary, Header = header ?? [], Height = height, Selectable = selectable });
    void Navigate(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (!ready || !gate.CanNavigate)
            return;
        CancelDataQuery();
        EditableCell.CloseHint();
        shell.TryNavigate(ShellPages.FromTag((args.SelectedItem as NavigationViewItem)?.Tag?.ToString()));
        Render();
    }
    async void WorkspaceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || WorkspacePicker.SelectedItem is not WorkspaceChoice choice || choice.Id == workspace.Id)
            return;
        if (!gate.CanSwitchContext)
        {
            // Another switch or a piece of work is still running: the choice is undone once this event is over.
            DispatcherQueue.TryEnqueue(ShowCurrentWorkspaceChoice);
            return;
        }
        await SwitchWorkspace(choice.Id);
    }
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
        if (shell.Page != ShellPage.Review)
            ReleaseReviewPage();
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
            OpenResource: kind => { resource = kind; SelectResourceTab(kind); SelectPane(ShellPage.Data); },
            OpenPage: SelectPane));
    async Task NewWorkspace()
    {
        if (!gate.CanAct)
            return;
        var name = new TextInputDialogViewModel { Title = L("Text_CDD0362664"), InputHeader = L("Text_8DCC576817"), Text = L("Text_A818AFCD80"), MaxLength = 80, PrimaryLabel = L("Confirm") };
        if (await Confirm(name) && !string.IsNullOrWhiteSpace(name.Text))
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
        // The record details pane opens beside the table.
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var query = BuildQueryBar(state);
        Grid.SetColumnSpan((FrameworkElement)query, 2);
        grid.Children.Add(query);
        var actions = BuildActionBar();
        Grid.SetRow(actions, 1);
        Grid.SetColumnSpan(actions, 2);
        grid.Children.Add(actions);
        var control = dataGrid = new DataGrid(dataFooter = new(localization), DataGridControlActions(), tableTemplates!.Template("RowTemplate"), DataRowHeight, localization);
        control.View.UserSelectionChanged += DataSelectionChanged;
        control.View.HeaderClicked += SortByHeader;
        control.View.PageSelectionClicked += async selectPage =>
        {
            if (CanEditData())
                await UserAction(() => { if (selectPage) SelectShownPage(); else ClearDataSelection(); return Task.CompletedTask; });
        };
        control.View.RowMenuRequested += ShowRowMenu;
        control.View.ShowHeaders(DisplayColumns());
        control.View.ShowSort(sortBox!.Current);
        shownDataPage = grid;
        // The table and its footer share the remaining rows.
        Grid.SetRow(control, 2);
        Grid.SetRowSpan(control, 2);
        grid.Children.Add(control);
        var details = BuildDetailsPane();
        Grid.SetRow(details, 2);
        Grid.SetRowSpan(details, 2);
        Grid.SetColumn(details, 1);
        // A margin, not column spacing: the closed pane leaves no gap.
        details.Margin = new Thickness(UiTokens.SpacingLarge, 0, 0, 0);
        grid.Children.Add(details);
        control.View.Table.Loaded += (_, _) => { if (shownState != null) RestoreDataOffsets(shownState); };
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
    // Every column of the resource, in table order: the status, the key, the fleet, then the data's own order.
    List<string> AllColumns()
    {
        var leading = new List<string> { "_delete", Schemas.All[resource].Key };
        if (resource is ResourceKind.Aircraft or ResourceKind.Routes)
            leading.Add(FleetAssignmentService.Field(resource));
        return leading.Concat(Data.Columns.Count == 0 ? Schemas.All[resource].Columns : Data.Columns).Distinct().ToList();
    }
    // The columns the table shows: all but the ones hidden from the view menu (never the status or the key).
    List<string> DisplayColumns() => HiddenColumns.Shown(resource, AllColumns()).ToList();
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
