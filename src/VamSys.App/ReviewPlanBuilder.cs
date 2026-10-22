using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VamSys.Core;

namespace VamSys.App;

public enum ReviewDisposition { Ready, Deferred, Blocked, AwaitingConnection }
public sealed record ReviewEntry(ChangeItem Change, ReviewDisposition Disposition, MessageDescriptor? Reason);

public sealed class WorkspaceReviewPlan(IReadOnlyList<ReviewEntry> entries, IReadOnlyList<MessageDescriptor> blockers)
{
    public IReadOnlyList<ReviewEntry> Entries { get; } = entries;
    public IReadOnlyList<MessageDescriptor> Blockers { get; } = blockers;
    public List<ChangeItem> SubmitItems => Blockers.Count == 0
        ? Entries.Where(e => e.Disposition == ReviewDisposition.Ready).Select(e => e.Change).ToList() : [];

    // Planner IDs are deliberately excluded: dependencies are compared by row identity.
    // A SHA-256 over the same content the JSON form held, computed once: the plan's change
    // copies are not edited after planning. Every value is length-prefixed, so no two contents collide by layout.
    string? key;
    public string ContentKey() => key ??= ComputeKey();
    string ComputeKey()
    {
        var identities = new Dictionary<Guid, string>(Entries.Count);
        foreach (var e in Entries)
            identities[e.Change.Id] = $"{e.Change.Resource}:{e.Change.After.LocalId}";
        var hash = new KeyHash();
        hash.Json(Blockers);
        hash.Int(Entries.Count);
        foreach (var e in Entries)
        {
            var change = e.Change;
            var after = change.After;
            hash.Int((int)change.Resource);
            hash.Int((int)change.Kind);
            hash.Text(after.LocalId.ToString("N"));
            hash.Int(change.RequiresAirlineParameter ? 1 : 0);
            hash.Fields(change.Before?.Fields);
            hash.Fields(after.Fields);
            hash.Text(after.Identity?.RemoteId);
            hash.Text(after.Identity?.ConnectionId);
            hash.Text(after.Identity?.ParentFleetId);
            hash.Text(after.RawApiJson);
            hash.Int(change.Fields.Count);
            foreach (var name in hash.Sorted(change.Fields.Keys))
            { var field = change.Fields[name]; hash.Text(name); hash.Int((int)field.Intent); hash.Text(field.Value); }
            hash.Int(change.Dependencies.Count);
            if (change.Dependencies.Count > 0)
                foreach (var dependency in change.Dependencies.Select(id => identities.GetValueOrDefault(id, "missing:" + id)).Order(StringComparer.Ordinal))
                    hash.Text(dependency);
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
        public void Text(string? value)
        {
            if (value is null)
            { Int(-1); return; }
            var target = Reserve(4 + Encoding.UTF8.GetMaxByteCount(value.Length));
            var written = Encoding.UTF8.GetBytes(value, target[4..]);
            BinaryPrimitives.WriteInt32LittleEndian(target, written);
            used -= target.Length - 4 - written;
        }
        public void Fields(Dictionary<string, string>? fields)
        {
            if (fields is null)
            { Int(-1); return; }
            Int(fields.Count);
            foreach (var name in Sorted(fields.Keys))
            { Text(name); Text(fields[name]); }
        }
        // One reused buffer instead of a sorted copy per row; valid until the next call.
        string[] names = new string[32];
        public ArraySegment<string> Sorted(ICollection<string> keys)
        {
            if (names.Length < keys.Count)
                names = new string[keys.Count * 2];
            keys.CopyTo(names, 0);
            Array.Sort(names, 0, keys.Count, StringComparer.Ordinal);
            return new(names, 0, keys.Count);
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
        var plan = Build(planner.PlanWorkspace(w), mode, connected, verified, preview, blockers, w.ContractHash);
        _ = plan.ContentKey();
        return plan;
    }

    public static WorkspaceReviewPlan Build(IReadOnlyList<ChangeItem> changes, RunMode mode, bool connected,
        ISet<string> verified, Action<ChangeItem>? preview = null, IReadOnlyList<MessageDescriptor>? blockers = null, string? externalEvidenceContractHash = null)
    {
        var entries = new List<ReviewEntry>(changes.Count);
        var samples = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var change in changes)
        {
            if (mode != RunMode.Demo && !connected)
            {
                entries.Add(new(change, ReviewDisposition.AwaitingConnection, Messages.Define("ReviewConnect")));
                continue;
            }
            try
            { if (mode == RunMode.Online) preview!(change); }
            catch (Exception e) { entries.Add(new(change, ReviewDisposition.Blocked, MessageErrors.Describe(e))); continue; }
            var operation = ApiOperationPolicy.Key(change);
            var external = mode == RunMode.Online && IndependentApiEvidence.Covers(change, externalEvidenceContractHash);
            if (mode == RunMode.Online && !verified.Contains(operation) && !external)
            {
                var count = samples.GetValueOrDefault(operation);
                samples[operation] = count + 1;
                if (count >= 3)
                { entries.Add(new(change, ReviewDisposition.Deferred, Messages.Define("ReviewSample"))); continue; }
            }
            entries.Add(new(change, ReviewDisposition.Ready, external ? Messages.Define("ApiIndependentEvidence") : null));
        }
        bool changed;
        do
        {
            changed = false;
            var allowed = entries.Where(e => e.Disposition == ReviewDisposition.Ready).Select(e => e.Change.Id).ToHashSet();
            for (var n = 0; n < entries.Count; n++)
                if (entries[n].Disposition == ReviewDisposition.Ready && entries[n].Change.Dependencies.Any(id => !allowed.Contains(id)))
                {
                    entries[n] = entries[n] with { Disposition = ReviewDisposition.Deferred, Reason = Messages.Define("ReviewDependency") };
                    changed = true;
                }
        } while (changed);
        if (blockers is { Count: > 0 })
            for (var n = 0; n < entries.Count; n++)
                if (entries[n].Disposition == ReviewDisposition.Ready)
                    entries[n] = entries[n] with { Disposition = ReviewDisposition.Blocked, Reason = blockers[0] };
        return new(entries, blockers ?? []);
    }
}
