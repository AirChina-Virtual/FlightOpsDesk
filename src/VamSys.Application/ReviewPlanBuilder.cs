using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VamSys.Core;

public enum ReviewDisposition { Ready, Deferred, Blocked, AwaitingConnection }
/// <summary>A reviewed change. <see cref="Planned"/> refers to the workspace's rows and the changed fields only;
/// <see cref="Change"/> builds the full item (once) for code that needs one, such as the submission.</summary>
public sealed record ReviewEntry(PlannedChange Planned, ReviewDisposition Disposition, MessageDescriptor? Reason)
{
    public ChangeItem Change => Planned.ToItem();
}

// Blockers stop the whole submission; Issues are what the page lists for the person (3.1: conflicts, open task items and
// validation problems now block only their own entries, so they are issues, not blockers).
public sealed class WorkspaceReviewPlan(IReadOnlyList<ReviewEntry> entries, IReadOnlyList<MessageDescriptor> blockers, IReadOnlyList<MessageDescriptor>? issues = null)
{
    public IReadOnlyList<ReviewEntry> Entries { get; } = entries;
    public IReadOnlyList<MessageDescriptor> Blockers { get; } = blockers;
    public IReadOnlyList<MessageDescriptor> Issues { get; } = issues ?? blockers;
    public int SubmitCount
    {
        get
        {
            if (Blockers.Count > 0)
                return 0;
            var count = 0;
            foreach (var entry in Entries)
                if (entry.Disposition == ReviewDisposition.Ready)
                    count++;
            return count;
        }
    }
    // The items are built from the reviewed rows on first use and kept: every call returns the same objects, in a new list.
    ChangeItem[]? submit;
    public List<ChangeItem> SubmitItems
    {
        get
        {
            if (Blockers.Count > 0)
                return [];
            if (submit is null)
            {
                var items = new List<ChangeItem>(SubmitCount);
                foreach (var entry in Entries)
                    if (entry.Disposition == ReviewDisposition.Ready)
                        items.Add(entry.Planned.ToItem());
                submit = [.. items];
            }
            return [.. submit];
        }
    }

    // 3.5 (F9, D8): the submission of only some resources. A ready change of a chosen resource that depends on a change
    // left out (another resource's create) is left out too, transitively, so nothing is sent ahead of what it needs.
    public List<ChangeItem> SubmitItemsFor(IReadOnlySet<ResourceKind> resources)
    {
        var all = SubmitItems;
        if (all.All(i => resources.Contains(i.Resource)))
            return all;
        var included = all.Where(i => resources.Contains(i.Resource)).ToList();
        var ids = included.Select(i => i.Id).ToHashSet();
        bool dropped;
        do
        {
            dropped = false;
            foreach (var item in included.ToArray())
                if (item.Dependencies.Any(d => !ids.Contains(d)))
                { included.Remove(item); ids.Remove(item.Id); dropped = true; }
        } while (dropped);
        return included;
    }

    // Planner IDs are deliberately excluded: dependencies are compared by row identity.
    // A SHA-256 over the content that affects the submission (the full rows and the changed fields), computed once: the reviewed
    // rows are not edited after planning. Every value is length-prefixed, so no two contents collide by layout.
    string? key;
    public string ContentKey() => key ??= ComputeKey();
    string ComputeKey()
    {
        // Dependencies are compared by the row they point at; only entries that have dependencies need the lookup.
        Dictionary<Guid, PlannedChange>? byId = null;
        var hash = new KeyHash();
        hash.Json(Blockers);
        hash.Json(Issues);
        hash.Int(Entries.Count);
        foreach (var e in Entries)
        {
            var change = e.Planned;
            var after = change.After;
            hash.Int((int)change.Resource);
            hash.Int((int)change.Kind);
            hash.Guid(after.LocalId);
            hash.Int(change.RequiresAirlineParameter ? 1 : 0);
            hash.Fields(change.Before?.FieldEntries());
            hash.Fields(after.FieldEntries());
            hash.Text(after.Identity?.RemoteId);
            hash.Text(after.Identity?.ConnectionId);
            hash.Text(after.Identity?.ParentFleetId);
            hash.Text(after.RawApiJson);
            hash.Int(change.FieldCount);
            foreach (var name in hash.Sorted(change))
            { change.TryGetField(name, out var intent, out var value); hash.Text(name); hash.Int((int)intent); hash.Text(value); }
            var dependencies = change.DependencyIds;
            hash.Int(dependencies.Count);
            if (dependencies.Count > 0)
            {
                if (byId is null)
                {
                    byId = new(Entries.Count);
                    foreach (var other in Entries)
                        byId[other.Planned.Id] = other.Planned;
                }
                foreach (var dependency in dependencies.Select(id => byId.TryGetValue(id, out var target) ? $"{target.Resource}:{target.After.LocalId}" : "missing:" + id).Order(StringComparer.Ordinal))
                    hash.Text(dependency);
            }
            hash.Int((int)e.Disposition);
            hash.Json(e.Reason);
        }
        return hash.Finish();
    }
    // Collects bytes in a block and hashes whole blocks: one native hash call per small value cost more than the hash.
    sealed class KeyHash
    {
        readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] block = new byte[64 * 1024]; int used;
        Span<byte> Reserve(int length)
        {
            if (used + length > block.Length)
            { hash.AppendData(block, 0, used); used = 0; if (length > block.Length) block = new byte[length]; }
            var span = block.AsSpan(used, length);
            used += length;
            return span;
        }
        void Bytes(ReadOnlySpan<byte> bytes) => bytes.CopyTo(Reserve(bytes.Length));
        public void Int(int value) => BinaryPrimitives.WriteInt32LittleEndian(Reserve(4), value);
        public void Guid(Guid value) => value.TryWriteBytes(Reserve(16));
        public void Text(string? value)
        {
            if (value is null)
            { Int(-1); return; }
            var target = Reserve(4 + Encoding.UTF8.GetMaxByteCount(value.Length));
            var written = Encoding.UTF8.GetBytes(value, target[4..]);
            BinaryPrimitives.WriteInt32LittleEndian(target, written);
            used -= target.Length - 4 - written;
        }
        public void Fields(DataRow.RowFields? fields)
        {
            if (fields is null)
            { Int(-1); return; }
            var map = fields.Value;
            Int(map.Count);
            foreach (var name in Sorted(map))
            { Text(name); map.TryGetValue(name, out var text); Text(text); }
        }
        // One reused buffer instead of a sorted copy per row; valid until the next call.
        string[] names = new string[32];
        public ArraySegment<string> Sorted(DataRow.RowFields fields)
        {
            var count = fields.Count;
            if (names.Length < count)
                names = new string[count * 2];
            var next = 0;
            foreach (var key in fields.Keys)
                names[next++] = key;
            Array.Sort(names, 0, count, StringComparer.Ordinal);
            return new(names, 0, count);
        }
        public ArraySegment<string> Sorted(PlannedChange change)
        {
            var count = change.FieldCount;
            if (names.Length < count)
                names = new string[count * 2];
            change.CopyFieldNames(names);
            Array.Sort(names, 0, count, StringComparer.Ordinal);
            return new(names, 0, count);
        }
        // Reasons and blockers are shared objects (one blocker can mark every entry): serialize each once.
        readonly Dictionary<object, byte[]> serialized = new(ReferenceEqualityComparer.Instance);
        public void Json<T>(T value)
        {
            if (value is null)
            { Int(-1); return; }
            if (!serialized.TryGetValue(value, out var bytes))
                serialized[value] = bytes = JsonSerializer.SerializeToUtf8Bytes(value);
            Int(bytes.Length);
            Bytes(bytes);
        }
        public string Finish() { hash.AppendData(block, 0, used); var result = Convert.ToHexString(hash.GetHashAndReset()); hash.Dispose(); return result; }
    }
}

/// <summary>Local preview only. The supplied API preview must not send requests.</summary>
public static class ReviewPlanBuilder
{
    /// <summary>The whole background build of the review page (blockers, plan, content key), free of any window state.</summary>
    public static WorkspaceReviewPlan BuildForWorkspace(ChangePlanner planner, Workspace w, RunMode mode, bool connected,
        ISet<string> verified, Action<ChangeItem>? preview)
    {
        // 3.1 (D6, D7): nothing blocks the whole workspace any more. A conflict, an item of an unfinished online task and a
        // validation problem each block only the change of their own row; what depends on it is deferred. Online and demo
        // reviews validate the same way. The executor checks conflicts per item too (PreflightStage).
        var changes = planner.ScanWorkspace(w);
        var changed = new HashSet<Guid>();
        foreach (var change in changes)
            changed.Add(change.After.LocalId);
        var rowBlocks = new Dictionary<Guid, MessageDescriptor>();
        var issues = new List<MessageDescriptor>();

        MessageDescriptor? conflict = null;
        foreach (var data in w.Resources.Values)
            foreach (var c in data.Conflicts)
                if (changed.Contains(c.RowId))
                    rowBlocks.TryAdd(c.RowId, conflict ??= Messages.Define("ApiConflict"));
        if (w.Resources.Values.Any(d => d.Conflicts.Count > 0))
            issues.Add(Messages.Define("ApiConflict"));

        if (mode == RunMode.Online)
        {
            var open = OpenTaskKeys(w);
            var held = 0;
            MessageDescriptor? reason = null;
            foreach (var change in changes)
                if (open.Count > 0 && TaskKeys(change.Resource, change.Before, change.After).Any(open.Contains) && rowBlocks.TryAdd(change.After.LocalId, reason ??= Messages.Define("ReviewOpenTaskItem")))
                    held++;
            if (held > 0)
                issues.Add(Messages.Define("ReviewOpenTaskItems", ("count", held)));
        }

        if (changed.Count > 0)
        {
            var pass = new ValidationPass(w);
            var kinds = changes.Select(c => c.Resource).ToHashSet();
            foreach (var (kind, data) in w.Resources.Where(p => kinds.Contains(p.Key)))
                foreach (var issue in Schemas.Validate(w, kind, pass))
                {
                    if (!changed.Contains(issue.RowId))
                        continue;
                    var row = data.FindDraftRow(issue.RowId);
                    var message = Messages.Define("ReviewValidationIssue", ("resource", Schemas.All[kind].Name),
                        ("key", row is null ? issue.RowId.ToString()[..8] : Schemas.Key(kind, row)), ("field", issue.Field), ("reason", issue.Description));
                    rowBlocks.TryAdd(issue.RowId, message);
                    issues.Add(message);
                }
        }
        var plan = BuildPlanned(changes, mode, connected, verified, preview, null, w.ContractHash, rowBlocks, issues);
        _ = plan.ContentKey();
        return plan;
    }

    // Who an unfinished online task's items are: their rows, their remote records and their keys (codes, names).
    // A skipped item counts while "Continue" can still send it, that is while everything it waits on has succeeded or is
    // itself still open; one that waits on a failed, conflicting or manually confirmed item can never run in its task
    // and does not hold its row (otherwise the row could never be submitted again).
    static HashSet<string> OpenTaskKeys(Workspace w)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var job in w.Jobs)
            if (job.Mode == RunMode.Online)
                foreach (var item in OpenItems(job))
                    keys.UnionWith(TaskKeys(item.Resource, item.Before, item.After));
        return keys;
    }

    internal static IEnumerable<ChangeItem> OpenItems(BatchJob job)
    {
        var byId = new Dictionary<Guid, ChangeItem>();
        foreach (var item in job.Items)
            byId.TryAdd(item.Id, item);
        var open = new Dictionary<ChangeItem, bool>();
        foreach (var item in job.Items)
            if (IsOpen(item, []))
                yield return item;

        bool IsOpen(ChangeItem item, HashSet<ChangeItem> visiting)
        {
            if (item.State is ItemState.Pending or ItemState.Running or ItemState.Unknown)
                return true;
            if (item.State != ItemState.Skipped)
                return false;
            if (open.TryGetValue(item, out var known))
                return known;
            if (!visiting.Add(item))
                return false;
            var result = item.Dependencies.All(id => !byId.TryGetValue(id, out var dependency)
                || dependency.State == ItemState.Succeeded || IsOpen(dependency, visiting));
            open[item] = result;
            return result;
        }
    }

    static IEnumerable<string> TaskKeys(ResourceKind kind, DataRow? before, DataRow after)
    {
        yield return $"{kind}|row|{after.LocalId}";
        foreach (var row in before is null ? [after] : new[] { before, after })
        {
            if ((row.Identity?.RemoteId ?? (row.Get("ID") is { Length: > 0 } id ? id : null)) is string remote)
                yield return $"{kind}|id|{remote}";
            if (Schemas.Key(kind, row) is { Length: > 0 } key)
                yield return $"{kind}|key|{key}";
        }
    }

    public static WorkspaceReviewPlan Build(IReadOnlyList<ChangeItem> changes, RunMode mode, bool connected,
        ISet<string> verified, Action<ChangeItem>? preview = null, IReadOnlyList<MessageDescriptor>? blockers = null, string? externalEvidenceContractHash = null)
    {
        var planned = new List<PlannedChange>(changes.Count);
        foreach (var change in changes)
            planned.Add(PlannedChange.Wrap(change));
        return BuildPlanned(planned, mode, connected, verified, preview, blockers, externalEvidenceContractHash);
    }

    public static WorkspaceReviewPlan BuildPlanned(IReadOnlyList<PlannedChange> changes, RunMode mode, bool connected,
        ISet<string> verified, Action<ChangeItem>? preview = null, IReadOnlyList<MessageDescriptor>? blockers = null, string? externalEvidenceContractHash = null,
        IReadOnlyDictionary<Guid, MessageDescriptor>? rowBlocks = null, IReadOnlyList<MessageDescriptor>? issues = null)
    {
        var entries = new List<ReviewEntry>(changes.Count);
        var samples = new Dictionary<string, int>(StringComparer.Ordinal);
        // The same reason objects are shared by all entries that have it (the key serializes each once).
        MessageDescriptor? connect = null, sample = null, evidence = null;
        // Operation keys, evidence and the API preview read a ChangeItem. A change that has none yet gets its own transient item
        // that refers to the planned rows (no row copies); it is not kept or shared with another change.
        static ChangeItem ItemOf(PlannedChange change)
        {
            if (change.BuiltItem is { } built)
                return built;
            var fields = new Dictionary<string, FieldChange>(change.FieldCount, StringComparer.Ordinal);
            foreach (var field in change.Fields())
                fields.Add(field.Key, field.Value);
            return new ChangeItem
            {
                Resource = change.Resource, Kind = change.Kind, Before = change.Before, After = change.After,
                RequiresAirlineParameter = change.RequiresAirlineParameter, Fields = fields
            };
        }
        foreach (var change in changes)
        {
            if (mode != RunMode.Demo && !connected)
            {
                entries.Add(new(change, ReviewDisposition.AwaitingConnection, connect ??= Messages.Define("ReviewConnect")));
                continue;
            }
            if (rowBlocks != null && rowBlocks.TryGetValue(change.After.LocalId, out var rowBlock))
            { entries.Add(new(change, ReviewDisposition.Blocked, rowBlock)); continue; }
            var item = mode == RunMode.Online ? ItemOf(change) : null;
            try
            { if (mode == RunMode.Online) preview!(item!); }
            catch (Exception e) { entries.Add(new(change, ReviewDisposition.Blocked, MessageErrors.Describe(e))); continue; }
            // Only an Online review samples operations and consults the evidence.
            var external = false;
            if (mode == RunMode.Online)
            {
                var operation = ApiOperationPolicy.Key(item!);
                external = IndependentApiEvidence.Covers(item!, externalEvidenceContractHash);
                if (!verified.Contains(operation) && !external)
                {
                    var count = samples.GetValueOrDefault(operation);
                    samples[operation] = count + 1;
                    if (count >= 3)
                    { entries.Add(new(change, ReviewDisposition.Deferred, sample ??= Messages.Define("ReviewSample"))); continue; }
                }
            }
            entries.Add(new(change, ReviewDisposition.Ready, external ? evidence ??= Messages.Define("ApiIndependentEvidence") : null));
        }
        DeferDependents(entries);
        if (blockers is { Count: > 0 })
            for (var n = 0; n < entries.Count; n++)
                if (entries[n].Disposition == ReviewDisposition.Ready)
                    entries[n] = entries[n] with { Disposition = ReviewDisposition.Blocked, Reason = blockers[0] };
        return new(entries, blockers ?? [], issues);
    }

    // A ready entry whose dependency is not ready is deferred, until nothing changes. Most reviews have no dependencies at all.
    static void DeferDependents(List<ReviewEntry> entries)
    {
        List<int>? dependents = null;
        for (var n = 0; n < entries.Count; n++)
            if (entries[n].Disposition == ReviewDisposition.Ready && entries[n].Planned.DependencyIds.Count > 0)
                (dependents ??= []).Add(n);
        if (dependents is null)
            return;
        var position = new Dictionary<Guid, int>();
        var wanted = dependents.SelectMany(n => entries[n].Planned.DependencyIds).ToHashSet();
        for (var n = 0; n < entries.Count; n++)
            if (wanted.Contains(entries[n].Planned.Id))
                position[entries[n].Planned.Id] = n;
        MessageDescriptor? reason = null;
        bool changed;
        do
        {
            changed = false;
            foreach (var n in dependents)
                if (entries[n].Disposition == ReviewDisposition.Ready
                    && entries[n].Planned.DependencyIds.Any(id => !position.TryGetValue(id, out var at) || entries[at].Disposition != ReviewDisposition.Ready))
                {
                    entries[n] = entries[n] with { Disposition = ReviewDisposition.Deferred, Reason = reason ??= Messages.Define("ReviewDependency") };
                    changed = true;
                }
        } while (changed);
    }
}