namespace VamSys.Core;

/// <summary>
/// One change found by planning, without the copies a <see cref="ChangeItem"/> owns. It refers to the workspace's draft and
/// snapshot rows (rows are never edited in place, edits replace them) and records only the names of the changed fields; the
/// before/after values are read from those rows when asked. <see cref="ToItem"/> builds the full item once, from the same rows,
/// exactly as <see cref="ChangePlanner.PlanWorkspace"/> always did. Reference fields that point at local rows are resolved
/// on a private copy of the row, never on the workspace's row.
/// </summary>
public sealed class PlannedChange
{
    static readonly string[] NoNames = [];
    static readonly Guid[] NoDependencies = [];

    // A planned change owns these; a wrapped item reads everything from the item instead.
    readonly ResourceKind resource;
    readonly ChangeKind kind;
    readonly DataRow? before;
    DataRow after;
    string[] names;
    readonly ChangeItem? existing;
    ChangeItem? item;

    internal PlannedChange(ResourceKind resource, ChangeKind kind, DataRow after, DataRow? before, string[] names)
    {
        this.resource = resource; this.kind = kind; this.after = after; this.before = before; this.names = names;
        Id = Guid.NewGuid();
    }
    PlannedChange(ChangeItem existing)
    {
        this.existing = existing; item = existing; after = existing.After; names = NoNames;
        Id = existing.Id;
    }

    /// <summary>Views an already built item (its fields, rows and dependencies are read from it each time); <see cref="ToItem"/> returns that same object.</summary>
    public static PlannedChange Wrap(ChangeItem item) => new(item);

    public Guid Id { get; }
    public ResourceKind Resource => existing?.Resource ?? resource;
    public ChangeKind Kind => existing?.Kind ?? kind;
    /// <summary>The snapshot row. Read only: it belongs to the workspace.</summary>
    public DataRow? Before => existing is null ? before : existing.Before;
    /// <summary>The row as it would be submitted: the draft row, or a private copy when local references were resolved. Read only.</summary>
    public DataRow After => existing?.After ?? after;
    public bool RequiresAirlineParameter { get => existing?.RequiresAirlineParameter ?? requiresAirlineParameter; internal set => requiresAirlineParameter = value; }
    bool requiresAirlineParameter;
    Guid[] dependencies = NoDependencies;
    public IReadOnlyList<Guid> DependencyIds => existing is null ? dependencies : existing.Dependencies;

    internal void SetDependencies(Guid[] ids) => dependencies = ids;
    /// <summary>Rewrites one reference field on the planned row (a private copy) and marks it as set.</summary>
    internal void Resolve(string field, string value)
    {
        after = after.With(field, value);
        if (Array.IndexOf(names, field) < 0)
            names = [.. names, field];
    }

    public int FieldCount => existing?.Fields.Count ?? names.Length;
    public void CopyFieldNames(string[] destination)
    {
        if (existing != null)
            existing.Fields.Keys.CopyTo(destination, 0);
        else
            names.CopyTo(destination, 0);
    }
    public bool HasField(string name) => existing is null ? Array.IndexOf(names, name) >= 0 : existing.Fields.ContainsKey(name);
    /// <summary>A changed field's intent and new value: a set or clear of the row's value, as the planned item would carry it.</summary>
    public bool TryGetField(string name, out FieldIntent intent, out string? value)
    {
        if (existing != null)
        {
            var found = existing.Fields.TryGetValue(name, out var field);
            intent = field?.Intent ?? default; value = field?.Value;
            return found;
        }
        if (Array.IndexOf(names, name) < 0)
        { intent = default; value = null; return false; }
        value = after.Get(name);
        intent = value == "" ? FieldIntent.Clear : FieldIntent.Set;
        return true;
    }
    /// <summary>The changed fields in the order the item lists them.</summary>
    public IEnumerable<KeyValuePair<string, FieldChange>> Fields()
    {
        if (existing != null)
        {
            foreach (var field in existing.Fields)
                yield return field;
            yield break;
        }
        foreach (var name in names)
        {
            var value = after.Get(name);
            yield return new(name, new(value == "" ? FieldIntent.Clear : FieldIntent.Set, value));
        }
    }

    /// <summary>The item already built for this change, or null.</summary>
    public ChangeItem? BuiltItem => item;
    /// <summary>The full item (copied rows, per-field changes, dependency IDs). Built once; the same object afterwards.</summary>
    public ChangeItem ToItem()
    {
        if (item != null)
            return item;
        var fields = new Dictionary<string, FieldChange>(names.Length, StringComparer.Ordinal);
        foreach (var name in names)
        {
            var value = after.Get(name);
            fields.Add(name, new(value == "" ? FieldIntent.Clear : FieldIntent.Set, value));
        }
        item = new ChangeItem(after, fields)
        {
            Id = Id, Resource = resource, Kind = kind, Before = before,
            RequiresAirlineParameter = requiresAirlineParameter
        };
        // An item's dependency list is created on first use; most changes have none.
        if (dependencies.Length > 0)
            item.Dependencies = [.. dependencies];
        return item;
    }
}
