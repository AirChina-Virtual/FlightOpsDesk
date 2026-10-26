using System.Globalization;
using System.Text.Json;
using VamSys.Core;
using static VamSys.Infrastructure.ApiErrors;

namespace VamSys.Infrastructure;

// The airline parameters (approved callsign / flight number prefixes) a new fleet or a scheduled route is checked
// against. They are read once per session and only used for preview and preflight; the server decides in the end.
internal sealed class AirlineParameters(ApiContext ctx)
{
    AirlineParameter[]? parameters;
    string? parameterConnection;
    readonly HashSet<string> preflightFleets = [];

    public bool HasMetadata => parameters != null && parameterConnection == ctx.ConnectionId;
    public void Invalidate() { parameters = null; parameterConnection = null; preflightFleets.Clear(); }

    public async Task<IReadOnlyList<AirlineParameter>> ReadAsync(CancellationToken ct, bool force = false)
    {
        if (!force && HasMetadata)
            return parameters!;
        var owner = ctx.ConnectionId;
        var rows = new List<AirlineParameter>();
        try
        {
            await foreach (var value in ctx.Transport.ReadPagesAsync(new(OperationsAdapter.BaseUri, "parameters"), ct))
            {
                if (value.TryGetProperty("airline_id", out var va) && va.ToString() != owner)
                    throw Block("ApiWrongVa");
                var id = PositiveId(value.GetProperty("id").ToString()).ToString(CultureInfo.InvariantCulture);
                rows.Add(new(id, owner, CellText(value.GetProperty("icao_code")), CellText(value.GetProperty("iata_code")),
                    value.GetProperty("approved").GetBoolean(), value.GetProperty("restricted").GetBoolean(),
                    value.GetProperty("fleet_ids").EnumerateArray().Select(v => PositiveId(v.ToString()).ToString(CultureInfo.InvariantCulture)).ToArray()));
            }
            if (rows.Select(p => p.Id).Distinct().Count() != rows.Count || owner != ctx.ConnectionId)
                throw Block("ApiWrongVa");
            parameterConnection = owner;
            parameters = rows.ToArray();
            return parameters;
        }
        catch (ApiAccessException error) when (error.Status == 403) { Invalidate(); throw Block("ApiParametersUnavailable"); }
        catch { Invalidate(); throw; }
    }

    public bool NeedsPreflight(ChangeItem item) => item.Kind != ChangeKind.Delete && (
        item.Resource == ResourceKind.Fleets && item.Kind == ChangeKind.Create && (item.RequiresAirlineParameter || item.After.Get("Parameter IDs") != "")
        || item.Resource == ResourceKind.Routes && item.After.Get("Type") == "scheduled"
            && (item.Kind == ChangeKind.Create || item.Fields.Keys.Any(f => f is "Fleet IDs" or "Callsign" or "Flight Number" or "Type")));

    // Offline check against the parameters already read: a new fleet names one approved, unrestricted parameter; a scheduled
    // route's callsign and flight number start with the prefixes of a parameter that is linked to each of its fleets.
    public void ValidatePreview(ChangeItem item)
    {
        if (!NeedsPreflight(item))
            return;
        if (!HasMetadata)
            throw Block("ApiParametersUnavailable");
        var allowed = parameters!.Where(p => p.Approved && !p.Restricted).ToArray();
        if (item.Resource == ResourceKind.Fleets)
        {
            var ids = FleetAssignmentService.Tokens(item.After.Get("Parameter IDs"));
            if (ids.Count != 1 || !allowed.Any(p => p.Id == ids[0]))
                throw Block("ApiParameterRequired");
            return;
        }
        foreach (var token in FleetAssignmentService.Tokens(item.After.Get("Fleet IDs")))
        {
            var local = LocalReference.IsLocal(token);
            var fleet = local ? ctx.Identity.LocalDraft(ResourceKind.Fleets, token) : ctx.Identity.Known(ResourceKind.Fleets, token);
            if (fleet == null || Schemas.Deleted(fleet))
                throw Block("ApiInvalid", "Fleet IDs");
            ctx.CheckConnection(fleet);
            var linked = FleetAssignmentService.Tokens(fleet.Get("Parameter IDs"));
            var explicitRelation = false;
            if (fleet.RawApiJson is string raw)
            { using var json = JsonDocument.Parse(raw); explicitRelation = json.RootElement.TryGetProperty("parameter_ids", out _); }
            if (!allowed.Any(p => (local ? linked.Contains(p.Id) : p.FleetIds.Contains(token) && (!explicitRelation || linked.Contains(p.Id))) && p.IcaoCode.Length > 0 && p.IataCode.Length > 0
                && item.After.Get("Callsign").StartsWith(p.IcaoCode, StringComparison.OrdinalIgnoreCase)
                && item.After.Get("Flight Number").StartsWith(p.IataCode, StringComparison.OrdinalIgnoreCase)))
                throw MessageErrors.Attach(new InvalidOperationException(Messages.Define("ApiPrefixDenied", ("fleet", fleet.Get("Name")), ("callsign", item.After.Get("Callsign")), ("flight", item.After.Get("Flight Number")))),
                    Messages.Define("ApiPrefixDenied", ("fleet", fleet.Get("Name")), ("callsign", item.After.Get("Callsign")), ("flight", item.After.Get("Flight Number"))));
        }
    }

    // Reads the parameters (and the fleets a route names, once each) and validates the item against them.
    public async Task PreflightAsync(ChangeItem item, CancellationToken ct)
    {
        if (!NeedsPreflight(item))
            return;
        await ReadAsync(ct);
        if (item.Resource == ResourceKind.Routes)
            foreach (var id in FleetAssignmentService.Tokens(item.After.Get("Fleet IDs")))
                if (!preflightFleets.Contains(id))
                { if (await ctx.Reader.FindAsync(ResourceKind.Fleets, id, ct) == null) throw Block("ApiInvalid", "Fleet IDs"); preflightFleets.Add(id); }
        ValidatePreview(item);
    }
}
