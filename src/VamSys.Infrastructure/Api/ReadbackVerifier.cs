using System.Globalization;
using System.Text.Json;
using VamSys.Core;

namespace VamSys.Infrastructure;

internal sealed record ReadbackVerification(bool Passed, List<ApiFieldDifference> Differences);

// Decides whether what the server holds is what the program meant it to hold, and keeps the evidence that explains
// the answer. Two questions, both answered from a GET and never from a write response:
//   Passes      after a write: does the record carry every value the request sent?
//   Unchanged   before a write: does the record still read as it did when the change was planned?
internal sealed class ReadbackVerifier(ApiContext ctx, RequestPlanners planners)
{
    // Read-back after a write. The verdict passes exactly when no reason is listed, so the saved evidence always
    // explains a failed check.
    public ReadbackVerification Verify(ChangeItem item, DataRow? current, DataRow expected, IReadOnlyDictionary<string, JsonElement> values)
    {
        var reasons = new List<ApiFieldDifference>();
        if (current?.RawApiJson is not string json)
        { reasons.Add(new("record", item.RemoteId ?? item.Before?.Identity?.RemoteId, null)); return new(false, reasons); }
        string Aliases() => string.Join(",", IdentityIndex.AirportAliases(current));
        var code = expected.Get("ICAO/IATA");
        if (item.Resource == ResourceKind.Airports && item.Kind == ChangeKind.Create && !IdentityIndex.HasAirportAlias(current, code))
            reasons.Add(new("ICAO/IATA", code, Aliases()));
        if (item.Resource == ResourceKind.Aircraft && (item.Kind == ChangeKind.Create || IndependentApiEvidence.IsMove(item))
            && current.Identity?.ParentFleetId != expected.Get("Fleet ID"))
            reasons.Add(new("fleet_id", expected.Get("Fleet ID"), current.Identity?.ParentFleetId));
        using var raw = JsonDocument.Parse(json);
        foreach (var (wire, wanted) in values)
        {
            if (wire == "icao_iata")
            {
                // The same code as the row's ICAO/IATA is already listed above.
                if (!IdentityIndex.HasAirportAlias(current, wanted.GetString()!) && !reasons.Any(r => r.Field == "ICAO/IATA" && string.Equals(r.Expected, wanted.GetString(), StringComparison.OrdinalIgnoreCase)))
                    reasons.Add(new(wire, wanted.GetRawText(), Aliases()));
                continue;
            }
            if (!raw.RootElement.TryGetProperty(wire, out var actual))
            { reasons.Add(new(wire, wanted.GetRawText(), null)); continue; }
            // A created jumpseat does not keep callsign / flight number, nor fleets (recorded live behaviour; fleets 2026-11-12,
            // route 14217362). New jumpseats are no longer sent with fleets; this settles one that was.
            if (ApiOperationPolicy.IsJumpseatCreate(item) && (wire is "callsign" or "flight_number" or "fleet_ids") && actual.ValueKind == JsonValueKind.Null)
                continue;
            if (!WireEquivalent(item.Resource, wire, actual, wanted))
                reasons.Add(new(wire, wanted.GetRawText(), actual.GetRawText()));
        }
        if (ApiOperationPolicy.IsJumpseatCreate(item) && item.CanProposeRecoveryId)
        {
            // A recovered jumpseat must be new since the request and created within its time window.
            var id = current.Identity!.RemoteId;
            if (item.PreCreateCandidateIds == null)
                reasons.Add(new("pre_create_candidates", "recorded", null));
            else if (item.PreCreateCandidateIds.Contains(id))
                reasons.Add(new("pre_create_candidates", "new record", id));
            var step = item.ApiSteps.FirstOrDefault(s => s.Method == "POST");
            var end = step?.FinishedAt ?? step?.DeadlineAt;
            if (step?.StartedAt == null || end == null)
                reasons.Add(new("request_window", "recorded", null));
            else
            {
                // G5: created_at is the server's clock; the window is moved by how far it is from this machine's.
                var shift = ServerClockOffset(ctx.Transport.LastResponse);
                var (from, to) = (step.StartedAt.Value + shift - RecoveryWindowMargin, end.Value + shift + RecoveryWindowMargin);
                var window = $"{from:O} .. {to:O}";
                var present = raw.RootElement.TryGetProperty("created_at", out var createdAt);
                if (!present || !DateTimeOffset.TryParse(createdAt.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var created)
                    || created < from || created > to)
                    reasons.Add(new("created_at", window, present ? createdAt.GetRawText() : null));
            }
        }
        return new(reasons.Count == 0, reasons);
    }

    internal static readonly TimeSpan RecoveryWindowMargin = TimeSpan.FromSeconds(60);

    // How far the server's clock is ahead of this machine's, from the Date header of a response (zero without one).
    internal static TimeSpan ServerClockOffset(ApiDiagnostic? response)
        => response?.ServerDate is { } server ? server - response.FinishedAt : TimeSpan.Zero;

    // Whether a record carries every value the create sent, leaving out the checks that say whether it is new and
    // when it was made (the caller decides those).
    public bool ContentMatches(ChangeItem item, DataRow current)
    {
        ctx.CheckConnection(current);
        return VerifyPlanned(item, current, item.After).Differences.All(d => d.Field is "pre_create_candidates" or "request_window" or "created_at");
    }

    // The values a write is expected to have left: those saved with the item, or what its plan says now.
    ReadbackVerification VerifyPlanned(ChangeItem item, DataRow current, DataRow expected)
        => Verify(item, current, expected, item.ApiExpectedValues ?? ApiSteps.ExpectedValues(planners.Plan(item)));

    // After a write (before == false) the record must carry the expected values; before a write (before == true) it
    // must still read as the item's Before row. Both reject a record of another VA.
    public bool Matches(ChangeItem item, DataRow current, DataRow expected, bool before)
    {
        ctx.CheckConnection(current);
        ctx.CheckConnection(expected);
        return before ? Unchanged(item, current, expected) : VerifyPlanned(item, current, expected).Passed;
    }

    // Conflict check before a write: only the fields the change touches (or, for a delete, every API field) decide.
    // Server times and dates are compared as the server wrote them, so a stored seconds part a grid hides still counts.
    bool Unchanged(ChangeItem item, DataRow current, DataRow expected)
    {
        if (item.Resource == ResourceKind.Airports && item.Kind == ChangeKind.Create
            && !IdentityIndex.HasAirportAlias(current, expected.Get("ICAO/IATA")))
            return false;
        if (item.Resource == ResourceKind.Aircraft && current.Identity?.ParentFleetId != expected.Identity?.ParentFleetId)
            return false;
        IEnumerable<string> fields = item.Kind == ChangeKind.Delete ? FieldSpecs.Get(item.Resource).WireFields.Select(f => f.Csv) : item.Fields.Keys;
        var relevant = fields.Where(f => FieldSpecs.Get(item.Resource).Find(f)?.Wire != null || Schemas.ReferenceFields(item.Resource).Contains(f)).ToList();
        if (!relevant.All(f => f is "Departure Airport (ICAO/IATA)" or "Arrival Airport (ICAO/IATA)"
            ? ctx.Identity.SameEndpoint(item.Resource, current, expected, f, true)
            : Equivalent(f, current.Get(f), f == "Fleet ID" ? expected.Identity?.ParentFleetId ?? expected.Get(f) : expected.Get(f))))
            return false;
        if (current.RawApiJson != null && expected.RawApiJson != null)
        {
            var actual = RowFacts.Of(current.RawApiJson);
            var original = RowFacts.Of(expected.RawApiJson);
            foreach (var f in relevant)
                if (FieldSpecs.Get(item.Resource).Find(f) is { Wire: { } wire, Type: var type } && (type == FieldType.Time || type == FieldType.Date))
                    if (original.TryTemporal(wire, out _, out var a) && actual.TryTemporal(wire, out _, out var b) && a != b)
                        return false;
        }
        return true;
    }

    // For display only; whether the record is unchanged is decided by Matches(before: true).
    public List<ApiFieldDifference> BeforeDifferences(ChangeItem item, DataRow current)
        => FieldSpecs.Get(item.Resource).WireFields.Select(f => f.Csv).Where(f => !Equivalent(f, item.Before?.Get(f) ?? "", current.Get(f)))
            .Select(f => new ApiFieldDifference(f, item.Before?.Get(f), current.Get(f))).ToList();

    // The read-back of a created record must still hold every value the answered POST sent (not the later steps').
    public bool MatchesCreated(ChangeItem item, DataRow current)
    {
        ctx.CheckConnection(current);
        ctx.CheckConnection(item.After);
        return Verify(item, current, item.After, ApiSteps.ExpectedValues([ApiSteps.ToStep(item.ApiSteps[0])])).Passed;
    }

    // Saves what the last GET returned and why it passed or failed, on the item, so a failed check explains itself.
    public void RecordReadback(ChangeItem item, DataRow? current)
    {
        if (ctx.Transport.LastResponse is { } response)
            item.ReadbackDetails = current?.RawApiJson is string body
                ? ctx.Transport.DescribeReadback(response with { Phase = "Verifying" }, body) : response with { Phase = "Verifying" };
        item.VerificationDifferences = Verify(item, current, item.After, item.ApiExpectedValues ?? new Dictionary<string, JsonElement>()).Differences;
    }

    // Saves how the record read just before the write compared to the planned Before row.
    public void RecordPreflight(ChangeItem item, DataRow? current)
    {
        static string? Timestamp(DataRow? row) => row?.RawApiJson is string text ? RowFacts.Of(text).UpdatedAt : null;
        item.PreflightComparison = new(Timestamp(item.Before), Timestamp(current), FieldSpecs.Get(item.Resource).WireFields.Select(f => f.Csv)
            .Where(f => !Equivalent(f, item.Before?.Get(f) ?? "", current?.Get(f) ?? ""))
            .Select(f => new ApiFieldDifference(f, item.Before?.Get(f), current?.Get(f))).ToArray());
    }

    // Wire values are equal when they mean the same: dates by instant, id arrays as sets (the response may use string
    // ids where requests use integers), everything else structurally.
    static bool WireEquivalent(ResourceKind kind, string wire, JsonElement actual, JsonElement wanted)
    {
        var type = FieldSpecs.Get(kind).FindByWire(wire)?.Type ?? default;
        if (type == FieldType.Date)
            return actual.ValueKind == JsonValueKind.String && wanted.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(actual.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var a)
                && DateTimeOffset.TryParse(wanted.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var b) && a == b;
        if (wanted.ValueKind == JsonValueKind.Array)
        {
            if (actual.ValueKind != JsonValueKind.Array)
                return false;
            // The documented response allows string IDs, while requests require integer IDs.
            string Key(JsonElement value) => (type == FieldType.IdList || type == FieldType.ParameterIdList)
                && long.TryParse(value.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                ? "id:" + id.ToString(CultureInfo.InvariantCulture) : value.ValueKind == JsonValueKind.String ? "string:" + value.GetString() : value.GetRawText();
            var keys = actual.EnumerateArray().Select(Key).ToArray();
            return keys.Distinct().Count() == keys.Length && wanted.EnumerateArray().Select(Key).ToHashSet().SetEquals(keys);
        }
        return JsonElement.DeepEquals(actual, wanted);
    }

    // Grid text equality: set-valued columns compare as sets of trimmed, non-empty entries, everything else as text.
    public static bool Equivalent(string field, string a, string b) => FieldSpecs.IsSetCompared(field)
        ? a.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToHashSet().SetEquals(b.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)) : a == b;
}
