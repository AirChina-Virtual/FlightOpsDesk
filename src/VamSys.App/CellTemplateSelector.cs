using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace VamSys.App;

// Create the one control a cell uses instead of three controls with two collapsed.
// Density is carried by each cell's Height and Margin, so one set of templates serves both.
public sealed class CellTemplateSelector(DataTableTemplates templates) : DataTemplateSelector
{
    readonly DataTemplate editor = templates.Template("EditorTemplate"), fleet = templates.Template("FleetTemplate"), status = templates.Template("StatusTemplate");
    protected override DataTemplate SelectTemplateCore(object item) => item is CellViewModel cell
        ? cell.Field == "_delete" ? status : cell.Field is "Fleet ID" or "Fleet IDs" or "Parameter IDs" ? fleet : editor
        : editor;
    protected override DataTemplate SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);
}
