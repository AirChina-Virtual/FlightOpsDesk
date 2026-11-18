using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using VamSys.Infrastructure;
using VamSys.TestSupport;

namespace VamSys.IntegrationTests.Packaging;

// Phase 6 (6.1 G4, 6.2 G8): the write evidence tool (tools/VamSys.QaSeed --write-evidence) against the fake Operations
// server. It must write nothing before the plan is confirmed, touch only the records it created, delete them all
// (also after a failure or Ctrl+C), and list in the report and on the console whatever it could not delete.
public sealed class WriteEvidenceTests : IDisposable
{
    readonly string report = Path.Combine(Path.GetTempPath(), "vamsys-write-evidence-" + Guid.NewGuid().ToString("N") + ".json");
    static readonly Uri Origin = new("https://fake.operations.test/");

    public void Dispose()
    {
        if (File.Exists(report))
            File.Delete(report);
    }

    sealed record Result(int Code, string Console, JsonNode Report, string Text);

    async Task<Result> RunAsync(FakeRemote remote, string typed = WriteEvidence.ConfirmWord, bool dryRun = false, CancellationTokenSource? stop = null)
    {
        var clock = new TestTime();
        using var output = new StringWriter();
        var run = WriteEvidence.RunAsync(report, new(dryRun), remote.Server, () => ("client", "secret-value"), Origin,
            new RequestCoordinator(clock), new StringReader(typed + "\n"), output, stop?.Token ?? CancellationToken.None);
        await clock.Drive(run);
        var text = await File.ReadAllTextAsync(report, TestContext.Current.CancellationToken);
        Assert.DoesNotContain("secret-value", text, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-token", text, StringComparison.Ordinal);
        return new(await run, output.ToString(), JsonNode.Parse(text)!, text);
    }

    static FakeRemote Remote()
    {
        var remote = new FakeRemote();
        remote.AddStandardData();
        return remote;
    }

    static Dictionary<string, string> Snapshot(FakeRemote remote) => remote.Rows.ToDictionary(p => p.Key, p => p.Value.ToJsonString());
    static string Relative(RecordedRequest r) => r.Path[FakeOperationsServer.OperationsPrefix.Length..];

    // Existing rows are only read: every one is still there and unchanged, and no write named one of them.
    static void ExistingUntouched(FakeRemote remote, Dictionary<string, string> before)
    {
        foreach (var (path, json) in before)
            Assert.Equal(json, remote.Rows[path].ToJsonString());
        Assert.DoesNotContain(remote.Server.Requests, r => r.Method is "PUT" or "DELETE" or "PATCH" && before.ContainsKey(Relative(r)));
    }

    [Fact]
    public async Task WritesOnlyItsOwnRecordsReportsNormalisationAndSoftDeletesAndCleansUp()
    {
        var remote = Remote();
        var before = Snapshot(remote);
        var limited = false;
        remote.Intercept = r =>
        {
            // One 429 on the first create: waited out and sent again.
            if (r.Method == "POST" && r.Path.EndsWith("/operations/fleet", StringComparison.Ordinal) && !limited)
            {
                limited = true;
                return new((HttpStatusCode)429, "{}", new Dictionary<string, string> { ["Retry-After"] = "2" });
            }
            // This server trims names and upper-cases callsigns on update.
            if (r.Method == "PUT")
            {
                var body = JsonNode.Parse(r.Body!)!.AsObject();
                if (body["name"] is JsonValue name)
                    body["name"] = name.GetValue<string>().Trim();
                if (body["callsign"] is JsonValue callsign)
                    body["callsign"] = callsign.GetValue<string>().ToUpperInvariant();
                return remote.Respond(r with { Body = body.ToJsonString() });
            }
            // A training route stays readable after its DELETE is accepted.
            if (r.Method == "DELETE" && remote.Rows.TryGetValue(Relative(r), out var row) && (string?)row["type"] == "training")
                return new(HttpStatusCode.NoContent, null);
            return null;
        };

        var result = await RunAsync(remote);

        Assert.Equal(WriteEvidence.Completed, result.Code);
        Assert.Contains("写入取证计划", result.Console, StringComparison.Ordinal);
        var json = result.Report;
        Assert.Equal("completed", (string?)json["outcome"]);
        Assert.Empty(json["leftovers"]!.AsArray());
        ExistingUntouched(remote, before);

        // Every record was created by this run, written only at its own address, and deleted.
        var records = json["records"]!.AsArray();
        Assert.Equal(4 + WriteEvidence.SoftDeleteTypes.Count, records.Count);
        Assert.All(records, r => Assert.Equal("deleted", (string?)r!["state"]));
        var own = records.Select(r => (string)r!["path"]!).ToHashSet();
        Assert.All(remote.Server.Requests.Where(r => r.Method is "PUT" or "DELETE"), r => Assert.Contains(Relative(r), own));
        Assert.All(remote.Server.Requests.Where(r => r.Method == "POST"), r => Assert.Contains(Relative(r), new[] { "fleet", "routings", "routes" }.Concat(own.Where(p => p.StartsWith("fleet/", StringComparison.Ordinal) && !p.Contains("/aircraft/", StringComparison.Ordinal)).Select(p => p + "/aircraft"))));
        var training = records.Single(r => (string?)r!["key"] == "g8-training")!;
        Assert.Equal(new[] { (string)training["path"]! }, remote.Rows.Keys.Except(before.Keys));
        Assert.All(remote.Server.Requests.Where(r => r.Method == "POST" && Relative(r) == "routes"),
            r => Assert.Contains(WriteEvidence.MarkerPrefix, JsonNode.Parse(r.Body!)!["internal_remarks"]!.GetValue<string>(), StringComparison.Ordinal));

        // The 429 is in the report, followed by the create that went through.
        var posts = json["requests"]!.AsArray().Where(r => (string?)r!["method"] == "POST" && (string?)r["path"] == "fleet").ToList();
        Assert.Equal([429, 201], posts.Select(p => (int)p!["status"]!));

        // G4: every probe was sent and read back; what this server changes is classified.
        var probes = json["G4_probes"]!.AsArray();
        Assert.Equal((int)json["plan"]!["requestsAfterConfirmation"]!["PUT"]!, probes.Count);
        string Kind(string record, string field, string variant) => (string)probes.Single(p => (string?)p!["record"] == record && (string?)p["field"] == field && (string?)p["variant"] == variant)!["kind"]!;
        Assert.Equal("trim", Kind("g4-fleet", "name", "trim-padding"));
        Assert.Equal("trim", Kind("g4-aircraft", "name", "trim-padding"));
        Assert.Equal("case", Kind("g4-route", "callsign", "lowercase"));
        Assert.Equal("equal", Kind("g4-route", "remarks", "trim-padding"));
        Assert.Equal("equal", Kind("g4-route", "start_date", "date-only"));
        var probe = probes.First(p => (string?)p!["variant"] == "trim-padding")!;
        Assert.NotNull(probe["sent"]);
        Assert.NotNull(probe["readback"]);
        // The fake answers parameter IDs as numbers to the strings sent (as the live API does); the rest reads back as sent.
        Assert.All(json["G4_createReadback"]!.AsArray(), c => Assert.Equal((string?)c!["field"] == "parameter_ids" ? "array-element-type" : "equal", (string?)c["kind"]));

        // G8: one route per remaining type, deleted (204) and read again.
        var soft = json["G8_softDelete"]!.AsArray();
        Assert.Equal(WriteEvidence.SoftDeleteTypes, soft.Select(s => (string)s!["type"]!));
        Assert.All(soft, s => Assert.Equal(204, (int)s!["deleteStatus"]!));
        Assert.All(soft, s => Assert.Equal((string?)s!["type"] == "training" ? 200 : 404, (int)s["getAfterDeleteStatus"]!));
        Assert.Equal("training", (string?)soft.Single(s => (bool)s!["readableAfterDelete"]!)!["getAfterDeleteBody"]!["type"]);

        // Requests stay under the plan's estimate (which counts the cleanup reads as well); the wire log lists every one,
        // the discovery reads included, with its status.
        Assert.True((int)json["requestCount"]! <= (int)json["plan"]!["expectedRequests"]! + 1, json["requestCount"]!.ToJsonString());
        var wire = json["wire"]!.AsArray();
        Assert.Equal((int)json["requestCount"]!, wire.Count);
        Assert.Equal(remote.Server.Requests.Count, wire.Count(w => (string?)w!["path"] != "/oauth/token"));
        Assert.Contains(wire, w => (string?)w!["path"] == "/api/v3/operations/parameters?…" && (int)w["status"]! == 200);
    }

    [Fact]
    public async Task DecliningThePlanWritesNothing()
    {
        var remote = Remote();
        var result = await RunAsync(remote, typed: "write");

        Assert.Equal(WriteEvidence.Declined, result.Code);
        Assert.All(remote.Server.Requests, r => Assert.Equal("GET", r.Method));
        Assert.Equal("declined", (string?)result.Report["outcome"]);
        Assert.NotEmpty(result.Report["plan"]!["records"]!.AsArray());
        Assert.Contains("没有发出任何写入", result.Console, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DryRunPrintsThePlanWithoutWriting()
    {
        var remote = Remote();
        var result = await RunAsync(remote, typed: WriteEvidence.ConfirmWord, dryRun: true);

        Assert.Equal(WriteEvidence.Completed, result.Code);
        Assert.All(remote.Server.Requests, r => Assert.Equal("GET", r.Method));
        Assert.Equal("dry-run", (string?)result.Report["outcome"]);
        var plan = result.Report["plan"]!;
        Assert.True((int)plan["expectedRequests"]! > (int)plan["requestsAfterConfirmation"]!["PUT"]!);
        Assert.Equal(101, (int)result.Report["discovery"]!["departureAirportId"]!);
        Assert.Equal(4, (int)result.Report["discovery"]!["fleetId"]!);
        Assert.Contains("--dry-run", result.Console, StringComparison.Ordinal);
        Assert.Empty(result.Report["leftovers"]!.AsArray());
    }

    [Fact]
    public async Task ATransportFailureMidwayStopsAndStillCleansUp()
    {
        var remote = Remote();
        var before = Snapshot(remote);
        remote.Intercept = r => r.Method == "PUT" && Relative(r).StartsWith("routings/", StringComparison.Ordinal)
            ? throw new HttpRequestException("connection reset") : null;

        var result = await RunAsync(remote);

        Assert.Equal(WriteEvidence.StoppedClean, result.Code);
        Assert.StartsWith("stopped", (string?)result.Report["outcome"], StringComparison.Ordinal);
        Assert.Empty(result.Report["leftovers"]!.AsArray());
        Assert.Equal(before.Keys.Order(), remote.Rows.Keys.Order());
        ExistingUntouched(remote, before);
        // Nothing more was written after the failure but the cleanup; G8 never started.
        Assert.Single(remote.Server.Requests, r => r.Method == "PUT" && Relative(r).StartsWith("routings/", StringComparison.Ordinal));
        Assert.Empty(result.Report["G8_softDelete"]!.AsArray());
        Assert.Equal(4, result.Report["cleanup"]!.AsArray().Count(c => (string?)c!["result"] == "deleted"));
    }

    [Fact]
    public async Task CtrlCStopsAfterTheRequestInFlightAndCleansUp()
    {
        var remote = Remote();
        var before = Snapshot(remote);
        using var stop = new CancellationTokenSource();
        remote.Intercept = r =>
        {
            if (r.Method == "PUT")
                stop.Cancel();
            return null;
        };

        var result = await RunAsync(remote, stop: stop);

        Assert.Equal(WriteEvidence.StoppedClean, result.Code);
        Assert.Equal("stopped: Ctrl+C", (string?)result.Report["outcome"]);
        Assert.Single(remote.Server.Requests, r => r.Method == "PUT");
        Assert.Equal(before.Keys.Order(), remote.Rows.Keys.Order());
        Assert.Empty(result.Report["leftovers"]!.AsArray());
    }

    [Fact]
    public async Task ARecordThatCannotBeDeletedIsListedInTheReportAndOnTheConsole()
    {
        var remote = Remote();
        var before = Snapshot(remote);
        string? fleet = null;
        remote.Intercept = r =>
        {
            var path = Relative(r);
            if (r.Method == "DELETE" && path.StartsWith("fleet/", StringComparison.Ordinal) && !path.Contains("/aircraft/", StringComparison.Ordinal))
            {
                fleet = path;
                return FakeResponse.WithStatus(500, """{"message":"Server Error"}""");
            }
            return null;
        };

        var result = await RunAsync(remote);

        Assert.Equal(WriteEvidence.LeftRecords, result.Code);
        Assert.NotNull(fleet);
        var left = Assert.Single(result.Report["leftovers"]!.AsArray())!;
        Assert.Equal(fleet, (string?)left["path"]);
        Assert.Equal("g4-fleet", (string?)left["record"]);
        Assert.Equal(result.Report["marker"]!.GetValue<string>(), (string?)left["marker"]);
        Assert.Contains("!! ", result.Console, StringComparison.Ordinal);
        Assert.Contains("ID " + fleet!["fleet/".Length..], result.Console, StringComparison.Ordinal);
        // Everything else is gone, and the one fleet is the only record left behind.
        Assert.Equal(new[] { fleet! }, remote.Rows.Keys.Except(before.Keys));
        ExistingUntouched(remote, before);
    }

    [Theory]
    [InlineData("\"  a  \"", "\"a\"", "trim")]
    [InlineData("\"abc\"", "\"ABC\"", "case")]
    [InlineData("\"a  b\"", "\"a b\"", "whitespace")]
    [InlineData("\"120\"", "120", "number-string")]
    // JSON numbers compare by value, as in the product's read-back check (JsonElement.DeepEquals).
    [InlineData("130.0", "130", "equal")]
    [InlineData("\"050\"", "\"50\"", "number-text")]
    [InlineData("\"\"", "null", "empty-to-null")]
    [InlineData("\"08:05\"", "\"08:05:00\"", "time-format")]
    [InlineData("\"2026-01-15\"", "\"2026-01-15T00:00:00+00:00\"", "date-format")]
    [InlineData("\"2026-01-15T08:00:00+08:00\"", "\"2026-01-15T00:00:00.000000Z\"", "date-format")]
    [InlineData("\"2026-01-15T08:00:00+08:00\"", "\"2026-01-15T08:00:00+00:00\"", "date-offset-ignored")]
    [InlineData("[\"b\",\"a\"]", "[\"a\",\"b\"]", "array-order")]
    [InlineData("[\"d\",\"d\"]", "[\"d\"]", "array-duplicates")]
    [InlineData("[\"4\"]", "[4]", "array-element-type")]
    [InlineData("[\"Monday\"]", "[\"monday\"]", "array-case")]
    [InlineData("\"x\"", "\"y\"", "other")]
    [InlineData("\"x\"", "\"x\"", "equal")]
    public void ClassifiesHowAReadBackDiffers(string sent, string actual, string kind)
        => Assert.Equal(kind, Normalisation.Classify(JsonNode.Parse(sent), JsonNode.Parse(actual), present: true));

    [Fact]
    public void AFieldMissingFromTheReadBackIsMissing()
        => Assert.Equal("missing", Normalisation.Classify(JsonValue.Create("x"), null, present: false));
}
