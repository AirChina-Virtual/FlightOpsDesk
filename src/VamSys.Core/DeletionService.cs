namespace VamSys.Core;

public sealed record DeletionPlan(ResourceKind Resource, List<Guid> RemoveDrafts, List<Guid> MarkExisting, List<Issue> Issues);

public sealed class DeletionService
{
    public DeletionPlan Preview(Workspace workspace, ResourceKind kind, IEnumerable<Guid> selected)
    {
        var ids = selected.ToHashSet(); var data = workspace.Resources[kind];
        var baseline = data.Snapshot.Select(r => r.LocalId).ToHashSet();
        var rows = data.Draft.Where(r => ids.Contains(r.LocalId)).ToList();
        return new(kind, rows.Where(r => !baseline.Contains(r.LocalId)).Select(r => r.LocalId).ToList(),
            rows.Where(r => baseline.Contains(r.LocalId) && !Schemas.Deleted(r)).Select(r => r.LocalId).ToList(),
            CheckReferences(workspace, kind, ids));
    }

    public List<Issue> CheckReferences(Workspace workspace, ResourceKind kind, ISet<Guid> targets)
    {
        var aliases = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in workspace.Resources[kind].Draft.Where(r => targets.Contains(r.LocalId)))
        {
            aliases["local:" + row.LocalId] = row.LocalId;
            var key = Schemas.Key(kind, row); if (key != "") aliases[key] = row.LocalId;
            foreach(var alias in Schemas.MatchKeys(kind,row).Where(k=>k!="")) aliases[alias]=row.LocalId;
            if(row.Identity!=null) { aliases[row.Identity.RemoteId]=row.LocalId; aliases["unresolved:"+row.Identity.RemoteId]=row.LocalId; }
            if (kind == ResourceKind.Airports)
                foreach (var f in new[] { "Info-Airport ID", "Info-Airport ICAO", "Info-Airport IATA" })
                    if (row.Get(f) != "") aliases[row.Get(f)] = row.LocalId;
        }
        var issues = new List<Issue>();
        foreach (var (sourceKind, data) in workspace.Resources)
        {
            var fields = kind switch
            {
                ResourceKind.Fleets when sourceKind == ResourceKind.Aircraft => new[] { "Fleet ID" },
                ResourceKind.Fleets when sourceKind == ResourceKind.Routes => ["Fleet IDs"],
                ResourceKind.Airports when sourceKind is ResourceKind.Routes or ResourceKind.Routings => ["Departure Airport (ICAO/IATA)", "Arrival Airport (ICAO/IATA)"],
                _ => Array.Empty<string>()
            };
            foreach (var source in data.Draft.Where(r => !Schemas.Deleted(r) && !(sourceKind == kind && targets.Contains(r.LocalId))))
                foreach (var field in fields)
                    foreach (var token in FleetAssignmentService.Tokens(source.Get(field)))
                        if (aliases.TryGetValue(token, out var targetId))
                            issues.Add(new(targetId, "_delete", Messages.Define("Text_CCEF575C0D", ("arg0", Schemas.All[sourceKind].Name), ("arg1", Schemas.Key(sourceKind, source)), ("arg2", source.Get("Name")), ("arg3", source.Get("Flight Number")), ("arg4", source.LocalId.ToString()[..8]), ("arg5", field))));
        }
        return issues;
    }
    public void Apply(Workspace workspace, DeletionPlan plan)
    {
        // Revalidate against current data, so stale previews cannot bypass a new dependency.
        var current = Preview(workspace, plan.Resource, plan.RemoveDrafts.Concat(plan.MarkExisting));
        if (current.Issues.Count > 0) throw MessageErrors.Attach(new InvalidOperationException(Messages.Define("Text_0829856E83")), Messages.Define("Text_0829856E83"));
        if (current.RemoveDrafts.Count + current.MarkExisting.Count == 0) return;
        var data = workspace.Resources[plan.Resource]; data.Checkpoint();
        var remove = current.RemoveDrafts.ToHashSet(); var mark = current.MarkExisting.ToHashSet();
        data.Draft.RemoveAll(r => remove.Contains(r.LocalId));
        foreach (var row in data.Draft.Where(r => mark.Contains(r.LocalId))) row.Fields["_delete"] = "TRUE";
    }
    public void Restore(ResourceData data, IEnumerable<Guid> selected)
    {
        var ids = selected.ToHashSet(); var rows = data.Draft.Where(r => ids.Contains(r.LocalId) && Schemas.Deleted(r)).ToList();
        if (rows.Count == 0) return;
        data.Checkpoint(); foreach (var row in rows) row.Fields["_delete"] = "FALSE";
    }
}
