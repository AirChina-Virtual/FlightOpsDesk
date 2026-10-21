namespace VamSys.Core;

/// <summary>
/// A reference to a draft row that has no remote id yet: the text <c>local:</c> followed by the row's <see cref="DataRow.LocalId"/>
/// in the default Guid format. The prefix is recognised case-insensitively everywhere; <see cref="Format"/> always writes it in lowercase.
/// </summary>
public static class LocalReference
{
    public const string Prefix = "local:";

    public static string Format(Guid localId) => Prefix + localId.ToString();

    public static bool IsLocal(string? token) => token is not null && token.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when any comma-separated token of <paramref name="field"/> carries the prefix (cheap pre-check, no id validation).</summary>
    public static bool ContainsLocal(string? field) => field is not null && field.Contains(Prefix, StringComparison.OrdinalIgnoreCase);

    public static bool TryParse(string? token, out Guid localId)
    {
        localId = default;
        return IsLocal(token) && Guid.TryParse(token![Prefix.Length..], out localId);
    }

    /// <summary>Rewrites the prefix of every token in a comma-separated field to lowercase; everything else is kept byte for byte.</summary>
    public static string Normalize(string? field)
    {
        if (!ContainsLocal(field))
            return field ?? "";
        var parts = field!.Split(',');
        for (int i = 0; i < parts.Length; i++)
        {
            var start = parts[i].Length - parts[i].TrimStart().Length;
            if (parts[i].AsSpan(start).StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
                parts[i] = string.Concat(parts[i].AsSpan(0, start), Prefix, parts[i].AsSpan(start + Prefix.Length));
        }
        return string.Join(',', parts);
    }
}
