namespace VamSys.Core;

public static class SnapshotMerger
{
    public static void Merge(ResourceKind kind, ResourceData data, IReadOnlyList<DataRow> incoming)
    {
        string Key(DataRow row) => row.Identity?.RemoteId ?? Schemas.Key(kind, row);
        var remote = incoming.ToDictionary(Key);
        var aliases = incoming.SelectMany(r=>Schemas.MatchKeys(kind,r).Where(k=>k!="").Select(k=>(Alias:k,Remote:Key(r)))).Distinct().ToDictionary(p=>p.Alias,p=>p.Remote);
        var old = data.Snapshot.ToDictionary(r => r.LocalId);
        var merged = new List<DataRow>();
        var conflicts = new List<MergeConflict>();
        foreach (var local in data.Draft)
        {
            old.TryGetValue(local.LocalId, out var baseline);
            var key = Key(baseline ?? local);
            if ((baseline ?? local).Identity == null && aliases.TryGetValue(Schemas.Key(kind,baseline ?? local),out var matched)) key = matched;
            if (key.Length == 0 || !remote.Remove(key, out var fresh))
            {
                if (baseline == null) { merged.Add(local.Copy()); continue; }
                if (Schemas.Deleted(local)) continue;
                if (local.Fields.Any(f => f.Value != baseline.Get(f.Key)))
                { merged.Add(local.Copy()); conflicts.Add(new(local.LocalId, "*", "exists", "edited", null)); }
                continue;
            }
            fresh.LocalId = local.LocalId;
            var result = fresh.Copy();
            foreach (var field in local.Fields.Keys.Union(baseline?.Fields.Keys.AsEnumerable() ?? []))
            {
                var original = baseline?.Get(field);
                var mine = local.Get(field); var theirs = fresh.Get(field);
                if(kind==ResourceKind.Airports && field=="ICAO/IATA" && Schemas.MatchKeys(kind,fresh).Contains(mine.Trim().ToUpperInvariant())) continue;
                // Imported candidates with identifiers require explicit review against API state.
                if (baseline == null || mine != original)
                {
                    result.Fields[field] = mine;
                    if (mine != theirs && (baseline == null || theirs != original))
                        conflicts.Add(new(local.LocalId, field, original, mine, theirs));
                }
                else if (!fresh.Fields.ContainsKey(field)) result.Fields[field] = mine; // CSV-only columns
            }
            if (Schemas.Deleted(local) && baseline != null && fresh.Fields.Any(f => f.Value != baseline.Get(f.Key)))
                conflicts.Add(new(local.LocalId, "_delete", "FALSE", "TRUE", "FALSE"));
            merged.Add(result);
        }
        merged.AddRange(remote.Values.Select(r => r.Copy()));
        // Retain unresolved conflicts over repeated refreshes; refreshing is not conflict resolution.
        conflicts.AddRange(data.Conflicts.Where(c => merged.Any(r => r.LocalId == c.RowId) && !conflicts.Any(n => n.RowId == c.RowId && n.Field == c.Field)));
        data.Snapshot = incoming.Select(r => r.Copy()).ToList(); data.Draft = merged;
        data.Conflicts = conflicts; data.SnapshotAt = DateTimeOffset.UtcNow;
        data.Columns = data.Columns.Concat(merged.SelectMany(r => r.Fields.Keys)).Distinct().ToList();
        data.Undo.Clear(); data.Redo.Clear();
    }
}
