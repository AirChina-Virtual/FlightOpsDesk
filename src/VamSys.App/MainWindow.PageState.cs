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
        public HashSet<Guid> Selected = [];
        public double Vertical, Horizontal;
    }
    readonly Dictionary<(Guid, ResourceKind), DataPageState> dataStates = [];
    readonly Dictionary<Guid, UIElement> settingsPages = [];
    DataPageState? shownState;
    UIElement? shownDataPage;
    TextBox? searchBox; ComboBox? filterBox, sortBox;
    ScrollViewer? horizontalTable;
    DataPageState CurrentDataState => dataStates.TryGetValue((workspace.Id, resource), out var state) ? state : dataStates[(workspace.Id, resource)] = new();
    static T? FindVisual<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            if (FindVisual<T>(child) is { } found) return found;
        }
        return null;
    }
    void CapturePageState()
    {
        if (shownState is null || !ReferenceEquals(PageHost.Content, shownDataPage) || table is null) return;
        shownState.Search = searchBox?.Text ?? "";
        shownState.Filter = filterBox?.SelectedIndex ?? 0;
        shownState.Sort = sortBox?.SelectedItem as string;
        shownState.Selected = SelectedRows().Select(r => r.LocalId).ToHashSet();
        shownState.Vertical = FindVisual<ScrollViewer>(table)?.VerticalOffset ?? 0;
        shownState.Horizontal = horizontalTable?.HorizontalOffset ?? 0;
    }
    void RestoreTableState(ListView target, ScrollViewer horizontal, DataPageState state)
    {
        foreach (var row in visible.Where(r => state.Selected.Contains(r.Row.LocalId))) target.SelectedItems.Add(row);
        target.Loaded += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            FindVisual<ScrollViewer>(target)?.ChangeView(null, state.Vertical, null, true);
            horizontal.ChangeView(state.Horizontal, null, null, true);
        });
    }
    UIElement GetSettingsPage()
    {
        if (!settingsPages.TryGetValue(workspace.Id, out var content)) settingsPages[workspace.Id] = content = SettingsPage();
        return content;
    }
}
