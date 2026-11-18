namespace VamSys.Core;

// The columns hidden from the data page's view menu, per resource, kept in the user's settings (app_settings, like the
// density) so they stay hidden after a restart. The status and key columns are always shown, whatever is stored. A
// stored name the table does not have (a column the contract or field table no longer has, or another workspace's
// imported column) is ignored, and kept: showing every column of this table leaves it for the tables that have it.
public sealed class HiddenColumnsPreference(IUserSettings settings)
{
    readonly Dictionary<ResourceKind, HashSet<string>> stored = [];

    // The status and key columns of a resource, which cannot be hidden.
    public static IReadOnlyList<string> Fixed(ResourceKind kind) => ["_delete", Schemas.All[kind].Key];

    HashSet<string> Stored(ResourceKind kind)
    {
        if (!stored.TryGetValue(kind, out var names))
            stored[kind] = names = new HashSet<string>(settings.LoadHiddenColumns(kind), StringComparer.Ordinal);
        return names;
    }

    // The hidden columns that apply to a table with these columns.
    public IReadOnlySet<string> Hidden(ResourceKind kind, IReadOnlyList<string> columns)
    {
        var names = Stored(kind);
        var fixedColumns = Fixed(kind);
        return columns.Where(c => names.Contains(c) && !fixedColumns.Contains(c)).ToHashSet(StringComparer.Ordinal);
    }

    // The table's columns in their order, without the hidden ones.
    public IReadOnlyList<string> Shown(ResourceKind kind, IReadOnlyList<string> columns)
    {
        var hidden = Hidden(kind, columns);
        return hidden.Count == 0 ? columns.ToList() : columns.Where(c => !hidden.Contains(c)).ToList();
    }

    // Shows or hides one column. The status and key columns are left alone. Saved before it takes effect: a failed save
    // throws and leaves the shown columns as they were.
    public void Show(ResourceKind kind, string column, bool shown, IReadOnlyList<string> columns)
    {
        if (Fixed(kind).Contains(column) || !columns.Contains(column))
            return;
        var next = new HashSet<string>(Stored(kind), StringComparer.Ordinal);
        if (!(shown ? next.Remove(column) : next.Add(column)))
            return;
        Save(kind, next);
    }

    // Shows every column of this table; names it does not have stay stored.
    public void ShowAll(ResourceKind kind, IReadOnlyList<string> columns)
    {
        var next = new HashSet<string>(Stored(kind), StringComparer.Ordinal);
        if (next.RemoveWhere(columns.Contains) > 0)
            Save(kind, next);
    }

    void Save(ResourceKind kind, HashSet<string> next)
    {
        settings.SaveHiddenColumns(kind, next.Order(StringComparer.Ordinal).ToList());
        stored[kind] = next;
    }
}
