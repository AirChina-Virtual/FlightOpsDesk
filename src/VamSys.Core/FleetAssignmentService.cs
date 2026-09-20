namespace VamSys.Core;

public enum FleetAssignmentMode { Replace, Add, Remove }
public sealed record FleetOption(string Token, MessageDescriptor Name, string TypeCode, MessageDescriptor Identity, bool Available, MessageDescriptor? Problem)
{
    public string Label => $"{Name} · {TypeCode} · {Identity}" + (Problem is null ? "" : $" · {Problem}");
    public string Describe(ILocalizationService localizer) => $"{localizer.Format(Name)} · {TypeCode} · {localizer.Format(Identity)}" + (Problem is null ? "" : " · " + localizer.Format(Problem));
}
public sealed record FleetAssignmentPreview(string Field, List<DataRow> Rows, List<Issue> Issues);

/// <summary>Computes final local association sets, never HTTP array patch operations.</summary>
public sealed class FleetAssignmentService
{
    public static string Field(ResourceKind kind) => kind switch
    {
        ResourceKind.Aircraft => "Fleet ID",
        ResourceKind.Routes => "Fleet IDs",
        _ => throw MessageErrors.Attach(new ArgumentException(Messages.Define("Text_77F9BBEB62")), Messages.Define("Text_77F9BBEB62"))
    };
    public static List<string> Tokens(string value) => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public IReadOnlyList<FleetOption> Options(Workspace workspace, IEnumerable<string>? existing = null)
    {
        var rows = workspace.Resources[ResourceKind.Fleets].Draft;
        var duplicateIds = rows.Where(r => r.Get("ID") != "").GroupBy(r => r.Get("ID").Trim(), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var options = rows.Select(r =>
        {
            var id = r.Get("ID").Trim();
            var problem = Schemas.Deleted(r) ? Messages.Define("Text_EE96DC1EBE") : duplicateIds.Contains(id) ? Messages.Define("Text_95BA634DEA") : null;
            return new FleetOption(id.Length > 0 ? id : "local:" + r.LocalId, r.Get("Name"), r.Get("Type Code"), id.Length > 0 ? id : Messages.Define("Text_0772B8422B", ("arg0", r.LocalId.ToString()[..8])), problem is null, problem);
        }).GroupBy(o => o.Token, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).OrderBy(o => o.Name.ToString(), StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var token in (existing ?? []).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (options.Any(o => o.Token.Equals(token, StringComparison.OrdinalIgnoreCase))) continue;
            // Legacy local aliases for already-identified rows remain representable and resolvable.
            var alias = rows.FirstOrDefault(r => ("local:" + r.LocalId).Equals(token, StringComparison.OrdinalIgnoreCase));
            options.Add(alias is null
                ? new(token, Messages.Define("Text_63524EC09F"), "?", token, false, Messages.Define("Text_F6036A3E65"))
                : new(token, alias.Get("Name"), alias.Get("Type Code"), Messages.Define("Text_18371681C3"), !Schemas.Deleted(alias), Schemas.Deleted(alias) ? Messages.Define("Text_EE96DC1EBE") : null));
        }
        return options;
    }

    public FleetAssignmentPreview Preview(Workspace workspace, ResourceKind kind, IEnumerable<DataRow> rows, IEnumerable<string> selected, FleetAssignmentMode mode)
    {
        var field = Field(kind); var chosen = selected.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var source = rows.ToList(); var result = new List<DataRow>(); var issues = new List<Issue>();
        if (kind == ResourceKind.Aircraft && mode != FleetAssignmentMode.Replace) throw MessageErrors.Attach(new ArgumentException(Messages.Define("Text_1C6416508F")), Messages.Define("Text_1C6416508F"));
        var catalog = Options(workspace, source.SelectMany(r => Tokens(r.Get(field))).Concat(chosen)).ToDictionary(o => o.Token, StringComparer.OrdinalIgnoreCase);
        foreach (var row in source)
        {
            if (Schemas.Deleted(row)) { issues.Add(new(row.LocalId, field, Messages.Define("Text_92C7B26624"))); continue; }
            var old = Tokens(row.Get(field));
            var target = mode switch
            {
                FleetAssignmentMode.Replace => chosen.ToList(),
                FleetAssignmentMode.Add => old.Concat(chosen).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                FleetAssignmentMode.Remove => old.Except(chosen, StringComparer.OrdinalIgnoreCase).ToList(),
                _ => throw new ArgumentOutOfRangeException(nameof(mode))
            };
            if (kind == ResourceKind.Aircraft && target.Count != 1) issues.Add(new(row.LocalId, field, Messages.Define("Text_0CEB89655F")));
            if (kind == ResourceKind.Routes && row.Get("Type") != "jumpseat" && target.Count == 0) issues.Add(new(row.LocalId, field, Messages.Define("Text_2335DBC57B")));
            foreach (var token in target.Where(t => !old.Contains(t, StringComparer.OrdinalIgnoreCase)))
                if (!catalog[token].Available) issues.Add(new(row.LocalId, field, Messages.Define("Text_F7B2F3BC0C", ("arg0", token), ("arg1", catalog[token].Problem))));
            // Preserve exact strings on set-equivalent no-ops, including unresolved imported references.
            if (old.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(target)) continue;
            var copy = row.Copy(); copy.Fields[field] = string.Join(',', target); result.Add(copy);
        }
        return new(field, result, issues);
    }

    public void Apply(ResourceData data, FleetAssignmentPreview preview)
    {
        if (preview.Issues.Count > 0) throw MessageErrors.Attach(new InvalidOperationException(Messages.Define("Text_919362E2F8")), Messages.Define("Text_919362E2F8"));
        if (preview.Rows.Count == 0) return;
        data.Checkpoint(); var replacements = preview.Rows.ToDictionary(r => r.LocalId);
        for (int i = 0; i < data.Draft.Count; i++) if (replacements.TryGetValue(data.Draft[i].LocalId, out var replacement)) data.Draft[i] = replacement.Copy();
        if (!data.Columns.Contains(preview.Field)) data.Columns.Add(preview.Field);
    }
}
