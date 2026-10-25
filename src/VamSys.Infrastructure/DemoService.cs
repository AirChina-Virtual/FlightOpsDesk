using VamSys.Core;

namespace VamSys.Infrastructure;

public sealed class DemoService : IResourceReader, IResourceWriter
{
    readonly Dictionary<ResourceKind, Dictionary<string, DataRow>> data;
    int sequence = 10000;
    public DemoService(Workspace w) => data = w.Resources.ToDictionary(p => p.Key, p => p.Value.Snapshot.Where(r => Schemas.Key(p.Key, r) != "").GroupBy(r => Schemas.Key(p.Key, r)).ToDictionary(g => g.Key, g => g.First()));
    public async IAsyncEnumerable<DataRow> ReadAllAsync(ResourceKind kind, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    { foreach (var r in data[kind].Values.ToArray()) { ct.ThrowIfCancellationRequested(); await Task.Yield(); yield return r; } }
    public Task<DataRow?> FindAsync(ResourceKind kind, string id, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); return Task.FromResult(data[kind].GetValueOrDefault(id.ToUpperInvariant())); }
    public async Task<string?> WriteAsync(ChangeItem change, CancellationToken ct)
    {
        await Task.Delay(80, ct);
        var key = Schemas.Key(change.Resource, change.After);
        if (key == "")
            key = (++sequence).ToString();
        if (change.Kind == ChangeKind.Delete)
            data[change.Resource].Remove(key);
        else
        data[change.Resource][key] = change.After.With(Schemas.All[change.Resource].Key, key);
        return key;
    }
    public void RestoreSuccessful(BatchJob job)
    {
        foreach (var item in job.Items.Where(i => i.State == ItemState.Succeeded))
        {
            var key = item.RemoteId ?? Schemas.Key(item.Resource, item.After);
            if (item.Kind == ChangeKind.Delete)
                data[item.Resource].Remove(key);
            else
            data[item.Resource][key] = item.After.With(Schemas.All[item.Resource].Key, key);
            if (int.TryParse(key, out var n))
                sequence = Math.Max(sequence, n);
        }
    }
}
