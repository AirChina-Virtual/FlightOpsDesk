using VamSys.Core;
using VamSys.Infrastructure;

namespace VamSys.UnitTests.Api;

/// <summary>Request bodies are checked against the Operations contract that ships with the program before anything is sent.</summary>
public class ContractValidatorTests
{
    static Dictionary<string, object?> Body(params (string Key, object? Value)[] pairs) => pairs.ToDictionary(p => p.Key, p => p.Value);

    static string Check(ResourceKind kind, bool create, Dictionary<string, object?> body)
    {
        try
        { OperationsAdapter.ValidateBody(kind, create, body); return "valid"; }
        catch (Exception e)
        { return MessageErrors.Describe(e).Code + ":" + (MessageErrors.Describe(e).Arguments.TryGetValue("field", out var f) ? f.Text : ""); }
    }

    [Fact]
    public void Bodies_that_follow_the_contract_pass()
    {
        Assert.Equal("valid", Check(ResourceKind.Airports, true, Body(("icao_iata", "EGLL"), ("name", "London"))));
        Assert.Equal("valid", Check(ResourceKind.Airports, false, Body(("name", "London"), ("container_ids", new long[] { 1, 2 }))));
        Assert.Equal("valid", Check(ResourceKind.Airports, false, Body(("category", null))));
        Assert.Equal("valid", Check(ResourceKind.Fleets, true, Body(("name", "F"), ("code", "B738"), ("type", "pax"), ("max_pax", 189L), ("parameter_ids", new[] { "1" }))));
        Assert.Equal("valid", Check(ResourceKind.Routings, true, Body(("departure_airport_id", 1L), ("arrival_airport_id", 2L), ("route", "DCT"))));
    }

    [Fact]
    public void A_property_the_contract_does_not_know_is_unsupported()
    {
        Assert.Equal("ApiUnsupported:colour", Check(ResourceKind.Airports, false, Body(("colour", "red"))));
        // a property of the create request is not allowed in the update request
        Assert.Equal("ApiUnsupported:icao_iata", Check(ResourceKind.Airports, false, Body(("icao_iata", "EGLL"))));
    }

    [Fact]
    public void Types_are_checked_and_integers_are_not_numbers_with_fractions()
    {
        Assert.Equal("ApiInvalid:name", Check(ResourceKind.Airports, false, Body(("name", 5L))));
        Assert.Equal("ApiInvalid:max_pax", Check(ResourceKind.Fleets, false, Body(("max_pax", "189"))));
        Assert.Equal("ApiInvalid:max_pax", Check(ResourceKind.Fleets, false, Body(("max_pax", 1.5))));
        Assert.Equal("ApiInvalid:hide_in_phoenix", Check(ResourceKind.Fleets, false, Body(("hide_in_phoenix", "TRUE"))));
        Assert.Equal("ApiInvalid:container_ids", Check(ResourceKind.Airports, false, Body(("container_ids", "1,2"))));
    }

    [Fact]
    public void Required_properties_are_only_demanded_of_creates()
    {
        Assert.Equal("ApiInvalid:name", Check(ResourceKind.Airports, true, Body(("icao_iata", "EGLL"))));
        Assert.Equal("ApiInvalid:icao_iata", Check(ResourceKind.Airports, true, Body(("name", "London"))));
        Assert.Equal("valid", Check(ResourceKind.Airports, false, Body()));
        Assert.Equal("ApiInvalid:registration", Check(ResourceKind.Aircraft, true, Body(("name", "P"))));
    }

    [Fact]
    public void Enums_lengths_and_minimums_are_enforced()
    {
        Assert.Equal("ApiInvalid:type", Check(ResourceKind.Fleets, false, Body(("type", "submarine"))));
        Assert.Equal("valid", Check(ResourceKind.Fleets, false, Body(("type", "pax-cargo"))));
        Assert.Equal("ApiInvalid:code", Check(ResourceKind.Fleets, false, Body(("code", "B73"))));
        Assert.Equal("ApiInvalid:code", Check(ResourceKind.Fleets, false, Body(("code", "B7388"))));
        Assert.Equal("ApiInvalid:icao_iata", Check(ResourceKind.Airports, true, Body(("icao_iata", "EG"), ("name", "x"))));
        Assert.Equal("ApiInvalid:name", Check(ResourceKind.Airports, false, Body(("name", new string('x', 241)))));
        Assert.Equal("valid", Check(ResourceKind.Airports, false, Body(("name", new string('x', 240)))));
        Assert.Equal("ApiInvalid:max_pax", Check(ResourceKind.Fleets, false, Body(("max_pax", -1L))));
        Assert.Equal("valid", Check(ResourceKind.Fleets, false, Body(("max_pax", 0L))));
    }

    [Fact]
    public void Array_items_are_checked_one_by_one()
    {
        Assert.Equal("ApiInvalid:container_ids", Check(ResourceKind.Airports, false, Body(("container_ids", new object[] { 1L, "two" }))));
    }

    [Theory]
    [InlineData("weight_unit")]
    [InlineData("simbrief_overrides")]
    public void Properties_the_program_never_sends_are_refused_even_though_the_contract_allows_them(string property)
    {
        Assert.Equal("ApiKnownWeightRestriction:", Check(ResourceKind.Fleets, false, Body((property, "x"))));
    }

    [Fact]
    public void Every_resource_has_a_schema_for_create_and_for_update()
    {
        foreach (var kind in Enum.GetValues<ResourceKind>())
            foreach (var create in new[] { true, false })
                Assert.NotEqual("", Check(kind, create, Body(("unknown_property", 1L))));
    }
}
