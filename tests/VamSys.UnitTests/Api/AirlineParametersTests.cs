using VamSys.Core;
using VamSys.Infrastructure;
using VamSys.TestSupport;

namespace VamSys.UnitTests.Api;

/// <summary>The airline parameters (approved callsign and flight number prefixes) that new fleets and scheduled routes are checked against.</summary>
public class AirlineParametersTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    const string Dep = "Departure Airport (ICAO/IATA)", Arr = "Arrival Airport (ICAO/IATA)";

    static string Parameter(int id = 1, bool approved = true, bool restricted = false, string fleets = "[4,5]", int airline = 7)
        => $$"""{"id":{{id}},"airline_id":{{airline}},"icao_code":"CAO","iata_code":"CA","approved":{{approved.ToString().ToLowerInvariant()}},"restricted":{{restricted.ToString().ToLowerInvariant()}},"fleet_ids":{{fleets}}}""";

    static FakeResponse List(params string[] records) => FakeResponse.Ok("{\"data\":[" + string.Join(',', records) + "],\"meta\":{\"next_cursor_url\":null}}");

    static ApiKit Kit(params string[] parameters)
    {
        var kit = new ApiKit();
        kit.LoadStandardAirportsAndFleets();
        kit.Server.On(HttpMethod.Get, "parameters", List(parameters));
        return kit;
    }

    static int Reads(ApiKit kit) => kit.Server.Requests.Count(r => r.Path.EndsWith("/parameters", StringComparison.Ordinal));

    static ChangeItem ScheduledRoute(string callsign = "CAO200", string flight = "CA200", string fleets = "4", string type = "scheduled")
        => ApiKit.Create(ResourceKind.Routes, ("Type", type), (Dep, "EGLL"), (Arr, "EGKK"), ("Callsign", callsign), ("Flight Number", flight), ("Fleet IDs", fleets));

    // ---- reading ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Parameters_are_read_once_and_kept_for_the_session()
    {
        var kit = Kit(Parameter());
        Assert.False(kit.Api.HasParameterMetadata);
        var read = await kit.Api.ReadParametersAsync(Ct);
        var parameter = Assert.Single(read);
        Assert.Equal(new AirlineParameter("1", "7", "CAO", "CA", true, false, ["4", "5"]).Label, parameter.Label);
        Assert.Equal(["4", "5"], parameter.FleetIds);
        Assert.True(kit.Api.HasParameterMetadata);
        await kit.Api.ReadParametersAsync(Ct);
        Assert.Equal(1, Reads(kit));
        await kit.Api.ReadParametersAsync(Ct, force: true);
        Assert.Equal(2, Reads(kit));
        kit.Api.InvalidateParameters();
        Assert.False(kit.Api.HasParameterMetadata);
    }

    [Fact]
    public async Task Unavailable_parameters_are_reported_as_such_and_forgotten()
    {
        var kit = Kit(Parameter());
        await kit.Api.ReadParametersAsync(Ct);
        kit.Server.On(HttpMethod.Get, "parameters", FakeResponse.WithStatus(403));
        var error = await Assert.ThrowsAnyAsync<Exception>(() => kit.Api.ReadParametersAsync(Ct, force: true));
        Assert.Equal("ApiParametersUnavailable", MessageErrors.Describe(error).Code);
        Assert.False(kit.Api.HasParameterMetadata);
    }

    [Fact]
    public async Task Parameters_of_another_airline_or_with_repeated_ids_are_refused()
    {
        var kit = Kit(Parameter(airline: 8));
        var error = await Assert.ThrowsAnyAsync<Exception>(() => kit.Api.ReadParametersAsync(Ct));
        Assert.Equal("ApiWrongVa", MessageErrors.Describe(error).Code);
        Assert.False(kit.Api.HasParameterMetadata);
        kit.Server.On(HttpMethod.Get, "parameters", List(Parameter(1), Parameter(1)));
        error = await Assert.ThrowsAnyAsync<Exception>(() => kit.Api.ReadParametersAsync(Ct));
        Assert.Equal("ApiWrongVa", MessageErrors.Describe(error).Code);
    }

    // ---- who needs a check -------------------------------------------------------------------------------------

    [Fact]
    public void Only_new_fleets_with_a_parameter_and_scheduled_routes_are_checked()
    {
        var kit = Kit();
        var fleet = kit.Map(ResourceKind.Fleets, ApiKit.Fleet4);
        var newFleet = ApiKit.Create(ResourceKind.Fleets, ("Name", "x"), ("Parameter IDs", "1"));
        Assert.True(kit.Api.NeedsParameterPreflight(newFleet));
        Assert.False(kit.Api.NeedsParameterPreflight(ApiKit.Create(ResourceKind.Fleets, ("Name", "x"))));
        var required = ApiKit.Create(ResourceKind.Fleets, ("Name", "x"));
        required.RequiresAirlineParameter = true;
        Assert.True(kit.Api.NeedsParameterPreflight(required));
        Assert.False(kit.Api.NeedsParameterPreflight(ApiKit.Update(ResourceKind.Fleets, fleet, ("Name", "y"))));
        Assert.False(kit.Api.NeedsParameterPreflight(ApiKit.Delete(ResourceKind.Fleets, fleet)));

        Assert.True(kit.Api.NeedsParameterPreflight(ScheduledRoute()));
        var route = kit.Map(ResourceKind.Routes, ApiKit.Scheduled10);
        Assert.True(kit.Api.NeedsParameterPreflight(ApiKit.Update(ResourceKind.Routes, route, ("Callsign", "CAO300"))));
        Assert.True(kit.Api.NeedsParameterPreflight(ApiKit.Update(ResourceKind.Routes, route, ("Fleet IDs", "5"))));
        Assert.False(kit.Api.NeedsParameterPreflight(ApiKit.Update(ResourceKind.Routes, route, ("Remarks", "x"))));
        var jumpseat = ApiKit.Create(ResourceKind.Routes, ("Type", "jumpseat"), (Dep, "EGLL"), (Arr, "EGKK"));
        Assert.False(kit.Api.NeedsParameterPreflight(jumpseat));
        Assert.False(kit.Api.NeedsParameterPreflight(ApiKit.Create(ResourceKind.Airports, ("ICAO/IATA", "EGSS"), ("Name", "x"))));
    }

    // ---- checking against the parameters -----------------------------------------------------------------------

    [Fact]
    public void Checking_needs_the_parameters_to_have_been_read()
    {
        var kit = Kit(Parameter());
        Assert.Equal("ApiParametersUnavailable", ApiKit.BlockedWith(() => kit.Api.ValidateParameterPreview(ScheduledRoute())));
        // nothing to check, nothing to read
        kit.Api.ValidateParameterPreview(ApiKit.Create(ResourceKind.Airports, ("ICAO/IATA", "EGSS"), ("Name", "x")));
    }

    [Fact]
    public async Task A_new_fleet_must_name_one_approved_parameter_that_is_not_restricted()
    {
        var kit = Kit(Parameter(1), Parameter(2, approved: false), Parameter(3, restricted: true));
        await kit.Api.ReadParametersAsync(Ct);
        void Check(string ids) => kit.Api.ValidateParameterPreview(ApiKit.Create(ResourceKind.Fleets, ("Name", "x"), ("Parameter IDs", ids)));
        Check("1");
        foreach (var bad in new[] { "2", "3", "9", "1,2" })
            Assert.Equal("ApiParameterRequired", ApiKit.BlockedWith(() => Check(bad)));
    }

    [Fact]
    public async Task A_scheduled_route_must_use_the_prefixes_of_a_parameter_linked_to_each_of_its_fleets()
    {
        var kit = Kit(Parameter(1, fleets: "[4]"));
        await kit.Api.ReadParametersAsync(Ct);
        kit.Api.ValidateParameterPreview(ScheduledRoute());
        kit.Api.ValidateParameterPreview(ScheduledRoute(callsign: "cao999", flight: "ca999"));
        Assert.Equal("ApiPrefixDenied", ApiKit.BlockedWith(() => kit.Api.ValidateParameterPreview(ScheduledRoute(callsign: "XYZ200"))));
        Assert.Equal("ApiPrefixDenied", ApiKit.BlockedWith(() => kit.Api.ValidateParameterPreview(ScheduledRoute(flight: "XY200"))));
        // fleet 5 is not linked to the parameter
        Assert.Equal("ApiPrefixDenied", ApiKit.BlockedWith(() => kit.Api.ValidateParameterPreview(ScheduledRoute(fleets: "4,5"))));
        // fleets that do not exist cannot be checked
        Assert.Equal("ApiInvalid", ApiKit.BlockedWith(() => kit.Api.ValidateParameterPreview(ScheduledRoute(fleets: "99"))));
    }

    // D11: every route type that carries fleets is checked; a jumpseat (which need not name a fleet) is not.
    [Theory]
    [InlineData("repositioning")]
    [InlineData("cargo")]
    [InlineData("charter")]
    [InlineData("training")]
    [InlineData("vfr")]
    public async Task Every_route_type_with_fleets_is_checked_like_a_scheduled_route(string type)
    {
        var kit = Kit(Parameter(1, fleets: "[4]"));
        await kit.Api.ReadParametersAsync(Ct);
        Assert.True(kit.Api.NeedsParameterPreflight(ScheduledRoute(type: type)));
        kit.Api.ValidateParameterPreview(ScheduledRoute(type: type));
        Assert.Equal("ApiPrefixDenied", ApiKit.BlockedWith(() => kit.Api.ValidateParameterPreview(ScheduledRoute(fleets: "4,5", type: type))));
        Assert.False(kit.Api.NeedsParameterPreflight(ScheduledRoute(fleets: "4,5", type: "jumpseat")));
    }

    [Fact]
    public async Task Every_unapproved_fleet_is_named_at_once_in_the_order_the_route_lists_them()
    {
        var kit = Kit(Parameter(1, fleets: "[]"));
        await kit.Api.ReadParametersAsync(Ct);
        var route = ScheduledRoute(fleets: "5,4");
        Assert.Equal([("5", "Fleet 5"), ("4", "Fleet 4")], kit.Api.DeniedFleets(route));
        var error = Assert.ThrowsAny<Exception>(() => kit.Api.ValidateParameterPreview(route));
        var message = MessageErrors.Describe(error);
        Assert.Equal("ApiPrefixDeniedFleets", message.Code);
        Assert.Contains("Fleet 5 (5), Fleet 4 (4)", message.ToString(), StringComparison.Ordinal);
    }

    // The two repositioning routes of 2026-11-06 (CAPABILITY-MATRIX): 31 fleets, 6 of them not approved for CCA / CA. The
    // server refused them in two rounds; the preview now names all six before anything is sent.
    [Fact]
    public async Task The_repositioning_route_of_2026_11_06_is_refused_before_sending_with_all_six_fleets_named()
    {
        int[] unapproved = [26725, 26718, 27930, 26719, 26724, 26722];
        var fleets = Enumerable.Range(26800, 25).Concat(unapproved).ToArray();
        var kit = new ApiKit();
        kit.LoadStandardAirportsAndFleets();
        foreach (var id in fleets)
            kit.Map(ResourceKind.Fleets, $$"""{"id":{{id}},"airline_id":7,"name":"F{{id}}","code":"B738","type":"pax"}""");
        var approved = string.Join(',', fleets.Except(unapproved));
        kit.Server.On(HttpMethod.Get, "parameters", List($$"""{"id":1,"airline_id":7,"icao_code":"CCA","iata_code":"CA","approved":true,"restricted":false,"fleet_ids":[{{approved}}]}"""));
        await kit.Api.ReadParametersAsync(Ct);
        var route = ScheduledRoute("CCA088", "CA88", string.Join(',', fleets), "repositioning");

        Assert.Equal(unapproved.Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)), kit.Api.DeniedFleets(route).Select(f => f.Token));
        Assert.Equal("ApiPrefixDeniedFleets", ApiKit.BlockedWith(() => kit.Api.ValidateParameterPreview(route)));
        kit.Api.ValidateParameterPreview(ScheduledRoute("CCA088", "CA88", approved, "repositioning"));
    }

    [Fact]
    public async Task A_fleet_that_names_its_parameters_must_agree_with_the_parameter_list()
    {
        // The parameter lists fleet 4, but the fleet's own record links only parameter 2.
        var kit = Kit(Parameter(1, fleets: "[4]"));
        kit.Map(ResourceKind.Fleets, """{"id":4,"airline_id":7,"name":"Fleet 4","code":"B738","type":"cargo","parameter_ids":[2]}""");
        await kit.Api.ReadParametersAsync(Ct);
        Assert.Equal("ApiPrefixDenied", ApiKit.BlockedWith(() => kit.Api.ValidateParameterPreview(ScheduledRoute())));
    }

    [Fact]
    public async Task A_fleet_that_does_not_exist_yet_uses_the_parameter_named_in_its_draft()
    {
        var kit = Kit(Parameter(1, fleets: "[]"));
        var draft = new DataRow { Fields = new Dictionary<string, string> { ["Name"] = "New fleet", ["Parameter IDs"] = "1" } };
        kit.Workspace.Resources[ResourceKind.Fleets].Draft.Add(draft);
        await kit.Api.ReadParametersAsync(Ct);
        kit.Api.ValidateParameterPreview(ScheduledRoute(fleets: LocalReference.Format(draft.LocalId)));
        Assert.Equal("ApiInvalid", ApiKit.BlockedWith(() => kit.Api.ValidateParameterPreview(ScheduledRoute(fleets: LocalReference.Format(Guid.NewGuid())))));
    }

    // ---- preflight --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Preflight_reads_the_parameters_and_each_fleet_of_a_route_once()
    {
        var kit = Kit(Parameter(1, fleets: "[4,5]"));
        kit.Server.On(HttpMethod.Get, "fleet/4", ApiKit.Data(ApiKit.Fleet4));
        kit.Server.On(HttpMethod.Get, "fleet/5", ApiKit.Data(ApiKit.Fleet5));
        await kit.Api.PreflightParametersAsync(ScheduledRoute(fleets: "4,5"), Ct);
        await kit.Api.PreflightParametersAsync(ScheduledRoute(fleets: "5,4"), Ct);
        Assert.Equal(["parameters", "fleet/4", "fleet/5"], kit.Server.Requests.Select(r => r.Path[OperationsAdapter.BaseUri.AbsolutePath.Length..]).ToArray());
    }

    [Fact]
    public async Task Preflight_refuses_a_route_whose_fleet_is_gone()
    {
        var kit = Kit(Parameter(1, fleets: "[4]"));
        kit.Server.On(HttpMethod.Get, "fleet/4", FakeResponse.WithStatus(404));
        var error = await Assert.ThrowsAnyAsync<Exception>(() => kit.Api.PreflightParametersAsync(ScheduledRoute(fleets: "4"), Ct));
        Assert.Equal("ApiInvalid", MessageErrors.Describe(error).Code);
    }

    [Fact]
    public async Task Preflight_does_nothing_for_changes_that_need_no_check()
    {
        var kit = Kit(Parameter());
        await kit.Api.PreflightParametersAsync(ApiKit.Create(ResourceKind.Airports, ("ICAO/IATA", "EGSS"), ("Name", "x")), Ct);
        Assert.Empty(kit.Server.Requests);
    }

    [Fact]
    public async Task A_new_fleet_is_checked_against_the_parameters_after_reading_them()
    {
        var kit = Kit(Parameter(1));
        await kit.Api.PreflightParametersAsync(ApiKit.Create(ResourceKind.Fleets, ("Name", "x"), ("Parameter IDs", "1")), Ct);
        Assert.Equal(1, Reads(kit));
        var error = await Assert.ThrowsAnyAsync<Exception>(() => kit.Api.PreflightParametersAsync(ApiKit.Create(ResourceKind.Fleets, ("Name", "x"), ("Parameter IDs", "7")), Ct));
        Assert.Equal("ApiParameterRequired", MessageErrors.Describe(error).Code);
    }
}
