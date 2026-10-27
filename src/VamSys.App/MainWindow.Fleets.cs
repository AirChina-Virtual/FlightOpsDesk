using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using VamSys.Core;

namespace VamSys.App;

public sealed partial class MainWindow
{
    Task ChooseFleets(DataRow? single) => LocalOperation(async () =>
    {
        var start = batch.BeginFleetAssignment(resource, single is null ? SelectedRows() : new List<DataRow> { single });
        if (start is null)
        { Status(() => L("BatchNothing")); return; }
        var selection = start.Selection;
        var rows = selection.Eligible;
        var field = start.Field;
        var initial = start.Initial;
        var options = start.Options;
        var summary = Text(() => "");
        var choices = new List<FleetOptionViewModel>();
        void Changed(FleetOptionViewModel choice)
        {
            if (resource == ResourceKind.Aircraft && choice.Selected)
                foreach (var other in choices.Where(c => c != choice && c.Selected))
                    other.Selected = false;
            summary.Text = L("Text_D18ED1A023") + string.Join("；", choices.Where(c => c.Selected).Select(c => c.Label));
        }
        choices.AddRange(options.Select(o => new FleetOptionViewModel(o, initial.Contains(o.Token, StringComparer.OrdinalIgnoreCase), Changed, localization)));
        summary.Text = L("Text_D18ED1A023") + string.Join("；", choices.Where(c => c.Selected).Select(c => c.Label));
        var search = new TextBox { }.Localize(localization, "PlaceholderText", () => L("Text_368B16B1B0"));
        var mode = new ComboBox { ItemsSource = new[] { L("Text_DD9F9DBA0C"), L("Text_7A387A9608"), L("Text_58A816EF01") }, SelectedIndex = 0, IsEnabled = resource == ResourceKind.Routes }.Localize(localization, "Header", () => L("Text_ED31FBB483")).Localize(localization, "ItemsSource", () => new[] { L("Text_DD9F9DBA0C"), L("Text_7A387A9608"), L("Text_58A816EF01") });
        var list = new ListView { Height = UiTokens.FleetChoiceListHeight, SelectionMode = ListViewSelectionMode.None, ItemsSource = choices };
        list.ItemTemplate = (DataTemplate)XamlReader.Load("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
              <CheckBox Content="{Binding Label}" IsChecked="{Binding Selected, Mode=TwoWay}" IsEnabled="{Binding CanSelect}"/>
            </DataTemplate>
            """);
        search.TextChanged += (_, _) => list.ItemsSource = choices.Where(c => c.Label.Contains(search.Text, StringComparison.OrdinalIgnoreCase)).ToList();
        mode.SelectionChanged += (_, _) => { foreach (var c in choices) c.AllowRemoval = mode.SelectedIndex == 2; };
        if (!await Confirm(resource == ResourceKind.Aircraft ? L("Text_F1439C5A73") : L("Text_F042305012"),
            new ScrollViewer { MaxHeight = Math.Max(160, RootGrid.ActualHeight - 220), Content = Stack(BatchTargets(selection, single != null), mode, search, list, summary) }, L("Text_5C250CFB41")))
            return;
        var preview = batch.PreviewFleets(resource, rows, choices.Where(c => c.Selected).Select(c => c.Option.Token), (FleetAssignmentMode)mode.SelectedIndex);
        if (preview.Issues.Count > 0)
        { await Confirm(L("Text_7B23705C3B"), new ScrollViewer { MaxHeight = UiTokens.DialogListMaxHeight, Content = Text(() => string.Join("\n", preview.Issues.Take(60).Select(i => L(i.Description)))) }, L("Text_3FD47EDCE4")); return; }
        if (preview.Rows.Count == 0)
        { Status(() => BatchScope(selection, 0)); return; }
        var labels = options.ToDictionary(o => o.Token, o => o.Describe(localization), StringComparer.OrdinalIgnoreCase);
        string Label(string value) => string.Join("；", FleetAssignmentService.Tokens(value).Select(t => labels.GetValueOrDefault(t, t)));
        var before = rows.ToDictionary(r => r.LocalId);
        if (!await Confirm(L("Text_D1482F496E"), new ListView { MaxHeight = UiTokens.FleetPreviewMaxHeight, Header = Text(() => BatchScope(selection, preview.Rows.Count)), ItemsSource = preview.Rows.Select(r => $"{Schemas.Key(resource, r)} {r.Get("Flight Number")}{r.Get("Registration")}\n{Label(before[r.LocalId].Get(field))} → {Label(r.Get(field))}").ToList() }, L("Text_2354C137CE")))
            return;
        await batch.ApplyFleetsAsync(resource, preview);
        Status(() => BatchScope(selection, preview.Rows.Count));
    });
    Task RetireSelected() => LocalOperation(async () =>
    {
        if (batch.ProposeRetire(resource, SelectedRows()) is not { } proposal)
            return;
        var selection = proposal.Selection;
        if (selection.Eligible.Count == 0)
        { Status(() => L("BatchNothing")); return; }
        var preview = proposal.Preview!;
        if (preview.Changes.Count == 0)
        { Status(() => BatchScope(selection, 0)); return; }
        if (!await Confirm(L("Text_B80DA74EC5"), BatchPreview(selection, preview.Field, preview.Changes), L("Text_52D4945C25")))
            return;
        await batch.ApplyEditAsync(resource, preview);
        Status(() => BatchScope(selection, preview.Changes.Count));
    });
}
