using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using VamSys.Core;
using VamSys.Presentation;

namespace VamSys.App;

// The DataGrid control's row list and its horizontal scroller: column headers, row height, locking, the rows shown and
// their selection. The window decides what is shown and is told only about selection changes the user made.
sealed class DataTableView
{
    readonly ILocalizationService language;
    readonly Border headerHost;
    List<string> headerColumns = [];

    public DataTableView(ListView table, Border headerHost, ScrollViewer horizontal, DataTemplate rowTemplate, double rowHeight, ILocalizationService language)
    {
        Table = table;
        this.headerHost = headerHost;
        Horizontal = horizontal;
        this.language = language;
        AutomationProperties.SetAutomationId(table, "DataTable");
        // A locked table takes no pointer input; keyboard focus cannot enter it either.
        table.GettingFocus += (_, e) => { if (!table.IsHitTestVisible) e.TryCancel(); };
        table.SelectionChanged += (_, e) => { if (!Restoring) UserSelectionChanged?.Invoke(e.AddedItems.Cast<RowViewModel>().ToList(), e.RemovedItems.Cast<RowViewModel>().ToList()); };
        table.ItemTemplate = rowTemplate;
        // Right click, Shift+F10 or the menu key on a row: the window shows the row's menu.
        table.ContextRequested += (_, e) =>
        {
            if (!table.IsHitTestVisible || e.OriginalSource is not DependencyObject source)
                return;
            ListViewItem? item = null;
            EditableCell? cell = null;
            for (var node = source; node != null && item is null; node = VisualTreeHelper.GetParent(node))
            {
                cell ??= node as EditableCell;
                item = node as ListViewItem;
            }
            if (item?.Content is not RowViewModel row)
                return;
            e.Handled = true;
            var target = (FrameworkElement?)cell ?? item;
            RowMenuRequested?.Invoke(row, cell?.Cell, target, e.TryGetPosition(target, out var point) ? point : null);
        };
        table.ItemContainerTransitions = new Microsoft.UI.Xaml.Media.Animation.TransitionCollection();
        // Keep WinUI's themed selection presenter, including the paired checkbox fill and glyph.
        var style = new Style(typeof(ListViewItem)) { BasedOn = (Style)Application.Current.Resources["DefaultListViewItemStyle"] };
        style.Setters.Add(new Setter(FrameworkElement.HeightProperty, rowHeight));
        style.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, rowHeight));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        style.Setters.Add(new Setter(Control.FontSizeProperty, UiTokens.BodyFontSize));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        table.ItemContainerStyle = style;
        table.Resources["ListViewItemMinHeight"] = rowHeight;
    }

    public ListView Table { get; }
    public ScrollViewer Horizontal { get; }
    public ScrollViewer? Vertical => MainWindow.FindVisual<ScrollViewer>(Table);
    // Set while this class changes the selection, so those changes are not reported as the user's.
    public bool Restoring { get; private set; }
    // Rows the user selected and deselected (pointer, keyboard, automation).
    public event Action<List<RowViewModel>, List<RowViewModel>>? UserSelectionChanged;
    // A row's menu was asked for: the row, the cell it was asked on (if any), where to show it.
    public event Action<RowViewModel, CellViewModel?, FrameworkElement, Windows.Foundation.Point?>? RowMenuRequested;

    // A click on a column header asks to sort by it (the status column has no value to sort by).
    public event Action<string>? HeaderClicked;
    (string? Field, bool Descending) sortShown;
    readonly Dictionary<string, TextBlock> headerLabels = [];

    public void ShowHeaders(List<string> columns)
    {
        if (headerHost.Child != null && headerColumns.SequenceEqual(columns))
            return;
        headerColumns = columns;
        headerLabels.Clear();
        var headers = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0, Margin = UiTokens.DataHeaderInset };
        foreach (var field in columns)
        {
            string Name() => field == "_delete" ? language.Get("ColumnStatus") : ApiOperationPolicy.FieldLabel(field, language);
            string Label() => Name() + (sortShown.Field == field ? sortShown.Descending ? " ↓" : " ↑" : "");
            var label = new TextBlock
            {
                Width = new CellViewModel(field, "", _ => { }).Width,
                Margin = UiTokens.ColumnHeaderMargin,
                Style = UiTokens.ColumnHeaderTextStyle
            }.Localize(language, "Text", Label);
            if (field != "_delete")
            {
                label.Tapped += (_, _) => HeaderClicked?.Invoke(field);
                // A second click soon after the first is a double tap, not another tap: it turns the order round too.
                label.DoubleTapped += (_, _) => HeaderClicked?.Invoke(field);
                ToolTipService.SetToolTip(label, new ToolTip().Localize(language, "Content", () => Name() + " · " + language.Get("TableSortHint")));
            }
            else
                ToolTipService.SetToolTip(label, new ToolTip().Localize(language, "Content", Name));
            headerLabels[field] = label;
            headers.Children.Add(label);
        }
        headerHost.Child = headers;
        ShowSort(sortShown);
    }
    // The arrow on the sorted column's header.
    public void ShowSort((string? Field, bool Descending) sort)
    {
        sortShown = sort;
        foreach (var (field, label) in headerLabels)
        {
            var name = field == "_delete" ? language.Get("ColumnStatus") : ApiOperationPolicy.FieldLabel(field, language);
            label.Text = name + (sort.Field == field ? sort.Descending ? " ↓" : " ↑" : "");
        }
    }

    // A new ItemsSource makes WinUI create every row's controls again and keep native memory for the discarded ones,
    // so pooled rows are rebound in place whenever the row count stays the same. An observable collection would also
    // recycle them when the count changes, but any observable source doubled the layout time of paging and language
    // changes (P95 10 → 20 ms, 25 → 56 ms).
    public void ShowRows(List<RowViewModel> rows, Func<RowViewModel, bool> selected)
    {
        Restoring = true;
        try
        {
            Table.SelectedItems.Clear();
            if (Table.ItemsSource is not List<RowViewModel> old || old.Count != rows.Count || rows.Count > 0 && !ReferenceEquals(old[0], rows[0]))
                Table.ItemsSource = rows;
        }
        finally { Restoring = false; }
        ApplySelection(rows, selected);
    }
    // Selects exactly these of the shown rows, without changing the rows.
    public void ApplySelection(IEnumerable<RowViewModel> shown, Func<RowViewModel, bool> selected)
    {
        Restoring = true;
        try
        {
            Table.SelectedItems.Clear();
            foreach (var row in shown)
                if (selected(row))
                    Table.SelectedItems.Add(row);
        }
        finally { Restoring = false; }
    }
    // Removing the rows must not read as the user deselecting them.
    public void ClearRows() => ShowRows([], _ => false);
    public void ClearSelection()
    {
        Restoring = true;
        try { Table.SelectedItems.Clear(); }
        finally { Restoring = false; }
    }
    // Adds rows to the selection without reporting them.
    public void Select(IEnumerable<RowViewModel> rows)
    {
        Restoring = true;
        try { foreach (var row in rows) if (!Table.SelectedItems.Contains(row)) Table.SelectedItems.Add(row); }
        finally { Restoring = false; }
    }
    public IEnumerable<RowViewModel> SelectedRows => Table.SelectedItems.Cast<RowViewModel>();

    // Not IsEnabled: it cascades to every realized cell, and WinUI queues one IsEnabledChanged event per control and
    // drains one per dispatcher tick. Every query toggled it, so the queue only grew, and closing the window then
    // spent minutes draining it before the process exited. Returns true when locking took focus out of the table.
    public bool Lock(bool locked)
    {
        if (Table.IsHitTestVisible != locked)
            return false;
        Table.IsHitTestVisible = !locked;
        Table.Opacity = locked ? UiTokens.LockedTableOpacity : 1;
        if (!locked || Table.XamlRoot is null)
            return false;
        for (var focused = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(Table.XamlRoot) as DependencyObject; focused != null; focused = VisualTreeHelper.GetParent(focused))
            if (ReferenceEquals(focused, Table))
                return true;
        return false;
    }

    public (double Vertical, double Horizontal) Offsets => (Vertical?.VerticalOffset ?? 0, Horizontal.HorizontalOffset);

    // The first of the shown rows that is at least partly in view, and its top relative to the viewport.
    public (Guid Id, double Top)? FirstVisibleRow(IReadOnlyList<RowViewModel> shown)
    {
        if (Vertical is not { } scroll)
            return null;
        for (var n = 0; n < shown.Count; n++)
            if (Table.ContainerFromIndex(n) is FrameworkElement item)
            {
                var y = item.TransformToVisual(scroll).TransformPoint(new Windows.Foundation.Point()).Y;
                if (y + item.ActualHeight > 0 && y < scroll.ViewportHeight)
                    return (shown[n].Row.LocalId, y);
            }
        return null;
    }

    public void ScrollTo(double? vertical, double? horizontal)
    {
        if (vertical != null)
            Vertical?.ChangeView(null, vertical, null, true);
        if (horizontal != null)
            Horizontal.ChangeView(horizontal, null, null, true);
    }
}
