using System.Collections.Frozen;

namespace VamSys.Core;

/// <summary>
/// One string instance per field name for rows built from JSON. Every deserialized row would otherwise own a fresh copy
/// of each of its field names (25 per route row), which on a 50 000-row workspace is a large share of the retained heap
/// and of the work of every full collection.
/// The known column names of the field table are always shared (a fixed set). Other names (imported CSV-only columns)
/// are shared only inside a <see cref="Scope"/>, whose table is dropped when the scope ends, so nothing grows without
/// bound across loads.
/// </summary>
public static class FieldNames
{
    static readonly FrozenDictionary<string, string> known = new[] { FieldSpecs.DeleteMarker.Csv }
        .Concat(FieldSpecs.All.Values.SelectMany(r => r.Fields).Select(f => f.Csv))
        .Distinct(StringComparer.Ordinal).ToFrozenDictionary(n => n, n => n, StringComparer.Ordinal);

    [ThreadStatic] static Dictionary<string, string>? scoped;

    /// <summary>The shared instance of <paramref name="name"/>: the field table's string, the current scope's, or the argument itself.</summary>
    public static string Share(string name)
    {
        if (known.TryGetValue(name, out var shared))
            return shared;
        var table = scoped;
        if (table == null)
            return name;
        if (!table.TryGetValue(name, out shared))
            table.Add(name, shared = name);
        return shared;
    }

    /// <summary>Shares unknown names among the rows read on this thread until the returned scope is disposed. Nested scopes reuse the outer table.</summary>
    public static Scope BeginScope()
    {
        if (scoped != null)
            return new(false);
        scoped = new(StringComparer.Ordinal);
        return new(true);
    }

    public readonly struct Scope : IDisposable
    {
        readonly bool owner;
        internal Scope(bool owner) => this.owner = owner;
        public void Dispose()
        {
            if (owner)
                scoped = null;
        }
    }
}
