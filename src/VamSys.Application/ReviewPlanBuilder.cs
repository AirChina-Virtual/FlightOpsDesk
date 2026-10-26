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

public sealed class WorkspaceReviewPlan(IReadOnlyList<ReviewEntry> entries, IReadOnlyList<MessageDescriptor> blockers)
{
    public IReadOnlyList<ReviewEntry> Entries { get; } = entries;
    public IReadOnlyList<MessageDescriptor> Blockers { get; } = blockers;
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
        var blockers = new List<MessageDescriptor>();
        if (w.Resources.Values.Any(d => d.Conflicts.Count > 0))
            blockers.Add(Messages.Define("ApiConflict"));
        if (mode == RunMode.Online && ApiOperationPolicy.HasOpenOnlineItems(w))
            blockers.Add(Messages.Define("ApiPendingJob"));
        if (mode == RunMode.Demo)
        {
            var pass = new ValidationPass(w);
            foreach (var (kind, data) in w.Resources.Where(p => p.Value.Draft.Count > 0))
                foreach (var issue in Schemas.Validate(w, kind, pass))
                {
                    var row = data.FindDraftRow(issue.RowId);
                    blockers.Add(Messages.Define("ReviewValidationIssue", ("resource", Schemas.All[kind].Name),
                        ("key", row is null ? issue.RowId.ToString()[..8] : Schemas.Key(kind, row)), ("field", issue.Field), ("reason", issue.Description)));
                }
        }
        var plan = BuildPlanned(planner.ScanWorkspace(w), mode, connected, verified, preview, blockers, w.ContractHash);
        _ = plan.ContentKey();
        return plan;
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
        ISet<string> verified, Action<ChangeItem>? preview = null, IReadOnlyList<MessageDescriptor>? blockers = null, string? externalEvidenceContractHash = null)
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
        return new(entries, blockers ?? []);
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