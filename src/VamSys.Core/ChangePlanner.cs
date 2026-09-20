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
            foreach (var field in Schemas.ReferenceFields(item.Resource))
            {
                var original = item.After.Get(field);
                if (!original.Contains("local:", StringComparison.OrdinalIgnoreCase)) continue;
                var tokens = original.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < tokens.Length; i++)
                {
                    if (creates.TryGetValue(tokens[i], out var dependency)) item.Dependencies.Add(dependency.Id);
                    else if (known.TryGetValue(tokens[i], out var key)) tokens[i] = key;
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
        var baseline = data.Snapshot.ToDictionary(r => r.LocalId); var result = new List<ChangeItem>();
        foreach (var row in data.Draft)
        {
            baseline.TryGetValue(row.LocalId, out var before);
            var fields = row.Fields.Where(p => p.Key != "_delete" && (before is null || !before.Fields.TryGetValue(p.Key, out var old) || old != p.Value))
                .ToDictionary(p => p.Key, p => new FieldChange(p.Value == "" ? FieldIntent.Clear : FieldIntent.Set, p.Value));
            var delete = Schemas.Deleted(row);
            if (before != null && !delete && fields.Count == 0) continue;
            if (before is null && delete) continue;
            result.Add(new() { Resource = kind, Kind = delete ? ChangeKind.Delete : before is null ? ChangeKind.Create : ChangeKind.Update, Before = before?.Copy(), After = row.Copy(), Fields = fields });
        }
        return result;
    }
}
public enum RuleKind { Set, Replace, AddTags, RemoveTags, ShiftMinutes, ShiftDays, Retire }
public record EditRule(RuleKind Kind, string Field, string Value, string Search = "");
public static class RuleEngine
{
    public static List<DataRow> Preview(IEnumerable<DataRow> rows, EditRule rule)
    {
        return rows.Select(row =>
        {
            var copy = row.Copy(); var old = row.Get(rule.Field);
            var value = rule.Kind switch
            {
                RuleKind.Set => rule.Value,
                RuleKind.Replace when rule.Search.Length > 0 => old.Replace(rule.Search, rule.Value, StringComparison.Ordinal),
                RuleKind.Replace => throw MessageErrors.Attach(new ArgumentException(Messages.Define("Text_6216D1C3A0")), Messages.Define("Text_6216D1C3A0")),
                RuleKind.AddTags => string.Join(',', old.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Concat(rule.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).Distinct()),
                RuleKind.RemoveTags => string.Join(',', old.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Except(rule.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))),
                RuleKind.ShiftMinutes => TimeOnly.ParseExact(old, "HH:mm", CultureInfo.InvariantCulture).AddMinutes(int.Parse(rule.Value, CultureInfo.InvariantCulture)).ToString("HH:mm", CultureInfo.InvariantCulture),
                RuleKind.ShiftDays => DateTime.ParseExact(old, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture).AddDays(int.Parse(rule.Value, CultureInfo.InvariantCulture)).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                RuleKind.Retire => DateTime.UtcNow.AddDays(-1).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                _ => throw MessageErrors.Attach(new ArgumentException(Messages.Define("Text_5CBDBA3063")), Messages.Define("Text_5CBDBA3063"))
            };
            copy.Fields[rule.Kind == RuleKind.Retire ? "End Date" : rule.Field] = value;
            return copy;
        }).ToList();
    }
}
