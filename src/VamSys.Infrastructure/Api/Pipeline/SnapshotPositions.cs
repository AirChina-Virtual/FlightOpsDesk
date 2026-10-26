using VamSys.Core;

namespace VamSys.Infrastructure;

// Removes rows from a snapshot list by position instead of scanning the list for every row (review B7).
//
// The executor replaces one snapshot row per finished item: remove the row with the item's LocalId, append the verified
// row. List.RemoveAll costs a full scan each time, so a job of n items over an n-row snapshot costs n squared
// comparisons. Here each row's position is looked up in a dictionary built once and corrected for the rows removed
// before it (a Fenwick tree counts them), so a removal costs O(log n) plus the list's own shift.
//
// The result is exactly that of RemoveAll followed by Add: removed rows disappear, the others keep their order, appended
// rows go to the end. If the list is not what this index expects (someone else changed it, or LocalIds repeat), it is
// rebuilt or the plain scan is used, so a stale index can never remove the wrong row.
internal sealed class SnapshotPositions(ResourceData data)
{
    List<DataRow>? list;
    Dictionary<Guid, int> originalIndex = [];
    int[] removedTree = [];
    int originalCount;
    int removed;
    int appended;
    bool plainScan;

    public void Remove(Guid localId)
    {
        Ensure();
        var snapshot = data.Snapshot;
        if (plainScan)
        { snapshot.RemoveAll(r => r.LocalId == localId); return; }
        if (originalIndex.TryGetValue(localId, out var index) && !IsRemoved(index))
        {
            var position = index - RemovedBefore(index);
            if (position < snapshot.Count && snapshot[position].LocalId == localId)
            {
                snapshot.RemoveAt(position);
                MarkRemoved(index);
                removed++;
                return;
            }
            // The list does not look like the index says: fall back to the scan and rebuild next time.
            list = null;
            snapshot.RemoveAll(r => r.LocalId == localId);
            return;
        }
        // Not part of the original list: only rows appended by this run can match, and they sit at the end.
        for (var n = snapshot.Count - 1; n >= originalCount - removed && n >= 0; n--)
            if (snapshot[n].LocalId == localId)
            { snapshot.RemoveAt(n); appended--; return; }
    }

    public void Append(DataRow row)
    {
        Ensure();
        data.Snapshot.Add(row);
        appended++;
    }

    // The index is valid while it is built over the same list and that list holds what this index left in it.
    void Ensure()
    {
        var snapshot = data.Snapshot;
        if (list != null && ReferenceEquals(list, snapshot) && snapshot.Count == originalCount - removed + appended)
            return;
        list = snapshot;
        originalCount = snapshot.Count;
        removed = 0;
        appended = 0;
        removedTree = new int[originalCount + 1];
        originalIndex = new Dictionary<Guid, int>(originalCount);
        plainScan = false;
        for (var n = 0; n < originalCount; n++)
            if (!originalIndex.TryAdd(snapshot[n].LocalId, n))
            { plainScan = true; break; }
    }

    bool IsRemoved(int index) => RemovedBefore(index + 1) - RemovedBefore(index) == 1;

    // Rows already removed among the original positions 0 .. index-1.
    int RemovedBefore(int index)
    {
        var sum = 0;
        for (var i = index; i > 0; i -= i & -i)
            sum += removedTree[i];
        return sum;
    }

    void MarkRemoved(int index)
    {
        for (var i = index + 1; i < removedTree.Length; i += i & -i)
            removedTree[i]++;
    }
}
