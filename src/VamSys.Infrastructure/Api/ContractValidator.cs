using System.Text.Json;
using VamSys.Core;
using static VamSys.Infrastructure.ApiErrors;

namespace VamSys.Infrastructure;

// Checks request bodies against the Operations API contract that ships inside this assembly (docs/contracts/operations.json):
// property names, types, required properties, enums, lengths, minimums and array limits. Planning builds bodies;
// this class alone decides whether a body is allowed to leave the machine.
internal static class ContractValidator
{
    static readonly JsonElement SchemasJson = JsonDocument.Parse(typeof(ContractValidator).Assembly.GetManifestResourceStream("Operations.OpenApi.json")!)
        .RootElement.GetProperty("components").GetProperty("schemas");

    public static string SchemaName(ResourceKind kind, bool create) => (kind, create) switch
    {
        (ResourceKind.Airports, true) => "CreateAirportData",
        (ResourceKind.Airports, false) => "UpdateAirportData",
        (ResourceKind.Fleets, true) => "CreateFleetData",
        (ResourceKind.Fleets, false) => "UpdateFleetData",
        (ResourceKind.Aircraft, true) => "CreateAircraftData",
        (ResourceKind.Aircraft, false) => "UpdateAircraftData",
        (ResourceKind.Routings, true) => "CreateRoutingData",
        (ResourceKind.Routings, false) => "UpdateRoutingData",
        (ResourceKind.Routes, true) => "StoreRouteRequest",
        _ => "UpdateRouteRequest"
    };

    // Properties the create request of a resource accepts; the rest of a new record's fields go in a second, update request.
    public static JsonElement CreateProperties(ResourceKind kind) => SchemasJson.GetProperty(SchemaName(kind, true)).GetProperty("properties");

    public static void ValidateBody(ResourceKind kind, bool create, Dictionary<string, object?> body)
    {
        ValidateCompatibility(body);
        var schema = SchemasJson.GetProperty(SchemaName(kind, create));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(body));
        ValidateSchema(schema, json.RootElement, SchemaName(kind, create));
    }

    // The aircraft move is a PATCH with a single fleet_id and nothing else.
    public static void ValidateMoveBody(Dictionary<string, object?> body)
    {
        if (body.Count != 1 || !body.ContainsKey("fleet_id"))
            throw Block("ApiMoveSeparate");
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(body));
        ValidateSchema(SchemasJson.GetProperty("MoveAircraftFleetData"), json.RootElement, "MoveAircraftFleetData");
    }

    // Fields the documentation allows but this program never sends (unverified weight unit and SimBrief overrides).
    public static void ValidateCompatibility(IReadOnlyDictionary<string, object?> body)
    {
        if (body.Keys.Any(k => k is "weight_unit" or "simbrief_overrides"))
            throw Block("ApiKnownWeightRestriction");
    }

    static void ValidateSchema(JsonElement schema, JsonElement value, string field)
    {
        if (schema.TryGetProperty("$ref", out var reference))
        { ValidateSchema(SchemasJson.GetProperty(reference.GetString()!.Split('/')[^1]), value, field); return; }
        if (schema.TryGetProperty("type", out var type))
        {
            var types = type.ValueKind == JsonValueKind.Array ? type.EnumerateArray().Select(v => v.GetString()).ToArray() : [type.GetString()];
            string actual = value.ValueKind switch { JsonValueKind.Object => "object", JsonValueKind.Array => "array", JsonValueKind.Number => value.TryGetInt64(out _) ? "integer" : "number", JsonValueKind.True or JsonValueKind.False => "boolean", JsonValueKind.Null => "null", _ => "string" };
            if (!types.Contains(actual))
                throw Block("ApiInvalid", field);
        }
        if (schema.TryGetProperty("enum", out var allowed) && !allowed.EnumerateArray().Any(v => v.ToString() == value.ToString()))
            throw Block("ApiInvalid", field);
        if (value.ValueKind == JsonValueKind.String)
        {
            var length = value.GetString()!.Length;
            if (schema.TryGetProperty("minLength", out var min) && length < min.GetInt32() || schema.TryGetProperty("maxLength", out var max) && length > max.GetInt32())
                throw Block("ApiInvalid", field);
        }
        if (value.ValueKind == JsonValueKind.Number && schema.TryGetProperty("minimum", out var minimum) && value.GetDecimal() < minimum.GetDecimal())
            throw Block("ApiInvalid", field);
        if (value.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out var items))
        { if (schema.TryGetProperty("maxItems", out var max) && value.GetArrayLength() > max.GetInt32()) throw Block("ApiInvalid", field); foreach (var item in value.EnumerateArray()) ValidateSchema(items, item, field); }
        if (value.ValueKind == JsonValueKind.Object && schema.TryGetProperty("properties", out var properties))
        {
            if (schema.TryGetProperty("required", out var required))
                foreach (var r in required.EnumerateArray())
                    if (!value.TryGetProperty(r.GetString()!, out _))
                        throw Block("ApiInvalid", r.GetString());
            foreach (var p in value.EnumerateObject())
            { if (!properties.TryGetProperty(p.Name, out var child)) throw Block("ApiUnsupported", p.Name); ValidateSchema(child, p.Value, p.Name); }
        }
    }
}
