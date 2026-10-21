using VamSys.Core;
using VamSys.IntegrationTests.Golden.Requests;

namespace VamSys.IntegrationTests;

/// <summary>A2: a CSV that writes the local: prefix in another case behaves exactly like the lowercase spelling.</summary>
public class LocalReferenceCaseTests
{
    static async Task<(string[] Requests, string[] Outcomes)> Run(Func<string, string> spell, bool throughCsv = true)
    {
        var w = new GoldenWorld();
        var fleet = w.New(ResourceKind.Fleets, ("Name", "机型 New"), ("Type Code", "B738"), ("Type (pax/cargo/...)", "pax"), ("Parameter IDs", "1"));
        if (!throughCsv)
            w.New(ResourceKind.Aircraft, ("Name", "Plane Three"), ("Registration", "QA-NEW"), ("Fleet ID", spell("local:" + fleet.LocalId)));
        var csv = new CsvAdapter();
        var text = "Name,Registration,Fleet ID\r\nPlane Three,QA-NEW," + spell("local:" + fleet.LocalId) + "\r\n";
        if (throughCsv)
            CsvAdapter.Import(ResourceKind.Aircraft, w.Workspace.Resources[ResourceKind.Aircraft], csv.Map(csv.Parse(text), new Dictionary<string, string>()), baseline: false);
        await w.RunAsync();
        return (w.Remote.Server.Requests.Select(r => $"{r.Method} {r.Path} {r.Body}").ToArray(), w.Outcomes().ToArray());
    }

    [Theory]
    [InlineData("LOCAL:")]
    [InlineData("Local:")]
    public async Task Csv_aircraft_create_with_differently_cased_local_fleet_reference_succeeds_like_lowercase(string prefix)
    {
        var expected = await Run(t => t);
        Assert.DoesNotContain(expected.Outcomes, o => o.Contains("Failed", StringComparison.Ordinal));
        Assert.Contains("Aircraft Create Succeeded", expected.Outcomes);

        var actual = await Run(t => prefix + t["local:".Length..]);

        Assert.Equal(expected.Outcomes, actual.Outcomes);
        Assert.Equal(expected.Requests, actual.Requests);
    }

    // The CSV import normalises the prefix, so also feed the planner and executor a draft that still has the other spelling
    // (for example typed by hand or restored from an older workspace).
    [Theory]
    [InlineData("LOCAL:")]
    [InlineData("Local:")]
    public async Task Draft_aircraft_with_differently_cased_local_fleet_reference_executes_like_lowercase(string prefix)
    {
        var expected = await Run(t => t, throughCsv: false);
        Assert.Contains("Aircraft Create Succeeded", expected.Outcomes);

        var actual = await Run(t => prefix + t["local:".Length..], throughCsv: false);

        Assert.Equal(expected.Outcomes, actual.Outcomes);
        Assert.Equal(expected.Requests, actual.Requests);
    }
}
