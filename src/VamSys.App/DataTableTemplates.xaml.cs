using Microsoft.UI.Xaml;

namespace VamSys.App;

public sealed partial class DataTableTemplates : ResourceDictionary
{
    public DataTableTemplates() => InitializeComponent();
    public DataTemplate Template(string key) => (DataTemplate)this[key];
}
