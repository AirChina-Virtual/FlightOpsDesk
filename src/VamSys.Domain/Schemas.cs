using System.Globalization;

namespace VamSys.Core;

public sealed record ResourceSchema(MessageDescriptor Name, string Key, string[] Required, string[] Optional)
{
    public IEnumerable<string> Columns => new[] { Key }.Concat(Required).Concat(Optional).Append("_delete").Distinct();
}
public static class Schemas
{
    // Columns come from the field table (FieldSpecs); only the localised resource names live here.
    static ResourceSchema Schema(ResourceKind kind, MessageDescriptor name)
    {
        var spec = FieldSpecs.Get(kind);
        return new(name, spec.Key, [.. spec.Required], [.. spec.Optional]);
    }
    public static readonly Dictionary<ResourceKind, ResourceSchema> All = new()
    {
        [ResourceKind.Airports] = Schema(ResourceKind.Airports, Messages.Define("Text_6A5347AC5F")),
        [ResourceKind.Fleets] = Schema(ResourceKind.Fleets, Messages.Define("Text_D082D02D37")),
        [ResourceKind.Aircraft] = Schema(ResourceKind.Aircraft, Messages.Define("Text_C05F7CD6ED")),
        [ResourceKind.Routings] = Schema(ResourceKind.Routings, Messages.Define("Text_ABF79C546F")),
        [ResourceKind.Routes] = Schema(ResourceKind.Routes, Messages.Define("Text_486B1DF063"))
    };
    public static string Key(ResourceKind kind, DataRow row) => row.Get(All[kind].Key).Trim().ToUpperInvariant();
    public static IEnumerable<string> MatchKeys(ResourceKind kind, DataRow row)
    {
        yield return Key(kind, row);
        if (kind == ResourceKind.Airports && row.RawApiJson != null)
        {
            using var json = System.Text.Json.JsonDocument.Parse(row.RawApiJson);
            foreach (var field in new[] { "icao", "iata" })
                if (json.RootElement.TryGetProperty(field, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
                    yield return value.GetString()!.Trim().ToUpperInvariant();
        }
    }
    public static string[] ReferenceFields(ResourceKind kind) => [.. FieldSpecs.Get(kind).References.Select(f => f.Csv)];
    // Documented values: empty, AUTO or an integer from 0 to 999.
    public static bool ValidCostIndex(string value) => value is "" or "AUTO"
        || int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n <= 999;
    public static bool Deleted(DataRow row) => row.Get("_delete").Equals("TRUE", StringComparison.OrdinalIgnoreCase);
    // Sets the validation rules test membership against; built once for the process.
    // In week order, for the day pickers.
    public static readonly IReadOnlyList<string> WeekdayOrder = ["monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday"];
    static readonly HashSet<string> Weekdays = new(WeekdayOrder, StringComparer.Ordinal);
    public static readonly IReadOnlyList<string> FleetTypeOrder = ["pax", "pax-cargo", "pax-containers", "cargo", "cargo-containers"];
    static readonly HashSet<string> FleetTypes = new(FleetTypeOrder, StringComparer.Ordinal);
    public static readonly IReadOnlyList<string> RouteTypeOrder = ["scheduled", "cargo", "charter", "training", "vfr", "repositioning", "jumpseat"];
    public static readonly IReadOnlySet<string> RouteTypes = new HashSet<string>(RouteTypeOrder, StringComparer.Ordinal);
    static readonly string[] RouteSignature = ["Departure Airport (ICAO/IATA)", "Arrival Airport (ICAO/IATA)", "Flight Number"];
    static readonly string[] RoutingSignature = ["Departure Airport (ICAO/IATA)", "Arrival Airport (ICAO/IATA)", "Route String"];
    static readonly string[] RegistrationSignature = ["Registration"];
    static readonly string[] NameSignature = ["Name"];
    static readonly string[] RouteIdentityFields = ["Callsign", "Flight Number"];
    public static List<Issue> Validate(Workspace workspace, ResourceKind kind) => Validate(workspace, kind, new ValidationPass(workspace));
    // One pass shares the row keys and reference-key sets between resources. Use a new pass after any row changed.
    public static List<Issue> Validate(Workspace workspace, ResourceKind kind, ValidationPass pass)
    {
        string Key(ResourceKind k, DataRow row) => pass.Key(k, row);
        var data = workspace.Resources[kind];
        var schema = All[kind];
        var issues = new List<Issue>();
        var snapshot = data.Snapshot.ToDictionary(r => r.LocalId);
        void Add(DataRow r, string f, MessageDescriptor m) => issues.Add(new(r.LocalId, f, m));
        var duplicates = data.Draft.Where(r => Key(kind, r) != "").GroupBy(r => Key(kind, r)).Where(g => g.Count() > 1);
        foreach (var group in duplicates)
        foreach (var r in group)
            Add(r, schema.Key, Messages.Define("Text_DA77CC5B62"));
        foreach (var r in data.Draft)
        {
            snapshot.TryGetValue(r.LocalId, out var before);
            CheckRow(workspace, kind, r, before, pass, issues);
        }
        // Flag possible duplicate creates without silently treating flight numbers as identities.
        var signatureFields = kind switch
        {
            ResourceKind.Routes => RouteSignature,
            ResourceKind.Routings => RoutingSignature,
            ResourceKind.Aircraft => RegistrationSignature,
            _ => NameSignature
        };
        foreach (var group in data.Draft.Where(r => !Deleted(r)).GroupBy(r => kind == ResourceKind.Routes && r.Get("Type") == "jumpseat"
            ? string.Join("\u001f", r.Get("Departure Airport (ICAO/IATA)"), r.Get("Arrival Airport (ICAO/IATA)"), ApiOperationPolicy.JumpseatSignature(r))
            : string.Join("\u001f", signatureFields.Select(r.Get))))
            if (group.Count() > 1 && group.Any(r => Key(kind, r) == ""))
                foreach (var r in group.Where(r => Key(kind, r) == ""))
                    Add(r, schema.Key, Messages.Define("Text_CDDA93CCD1"));
        var deletedIds = data.Draft.Where(Deleted).Select(r => r.LocalId).ToHashSet();
        if (deletedIds.Count > 0)
            issues.AddRange(new DeletionService().CheckReferences(workspace, kind, deletedIds));
        return issues;
    }
    // The rules that look at one row alone (and at the keys of other resources it refers to). The table shows these on
    // the cells as they are edited; Validate adds the rules that compare rows with each other.
    public static void CheckRow(Workspace workspace, ResourceKind kind, DataRow r, DataRow? before, ValidationPass pass, List<Issue> issues)
    {
        string Key(ResourceKind k, DataRow row) => pass.Key(k, row);
        var schema = All[kind];
        var spec = FieldSpecs.Get(kind);
        void Add(DataRow row, string f, MessageDescriptor m) => issues.Add(new(row.LocalId, f, m));
        // One row's rules, kept at the indentation of the loop they came from.
        {
            if (Schemas.Deleted(r))
            {
                if (Key(kind, r) == "" || before is null)
                    Add(r, schema.Key, Messages.Define("Text_73DF5EC597"));
                if (before != null && Key(kind, before) != Key(kind, r))
                    Add(r, schema.Key, Messages.Define("Text_F9F0B63720"));
                return;
            }
            foreach (var f in schema.Required)
            if (string.IsNullOrWhiteSpace(r.Get(f)))
                Add(r, f, Messages.Define("Text_B283E8E5F2"));
            if (before != null && Key(kind, before) != Key(kind, r))
                Add(r, schema.Key, Messages.Define("Text_783B7A2935"));
            if (before is null && schema.Key == "ID" && Key(kind, r) != "")
                Add(r, "ID", Messages.Define("Text_0D4C574F60"));
            foreach (var (f, v) in r.FieldEntries())
            {
                if (v == "")
                    continue;
                if (workspace.Mode == RunMode.Online && FieldSpecs.Is(f, FieldType.KgInteger)
                    && (!long.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var kg) || kg < 0))
                    Add(r, f, Messages.Define("WeightIntegerKg"));
                if (FieldSpecs.Is(f, FieldType.Bool))
                    if (v is not "TRUE" and not "FALSE")
                        Add(r, f, Messages.Define("Text_1B7AB7E0F1"));
                if (FieldSpecs.Is(f, FieldType.Date))
                    if (!DateTime.TryParseExact(v, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                        Add(r, f, Messages.Define("Text_F59EB79050"));
                if (FieldSpecs.Is(f, FieldType.Time))
                    if (!TimeOnly.TryParseExact(v, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                        Add(r, f, Messages.Define("Text_C8CF0D677E"));
                if (FieldSpecs.Is(f, FieldType.DayList))
                    if (v.Split(',').Any(d => !Weekdays.Contains(d.Trim())))
                        Add(r, f, Messages.Define("Text_C4946548B5"));
            }
            if (kind == ResourceKind.Fleets)
            {
                var type = r.Get("Type (pax/cargo/...)");
                if (!FleetTypes.Contains(type))
                    Add(r, "Type (pax/cargo/...)", Messages.Define("Text_50DDC19E1F"));
                var required = new List<string>();
                if (type.StartsWith("pax"))
                    required.Add("Max Passengers");
                if (type is "cargo" or "pax-cargo")
                    required.Add("Max Freight");
                if (type.Contains("containers"))
                    required.Add("Container Units");
                foreach (var f in required)
                if (!decimal.TryParse(r.Get(f), CultureInfo.InvariantCulture, out var n) || n < 0)
                    Add(r, f, Messages.Define("Text_675251FAAF"));
            }
            if (kind == ResourceKind.Routes)
            {
                if (!RouteTypes.Contains(r.Get("Type")))
                    Add(r, "Type", Messages.Define("Text_EFB86BD704"));
                // StoreRouteRequest requires callsign and flight_number for every type, jumpseat included.
                foreach (var f in RouteIdentityFields)
                    if (r.Get(f) == "" && !(r.Get("Type") == "jumpseat" && before?.Identity != null))
                        Add(r, f, Messages.Define("Text_B283E8E5F2"));
                if (r.Get("Type") != "jumpseat" && r.Get("Fleet IDs") == "")
                    Add(r, "Fleet IDs", Messages.Define("Text_134FD96D33"));
                // The server drops a jumpseat's fleets (2026-11-12, route 14217362): sending them could never be read back.
                if (r.Get("Type") == "jumpseat" && r.Get("Fleet IDs") != "")
                    Add(r, "Fleet IDs", Messages.Define("RouteJumpseatNoFleets"));
                if (r.Get("Callsign") != "" && r.Get("Callsign").Length is < 4 or > 7)
                    Add(r, "Callsign", Messages.Define("Text_39CD1EC048"));
                if (r.Get("Flight Number") != "" && r.Get("Flight Number").Length is < 3 or > 6)
                    Add(r, "Flight Number", Messages.Define("Text_D60FFE3B83"));
                if (!ValidCostIndex(r.Get("Cost Index")))
                    Add(r, "Cost Index", Messages.Define("CostIndexInvalid"));
            }
            // Route endpoints are immutable online too (Plan blocks them), but only routing edits are flagged here.
            if (kind == ResourceKind.Routings && before != null)
                foreach (var endpoint in spec.Endpoints)
                    if (r.Get(endpoint.Csv) != before.Get(endpoint.Csv))
                        Add(r, endpoint.Csv, Messages.Define("Text_31E5AEFD06"));
            void Reference(string field, ResourceKind target)
            {
                foreach (var id in r.Get(field).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    if (!pass.ReferenceKeys(target).Contains(id.ToUpperInvariant()) && !pass.LocalKeys(target).Contains(id))
                        Add(r, field, Messages.Define("Text_5CDAFFB468", ("arg0", id), ("arg1", All[target].Name)));
            }
            // A single fleet is required before the aircraft fleet reference is resolved.
            if (kind == ResourceKind.Aircraft && FleetAssignmentService.Tokens(r.Get("Fleet ID")).Count != 1)
                Add(r, "Fleet ID", Messages.Define("Text_06FE405F2A"));
            foreach (var reference in spec.References)
                Reference(reference.Csv, reference.Reference!.Value);
        }
    }
}

// Per-validation caches: a row's key (Trim + ToUpper) is computed once, and the reference sets of a resource are built
// when a rule first needs them. Rows must not change while a pass is in use.
public sealed class ValidationPass(Workspace workspace)
{
    readonly Dictionary<DataRow, string> keys = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<ResourceKind, HashSet<string>> referenceKeys = [];
    readonly Dictionary<ResourceKind, HashSet<string>> localKeys = [];
    public string Key(ResourceKind kind, DataRow row)
    {
        if (!keys.TryGetValue(row, out var key))
            keys[row] = key = Schemas.Key(kind, row);
        return key;
    }
    public HashSet<string> ReferenceKeys(ResourceKind kind)
    {
        if (!referenceKeys.TryGetValue(kind, out var set))
            referenceKeys[kind] = set = workspace.Resources[kind].Draft.Where(r => !Schemas.Deleted(r)).Select(r => Key(kind, r)).ToHashSet();
        return set;
    }
    public HashSet<string> LocalKeys(ResourceKind kind)
    {
        if (!localKeys.TryGetValue(kind, out var set))
            localKeys[kind] = set = workspace.Resources[kind].Draft.Where(r => !Schemas.Deleted(r)).Select(r => LocalReference.Format(r.LocalId)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return set;
    }
}
