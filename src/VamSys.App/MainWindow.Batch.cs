using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VamSys.Core;

namespace VamSys.App;

public sealed partial class MainWindow
{
    AppBarButton BatchCommand(Func<string> label, Symbol icon, Func<Task> action, string target)
    {
        var button = Command(label, icon, action);
        button.Tag = target;
        ToolTipService.SetToolTip(button, new ToolTip().Localize(localization, "Content", () => L("BatchOnlySelected")));
        return button;
    }
    (int Active, int Deleted, int Deletable) SelectionCounts()
    {
        if (shownState is null)
            return (0, 0, 0);
        return shownState.Counts.Read(shownState.Model.Selected, Data.FindDraftRow, tableBaseline.ContainsKey);
    }
    async Task LocalOperation(Func<Task> action)
    {
        var scope = gate.TryBeginLocal();
        if (scope is null)
            return;
        Navigation.IsEnabled = WorkspacePicker.IsEnabled = ResourcePicker.IsEnabled = false;
        PageHost.IsEnabled = false;
        BusyRing.IsActive = true;
        RefreshDataAvailability();
        try
        {
            await session.FlushAutosaveAsync();
            await action();
        }
        finally
        {
            // A review worker started inside this operation still reads the workspace: it unlocks when done.
            scope.Dispose();
            if (scope.Released)
            {
                Navigation.IsEnabled = WorkspacePicker.IsEnabled = ResourcePicker.IsEnabled = gate.ControlsEnabled;
                PageHost.IsEnabled = gate.ControlsEnabled;
            }
            BusyRing.IsActive = gate.ShowsProgress;
            Render();
        }
    }

    string BatchScope(BatchSelection selection, int changed, int ruleSkipped = 0) => L("BatchCounts", ("selected", selection.Selected.Count),
        ("skipped", selection.Skipped + ruleSkipped), ("unchanged", selection.Eligible.Count - changed - ruleSkipped), ("changed", changed));
    string BatchScope(BatchEditPreview preview) => BatchScope(preview.Selection, preview.Changes.Count, preview.RuleSkips.Count);
    TextBlock BatchTargets(BatchSelection selection, bool single = false) => Text(() => L(single ? "BatchSingle" : "BatchTargets",
        ("selected", selection.Selected.Count), ("skipped", selection.Skipped), ("eligible", selection.Eligible.Count)));
    ListView BatchPreview(BatchSelection selection, string field, IReadOnlyList<DataRow> changes, IReadOnlyList<MessageDescriptor>? skipReasons = null, int ruleSkipped = 0)
    {
        var before = selection.Eligible.ToDictionary(r => r.LocalId);
        return new ListView
        {
            MaxHeight = UiTokens.BatchPreviewMaxHeight,
            SelectionMode = ListViewSelectionMode.None,
            Header = skipReasons is { Count: > 0 }
                ? Stack([Text(() => BatchScope(selection, changes.Count, ruleSkipped)), .. skipReasons.Select(reason => Text(() => L(reason)))])
                : Text(() => BatchScope(selection, changes.Count)),
            ItemsSource = changes.Select(r => $"{Schemas.Key(resource, r)} {r.Get("Flight Number")}{r.Get("Registration")}\n" +
                (field == "_delete" ? $"{L("ColumnStatus")}: {L("Text_EE96DC1EBE")} → {L("BatchRestored")}" : $"{field}: {before[r.LocalId].Get(field)} → {r.Get(field)}")).ToArray()
        };
    }

    Task DeleteSelected() => LocalOperation(async () =>
    {
        var proposal = batch.ProposeDelete(resource, SelectedRows());
        var selection = proposal.Selection;
        if (proposal.NothingToDo)
        { Status(() => L("BatchNothing")); return; }
        if (proposal.Issues.Count > 0)
        {
            await Confirm(L("Text_FD6D0919A9"), new ListView { MaxHeight = UiTokens.DialogListMaxHeight, ItemsSource = proposal.Issues.Select(i => L(i.Description)).ToArray() }, L("Text_3FD47EDCE4"));
            return;
        }
        var plan = proposal.Plan!;
        var ids = proposal.Ids;
        var preview = new ListView
        {
            MaxHeight = UiTokens.BatchPreviewMaxHeight,
            SelectionMode = ListViewSelectionMode.None,
            Header = Stack(Text(() => BatchScope(selection, ids.Count)), Text(() => L("Text_0360D81E1F", ("arg0", plan.RemoveDrafts.Count), ("arg1", plan.MarkExisting.Count))), Text(() => L("Text_0E38068B45"))),
            ItemsSource = selection.Eligible.Where(r => ids.Contains(r.LocalId)).Select(r => $"{Schemas.Key(resource, r)} · {r.Get("Name")}{r.Get("Flight Number")}").ToArray()
        };
        if (!await Confirm(L("Text_948725D00E"), preview, L("Text_A3EA3C17B4")))
            return;
        await batch.ApplyDeleteAsync(proposal);
        Status(() => BatchScope(selection, ids.Count));
    });

    Task RestoreSelected() => LocalOperation(async () =>
    {
        var proposal = batch.ProposeRestore(SelectedRows());
        var selection = proposal.Selection;
        if (proposal.Changes.Count == 0)
        { Status(() => L("BatchNothing")); return; }
        if (!await Confirm(L("Text_A6926595CE"), BatchPreview(selection, "_delete", proposal.Changes), L("Confirm")))
            return;
        await batch.ApplyRestoreAsync(resource, proposal);
        Status(() => BatchScope(selection, proposal.Changes.Count));
    });

    Task EditRules() => LocalOperation(async () =>
    {
        var selection = batch.Capture(SelectedRows());
        if (selection.Eligible.Count == 0)
        { Status(() => L("BatchNothing")); return; }
        var field = new ComboBox { ItemsSource = batch.RuleFields(DisplayColumns()), SelectedIndex = 0, MinWidth = UiTokens.RuleFieldPickerMinWidth }
            .Localize(localization, "Header", () => L("Text_86F623E5CC"));
        string[] keys = ["Text_A30D395090", "Text_5B7341C2D6", "Text_795CBFF909", "Text_6FE8FC1819", "Text_FAC7E021A1", "Text_E79453E164", "Text_6797F1B017"];
        var kinds = batch.RuleKinds(resource);
        var kind = new ComboBox { SelectedIndex = 0 }.Localize(localization, "Header", () => L("Text_ED31FBB483"))
            .Localize(localization, "ItemsSource", () => kinds.Select(k => L(keys[(int)k])).ToArray());
        kind.SelectionChanged += (_, _) => field.IsEnabled = kind.SelectedIndex >= 0 && kinds[kind.SelectedIndex] != RuleKind.Retire;
        var value = new TextBox().Localize(localization, "Header", () => L("Text_DD5ABC95B1"));
        var find = new TextBox().Localize(localization, "Header", () => L("Text_8FAB339859"));
        if (!await Confirm(L("Text_E306752B35"), Stack(BatchTargets(selection), field, kind, find, value), L("Text_13D61FEA9F")))
            return;
        var rule = new EditRule(kinds[kind.SelectedIndex], field.SelectedItem!.ToString()!, value.Text, find.Text);
        // UI mutation remains locked while the copies are processed off-thread.
        var preview = await batch.PreviewRuleAsync(selection, rule);
        if (preview.Validation is { } invalid)
        { Status(() => L(invalid)); return; }
        if (preview.Changes.Count == 0)
        { Status(() => string.Join(" ", new[] { BatchScope(preview) }.Concat(preview.SkipReasons.Select(r => L(r))))); return; }
        if (!await Confirm(L("Text_AD86791376"), BatchPreview(selection, preview.Field, preview.Changes, preview.SkipReasons, preview.RuleSkips.Count), L("Text_2354C137CE")))
            return;
        await batch.ApplyEditAsync(resource, preview);
        Status(() => BatchScope(preview));
    });
}
