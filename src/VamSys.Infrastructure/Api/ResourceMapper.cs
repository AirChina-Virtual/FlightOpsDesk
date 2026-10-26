using System.Globalization;
using System.Text.Json;
using VamSys.Core;
using static VamSys.Infrastructure.ApiErrors;

namespace VamSys.Infrastructure;

// JSON from the server -> one grid row. The columns that carry an API field come from the field table (FieldSpecs);
// what differs per resource is how the row is identified and which columns are not plain fields:
//   airports  key column "ICAO/IATA" instead of an ID
//   aircraft  belong to a fleet (identity parent, "Fleet ID" column)
//   routings  departure / arrival airport columns resolved from airport ids
//   routes    same, and the server documents no airline_id on a route
// The result is stored in the identity index so later references resolve without another request.
internal abstract class ResourceMapper(ApiContext ctx, ResourceKind kind)
{
    public ResourceKind Kind => kind;
    protected ApiContext Ctx { get; } = ctx;

    public DataRow FromJson(JsonElement value)
    {
        var id = value.GetProperty("id").ToString();
        var airline = Ctx.Owner(value) ?? OwnerlessAirline();
        var parent = ParentFleetId(value);
        var row = new DataRow.Builder(16) { Identity = new(id, airline, parent), RawApiJson = value.GetRawText() };
        row["_delete"] = "FALSE";
        AddKeyColumn(row, value, id);
        foreach (var spec in FieldSpecs.Get(kind).WireFields)
            if (value.TryGetProperty(spec.Wire!, out var v))
                row[spec.Csv] = CellValue(spec, CellText(v));
        AddReferenceColumns(row, value, parent);
        var result = row.ToRow();
        Ctx.Identity.Remember(kind, id, result);
        return result;
    }

    // A record without airline_id: only routes are allowed to omit it.
    protected virtual string OwnerlessAirline() => throw Block("ApiInvalid", "airline_id");
    protected virtual string? ParentFleetId(JsonElement value) => null;
    protected virtual void AddKeyColumn(DataRow.Builder row, JsonElement value, string id) => row["ID"] = id;
    protected virtual string CellValue(FieldSpec spec, string text) => text;
    protected virtual void AddReferenceColumns(DataRow.Builder row, JsonElement value, string? parent) { }
}

internal sealed class AirportMapper(ApiContext ctx) : ResourceMapper(ctx, ResourceKind.Airports)
{
    // The key is the ICAO code, or the IATA code when the airport has none.
    protected override void AddKeyColumn(DataRow.Builder row, JsonElement value, string id)
        => row["ICAO/IATA"] = value.TryGetProperty("icao", out var icao) && icao.ValueKind == JsonValueKind.String ? icao.GetString()! : CellText(value.GetProperty("iata"));
}

internal sealed class FleetMapper(ApiContext ctx) : ResourceMapper(ctx, ResourceKind.Fleets);

internal sealed class AircraftMapper(ApiContext ctx) : ResourceMapper(ctx, ResourceKind.Aircraft)
{
    protected override string? ParentFleetId(JsonElement value) => value.GetProperty("fleet_id").ToString();
    protected override void AddReferenceColumns(DataRow.Builder row, JsonElement value, string? parent) => row["Fleet ID"] = parent!;
}

// Routings and routes point at their airports by remote id; the grid shows the airport code when that airport is known.
internal abstract class EndpointMapper(ApiContext ctx, ResourceKind kind) : ResourceMapper(ctx, kind)
{
    protected override void AddReferenceColumns(DataRow.Builder row, JsonElement value, string? parent)
    {
        foreach (var side in new[] { "Departure", "Arrival" })
        {
            var wire = side.ToLowerInvariant() + (Kind == ResourceKind.Routes ? "_id" : "_airport_id");
            var airportId = value.GetProperty(wire).ToString();
            var airport = Ctx.Identity.Known(ResourceKind.Airports, airportId);
            row[side + " Airport (ICAO/IATA)"] = airport?.Get("ICAO/IATA") ?? "unresolved:" + airportId;
        }
    }
}

internal sealed class RoutingMapper(ApiContext ctx) : EndpointMapper(ctx, ResourceKind.Routings);

internal sealed class RouteMapper(ApiContext ctx) : EndpointMapper(ctx, ResourceKind.Routes)
{
    // RouteData documents no airline_id; the credentials belong to exactly one VA.
    protected override string OwnerlessAirline() => Ctx.Workspace.AirlineId ?? Ctx.SessionAirlineId ?? throw Block("ApiConnectFirst");

    // The server sends times as HH:mm:ss and dates as ISO 8601 with an offset; the grid shows HH:mm and UTC "yyyy-MM-dd HH:mm:ss".
    // The planner restores the seconds and fractions from RawApiJson when the value is sent back.
    protected override string CellValue(FieldSpec spec, string text)
    {
        if (spec.Type == FieldType.Time && text.Length == 8)
            return text[..5];
        if (spec.Type == FieldType.Date && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return date.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        return text;
    }
}

internal sealed class ResourceMappers
{
    readonly ResourceMapper[] mappers;

    public ResourceMappers(ApiContext ctx)
    {
        mappers = new ResourceMapper[Enum.GetValues<ResourceKind>().Length];
        foreach (var mapper in new ResourceMapper[] { new AirportMapper(ctx), new FleetMapper(ctx), new AircraftMapper(ctx), new RoutingMapper(ctx), new RouteMapper(ctx) })
            mappers[(int)mapper.Kind] = mapper;
    }

    public ResourceMapper For(ResourceKind kind) => mappers[(int)kind];
}
