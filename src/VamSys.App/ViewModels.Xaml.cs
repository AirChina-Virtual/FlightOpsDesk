using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace VamSys.App;

// WinUI-typed members for compiled bindings; ViewModels.cs stays free of WinUI for the test project.
public sealed partial class CellViewModel
{
    public Thickness Margin => Compact ? new(0, 1, 6, 1) : new(0, 3, 6, 3);
}
public sealed partial class RowViewModel
{
    public DataTemplateSelector? Selector => CellTemplates as DataTemplateSelector;
}
