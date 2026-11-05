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
        ApplyShellState();
        RefreshDataAvailability();
        var statusBefore = statusText;
        try
        {
            await session.FlushAutosaveAsync();
            await action();
        }
        finally
        {
            // A review worker started inside this operation still reads the workspace: the shell stays locked until it is done.
            scope.Dispose();
            ApplyShellState();
            // The table refresh this render starts must not replace what the operation just reported.
            keepStatusThroughRefresh = !ReferenceEquals(statusText, statusBefore);
            Render();
            keepStatusThroughRefresh = false;
        }
    }

    string BatchScope(BatchSelection selection, int changed, int ruleSkipped = 0) => L("BatchCounts", ("selected", selection.Selected.Count),
        ("skipped", selection.Skipped + ruleSkipped), ("unchanged", selection.Eligible.Count - changed - ruleSkipped), ("changed", changed));
    string BatchScope(BatchEditPreview preview) => BatchScope(preview.Selection, preview.Changes.Count, preview.RuleSkips.Count);
    string BatchTargetsText(BatchSelection selection, bool single = false) => L(single ? "BatchSingle" : "BatchTargets",
        ("selected", selection.Selected.Count), ("skipped", selection.Skipped), ("eligible", selection.Eligible.Count));
    // The rows a batch change affects, each with its value before and after, under the selection counts.
    ListDialogViewModel BatchPreview(string title, string primary, BatchSelection selection, string field, IReadOnlyList<DataRow> changes, IReadOnlyList<MessageDescriptor>? skipReasons = null, int ruleSkipped = 0)
    {
        var before = selection.Eligible.ToDictionary(r => r.LocalId);
        return new ListDialogViewModel
        {
            Title = title,
            PrimaryLabel = primary,
            Height = DialogHeight.BatchPreview,
            Selectable = false,
            Header = skipReasons is { Count: > 0 }
                ? [BatchScope(selection, changes.Count, ruleSkipped), .. skipReasons.Select(reason => L(reason))]
                : [BatchScope(selection, changes.Count)],
            Items = changes.Select(r => $"{Schemas.Key(resource, r)} {r.Get("Flight Number")}{r.Get("Registration")}\n" +
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
            await ConfirmList(L("Text_FD6D0919A9"), proposal.Issues.Select(i => L(i.Description)).ToArray(), L("Text_3FD47EDCE4"));
            return;
        }
        var plan = proposal.Plan!;
        var ids = proposal.Ids;
        if (!await ConfirmList(L("Text_948725D00E"), selection.Eligible.Where(r => ids.Contains(r.LocalId)).Select(r => $"{Schemas.Key(resource, r)} · {r.Get("Name")}{r.Get("Flight Number")}").ToArray(), L("Text_A3EA3C17B4"),
            [BatchScope(selection, ids.Count), L("Text_0360D81E1F", ("arg0", plan.RemoveDrafts.Count), ("arg1", plan.MarkExisting.Count)), L("Text_0E38068B45")], DialogHeight.BatchPreview, selectable: false))
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
        if (!await Confirm(BatchPreview(L("Text_A6926595CE"), L("Confirm"), selection, "_delete", proposal.Changes)))
            return;
        await batch.ApplyRestoreAsync(resource, proposal);
        Status(() => BatchScope(selection, proposal.Changes.Count));
    });

    Task EditRules() => LocalOperation(async () =>
    {
        var selection = batch.Capture(SelectedRows());
        if (selection.Eligible.Count == 0)
        { Status(() => L("BatchNothing")); return; }
        var form = new BatchRuleDialogViewModel(BatchTargetsText(selection), batch.RuleFields(DisplayColumns()), batch.RuleKinds(resource), localization)
        { Title = L("Text_E306752B35"), PrimaryLabel = L("Text_13D61FEA9F") };
        if (!await Confirm(form) || form.Rule is not { } rule)
            return;
        // UI mutation remains locked while the copies are processed off-thread.
        var preview = await batch.PreviewRuleAsync(selection, rule);
        if (preview.Validation is { } invalid)
        { Status(() => L(invalid)); return; }
        if (preview.Changes.Count == 0)
        { Status(() => string.Join(" ", new[] { BatchScope(preview) }.Concat(preview.SkipReasons.Select(r => L(r))))); return; }
        if (!await Confirm(BatchPreview(L("Text_AD86791376"), L("Text_2354C137CE"), selection, preview.Field, preview.Changes, preview.SkipReasons, preview.RuleSkips.Count)))
            return;
        await batch.ApplyEditAsync(resource, preview);
        Status(() => BatchScope(preview));
    });
}
