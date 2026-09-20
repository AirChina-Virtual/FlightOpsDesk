using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using VamSys.Core;

namespace VamSys.App;

public sealed partial class MainWindow
{
    async Task ChooseFleets(DataRow? single)
    {
        if (busy) return;
        var rows = single is null ? SelectedRows() : new List<DataRow> { single };
        if (rows.Count == 0) { Status(() => L("Text_1AB133520F")); return; }
        if (rows.Any(Schemas.Deleted)) { Status(() => L("Text_A1896E4C88")); return; }
        var field = FleetAssignmentService.Field(resource);
        var initial = rows.Count == 1 ? FleetAssignmentService.Tokens(rows[0].Get(field)) : new List<string>();
        var options = fleets.Options(workspace, rows.SelectMany(r => FleetAssignmentService.Tokens(r.Get(field))));
        var summary = Text(() => "");
        var choices = new List<FleetOptionViewModel>();
        void Changed(FleetOptionViewModel choice)
        {
            if (resource == ResourceKind.Aircraft && choice.Selected)
                foreach (var other in choices.Where(c => c != choice && c.Selected)) other.Selected = false;
            summary.Text = L("Text_D18ED1A023") + string.Join("；", choices.Where(c => c.Selected).Select(c => c.Label));
        }
        choices.AddRange(options.Select(o => new FleetOptionViewModel(o, initial.Contains(o.Token, StringComparer.OrdinalIgnoreCase), Changed, localization)));
        summary.Text = L("Text_D18ED1A023") + string.Join("；", choices.Where(c => c.Selected).Select(c => c.Label));
        var search = new TextBox { }.Localize(localization, "PlaceholderText", () => L("Text_368B16B1B0"));
        var mode = new ComboBox { ItemsSource = new[] { L("Text_DD9F9DBA0C"), L("Text_7A387A9608"), L("Text_58A816EF01") }, SelectedIndex = 0, IsEnabled = resource == ResourceKind.Routes }.Localize(localization, "Header", () => L("Text_ED31FBB483")).Localize(localization, "ItemsSource", () => new[] { L("Text_DD9F9DBA0C"), L("Text_7A387A9608"), L("Text_58A816EF01") });
        var list = new ListView { Height = 180, SelectionMode = ListViewSelectionMode.None, ItemsSource = choices };
        list.ItemTemplate = (DataTemplate)XamlReader.Load("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
              <CheckBox Content="{Binding Label}" IsChecked="{Binding Selected, Mode=TwoWay}" IsEnabled="{Binding CanSelect}"/>
            </DataTemplate>
            """);
        search.TextChanged += (_, _) => list.ItemsSource = choices.Where(c => c.Label.Contains(search.Text, StringComparison.OrdinalIgnoreCase)).ToList();
        mode.SelectionChanged += (_, _) => { foreach (var c in choices) c.AllowRemoval = mode.SelectedIndex == 2; };
        if (!await Confirm(resource == ResourceKind.Aircraft ? L("Text_F1439C5A73") : L("Text_F042305012"),
            new ScrollViewer { MaxHeight = Math.Max(160, RootGrid.ActualHeight - 220), Content = Stack(Text(() => L("Text_182B2BEF88", ("arg0", rows.Count))), mode, search, list, summary) }, L("Text_5C250CFB41"))) return;
        var preview = fleets.Preview(workspace, resource, rows, choices.Where(c => c.Selected).Select(c => c.Option.Token), (FleetAssignmentMode)mode.SelectedIndex);
        if (preview.Issues.Count > 0) { await Confirm(L("Text_7B23705C3B"), new ScrollViewer { MaxHeight = 350, Content = Text(() => string.Join("\n", preview.Issues.Take(60).Select(i => L(i.Description)))) }, L("Text_3FD47EDCE4")); return; }
        if (preview.Rows.Count == 0) { Status(() => L("Text_AB90F9524E")); return; }
        var labels = options.ToDictionary(o => o.Token, o => o.Describe(localization), StringComparer.OrdinalIgnoreCase);
        string Label(string value) => string.Join("；", FleetAssignmentService.Tokens(value).Select(t => labels.GetValueOrDefault(t, t)));
        var before = rows.ToDictionary(r => r.LocalId);
        if (!await Confirm(L("Text_D1482F496E"), new ListView { MaxHeight = 380, Header = Text(() => L("Text_CCFF94DA2A", ("arg0", preview.Rows.Count))), ItemsSource = preview.Rows.Select(r => $"{Schemas.Key(resource, r)} {r.Get("Flight Number")}{r.Get("Registration")}\n{Label(before[r.LocalId].Get(field))} → {Label(r.Get(field))}").ToList() }, L("Text_2354C137CE"))) return;
        fleets.Apply(Data, preview); await Save(); Render(); Status(() => L("Text_46F566CD0E", ("arg0", preview.Rows.Count)));
    }

    async Task RetireSelected()
    {
        if (busy || resource != ResourceKind.Routes) return;
        var rows = SelectedRows();
        if (rows.Count == 0 || rows.Any(Schemas.Deleted)) { Status(() => L("Text_C124B32C00")); return; }
        // A past date remains past regardless of the VA's configured time zone.
        var end = DateTime.UtcNow.AddDays(-2).ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        if (!await Confirm(L("Text_B80DA74EC5"), new ListView { MaxHeight = 350, Header = Text(() => L("Text_A5124DA2BA", ("arg0", rows.Count), ("arg1", end))), ItemsSource = rows.Select(r => $"{r.Get("ID")} · {r.Get("Flight Number")} · {r.Get("End Date")} → {end}").ToList() }, L("Text_52D4945C25"))) return;
        Data.Checkpoint(); foreach (var row in rows) row.Fields["End Date"] = end;
        if (!Data.Columns.Contains("End Date")) Data.Columns.Add("End Date");
        await Save(); Render(); Status(() => L("Text_B4F14FDEB3"));
    }
}
