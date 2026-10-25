namespace VamSys.Core;

// What a save may be limited to. A scoped save leaves the other parts pending in the journal.
[Flags]
public enum JournalScope { None = 0, Header = 1, Jobs = 2, Resources = 4, All = 7 }

public enum ChangeMode { Unchanged, Delta, Rewrite }

public sealed record PlacedRow(DataRow Row, int Ordinal);
public sealed record MovedRow(Guid Id, int Ordinal);

// The pending change of one row list (Snapshot or Draft) of one resource, relative to what the database holds.
//   Delta:   Deleted rows go, Moved rows get a new position, Replaced rows (same id, new content) and Added rows are written.
//   Rewrite: the list is not expressible as a small delta; All holds the whole list and replaces the stored one.
public sealed record RowListChange(ChangeMode Mode, IReadOnlyList<Guid> Deleted, IReadOnlyList<MovedRow> Moved, IReadOnlyList<PlacedRow> Replaced, IReadOnlyList<PlacedRow> Added, IReadOnlyList<DataRow> All)
{
    public static readonly RowListChange Unchanged = new(ChangeMode.Unchanged, [], [], [], [], []);
    public bool IsEmpty => Mode == ChangeMode.Unchanged;
    // Rows whose JSON is written.
    public int RowWrites => Mode == ChangeMode.Rewrite ? All.Count : Replaced.Count + Added.Count;
}

public sealed record ResourceChange(ResourceKind Kind, RowListChange Snapshot, RowListChange Draft, bool MetadataChanged,
    IReadOnlyList<string> Columns, IReadOnlyList<MergeConflict> Conflicts, DateTimeOffset? SnapshotAt)
{
    public bool IsEmpty => Snapshot.IsEmpty && Draft.IsEmpty && !MetadataChanged;
}

public sealed record JobPlacement(BatchJob Job, int Ordinal);
// Inserted jobs are new (written with their items); Rewritten jobs exist and are written again (header and all items).
public sealed record JobsChange(ChangeMode Mode, IReadOnlyList<JobPlacement> Inserted, IReadOnlyList<JobPlacement> Rewritten, IReadOnlyList<BatchJob> All)
{
    public static readonly JobsChange Unchanged = new(ChangeMode.Unchanged, [], [], []);
    public bool IsEmpty => Mode == ChangeMode.Unchanged;
}

// Everything one journal save has to write, computed without touching the database. Accepting it (after the commit)
// moves the journal forward; dropping it leaves the journal exactly as it was.
public sealed class JournalPlan
{
    internal JournalPlan(object baseState, object next, long baseRevision, long tick, JournalScope scope, bool headerChanged, IReadOnlyList<ResourceChange> resources, JobsChange jobs)
    { BaseState = baseState; Next = next; BaseRevision = baseRevision; Tick = tick; Scope = scope; HeaderChanged = headerChanged; Resources = resources; Jobs = jobs; }
    internal object BaseState { get; }
    internal object Next { get; }
    public long BaseRevision { get; }
    internal long Tick { get; }
    public JournalScope Scope { get; }
    public bool HeaderChanged { get; }
    public IReadOnlyList<ResourceChange> Resources { get; }
    public JobsChange Jobs { get; }
    // History is outside the journal (it keeps its own incremental mechanism), so an empty plan can still need a history write.
    public bool IsEmpty => !HeaderChanged && Jobs.IsEmpty && Resources.Count == 0;
}

// A complete observation of a workspace taken at the moment a full save was captured.
public sealed class JournalCapture
{
    internal JournalCapture(object state, long tick) { State = state; Tick = tick; }
    internal object State { get; }
    internal long Tick { get; }
}

// The record of what the database holds for one workspace, kept next to the in-memory workspace.
//
// Rows are never edited in place (DataRow is immutable, enforced by the compiler in every configuration): every change replaces, adds or removes row
// objects. The journal therefore keeps the row objects the database was last synchronised with and derives the pending
// changes by comparing them with the current lists. This covers every mutation path, including code that manipulates
// the lists directly, so a change cannot be skipped by forgetting to record it. The small collections (columns,
// conflicts, snapshot time, workspace header) are compared by value in the same way.
//
// Writers that change the database without going through the journal bump the stored revision; a journal whose
// revision differs from the workspace's is not trusted and the next save is a full one. Writers that do know about the
// journal tell it (AfterExternalWrite plus the Mark/Touch calls). Explicit marks exist only for parts that cannot be
// compared: MarkRowsDirty rewrites the rows of a resource, TouchJob rewrites one job (jobs and their items are mutated
// in place by the executors).
public sealed class ChangeJournal
{
    // The database may hold a different order of ordinals than the lists once many rows shift; beyond this a list is
    // rewritten instead of renumbered.
    public const int MaxRenumber = 256;

    sealed record ResourceState(DataRow[] Snapshot, DataRow[] Draft, string[] Columns, MergeConflict[] Conflicts, DateTimeOffset? SnapshotAt)
    {
        public static readonly ResourceState Empty = new([], [], [], [], null);
    }
    sealed record State(string HeaderKey, IReadOnlyDictionary<ResourceKind, ResourceState> Resources, BatchJob[] Jobs, long[] JobPrints);

    readonly object gate = new();
    State? state;
    long revision;
    long tick;
    readonly Dictionary<ResourceKind, long> dirtyRows = [];
    readonly Dictionary<Guid, long> touchedJobs = [];

    // True when the journal describes the database at exactly this stored revision.
    public bool IsTracking(long storedRevision) { lock (gate) return state != null && revision == storedRevision; }

    public void RequireFullSave() { lock (gate) state = null; }
    public void MarkRowsDirty(ResourceKind kind) { lock (gate) dirtyRows[kind] = ++tick; }
    public void MarkAllRowsDirty() { lock (gate) foreach (var kind in Enum.GetValues<ResourceKind>()) dirtyRows[kind] = ++tick; }
    public void TouchJob(BatchJob job) => TouchJob(job.Id);
    public void TouchJob(Guid id) { lock (gate) touchedJobs[id] = ++tick; }

    // Takes over the state of a staged copy of the workspace whose resources, jobs and header replace this one's.
    public void AdoptFrom(ChangeJournal other)
    {
        State? s; long r; long t;
        lock (other.gate) { s = other.state; r = other.revision; t = other.tick; }
        lock (gate) { state = s; revision = r; tick = Math.Max(tick, t) + 1; dirtyRows.Clear(); touchedJobs.Clear(); }
    }

    public JournalCapture Capture(Workspace w, string headerKey)
    {
        var resources = new Dictionary<ResourceKind, ResourceState>();
        foreach (var (kind, data) in w.Resources)
            resources[kind] = Observe(data);
        var captured = new State(headerKey, resources, [.. w.Jobs], [.. w.Jobs.Select(Fingerprint)]);
        lock (gate) return new(captured, tick);
    }
    static ResourceState Observe(ResourceData data) => new([.. data.Snapshot], [.. data.Draft], [.. data.Columns], [.. data.Conflicts], data.SnapshotAt);

    // After a full save committed: the journal now equals the captured state.
    public void Adopt(JournalCapture capture, long storedRevision)
    {
        lock (gate)
        {
            state = (State)capture.State;
            revision = storedRevision;
            foreach (var key in dirtyRows.Where(p => p.Value <= capture.Tick).Select(p => p.Key).ToList())
                dirtyRows.Remove(key);
            foreach (var key in touchedJobs.Where(p => p.Value <= capture.Tick).Select(p => p.Key).ToList())
                touchedJobs.Remove(key);
        }
    }

    // A writer that knows what it changed (task checkpoints) committed revision `next`; the journal stays valid.
    public void AfterExternalWrite(long expected, long next)
    {
        lock (gate)
            if (state != null && revision == expected)
                revision = next;
    }

    // Draft rows were written in place (same positions) by the row-level save; follow with AfterExternalWrite.
    public void AcceptDraftRows(ResourceKind kind, IReadOnlyList<DataRow> rows)
    {
        lock (gate)
        {
            if (state == null)
                return;
            var old = state.Resources.GetValueOrDefault(kind) ?? ResourceState.Empty;
            var draft = (DataRow[])old.Draft.Clone();
            Dictionary<Guid, int>? index = null;
            foreach (var row in rows)
            {
                int at;
                if (rows.Count > 8)
                {
                    if (index == null)
                    { index = new(draft.Length); for (var i = 0; i < draft.Length; i++) index[draft[i].LocalId] = i; }
                    at = index.GetValueOrDefault(row.LocalId, -1);
                }
                else
                    at = Array.FindIndex(draft, r => r.LocalId == row.LocalId);
                if (at < 0)
                { state = null; return; }
                draft[at] = row;
            }
            state = state with { Resources = new Dictionary<ResourceKind, ResourceState>(state.Resources) { [kind] = old with { Draft = draft } } };
        }
    }

    // Computes what has to be written. Returns null when the journal cannot be trusted (new workspace, a writer that did
    // not report, an earlier failure): the caller then performs a full save.
    public JournalPlan? Plan(Workspace w, string headerKey, long storedRevision, JournalScope scope)
    {
        lock (gate)
        {
            if (state == null || revision != storedRevision)
                return null;
            var current = state;
            var headerChanged = scope.HasFlag(JournalScope.Header) && headerKey != current.HeaderKey;
            var changes = new List<ResourceChange>();
            var resources = current.Resources;
            if (scope.HasFlag(JournalScope.Resources))
            {
                var next = new Dictionary<ResourceKind, ResourceState>(current.Resources);
                foreach (var (kind, data) in w.Resources)
                {
                    var old = current.Resources.GetValueOrDefault(kind) ?? ResourceState.Empty;
                    var force = dirtyRows.ContainsKey(kind);
                    var snapshot = Compare(old.Snapshot, data.Snapshot, force, out var snapshotRows);
                    var draft = Compare(old.Draft, data.Draft, force, out var draftRows);
                    var currentColumns = data.Columns.ToArray();
                    var columns = old.Columns.AsSpan().SequenceEqual(currentColumns) ? old.Columns : currentColumns;
                    var currentConflicts = data.Conflicts.ToArray();
                    var conflicts = old.Conflicts.AsSpan().SequenceEqual(currentConflicts) ? old.Conflicts : currentConflicts;
                    var metadata = force || !ReferenceEquals(columns, old.Columns) || !ReferenceEquals(conflicts, old.Conflicts) || old.SnapshotAt != data.SnapshotAt;
                    var change = new ResourceChange(kind, snapshot, draft, metadata, columns, conflicts, data.SnapshotAt);
                    if (!change.IsEmpty)
                        changes.Add(change);
                    next[kind] = new(snapshotRows, draftRows, columns, conflicts, data.SnapshotAt);
                }
                resources = next;
            }
            var jobs = JobsChange.Unchanged;
            var nextJobs = current.Jobs;
            var nextPrints = current.JobPrints;
            if (scope.HasFlag(JournalScope.Jobs))
            {
                jobs = CompareJobs(current.Jobs, current.JobPrints, w.Jobs, out nextJobs, out nextPrints);
            }
            var nextState = new State(headerChanged ? headerKey : current.HeaderKey, resources, nextJobs, nextPrints);
            return new(current, nextState, storedRevision, tick, scope, headerChanged, changes, jobs);
        }
    }

    // After the plan committed as revision `storedRevision`.
    public void Accept(JournalPlan plan, long storedRevision)
    {
        lock (gate)
        {
            if (!ReferenceEquals(state, plan.BaseState))
                return;
            state = (State)plan.Next;
            revision = storedRevision;
            if (plan.Scope.HasFlag(JournalScope.Resources))
                foreach (var key in dirtyRows.Where(p => p.Value <= plan.Tick).Select(p => p.Key).ToList())
                    dirtyRows.Remove(key);
            if (plan.Scope.HasFlag(JournalScope.Jobs))
                foreach (var key in touchedJobs.Where(p => p.Value <= plan.Tick).Select(p => p.Key).ToList())
                    touchedJobs.Remove(key);
        }
    }

    // Safety net for jobs nobody marked: a job whose state fingerprint differs from the saved one is rewritten as if touched.
    JobsChange CompareJobs(BatchJob[] old, long[] oldPrints, List<BatchJob> current, out BatchJob[] next, out long[] nextPrints)
    {
        var prints = new long[current.Count];
        for (var i = 0; i < prints.Length; i++)
            prints[i] = Fingerprint(current[i]);
        nextPrints = prints;
        var prefix = current.Count >= old.Length;
        for (var i = 0; prefix && i < old.Length; i++)
            prefix = current[i].Id == old[i].Id;
        var rewritten = new List<JobPlacement>();
        if (prefix)
            for (var i = 0; i < old.Length; i++)
                if (!ReferenceEquals(current[i], old[i]) || touchedJobs.ContainsKey(current[i].Id) || prints[i] != oldPrints[i])
                    rewritten.Add(new(current[i], i));
        var inserted = prefix ? current.Skip(old.Length).Select((j, n) => new JobPlacement(j, old.Length + n)).ToList() : [];
        if (prefix && rewritten.Count == 0 && inserted.Count == 0)
        { next = old; nextPrints = oldPrints; return JobsChange.Unchanged; }
        next = [.. current];
        return prefix ? new(ChangeMode.Delta, inserted, rewritten, []) : new(ChangeMode.Rewrite, [], [], next);
    }

    // A cheap, serialization-free digest of everything the executors and resolvers keep in a job: status, and per item the
    // state flags, remote ids, messages, diagnostics, API steps and the identity of the attached evidence objects. It
    // catches in-place state changes nobody marked (those decide whether a write may be sent again); it is not a hash of
    // the full JSON, so edits to other item details still need TouchJob.
    static long Fingerprint(BatchJob job)
    {
        long h = unchecked((long)0xcbf29ce484222325);
        void Add(long v) { unchecked { h = (h ^ v) * 0x100000001b3; h ^= h >>> 29; } }
        void Text(string? s) => Add(s == null ? -1 : s.GetHashCode(StringComparison.Ordinal));
        void Ref(object? o) => Add(o == null ? 0 : o is ValueType ? o.GetHashCode() : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o));
        Add(job.Id.GetHashCode()); Add((int)job.Mode); Add((int)job.StatusCode); Text(job.Status); Ref(job.Description); Add(job.Items.Count);
        foreach (var item in job.Items)
        {
            Add(item.Id.GetHashCode()); Add((int)item.State); Add((int)item.Kind); Add((int)item.Resource);
            Text(item.RemoteId); Text(item.RecoveryCandidateId); Text(item.Message); Text(item.Diagnostic);
            Add((item.Rebased ? 1 : 0) | (item.CreateCompleted ? 2 : 0) | (item.WriteAccepted ? 4 : 0) | (item.RequiresAirlineParameter ? 8 : 0));
            Add(item.RecoveryCandidates.Count); Add(item.Dependencies.Count); Add(item.VerificationDifferences.Count); Add(item.PreCreateCandidateIds?.Count ?? -1);
            Ref(item.Description); Ref(item.DiagnosticDetails); Ref(item.ReadbackDetails); Ref(item.DeletionVerification); Ref(item.ManualResolution); Ref(item.PreflightComparison); Ref(item.ApiExpectedValues);
            Ref(item.After); Ref(item.Before); Add(item.After.Fields.Count); Add(item.After.Identity?.GetHashCode() ?? 0);
            Add(item.ApiSteps.Count);
            foreach (var step in item.ApiSteps)
            { Add((int)step.State); Text(step.Method); Text(step.Path); Text(step.RemoteId); Add(step.StartedAt?.Ticks ?? 0); Add(step.FinishedAt?.Ticks ?? 0); }
        }
        return h;
    }

    // Compares the stored rows with the current list. `rows` is the new stored state (the old array when nothing changed).
    static RowListChange Compare(DataRow[] old, List<DataRow> current, bool rewrite, out DataRow[] rows)
    {
        var n = current.Count;
        var shared = Math.Min(n, old.Length);
        if (!rewrite)
        {
            // Fast path: no row changed place. Mismatches are replacements; the tail is appended or removed.
            List<PlacedRow>? replaced = null;
            var aligned = true;
            for (var i = 0; i < shared; i++)
            {
                var row = current[i];
                if (ReferenceEquals(row, old[i]))
                    continue;
                if (row.LocalId != old[i].LocalId)
                { aligned = false; break; }
                if (!DataRow.SameContent(row, old[i]))
                    (replaced ??= []).Add(new(row, i));
            }
            if (aligned)
            {
                if (replaced == null && n == old.Length)
                { rows = AllReferencesEqual(old, current) ? old : [.. current]; return RowListChange.Unchanged; }
                rows = [.. current];
                var added = new List<PlacedRow>();
                for (var i = old.Length; i < n; i++)
                    added.Add(new(current[i], i));
                var deleted = new List<Guid>();
                for (var i = n; i < old.Length; i++)
                    deleted.Add(old[i].LocalId);
                return new(ChangeMode.Delta, deleted, [], replaced ?? [], added, []);
            }
            var slow = Shifted(old, current);
            if (slow != null)
            { rows = [.. current]; return slow; }
        }
        rows = [.. current];
        return new(ChangeMode.Rewrite, [], [], [], [], rows);
    }

    static bool AllReferencesEqual(DataRow[] old, List<DataRow> current)
    {
        for (var i = 0; i < old.Length; i++)
            if (!ReferenceEquals(old[i], current[i]))
                return false;
        return true;
    }

    // Rows were inserted or removed before the end, or reordered. Null when that is too much to renumber.
    static RowListChange? Shifted(DataRow[] old, List<DataRow> current)
    {
        var index = new Dictionary<Guid, int>(old.Length);
        for (var i = 0; i < old.Length; i++)
            if (!index.TryAdd(old[i].LocalId, i))
                return null;
        var seen = new bool[old.Length];
        var moved = new List<MovedRow>();
        var replaced = new List<PlacedRow>();
        var added = new List<PlacedRow>();
        for (var i = 0; i < current.Count; i++)
        {
            var row = current[i];
            if (!index.TryGetValue(row.LocalId, out var j))
            { added.Add(new(row, i)); continue; }
            if (seen[j])
                return null;
            seen[j] = true;
            if (j != i)
            {
                moved.Add(new(row.LocalId, i));
                if (moved.Count > Math.Max(MaxRenumber, current.Count / 20))
                    return null;
            }
            if (!ReferenceEquals(row, old[j]) && !DataRow.SameContent(row, old[j]))
                replaced.Add(new(row, i));
        }
        var deleted = new List<Guid>();
        for (var j = 0; j < old.Length; j++)
            if (!seen[j])
                deleted.Add(old[j].LocalId);
        return new(ChangeMode.Delta, deleted, moved, replaced, added, []);
    }
}
