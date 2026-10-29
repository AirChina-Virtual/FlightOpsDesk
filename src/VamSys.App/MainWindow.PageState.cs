using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using VamSys.Core;

namespace VamSys.App;

public sealed partial class MainWindow
{
    sealed class DataPageState
    {
        public string Search = "", AppliedSearch = "";
        public int Filter, AppliedFilter;
        public string? Sort, AppliedSort;
        public DataTableViewModel Model = new();
        public HashSet<Guid> Selected => Model.Selected;
        public Dictionary<Guid, DataRow>? Baseline;
        public HashSet<Guid> Conflicts = [];
        public Dictionary<string, FleetOption> FleetLabels = new(StringComparer.OrdinalIgnoreCase);
        public double Vertical, Horizontal;
        public readonly SelectionCounts Counts = new();

    }
    readonly Dictionary<(Guid, ResourceKind), DataPageState> dataStates = [];
    readonly Dictionary<Guid, SettingsPage> settingsPages = [];
    DataPageState? shownState;
    UIElement? shownDataPage;
    TextBox? searchBox; ComboBox? filterBox, sortBox;
    ScrollViewer? horizontalTable;
    DataPageState CurrentDataState => dataStates.TryGetValue((workspace.Id, resource), out var state) ? state : dataStates[(workspace.Id, resource)] = new();
    internal static T? FindVisual<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
                return match;
            if (FindVisual<T>(child) is { } found)
                return found;
        }
        return null;
    }
    void CapturePageState()
    {
        if (shownState is null || !ReferenceEquals(PageHost.Content, shownDataPage) || table is null)
            return;
        shownState.Search = searchBox?.Text ?? "";
        shownState.Filter = filterBox?.SelectedIndex ?? 0;
        shownState.Sort = sortBox?.SelectedItem as string;
        shownState.Model.SelectPage(table.SelectedItems.Cast<RowViewModel>().Select(r => r.Row.LocalId));
        shownState.Vertical = FindVisual<ScrollViewer>(table)?.VerticalOffset ?? 0;
        shownState.Horizontal = horizontalTable?.HorizontalOffset ?? 0;
    }
    void RestoreTableState(ListView target, ScrollViewer horizontal, DataPageState state)
    {
        foreach (var row in visible.Where(r => state.Selected.Contains(r.Row.LocalId)))
            target.SelectedItems.Add(row);
        target.Loaded += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            FindVisual<ScrollViewer>(target)?.ChangeView(null, state.Vertical, null, true);
            horizontal.ChangeView(state.Horizontal, null, null, true);
        });
    }
    // Views built for a replaced Workspace instance. A discarded page is released by WinUI only some
    // time after it leaves the tree, and until then its rows' edit closures keep the old workspace
    // alive; switching back then loads the new copy beside it, and the GC keeps both peaks committed.
    Workspace? viewsWorkspace;
    void ReleaseWorkspaceViews()
    {
        var previous = viewsWorkspace;
        viewsWorkspace = workspace;
        if (previous is null || ReferenceEquals(previous, workspace))
            return;
        // The data page and its row pool are kept and rebound when shown again; only the rows go.
        CancelDataQuery();
        shownState = null;
        dataPageOwner = null;
        ClearDataRows();
        DataGrid.Release();
        changeCounts.Clear();
        tableBaseline = [];
        tableConflicts = [];
        tableFleetLabels = new(StringComparer.OrdinalIgnoreCase);
        shownWorkspacePage = null;
        tasksPage = null;
        Tasks.Clear();
        // Each session's adapter holds the instance it was created for; a reload gets a new one anyway.
        online.Release(previous.Id);
    }
    // One settings page per workspace, kept so unsaved input survives navigation and language switches.
    UIElement GetSettingsPage()
    {
        if (!settingsPages.TryGetValue(workspace.Id, out var content))
            settingsPages[workspace.Id] = content = new SettingsPage(
                new SettingsViewModel(session, gate, settings, localization, online.EndpointUrl, online.Release),
                new SettingsPageActions(
                    Invoke: async action => { if (gate.CanInvokeCommand) await UserAction(action); },
                    Guard: Guard,
                    Status: key => Status(() => L(key)),
                    Connect: ConnectApi,
                    Refresh: RefreshApi,
                    LoadParameters: LoadParameters,
                    BackupClear: BackupAndClearWorkspace));
        return content;
    }
}
