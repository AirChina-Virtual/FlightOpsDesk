using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace VamSys.App;

// Lays out one table row's cells and keeps them when the list recycles the row for another record:
// each cell control only receives the new cell model as DataContext, and its compiled bindings update.
// An ItemsControl regenerated all cell controls whenever a recycled row got a different cell list.
public sealed partial class DataRowPresenter : StackPanel
{
    public static readonly DependencyProperty RowProperty = DependencyProperty.Register(nameof(Row), typeof(RowViewModel), typeof(DataRowPresenter),
        new PropertyMetadata(null, (target, args) => ((DataRowPresenter)target).Present((RowViewModel?)args.NewValue)));
    public RowViewModel? Row { get => (RowViewModel?)GetValue(RowProperty); set => SetValue(RowProperty, value); }
    readonly List<DataTemplate> templates = [];

    public DataRowPresenter() => Orientation = Orientation.Horizontal;
    void Present(RowViewModel? row)
    {
        if (row?.CellTemplates is not DataTemplateSelector selector)
        { Children.Clear(); templates.Clear(); return; }
        var cells = row.Cells;
        for (var n = 0; n < cells.Count; n++)
        {
            var template = selector.SelectTemplate(cells[n]);
            if (n < Children.Count && ReferenceEquals(templates[n], template))
            { ((FrameworkElement)Children[n]).DataContext = cells[n]; continue; }
            var element = (FrameworkElement)template.LoadContent();
            element.DataContext = cells[n];
            if (n < Children.Count)
            { Children[n] = element; templates[n] = template; }
            else
            { Children.Add(element); templates.Add(template); }
        }
        while (Children.Count > cells.Count)
        { Children.RemoveAt(Children.Count - 1); templates.RemoveAt(templates.Count - 1); }
    }
}
