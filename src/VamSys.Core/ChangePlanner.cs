using System.Globalization;

namespace VamSys.Core;

public sealed class ChangePlanner : IChangePlanner
{
    public List<ChangeItem> PlanWorkspace(Workspace workspace)
    {
        var items = workspace.Resources.SelectMany(p => Plan(p.Key, p.Value)).ToList();
        var creates = items.Where(i => i.Kind == ChangeKind.Create).ToDictionary(i => "local:" + i.After.LocalId, StringComparer.OrdinalIgnoreCase);
        var known = workspace.Resources.SelectMany(p => p.Value.Snapshot.Select(r => (Token: "local:" + r.LocalId, Key: Schemas.Key(p.Key, r)))).ToDictionary(p => p.Token, p => p.Key, StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            item.RequiresAirlineParameter = workspace.Mode == RunMode.Online && item.Resource == ResourceKind.Fleets && item.Kind == ChangeKind.Create;
            foreach (var field in Schemas.ReferenceFields(item.Resource))
            {
                var original = item.After.Get(field);
                if (!original.Contains("local:", StringComparison.OrdinalIgnoreCase))
                    continue;
                var tokens = original.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < tokens.Length; i++)
                {
                    if (creates.TryGetValue(tokens[i], out var dependency))
                        item.Dependencies.Add(dependency.Id);
                    else if (known.TryGetValue(tokens[i], out var key))
                        tokens[i] = key;
                }
                item.After.Fields[field] = string.Join(',', tokens);
                item.Fields[field] = new(FieldIntent.Set, item.After.Fields[field]);
            }
            item.Dependencies = item.Dependencies.Distinct().ToList();
        }
        return items;
    }
    public List<ChangeItem> Plan(ResourceKind kind, ResourceData data)
    {
        var baseline = data.Snapshot.ToDictionary(r => r.LocalId);
        var result = new List<ChangeItem>();
        foreach (var row in data.Draft)
        {
            baseline.TryGetValue(row.LocalId, out var before);
            Dictionary<string, FieldChange>? fields = null;
            foreach (var field in row.Fields)
                if (Changed(field, before))
                    (fields ??= new(StringComparer.Ordinal)).Add(field.Key,
                        new(field.Value == "" ? FieldIntent.Clear : FieldIntent.Set, field.Value));
            var delete = Schemas.Deleted(row);
            if (before != null && !delete && fields is null)
                continue;
            if (before is null && delete)
                continue;
            result.Add(new(row.Copy(), fields ?? new(StringComparer.Ordinal)) { Resource = kind, Kind = delete ? ChangeKind.Delete : before is null ? ChangeKind.Create : ChangeKind.Update, Before = before?.Copy() });
        }
        return result;
    }
    static bool Changed(KeyValuePair<string, string> field, DataRow? baseline)
        => field.Key != "_delete" && (baseline is null || !baseline.Fields.TryGetValue(field.Key, out var old) || old != field.Value);

    public static ChangeKind? GetChangeKind(DataRow row, DataRow? baseline)
    {
        if (Schemas.Deleted(row))
            return baseline is null ? null : ChangeKind.Delete;
        if (baseline is null)
            return ChangeKind.Create;
        foreach (var field in row.Fields)
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
            var copy = row.Copy();
            result.Add(copy);
            var old = row.Get(rule.Field);
            if (!TimeOnly.TryParseExact(old, ["HH:mm", "HH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            { skips.Add(new(row.LocalId, "BatchShiftSkippedInvalidTime")); continue; }
            // Whole-day arithmetic: the date and the other time are never adjusted, so leaving 00:00-23:59 skips the row.
            var seconds = (long)time.ToTimeSpan().TotalSeconds + minutes * 60L;
            if (seconds is < 0 or >= 86400)
            { skips.Add(new(row.LocalId, "BatchShiftSkippedCrossesMidnight")); continue; }
            // Seconds in API times are kept; the original HH:mm / HH:mm:ss shape is kept too.
            copy.Fields[rule.Field] = TimeOnly.FromTimeSpan(TimeSpan.FromSeconds(seconds)).ToString(old.Length == 8 ? "HH:mm:ss" : "HH:mm", CultureInfo.InvariantCulture);
        }
        return new(result, skips);
    }

    static List<DataRow> Apply(IEnumerable<DataRow> rows, EditRule rule)
    {
        return rows.Select(row =>
        {
            var copy = row.Copy();
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
            copy.Fields[rule.Kind == RuleKind.Retire ? "End Date" : rule.Field] = value;
            return copy;
        }).ToList();
    }
}
