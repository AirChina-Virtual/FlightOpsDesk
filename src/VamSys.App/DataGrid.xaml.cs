using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using VamSys.Core;
using VamSys.Presentation;

namespace VamSys.App;

// What the table footer asks of the window. Run applies the guard every paging and selection button uses; page size
// and query cancellation act directly (the window checks the page size against the shown state).
public sealed record DataGridActions(Func<bool> CanEdit, Func<Action, Task> Run, Action FirstPage, Action PreviousPage, Action NextPage, Action LastPage,
    Action<string> GoToPage, Action<int> ChangePageSize, Action SelectPage, Action SelectAll, Action ClearSelection, Action CancelQuery);

// The data table: the row list in its horizontal scroller (headers, row height, rows, selection and locking through
// View), and the paging and selection footer. The window decides which rows are shown and acts on the footer's requests.
public sealed partial class DataGrid : UserControl
{
    readonly DataGridActions actions;

    public DataGrid(DataGridFooterViewModel footer, DataGridActions actions, DataTemplate rowTemplate, double rowHeight, ILocalizationService language)
    {
        Footer = footer;
        this.actions = actions;
        InitializeComponent();
        var root = (Grid)Content;
        var horizontal = (ScrollViewer)root.Children[0];
        var table = (Grid)horizontal.Content;
        View = new DataTableView((ListView)table.Children[1], (Border)table.Children[0], horizontal, rowTemplate, rowHeight, language);
        ArrangeFooter(FooterGrid);
    }
    Grid FooterGrid => (Grid)((Grid)Content).Children[2];

    public DataGridFooterViewModel Footer { get; }
    internal DataTableView View { get; }
    internal TextBox PageJump => (TextBox)Navigation.Children[4];
    internal ComboBox PageSize => (ComboBox)Navigation.Children[6];
    StackPanel Navigation => (StackPanel)((StackPanel)((ScrollViewer)FooterGrid.Children[1]).Content).Children[0];

    void FooterSizeChanged(object sender, SizeChangedEventArgs e) => ArrangeFooter((Grid)sender);
    // Selection and paging share a row when both fit, and paging moves below the selection otherwise.
    static void ArrangeFooter(Grid footer)
    {
        var selectionHost = (FrameworkElement)footer.Children[0];
        var navigationHost = (FrameworkElement)footer.Children[1];
        var selected = Inner(selectionHost);
        var navigation = Inner(navigationHost);
        var wide = footer.ActualWidth >= Math.Max(800, selected.DesiredSize.Width + navigation.DesiredSize.Width + 8);
        Grid.SetColumn(navigationHost, wide ? 1 : 0);
        Grid.SetRow(navigationHost, wide ? 0 : 1);
        Grid.SetColumnSpan(navigationHost, wide ? 1 : 2);
        Grid.SetColumnSpan(selectionHost, wide ? 1 : 2);
        navigationHost.HorizontalAlignment = wide ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
        static UIElement Inner(FrameworkElement host) => ((StackPanel)((ScrollViewer)host).Content).Children[0];
    }

    async void FirstPage(object sender, RoutedEventArgs e) => await actions.Run(actions.FirstPage);
    async void PreviousPage(object sender, RoutedEventArgs e) => await actions.Run(actions.PreviousPage);
    async void NextPage(object sender, RoutedEventArgs e) => await actions.Run(actions.NextPage);
    async void LastPage(object sender, RoutedEventArgs e) => await actions.Run(actions.LastPage);
    async void GoPage(object sender, RoutedEventArgs e) => await actions.Run(() => actions.GoToPage(Footer.PageJumpText));
    async void PageJumpKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter || !actions.CanEdit())
            return;
        e.Handled = true;
        await actions.Run(() => actions.GoToPage(((TextBox)sender).Text));
    }
    void PageSizeChanged(object sender, SelectionChangedEventArgs e)
    { if (((ComboBox)sender).SelectedItem is int size) actions.ChangePageSize(size); }
    async void SelectPage(object sender, RoutedEventArgs e) => await actions.Run(actions.SelectPage);
    async void SelectAll(object sender, RoutedEventArgs e) => await actions.Run(actions.SelectAll);
    async void ClearSelection(object sender, RoutedEventArgs e) => await actions.Run(actions.ClearSelection);
    void CancelQuery(object sender, RoutedEventArgs e) => actions.CancelQuery();
}
