using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;
using VamSys.Core;

namespace VamSys.Infrastructure;

// One field's evidenced rewrites. Probe names the report entry (record / field / variant) the rule rests on.
internal sealed record NormalisationRule(string Probe, string Evidence)
{
    public bool Trim { get; init; }          // leading and trailing spaces removed
    public bool Upper { get; init; }         // letters upper-cased
    public bool NumberString { get; init; }  // a decimal digit string is stored as that number
    public bool EmptyToNull { get; init; }   // an empty string is stored as null
    public bool LowerDays { get; init; }     // weekday names lower-cased (and sorted Monday..Sunday; lists compare as sets anyway)
    public bool TimeSeconds { get; init; }   // HH:MM is stored as HH:MM:00
    public bool DateOnlyUtc { get; init; }   // a date without time or offset is stored as midnight UTC
    public bool DropFraction { get; init; }  // a date's sub-second part is dropped
}

// G4 (plan 6.1 / 6.3): how the vCCA server rewrites values it accepts, registered field by field from the live write
// evidence, so a read-back that differs only by such a rewrite still verifies. Nothing here is inferred: a field gets a
// rule only for the rewrite the report showed for that very field, and every rule is locked by ServerNormalisationTests.
// Unlisted fields and unlisted rewrites compare strictly and leave the item Unknown.
//
// Deliberately not registered (same report):
//   - a non-UTC offset on a date is dropped while the wall clock is kept (08:00+08:00 is stored as 08:00+00:00). That is
//     another instant; the program only sends +00:00 (RoutePlanner.ConvertTemporal), so it can never be the program's write.
//   - H:MM:SS times, a number for cost_index and any other form of the route string were refused with 422.
//   - tags keep their order and duplicates; cost_index keeps "auto" and "050" as sent; a fleet name keeps its case and
//     inner spaces. These already compare equal and need no rule.
//   - a created aircraft's internal_remarks sent with the POST read back null. That is a lost value, not a rewrite: the
//     create sends them in a PUT after the POST instead (AircraftPlanner); only "" → null is a rewrite (rule above).
internal static class ServerNormalisation
{
    public const string Report = "artifacts/write-evidence/report.json";
    public const string Run = "FODESK-WE 20261118T001624Z";
    const string Evidence = Report + ", run " + Run;
    const string RemarksEvidence = "artifacts/write-evidence/aircraft-remarks.json, run FODESK-WE 20261118T005911Z";

    public static readonly NormalisationRule None = new("", "");

    static NormalisationRule R(string probe) => new(probe, Evidence);

    public static readonly FrozenDictionary<(ResourceKind Kind, string Wire), NormalisationRule> Rules =
        new Dictionary<(ResourceKind, string), NormalisationRule>
        {
            [(ResourceKind.Fleets, "name")] = R("g4-fleet name trim-padding (lowercase and inner-whitespace kept)") with { Trim = true },
            [(ResourceKind.Fleets, "code")] = R("g4-fleet code lowercase") with { Upper = true },
            [(ResourceKind.Fleets, "max_pax")] = R("g4-fleet max_pax number-as-string, decimal-number") with { NumberString = true },
            [(ResourceKind.Fleets, "max_cargo")] = R("g4-fleet max_cargo number-as-string") with { NumberString = true },
            [(ResourceKind.Aircraft, "name")] = R("g4-aircraft name trim-padding") with { Trim = true },
            [(ResourceKind.Aircraft, "registration")] = R("g4-aircraft registration lowercase, trim-padding") with { Trim = true, Upper = true },
            [(ResourceKind.Aircraft, "selcal")] = R("g4-aircraft selcal lowercase") with { Upper = true },
            [(ResourceKind.Aircraft, "hexcode")] = R("g4-aircraft hexcode lowercase") with { Upper = true },
            [(ResourceKind.Aircraft, "passengers")] = R("g4-aircraft passengers number-as-string") with { NumberString = true },
            [(ResourceKind.Aircraft, "cargo")] = R("g4-aircraft cargo number-as-string") with { NumberString = true },
            [(ResourceKind.Aircraft, "internal_remarks")] = new NormalisationRule("aircraftInternalRemarks PUT empty string", RemarksEvidence) { EmptyToNull = true },
            [(ResourceKind.Aircraft, "fin_number")] = R("g4-aircraft fin_number empty-string") with { EmptyToNull = true },
            [(ResourceKind.Routings, "remarks")] = R("g4-routing remarks trim-padding, empty-string") with { Trim = true, EmptyToNull = true },
            [(ResourceKind.Routings, "days_of_operation")] = R("g4-routing days_of_operation array-order, case") with { LowerDays = true },
            [(ResourceKind.Routes, "callsign")] = R("g4-route callsign lowercase") with { Upper = true },
            [(ResourceKind.Routes, "flight_number")] = R("g4-route flight_number lowercase") with { Upper = true },
            [(ResourceKind.Routes, "remarks")] = R("g4-route remarks trim-padding, empty-string") with { Trim = true, EmptyToNull = true },
            [(ResourceKind.Routes, "altitude")] = R("g4-route altitude number-as-string") with { NumberString = true },
            [(ResourceKind.Routes, "service_days")] = R("g4-route service_days array-order, case") with { LowerDays = true },
            [(ResourceKind.Routes, "departure_time")] = R("g4-route departure_time time-without-seconds") with { TimeSeconds = true },
            // arrival_time was only probed with H:MM:SS (refused), so it has no rule; the program always sends HH:MM:SS anyway.
            // "Z" for "+00:00" (start_date date-utc-z) is the same instant and needs no rule.
            [(ResourceKind.Routes, "start_date")] = R("g4-route start_date date-only") with { DateOnlyUtc = true },
            [(ResourceKind.Routes, "end_date")] = R("g4-route end_date date-fraction") with { DropFraction = true },
        }.ToFrozenDictionary();

    public static NormalisationRule For(ResourceKind kind, string wire) => Rules.GetValueOrDefault((kind, wire), None);

    // Whether a scalar read back from the server is what the server makes of the value sent, by this field's rules.
    // The value exactly as sent always counts; the caller compares everything else strictly.
    public static bool Scalar(NormalisationRule rule, JsonElement actual, JsonElement wanted)
    {
        if (wanted.ValueKind != JsonValueKind.String)
            return false;
        var sent = wanted.GetString()!;
        if (rule.EmptyToNull && sent.Length == 0 && actual.ValueKind == JsonValueKind.Null)
            return true;
        if (rule.NumberString && actual.ValueKind == JsonValueKind.Number
            && long.TryParse(sent, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && sent == number.ToString(CultureInfo.InvariantCulture))
            return actual.TryGetInt64(out var stored) && stored == number;
        if (actual.ValueKind != JsonValueKind.String)
            return false;
        var expected = sent;
        if (rule.Trim)
            expected = expected.Trim(' ');
        if (rule.Upper)
            expected = expected.ToUpperInvariant();
        if (rule.TimeSeconds && expected.Length == 5 && TimeOnly.TryParseExact(expected, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            expected += ":00";
        return actual.GetString() == expected;
    }

    // Dates: the same instant, or, by rule, the instant the server stores (a bare date = midnight UTC; sub-second part dropped).
    public static bool Date(NormalisationRule rule, JsonElement actual, JsonElement wanted)
    {
        if (actual.ValueKind != JsonValueKind.String || wanted.ValueKind != JsonValueKind.String
            || !DateTimeOffset.TryParse(actual.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var a))
            return false;
        var sent = wanted.GetString()!;
        if (rule.DateOnlyUtc && DateOnly.TryParseExact(sent, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            return a == new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        // A value without an offset would be read in this machine's zone; the program never sends one, so it never matches.
        if (!DateTime.TryParse(sent, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var kind) || kind.Kind == DateTimeKind.Unspecified
            || !DateTimeOffset.TryParse(sent, CultureInfo.InvariantCulture, DateTimeStyles.None, out var b))
            return false;
        return a == b || rule.DropFraction && a == b.AddTicks(-(b.Ticks % TimeSpan.TicksPerSecond));
    }
}
