
namespace VamSys.Core;

public sealed record DeleteProposal(BatchSelection Selection, DeletionPlan? Plan, IReadOnlyCollection<Guid> Ids, bool NothingToDo)
{
    // Rows that cannot be deleted because other records still use them.
    public IReadOnlyList<Issue> Issues => Plan?.Issues ?? [];
}
public sealed record RestoreProposal(BatchSelection Selection, IReadOnlyList<DataRow> Changes);
// Preview is null when the resource has no retirement or nothing was eligible.
public sealed record RetireProposal(BatchSelection Selection, BatchEditPreview? Preview);
public sealed record FleetAssignmentStart(BatchSelection Selection, string Field, IReadOnlyList<string> Initial, IReadOnlyList<FleetOption> Options);

// Edits applied to many selected records at once: delete and restore, rule edits, retirement, fleet assignment, adding a
// row, and the undo or redo of such steps. Each command first proposes what it would change (nothing is touched), and
// applying it changes memory and saves once.
public sealed class BatchEditService(WorkspaceSession session)
{
    readonly DeletionService deletions = new();
    readonly FleetAssignmentService fleets = new();

    ResourceData Data(ResourceKind kind) => session.Workspace.Resources[kind];

    public BatchSelection Capture(IEnumerable<DataRow> selected) => BatchSelection.Capture(selected);

    // --- Delete and restore ---
    public DeleteProposal ProposeDelete(ResourceKind kind, IEnumerable<DataRow> selected)
    {
        var data = Data(kind);
        var selection = BatchSelection.ForDeletion(selected, data.Snapshot);
        if (selection.Eligible.Count == 0)
            return new(selection, null, [], true);
        var plan = deletions.Preview(session.Workspace, kind, selection.Eligible.Select(r => r.LocalId));
        if (plan.Issues.Count > 0)
            return new(selection, plan, [], false);
        var ids = plan.RemoveDrafts.Concat(plan.MarkExisting).ToHashSet();
        return new(selection, plan, ids, ids.Count == 0);
    }
    public async Task ApplyDeleteAsync(DeleteProposal proposal)
    {
        deletions.Apply(session.Workspace, proposal.Plan!);
        await session.SaveAsync();
    }

    public RestoreProposal ProposeRestore(IEnumerable<DataRow> selected)
    {
        var selection = BatchSelection.Capture(selected, restore: true);
        return new(selection, selection.Eligible.Select(r => r.With("_delete", "FALSE")).ToArray());
    }
    public async Task ApplyRestoreAsync(ResourceKind kind, RestoreProposal proposal)
    {
        deletions.Restore(Data(kind), proposal.Changes.Select(r => r.LocalId));
        await session.SaveAsync();
    }

    // --- Rule edits and retirement ---
    // Retirement only exists for routes.
    public IReadOnlyList<RuleKind> RuleKinds(ResourceKind kind) => Enum.GetValues<RuleKind>().Where(k => k != RuleKind.Retire || kind == ResourceKind.Routes).ToArray();
    // The columns a rule may change: fleet references have their own command and the deletion marker its own commands.
    public IReadOnlyList<string> RuleFields(IEnumerable<string> columns) => columns.Where(f => f is not "_delete" and not "Fleet ID" and not "Fleet IDs").ToArray();
    // The copies are processed off the UI thread while the window stays locked.
    public Task<BatchEditPreview> PreviewRuleAsync(BatchSelection selection, EditRule rule) => Task.Run(() => BatchEditPreview.Rule(selection, rule));
    public async Task ApplyEditAsync(ResourceKind kind, BatchEditPreview preview)
    {
        preview.Apply(Data(kind));
        await session.SaveAsync();
    }

    public RetireProposal? ProposeRetire(ResourceKind kind, IEnumerable<DataRow> selected)
    {
        if (kind != ResourceKind.Routes)
            return null;
        var selection = BatchSelection.Capture(selected);
        if (selection.Eligible.Count == 0)
            return new(selection, null);
        return new(selection, BatchEditPreview.Rule(selection, new(RuleKind.Retire, "End Date", "")));
    }

    // --- Fleet assignment ---
    public IReadOnlyList<FleetOption> FleetOptions(Workspace workspace) => fleets.Options(workspace);
    // Null when none of the rows can be assigned (all deleted or nothing selected).
    public FleetAssignmentStart? BeginFleetAssignment(ResourceKind kind, IEnumerable<DataRow> rows)
    {
        var selection = BatchSelection.Capture(rows);
        if (selection.Eligible.Count == 0)
            return null;
        var field = FleetAssignmentService.Field(kind);
        var initial = selection.Eligible.Count == 1 ? FleetAssignmentService.Tokens(selection.Eligible[0].Get(field)) : new List<string>();
        var options = fleets.Options(session.Workspace, selection.Eligible.SelectMany(r => FleetAssignmentService.Tokens(r.Get(field))));
        return new(selection, field, initial, options);
    }
    public FleetAssignmentPreview PreviewFleets(ResourceKind kind, IEnumerable<DataRow> rows, IEnumerable<string> tokens, FleetAssignmentMode mode)
        => fleets.Preview(session.Workspace, kind, rows, tokens, mode);
    public async Task ApplyFleetsAsync(ResourceKind kind, FleetAssignmentPreview preview)
    {
        fleets.Apply(Data(kind), preview);
        await session.SaveAsync();
    }

    // --- One row, and history ---
    public async Task AddRowAsync(ResourceKind kind)
    {
        var data = Data(kind);
        var builder = new DataRow.Builder();
        foreach (var f in Schemas.All[kind].Columns)
            builder[f] = f == "_delete" ? "FALSE" : "";
        var row = builder.ToRow();
        data.Edit([row.LocalId], () => data.Draft.Add(row), structural: true);
        data.Columns = data.Columns.Concat(row.Fields.Keys).Distinct().ToList();
        await session.SaveAsync();
    }

    // Undo or redo one step. A step that only replaced rows saves those rows and the moved history step; adding, removing
    // or reordering rows takes a full save. Nothing to move changes nothing and saves nothing.
    public HistoryMove MoveHistory(ResourceKind kind, bool undo)
    {
        var data = Data(kind);
        return undo ? data.UndoEdit() : data.RedoEdit();
    }
    public async Task SaveMoveAsync(ResourceKind kind, HistoryMove move)
    {
        if (!move.Moved)
            return;
        if (move.ReplacedRows is { } rows)
            await session.SaveReplacedRowsAsync(kind, rows.ToArray());
        else
            await session.SaveAsync();
    }
}
