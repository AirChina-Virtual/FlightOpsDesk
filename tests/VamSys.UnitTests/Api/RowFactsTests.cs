using System.Text.Json;
using VamSys.Core;
using VamSys.Infrastructure;

namespace VamSys.UnitTests.Api;

/// <summary>The few values read again and again from a row's RawApiJson are parsed once per row text (review B8).</summary>
public class RowFactsTests
{
    [Fact]
    public void An_airport_answers_to_its_icao_and_its_iata_code()
    {
        var facts = RowFacts.Of(ApiKit.Egll);
        Assert.Equal(["EGLL", "LHR"], facts.Aliases);
    }

    [Theory]
    [InlineData("""{"id":1,"icao":null,"iata":"MAN"}""", new[] { "MAN" })]
    [InlineData("""{"id":1,"icao":"EGCC"}""", new[] { "EGCC" })]
    [InlineData("""{"id":1,"icao":"  ","iata":"MAN"}""", new[] { "MAN" })]
    [InlineData("""{"id":1,"icao":5,"iata":""}""", new string[0])]
    [InlineData("""{"id":1}""", new string[0])]
    public void Only_codes_sent_as_non_blank_text_count_as_aliases(string json, string[] expected)
        => Assert.Equal(expected, RowFacts.Of(json).Aliases);

    [Fact]
    public void Endpoint_ids_are_the_text_of_the_value_and_null_when_the_property_is_missing()
    {
        var route = RowFacts.Of("""{"id":10,"departure_id":101,"arrival_id":"102","departure_airport_id":null}""");
        Assert.Equal("101", route.Endpoint("departure_id"));
        Assert.Equal("102", route.Endpoint("arrival_id"));
        Assert.Equal("", route.Endpoint("departure_airport_id"));
        Assert.Null(route.Endpoint("arrival_airport_id"));
        Assert.Throws<ArgumentOutOfRangeException>(() => route.Endpoint("other"));
    }

    [Fact]
    public void The_update_timestamp_is_kept_as_sent()
    {
        Assert.Equal("2026-01-01T00:00:00Z", RowFacts.Of(ApiKit.Fleet4).UpdatedAt);
        Assert.Null(RowFacts.Of(ApiKit.Fleet5).UpdatedAt);
    }

    [Fact]
    public void Times_and_dates_keep_their_raw_text_and_whether_they_were_strings()
    {
        var facts = RowFacts.Of(ApiKit.Scheduled12);
        Assert.True(facts.TryTemporal("departure_time", out var isString, out var text));
        Assert.True(isString);
        Assert.Equal("23:59:37", text);
        Assert.True(facts.TryTemporal("start_date", out _, out var date));
        Assert.Equal("2026-10-01T00:00:00.1234567+00:00", date);
        Assert.False(facts.TryTemporal("end_date", out _, out _));
        Assert.False(facts.TryTemporal("remarks", out _, out _));
        var odd = RowFacts.Of("""{"departure_time":123456}""");
        Assert.True(odd.TryTemporal("departure_time", out var oddIsString, out var oddText));
        Assert.False(oddIsString);
        Assert.Equal("123456", oddText);
    }

    [Fact]
    public void A_row_text_is_parsed_once_and_the_same_facts_serve_every_copy_of_the_row()
    {
        var kit = new ApiKit();
        var row = kit.Map(ResourceKind.Airports, ApiKit.Egll);
        var copy = row.WithIdentity(new("101", "7")).WithLocalId(Guid.NewGuid()).With("Name", "changed");
        Assert.Same(RowFacts.Of(row.RawApiJson!), RowFacts.Of(copy.RawApiJson!));
        Assert.Same(RowFacts.Of(row.RawApiJson!), RowFacts.Of(row.RawApiJson!));
    }

    [Fact]
    public void Equal_text_in_different_strings_gives_equal_but_separate_facts()
    {
        var a = new string(ApiKit.Egll.AsSpan());
        var b = new string(ApiKit.Egll.AsSpan());
        Assert.NotSame(a, b);
        Assert.NotSame(RowFacts.Of(a), RowFacts.Of(b));
        Assert.Equal(RowFacts.Of(a).Aliases, RowFacts.Of(b).Aliases);
    }

    [Fact]
    public void Text_that_is_not_a_json_object_fails_every_time_instead_of_being_cached_as_empty()
    {
        Assert.ThrowsAny<JsonException>(() => RowFacts.Of("{not json"));
        Assert.ThrowsAny<JsonException>(() => RowFacts.Of("{not json"));
        Assert.Throws<InvalidOperationException>(() => RowFacts.Of("[1,2]"));
    }

    [Fact]
    public void Airport_aliases_of_a_row_come_from_the_cache_and_a_row_without_raw_text_has_none()
    {
        var kit = new ApiKit();
        var row = kit.Map(ResourceKind.Airports, ApiKit.Egll);
        Assert.Equal(["EGLL", "LHR"], IdentityIndex.AirportAliases(row));
        Assert.Empty(IdentityIndex.AirportAliases(new DataRow()));
        Assert.True(IdentityIndex.HasAirportAlias(row, "lhr"));
        Assert.False(IdentityIndex.HasAirportAlias(row, " "));
        Assert.False(IdentityIndex.HasAirportAlias(row, "EGKK"));
    }

    [Fact]
    public void Cell_text_follows_the_grid_conventions()
    {
        static string Cell(string json) { using var doc = JsonDocument.Parse(json); return ApiErrors.CellText(doc.RootElement); }
        Assert.Equal("", Cell("null"));
        Assert.Equal("TRUE", Cell("true"));
        Assert.Equal("FALSE", Cell("false"));
        Assert.Equal("text", Cell("\"text\""));
        Assert.Equal("12", Cell("12"));
        Assert.Equal("1.5", Cell("1.5"));
        Assert.Equal("a,b,", Cell("[\"a\",\"b\",null]"));
        Assert.Equal("1,2", Cell("[1,2]"));
        Assert.Equal("TRUE,1", Cell("[true,[1]]"));
    }
}
