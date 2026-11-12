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
        var form = new FleetChoiceDialogViewModel(BatchTargetsText(selection, single != null), options, initial, singleFleet: resource == ResourceKind.Aircraft, canChooseMode: resource == ResourceKind.Routes, localization)
        {
            Title = resource == ResourceKind.Aircraft ? L("Text_F1439C5A73") : L("Text_F042305012"),
            // One row: the choice is the change, applied without a second preview dialog (undo reverts it).
            PrimaryLabel = single != null ? L("FleetChoiceApply") : L("Text_5C250CFB41"),
            AvailableHeight = Math.Max(160, RootGrid.ActualHeight - 220)
        };
        if (!await Confirm(form))
            return;
        var preview = batch.PreviewFleets(resource, rows, form.SelectedTokens, form.Mode);
        if (preview.Issues.Count > 0)
        { await ConfirmMessage(L("Text_7B23705C3B"), string.Join("\n", preview.Issues.Take(60).Select(i => L(i.Description))), L("Text_3FD47EDCE4"), scrolls: true, DialogHeight.List); return; }
        if (preview.Rows.Count == 0)
        { Status(() => BatchScope(selection, 0)); return; }
        var labels = options.ToDictionary(o => o.Token, o => o.Describe(localization), StringComparer.OrdinalIgnoreCase);
        string Label(string value) => string.Join("；", FleetAssignmentService.Tokens(value).Select(t => labels.GetValueOrDefault(t, t)));
        var before = rows.ToDictionary(r => r.LocalId);
        if (single == null && !await ConfirmList(L("Text_D1482F496E"), preview.Rows.Select(r => $"{Schemas.Key(resource, r)} {r.Get("Flight Number")}{r.Get("Registration")}\n{Label(before[r.LocalId].Get(field))} → {Label(r.Get(field))}").ToList(),
            L("Text_2354C137CE"), [BatchScope(selection, preview.Rows.Count)], DialogHeight.FleetPreview))
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
        if (!await Confirm(BatchPreview(L("Text_B80DA74EC5"), L("Text_52D4945C25"), selection, preview.Field, preview.Changes)))
            return;
        await batch.ApplyEditAsync(resource, preview);
        Status(() => BatchScope(selection, preview.Changes.Count));
    });
}
