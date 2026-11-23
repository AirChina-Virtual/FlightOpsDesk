using System.Text.Json;

namespace VamSys.Core;

public enum ResourceKind { Airports, Fleets, Aircraft, Routings, Routes }
public enum RunMode { Offline, Demo, Online }
public enum ChangeKind { Create, Update, Delete }
public enum ItemState { Pending, Running, Succeeded, Failed, Unknown, Conflict, Skipped, ManuallyConfirmed }
public enum FieldIntent { Unspecified, Set, Clear }
public record FieldChange(FieldIntent Intent, string? Value);
public record Issue(Guid RowId, string Field, MessageDescriptor Description)
{
    public string Message => Description.ToString();
}

public sealed record ResourceIdentity(string RemoteId, string ConnectionId, string? ParentFleetId = null);
public sealed record MergeConflict(Guid RowId, string Field, string? Original, string? Local, string? Remote);

[System.Text.Json.Serialization.JsonConverter(typeof(ResourceDataConverter))]
public sealed partial class ResourceData
{
    List<DataRow> snapshot = [];
    List<DataRow> draft = [];
    Dictionary<Guid, int>? draftIndex;
    public List<DataRow> Snapshot { get => snapshot; set => snapshot = value; }
    public List<DataRow> Draft { get => draft; set { draft = value; InvalidateDraftIndex(); } }
    public List<string> Columns { get; set; } = [];
    public DateTimeOffset? SnapshotAt { get; set; }
    public EditHistory History { get; set; } = new();
    public IReadOnlyList<HistoryStep> Undo => History.Undo;
    public IReadOnlyList<HistoryStep> Redo => History.Redo;
    public List<MergeConflict> Conflicts { get; set; } = [];
    // New collections over the same (immutable) row objects.
    public ResourceData ShallowCopy() => new() { Snapshot = [.. Snapshot], Draft = [.. Draft], Columns = [.. Columns], SnapshotAt = SnapshotAt, History = History.Copy(), Conflicts = [.. Conflicts] };
    public void InvalidateDraftIndex() => draftIndex = null;
    internal void EnsureDraftIndex()
    {
        if (draftIndex != null && draftIndex.Count == Draft.Count)
            return;
        var index = new Dictionary<Guid, int>(Draft.Count);
        for (var i = 0; i < Draft.Count; i++)
            index.Add(Draft[i].LocalId, i);
        draftIndex = index;
    }
    internal Dictionary<Guid, int> GetDraftPositions() { EnsureDraftIndex(); return draftIndex!; }
    // Used only after history has validated every source and destination position.
    internal void ReplaceIndexedDraft(List<DataRow> rows, Dictionary<Guid, int> positions)
    { draft = rows; draftIndex = positions; }
    public int FindDraftPosition(Guid id)
    {
        EnsureDraftIndex();
        if (!draftIndex!.TryGetValue(id, out var position))
            return -1;
        if (Draft[position].LocalId == id)
            return position;
        InvalidateDraftIndex();
        EnsureDraftIndex();
        return draftIndex!.GetValueOrDefault(id, -1);
    }
    public DataRow? FindDraftRow(Guid id) => FindDraftPosition(id) is var position && position >= 0 ? Draft[position] : null;
    // Same-ID replacements preserve order and the position index. Use Edit around
    // this operation when it must create an undo step; missing targets are ignored.
    public void ReplaceDraftRows(IEnumerable<DataRow> replacements)
    {
        var count = replacements.TryGetNonEnumeratedCount(out var knownCount) ? knownCount : 0;
        var prepared = new List<(int Position, DataRow Row)>(count);
        var seen = new HashSet<Guid>(count);
        foreach (var row in replacements)
        {
            if (!seen.Add(row.LocalId))
                throw new ArgumentException("Duplicate replacement LocalId.", nameof(replacements));
            var position = FindDraftPosition(row.LocalId);
            if (position >= 0)
                prepared.Add((position, row));
        }
        foreach (var (position, row) in prepared)
            Draft[position] = row;
    }
    public void ClearHistory(bool invalidateIndex = true) { History.Clear(); if (invalidateIndex) InvalidateDraftIndex(); }
    /// <summary>
    /// After a read: every draft row whose content is exactly its snapshot row's (<see cref="DataRow.SameContent"/>) is
    /// replaced by that snapshot row object, so an unchanged record is held once instead of twice. Rows are immutable
    /// and both lists are compared separately everywhere (journal, history, planner), so sharing changes no result.
    /// Positions and ids stay the same, so the draft index stays valid. Returns the number of rows now shared.
    /// </summary>
    public int ShareUnchangedDraftRows()
    {
        if (snapshot.Count == 0 || draft.Count == 0)
            return 0;
        Dictionary<Guid, DataRow>? byId = null;
        var shared = 0;
        for (var i = 0; i < draft.Count; i++)
        {
            var row = draft[i];
            DataRow? baseline;
            if (i < snapshot.Count && snapshot[i].LocalId == row.LocalId)
                baseline = snapshot[i];
            else
            {
                if (byId == null)
                {
                    byId = new(snapshot.Count);
                    foreach (var s in snapshot)
                        byId.TryAdd(s.LocalId, s);
                }
                baseline = byId.GetValueOrDefault(row.LocalId);
            }
            if (baseline == null)
                continue;
            if (!ReferenceEquals(baseline, row) && DataRow.SameContent(baseline, row))
                draft[i] = baseline;
            if (ReferenceEquals(draft[i], baseline))
                shared++;
        }
        return shared;
    }
    public HistoryMove UndoEdit() => History.Move(this, undo: true);
    public HistoryMove RedoEdit() => History.Move(this, undo: false);
}

public sealed class Workspace
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public long StorageRevision { get; set; }
    public string Name { get; set; } = Messages.Define("Text_707DBE0213");
    public RunMode Mode { get; set; }
    public bool LocalRouteTimes { get; set; }
    // Persisted but unused: the API address is fixed (OperationsAdapter.BaseUri). Kept for the saved format.
    public string InstanceUrl { get; set; } = "";
    public string? AirlineId { get; set; }
    public string? ClientId { get; set; }
    public DateTimeOffset? ConnectedAt { get; set; }
    public string? ContractHash { get; set; }
    public List<string> VerifiedOperations { get; set; } = [];
    public Dictionary<ResourceKind, ResourceData> Resources { get; set; } = Enum.GetValues<ResourceKind>().ToDictionary(k => k, _ => new ResourceData());
    public List<BatchJob> Jobs { get; set; } = [];
    ChangeJournal journal = new();
    /// <summary>What the database holds for this workspace, so a save can write only the difference (see ChangeJournal).
    /// A method rather than a property: it is not part of the workspace's data and must stay out of its persisted shape.</summary>
    public ChangeJournal Journal() => journal;
    // Used to stage an atomic rebase. The caller replaces the changed ResourceData;
    // unchanged resources and jobs are shared for read-only serialization.
    public Workspace StageResource(ResourceKind kind, ResourceData data)
    {
        var copy = (Workspace)MemberwiseClone();
        copy.Resources = new(Resources) { [kind] = data };
        copy.VerifiedOperations = [.. VerifiedOperations];
        copy.journal = new();
        return copy;
    }
    // Stages a change to every resource without deep-copying rows, jobs or undo history.
    public Workspace StageResources()
    {
        var copy = (Workspace)MemberwiseClone();
        copy.Resources = Resources.ToDictionary(p => p.Key, p => p.Value.ShallowCopy());
        copy.VerifiedOperations = [.. VerifiedOperations];
        copy.journal = new();
        return copy;
    }
    // A copy for composing a header change (connection settings) without touching this workspace; rows and jobs are shared.
    public Workspace StageHeader()
    {
        var copy = (Workspace)MemberwiseClone();
        copy.VerifiedOperations = [.. VerifiedOperations];
        copy.journal = new();
        return copy;
    }
    public string Serialize() => JsonSerializer.Serialize(this);
    public static Workspace Deserialize(string json) => JsonSerializer.Deserialize<Workspace>(json)!;
}

// Source is null for an ID the user entered and "Automatic" for one the program found itself (E1): exactly one record that
// appeared after the request, matches the change and was created within the request window.
public sealed record RecoveryCandidateAttempt(string RemoteId, DateTimeOffset ProposedAt,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Source = null);

public sealed class ChangeItem
{
    public ChangeItem() { After = new(); Fields = []; }
    internal ChangeItem(DataRow after, Dictionary<string, FieldChange> fields) { After = after; Fields = fields; }
    public Guid Id { get; set; } = Guid.NewGuid();
    public ResourceKind Resource { get; set; }
    public ChangeKind Kind { get; set; }
    public DataRow? Before { get; set; }
    public DataRow After { get; set; }
    public Dictionary<string, FieldChange> Fields { get; set; }
    // The lists below are created on first use: a review builds an item per change and most items never fill them. A
    // saved item writes and reads them as before (an untouched list is written as []).
    List<Guid>? dependencies;
    public List<Guid> Dependencies { get => dependencies ??= []; set => dependencies = value; }
    public ItemState State { get; set; }
    public string? RemoteId { get; set; }
    public Dictionary<string, JsonElement>? ApiExpectedValues { get; set; }
    List<BatchStep>? apiSteps;
    public List<BatchStep> ApiSteps { get => apiSteps ??= []; set => apiSteps = value; }
    // Missing in old saved tasks: retain their original association semantics during recovery.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool RequiresAirlineParameter { get; set; }
    public string? RecoveryCandidateId { get; set; }
    List<RecoveryCandidateAttempt>? recoveryCandidates;
    public List<RecoveryCandidateAttempt> RecoveryCandidates { get => recoveryCandidates ??= []; set => recoveryCandidates = value; }
    [System.Text.Json.Serialization.JsonIgnore]
    public bool CanProposeRecoveryId => State == ItemState.Unknown && Kind == ChangeKind.Create && !CreateCompleted && After.Identity == null;
    [System.Text.Json.Serialization.JsonIgnore]
    public string? EditableRecoveryId => RecoveryCandidateId ?? (CanProposeRecoveryId ? RemoteId : null);
    public void ProposeRecoveryId(string value, string? source = null)
    {
        if (!CanProposeRecoveryId)
            throw new InvalidOperationException(Messages.Define("ApiUnknown"));
        if (!long.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id) || id <= 0)
            throw new FormatException(Messages.Define("ApiInvalid", ("field", "Remote ID")));
        // Earlier versions stored unverified user input in RemoteId.
        if (RemoteId != null && !RecoveryCandidates.Any(a => a.RemoteId == RemoteId))
            RecoveryCandidates.Add(new(RemoteId, DateTimeOffset.UtcNow));
        RemoteId = null;
        RecoveryCandidateId = id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        RecoveryCandidates.Add(new(RecoveryCandidateId, DateTimeOffset.UtcNow, source));
    }
    public bool Rebased { get; set; }
    public bool CreateCompleted { get; set; }
    public bool WriteAccepted { get; set; }
    public string? Diagnostic { get; set; }
    public ApiDiagnostic? DiagnosticDetails { get; set; }
    public ApiDiagnostic? ReadbackDetails { get; set; }
    // G3: the first read-back, kept when it did not show the write and the record was read a second time.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ApiDiagnostic? FirstReadbackDetails { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ApiDeletionVerification? DeletionVerification { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ApiManualResolution? ManualResolution { get; set; }
    public ApiSnapshotComparison? PreflightComparison { get; set; }
    List<ApiFieldDifference>? verificationDifferences;
    public List<ApiFieldDifference> VerificationDifferences { get => verificationDifferences ??= []; set => verificationDifferences = value; }
    public List<string>? PreCreateCandidateIds { get; set; }
    // E1: the records an unanswered create may have made, as the last search found them (none, or more than one, when
    // the program could not bind one itself); offered to the user to choose from.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? FoundCreateCandidates { get; set; }
    // The text of a new item is always the same (the Chinese text of Text_637DA56123): formatted once, not per item.
    static readonly string NewMessage = Messages.Define("Text_637DA56123");
    public string Message { get; set; } = NewMessage;
    public MessageDescriptor? Description { get; set; }
    public void SetMessage(MessageDescriptor description) { Description = description; Message = description.ToString(); }
}
public sealed class BatchJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset Created { get; set; } = DateTimeOffset.UtcNow;
    public RunMode Mode { get; set; }
    public List<ChangeItem> Items { get; set; } = [];
    public string Status { get; set; } = Messages.Define("Text_637DA56123");
    public JobStatus StatusCode { get; set; }
    public MessageDescriptor? Description { get; set; }
    public void SetStatus(JobStatus code, MessageDescriptor description) { StatusCode = code; Description = description; Status = description.ToString(); }
}
public record ResourceCapability(ResourceKind Resource, bool CanRead, bool CanWrite, MessageDescriptor Reason);
public interface ICsvResourceAdapter
{
    CsvTable Parse(string text);
    List<DataRow> Map(CsvTable table, IReadOnlyDictionary<string, string> mapping);
    IReadOnlyList<byte[]> Export(ResourceKind kind, IEnumerable<ChangeItem> changes, int maxBytes = 10 * 1024 * 1024);
}
public interface IChangePlanner { List<ChangeItem> Plan(ResourceKind kind, ResourceData data); }
