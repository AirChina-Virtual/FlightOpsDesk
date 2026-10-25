using System.Globalization;

namespace VamSys.Core;

public sealed class ChangePlanner : IChangePlanner
{
    public List<ChangeItem> PlanWorkspace(Workspace workspace)
    {
        var planned = ScanWorkspace(workspace);
        var items = new List<ChangeItem>(planned.Count);
        foreach (var change in planned)
            items.Add(change.ToItem());
        return items;
    }

    /// <summary>
    /// The same plan as <see cref="PlanWorkspace"/> without building the items: no row or field copies, only references to the
    /// workspace's rows and the names of the changed fields. Use it when the plan is only shown or compared.
    /// </summary>
    public List<PlannedChange> ScanWorkspace(Workspace workspace)
    {
        var items = new List<PlannedChange>();
        foreach (var (kind, data) in workspace.Resources)
            Scan(kind, data, items);
        // The lookups below are needed only when a row points at a local row, which is rare: build them on first use.
        Dictionary<string, PlannedChange>? creates = null;
        Dictionary<string, string>? known = null;
        var referenceFields = new string[(int)Enum.GetValues<ResourceKind>().Max() + 1][];
        foreach (var item in items)
        {
            item.RequiresAirlineParameter = workspace.Mode == RunMode.Online && item.Resource == ResourceKind.Fleets && item.Kind == ChangeKind.Create;
            var fieldsOfResource = referenceFields[(int)item.Resource] ??= Schemas.ReferenceFields(item.Resource);
            List<Guid>? dependencies = null;
            foreach (var field in fieldsOfResource)
            {
                var original = item.After.Get(field);
                if (!LocalReference.ContainsLocal(original))
                    continue;
                creates ??= items.Where(i => i.Kind == ChangeKind.Create).ToDictionary(i => LocalReference.Format(i.After.LocalId), StringComparer.OrdinalIgnoreCase);
                known ??= workspace.Resources.SelectMany(p => p.Value.Snapshot.Select(r => (Token: LocalReference.Format(r.LocalId), Key: Schemas.Key(p.Key, r)))).ToDictionary(p => p.Token, p => p.Key, StringComparer.OrdinalIgnoreCase);
                var tokens = original.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < tokens.Length; i++)
                {
                    if (creates.TryGetValue(tokens[i], out var dependency))
                        (dependencies ??= []).Add(dependency.Id);
                    else if (known.TryGetValue(tokens[i], out var key))
                        tokens[i] = key;
                }
                item.Resolve(field, string.Join(',', tokens));
            }
            if (dependencies != null)
                item.SetDependencies([.. dependencies.Distinct()]);
        }
        return items;
    }
    public List<ChangeItem> Plan(ResourceKind kind, ResourceData data)
    {
        var planned = new List<PlannedChange>();
        Scan(kind, data, planned);
        var result = new List<ChangeItem>(planned.Count);
        foreach (var change in planned)
            result.Add(change.ToItem());
        return result;
    }
    static void Scan(ResourceKind kind, ResourceData data, List<PlannedChange> result)
    {
        var baseline = new Dictionary<Guid, DataRow>(data.Snapshot.Count);
        foreach (var snapshot in data.Snapshot)
            baseline.Add(snapshot.LocalId, snapshot);
        var changed = new List<string>();
        foreach (var row in data.Draft)
        {
            baseline.TryGetValue(row.LocalId, out var before);
            changed.Clear();
            foreach (var field in row.FieldEntries())
                if (Changed(field, before))
                    changed.Add(field.Key);
            var delete = Schemas.Deleted(row);
            if (before != null && !delete && changed.Count == 0)
                continue;
            if (before is null && delete)
                continue;
            result.Add(new(kind, delete ? ChangeKind.Delete : before is null ? ChangeKind.Create : ChangeKind.Update, row, before, changed.Count == 0 ? [] : [.. changed]));
        }
    }
    static bool Changed(KeyValuePair<string, string> field, DataRow? baseline)
        => field.Key != "_delete" && (baseline is null || !baseline.FieldEntries().TryGetValue(field.Key, out var old) || old != field.Value);

    public static ChangeKind? GetChangeKind(DataRow row, DataRow? baseline)
    {
        if (Schemas.Deleted(row))
            return baseline is null ? null : ChangeKind.Delete;
        if (baseline is null)
            return ChangeKind.Create;
        foreach (var field in row.FieldEntries())
            if (Changed(field, baseline))
                return ChangeKind.Update;
        return null;
    }
}
public enum RuleKind { Set, Replace, AddTags, RemoveTags, ShiftMinutes, ShiftDays, Retire }
public record EditRule(RuleKind Kind, string Field, string Value, string Search = "");
public sealed record RuleSkip(Guid LocalId, string ReasonCode);
/// <summary>Rows (skipped rows come back unchanged), the per-row skips, or a validation message when the rule input itself is unusable.</summary>
public sealed record RuleOutcome(List<DataRow> Rows, List<RuleSkip> Skips, MessageDescriptor? Validation = null);
public static class RuleEngine
{
    public const int MaxShiftMinutes = 1439;
    public static List<DataRow> Preview(IEnumerable<DataRow> rows, EditRule rule)
    {
        var outcome = Evaluate(rows, rule);
        if (outcome.Validation is { } validation)
            throw MessageErrors.Attach(new ArgumentException(validation.ToString()), validation);
        return outcome.Rows;
    }
    public static RuleOutcome Evaluate(IEnumerable<DataRow> rows, EditRule rule)
        => rule.Kind == RuleKind.ShiftMinutes ? ShiftMinutes(rows, rule) : new(Apply(rows, rule), []);

    static RuleOutcome ShiftMinutes(IEnumerable<DataRow> rows, EditRule rule)
    {
        if (!int.TryParse(rule.Value.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var minutes) || Math.Abs(minutes) > MaxShiftMinutes)
            return new([], [], Messages.Define("BatchShiftInvalidMinutes", ("max", MaxShiftMinutes)));
        var result = new List<DataRow>();
        var skips = new List<RuleSkip>();
        foreach (var row in rows)
        {
            var old = row.Get(rule.Field);
            if (!TimeOnly.TryParseExact(old, ["HH:mm", "HH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            { result.Add(row); skips.Add(new(row.LocalId, "BatchShiftSkippedInvalidTime")); continue; }
            // Whole-day arithmetic: the date and the other time are never adjusted, so leaving 00:00-23:59 skips the row.
            var seconds = (long)time.ToTimeSpan().TotalSeconds + minutes * 60L;
            if (seconds is < 0 or >= 86400)
            { result.Add(row); skips.Add(new(row.LocalId, "BatchShiftSkippedCrossesMidnight")); continue; }
            // Seconds in API times are kept; the original HH:mm / HH:mm:ss shape is kept too.
            result.Add(row.With(rule.Field, TimeOnly.FromTimeSpan(TimeSpan.FromSeconds(seconds)).ToString(old.Length == 8 ? "HH:mm:ss" : "HH:mm", CultureInfo.InvariantCulture)));
        }
        return new(result, skips);
    }

    static List<DataRow> Apply(IEnumerable<DataRow> rows, EditRule rule)
    {
        return rows.Select(row =>
        {
            var old = row.Get(rule.Field);
            var value = rule.Kind switch
            {
                RuleKind.Set => rule.Value,
                RuleKind.Replace when rule.Search.Length > 0 => old.Replace(rule.Search, rule.Value, StringComparison.Ordinal),
                RuleKind.Replace => throw MessageErrors.Attach(new ArgumentException(Messages.Define("Text_6216D1C3A0")), Messages.Define("Text_6216D1C3A0")),
                RuleKind.AddTags => string.Join(',', old.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Concat(rule.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).Distinct()),
                RuleKind.RemoveTags => string.Join(',', old.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Except(rule.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))),
                RuleKind.ShiftDays => DateTime.ParseExact(old, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture).AddDays(int.Parse(rule.Value, CultureInfo.InvariantCulture)).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                RuleKind.Retire => DateTime.UtcNow.AddDays(-1).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                _ => throw MessageErrors.Attach(new ArgumentException(Messages.Define("Text_5CBDBA3063")), Messages.Define("Text_5CBDBA3063"))
            };
            return row.With(rule.Kind == RuleKind.Retire ? "End Date" : rule.Field, value);
        }).ToList();
    }
}
