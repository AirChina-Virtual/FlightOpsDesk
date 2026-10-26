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
        if (shownState.SelectionCountsDirty)
        {
            shownState.SelectedActive = shownState.SelectedDeleted = shownState.SelectedDeletable = 0;
            foreach (var id in shownState.Model.Selected)
                if (Data.FindDraftRow(id) is { } row)
                {
                    if (Schemas.Deleted(row))
                        shownState.SelectedDeleted++;
                    else
                        shownState.SelectedActive++;
                    if (!Schemas.Deleted(row) || !tableBaseline.ContainsKey(id))
                        shownState.SelectedDeletable++;
                }
            shownState.SelectionCountsDirty = false;
        }
        return (shownState.SelectedActive, shownState.SelectedDeleted, shownState.SelectedDeletable);
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
            if (autosave.IsEnabled)
            { autosave.Stop(); await SaveEdits(); }
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
            MaxHeight = 360,
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
        var selection = BatchSelection.ForDeletion(SelectedRows(), Data.Snapshot);
        if (selection.Eligible.Count == 0)
        { Status(() => L("BatchNothing")); return; }
        var plan = deletions.Preview(workspace, resource, selection.Eligible.Select(r => r.LocalId));
        if (plan.Issues.Count > 0)
        {
            await Confirm(L("Text_FD6D0919A9"), new ListView { MaxHeight = 350, ItemsSource = plan.Issues.Select(i => L(i.Description)).ToArray() }, L("Text_3FD47EDCE4"));
            return;
        }
        var ids = plan.RemoveDrafts.Concat(plan.MarkExisting).ToHashSet();
        if (ids.Count == 0)
        { Status(() => L("BatchNothing")); return; }
        var preview = new ListView
        {
            MaxHeight = 360,
            SelectionMode = ListViewSelectionMode.None,
            Header = Stack(Text(() => BatchScope(selection, ids.Count)), Text(() => L("Text_0360D81E1F", ("arg0", plan.RemoveDrafts.Count), ("arg1", plan.MarkExisting.Count))), Text(() => L("Text_0E38068B45"))),
            ItemsSource = selection.Eligible.Where(r => ids.Contains(r.LocalId)).Select(r => $"{Schemas.Key(resource, r)} · {r.Get("Name")}{r.Get("Flight Number")}").ToArray()
        };
        if (!await Confirm(L("Text_948725D00E"), preview, L("Text_A3EA3C17B4")))
            return;
        deletions.Apply(workspace, plan);
        await Save();
        Status(() => BatchScope(selection, ids.Count));
    });

    Task RestoreSelected() => LocalOperation(async () =>
    {
        var selection = BatchSelection.Capture(SelectedRows(), restore: true);
        if (selection.Eligible.Count == 0)
        { Status(() => L("BatchNothing")); return; }
        var changes = selection.Eligible.Select(r => r.With("_delete", "FALSE")).ToArray();
        if (!await Confirm(L("Text_A6926595CE"), BatchPreview(selection, "_delete", changes), L("Confirm")))
            return;
        deletions.Restore(Data, changes.Select(r => r.LocalId));
        await Save();
        Status(() => BatchScope(selection, changes.Length));
    });

    Task EditRules() => LocalOperation(async () =>
    {
        var selection = BatchSelection.Capture(SelectedRows());
        if (selection.Eligible.Count == 0)
        { Status(() => L("BatchNothing")); return; }
        var field = new ComboBox { ItemsSource = DisplayColumns().Where(f => f is not "_delete" and not "Fleet ID" and not "Fleet IDs").ToArray(), SelectedIndex = 0, MinWidth = 300 }
            .Localize(localization, "Header", () => L("Text_86F623E5CC"));
        string[] keys = ["Text_A30D395090", "Text_5B7341C2D6", "Text_795CBFF909", "Text_6FE8FC1819", "Text_FAC7E021A1", "Text_E79453E164", "Text_6797F1B017"];
        var kinds = Enum.GetValues<RuleKind>().Where(k => k != RuleKind.Retire || resource == ResourceKind.Routes).ToArray();
        var kind = new ComboBox { SelectedIndex = 0 }.Localize(localization, "Header", () => L("Text_ED31FBB483"))
            .Localize(localization, "ItemsSource", () => kinds.Select(k => L(keys[(int)k])).ToArray());
        kind.SelectionChanged += (_, _) => field.IsEnabled = kind.SelectedIndex >= 0 && kinds[kind.SelectedIndex] != RuleKind.Retire;
        var value = new TextBox().Localize(localization, "Header", () => L("Text_DD5ABC95B1"));
        var find = new TextBox().Localize(localization, "Header", () => L("Text_8FAB339859"));
        if (!await Confirm(L("Text_E306752B35"), Stack(BatchTargets(selection), field, kind, find, value), L("Text_13D61FEA9F")))
            return;
        var rule = new EditRule(kinds[kind.SelectedIndex], field.SelectedItem!.ToString()!, value.Text, find.Text);
        // UI mutation remains locked while the copies are processed off-thread.
        var preview = await Task.Run(() => BatchEditPreview.Rule(selection, rule));
        if (preview.Validation is { } invalid)
        { Status(() => L(invalid)); return; }
        if (preview.Changes.Count == 0)
        { Status(() => string.Join(" ", new[] { BatchScope(preview) }.Concat(preview.SkipReasons.Select(r => L(r))))); return; }
        if (!await Confirm(L("Text_AD86791376"), BatchPreview(selection, preview.Field, preview.Changes, preview.SkipReasons, preview.RuleSkips.Count), L("Text_2354C137CE")))
            return;
        preview.Apply(Data);
        await Save();
        Status(() => BatchScope(preview));
    });
}
