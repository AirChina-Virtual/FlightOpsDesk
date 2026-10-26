using System.Runtime.CompilerServices;
using System.Text.Json;
using VamSys.Core;

namespace VamSys.Infrastructure;

// The few values the API layer reads again and again from a row's RawApiJson: airport aliases, the four endpoint ids,
// updated_at and the raw text of the time and date columns. Parsing the JSON for each lookup cost one full parse per
// alias, endpoint or time comparison; now a row's text is parsed once.
//
// Rows are immutable and copies made by WithIdentity / WithLocalId share the same RawApiJson text, so the cache is keyed
// by that string's reference. The table drops an entry when its text is collected; nothing here outlives the rows.
internal sealed class RowFacts
{
    static readonly ConditionalWeakTable<string, RowFacts> Cache = new();
    // Columns whose raw wire text is compared or preserved: every Time and Date column of the field table.
    static readonly string[] TemporalWires = FieldSpecs.All.Values.SelectMany(r => r.WireFields)
        .Where(f => f.Type == FieldType.Time || f.Type == FieldType.Date).Select(f => f.Wire!).Distinct().ToArray();

    readonly (bool Present, bool IsString, string Text)[] temporal;

    RowFacts(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var aliases = new List<string>(2);
        foreach (var key in new[] { "icao", "iata" })
            if (root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()))
                aliases.Add(v.GetString()!);
        Aliases = aliases.ToArray();
        DepartureId = Text(root, "departure_id");
        ArrivalId = Text(root, "arrival_id");
        DepartureAirportId = Text(root, "departure_airport_id");
        ArrivalAirportId = Text(root, "arrival_airport_id");
        UpdatedAt = Text(root, "updated_at");
        temporal = new (bool, bool, string)[TemporalWires.Length];
        for (var n = 0; n < TemporalWires.Length; n++)
            if (root.TryGetProperty(TemporalWires[n], out var t))
                temporal[n] = (true, t.ValueKind == JsonValueKind.String, t.ToString());
    }

    static string? Text(JsonElement root, string name) => root.TryGetProperty(name, out var v) ? v.ToString() : null;

    public static RowFacts Of(string rawApiJson) => Cache.GetValue(rawApiJson, static json => new RowFacts(json));

    // icao and iata as sent by the server, blank ones left out.
    public string[] Aliases { get; }
    public string? DepartureId { get; }
    public string? ArrivalId { get; }
    public string? DepartureAirportId { get; }
    public string? ArrivalAirportId { get; }
    public string? UpdatedAt { get; }

    // The value of a route (departure_id) or routing (departure_airport_id) endpoint, null when the property is missing.
    public string? Endpoint(string wire) => wire switch
    {
        "departure_id" => DepartureId,
        "arrival_id" => ArrivalId,
        "departure_airport_id" => DepartureAirportId,
        "arrival_airport_id" => ArrivalAirportId,
        _ => throw new ArgumentOutOfRangeException(nameof(wire))
    };

    // The raw text of a time or date property, with whether the server sent it as a JSON string.
    public bool TryTemporal(string wire, out bool isString, out string text)
    {
        var at = Array.IndexOf(TemporalWires, wire);
        if (at < 0 || !temporal[at].Present)
        { isString = false; text = ""; return false; }
        (_, isString, text) = temporal[at];
        return true;
    }
}
