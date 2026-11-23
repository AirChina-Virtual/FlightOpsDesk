using System.Text;
using System.Text.Json;
using System.Text.Unicode;

namespace VamSys.Core;

/// <summary>
/// Reads a stored row straight from its UTF-8 JSON into the row's own field dictionary. The general way
/// (<c>JsonSerializer.Deserialize&lt;DataRow&gt;</c> on the text as a string) turns the text into UTF-16 and back, builds a
/// dictionary that the row then copies (the row owns its fields) and allocates every field name; on a 50 000-route workspace
/// that is most of the half a gigabyte a read allocated (2026-11-23, stage 7 item 3).
/// <para>Only the exact shape <c>JsonSerializer.Serialize(DataRow)</c> writes is read here: <c>LocalId</c> (a GUID string),
/// <c>Identity</c> (null, or an object with exactly <c>RemoteId</c>, <c>ConnectionId</c> and <c>ParentFleetId</c>, each a
/// string or null), <c>RawApiJson</c> (a string or null) and <c>Fields</c> (an object of strings with no repeated name), each
/// exactly once, nothing else, as valid UTF-8. For anything else <see cref="TryRead"/> returns null and the caller reads the
/// text the general way, so whatever that would make of it (or throw) stays as it was. For the accepted shape the result
/// equals what the general way builds: the same identity, raw JSON, field names (shared as <see cref="FieldNames"/> shares
/// them) and values in the same order, in a dictionary of the same capacity.</para>
/// </summary>
public static class DataRowJson
{
    [ThreadStatic] static List<KeyValuePair<string, string>>? pending;

    // values: when given, equal field values of the rows read with it share one string (the table belongs to one read).
    public static DataRow? TryRead(ReadOnlySpan<byte> utf8, Dictionary<string, string>? values = null)
    {
        if (!Utf8.IsValid(utf8))
            return null;
        try
        { return Read(utf8, values); }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        { return null; }
    }

    static DataRow? Read(ReadOnlySpan<byte> utf8, Dictionary<string, string>? values)
    {
        var reader = new Utf8JsonReader(utf8);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            return null;
        Guid? localId = null;
        bool identityRead = false, rawRead = false;
        ResourceIdentity? identity = null;
        string? raw = null;
        Dictionary<string, string>? fields = null;
        while (true)
        {
            if (!reader.Read())
                return null;
            if (reader.TokenType == JsonTokenType.EndObject)
                break;
            if (reader.TokenType != JsonTokenType.PropertyName || reader.ValueIsEscaped)
                return null;
            if (reader.ValueTextEquals("LocalId"u8))
            {
                if (localId != null || !reader.Read() || reader.TokenType != JsonTokenType.String || !reader.TryGetGuid(out var id))
                    return null;
                localId = id;
            }
            else if (reader.ValueTextEquals("Identity"u8))
            {
                if (identityRead || !reader.Read())
                    return null;
                identityRead = true;
                if (reader.TokenType == JsonTokenType.StartObject)
                {
                    if (ReadIdentity(ref reader) is not { } read)
                        return null;
                    identity = read;
                }
                else if (reader.TokenType != JsonTokenType.Null)
                    return null;
            }
            else if (reader.ValueTextEquals("RawApiJson"u8))
            {
                if (rawRead || !reader.Read() || !ReadText(ref reader, out raw))
                    return null;
                rawRead = true;
            }
            else if (reader.ValueTextEquals("Fields"u8))
            {
                if (fields != null || !reader.Read() || reader.TokenType != JsonTokenType.StartObject || (fields = ReadFields(ref reader, values)) == null)
                    return null;
            }
            else
                return null;
        }
        // Nothing may follow the row (the general way throws on trailing content).
        if (reader.Read() || localId is not { } local || !identityRead || !rawRead || fields == null)
            return null;
        return new DataRow(local, identity, raw, fields);
    }

    // A string or null; false for any other token.
    static bool ReadText(ref Utf8JsonReader reader, out string? text)
    {
        text = null;
        if (reader.TokenType == JsonTokenType.Null)
            return true;
        if (reader.TokenType != JsonTokenType.String)
            return false;
        text = reader.GetString();
        return true;
    }

    static ResourceIdentity? ReadIdentity(ref Utf8JsonReader reader)
    {
        string? remote = null, connection = null, parent = null;
        var seen = 0;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName || reader.ValueIsEscaped)
                return null;
            int bit;
            if (reader.ValueTextEquals("RemoteId"u8)) bit = 1;
            else if (reader.ValueTextEquals("ConnectionId"u8)) bit = 2;
            else if (reader.ValueTextEquals("ParentFleetId"u8)) bit = 4;
            else return null;
            if ((seen & bit) != 0 || !reader.Read() || !ReadText(ref reader, out var value))
                return null;
            seen |= bit;
            if (bit == 1) remote = value; else if (bit == 2) connection = value; else parent = value;
        }
        return reader.TokenType == JsonTokenType.EndObject && seen == 7 ? new ResourceIdentity(remote!, connection!, parent) : null;
    }

    static string Value(ref Utf8JsonReader reader, Dictionary<string, string> values, scoped Span<char> buffer)
    {
        if (reader.ValueIsEscaped || reader.HasValueSequence || reader.ValueSpan.Length > buffer.Length)
            return reader.GetString()!;
        var text = buffer[..Encoding.UTF8.GetChars(reader.ValueSpan, buffer)];
        var lookup = values.GetAlternateLookup<ReadOnlySpan<char>>();
        if (!lookup.TryGetValue(text, out var shared))
            values.Add(shared = text.ToString(), shared);
        return shared;
    }

    // The field object, in order, into a dictionary sized like the one the row's initializer makes (the exact count).
    static Dictionary<string, string>? ReadFields(ref Utf8JsonReader reader, Dictionary<string, string>? values)
    {
        var pairs = pending ??= new(64);
        pairs.Clear();
        Span<char> buffer = stackalloc char[128];
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
                return null;
            string name;
            if (reader.ValueIsEscaped || reader.HasValueSequence || reader.ValueSpan.Length > buffer.Length)
                name = FieldNames.Share(reader.GetString()!);
            else
            {
                var length = Encoding.UTF8.GetChars(reader.ValueSpan, buffer);
                name = FieldNames.Share(buffer[..length]);
            }
            if (!reader.Read() || reader.TokenType != JsonTokenType.String)
                return null;
            pairs.Add(new(name, values == null ? reader.GetString()! : Value(ref reader, values, buffer)));
        }
        if (reader.TokenType != JsonTokenType.EndObject)
            return null;
        var fields = new Dictionary<string, string>(pairs.Count, StringComparer.Ordinal);
        foreach (var pair in pairs)
            if (!fields.TryAdd(pair.Key, pair.Value))
                return null;
        pairs.Clear();
        return fields;
    }
}
