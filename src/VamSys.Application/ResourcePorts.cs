namespace VamSys.Core;

public interface IResourceReader
{
    IAsyncEnumerable<DataRow> ReadAllAsync(ResourceKind kind, CancellationToken ct);
    Task<DataRow?> FindAsync(ResourceKind kind, string id, CancellationToken ct);
}
public interface IResourceWriter
{
    Task<string?> WriteAsync(ChangeItem change, CancellationToken ct);
}
public interface IBatchExecutor
{
    Task ExecuteAsync(BatchJob job, Func<Task> persist, CancellationToken ct);
}
