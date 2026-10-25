using System.Text.Json;
using VamSys.Core;
using VamSys.Infrastructure;
using VamSys.TestSupport;

namespace VamSys.UnitTests.Domain;

// Locks the field table (columns, wire names, flags) and the consumers that read it. A change to the table is
// a change to CSV columns, request bodies, validation or evidence scope and must be made on purpose.
public class FieldSpecTests
{
    const string Dep = "Departure Airport (ICAO/IATA)", Arr = "Arrival Airport (ICAO/IATA)";

    static IEnumerable<string> Csv(ResourceKind kind) => FieldSpecs.Get(kind).Fields.Select(f => f.Csv);
    static string[] Where(ResourceKind kind, Func<FieldSpec, bool> test) => FieldSpecs.Get(kind).Fields.Where(test).Select(f => f.Csv).ToArray();
    static Dictionary<string, string> Wire(ResourceKind kind) => FieldSpecs.Get(kind).WireFields.ToDictionary(f => f.Csv, f => f.Wire!);

    [Fact]
    public void Every_resource_has_a_table_and_the_key_comes_first()
    {
        foreach (var kind in Enum.GetValues<ResourceKind>())
        {
            var spec = FieldSpecs.Get(kind);
            Assert.Equal(kind, spec.Kind);
            Assert.Equal(spec.Key, spec.Fields[0].Csv);
            Assert.Equal(FieldRole.Key, spec.Fields[0].Role);
            Assert.Equal(spec.Fields.Count, spec.Fields.Select(f => f.Csv).Distinct().Count());
        }
    }

    [Fact]
    public void Columns_in_grid_order_per_resource()
    {
        Assert.Equal(["ICAO/IATA", "Name", "Category", "Container IDs", "Pax LF ID", "Cargo LF ID"], Csv(ResourceKind.Airports));
        Assert.Equal(["ID", "Name", "Type Code", "Type (pax/cargo/...)", "Max Passengers", "Max Freight", "Container Units", "Hide in Phoenix", "Allowed Prefix IDs", "Parameter IDs"], Csv(ResourceKind.Fleets));
        Assert.Equal(["ID", "Name", "Registration", "Fleet ID", "SELCAL", "Fin Number", "Hex Code", "Passengers", "Freight", "Internal Remarks"], Csv(ResourceKind.Aircraft));
        Assert.Equal(["ID", Dep, Arr, "Route String", "Remarks", "Internal Remarks", "Tags", "Days of Operation"], Csv(ResourceKind.Routings));
        Assert.Equal(["ID", Dep, Arr, "Type", "Callsign", "Flight Number", "Fleet IDs", "Start Date", "End Date", "Departure Time (HH:MM)", "Arrival Time (HH:MM)",
            "Service Days", "Routing", "Remarks", "Internal Remarks", "Tags", "Is Hidden", "Altitude", "Cost Index"], Csv(ResourceKind.Routes));
    }

    [Fact]
    public void Schemas_expose_the_required_and_optional_split_of_the_table()
    {
        Assert.Equal(["ICAO/IATA", "Name"], Schemas.All[ResourceKind.Airports].Required);
        Assert.Equal(["Category", "Container IDs", "Pax LF ID", "Cargo LF ID"], Schemas.All[ResourceKind.Airports].Optional);
        Assert.Equal(["Name", "Type Code", "Type (pax/cargo/...)"], Schemas.All[ResourceKind.Fleets].Required);
        Assert.Equal(["Name", "Registration", "Fleet ID"], Schemas.All[ResourceKind.Aircraft].Required);
        Assert.Equal([Dep, Arr, "Route String"], Schemas.All[ResourceKind.Routings].Required);
        Assert.Equal([Dep, Arr, "Type"], Schemas.All[ResourceKind.Routes].Required);
        Assert.Equal(["ID", "Name", "Type Code", "Type (pax/cargo/...)", "Max Passengers", "Max Freight", "Container Units", "Hide in Phoenix", "Allowed Prefix IDs", "Parameter IDs", "_delete"],
            Schemas.All[ResourceKind.Fleets].Columns);
    }

    [Fact]
    public void Wire_names_per_resource()
    {
        Assert.Equal(new() { ["Name"] = "name", ["Category"] = "category", ["Container IDs"] = "container_ids" }, Wire(ResourceKind.Airports));
        Assert.Equal(new()
        {
            ["Name"] = "name", ["Type Code"] = "code", ["Type (pax/cargo/...)"] = "type", ["Max Passengers"] = "max_pax", ["Max Freight"] = "max_cargo",
            ["Container Units"] = "container_units", ["Hide in Phoenix"] = "hide_in_phoenix", ["Parameter IDs"] = "parameter_ids"
        }, Wire(ResourceKind.Fleets));
        Assert.Equal(new()
        {
            ["Name"] = "name", ["Registration"] = "registration", ["SELCAL"] = "selcal", ["Fin Number"] = "fin_number", ["Hex Code"] = "hexcode",
            ["Passengers"] = "passengers", ["Freight"] = "cargo", ["Internal Remarks"] = "internal_remarks"
        }, Wire(ResourceKind.Aircraft));
        Assert.Equal(new()
        {
            ["Route String"] = "route", ["Remarks"] = "remarks", ["Internal Remarks"] = "internal_remarks", ["Tags"] = "tag", ["Days of Operation"] = "days_of_operation"
        }, Wire(ResourceKind.Routings));
        Assert.Equal(new()
        {
            ["Type"] = "type", ["Callsign"] = "callsign", ["Flight Number"] = "flight_number", ["Fleet IDs"] = "fleet_ids", ["Start Date"] = "start_date", ["End Date"] = "end_date",
            ["Departure Time (HH:MM)"] = "departure_time", ["Arrival Time (HH:MM)"] = "arrival_time", ["Service Days"] = "service_days", ["Routing"] = "route",
            ["Remarks"] = "remarks", ["Internal Remarks"] = "internal_remarks", ["Tags"] = "tag", ["Is Hidden"] = "hidden", ["Altitude"] = "altitude", ["Cost Index"] = "cost_index"
        }, Wire(ResourceKind.Routes));
    }

    [Fact]
    public void Wire_reading_order_is_the_grid_order()
    {
        foreach (var kind in Enum.GetValues<ResourceKind>())
            Assert.Equal(Csv(kind).Where(c => Wire(kind).ContainsKey(c)), FieldSpecs.Get(kind).WireFields.Select(f => f.Csv));
    }

    [Fact]
    public void Value_types()
    {
        var types = Enum.GetValues<ResourceKind>().SelectMany(k => FieldSpecs.Get(k).Fields).Where(f => f.Type != FieldType.Text)
            .GroupBy(f => f.Type).ToDictionary(g => g.Key, g => g.Select(f => f.Csv).Distinct().Order().ToArray());
        Assert.Equal(["Altitude", "Container Units", "Max Passengers", "Passengers"], types[FieldType.Integer]);
        Assert.Equal(["Freight", "Max Freight"], types[FieldType.KgInteger]);
        Assert.Equal(["Hide in Phoenix", "Is Hidden"], types[FieldType.Bool]);
        Assert.Equal(["End Date", "Start Date"], types[FieldType.Date]);
        Assert.Equal(["Arrival Time (HH:MM)", "Departure Time (HH:MM)"], types[FieldType.Time]);
        Assert.Equal(["Container IDs", "Fleet IDs"], types[FieldType.IdList]);
        Assert.Equal(["Parameter IDs"], types[FieldType.ParameterIdList]);
        Assert.Equal(["Tags"], types[FieldType.TagList]);
        Assert.Equal(["Days of Operation", "Service Days"], types[FieldType.DayList]);
        Assert.True(FieldSpecs.Is("_delete", FieldType.Bool));
    }

    [Fact]
    public void A_column_name_means_the_same_in_every_resource()
    {
        var all = Enum.GetValues<ResourceKind>().SelectMany(k => FieldSpecs.Get(k).Fields).GroupBy(f => f.Csv);
        foreach (var group in all)
        {
            Assert.Single(group.Select(f => f.Type).Distinct());
            Assert.Single(group.Select(f => f.SetCompare).Distinct());
            Assert.Single(group.Select(f => f.Role).Distinct());
        }
    }

    [Fact]
    public void Flags_per_resource()
    {
        Assert.Equal(["Container IDs"], Where(ResourceKind.Airports, f => f.ClearsToEmptyArray));
        Assert.Empty(Enum.GetValues<ResourceKind>().Where(k => k != ResourceKind.Airports).SelectMany(k => Where(k, f => f.ClearsToEmptyArray)));
        Assert.Equal(["Container IDs", "Tags", "Days of Operation", "Fleet IDs", "Service Days"],
            Enum.GetValues<ResourceKind>().SelectMany(k => Where(k, f => f.SetCompare)).Distinct());
        Assert.Equal(["Parameter IDs"], Where(ResourceKind.Fleets, f => f.CreateOnly));
        Assert.Equal(["Callsign", "Flight Number"], Where(ResourceKind.Routes, f => f.JumpseatReadOnly));
        Assert.Equal(["ICAO/IATA"], Where(ResourceKind.Airports, f => f.Immutable));
        Assert.Equal(["ID", Dep, Arr], Where(ResourceKind.Routings, f => f.Immutable));
        Assert.Equal(["ID", Dep, Arr], Where(ResourceKind.Routes, f => f.Immutable));
        Assert.Equal([Dep, Arr], FieldSpecs.Get(ResourceKind.Routes).Endpoints.Select(f => f.Csv));
        Assert.Equal(["Fleet ID"], Where(ResourceKind.Aircraft, f => f.Role == FieldRole.Parent));
    }

    [Fact]
    public void Reference_fields_keep_their_historic_order()
    {
        Assert.Equal(["Fleet ID"], Schemas.ReferenceFields(ResourceKind.Aircraft));
        Assert.Equal(["Fleet IDs", Dep, Arr], Schemas.ReferenceFields(ResourceKind.Routes));
        Assert.Equal([Dep, Arr], Schemas.ReferenceFields(ResourceKind.Routings));
        Assert.Empty(Schemas.ReferenceFields(ResourceKind.Airports));
        Assert.Empty(Schemas.ReferenceFields(ResourceKind.Fleets));
        Assert.Equal("Fleet ID", FleetAssignmentService.Field(ResourceKind.Aircraft));
        Assert.Equal("Fleet IDs", FleetAssignmentService.Field(ResourceKind.Routes));
        Assert.ThrowsAny<ArgumentException>(() => FleetAssignmentService.Field(ResourceKind.Routings));
    }

    [Fact]
    public void Evidence_scope_per_operation()
    {
        string[] Covered(ResourceKind kind, ChangeKind op, bool move = false, string type = "") =>
            Csv(kind).Where(c => FieldSpecs.InEvidence(kind, c, op, move, type)).ToArray();
        Assert.Equal(["ICAO/IATA", "Name"], Covered(ResourceKind.Airports, ChangeKind.Create));
        Assert.Equal(["Name"], Covered(ResourceKind.Airports, ChangeKind.Update));
        Assert.Equal(["Name", "Type Code", "Type (pax/cargo/...)", "Max Passengers", "Max Freight", "Hide in Phoenix", "Parameter IDs"], Covered(ResourceKind.Fleets, ChangeKind.Create));
        Assert.Equal(["Name", "Max Passengers", "Max Freight"], Covered(ResourceKind.Fleets, ChangeKind.Update));
        Assert.Equal(["Name", "Registration", "Fleet ID", "Passengers", "Freight", "Internal Remarks"], Covered(ResourceKind.Aircraft, ChangeKind.Create));
        Assert.Equal(["Name", "Freight"], Covered(ResourceKind.Aircraft, ChangeKind.Update));
        Assert.Equal(["Fleet ID"], Covered(ResourceKind.Aircraft, ChangeKind.Update, move: true));
        Assert.Equal([Dep, Arr, "Route String", "Remarks"], Covered(ResourceKind.Routings, ChangeKind.Create));
        Assert.Equal(["Remarks"], Covered(ResourceKind.Routings, ChangeKind.Update));
        string[] routeCreate = [Dep, Arr, "Type", "Callsign", "Flight Number", "Start Date", "End Date", "Departure Time (HH:MM)", "Arrival Time (HH:MM)", "Service Days", "Routing", "Remarks", "Internal Remarks", "Is Hidden"];
        Assert.Equal(routeCreate.Order(), Covered(ResourceKind.Routes, ChangeKind.Create, type: "cargo").Order());
        Assert.Equal(routeCreate.Append("Fleet IDs").Order(), Covered(ResourceKind.Routes, ChangeKind.Create, type: "scheduled").Order());
        Assert.Equal(routeCreate.Append("Cost Index").Order(), Covered(ResourceKind.Routes, ChangeKind.Create, type: "jumpseat").Order());
        Assert.Equal(new[] { "Fleet IDs", "Departure Time (HH:MM)", "Arrival Time (HH:MM)", "Remarks", "Cost Index" }.Order(), Covered(ResourceKind.Routes, ChangeKind.Update, type: "scheduled").Order());
        Assert.Equal(new[] { "Departure Time (HH:MM)", "Arrival Time (HH:MM)", "Remarks", "Cost Index" }.Order(), Covered(ResourceKind.Routes, ChangeKind.Update, type: "jumpseat").Order());
        Assert.False(FieldSpecs.InEvidence(ResourceKind.Fleets, "Unknown Column", ChangeKind.Create, false, ""));
    }

    [Fact]
    public void Evidence_covers_follows_the_table()
    {
        static ChangeItem Item(ResourceKind kind, ChangeKind op, params (string, string)[] fields)
        {
            var row = new DataRow { Fields = fields.ToDictionary(p => p.Item1, p => p.Item2) };
            return new() { Resource = kind, Kind = op, After = row, Fields = fields.ToDictionary(p => p.Item1, p => new FieldChange(FieldIntent.Set, p.Item2)) };
        }
        var hash = IndependentApiEvidence.ContractHash;
        Assert.True(IndependentApiEvidence.Covers(Item(ResourceKind.Airports, ChangeKind.Update, ("Name", "X")), hash));
        Assert.False(IndependentApiEvidence.Covers(Item(ResourceKind.Airports, ChangeKind.Update, ("Category", "x")), hash));
        Assert.True(IndependentApiEvidence.Covers(Item(ResourceKind.Aircraft, ChangeKind.Update, ("Fleet ID", "2")), hash));
        Assert.False(IndependentApiEvidence.Covers(Item(ResourceKind.Aircraft, ChangeKind.Update, ("Fleet ID", "2"), ("Name", "n")), hash));
        Assert.False(IndependentApiEvidence.Covers(Item(ResourceKind.Routes, ChangeKind.Create, ("Type", "cargo")), hash));
        Assert.True(IndependentApiEvidence.Covers(Item(ResourceKind.Routes, ChangeKind.Create, ("Type", "jumpseat"), ("Cost Index", "10")), hash));
        Assert.False(IndependentApiEvidence.Covers(Item(ResourceKind.Routes, ChangeKind.Create, ("Type", "scheduled"), ("Cost Index", "10")), hash));
        Assert.False(IndependentApiEvidence.Covers(Item(ResourceKind.Airports, ChangeKind.Update, ("Name", "X")), "other"));
    }

    [Fact]
    public void Set_columns_compare_as_sets_and_the_rest_as_text()
    {
        Assert.True(OperationsAdapter.Equivalent("Fleet IDs", "1, 2", "2,1"));
        Assert.True(OperationsAdapter.Equivalent("Container IDs", "3,3,4", "4,3"));
        Assert.True(OperationsAdapter.Equivalent("Tags", "a,b", " b ,a"));
        Assert.True(OperationsAdapter.Equivalent("Service Days", "monday,friday", "friday,monday"));
        Assert.False(OperationsAdapter.Equivalent("Parameter IDs", "1,2", "2,1"));
        Assert.False(OperationsAdapter.Equivalent("Name", "a ", "a"));
        Assert.False(OperationsAdapter.Equivalent("Fleet IDs", "1", "1,2"));
    }

    static readonly HttpClient Client = new(new FakeOperationsServer());

    static (OperationsAdapter Api, Workspace Workspace) Adapter()
    {
        var workspace = new Workspace { AirlineId = "7" };
        return (new OperationsAdapter(new OperationsTransport(Client, OperationsAdapter.BaseUri, Guid.NewGuid().ToString(),
            new TokenProvider(_ => Task.FromResult(new AccessToken("test", DateTimeOffset.UtcNow.AddHours(1))))), workspace), workspace);
    }

    static Dictionary<string, JsonElement> Sent(IEnumerable<ApiStep> steps) => OperationsAdapter.ExpectedValues(steps);

    [Fact]
    public void Csv_columns_and_wire_names_round_trip_through_read_and_create_plan()
    {
        var (api, _) = Adapter();
        const string json = """
            {"id":5,"airline_id":7,"name":"Freighter","code":"B77F","type":"cargo","max_pax":0,"max_cargo":100000,"container_units":4,"hide_in_phoenix":true,"parameter_ids":["9"]}
            """;
        using var doc = JsonDocument.Parse(json);
        var row = api.FromJson(ResourceKind.Fleets, doc.RootElement);
        Assert.Equal("Freighter", row.Get("Name"));
        Assert.Equal("B77F", row.Get("Type Code"));
        Assert.Equal("cargo", row.Get("Type (pax/cargo/...)"));
        Assert.Equal("100000", row.Get("Max Freight"));
        Assert.Equal("TRUE", row.Get("Hide in Phoenix"));
        Assert.Equal("9", row.Get("Parameter IDs"));

        var create = row.WithIdentity(null).With("ID", "");
        var item = new ChangeItem
        {
            Resource = ResourceKind.Fleets, Kind = ChangeKind.Create, After = create,
            Fields = FieldSpecs.Get(ResourceKind.Fleets).WireFields.ToDictionary(f => f.Csv, f => new FieldChange(FieldIntent.Set, row.Get(f.Csv)))
        };
        var body = Sent(api.Plan(item));
        using var expected = JsonDocument.Parse(json);
        foreach (var field in FieldSpecs.Get(ResourceKind.Fleets).WireFields)
            Assert.True(JsonElement.DeepEquals(expected.RootElement.GetProperty(field.Wire!), body[field.Wire!]), field.Wire);
        Assert.Equal(FieldSpecs.Get(ResourceKind.Fleets).WireFields.Count, body.Count);
    }

    [Fact]
    public void Read_keeps_route_times_to_minutes_and_dates_in_grid_format()
    {
        var (api, _) = Adapter();
        using var doc = JsonDocument.Parse("""
            {"id":3,"type":"scheduled","callsign":"ABC123","flight_number":"123","fleet_ids":[1,2],"departure_id":1,"arrival_id":2,"departure_time":"08:30:15","arrival_time":"10:00:00",
             "start_date":"2026-01-02T03:04:05+00:00","end_date":null,"service_days":["monday","friday"],"hidden":false}
            """);
        var row = api.FromJson(ResourceKind.Routes, doc.RootElement);
        Assert.Equal("08:30", row.Get("Departure Time (HH:MM)"));
        Assert.Equal("10:00", row.Get("Arrival Time (HH:MM)"));
        Assert.Equal("2026-01-02 03:04:05", row.Get("Start Date"));
        Assert.Equal("", row.Get("End Date"));
        Assert.Equal("1,2", row.Get("Fleet IDs"));
        Assert.Equal("monday,friday", row.Get("Service Days"));
        Assert.Equal("FALSE", row.Get("Is Hidden"));
    }

    static ChangeItem Update(ResourceKind kind, DataRow before, params (string Field, string Value)[] edits)
    {
        var after = before.With(edits.Select(e => KeyValuePair.Create(e.Field, e.Value)));
        return new() { Resource = kind, Kind = ChangeKind.Update, Before = before, After = after, RemoteId = before.Identity!.RemoteId,
            Fields = edits.ToDictionary(e => e.Field, e => new FieldChange(e.Value.Length == 0 ? FieldIntent.Clear : FieldIntent.Set, e.Value)) };
    }

    [Fact]
    public void Clearing_decisions_follow_the_table()
    {
        var (api, _) = Adapter();
        using var airport = JsonDocument.Parse("""{"id":1,"airline_id":7,"icao":"ZBAA","iata":"PEK","name":"Beijing","category":"large","container_ids":[1,2]}""");
        var row = api.FromJson(ResourceKind.Airports, airport.RootElement);
        var cleared = Sent(api.Plan(Update(ResourceKind.Airports, row, ("Container IDs", ""))));
        Assert.Equal(JsonValueKind.Array, cleared["container_ids"].ValueKind);
        Assert.Equal(0, cleared["container_ids"].GetArrayLength());
        var error = Assert.Throws<InvalidOperationException>(() => api.Plan(Update(ResourceKind.Airports, row, ("Name", ""))));
        Assert.True(error.Message.Contains("Name", StringComparison.Ordinal));
        Assert.Throws<InvalidOperationException>(() => api.Plan(Update(ResourceKind.Airports, row, ("ICAO/IATA", "ZSPD"))));
    }

    [Fact]
    public void Jumpseat_identity_and_create_only_fields_are_blocked_on_update()
    {
        var (api, _) = Adapter();
        using var jumpseat = JsonDocument.Parse("""{"id":3,"type":"jumpseat","callsign":"ABC123","flight_number":"123","departure_id":1,"arrival_id":2,"departure_time":"08:30:00"}""");
        var route = api.FromJson(ResourceKind.Routes, jumpseat.RootElement);
        Assert.Throws<InvalidOperationException>(() => api.Plan(Update(ResourceKind.Routes, route, ("Callsign", "XYZ999"))));
        Assert.Throws<InvalidOperationException>(() => api.Plan(Update(ResourceKind.Routes, route, (Dep, "ZSPD"))));
        // The stored seconds survive a minute-level edit.
        var edited = Sent(api.Plan(Update(ResourceKind.Routes, route, ("Departure Time (HH:MM)", "09:15"))));
        Assert.Equal("09:15:00", edited["departure_time"].GetString());
        using var fleet = JsonDocument.Parse("""{"id":4,"airline_id":7,"name":"F","code":"B738","type":"pax","max_pax":100}""");
        var fleetRow = api.FromJson(ResourceKind.Fleets, fleet.RootElement);
        Assert.Throws<InvalidOperationException>(() => api.Plan(Update(ResourceKind.Fleets, fleetRow, ("Parameter IDs", "5"))));
    }

    [Fact]
    public void Validation_uses_the_table_types()
    {
        var workspace = new Workspace { AirlineId = "7", Mode = RunMode.Online };
        var row = new DataRow { Fields = (new[] { ("ID", "1"), ("Name", "F"), ("Type Code", "B738"), ("Type (pax/cargo/...)", "cargo"), ("Max Freight", "1.5"), ("Hide in Phoenix", "yes"), ("Start Date", "x"), ("Service Days", "mon"), ("_delete", "FALSE") }).ToDictionary(p => p.Item1, p => p.Item2) };
        workspace.Resources[ResourceKind.Fleets].Draft.Add(row);
        workspace.Resources[ResourceKind.Fleets].Snapshot.Add(row.Dup());
        var issues = Schemas.Validate(workspace, ResourceKind.Fleets).Select(i => i.Field).ToHashSet();
        Assert.Contains("Max Freight", issues);
        Assert.Contains("Hide in Phoenix", issues);
        Assert.Contains("Start Date", issues);
        Assert.Contains("Service Days", issues);
        Assert.DoesNotContain("Name", issues);
    }

    [Fact]
    public void One_validation_pass_gives_the_same_issues_as_separate_runs()
    {
        var workspace = new Workspace { AirlineId = "7" };
        var fleet = new DataRow { Fields = (new[] { ("ID", ""), ("Name", "F"), ("Type Code", "B738"), ("Type (pax/cargo/...)", "pax"), ("Max Passengers", "100"), ("_delete", "FALSE") }).ToDictionary(p => p.Item1, p => p.Item2) };
        workspace.Resources[ResourceKind.Fleets].Draft.Add(fleet);
        var aircraft = new DataRow { Fields = (new[] { ("ID", ""), ("Name", "A"), ("Registration", "B-1"), ("Fleet ID", "99"), ("_delete", "FALSE") }).ToDictionary(p => p.Item1, p => p.Item2) };
        workspace.Resources[ResourceKind.Aircraft].Draft.Add(aircraft);
        var pass = new ValidationPass(workspace);
        foreach (var kind in Enum.GetValues<ResourceKind>())
            Assert.Equal(Schemas.Validate(workspace, kind).Select(i => (i.RowId, i.Field, i.Description.ToString())),
                Schemas.Validate(workspace, kind, pass).Select(i => (i.RowId, i.Field, i.Description.ToString())));
        Assert.Contains(Schemas.Validate(workspace, ResourceKind.Aircraft, pass), i => i.Field == "Fleet ID");
    }
}
