namespace VamSys.Core;

public static class SnapshotMerger
{
    public static void Merge(ResourceKind kind, ResourceData data, IReadOnlyList<DataRow> incoming)
    {
        string Key(DataRow row) => row.Identity?.RemoteId ?? Schemas.Key(kind, row);
        var remote = incoming.ToDictionary(Key);
        // Several remote rows may share an alias (for example two airports with one IATA code). Such an
        // ambiguous alias matches no local row without identity; the row stays local, like a new one.
        var aliases = new Dictionary<string, HashSet<string>>();
        foreach (var r in incoming)
            foreach (var alias in Schemas.MatchKeys(kind, r).Where(k => k != ""))
            { if (!aliases.TryGetValue(alias, out var keys)) aliases[alias] = keys = []; keys.Add(Key(r)); }
        var old = data.Snapshot.ToDictionary(r => r.LocalId);
        var merged = new List<DataRow>();
        var conflicts = new List<MergeConflict>();
        var identified = new Dictionary<DataRow, DataRow>(ReferenceEqualityComparer.Instance);
        foreach (var local in data.Draft)
        {
            old.TryGetValue(local.LocalId, out var baseline);
            var key = Key(baseline ?? local);
            if ((baseline ?? local).Identity == null && aliases.TryGetValue(Schemas.Key(kind, baseline ?? local), out var matched) && matched.Count == 1)
                key = matched.Single();
            if (key.Length == 0 || !remote.Remove(key, out var fresh))
            {
                if (baseline == null)
                { merged.Add(local); continue; }
                if (Schemas.Deleted(local))
                    continue;
                if (local.Fields.Any(f => f.Value != baseline.Get(f.Key)))
                { merged.Add(local); conflicts.Add(new(local.LocalId, "*", "exists", "edited", null)); }
                continue;
            }
            // The remote row takes over the local row's id, in the snapshot as well as in the merged draft.
            identified[fresh] = fresh.WithLocalId(local.LocalId);
            var result = fresh.ToBuilder();
            result.LocalId = local.LocalId;
            foreach (var field in local.Fields.Keys.Union(baseline?.Fields.Keys.AsEnumerable() ?? []))
            {
                var original = baseline?.Get(field);
                var mine = local.Get(field);
                var theirs = fresh.Get(field);
                if (kind == ResourceKind.Airports && field == "ICAO/IATA" && Schemas.MatchKeys(kind, fresh).Contains(mine.Trim().ToUpperInvariant()))
                    continue;
                // Imported candidates with identifiers require explicit review against API state.
                if (baseline == null || mine != original)
                {
                    result[field] = mine;
                    if (mine != theirs && (baseline == null || theirs != original))
                        conflicts.Add(new(local.LocalId, field, original, mine, theirs));
                }
                else if (!fresh.Fields.ContainsKey(field))
                    result[field] = mine; // CSV-only columns
            }
            if (Schemas.Deleted(local) && baseline != null && fresh.Fields.Any(f => f.Value != baseline.Get(f.Key)))
                conflicts.Add(new(local.LocalId, "_delete", "FALSE", "TRUE", "FALSE"));
            merged.Add(result.ToRow());
        }
        merged.AddRange(remote.Values);
        // Retain unresolved conflicts over repeated refreshes; refreshing is not conflict resolution.
        var mergedIds = merged.Select(r => r.LocalId).ToHashSet();
        var recorded = conflicts.Select(c => (c.RowId, c.Field)).ToHashSet();
        foreach (var c in data.Conflicts)
            if (mergedIds.Contains(c.RowId) && recorded.Add((c.RowId, c.Field)))
                conflicts.Add(c);
        data.Snapshot = [.. incoming.Select(r => identified.GetValueOrDefault(r) ?? r)];
        data.Draft = merged;
        data.Conflicts = conflicts;
        data.SnapshotAt = DateTimeOffset.UtcNow;
        data.Columns = data.Columns.Concat(merged.SelectMany(r => r.Fields.Keys)).Distinct().ToList();
        data.ClearHistory();
    }
}
