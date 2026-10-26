using VamSys.Core;

namespace VamSys.Infrastructure;

// Items of one job by Id and by the LocalId of their row, built once when the run starts. Dependencies and local
// references used to be found by scanning the whole job for every item (quadratic in the size of the job).
// Lookups keep the semantics of LINQ Single: exactly one match, otherwise InvalidOperationException.
internal sealed class ItemIndex
{
    readonly Dictionary<Guid, ChangeItem> byId = [];
    readonly Dictionary<Guid, ChangeItem> byLocalId = [];
    readonly HashSet<Guid> repeatedIds = [];
    readonly HashSet<Guid> repeatedLocalIds = [];

    public ItemIndex(IEnumerable<ChangeItem> items)
    {
        foreach (var item in items)
        {
            if (!byId.TryAdd(item.Id, item))
                repeatedIds.Add(item.Id);
            if (!byLocalId.TryAdd(item.After.LocalId, item))
                repeatedLocalIds.Add(item.After.LocalId);
        }
    }

    public ChangeItem ById(Guid id) => Single(byId, repeatedIds, id);
    public ChangeItem ByLocalId(Guid localId) => Single(byLocalId, repeatedLocalIds, localId);

    static ChangeItem Single(Dictionary<Guid, ChangeItem> items, HashSet<Guid> repeated, Guid key)
    {
        if (!items.TryGetValue(key, out var item))
            throw new InvalidOperationException("Sequence contains no matching element");
        if (repeated.Contains(key))
            throw new InvalidOperationException("Sequence contains more than one matching element");
        return item;
    }
}
