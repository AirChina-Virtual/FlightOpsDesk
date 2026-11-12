using VamSys.Core;
using VamSys.IntegrationTests.Golden.Requests;

namespace VamSys.IntegrationTests.Api;

// E8: a few new airports or fleets against a long list are checked for duplicates by filtered reads (filter[icao] and
// filter[iata], filter[name]) instead of reading the whole list; a duplicate is still found and nothing is created twice.
// When the filtered reads would cost as much as the list (few rows, many creates), the whole list is read as before.
public class DuplicateLookupTests
{
    static GoldenWorld World(int airports, int fleets)
    {
        var w = new GoldenWorld(false);
        w.Remote.AddRow("airports/101", """{"id":101,"airline_id":7,"icao":"EGLL","iata":"LHR","name":"London"}""");
        for (var i = 0; i < airports; i++)
            w.Remote.AddRow($"airports/{1000 + i}", $$"""{"id":{{1000 + i}},"airline_id":7,"icao":"K{{i:000}}","iata":"Q{{i % 100:00}}","name":"Field {{i}}"}""");
        for (var i = 0; i < fleets; i++)
            w.Remote.AddRow($"fleet/{2000 + i}", $$"""{"id":{{2000 + i}},"airline_id":7,"name":"Fleet {{i}}","code":"B738","type":"pax","max_pax":180}""");
        w.LoadRemote();
        return w;
    }

    static List<string> Gets(GoldenWorld w, string collection) => w.Remote.Server.Requests
        .Where(r => r.Method == "GET" && r.Path.EndsWith("/" + collection, StringComparison.Ordinal)).Select(r => Uri.UnescapeDataString(r.PathAndQuery)).ToList();

    [Fact]
    public async Task A_new_airport_against_a_long_list_is_checked_by_code_filters_and_created_once()
    {
        var w = World(airports: 250, fleets: 1);
        w.New(ResourceKind.Airports, ("ICAO/IATA", "ZZZZ"), ("Name", "New field"));
        await w.RunAsync();
        Assert.Equal(ItemState.Succeeded, w.Job!.Items.Single().State);
        var reads = Gets(w, "airports");
        Assert.Equal(2, reads.Count);
        Assert.Contains(reads, q => q.Contains("filter[icao]=ZZZZ", StringComparison.Ordinal));
        Assert.Contains(reads, q => q.Contains("filter[iata]=ZZZZ", StringComparison.Ordinal));
        Assert.Single(w.Remote.Server.Requests, r => r.Method == "POST");
    }

    [Theory]
    [InlineData("EGLL")]
    [InlineData("LHR")]
    public async Task A_new_airport_whose_code_exists_is_refused_from_the_filtered_reads(string code)
    {
        var w = World(airports: 250, fleets: 1);
        w.New(ResourceKind.Airports, ("ICAO/IATA", code), ("Name", "Again"));
        await w.RunAsync();
        var item = w.Job!.Items.Single();
        Assert.Equal(ItemState.Failed, item.State);
        Assert.Equal("ApiDuplicate", item.Description?.Code);
        Assert.Equal([101L.ToString(System.Globalization.CultureInfo.InvariantCulture)], item.PreCreateCandidateIds);
        Assert.All(Gets(w, "airports"), q => Assert.Contains("filter[", q, StringComparison.Ordinal));
        Assert.DoesNotContain(w.Remote.Server.Requests, r => r.Method == "POST");
    }

    [Fact]
    public async Task A_new_fleet_against_a_long_list_is_checked_by_name_and_a_duplicate_name_is_refused()
    {
        var w = World(airports: 0, fleets: 150);
        w.New(ResourceKind.Fleets, ("Name", "Fleet 7"), ("Type Code", "B738"), ("Type (pax/cargo/...)", "pax"), ("Max Passengers", "180"), ("Parameter IDs", "1"));
        await w.RunAsync();
        var item = w.Job!.Items.Single();
        Assert.Equal("ApiDuplicate", item.Description?.Code);
        var reads = Gets(w, "fleet");
        Assert.All(reads, q => Assert.Contains("filter[name]=Fleet 7", q, StringComparison.Ordinal));
        Assert.NotEmpty(reads);
    }

    // Three airports fit one page: two filtered reads would cost more, so the list is read whole, as before E8.
    [Fact]
    public async Task A_short_list_is_still_read_whole()
    {
        var w = World(airports: 2, fleets: 1);
        w.New(ResourceKind.Airports, ("ICAO/IATA", "ZZZZ"), ("Name", "New field"));
        await w.RunAsync();
        Assert.Equal(ItemState.Succeeded, w.Job!.Items.Single().State);
        Assert.All(Gets(w, "airports"), q => Assert.DoesNotContain("filter[", q, StringComparison.Ordinal));
    }

    // Two creates of the same code in one run: the second finds the first through the confirmed rows and the lookup.
    [Fact]
    public async Task Two_creates_of_one_code_in_one_run_create_it_once()
    {
        var w = World(airports: 250, fleets: 1);
        w.New(ResourceKind.Airports, ("ICAO/IATA", "ZZZZ"), ("Name", "First"));
        w.New(ResourceKind.Airports, ("ICAO/IATA", "zzzz"), ("Name", "Second"));
        await w.RunAsync();
        Assert.Single(w.Remote.Server.Requests, r => r.Method == "POST");
        Assert.Contains(w.Job!.Items, i => i.Description?.Code == "ApiDuplicate");
    }
}
