using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

// How a read-back value differs from the value sent (G4). Strict JSON equality first; then the kinds a server may
// normalise by. "other" is anything not recognised. Dates are compared as instants, a date without an offset as UTC.
public static class Normalisation
{
    public static string Classify(JsonNode? sent, JsonNode? actual, bool present)
    {
        if (!present)
            return "missing";
        if (JsonNode.DeepEquals(sent, actual))
            return "equal";
        if (sent is JsonArray s)
            return actual is JsonArray a ? Array(s, a) : "other";
        if (sent is JsonArray || actual is JsonArray || sent is JsonObject || actual is JsonObject)
            return "other";
        var (sk, ak) = (sent?.GetValueKind() ?? JsonValueKind.Null, actual?.GetValueKind() ?? JsonValueKind.Null);
        if (sk == JsonValueKind.String && ak == JsonValueKind.Null && sent!.GetValue<string>().Length == 0)
            return "empty-to-null";
        if (sk == JsonValueKind.Null && ak == JsonValueKind.String && actual!.GetValue<string>().Length == 0)
            return "null-to-empty";
        if (sk == JsonValueKind.String && ak == JsonValueKind.String)
            return Text(sent!.GetValue<string>(), actual!.GetValue<string>());
        var (st, at) = (Scalar(sent), Scalar(actual));
        if (Number(st) is { } x && Number(at) is { } y && x == y)
            return sk == ak ? "number-format" : "number-string";
        if (sk is JsonValueKind.True or JsonValueKind.False || ak is JsonValueKind.True or JsonValueKind.False)
            return string.Equals(Bool(st), Bool(at), StringComparison.Ordinal) && Bool(st) != null ? "bool-type" : "other";
        return "other";
    }

    static string Text(string s, string a)
    {
        if (s.Trim() == a)
            return "trim";
        if (string.Equals(s, a, StringComparison.OrdinalIgnoreCase))
            return "case";
        if (string.Equals(s.Trim(), a, StringComparison.OrdinalIgnoreCase))
            return "trim+case";
        if (Collapse(s) == Collapse(a))
            return "whitespace";
        if (string.Equals(Collapse(s), Collapse(a), StringComparison.OrdinalIgnoreCase))
            return "whitespace+case";
        string[] times = ["H:mm", "HH:mm", "H:mm:ss", "HH:mm:ss"];
        if (TimeOnly.TryParseExact(s.Trim(), times, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t1)
            && TimeOnly.TryParseExact(a.Trim(), times, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t2))
            return t1 == t2 ? "time-format" : "other";
        const DateTimeStyles utc = DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces;
        if (s.Contains('-', StringComparison.Ordinal) && a.Contains('-', StringComparison.Ordinal)
            && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, utc, out var d1) && DateTimeOffset.TryParse(a, CultureInfo.InvariantCulture, utc, out var d2))
        {
            if (d1 == d2)
                return "date-format";
            if (d1.DateTime == d2.DateTime)
                return "date-offset-ignored";
            if (d1.UtcDateTime.Date == d2.UtcDateTime.Date && d1.Ticks % TimeSpan.TicksPerSecond != d2.Ticks % TimeSpan.TicksPerSecond
                && d1.AddTicks(-(d1.Ticks % TimeSpan.TicksPerSecond)) == d2.AddTicks(-(d2.Ticks % TimeSpan.TicksPerSecond)))
                return "date-fraction-dropped";
            return "other";
        }
        if (Number(s) is { } x && Number(a) is { } y && x == y)
            return "number-text";
        return "other";
    }

    static string Array(JsonArray s, JsonArray a)
    {
        static string Typed(JsonNode? n) => n?.ToJsonString() ?? "null";
        static string Loose(JsonNode? n) => Scalar(n) ?? "null";
        var (st, at) = (s.Select(Typed).Order(StringComparer.Ordinal).ToList(), a.Select(Typed).Order(StringComparer.Ordinal).ToList());
        if (st.SequenceEqual(at))
            return "array-order";
        if (st.ToHashSet().SetEquals(at))
            return "array-duplicates";
        if (s.Select(Loose).ToHashSet().SetEquals(a.Select(Loose)))
            return "array-element-type";
        if (s.Select(Loose).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(a.Select(Loose)))
            return "array-case";
        return "other";
    }

    static string? Scalar(JsonNode? n) => n is null ? null : n.GetValueKind() == JsonValueKind.String ? n.GetValue<string>() : n.ToJsonString();
    static decimal? Number(string? text) => decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
    static string? Bool(string? text) => text?.Trim().ToLowerInvariant() switch { "true" or "1" => "true", "false" or "0" => "false", _ => null };
    static string Collapse(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
