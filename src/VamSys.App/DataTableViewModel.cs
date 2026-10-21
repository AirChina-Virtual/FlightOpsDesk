using System.Collections.Immutable;

namespace VamSys.App;

// Immutable query input contains strings and scalar values only; no live DataRow
// or Workspace is accessed by the worker thread.
public readonly record struct DataQueryRow(Guid LocalId, ImmutableArray<string> Values, string SortValue, bool Modified, bool Deleted);
public sealed record DataQueryResult(Guid[] RowIds, int ModifiedCount);

public sealed class DataTableViewModel
{
    Guid[] results = [];
    public IReadOnlyList<Guid> ResultIds => results;
    public HashSet<Guid> Selected { get; } = [];
    public int Page { get; private set; } = 1;
    public int PageSize { get; private set; } = 200;
    public int Total => results.Length;
    public int PageCount => Math.Max(1, (Total + PageSize - 1) / PageSize);
    public int First => Total == 0 ? 0 : (Page - 1) * PageSize + 1;
    public int Last => Math.Min(Page * PageSize, Total);
    public int ModifiedCount { get; private set; }
    public bool IsDirty { get; private set; } = true;
    public int QueryBuilds { get; private set; }
    public string AppliedSearch { get; private set; } = "";
    public int AppliedFilter { get; private set; }
    public string? AppliedSort { get; private set; }

    public void Invalidate() => IsDirty = true;
    public Guid[] PageIds() => results.AsSpan((Page - 1) * PageSize, Math.Max(0, Last - First + (Total == 0 ? 0 : 1))).ToArray();
    public void SetPage(int page) => Page = Math.Clamp(page, 1, PageCount);
    public void SetPageSize(int size)
    {
        if (size is not (100 or 200 or 500))
            throw new ArgumentOutOfRangeException(nameof(size));
        var anchor = (Page - 1) * PageSize;
        PageSize = size;
        SetPage(anchor / size + 1);
    }
    public void Apply(DataQueryResult result, string search, int filter, string? sort)
    {
        var changedFilter = search != AppliedSearch || filter != AppliedFilter;
        results = result.RowIds;
        ModifiedCount = result.ModifiedCount;
        if (changedFilter)
        { Selected.Clear(); Page = 1; }
        else
            Selected.IntersectWith(results);
        AppliedSearch = search;
        AppliedFilter = filter;
        AppliedSort = sort;
        SetPage(Page);
        IsDirty = false;
        QueryBuilds++;
    }
    public void SelectPage(IEnumerable<Guid> ids)
    {
        Selected.ExceptWith(PageIds());
        Selected.UnionWith(ids);
    }
    public void SelectAll() => Selected.UnionWith(results);
    public static DataQueryResult Query(IReadOnlyList<DataQueryRow> rows, string search, int filter, bool sort, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var matches = new List<int>(rows.Count);
        var modified = 0;
        for (var i = 0; i < rows.Count; i++)
        {
            if ((i & 255) == 0)
                ct.ThrowIfCancellationRequested();
            var row = rows[i];
            if (row.Modified)
                modified++;
            if (filter == 1 && !row.Modified || filter == 2 && !row.Deleted)
                continue;
            if (search.Length > 0 && !row.Values.Any(v => v.Contains(search, StringComparison.OrdinalIgnoreCase)))
                continue;
            matches.Add(i);
        }
        if (sort)
            matches.Sort((a, b) =>
        {
            var order = StringComparer.OrdinalIgnoreCase.Compare(rows[a].SortValue, rows[b].SortValue);
            return order == 0 ? a.CompareTo(b) : order;
        });
        ct.ThrowIfCancellationRequested();
        var ids = new Guid[matches.Count];
        for (var i = 0; i < ids.Length; i++)
            ids[i] = rows[matches[i]].LocalId;
        return new(ids, modified);
    }
}
