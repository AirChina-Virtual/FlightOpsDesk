using System.Collections.Frozen;

namespace VamSys.Core;

// How a column's text is read, validated and sent. Text has no format of its own. These are value types with
// named instances, not enums, because the enum inventory locks every enum in the assembly.
public readonly record struct FieldType(int Id)
{
    public static readonly FieldType Text = new(0);
    public static readonly FieldType Integer = new(1);          // non-negative whole number on the wire
    public static readonly FieldType KgInteger = new(2);        // Integer that must also be whole kg in online mode (validated)
    public static readonly FieldType Bool = new(3);             // TRUE / FALSE in the grid, true / false on the wire
    public static readonly FieldType Date = new(4);             // "yyyy-MM-dd HH:mm:ss" in the grid, ISO 8601 with offset on the wire; sub-second part preserved
    public static readonly FieldType Time = new(5);             // HH:mm in the grid, HH:mm:ss on the wire; stored seconds preserved
    public static readonly FieldType IdList = new(6);           // comma separated positive IDs, sent as an integer array
    public static readonly FieldType ParameterIdList = new(7);  // exactly one positive ID, sent as a string array
    public static readonly FieldType TagList = new(8);          // comma separated strings, sent as a string array
    public static readonly FieldType DayList = new(9);          // comma separated weekday names, sent as a string array
}

// Role of the column inside the resource. Everything else is plain data.
public readonly record struct FieldRole(int Id)
{
    public static readonly FieldRole Data = new(0);
    public static readonly FieldRole Key = new(1);
    public static readonly FieldRole Endpoint = new(2);
    public static readonly FieldRole Parent = new(3);
}

// Membership in IndependentApiEvidence. Create / Update apply to every item of the resource; the other flags
// need the context named in their comment.
public readonly record struct EvidenceScope(int Bits)
{
    public static readonly EvidenceScope None = new(0);
    public static readonly EvidenceScope Create = new(1);
    public static readonly EvidenceScope Update = new(2);
    public static readonly EvidenceScope UpdateMove = new(4);         // only when the update is an aircraft fleet move
    public static readonly EvidenceScope CreateScheduled = new(8);    // only for a scheduled route create
    public static readonly EvidenceScope CreateJumpseat = new(16);    // only for a jumpseat route create
    public static readonly EvidenceScope UpdateScheduled = new(32);   // only for a scheduled route update
    public static EvidenceScope operator |(EvidenceScope a, EvidenceScope b) => new(a.Bits | b.Bits);
    public bool Has(EvidenceScope flag) => (Bits & flag.Bits) != 0;
}

// ClearsToEmptyArray: an empty value in an online update sends [] (otherwise clearing is blocked).
public sealed record FieldSpec(
    string Csv,
    string? Wire = null,
    FieldType Type = default,
    bool Required = false,
    bool ClearsToEmptyArray = false,
    bool SetCompare = false,
    bool Immutable = false,
    bool CreateOnly = false,
    bool JumpseatReadOnly = false,
    FieldRole Role = default,
    ResourceKind? Reference = null,
    EvidenceScope Evidence = default);
public sealed record ResourceSpec(ResourceKind Kind, string Key, IReadOnlyList<FieldSpec> Fields)
{
    public IReadOnlyDictionary<string, FieldSpec> ByCsv { get; } = Fields.ToFrozenDictionary(f => f.Csv, StringComparer.Ordinal);
    // The key column is listed first; required columns precede optional ones, as in the grid.
    public IReadOnlyList<string> Required { get; } = Fields.Where(f => f.Required).Select(f => f.Csv).ToArray();
    public IReadOnlyList<string> Optional { get; } = Fields.Where(f => !f.Required && f.Csv != Key).Select(f => f.Csv).ToArray();
    // Columns with an API field, in wire reading order.
    public IReadOnlyList<FieldSpec> WireFields { get; } = Fields.Where(f => f.Wire != null).ToArray();
    // Historic order: fleet references are resolved before airport references.
    public IReadOnlyList<FieldSpec> References { get; } = Fields.Where(f => f.Reference != null).OrderBy(f => f.Reference == ResourceKind.Fleets ? 0 : 1).ToArray();
    public IReadOnlyList<FieldSpec> Endpoints { get; } = Fields.Where(f => f.Role == FieldRole.Endpoint).ToArray();
    public FieldSpec? Find(string csv) => ByCsv.GetValueOrDefault(csv);
    public FieldSpec? FindByWire(string wire) => WireFields.FirstOrDefault(f => f.Wire == wire);
}

// The single field table per resource: CSV column, API wire name, value type, online clearing, set comparison,
// immutability, jumpseat read-only and evidence scope. Facts that depend on other fields of the same item
// (fleet_ids on update, type changes, endpoint resolution, time zone checks) stay explicit where they are used.
public static class FieldSpecs
{
    static readonly EvidenceScope C = EvidenceScope.Create, U = EvidenceScope.Update;
    const string Dep = "Departure Airport (ICAO/IATA)", Arr = "Arrival Airport (ICAO/IATA)";

    public static readonly FieldSpec DeleteMarker = new("_delete", Type: FieldType.Bool);

    public static readonly FrozenDictionary<ResourceKind, ResourceSpec> All = new Dictionary<ResourceKind, ResourceSpec>
    {
        [ResourceKind.Airports] = new(ResourceKind.Airports, "ICAO/IATA",
        [
            new("ICAO/IATA", Required: true, Role: FieldRole.Key, Immutable: true, Evidence: C),
            new("Name", "name", Required: true, Evidence: C | U),
            new("Category", "category"),
            new("Container IDs", "container_ids", FieldType.IdList, ClearsToEmptyArray: true, SetCompare: true),
            new("Pax LF ID"),
            new("Cargo LF ID")
        ]),
        [ResourceKind.Fleets] = new(ResourceKind.Fleets, "ID",
        [
            new("ID", Role: FieldRole.Key, Immutable: true),
            new("Name", "name", Required: true, Evidence: C | U),
            new("Type Code", "code", Required: true, Evidence: C),
            new("Type (pax/cargo/...)", "type", Required: true, Evidence: C),
            new("Max Passengers", "max_pax", FieldType.Integer, Evidence: C | U),
            new("Max Freight", "max_cargo", FieldType.KgInteger, Evidence: C | U),
            new("Container Units", "container_units", FieldType.Integer),
            new("Hide in Phoenix", "hide_in_phoenix", FieldType.Bool, Evidence: C),
            new("Allowed Prefix IDs"),
            new("Parameter IDs", "parameter_ids", FieldType.ParameterIdList, CreateOnly: true, Evidence: C)
        ]),
        [ResourceKind.Aircraft] = new(ResourceKind.Aircraft, "ID",
        [
            new("ID", Role: FieldRole.Key, Immutable: true),
            new("Name", "name", Required: true, Evidence: C | U),
            new("Registration", "registration", Required: true, Evidence: C),
            new("Fleet ID", Required: true, Role: FieldRole.Parent, Reference: ResourceKind.Fleets, Evidence: C | EvidenceScope.UpdateMove),
            new("SELCAL", "selcal"),
            new("Fin Number", "fin_number"),
            new("Hex Code", "hexcode"),
            new("Passengers", "passengers", FieldType.Integer, Evidence: C),
            new("Freight", "cargo", FieldType.KgInteger, Evidence: C | U),
            new("Internal Remarks", "internal_remarks", Evidence: C)
        ]),
        [ResourceKind.Routings] = new(ResourceKind.Routings, "ID",
        [
            new("ID", Role: FieldRole.Key, Immutable: true),
            new(Dep, Required: true, Immutable: true, Role: FieldRole.Endpoint, Reference: ResourceKind.Airports, Evidence: C),
            new(Arr, Required: true, Immutable: true, Role: FieldRole.Endpoint, Reference: ResourceKind.Airports, Evidence: C),
            new("Route String", "route", Required: true, Evidence: C),
            new("Remarks", "remarks", Evidence: C | U),
            new("Internal Remarks", "internal_remarks"),
            new("Tags", "tag", FieldType.TagList, SetCompare: true),
            new("Days of Operation", "days_of_operation", FieldType.DayList, SetCompare: true)
        ]),
        [ResourceKind.Routes] = new(ResourceKind.Routes, "ID",
        [
            new("ID", Role: FieldRole.Key, Immutable: true),
            new(Dep, Required: true, Immutable: true, Role: FieldRole.Endpoint, Reference: ResourceKind.Airports, Evidence: C),
            new(Arr, Required: true, Immutable: true, Role: FieldRole.Endpoint, Reference: ResourceKind.Airports, Evidence: C),
            new("Type", "type", Required: true, Evidence: C),
            new("Callsign", "callsign", JumpseatReadOnly: true, Evidence: C),
            new("Flight Number", "flight_number", JumpseatReadOnly: true, Evidence: C),
            new("Fleet IDs", "fleet_ids", FieldType.IdList, SetCompare: true, Reference: ResourceKind.Fleets, Evidence: EvidenceScope.CreateScheduled | EvidenceScope.UpdateScheduled),
            new("Start Date", "start_date", FieldType.Date, Evidence: C),
            new("End Date", "end_date", FieldType.Date, Evidence: C),
            new("Departure Time (HH:MM)", "departure_time", FieldType.Time, Evidence: C | U),
            new("Arrival Time (HH:MM)", "arrival_time", FieldType.Time, Evidence: C | U),
            new("Service Days", "service_days", FieldType.DayList, SetCompare: true, Evidence: C),
            new("Routing", "route", Evidence: C),
            new("Remarks", "remarks", Evidence: C | U),
            new("Internal Remarks", "internal_remarks", Evidence: C),
            new("Tags", "tag", FieldType.TagList, SetCompare: true),
            new("Is Hidden", "hidden", FieldType.Bool, Evidence: C),
            new("Altitude", "altitude", FieldType.Integer),
            new("Cost Index", "cost_index", Evidence: U | EvidenceScope.CreateJumpseat)
        ])
    }.ToFrozenDictionary();

    public static ResourceSpec Get(ResourceKind kind) => All[kind];

    // Column facts by name across all resources: rows may carry columns of any resource, and the same column name
    // has the same type everywhere (locked by FieldSpecTests). The first resource that lists a column wins.
    static readonly FrozenDictionary<string, FieldSpec> Columns = new[] { DeleteMarker }
        .Concat(All.Values.SelectMany(r => r.Fields)).GroupBy(f => f.Csv, StringComparer.Ordinal)
        .ToFrozenDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

    public static FieldSpec? Column(string csv) => Columns.GetValueOrDefault(csv);
    public static bool Is(string csv, FieldType type) => Columns.TryGetValue(csv, out var spec) && spec.Type == type;

    // Set-valued columns compare as sets of trimmed, non-empty entries; every other column compares as text.
    public static bool IsSetCompared(string csv) => Columns.TryGetValue(csv, out var spec) && spec.SetCompare;

    // Whether a field of this item is inside the isolated-test evidence. Context flags carry what the item's
    // other fields decide: the move, and the route type.
    public static bool InEvidence(ResourceKind kind, string csv, ChangeKind op, bool move, string routeType)
    {
        if (!All[kind].ByCsv.TryGetValue(csv, out var spec))
            return false;
        var scope = spec.Evidence;
        if (op == ChangeKind.Create)
            return scope.Has(EvidenceScope.Create)
                || scope.Has(EvidenceScope.CreateScheduled) && routeType == "scheduled"
                || scope.Has(EvidenceScope.CreateJumpseat) && routeType == "jumpseat";
        if (move)
            return scope.Has(EvidenceScope.UpdateMove);
        return scope.Has(EvidenceScope.Update) || scope.Has(EvidenceScope.UpdateScheduled) && routeType == "scheduled";
    }
}
