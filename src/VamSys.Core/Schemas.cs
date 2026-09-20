using System.Globalization;

namespace VamSys.Core;

public sealed record ResourceSchema(MessageDescriptor Name, string Key, string[] Required, string[] Optional)
{
    public IEnumerable<string> Columns => new[] { Key }.Concat(Required).Concat(Optional).Append("_delete").Distinct();
}
public static class Schemas
{
    public static readonly Dictionary<ResourceKind, ResourceSchema> All = new()
    {
        [ResourceKind.Airports] = new(Messages.Define("Text_6A5347AC5F"), "ICAO/IATA", ["ICAO/IATA", "Name"], ["Category", "Container IDs", "Pax LF ID", "Cargo LF ID"]),
        [ResourceKind.Fleets] = new(Messages.Define("Text_D082D02D37"), "ID", ["Name", "Type Code", "Type (pax/cargo/...)"], ["Max Passengers", "Max Freight", "Container Units", "Hide in Phoenix", "Allowed Prefix IDs"]),
        [ResourceKind.Aircraft] = new(Messages.Define("Text_C05F7CD6ED"), "ID", ["Name", "Registration", "Fleet ID"], ["SELCAL", "Fin Number", "Hex Code", "Passengers", "Freight", "Internal Remarks"]),
        [ResourceKind.Routings] = new(Messages.Define("Text_ABF79C546F"), "ID", ["Departure Airport (ICAO/IATA)", "Arrival Airport (ICAO/IATA)", "Route String"], ["Remarks", "Internal Remarks", "Tags", "Days of Operation"]),
        [ResourceKind.Routes] = new(Messages.Define("Text_486B1DF063"), "ID", ["Departure Airport (ICAO/IATA)", "Arrival Airport (ICAO/IATA)", "Type"], ["Callsign", "Flight Number", "Fleet IDs", "Start Date", "End Date", "Departure Time (HH:MM)", "Arrival Time (HH:MM)", "Service Days", "Routing", "Remarks", "Internal Remarks", "Tags", "Is Hidden", "Altitude", "Cost Index"])
    };
    public static string Key(ResourceKind kind, DataRow row) => row.Get(All[kind].Key).Trim().ToUpperInvariant();
    public static IEnumerable<string> MatchKeys(ResourceKind kind, DataRow row)
    {
        yield return Key(kind,row);
        if(kind==ResourceKind.Airports && row.RawApiJson!=null)
        {
            using var json=System.Text.Json.JsonDocument.Parse(row.RawApiJson);
            foreach(var field in new[]{"icao","iata"})
                if(json.RootElement.TryGetProperty(field,out var value) && value.ValueKind==System.Text.Json.JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())) yield return value.GetString()!.Trim().ToUpperInvariant();
        }
    }
    public static string[] ReferenceFields(ResourceKind kind) => kind switch
    {
        ResourceKind.Aircraft => ["Fleet ID"],
        ResourceKind.Routes => ["Fleet IDs", "Departure Airport (ICAO/IATA)", "Arrival Airport (ICAO/IATA)"],
        ResourceKind.Routings => ["Departure Airport (ICAO/IATA)", "Arrival Airport (ICAO/IATA)"],
        _ => []
    };
    public static bool Deleted(DataRow row) => row.Get("_delete").Equals("TRUE", StringComparison.OrdinalIgnoreCase);
    public static List<Issue> Validate(Workspace workspace, ResourceKind kind)
    {
        var data = workspace.Resources[kind]; var schema = All[kind]; var issues = new List<Issue>();
        var snapshot = data.Snapshot.ToDictionary(r => r.LocalId);
        var referenceKeys = workspace.Resources.ToDictionary(p => p.Key, p => p.Value.Draft.Where(r => !Deleted(r)).Select(r => Key(p.Key, r)).ToHashSet());
        var localKeys = workspace.Resources.ToDictionary(p => p.Key, p => p.Value.Draft.Where(r => !Deleted(r)).Select(r => "local:" + r.LocalId).ToHashSet(StringComparer.OrdinalIgnoreCase));
        void Add(DataRow r, string f, MessageDescriptor m) => issues.Add(new(r.LocalId, f, m));
        var duplicates = data.Draft.Where(r => Key(kind, r) != "").GroupBy(r => Key(kind, r)).Where(g => g.Count() > 1);
        foreach (var group in duplicates) foreach (var r in group) Add(r, schema.Key, Messages.Define("Text_DA77CC5B62"));
        foreach (var r in data.Draft)
        {
            snapshot.TryGetValue(r.LocalId, out var before);
            if (Schemas.Deleted(r))
            {
                if (Key(kind, r) == "" || before is null) Add(r, schema.Key, Messages.Define("Text_73DF5EC597"));
                if (before != null && Key(kind, before) != Key(kind, r)) Add(r, schema.Key, Messages.Define("Text_F9F0B63720"));
                continue;
            }
            foreach (var f in schema.Required) if (string.IsNullOrWhiteSpace(r.Get(f))) Add(r, f, Messages.Define("Text_B283E8E5F2"));
            if (before != null && Key(kind, before) != Key(kind, r)) Add(r, schema.Key, Messages.Define("Text_783B7A2935"));
            if (before is null && schema.Key == "ID" && Key(kind, r) != "") Add(r, "ID", Messages.Define("Text_0D4C574F60"));
            foreach (var (f, v) in r.Fields)
            {
                if (v == "") continue;
                if (f is "_delete" or "Is Hidden" or "Hide in Phoenix")
                    if (v is not "TRUE" and not "FALSE") Add(r, f, Messages.Define("Text_1B7AB7E0F1"));
                if (f is "Start Date" or "End Date")
                    if (!DateTime.TryParseExact(v, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) Add(r, f, Messages.Define("Text_F59EB79050"));
                if (f is "Departure Time (HH:MM)" or "Arrival Time (HH:MM)")
                    if (!TimeOnly.TryParseExact(v, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) Add(r, f, Messages.Define("Text_C8CF0D677E"));
                if (f is "Service Days" or "Days of Operation")
                    if (v.Split(',').Any(d => !new[] { "monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday" }.Contains(d.Trim()))) Add(r, f, Messages.Define("Text_C4946548B5"));
            }
            if (kind == ResourceKind.Fleets)
            {
                var type = r.Get("Type (pax/cargo/...)");
                if (!new[] { "pax", "pax-cargo", "pax-containers", "cargo", "cargo-containers" }.Contains(type)) Add(r, "Type (pax/cargo/...)", Messages.Define("Text_50DDC19E1F"));
                var required = new List<string>();
                if (type.StartsWith("pax")) required.Add("Max Passengers");
                if (type is "cargo" or "pax-cargo") required.Add("Max Freight");
                if (type.Contains("containers")) required.Add("Container Units");
                foreach (var f in required) if (!decimal.TryParse(r.Get(f), CultureInfo.InvariantCulture, out var n) || n < 0) Add(r, f, Messages.Define("Text_675251FAAF"));
            }
            if (kind == ResourceKind.Routes)
            {
                if (!new[] { "scheduled", "cargo", "charter", "training", "vfr", "repositioning", "jumpseat" }.Contains(r.Get("Type"))) Add(r, "Type", Messages.Define("Text_EFB86BD704"));
                if (r.Get("Type") != "jumpseat")
                {
                    foreach (var f in new[] { "Callsign", "Flight Number", "Fleet IDs" }) if (r.Get(f) == "") Add(r, f, Messages.Define("Text_134FD96D33"));
                    if (r.Get("Callsign").Length is < 4 or > 7) Add(r, "Callsign", Messages.Define("Text_39CD1EC048"));
                    if (r.Get("Flight Number").Length is < 3 or > 6) Add(r, "Flight Number", Messages.Define("Text_D60FFE3B83"));
                }
            }
            if (kind == ResourceKind.Routings && before != null)
                foreach (var f in new[] { "Departure Airport (ICAO/IATA)", "Arrival Airport (ICAO/IATA)" })
                    if (r.Get(f) != before.Get(f)) Add(r, f, Messages.Define("Text_31E5AEFD06"));
            void Reference(string field, ResourceKind target)
            {
                foreach (var id in r.Get(field).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    if (!referenceKeys[target].Contains(id.ToUpperInvariant()) && !localKeys[target].Contains(id)) Add(r, field, Messages.Define("Text_5CDAFFB468", ("arg0", id), ("arg1", All[target].Name)));
            }
            if (kind == ResourceKind.Aircraft)
            {
                if (FleetAssignmentService.Tokens(r.Get("Fleet ID")).Count != 1) Add(r, "Fleet ID", Messages.Define("Text_06FE405F2A"));
                Reference("Fleet ID", ResourceKind.Fleets);
            }
            if (kind == ResourceKind.Routes) Reference("Fleet IDs", ResourceKind.Fleets);
            if (kind is ResourceKind.Routes or ResourceKind.Routings)
            {
                Reference("Departure Airport (ICAO/IATA)", ResourceKind.Airports);
                Reference("Arrival Airport (ICAO/IATA)", ResourceKind.Airports);
            }
        }
        // Flag possible duplicate creates without silently treating flight numbers as identities.
        var signatureFields = kind switch
        {
            ResourceKind.Routes => new[] { "Departure Airport (ICAO/IATA)", "Arrival Airport (ICAO/IATA)", "Flight Number" },
            ResourceKind.Routings => ["Departure Airport (ICAO/IATA)", "Arrival Airport (ICAO/IATA)", "Route String"],
            ResourceKind.Aircraft => ["Registration"],
            _ => new[] { "Name" }
        };
        foreach (var group in data.Draft.Where(r => !Deleted(r)).GroupBy(r => string.Join("\u001f", signatureFields.Select(r.Get))))
            if (group.Count() > 1 && group.Any(r => Key(kind, r) == ""))
                foreach (var r in group.Where(r => Key(kind, r) == "")) Add(r, schema.Key, Messages.Define("Text_CDDA93CCD1"));
        var deletedIds = data.Draft.Where(Deleted).Select(r => r.LocalId).ToHashSet();
        if (deletedIds.Count > 0) issues.AddRange(new DeletionService().CheckReferences(workspace, kind, deletedIds));
        return issues;
    }
}
