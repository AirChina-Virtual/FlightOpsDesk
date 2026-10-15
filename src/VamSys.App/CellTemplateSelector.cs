using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;

namespace VamSys.App;

// Create the one control a cell uses instead of three controls with two collapsed.
public sealed class CellTemplateSelector : DataTemplateSelector
{
    readonly DataTemplate editor = Template("""
        <TextBox IsReadOnly="{Binding IsReadOnly}" Text="{Binding Value, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}"
            ToolTipService.ToolTip="{Binding Field}" AutomationProperties.Name="{Binding Field}"/>
        """);
    readonly DataTemplate fleet = Template("""
        <Button IsEnabled="{Binding IsEnabled}" Command="{Binding ChooseFleet}" HorizontalAlignment="Stretch" HorizontalContentAlignment="Left"
            ToolTipService.ToolTip="{Binding DisplayValue}" AutomationProperties.Name="{Binding FleetAutomationName}">
            <TextBlock Text="{Binding DisplayValue}" TextTrimming="CharacterEllipsis"/>
        </Button>
        """);
    readonly DataTemplate status = Template("""
        <TextBlock Text="{Binding DisplayValue}" VerticalAlignment="Center" Margin="8,0"/>
        """);
    static DataTemplate Template(string content) => (DataTemplate)XamlReader.Load(
        "<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"><Grid Width=\"{Binding Width}\" Margin=\"0,3,6,3\">"+content+"</Grid></DataTemplate>");
    protected override DataTemplate SelectTemplateCore(object item) => item is CellViewModel cell
        ? cell.Field == "_delete" ? status : cell.Field is "Fleet ID" or "Fleet IDs" ? fleet : editor
        : editor;
    protected override DataTemplate SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);
}
