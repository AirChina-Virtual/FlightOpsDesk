using VamSys.Core;
using VamSys.Infrastructure;
using VamSys.TestSupport;
using VamSys.UnitTests.Api;

namespace VamSys.UnitTests.Application;

/// <summary>
/// Independent live evidence (the isolated vCCA tests) lifts the sampling limit of the review, but only for the fields, types and
/// the contract it covers, and it never bypasses the real preview validation or a pending task.
/// </summary>
public class ExternalEvidenceReviewTests
{
    static ChangeItem Update(ResourceKind kind, string field, string value) => new()
    {
        Resource = kind,
        Kind = ChangeKind.Update,
        Before = new() { Fields = new Dictionary<string, string> { ["Type"] = "scheduled" } },
        After = new() { Fields = new Dictionary<string, string> { ["Type"] = "scheduled", [field] = value } },
        Fields = new() { [field] = new(FieldIntent.Set, value) }
    };

    static int Submitted(IReadOnlyList<ChangeItem> items, string? contractHash, HashSet<string>? markers = null)
        => ReviewPlanBuilder.Build(items, RunMode.Online, true, markers ?? [], _ => { }, externalEvidenceContractHash: contractHash).SubmitItems.Count;

    [Fact]
    public void The_external_evidence_contract_is_the_fixed_contract_of_this_release()
        => Assert.Equal(ApiContracts.Sha256, IndependentApiEvidence.ContractHash);

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Covered_updates_of_every_resource_open_in_full_without_adding_workspace_markers_and_only_for_the_fixed_contract(ResourceKind kind)
    {
        var field = kind is ResourceKind.Routings or ResourceKind.Routes ? "Remarks" : "Name";
        var items = Enumerable.Range(0, 8).Select(_ => Update(kind, field, "Updated")).ToList();
        var markers = new HashSet<string>();

        Assert.Equal(8, Submitted(items, ApiContracts.Sha256, markers));
        Assert.Empty(markers);
        Assert.All(Plan(items, ApiContracts.Sha256).Entries, e => Assert.Equal("ApiIndependentEvidence", e.Reason!.Code));
        // another contract is not the evidence; these updates are still sent in full because they are verified operations
        // (ApiOperationPolicy.ReleaseOf), and say nothing about evidence
        Assert.False(IndependentApiEvidence.Covers(items[0], "other-contract"));
        Assert.All(Plan(items, "other-contract").Entries, e => { Assert.Equal(ReviewDisposition.Ready, e.Disposition); Assert.Null(e.Reason); });
    }

    static WorkspaceReviewPlan Plan(IReadOnlyList<ChangeItem> items, string? contractHash)
        => ReviewPlanBuilder.Build(items, RunMode.Online, true, new HashSet<string>(), _ => { }, externalEvidenceContractHash: contractHash);

    public static TheoryData<ResourceKind> Kinds() => new(Enum.GetValues<ResourceKind>());

    [Fact]
    public void An_unverified_field_variant_is_outside_the_independent_evidence_but_a_verified_operation_still_opens()
    {
        var untested = Enumerable.Range(0, 5).Select(_ => Update(ResourceKind.Routings, "Tags", "not-tested")).ToList();
        Assert.False(IndependentApiEvidence.Covers(untested[0], ApiContracts.Sha256));
        Assert.All(Plan(untested, ApiContracts.Sha256).Entries, e => { Assert.Equal(ReviewDisposition.Ready, e.Disposition); Assert.Null(e.Reason); });
    }

    [Fact]
    public void Types_and_fields_the_evidence_never_exercised_are_not_covered()
    {
        var charter = Update(ResourceKind.Routes, "Remarks", "x");
        charter.Before = charter.Before!.With("Type", "charter");
        charter.After = charter.After.With("Type", "charter");
        Assert.False(IndependentApiEvidence.Covers(charter, ApiContracts.Sha256));

        var fleet = new ChangeItem
        {
            Resource = ResourceKind.Fleets,
            Kind = ChangeKind.Create,
            After = new() { Fields = new Dictionary<string, string> { ["Type (pax/cargo/...)"] = "pax" } },
            Fields = new() { ["Type (pax/cargo/...)"] = new(FieldIntent.Set, "pax") }
        };
        Assert.False(IndependentApiEvidence.Covers(fleet, ApiContracts.Sha256));

        var jumpseat = new ChangeItem
        {
            Resource = ResourceKind.Routes,
            Kind = ChangeKind.Create,
            After = new() { Fields = new Dictionary<string, string> { ["Type"] = "jumpseat", ["Fleet IDs"] = "4" } },
            Fields = new() { ["Type"] = new(FieldIntent.Set, "jumpseat"), ["Fleet IDs"] = new(FieldIntent.Set, "4") }
        };
        Assert.False(IndependentApiEvidence.Covers(jumpseat, ApiContracts.Sha256));
    }

    // G8: DELETE 204 then GET 404 was seen live for every route type (scheduled and jumpseat on 2026-10-20; the other five in
    // artifacts/write-evidence/report.json, run FODESK-WE 20261118T001624Z). Only the delete is covered for the other five.
    [Theory]
    [InlineData("scheduled")]
    [InlineData("jumpseat")]
    [InlineData("cargo")]
    [InlineData("charter")]
    [InlineData("training")]
    [InlineData("vfr")]
    [InlineData("repositioning")]
    public void Route_deletes_of_every_type_are_covered_but_other_types_stay_uncovered_for_writes(string type)
    {
        var route = new DataRow { Fields = new Dictionary<string, string> { ["Type"] = type } };
        var delete = new ChangeItem { Resource = ResourceKind.Routes, Kind = ChangeKind.Delete, Before = route, After = route.With("_delete", "TRUE"), Fields = [] };
        Assert.True(IndependentApiEvidence.Covers(delete, ApiContracts.Sha256));
        Assert.False(IndependentApiEvidence.Covers(delete, "other-contract"));
        var update = Update(ResourceKind.Routes, "Remarks", "x");
        update.Before = update.Before!.With("Type", type);
        update.After = update.After.With("Type", type);
        Assert.Equal(type is "scheduled" or "jumpseat", IndependentApiEvidence.Covers(update, ApiContracts.Sha256));
    }

    // An aircraft's internal remarks were updated live by a PUT of that field alone, twice, and read back the same from the
    // aircraft and from its fleet's list (artifacts/write-evidence/aircraft-remarks.json, run FODESK-WE 20261118T005911Z).
    [Fact]
    public void Aircraft_internal_remarks_updates_are_covered_but_not_when_an_unverified_field_rides_along()
    {
        var remarks = Enumerable.Range(0, 5).Select(_ => Update(ResourceKind.Aircraft, "Internal Remarks", "hangar 3")).ToList();
        Assert.True(IndependentApiEvidence.Covers(remarks[0], ApiContracts.Sha256));
        Assert.False(IndependentApiEvidence.Covers(remarks[0], "other-contract"));
        // Aircraft:Update is a verified operation, so all of them are sent either way
        Assert.Equal(5, Submitted(remarks, ApiContracts.Sha256));
        Assert.Equal(5, Submitted(remarks, "other-contract"));

        var mixed = Enumerable.Range(0, 5).Select(_ =>
        {
            var item = Update(ResourceKind.Aircraft, "Internal Remarks", "hangar 3");
            item.Fields["SELCAL"] = new(FieldIntent.Set, "AB-CD");
            return item;
        }).ToList();
        Assert.False(IndependentApiEvidence.Covers(mixed[0], ApiContracts.Sha256));
        Assert.Equal(5, Submitted(mixed, ApiContracts.Sha256));
        // a verified field next to it keeps it covered
        var withName = Update(ResourceKind.Aircraft, "Internal Remarks", "hangar 3");
        withName.Fields["Name"] = new(FieldIntent.Set, "Plane");
        Assert.True(IndependentApiEvidence.Covers(withName, ApiContracts.Sha256));
        // only the aircraft's field: routings keep their internal remarks outside the evidence
        Assert.False(IndependentApiEvidence.Covers(Update(ResourceKind.Routings, "Internal Remarks", "x"), ApiContracts.Sha256));
    }

    // Route operations are keyed per route type. scheduled keeps the plain keys and its fleet-set key; a type outside the
    // API's list keeps the plain keys (the planner refuses it); deletes keep the plain key (G8 covers them independently).
    // A jumpseat or unknown type has no fleet-set key (the planner always refuses that change): it falls back to the plain
    // update key of its type.
    [Theory]
    [InlineData("scheduled", "Routes:Create", "Routes:Update", "Routes:Update:FleetIds:Scheduled")]
    [InlineData("cargo", "Routes:Create:Cargo", "Routes:Update:Cargo", "Routes:Update:FleetIds:Cargo")]
    [InlineData("charter", "Routes:Create:Charter", "Routes:Update:Charter", "Routes:Update:FleetIds:Charter")]
    [InlineData("training", "Routes:Create:Training", "Routes:Update:Training", "Routes:Update:FleetIds:Training")]
    [InlineData("vfr", "Routes:Create:Vfr", "Routes:Update:Vfr", "Routes:Update:FleetIds:Vfr")]
    [InlineData("repositioning", "Routes:Create:Repositioning", "Routes:Update:Repositioning", "Routes:Update:FleetIds:Repositioning")]
    [InlineData("jumpseat", "Routes:Create:Jumpseat", "Routes:Update:Jumpseat", "Routes:Update:Jumpseat")]
    [InlineData("ferry", "Routes:Create", "Routes:Update", "Routes:Update")]
    [InlineData("", "Routes:Create", "Routes:Update", "Routes:Update")]
    public void Route_operations_are_keyed_by_route_type(string type, string create, string update, string fleets)
    {
        Assert.Equal(create, ApiOperationPolicy.Key(RouteCreate(type)));
        Assert.Equal(update, ApiOperationPolicy.Key(RouteUpdate(type, type, "Remarks", "x")));
        Assert.Equal(fleets, ApiOperationPolicy.Key(RouteUpdate(type, type, "Fleet IDs", "4")));
        var row = new DataRow { Fields = new Dictionary<string, string> { ["Type"] = type } };
        Assert.Equal("Routes:Delete", ApiOperationPolicy.Key(new ChangeItem { Resource = ResourceKind.Routes, Kind = ChangeKind.Delete, Before = row, After = row.With("_delete", "TRUE") }));
    }

    // A type change is keyed by the non-scheduled type it involves, so it never takes the scheduled key.
    [Theory]
    [InlineData("scheduled", "cargo", "Routes:Update:Cargo")]
    [InlineData("cargo", "scheduled", "Routes:Update:Cargo")]
    [InlineData("cargo", "charter", "Routes:Update:Charter")]
    [InlineData("training", "scheduled", "Routes:Update:Training")]
    public void A_route_type_change_is_keyed_by_the_non_scheduled_type_it_involves(string from, string to, string key)
        => Assert.Equal(key, ApiOperationPolicy.Key(RouteUpdate(from, to, "Type", to)));

    // 2026-11-24: what the vCCA workspace verified, and what the user opened without evidence, skip the sample everywhere.
    // Only an operation outside both (in practice Fleets:Create of an old job) is still sampled.
    [Fact]
    public void Released_operations_are_the_verified_ones_and_the_user_decided_ones()
    {
        string[] verified = ["Airports:Create", "Airports:Update", "Airports:Delete", "Fleets:Create:ParameterIds", "Fleets:Update", "Fleets:Delete",
            "Aircraft:Create", "Aircraft:Update", "Aircraft:Delete", "Aircraft:Update:FleetId:Move", "Routings:Create", "Routings:Update", "Routings:Delete",
            "Routes:Create", "Routes:Create:Jumpseat", "Routes:Update", "Routes:Update:FleetIds:Scheduled", "Routes:Delete"];
        foreach (var key in verified)
            Assert.Equal(SampleRelease.Verified, ApiOperationPolicy.ReleaseOf(key));
        foreach (var type in new[] { "Cargo", "Charter", "Training", "Vfr", "Repositioning" })
            foreach (var key in new[] { $"Routes:Create:{type}", $"Routes:Update:{type}", $"Routes:Update:FleetIds:{type}" })
                Assert.Equal(SampleRelease.UserDecided, ApiOperationPolicy.ReleaseOf(key));
        Assert.Equal(SampleRelease.UserDecided, ApiOperationPolicy.ReleaseOf("Routes:Update:Jumpseat"));
        foreach (var key in new[] { "Fleets:Create", "Routes:Read", "Something:Else" })
            Assert.Equal(SampleRelease.Sampled, ApiOperationPolicy.ReleaseOf(key));
        // every key the policy can produce is released except Fleets:Create
        var produced = new List<string>();
        foreach (var resource in Enum.GetValues<ResourceKind>())
            foreach (var kind in Enum.GetValues<ChangeKind>())
                produced.Add(ApiOperationPolicy.Key(new ChangeItem { Resource = resource, Kind = kind }));
        Assert.Equal(["Fleets:Create"], produced.Where(k => ApiOperationPolicy.ReleaseOf(k) == SampleRelease.Sampled));
        foreach (var type in new[] { "cargo", "charter", "training", "vfr", "repositioning" })
        {
            Assert.False(IndependentApiEvidence.Covers(RouteCreate(type), ApiContracts.Sha256));
            Assert.False(IndependentApiEvidence.Covers(RouteUpdate(type, type, "Fleet IDs", "4"), ApiContracts.Sha256));
        }
    }

    // A new workspace (nothing verified) sends a typical change of every verified operation in full, with no reason and
    // outside the independent evidence (another contract), and the user-decided ones with their own reason.
    public static TheoryData<string> VerifiedChanges() => new(VerifiedSamples().Keys);
    static Dictionary<string, Func<ChangeItem>> VerifiedSamples() => new()
    {
        ["Airports:Create"] = () => Create(ResourceKind.Airports, ("ICAO/IATA", "ZBAA"), ("Name", "Beijing")),
        ["Airports:Update"] = () => Update(ResourceKind.Airports, "Name", "Beijing Capital"),
        ["Airports:Delete"] = () => Delete(ResourceKind.Airports),
        ["Fleets:Create:ParameterIds"] = () => Create(ResourceKind.Fleets, ("Name", "A330"), ("Parameter IDs", "1")),
        ["Fleets:Update"] = () => Update(ResourceKind.Fleets, "Name", "A330-300"),
        ["Fleets:Delete"] = () => Delete(ResourceKind.Fleets),
        ["Aircraft:Create"] = () => Create(ResourceKind.Aircraft, ("Registration", "B-1234"), ("Fleet ID", "4")),
        ["Aircraft:Update"] = () => Update(ResourceKind.Aircraft, "Name", "Plane"),
        ["Aircraft:Delete"] = () => Delete(ResourceKind.Aircraft),
        ["Aircraft:Update:FleetId:Move"] = () => Update(ResourceKind.Aircraft, "Fleet ID", "5"),
        ["Routings:Create"] = () => Create(ResourceKind.Routings, ("Route String", "DCT")),
        ["Routings:Update"] = () => Update(ResourceKind.Routings, "Tags", "a"),
        ["Routings:Delete"] = () => Delete(ResourceKind.Routings),
        ["Routes:Create"] = () => RouteCreate("scheduled"),
        ["Routes:Create:Jumpseat"] = () => RouteCreate("jumpseat"),
        ["Routes:Update"] = () => RouteUpdate("scheduled", "scheduled", "Tags", "t"),
        ["Routes:Update:FleetIds:Scheduled"] = () => RouteUpdate("scheduled", "scheduled", "Fleet IDs", "4"),
        ["Routes:Delete"] = () => Delete(ResourceKind.Routes),
    };

    [Theory]
    [MemberData(nameof(VerifiedChanges))]
    public void A_new_workspace_sends_every_verified_operation_in_full(string key)
    {
        var items = Enumerable.Range(0, 8).Select(_ => VerifiedSamples()[key]()).ToList();
        Assert.Equal(key, ApiOperationPolicy.Key(items[0]));
        var plan = ReviewPlanBuilder.Build(items, RunMode.Online, true, new HashSet<string>(), _ => { }, externalEvidenceContractHash: "other-contract");
        Assert.All(plan.Entries, e => { Assert.Equal(ReviewDisposition.Ready, e.Disposition); Assert.Null(e.Reason); });
        Assert.Equal(8, plan.SubmitItems.Count);
    }

    [Fact]
    public void A_new_workspace_sends_every_non_scheduled_route_operation_with_the_user_decided_reason_and_samples_only_the_rest()
    {
        var updates = Enumerable.Range(0, 8).Select(n => RouteUpdate("cargo", "cargo", "Departure Time (HH:MM)", $"0{n}:15")).ToList();
        var creates = Enumerable.Range(0, 8).Select(_ => RouteCreate("cargo")).ToList();
        var fleets = Enumerable.Range(0, 8).Select(_ => RouteUpdate("cargo", "cargo", "Fleet IDs", "4")).ToList();
        foreach (var items in new[] { updates, creates, fleets })
        {
            var plan = ReviewPlanBuilder.Build(items, RunMode.Online, true, new HashSet<string>(), _ => { }, externalEvidenceContractHash: ApiContracts.Sha256);
            Assert.All(plan.Entries, e => { Assert.Equal(ReviewDisposition.Ready, e.Disposition); Assert.Equal("ReviewUserDecided", e.Reason!.Code); });
            Assert.Equal(8, plan.SubmitItems.Count);
        }
        // the sample itself remains: a plain fleet create (an old job's) is sampled until the workspace verified it
        var plain = Enumerable.Range(0, 5).Select(_ => Create(ResourceKind.Fleets, ("Name", "Old"), ("Type (pax/cargo/...)", "pax"))).ToList();
        Assert.Equal("Fleets:Create", ApiOperationPolicy.Key(plain[0]));
        Assert.Equal(3, Submitted(plain, "other-contract"));
        Assert.Equal(5, Submitted(plain, "other-contract", ["Fleets:Create"]));
    }

    static ChangeItem Create(ResourceKind kind, params (string Field, string Value)[] fields) => new()
    {
        Resource = kind,
        Kind = ChangeKind.Create,
        After = new() { Fields = fields.ToDictionary(f => f.Field, f => f.Value) },
        Fields = fields.ToDictionary(f => f.Field, f => new FieldChange(FieldIntent.Set, f.Value))
    };

    static ChangeItem Delete(ResourceKind kind)
    {
        var row = new DataRow { Fields = new Dictionary<string, string> { ["Type"] = "scheduled" } };
        return new() { Resource = kind, Kind = ChangeKind.Delete, Before = row, After = row.With("_delete", "TRUE"), Fields = [] };
    }

    // The 2026-27 winter-spring update: 11 cargo routes drop one fleet (26722,26724,26725,26728 -> 26722,26724,26725) and
    // change their times, service days and cost index at the same time. They pass the real preview (planner and prefix
    // check), are keyed Routes:Update:FleetIds:Cargo and are all sent, in a workspace that has verified nothing.
    [Fact]
    public async Task Cargo_routes_that_drop_a_fleet_are_all_sent_and_never_blocked()
    {
        var kit = new ApiKit();
        kit.LoadStandardAirportsAndFleets();
        kit.Server.On(HttpMethod.Get, "parameters", FakeResponse.Ok("""{"data":[{"id":1,"airline_id":7,"icao_code":"CAO","iata_code":"CA","approved":true,"restricted":false,"fleet_ids":[4,5]}],"meta":{"next_cursor_url":null}}"""));
        await kit.Api.ReadParametersAsync(TestContext.Current.CancellationToken);
        var items = Enumerable.Range(0, 11).Select(n =>
        {
            var before = kit.Map(ResourceKind.Routes, ApiKit.Cargo13.Replace("\"id\":13", $"\"id\":{1300 + n}", StringComparison.Ordinal));
            return ApiKit.Update(ResourceKind.Routes, before, ("Fleet IDs", "4"), ("Departure Time (HH:MM)", "09:15"), ("Arrival Time (HH:MM)", "11:40"),
                ("Service Days", "monday,wednesday,friday"), ("Cost Index", "150"));
        }).ToList();

        var plan = ReviewPlanBuilder.Build(items, RunMode.Online, true, new HashSet<string>(), item => kit.Api.PreviewPlan(item), externalEvidenceContractHash: ApiContracts.Sha256);

        Assert.All(items, i => Assert.Equal("Routes:Update:FleetIds:Cargo", ApiOperationPolicy.Key(i)));
        Assert.All(plan.Entries, e => { Assert.Equal(ReviewDisposition.Ready, e.Disposition); Assert.Equal("ReviewUserDecided", e.Reason!.Code); });
        Assert.Equal(11, plan.SubmitItems.Count);
        var step = Assert.Single(kit.Api.Plan(items[0]));
        Assert.Equal("""{"fleet_ids":[4],"departure_time":"09:15:00","arrival_time":"11:40:00","service_days":["monday","wednesday","friday"],"cost_index":"150"}""", ApiKit.Body(step));
    }

    static ChangeItem RouteCreate(string type) => new()
    {
        Resource = ResourceKind.Routes,
        Kind = ChangeKind.Create,
        After = new() { Fields = new Dictionary<string, string> { ["Type"] = type, ["Callsign"] = "CAO100", ["Tags"] = "t" } },
        // Tags keep a create outside the independent evidence, so a scheduled one is sampled
        Fields = new() { ["Type"] = new(FieldIntent.Set, type), ["Callsign"] = new(FieldIntent.Set, "CAO100"), ["Tags"] = new(FieldIntent.Set, "t") }
    };

    static ChangeItem RouteUpdate(string from, string to, string field, string value)
    {
        var before = new DataRow { Fields = new Dictionary<string, string> { ["Type"] = from } };
        return new()
        {
            Resource = ResourceKind.Routes,
            Kind = ChangeKind.Update,
            Before = before,
            After = before.With("Type", to).With(field, value),
            Fields = new() { [field] = new(FieldIntent.Set, value) }
        };
    }

    [Fact]
    public void A_route_type_the_api_does_not_know_is_not_covered_for_delete()
    {
        var route = new DataRow { Fields = new Dictionary<string, string> { ["Type"] = "ferry" } };
        var delete = new ChangeItem { Resource = ResourceKind.Routes, Kind = ChangeKind.Delete, Before = route, After = route.With("_delete", "TRUE"), Fields = [] };
        Assert.False(IndependentApiEvidence.Covers(delete, ApiContracts.Sha256));
    }

    [Fact]
    public void External_evidence_never_bypasses_the_real_preview_validation()
    {
        var kit = new ApiKit();
        kit.LoadStandardAirportsAndFleets();
        var aircraft = kit.Map(ResourceKind.Aircraft, ApiKit.Aircraft8);
        var move = ApiKit.Update(ResourceKind.Aircraft, aircraft, ("Fleet ID", "5"));
        move.Fields["Name"] = new(FieldIntent.Set, "Mixed");

        var plan = ReviewPlanBuilder.Build([move], RunMode.Online, true, new HashSet<string>(), c => kit.Api.PreviewPlan(c), externalEvidenceContractHash: ApiContracts.Sha256);

        Assert.Empty(plan.SubmitItems);
        Assert.Equal("ApiMoveSeparate", plan.Entries.Single().Reason!.Code);
    }

    [Fact]
    public void External_evidence_never_bypasses_a_pending_task()
    {
        var kit = new ApiKit();
        var plan = ReviewPlanBuilder.Build([Update(ResourceKind.Fleets, "Name", "New")], RunMode.Online, true, new HashSet<string>(), _ => { },
            [Messages.Define("ApiPendingJob")], ApiContracts.Sha256);

        Assert.Empty(plan.SubmitItems);
        Assert.Empty(kit.Server.Requests);
    }
}
