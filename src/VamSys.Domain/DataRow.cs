using System.Collections;

namespace VamSys.Core;

/// <summary>
/// One resource row. Immutable after construction, enforced by the compiler: <see cref="LocalId"/>,
/// <see cref="Identity"/> and <see cref="RawApiJson"/> are init-only and <see cref="Fields"/> is a read-only view.
/// The change journal detects edits by row-object identity, so an edit is always a replacement:
/// build a changed copy with <see cref="With(string, string)"/>, <see cref="With(IEnumerable{KeyValuePair{string, string}})"/>,
/// <see cref="WithIdentity"/>, <see cref="WithRawApiJson"/> or <see cref="ToBuilder"/>, then replace the row in its list.
/// New rows can still be written with an object initializer (the supplied dictionary is copied).
/// </summary>
public sealed class DataRow
{
    readonly Dictionary<string, string> fields;
    IReadOnlyDictionary<string, string>? view;

    public DataRow() { LocalId = Guid.NewGuid(); fields = new(StringComparer.Ordinal); }
    // Takes ownership of the dictionary (hot paths build it once and hand it over).
    internal DataRow(Guid localId, ResourceIdentity? identity, string? rawApiJson, Dictionary<string, string> fields)
    { LocalId = localId; Identity = identity; RawApiJson = rawApiJson; this.fields = fields; }

    public Guid LocalId { get; init; }
    public ResourceIdentity? Identity { get; init; }
    public string? RawApiJson { get; init; }
    /// <summary>Read-only view of the fields in insertion order. Initialization copies the supplied pairs.</summary>
    public IReadOnlyDictionary<string, string> Fields
    {
        get => view ??= new FieldView(fields);
        init
        {
            fields = new(value?.Count ?? 0, StringComparer.Ordinal);
            if (value != null)
                foreach (var pair in value)
                    fields[pair.Key] = pair.Value;
            view = null;
        }
    }

    public string Get(string key) => fields.GetValueOrDefault(key, "");
    /// <summary>Allocation-free read-only access for hot loops (<c>foreach</c> over <see cref="Fields"/> boxes an enumerator per row).</summary>
    public RowFields FieldEntries() => new(fields);

    /// <summary>Read-only struct view of a row's fields with non-allocating enumeration.</summary>
    public readonly struct RowFields
    {
        readonly Dictionary<string, string> source;
        internal RowFields(Dictionary<string, string> source) => this.source = source;
        public int Count => source.Count;
        public Dictionary<string, string>.KeyCollection Keys => source.Keys;
        public bool ContainsKey(string key) => source.ContainsKey(key);
        public bool TryGetValue(string key, out string value) => source.TryGetValue(key, out value!);
        public Dictionary<string, string>.Enumerator GetEnumerator() => source.GetEnumerator();
    }

    /// <summary>A copy with one field set.</summary>
    public DataRow With(string field, string value)
    {
        var copy = new Dictionary<string, string>(fields, StringComparer.Ordinal) { [field] = value };
        return new(LocalId, Identity, RawApiJson, copy);
    }
    /// <summary>A copy with several fields set (one dictionary copy for all changes).</summary>
    public DataRow With(IEnumerable<KeyValuePair<string, string>> changes)
    {
        var copy = new Dictionary<string, string>(fields, StringComparer.Ordinal);
        foreach (var (key, value) in changes)
            copy[key] = value;
        return new(LocalId, Identity, RawApiJson, copy);
    }
    /// <summary>A copy without one field.</summary>
    public DataRow Without(string field)
    {
        var copy = new Dictionary<string, string>(fields, StringComparer.Ordinal);
        copy.Remove(field);
        return new(LocalId, Identity, RawApiJson, copy);
    }
    // The copies below share the field data: fields are never mutated after construction.
    public DataRow WithIdentity(ResourceIdentity? identity) => new(LocalId, identity, RawApiJson, fields) { view = view };
    public DataRow WithRawApiJson(string? rawApiJson) => new(LocalId, Identity, rawApiJson, fields) { view = view };
    public DataRow WithLocalId(Guid localId) => new(localId, Identity, RawApiJson, fields) { view = view };

    /// <summary>A mutable copy for edits that touch several members or many fields; finish with <see cref="Builder.ToRow"/>.</summary>
    public Builder ToBuilder() => new(this);

    /// <summary>True when both rows serialize to the same JSON: same id, identity, raw API text and fields in the same order.</summary>
    public static bool SameContent(DataRow? a, DataRow? b)
    {
        if (ReferenceEquals(a, b))
            return true;
        if (a is null || b is null || a.LocalId != b.LocalId || a.Identity != b.Identity || a.RawApiJson != b.RawApiJson || a.fields.Count != b.fields.Count)
            return false;
        if (ReferenceEquals(a.fields, b.fields))
            return true;
        using var left = a.fields.GetEnumerator();
        using var right = b.fields.GetEnumerator();
        while (left.MoveNext() && right.MoveNext())
            if (left.Current.Key != right.Current.Key || left.Current.Value != right.Current.Value)
                return false;
        return true;
    }

    /// <summary>
    /// Mutable staging area for one new row. <see cref="ToRow"/> hands the field dictionary over to the row without
    /// copying and ends the builder's life, so each row costs one dictionary however many fields were set.
    /// </summary>
    public sealed class Builder
    {
        Dictionary<string, string>? fields;
        ResourceIdentity? identity;
        string? rawApiJson;
        Guid localId;

        public Builder() : this(Guid.NewGuid(), 0) { }
        public Builder(int capacity) : this(Guid.NewGuid(), capacity) { }
        public Builder(Guid localId, int capacity = 0) { this.localId = localId; fields = new(capacity, StringComparer.Ordinal); }
        internal Builder(DataRow source)
        {
            localId = source.LocalId;
            identity = source.Identity;
            rawApiJson = source.RawApiJson;
            fields = new(source.fields, StringComparer.Ordinal);
        }

        Dictionary<string, string> Open => fields ?? throw new InvalidOperationException("The builder already produced its row.");

        public Guid LocalId { get => localId; set { _ = Open; localId = value; } }
        public ResourceIdentity? Identity { get => identity; set { _ = Open; identity = value; } }
        public string? RawApiJson { get => rawApiJson; set { _ = Open; rawApiJson = value; } }
        public int Count => Open.Count;
        public string Get(string key) => Open.GetValueOrDefault(key, "");
        public bool Has(string key) => Open.ContainsKey(key);
        public string this[string key] { get => Get(key); set => Open[key] = value; }
        public Builder Set(string key, string value) { Open[key] = value; return this; }
        public Builder Remove(string key) { Open.Remove(key); return this; }

        public DataRow ToRow()
        {
            var owned = Open;
            fields = null;
            return new(localId, identity, rawApiJson, owned);
        }
    }

    // Read-only wrapper: no cast reaches the underlying dictionary through the public type.
    sealed class FieldView(Dictionary<string, string> source) : IReadOnlyDictionary<string, string>
    {
        public string this[string key] => source[key];
        public IEnumerable<string> Keys => source.Keys;
        public IEnumerable<string> Values => source.Values;
        public int Count => source.Count;
        public bool ContainsKey(string key) => source.ContainsKey(key);
        public bool TryGetValue(string key, out string value) => source.TryGetValue(key, out value!);
        public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => source.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => source.GetEnumerator();
    }
}
