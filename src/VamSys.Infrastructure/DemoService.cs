using VamSys.Core;

namespace VamSys.Infrastructure;

public sealed class DemoService : IResourceReader, IResourceWriter
{
    readonly Dictionary<ResourceKind, Dictionary<string, DataRow>> data;
    int sequence = 10000;
    public DemoService(Workspace w) => data = w.Resources.ToDictionary(p => p.Key, p => p.Value.Snapshot.Where(r => Schemas.Key(p.Key, r) != "").GroupBy(r => Schemas.Key(p.Key, r)).ToDictionary(g => g.Key, g => g.First().Copy()));
    public async IAsyncEnumerable<DataRow> ReadAllAsync(ResourceKind kind, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    { foreach (var r in data[kind].Values.ToArray()) { ct.ThrowIfCancellationRequested(); await Task.Yield(); yield return r.Copy(); } }
    public Task<DataRow?> FindAsync(ResourceKind kind, string id, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); return Task.FromResult(data[kind].GetValueOrDefault(id.ToUpperInvariant())?.Copy()); }
    public async Task<string?> WriteAsync(ChangeItem change, CancellationToken ct)
    {
        await Task.Delay(80, ct); var key = Schemas.Key(change.Resource, change.After);
        if (key == "") key = (++sequence).ToString();
        if (change.Kind == ChangeKind.Delete) data[change.Resource].Remove(key);
        else { var row = change.After.Copy(); row.Fields[Schemas.All[change.Resource].Key] = key; data[change.Resource][key] = row; }
        return key;
    }
    public void RestoreSuccessful(BatchJob job)
    {
        foreach (var item in job.Items.Where(i => i.State == ItemState.Succeeded))
        {
            var key = item.RemoteId ?? Schemas.Key(item.Resource, item.After);
            if (item.Kind == ChangeKind.Delete) data[item.Resource].Remove(key);
            else { var row = item.After.Copy(); row.Fields[Schemas.All[item.Resource].Key] = key; data[item.Resource][key] = row; }
            if (int.TryParse(key, out var n)) sequence = Math.Max(sequence, n);
        }
    }
}
