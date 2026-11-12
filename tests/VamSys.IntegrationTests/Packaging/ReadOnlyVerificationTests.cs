using VamSys.Infrastructure;
using VamSys.TestSupport;
using System.Text.Json.Nodes;

namespace VamSys.IntegrationTests.Packaging;

// 5.1: the read-only check (tools/VamSys.QaSeed --verify-read-only) against the fake Operations server. It must send
// nothing but GET and token requests, follow cursors at every page size, and say what each filter returned.
public sealed class ReadOnlyVerificationTests : IDisposable
{
    readonly string report = Path.Combine(Path.GetTempPath(), "vamsys-readonly-" + Guid.NewGuid().ToString("N") + ".json");

    public void Dispose()
    {
        if (File.Exists(report))
            File.Delete(report);
    }

    [Fact]
    public async Task OnlyReadsAndReportsPagingFiltersAndScopes()
    {
        var remote = new FakeRemote();
        remote.AddStandardData();
        remote.PageSizes["routes"] = 2;
        remote.PageSizes["airports"] = 2;
        var clock = new TestTime();
        using var output = new StringWriter();
        await clock.Drive(ReadOnlyVerification.RunAsync(report, remote.Server, () => ("client", "secret-value"),
            new Uri("https://fake.operations.test/"), new RequestCoordinator(clock), output));

        Assert.All(remote.Server.Requests, r => Assert.Equal("GET", r.Method));
        var text = await File.ReadAllTextAsync(report, TestContext.Current.CancellationToken);
        Assert.DoesNotContain("secret-value", text, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-token", text, StringComparison.Ordinal);
        var json = JsonNode.Parse(text)!;

        // E4: the fake pages by its own size, so every size yields the same IDs over several pages.
        foreach (var compare in json["E4_compare"]!.AsArray())
            Assert.True((bool)compare!["sameIdsAs100"]!, compare.ToJsonString());
        var routes = json["E4_reads"]!.AsArray().First(r => (string)r!["resource"]! == "routes" && (int)r["pageSize"]! == 500)!;
        Assert.Equal(3, (int)routes["rows"]!);
        Assert.Equal(2, (int)routes["pages"]!);
        Assert.True((bool)routes["cursorKeepsPageSize"]!);
        Assert.Equal(4, (int)json["E4_aircraft"]!["largestFleet"]!);

        // E8 and G7: the fake matches exactly and with case, so only the exact probe finds the sample.
        var probes = json["E8_filters"]!.AsArray().Concat(json["G7_registration"]!.AsArray()).ToList();
        Assert.Equal(6 + 3 + 4, probes.Count);
        Assert.All(probes, p => Assert.Equal(200, (int)p!["status"]!));
        var icao = probes.Where(p => (string)p!["filter"]! == "icao").ToList();
        Assert.Equal([true, false, false], icao.Select(p => (bool)p!["containsSample"]!));
        var registration = probes.Where(p => (string)p!["filter"]! == "registration").ToList();
        Assert.Equal([true, false, false, false], registration.Select(p => (bool)p!["containsSample"]!));

        // E10: the fake gives a token for every scope request, and each token reads.
        var scopes = json["E10_scopes"]!.AsArray();
        Assert.Equal(["*", "(omitted)", "(empty)"], scopes.Select(s => (string)s!["scope"]!));
        Assert.All(scopes, s => Assert.Equal(200, (int)s!["readStatus"]!));
    }
}
