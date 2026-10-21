using System.Globalization;
using VamSys.Core;

namespace VamSys.App;

public sealed record BatchSelection(IReadOnlyList<DataRow> Selected, IReadOnlyList<DataRow> Eligible)
{
    public int Skipped => Selected.Count - Eligible.Count;
    public static BatchSelection Capture(IEnumerable<DataRow> selected, bool restore = false)
    {
        var rows = selected.DistinctBy(r => r.LocalId).ToArray();
        return new(rows, rows.Where(r => Schemas.Deleted(r) == restore).ToArray());
    }
    public static BatchSelection ForDeletion(IEnumerable<DataRow> selected, IEnumerable<DataRow> snapshot)
    {
        var rows = selected.DistinctBy(r => r.LocalId).ToArray();
        var existing = snapshot.Select(r => r.LocalId).ToHashSet();
        // New drafts can be removed even when imported with a deletion marker.
        return new(rows, rows.Where(r => !Schemas.Deleted(r) || !existing.Contains(r.LocalId)).ToArray());
    }
}

public sealed record BatchEditPreview(BatchSelection Selection, string Field, IReadOnlyList<DataRow> Changes, IReadOnlyList<RuleSkip>? RuleSkipsOrNull = null, MessageDescriptor? Validation = null)
{
    public IReadOnlyList<RuleSkip> RuleSkips => RuleSkipsOrNull ?? [];
    /// <summary>One message per skip reason with its row count, in first-seen order.</summary>
    public IReadOnlyList<MessageDescriptor> SkipReasons => RuleSkips.GroupBy(s => s.ReasonCode).Select(g => Messages.Define(g.Key, ("count", g.Count()))).ToArray();
    public int Unchanged => Selection.Eligible.Count - Changes.Count - RuleSkips.Count;
    /// <summary>Rows dropped before the rule (not eligible) plus rows the rule itself could not process.</summary>
    public int TotalSkipped => Selection.Skipped + RuleSkips.Count;
    public static BatchEditPreview Rule(BatchSelection selection, EditRule rule)
    {
        // Capture the date once so preview and apply use the identical value for every row.
        if (rule.Kind == RuleKind.Retire)
            rule = new(RuleKind.Set, "End Date", DateTime.UtcNow.AddDays(-1).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        var before = selection.Eligible.ToDictionary(r => r.LocalId);
        var outcome = RuleEngine.Evaluate(selection.Eligible, rule);
        var changes = outcome.Rows.Where(r => r.Get(rule.Field) != before[r.LocalId].Get(rule.Field)).ToArray();
        return new(selection, rule.Field, changes, outcome.Skips, outcome.Validation);
    }
    public void Apply(ResourceData data)
    {
        if (Changes.Count == 0)
            return;
        data.Edit(Changes.Select(r => r.LocalId), () => data.ReplaceDraftRows(Changes.Select(r => r.Copy())));
        if (!data.Columns.Contains(Field))
            data.Columns.Add(Field);
    }
}
