using VamSys.Core;
using VamSys.Infrastructure;
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
        // another contract falls back to the sampling limit
        Assert.Equal(3, Submitted(items, "other-contract"));
    }

    public static TheoryData<ResourceKind> Kinds() => new(Enum.GetValues<ResourceKind>());

    [Fact]
    public void An_unverified_field_variant_keeps_the_sampling_limit_even_with_independent_evidence()
    {
        var untested = Enumerable.Range(0, 5).Select(_ => Update(ResourceKind.Routings, "Tags", "not-tested")).ToList();
        Assert.Equal(3, Submitted(untested, ApiContracts.Sha256));
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
    public void Aircraft_internal_remarks_updates_open_in_full_but_not_when_an_unverified_field_rides_along()
    {
        var remarks = Enumerable.Range(0, 5).Select(_ => Update(ResourceKind.Aircraft, "Internal Remarks", "hangar 3")).ToList();
        Assert.Equal(5, Submitted(remarks, ApiContracts.Sha256));
        Assert.Equal(3, Submitted(remarks, "other-contract"));

        var mixed = Enumerable.Range(0, 5).Select(_ =>
        {
            var item = Update(ResourceKind.Aircraft, "Internal Remarks", "hangar 3");
            item.Fields["SELCAL"] = new(FieldIntent.Set, "AB-CD");
            return item;
        }).ToList();
        Assert.Equal(3, Submitted(mixed, ApiContracts.Sha256));
        // a verified field next to it keeps it open
        var withName = Update(ResourceKind.Aircraft, "Internal Remarks", "hangar 3");
        withName.Fields["Name"] = new(FieldIntent.Set, "Plane");
        Assert.True(IndependentApiEvidence.Covers(withName, ApiContracts.Sha256));
        // only the aircraft's field: routings keep their internal remarks outside the evidence
        Assert.False(IndependentApiEvidence.Covers(Update(ResourceKind.Routings, "Internal Remarks", "x"), ApiContracts.Sha256));
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
