using System.Text.Json;
using VamSys.Core;
using VamSys.Infrastructure;
using VamSys.IntegrationTests.Golden.Requests;
using VamSys.TestSupport;
using static VamSys.IntegrationTests.Api.PipelineHarness;

namespace VamSys.IntegrationTests.Api;

/// <summary>
/// One test (or theory) per problem found by the earlier contract audit and re-audit: recovery stays read-only,
/// airport references are resolved by id, typed values are read back by value, and refreshed caches are not stale.
/// </summary>
public class AuditRegressionTests
{
    const string London = """{"id":101,"airline_id":7,"icao":"EGLL","iata":"LHR","name":"Hub"}""";
    const string Gatwick = """{"id":102,"airline_id":7,"icao":"EGKK","iata":"LGW","name":"Hub"}""";

    static ChangeItem Create(ResourceKind kind, params (string Field, string Value)[] fields)
        => new()
        {
            Resource = kind,
            Kind = ChangeKind.Create,
            After = new DataRow { Fields = fields.ToDictionary(p => p.Field, p => p.Value) },
            Fields = fields.ToDictionary(p => p.Field, p => new FieldChange(p.Value == "" ? FieldIntent.Clear : FieldIntent.Set, p.Value))
        };

    static BatchJob JobOf(GoldenWorld w, params ChangeItem[] items)
    {
        var job = new BatchJob { Mode = RunMode.Online, Items = [.. items] };
        w.Workspace.Jobs.Add(job);
        return job;
    }

    static DataRow Parse(GoldenWorld w, ResourceKind kind, string json)
    {
        using var doc = JsonDocument.Parse(json);
        return w.Api.FromJson(kind, doc.RootElement);
    }

    static int Writes(GoldenWorld w) => w.Remote.Server.Requests.Count(r => r.Method != "GET");

    static ChangeItem Routing(string dep, string arr)
        => Create(ResourceKind.Routings, ("Departure Airport (ICAO/IATA)", dep), ("Arrival Airport (ICAO/IATA)", arr), ("Route String", "DCT"));

    static ChangeItem ScheduledRoute(string dep, string arr)
        => Create(ResourceKind.Routes, ("Departure Airport (ICAO/IATA)", dep), ("Arrival Airport (ICAO/IATA)", arr), ("Type", "scheduled"),
            ("Callsign", "CAO100"), ("Flight Number", "CA100"), ("Fleet IDs", "4"));

    // ---- unknown writes: recovery never writes -----------------------------------------------------------------------

    static FakeResponse? Fault(string fault) => fault switch
    {
        "timeout" => throw new TimeoutException(),
        "network" => throw new HttpRequestException(),
        "unexpected" => throw new FormatException("Malformed response"),
        "json" => FakeResponse.Ok("{"),
        "429" => RateLimited(),
        _ => Status(int.Parse(fault))
    };

    [Theory]
    [InlineData("timeout")]
    [InlineData("network")]
    [InlineData("401")]
    [InlineData("403")]
    [InlineData("429")]
    [InlineData("500")]
    [InlineData("422")]
    [InlineData("json")]
    [InlineData("unexpected")]
    public async Task Unknown_recovery_never_writes_whatever_fails_and_confirms_only_what_the_server_shows(string fault)
    {
        var w = new GoldenWorld();
        w.Remote.Intercept = r => r.Method == "PUT" ? Status(500) : null;
        w.Edit(ResourceKind.Fleets, "4", ("Name", "renamed"));
        await w.RunAsync("first run");
        var item = w.Job!.Items.Single();
        Assert.Equal(ItemState.Unknown, item.State);
        Assert.Equal(1, Count(w, "PUT"));

        w.Remote.Intercept = r => r.Method == "GET" ? Fault(fault) : Status(500);
        await w.ResumeAsync("recovery with " + fault);
        Assert.Equal(ItemState.Unknown, item.State);
        Assert.Equal(1, Writes(w));
        if (fault is "401" or "403")
            Assert.Equal(JobStatus.Paused, w.Job.StatusCode);

        w.Remote.Intercept = null;
        w.Remote.Rows["fleet/4"]["name"] = "renamed";
        await w.ResumeAsync("recovery after the server applied it");
        Assert.Equal(ItemState.Succeeded, item.State);
        Assert.Equal(1, Writes(w));
    }

    OperationsAdapter ApiOver(GoldenWorld w, Workspace restored)
    {
        var requests = InstantTime.Requests();
        var tokens = new TokenProvider(_ => Task.FromResult(new AccessToken("golden-token", requests.Clock.GetUtcNow().AddHours(1))), requests.Clock);
        return new OperationsAdapter(new OperationsTransport(w.Remote.Server.CreateClient(), OperationsAdapter.BaseUri, "golden-va", tokens, requests), restored);
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("network")]
    [InlineData("cancel")]
    [InlineData("401")]
    [InlineData("403")]
    [InlineData("429")]
    [InlineData("500")]
    [InlineData("422")]
    [InlineData("json")]
    [InlineData("unexpected")]
    public async Task An_unknown_write_stays_unknown_across_a_restart_and_recovery_after_the_restart_never_writes(string fault)
    {
        using var dir = TempDataDirectory.Create("vamsys-audit-");
        var store = new WorkspaceStore(dir.Path);
        var w = new GoldenWorld();
        w.Remote.Intercept = r => r.Method == "PUT" ? Status(500) : null;
        w.Edit(ResourceKind.Fleets, "4", ("Name", "renamed"));
        await w.RunAsync("first run");
        var item = w.Job!.Items.Single();
        Assert.Equal(ItemState.Unknown, item.State);

        // Recovery fails in some way; whatever it saved last is what a restart sees.
        using var cancel = new CancellationTokenSource();
        w.Remote.Intercept = r =>
        {
            if (r.Method != "GET")
                return Status(500);
            if (fault == "cancel")
            {
                cancel.Cancel();
                throw new OperationCanceledException(cancel.Token);
            }
            return Fault(fault);
        };
        await Execute(w, w.Job, () => { store.Save(w.Workspace); return Task.CompletedTask; }, cancel.Token);
        Assert.Equal(ItemState.Unknown, item.State);
        if (fault is "401" or "403")
            Assert.Equal(JobStatus.Paused, w.Job.StatusCode);
        if (fault == "cancel")
            Assert.Equal(JobStatus.Canceled, w.Job.StatusCode);
        Assert.Equal(1, Writes(w));

        // Restart: a new store reads the saved workspace and a new executor works on it.
        var restored = new WorkspaceStore(dir.Path).Load(w.Workspace.Id);
        var job = restored.Jobs.Single();
        var restoredItem = job.Items.Single();
        Assert.Equal(ItemState.Unknown, restoredItem.State);
        var api = ApiOver(w, restored);

        w.Remote.Intercept = r => r.Method == "GET" ? null : Status(500);
        await new OperationsBatchExecutor(api, restored).ExecuteAsync(job, () => Task.CompletedTask, Ct);
        Assert.Equal(ItemState.Unknown, restoredItem.State);
        Assert.Equal(1, Writes(w));

        w.Remote.Intercept = null;
        w.Remote.Rows["fleet/4"]["name"] = "renamed";
        await new OperationsBatchExecutor(api, restored).ExecuteAsync(job, () => Task.CompletedTask, Ct);
        Assert.Equal(ItemState.Succeeded, restoredItem.State);
        Assert.Equal(1, Writes(w));
    }
    [Theory]
    [InlineData("running")]
    [InlineData("created")]
    [InlineData("accepted")]
    public async Task Interrupted_and_legacy_partially_written_items_remain_read_only(string mode)
    {
        var w = new GoldenWorld(false);
        w.Remote.Intercept = _ => throw new TimeoutException();
        var item = Create(ResourceKind.Airports, ("ICAO/IATA", "EGLL"), ("Name", "Hub"));
        item.RemoteId = "101";
        item.State = mode == "running" ? ItemState.Running : ItemState.Pending;
        item.CreateCompleted = mode == "created";
        item.WriteAccepted = mode == "accepted";
        await Execute(w, JobOf(w, item));
        Assert.Equal(ItemState.Unknown, item.State);
        Assert.Equal(0, Writes(w));
        Assert.Equal("101", item.RemoteId);
        Assert.Equal(mode == "created", item.CreateCompleted);
        Assert.Equal(mode == "accepted", item.WriteAccepted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recovery_cannot_be_downgraded_by_unresolved_dependencies_or_conflicts(bool conflict)
    {
        var w = new GoldenWorld();
        var parent = new ChangeItem { Resource = ResourceKind.Airports, State = ItemState.Failed };
        var item = Routing("local:" + parent.After.LocalId, "LGW");
        item.State = ItemState.Running;
        item.RemoteId = "9";
        item.Dependencies.Add(parent.Id);
        if (conflict)
            w.Workspace.Resources[item.Resource].Conflicts.Add(new(item.After.LocalId, "Remarks", "a", "b", "c"));
        await Execute(w, JobOf(w, parent, item));
        Assert.Equal(ItemState.Unknown, item.State);
        Assert.Empty(w.Remote.Server.Requests);
    }

    [Theory]
    [InlineData("EGLL")]
    [InlineData("lhr")]
    public async Task Airport_recovery_validates_aliases_before_binding_a_supplied_id(string code)
    {
        var w = new GoldenWorld(false);
        w.Remote.AddRow("airports/101", London);
        w.Remote.AddRow("airports/102", Gatwick);
        var item = Create(ResourceKind.Airports, ("ICAO/IATA", code), ("Name", "Hub"));
        item.State = ItemState.Unknown;
        item.RemoteId = "102";
        var resource = w.Workspace.Resources[item.Resource];
        resource.Draft.Add(item.After);
        var job = JobOf(w, item);
        await Execute(w, job);
        Assert.Equal(ItemState.Unknown, item.State);
        Assert.False(item.Rebased);
        Assert.Null(resource.Draft[0].Identity);
        Assert.Empty(resource.Snapshot);
        Assert.Empty(w.Workspace.VerifiedOperations);

        item.RemoteId = "101";
        await Execute(w, job);
        Assert.Equal(ItemState.Succeeded, item.State);
        Assert.Equal("101", resource.Draft[0].Identity!.RemoteId);
        Assert.Equal(0, Writes(w));
    }

    [Theory]
    [InlineData("ambiguous")]
    [InlineData("foreign")]
    [InlineData("missing")]
    public async Task Routing_recovery_with_ambiguous_foreign_or_unknown_airports_cannot_confirm_the_record(string mode)
    {
        var w = new GoldenWorld(false);
        w.Remote.AddRow("routings/9", """{"id":9,"airline_id":7,"departure_airport_id":101,"arrival_airport_id":102,"route":"DCT"}""");
        if (mode == "ambiguous")
        {
            foreach (var json in new[] { London, Gatwick, London.Replace("101", "103", StringComparison.Ordinal) })
            {
                var row = Parse(w, ResourceKind.Airports, json);
                w.Workspace.Resources[ResourceKind.Airports].Snapshot.Add(row);
            }
        }
        else if (mode == "foreign")
        {
            var airports = w.Workspace.Resources[ResourceKind.Airports].Snapshot;
            foreach (var json in new[] { London, Gatwick })
                airports.Add(Parse(w, ResourceKind.Airports, json));
            airports[0] = airports[0].WithIdentity(new("101", "8"));
        }
        else
        {
            foreach (var json in new[] { Gatwick })
                w.Workspace.Resources[ResourceKind.Airports].Snapshot.Add(Parse(w, ResourceKind.Airports, json));
        }
        var item = Routing("LHR", "LGW");
        item.State = ItemState.Unknown;
        item.RemoteId = "9";
        // A fresh adapter, as the app builds one per run: the airport index is read from the snapshot when the adapter is created.
        await new OperationsBatchExecutor(ApiOver(w, w.Workspace), w.Workspace).ExecuteAsync(JobOf(w, item), () => Task.CompletedTask, Ct);
        Assert.Equal(ItemState.Unknown, item.State);
        Assert.False(item.Rebased);
        Assert.Empty(w.Workspace.VerifiedOperations);
        Assert.Equal(0, Writes(w));
    }

    [Fact]
    public async Task A_recovery_candidate_can_be_corrected_after_a_mismatch_without_rebinding()
    {
        var w = new GoldenWorld(false);
        w.Remote.AddRow("airports/101", London);
        w.Remote.AddRow("airports/102", Gatwick);
        var item = Create(ResourceKind.Airports, ("ICAO/IATA", "EGLL"), ("Name", "Hub"));
        item.State = ItemState.Unknown;
        item.RemoteId = "102";
        var job = JobOf(w, item);
        w.Workspace.Resources[item.Resource].Draft.Add(item.After);
        Assert.True(item.CanProposeRecoveryId);
        Assert.Equal("102", item.EditableRecoveryId);
        item.ProposeRecoveryId("102");
        await Execute(w, job);
        Assert.Equal(ItemState.Unknown, item.State);
        Assert.Null(item.RemoteId);
        Assert.Null(item.After.Identity);
        Assert.True(item.CanProposeRecoveryId);

        item.ProposeRecoveryId("101");
        await Execute(w, job);
        Assert.Equal(ItemState.Succeeded, item.State);
        Assert.Equal("101", item.RemoteId);
        Assert.Equal("101", item.After.Identity!.RemoteId);
        Assert.Null(item.RecoveryCandidateId);
        Assert.False(item.CanProposeRecoveryId);
        Assert.Contains(item.RecoveryCandidates, x => x.RemoteId == "102");
        Assert.Contains(item.RecoveryCandidates, x => x.RemoteId == "101");
        Assert.Equal(0, Writes(w));
        item.State = ItemState.Unknown;
        Assert.False(item.CanProposeRecoveryId);
        Assert.Throws<InvalidOperationException>(() => item.ProposeRecoveryId("102"));
    }

    // ---- airport references are compared by id ----------------------------------------------------------------------

    [Theory]
    [InlineData("Routings", "EGLL")]
    [InlineData("Routings", "lhr")]
    [InlineData("Routings", "101")]
    [InlineData("Routes", "EGLL")]
    [InlineData("Routes", "lhr")]
    [InlineData("Routes", "101")]
    public void Readback_of_routings_and_routes_compares_airport_ids_for_icao_iata_and_numeric_references(string kindName, string code)
    {
        var w = new GoldenWorld();
        var kind = Enum.Parse<ResourceKind>(kindName);
        var item = kind == ResourceKind.Routings ? Routing(code, "LGW") : ScheduledRoute(code, "LGW");
        var current = Parse(w, kind, w.Remote.Rows[kind == ResourceKind.Routings ? "routings/9" : "routes/10"].ToJsonString());
        Assert.True(w.Api.Matches(item, current, item.After, false));
        item.After = item.After.With("Departure Airport (ICAO/IATA)", "LGW");
        item.Fields["Departure Airport (ICAO/IATA)"] = new(FieldIntent.Set, "LGW");
        Assert.False(w.Api.Matches(item, current, item.After, false));
    }

    [Theory]
    [InlineData("Routings", "EGLL")]
    [InlineData("Routings", "lhr")]
    [InlineData("Routings", "101")]
    [InlineData("Routes", "EGLL")]
    [InlineData("Routes", "lhr")]
    [InlineData("Routes", "101")]
    public async Task A_create_between_the_same_airports_is_a_duplicate_whichever_way_the_airports_are_written(string kindName, string code)
    {
        var w = new GoldenWorld();
        var kind = Enum.Parse<ResourceKind>(kindName);
        var item = kind == ResourceKind.Routings ? Routing(code, "LGW") : ScheduledRoute(code, "LGW");
        await Execute(w, JobOf(w, item));
        Assert.Equal(ItemState.Failed, item.State);
        Assert.Equal("ApiDuplicate", Code(item));
        Assert.Equal(0, Writes(w));
    }

    // ---- typed values are read back by value ---------------------------------------------------------------------------

    [Theory]
    [InlineData("boolean")]
    [InlineData("numeric")]
    [InlineData("unsent-default")]
    public async Task Typed_create_readback_verifies_booleans_numbers_and_unsent_defaults(string mode)
    {
        var w = new GoldenWorld();
        var field = mode == "boolean" ? "Hide in Phoenix" : "Max Passengers";
        var value = mode == "boolean" ? "true" : mode == "numeric" ? "0189" : "";
        w.New(ResourceKind.Fleets, ("Name", "Typed"), ("Type Code", "B738"), ("Type (pax/cargo/...)", "pax"), ("Parameter IDs", "1"), (field, value));
        await w.RunAsync();
        var item = w.Job!.Items.Single();
        Assert.Equal(ItemState.Succeeded, item.State);
        Assert.NotNull(item.ApiExpectedValues);
        if (mode == "unsent-default")
        {
            Assert.DoesNotContain("max_pax", item.ApiExpectedValues!.Keys);
            Assert.Equal(0, Count(w, "PUT"));
        }
        else
        {
            var key = mode == "boolean" ? "hide_in_phoenix" : "max_pax";
            Assert.Equal(mode == "boolean" ? JsonValueKind.True : JsonValueKind.Number, item.ApiExpectedValues![key].ValueKind);
        }
    }

    [Theory]
    [InlineData("Hide in Phoenix", "true")]
    [InlineData("Max Passengers", "0189")]
    public async Task A_sparse_typed_update_expects_only_the_field_it_changed(string field, string value)
    {
        var w = new GoldenWorld();
        w.Edit(ResourceKind.Fleets, "4", (field, value));
        await w.RunAsync();
        var item = w.Job!.Items.Single();
        Assert.Equal(ItemState.Succeeded, item.State);
        Assert.Single(item.ApiExpectedValues!);
    }

    [Fact]
    public void Explicit_zero_and_an_emptied_collection_are_not_omitted_defaults()
    {
        var w = new GoldenWorld();
        var item = Create(ResourceKind.Fleets, ("Name", "Typed"), ("Type Code", "B738"), ("Type (pax/cargo/...)", "pax"), ("Max Passengers", "0"));
        var record = Parse(w, ResourceKind.Fleets, """{"id":4,"airline_id":7,"name":"Typed","code":"B738","type":"pax"}""");
        Assert.False(w.Api.Matches(item, record, item.After, false));

        var before = Parse(w, ResourceKind.Airports, """{"id":101,"airline_id":7,"icao":"EGLL","iata":"LHR","container_ids":[1]}""");
        var after = before.With("Container IDs", "");
        var clear = new ChangeItem { Resource = ResourceKind.Airports, Kind = ChangeKind.Update, Before = before, After = after, Fields = new() { ["Container IDs"] = new(FieldIntent.Clear, "") } };
        Assert.False(w.Api.Matches(clear, before, after, false));
        Assert.True(w.Api.Matches(clear, Parse(w, ResourceKind.Airports, before.RawApiJson!.Replace("[1]", "[]", StringComparison.Ordinal)), after, false));
    }

    // ---- caches ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_successful_permanent_delete_evicts_the_record_from_the_lookup_cache()
    {
        var w = new GoldenWorld();
        w.MarkDelete(ResourceKind.Aircraft, "8");
        await w.RunAsync();
        Assert.Equal(ItemState.Succeeded, w.Job!.Items.Single().State);
        var calls = w.Remote.Server.Requests.Count;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => w.Api.FindAsync(ResourceKind.Aircraft, "8", Ct));
        Assert.Equal("ApiRefreshFirst", MessageErrors.Describe(error).Code);
        Assert.Equal(calls, w.Remote.Server.Requests.Count);
    }
}
