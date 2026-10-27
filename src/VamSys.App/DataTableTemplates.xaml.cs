using Microsoft.UI.Xaml;

namespace VamSys.App;

public sealed partial class DataTableTemplates : ResourceDictionary
{
    public DataTableTemplates() => InitializeComponent();
    public DataTemplate Template(string key) => (DataTemplate)this[key];
}

// WinUI-typed layout values for the cell templates; the cell models in VamSys.Presentation stay free of WinUI.
public static class CellLayout
{
    public static Thickness MarginFor(bool compact) => compact ? new(0, 1, 6, 1) : new(0, 3, 6, 3);
}
