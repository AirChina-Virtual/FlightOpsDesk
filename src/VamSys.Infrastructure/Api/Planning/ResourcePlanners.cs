using System.Globalization;
using VamSys.Core;
using static VamSys.Infrastructure.ApiErrors;

namespace VamSys.Infrastructure;

// ---- airports -------------------------------------------------------------------------------------------------

internal sealed class AirportPlanner(ApiContext ctx, RequestPlanners siblings) : RequestPlanner(ctx, siblings, ResourceKind.Airports)
{
    // The key column is sent as icao_iata when the airport is created and can never change afterwards.
    public override bool TryPlanColumn(ChangeItem c, string field, string text, bool create, bool preview, Dictionary<string, object?> body)
    {
        if (field != "ICAO/IATA")
            return false;
        if (!create)
            throw Block("ApiImmutable", field);
        body["icao_iata"] = text;
        return true;
    }
}

// ---- fleets ---------------------------------------------------------------------------------------------------

internal sealed class FleetPlanner(ApiContext ctx, RequestPlanners siblings) : RequestPlanner(ctx, siblings, ResourceKind.Fleets)
{
    // An online create must carry an airline parameter (Parameter IDs, exactly one, create only; see FieldSpecs).
    protected override void CheckBeforeFields(ChangeItem c, bool create)
    {
        if (create && c.RequiresAirlineParameter && c.After.Get("Parameter IDs") == "")
            throw Block("ApiParameterRequired");
    }
}

// ---- aircraft -------------------------------------------------------------------------------------------------

internal sealed class AircraftPlanner(ApiContext ctx, RequestPlanners siblings) : RequestPlanner(ctx, siblings, ResourceKind.Aircraft)
{
    // Live 2026-11-18 (artifacts/write-evidence/aircraft-remarks.json, run FODESK-WE 20261118T005911Z): internal_remarks sent
    // with the POST are not kept (the 201 answer, the single GET and the fleet's aircraft list all read null), while a PUT of
    // that field alone is kept. Leaving it out of the POST also keeps the POST's own read-back check (MatchesCreated) true,
    // which a resumed create needs before it may send the PUT.
    static readonly IReadOnlySet<string> RemarksByPut = new HashSet<string>(StringComparer.Ordinal) { "internal_remarks" };
    protected override IReadOnlySet<string> NotKeptByCreate => RemarksByPut;

    // The fleet is part of the address (fleet/{id}/aircraft), not of the body: a create validates it, an update of it
    // is a move and has its own request (see PlanSpecialOperation).
    public override bool TryPlanColumn(ChangeItem c, string field, string text, bool create, bool preview, Dictionary<string, object?> body)
    {
        if (field != "Fleet ID")
            return false;
        if (!create)
            throw Block("ApiMoveFleet");
        if (preview)
            Ctx.Identity.PreviewReference(text, ResourceKind.Fleets);
        else
            PositiveId(text);
        return true;
    }

    // Moving an aircraft to another fleet is a single PATCH aircraft/{id} {"fleet_id"} and nothing else can ride along.
    protected override ApiStep? PlanSpecialOperation(ChangeItem item, bool preview)
    {
        if (!IndependentApiEvidence.IsMove(item))
            return null;
        if (item.Before?.Identity?.ParentFleetId is null || item.After.Identity?.RemoteId != item.Before.Identity.RemoteId || item.Before.LocalId != item.After.LocalId)
            throw Block("ApiRefreshFirst");
        Ctx.CheckConnection(item.Before!);
        Ctx.CheckConnection(item.After);
        if (item.Fields.Any(p => p.Key != "Fleet ID" && p.Key != "_delete" && p.Value.Intent != FieldIntent.Unspecified))
            throw Block("ApiMoveSeparate");
        var target = item.After.Get("Fleet ID");
        if (Ctx.Workspace.Resources[ResourceKind.Fleets].Draft.Any(r => r.Identity?.RemoteId == target && Schemas.Deleted(r)))
            throw Block("ApiMoveTarget");
        if (item.Fields["Fleet ID"].Intent != FieldIntent.Set || item.Fields["Fleet ID"].Value != target)
            throw Block("ApiInvalid", "Fleet ID");
        if (preview && LocalReference.IsLocal(target))
            Ctx.Identity.PreviewReference(target, ResourceKind.Fleets);
        else
            PositiveId(target);
        if (target == item.Before!.Identity?.ParentFleetId)
            throw Block("ApiNoChanges");
        var fleet = LocalReference.IsLocal(target) ? Ctx.Identity.LocalDraft(ResourceKind.Fleets, target) : Ctx.Identity.Known(ResourceKind.Fleets, target);
        if (fleet is null || Schemas.Deleted(fleet))
            throw Block("ApiMoveTarget");
        Ctx.CheckConnection(fleet);
        if (!LocalReference.IsLocal(target) && fleet.Identity?.RemoteId != target)
            throw Block("ApiMoveTarget");
        var body = new Dictionary<string, object?> { ["fleet_id"] = preview ? Ctx.Identity.PreviewReference(target, ResourceKind.Fleets) : PositiveId(target) };
        ContractValidator.ValidateMoveBody(body);
        return new(HttpMethod.Patch, $"aircraft/{PositiveId(item.Before.Identity!.RemoteId)}", body);
    }
}

// ---- routings and routes: the airports are references -----------------------------------------------------------

internal abstract class EndpointPlanner(ApiContext ctx, RequestPlanners siblings, ResourceKind kind) : RequestPlanner(ctx, siblings, kind)
{
    // Departure / arrival are chosen when the record is created (airport code -> remote airport id) and are immutable.
    // Routes name the property departure_id, routings departure_airport_id.
    public override bool TryPlanColumn(ChangeItem c, string field, string text, bool create, bool preview, Dictionary<string, object?> body)
    {
        if (FieldSpecs.Column(field) is not { Role: var role } || role != FieldRole.Endpoint)
            return false;
        if (!create)
            throw Block("ApiImmutable", field);
        body[(field.StartsWith("Departure") ? "departure" : "arrival") + (c.Resource == ResourceKind.Routes ? "_id" : "_airport_id")] = Ctx.Identity.AirportId(text, preview);
        return true;
    }
}

internal sealed class RoutingPlanner(ApiContext ctx, RequestPlanners siblings) : EndpointPlanner(ctx, siblings, ResourceKind.Routings);

internal sealed class RoutePlanner(ApiContext ctx, RequestPlanners siblings) : EndpointPlanner(ctx, siblings, ResourceKind.Routes)
{
    // Rules that depend on the route type and on what is stored. Jumpseats keep their identity (callsign, flight number)
    // and type. A route that carries fleets may change its fleet set only while its type stays the same: the server keeps
    // fleet_ids for every such type (scheduled proved live; cargo, charter, training, vfr and repositioning are sampled
    // under their own capability key, see ApiOperationPolicy.Key), while a jumpseat keeps none and moving into or out of
    // jumpseat clears them.
    protected override void CheckField(ChangeItem c, FieldSpec spec, string wire, string text, bool create)
    {
        if (wire == "fleet_ids" && !create && !SameFleetCarryingType(c))
            throw Block("ApiFleetSet");
        if (!create && c.Before?.Get("Type") == "jumpseat" && spec.JumpseatReadOnly)
            throw Block("ApiJumpseatIdentity");
        if (wire == "type" && !create && (text == "jumpseat" || c.Before!.Get("Type") == "jumpseat"))
            throw Block("ApiJumpseat");
    }

    static bool SameFleetCarryingType(ChangeItem c)
        => c.Before?.Get("Type") is { } before && before == c.After.Get("Type") && Schemas.RouteTypes.Contains(before) && before != "jumpseat";

    // The grid shows HH:mm and UTC "yyyy-MM-dd HH:mm:ss"; the server stores HH:mm:ss and ISO 8601 with fractions.
    // Seconds and fractions the server already holds are kept when the grid does not show them. Local-time routes
    // cannot be sent: the API takes UTC only.
    protected override object ConvertTemporal(ChangeItem c, FieldSpec spec, string field, string wire, string text)
    {
        if (spec.Type == FieldType.Time)
        {
            if (Ctx.Workspace.LocalRouteTimes)
                throw Block("ApiUtc");
            if (!TimeOnly.TryParseExact(text, new[] { "HH:mm", "HH:mm:ss" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
                throw Block("ApiInvalid", field);
            var seconds = "00";
            if (text.Length == 5 && c.Before?.RawApiJson is string raw && RowFacts.Of(raw).TryTemporal(wire, out var isString, out var stored) && isString && stored.Length == 8)
                seconds = stored[6..];
            return text.Length == 5 ? time.ToString("HH:mm", CultureInfo.InvariantCulture) + ":" + seconds : text;
        }
        if (Ctx.Workspace.LocalRouteTimes || !DateTime.TryParseExact(text, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            throw Block("ApiUtc");
        if (c.Before?.RawApiJson is string before && RowFacts.Of(before).TryTemporal(wire, out _, out var old)
            && DateTimeOffset.TryParse(old, CultureInfo.InvariantCulture, DateTimeStyles.None, out var original))
            date = date.AddTicks(original.Ticks % TimeSpan.TicksPerSecond);
        // Match the documented ISO 8601 example; fractional seconds appear only when the original carried them.
        return new DateTimeOffset(DateTime.SpecifyKind(date, DateTimeKind.Unspecified), TimeSpan.Zero).ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture);
    }

    // What stands in for a jumpseat's empty callsign or flight number: the letters most of the airline's routes start
    // that field with, then "000" (CCA000, CA000). The only values a live jumpseat create was sent with had that shape
    // (CCA9043 / CA9043, 2026-11-16); the server answered with null for both.
    string JumpseatPlaceholder(string field)
    {
        var prefix = Ctx.Workspace.Resources[ResourceKind.Routes].Snapshot
            .Select(r => new string([.. r.Get(field).TakeWhile(char.IsAsciiLetter).Take(3)]).ToUpperInvariant())
            .Where(p => p.Length > 0).GroupBy(p => p)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).FirstOrDefault()?.Key;
        return (prefix ?? "JMP") + "000";
    }

    protected override void CheckBody(ChangeItem c, bool create, Dictionary<string, object?> body)
    {
        // Every route type but jumpseat needs a non-empty fleet set (also on update: no type may be emptied); a jumpseat has none.
        if (create && c.After.Get("Type") != "jumpseat" && !body.ContainsKey("fleet_ids"))
            throw Block("ApiInvalid", "Fleet IDs");
        if (body.ContainsKey("fleet_ids") && c.After.Get("Type") != "jumpseat" && ((long[])body["fleet_ids"]!).Length == 0)
            throw Block("ApiInvalid", "Fleet IDs");
        // A jumpseat has no fleets: the server drops them (2026-11-12), so they are never sent.
        if (c.After.Get("Type") == "jumpseat" && body.TryGetValue("fleet_ids", out var jumpseatFleets) && ((long[])jumpseatFleets!).Length > 0)
            throw Block("ApiInvalid", "Fleet IDs");
        if (!Schemas.RouteTypes.Contains(c.After.Get("Type")))
            throw Block("ApiInvalid", "Type");
        // The create schema requires callsign and flight number, and the server keeps neither for a jumpseat: left
        // empty, they are sent as placeholders.
        var placeholders = create && c.After.Get("Type") == "jumpseat";
        foreach (var (field, wire, min, max) in new[] { ("Callsign", "callsign", 4, 7), ("Flight Number", "flight_number", 3, 6) })
            if (placeholders && c.After.Get(field) == "")
                body[wire] = JumpseatPlaceholder(field);
            else if ((create || c.Fields.ContainsKey(field)) && c.After.Get(field).Length is var length && (length < min || length > max))
                throw Block("ApiInvalid", field);
        if (body.TryGetValue("cost_index", out var costIndex) && !Schemas.ValidCostIndex((string)costIndex!))
            throw Block("ApiInvalid", "Cost Index");
        if (DateTime.TryParseExact(c.After.Get("Start Date"), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)
            && DateTime.TryParseExact(c.After.Get("End Date"), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end) && end <= start)
            throw Block("ApiInvalid", "End Date");
    }
}
