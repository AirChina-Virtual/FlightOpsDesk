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

public sealed class DataRow
{
    public DataRow() { LocalId=Guid.NewGuid(); Fields=new(StringComparer.Ordinal); }
    // A copy already has its identity and fields; avoid allocating defaults to overwrite.
    DataRow(DataRow source)
    {
        LocalId=source.LocalId; Identity=source.Identity; RawApiJson=source.RawApiJson;
        Fields=new(source.Fields,StringComparer.Ordinal);
    }
    internal DataRow(Guid localId,ResourceIdentity? identity,string? rawApiJson,Dictionary<string,string> fields)
    { LocalId=localId; Identity=identity; RawApiJson=rawApiJson; Fields=fields; }
    public Guid LocalId { get; set; }
    public ResourceIdentity? Identity { get; set; }
    public string? RawApiJson { get; set; }
    public Dictionary<string, string> Fields { get; set; }
    public string Get(string key) => Fields.GetValueOrDefault(key, "");
    public DataRow Copy() => new(this);
}
public sealed record ResourceIdentity(string RemoteId, string ConnectionId, string? ParentFleetId = null);
public sealed record MergeConflict(Guid RowId, string Field, string? Original, string? Local, string? Remote);

[System.Text.Json.Serialization.JsonConverter(typeof(ResourceDataConverter))]
public sealed partial class ResourceData
{
    public List<DataRow> Snapshot { get; set; } = [];
    List<DataRow> draft = [];
    Dictionary<Guid, int>? draftIndex;
    public List<DataRow> Draft { get => draft; set { draft = value; InvalidateDraftIndex(); } }
    public List<string> Columns { get; set; } = [];
    public DateTimeOffset? SnapshotAt { get; set; }
    public EditHistory History { get; set; } = new();
    public IReadOnlyList<HistoryStep> Undo => History.Undo;
    public IReadOnlyList<HistoryStep> Redo => History.Redo;
    public List<MergeConflict> Conflicts { get; set; } = [];
    // New collections over the same row objects. Callers must replace rows, never edit them in place.
    public ResourceData ShallowCopy() => new() { Snapshot = [..Snapshot], Draft = [..Draft], Columns = [..Columns], SnapshotAt = SnapshotAt, History = History.Copy(), Conflicts = [..Conflicts] };
    public void InvalidateDraftIndex() => draftIndex = null;
    internal void EnsureDraftIndex()
    {
        if (draftIndex != null && draftIndex.Count == Draft.Count) return;
        var index = new Dictionary<Guid, int>(Draft.Count);
        for (var i = 0; i < Draft.Count; i++) index.Add(Draft[i].LocalId, i);
        draftIndex = index;
    }
    internal Dictionary<Guid,int> GetDraftPositions() { EnsureDraftIndex(); return draftIndex!; }
    // Used only after history has validated every source and destination position.
    internal void ReplaceIndexedDraft(List<DataRow> rows,Dictionary<Guid,int> positions)
    { draft=rows; draftIndex=positions; }
    public int FindDraftPosition(Guid id)
    {
        EnsureDraftIndex();
        if (!draftIndex!.TryGetValue(id, out var position)) return -1;
        if (Draft[position].LocalId == id) return position;
        InvalidateDraftIndex(); EnsureDraftIndex();
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
            if (!seen.Add(row.LocalId)) throw new ArgumentException("Duplicate replacement LocalId.", nameof(replacements));
            var position = FindDraftPosition(row.LocalId);
            if (position >= 0) prepared.Add((position, row));
        }
        foreach (var (position, row) in prepared) Draft[position] = row;
    }
    public void ClearHistory(bool invalidateIndex = true) { History.Clear(); if (invalidateIndex) InvalidateDraftIndex(); }
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
    public string InstanceUrl { get; set; } = "";
    public string? AirlineId { get; set; }
    public string? ClientId { get; set; }
    public DateTimeOffset? ConnectedAt { get; set; }
    public string? ContractHash { get; set; }
    public List<string> VerifiedOperations { get; set; } = [];
    public Dictionary<ResourceKind, ResourceData> Resources { get; set; } = Enum.GetValues<ResourceKind>().ToDictionary(k => k, _ => new ResourceData());
    public List<BatchJob> Jobs { get; set; } = [];
    // Used to stage an atomic rebase. The caller replaces the changed ResourceData;
    // unchanged resources and jobs are shared for read-only serialization.
    public Workspace StageResource(ResourceKind kind,ResourceData data)
    {
        var copy=(Workspace)MemberwiseClone();
        copy.Resources=new(Resources){[kind]=data};copy.VerifiedOperations=[..VerifiedOperations];
        return copy;
    }
    // Stages a change to every resource without deep-copying rows, jobs or undo history.
    public Workspace StageResources()
    {
        var copy=(Workspace)MemberwiseClone();
        copy.Resources=Resources.ToDictionary(p=>p.Key,p=>p.Value.ShallowCopy());copy.VerifiedOperations=[..VerifiedOperations];
        return copy;
    }
    public string Serialize() => JsonSerializer.Serialize(this);
    public static Workspace Deserialize(string json) => JsonSerializer.Deserialize<Workspace>(json)!;
}

public sealed record RecoveryCandidateAttempt(string RemoteId, DateTimeOffset ProposedAt);

public sealed class ChangeItem
{
    public ChangeItem() { After=new(); Fields=[]; }
    internal ChangeItem(DataRow after, Dictionary<string,FieldChange> fields) { After=after; Fields=fields; }
    public Guid Id { get; set; } = Guid.NewGuid();
    public ResourceKind Resource { get; set; }
    public ChangeKind Kind { get; set; }
    public DataRow? Before { get; set; }
    public DataRow After { get; set; }
    public Dictionary<string, FieldChange> Fields { get; set; }
    public List<Guid> Dependencies { get; set; } = [];
    public ItemState State { get; set; }
    public string? RemoteId { get; set; }
    public Dictionary<string, JsonElement>? ApiExpectedValues { get; set; }
    public List<BatchStep> ApiSteps { get; set; } = [];
    public string? RecoveryCandidateId { get; set; }
    public List<RecoveryCandidateAttempt> RecoveryCandidates { get; set; } = [];
    [System.Text.Json.Serialization.JsonIgnore]
    public bool CanProposeRecoveryId => State==ItemState.Unknown && Kind==ChangeKind.Create && !CreateCompleted && After.Identity==null;
    [System.Text.Json.Serialization.JsonIgnore]
    public string? EditableRecoveryId => RecoveryCandidateId ?? (CanProposeRecoveryId ? RemoteId : null);
    public void ProposeRecoveryId(string value)
    {
        if(!CanProposeRecoveryId) throw new InvalidOperationException(Messages.Define("ApiUnknown"));
        if(!long.TryParse(value,System.Globalization.NumberStyles.None,System.Globalization.CultureInfo.InvariantCulture,out var id) || id<=0)
            throw new FormatException(Messages.Define("ApiInvalid",("field","Remote ID")));
        // Earlier versions stored unverified user input in RemoteId.
        if(RemoteId!=null && !RecoveryCandidates.Any(a=>a.RemoteId==RemoteId)) RecoveryCandidates.Add(new(RemoteId,DateTimeOffset.UtcNow));
        RemoteId=null; RecoveryCandidateId=id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        RecoveryCandidates.Add(new(RecoveryCandidateId,DateTimeOffset.UtcNow));
    }
    public bool Rebased { get; set; }
    public bool CreateCompleted { get; set; }
    public bool WriteAccepted { get; set; }
    public string? Diagnostic { get; set; }
    public string Message { get; set; } = Messages.Define("Text_637DA56123");
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
public interface IResourceReader
{
    IAsyncEnumerable<DataRow> ReadAllAsync(ResourceKind kind, CancellationToken ct);
    Task<DataRow?> FindAsync(ResourceKind kind, string id, CancellationToken ct);
}
public interface IResourceWriter
{
    Task<string?> WriteAsync(ChangeItem change, CancellationToken ct);
}
public interface ICsvResourceAdapter
{
    CsvTable Parse(string text);
    List<DataRow> Map(CsvTable table, IReadOnlyDictionary<string, string> mapping);
    IReadOnlyList<byte[]> Export(ResourceKind kind, IEnumerable<ChangeItem> changes, int maxBytes = 10 * 1024 * 1024);
}
public interface IChangePlanner { List<ChangeItem> Plan(ResourceKind kind, ResourceData data); }
public interface IBatchExecutor
{
    Task ExecuteAsync(BatchJob job, Func<Task> persist, CancellationToken ct);
}
