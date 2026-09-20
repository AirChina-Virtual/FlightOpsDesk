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
    public Guid LocalId { get; set; } = Guid.NewGuid();
    public ResourceIdentity? Identity { get; set; }
    public string? RawApiJson { get; set; }
    public Dictionary<string, string> Fields { get; set; } = new(StringComparer.Ordinal);
    public string Get(string key) => Fields.GetValueOrDefault(key, "");
    public DataRow Copy() => new() { LocalId = LocalId, Identity = Identity, RawApiJson = RawApiJson, Fields = new(Fields, StringComparer.Ordinal) };
}
public sealed record ResourceIdentity(string RemoteId, string ConnectionId, string? ParentFleetId = null);
public sealed record MergeConflict(Guid RowId, string Field, string? Original, string? Local, string? Remote);

public sealed class ResourceData
{
    public List<DataRow> Snapshot { get; set; } = [];
    public List<DataRow> Draft { get; set; } = [];
    public List<string> Columns { get; set; } = [];
    public DateTimeOffset? SnapshotAt { get; set; }
    public List<List<DataRow>> Undo { get; set; } = [];
    public List<List<DataRow>> Redo { get; set; } = [];
    public List<MergeConflict> Conflicts { get; set; } = [];
    public void Checkpoint()
    {
        Undo.Add(Draft.Select(r => r.Copy()).ToList());
        if (Undo.Count > 20) Undo.RemoveAt(0);
        Redo.Clear();
    }
    public void UndoEdit()
    {
        if (Undo.Count == 0) return;
        Redo.Add(Draft); Draft = Undo[^1]; Undo.RemoveAt(Undo.Count - 1);
    }
    public void RedoEdit()
    {
        if (Redo.Count == 0) return;
        Undo.Add(Draft); Draft = Redo[^1]; Redo.RemoveAt(Redo.Count - 1);
    }
}

public sealed class Workspace
{
    public Guid Id { get; set; } = Guid.NewGuid();
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
    public string Serialize() => JsonSerializer.Serialize(this);
    public static Workspace Deserialize(string json) => JsonSerializer.Deserialize<Workspace>(json)!;
}

public sealed class ChangeItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public ResourceKind Resource { get; set; }
    public ChangeKind Kind { get; set; }
    public DataRow? Before { get; set; }
    public DataRow After { get; set; } = new();
    public Dictionary<string, FieldChange> Fields { get; set; } = [];
    public List<Guid> Dependencies { get; set; } = [];
    public ItemState State { get; set; }
    public string? RemoteId { get; set; }
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
