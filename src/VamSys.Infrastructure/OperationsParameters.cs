using System.Text.Json;
using VamSys.Core;

namespace VamSys.Infrastructure;

public sealed partial class OperationsAdapter
{
    AirlineParameter[]? parameters;
    string? parameterConnection;
    readonly HashSet<string> preflightFleets = [];
    public bool HasParameterMetadata => parameters != null && parameterConnection == ConnectionId;
    public void InvalidateParameters() { parameters = null; parameterConnection = null; preflightFleets.Clear(); }
    public async Task<IReadOnlyList<AirlineParameter>> ReadParametersAsync(CancellationToken ct, bool force = false)
    {
        if (!force && HasParameterMetadata)
            return parameters!;
        var owner = ConnectionId;
        var rows = new List<AirlineParameter>();
        try
        {
            await foreach (var value in transport.ReadPagesAsync(new(BaseUri, "parameters"), ct))
            {
                if (value.TryGetProperty("airline_id", out var va) && va.ToString() != owner)
                    throw Block("ApiWrongVa");
                var id = PositiveId(value.GetProperty("id").ToString()).ToString(System.Globalization.CultureInfo.InvariantCulture);
                rows.Add(new(id, owner, Value(value.GetProperty("icao_code")), Value(value.GetProperty("iata_code")),
                    value.GetProperty("approved").GetBoolean(), value.GetProperty("restricted").GetBoolean(),
                    value.GetProperty("fleet_ids").EnumerateArray().Select(v => PositiveId(v.ToString()).ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray()));
            }
            if (rows.Select(p => p.Id).Distinct().Count() != rows.Count || owner != ConnectionId)
                throw Block("ApiWrongVa");
            parameterConnection = owner;
            parameters = rows.ToArray();
            return parameters;
        }
        catch (ApiAccessException error) when (error.Status == 403) { InvalidateParameters(); throw Block("ApiParametersUnavailable"); }
        catch { InvalidateParameters(); throw; }
    }
    public bool NeedsParameterPreflight(ChangeItem item) => item.Kind != ChangeKind.Delete && (
        item.Resource == ResourceKind.Fleets && item.Kind == ChangeKind.Create && (item.RequiresAirlineParameter || item.After.Get("Parameter IDs") != "")
        || item.Resource == ResourceKind.Routes && item.After.Get("Type") == "scheduled"
            && (item.Kind == ChangeKind.Create || item.Fields.Keys.Any(f => f is "Fleet IDs" or "Callsign" or "Flight Number" or "Type")));

    public void ValidateParameterPreview(ChangeItem item)
    {
        if (!NeedsParameterPreflight(item))
            return;
        if (!HasParameterMetadata)
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
            var fleet = local ? LocalDraft(ResourceKind.Fleets, token) : Known(ResourceKind.Fleets, token);
            if (fleet == null || Schemas.Deleted(fleet))
                throw Block("ApiInvalid", "Fleet IDs");
            CheckConnection(fleet);
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
    public async Task PreflightParametersAsync(ChangeItem item, CancellationToken ct)
    {
        if (!NeedsParameterPreflight(item))
            return;
        await ReadParametersAsync(ct);
        if (item.Resource == ResourceKind.Routes)
            foreach (var id in FleetAssignmentService.Tokens(item.After.Get("Fleet IDs")))
                if (!preflightFleets.Contains(id))
                { if (await FindAsync(ResourceKind.Fleets, id, ct) == null) throw Block("ApiInvalid", "Fleet IDs"); preflightFleets.Add(id); }
        ValidateParameterPreview(item);
    }

    internal void RecordReadback(ChangeItem item, DataRow? current)
    {
        if (transport.LastResponse is { } response)
            item.ReadbackDetails = current?.RawApiJson is string body
            ? transport.DescribeReadback(response with { Phase = "Verifying" }, body) : response with { Phase = "Verifying" };
        item.VerificationDifferences = VerifyReadback(item, current, item.After, item.ApiExpectedValues ?? new Dictionary<string, JsonElement>()).Differences;
    }
    internal void RecordPreflight(ChangeItem item, DataRow? current)
    {
        string? Timestamp(DataRow? row)
        {
            if (row?.RawApiJson is not string text)
                return null;
            using var json = JsonDocument.Parse(text);
            return json.RootElement.TryGetProperty("updated_at", out var value) ? value.ToString() : null;
        }
        item.PreflightComparison = new(Timestamp(item.Before), Timestamp(current), Maps[item.Resource].Keys
            .Where(f => !Equivalent(f, item.Before?.Get(f) ?? "", current?.Get(f) ?? ""))
            .Select(f => new ApiFieldDifference(f, item.Before?.Get(f), current?.Get(f))).ToArray());
    }
}
