using System.Text.Json;
using System.Text.Json.Nodes;
using VamSys.Core;
using VamSys.Infrastructure;
using VamSys.IntegrationTests.Golden.Requests;
using VamSys.TestSupport;
using static VamSys.IntegrationTests.Api.PipelineHarness;

namespace VamSys.IntegrationTests.Api;

/// <summary>
/// Behaviour that was found against the live Operations API in October 2026: kilogram capacities, scheduled fleet sets,
/// airline parameter approval, jumpseat identity, update timestamps and what the item keeps as evidence after an unknown write.
/// Each fault test asserts that the fault path was really reached, not only that the end state looks right.
/// </summary>
public class LiveCompatibilityTests
{
    const string Dep = "Departure Airport (ICAO/IATA)";
    const string Arr = "Arrival Airport (ICAO/IATA)";

    static ChangeItem Create(ResourceKind kind, params (string Key, string Value)[] fields)
    {
        var row = new DataRow { Fields = fields.ToDictionary(p => p.Key, p => p.Value) };
        return new() { Resource = kind, Kind = ChangeKind.Create, After = row, Fields = row.Fields.ToDictionary(p => p.Key, p => new FieldChange(FieldIntent.Set, p.Value)) };
    }

    /// <summary>Edit a draft row and describe the edit as the update item the planner would produce.</summary>
    static ChangeItem EditItem(GoldenWorld w, ResourceKind kind, string id, string field, string value)
    {
        var before = w.Workspace.Resources[kind].Snapshot.Single(r => r.Identity!.RemoteId == id).Dup();
        var after = before.With(field, value);
        w.Workspace.Resources[kind].ReplaceDraftRows([after]);
        return new() { Resource = kind, Kind = ChangeKind.Update, Before = before, After = after, Fields = new() { [field] = new(FieldIntent.Set, value) } };
    }

    static string BlockedWith(Action action)
    {
        var error = Assert.ThrowsAny<Exception>(action);
        return MessageErrors.Describe(error).Code ?? error.Message;
    }

    static FakeResponse Parameters(int airline, params int[] fleets)
        => FakeResponse.Ok(JsonSerializer.Serialize(new
        {
            data = new[] { new { id = 1, airline_id = airline, icao_code = "CAO", iata_code = "CA", approved = true, restricted = false, fleet_ids = fleets } },
            meta = new { next_cursor_url = (string?)null }
        }));

    static bool IsWrite(RecordedRequest r) => r.Method is "POST" or "PUT" or "PATCH" or "DELETE";

    // ---- kilogram capacities ----------------------------------------------------------------------------------

    [Fact]
    public void Kilogram_capacities_are_created_in_a_single_plain_request()
    {
        var w = new GoldenWorld();
        var fleet = Create(ResourceKind.Fleets, ("Name", "New"), ("Type Code", "B738"), ("Type (pax/cargo/...)", "cargo"), ("Max Freight", "22046"));
        var aircraft = Create(ResourceKind.Aircraft, ("Name", "Plane"), ("Registration", "QA-TEST"), ("Fleet ID", "4"), ("Freight", "2205"));
        foreach (var item in new[] { fleet, aircraft })
        {
            var steps = w.Api.Plan(item);
            var step = Assert.Single(steps);
            Assert.Equal(HttpMethod.Post, step.Method);
            Assert.DoesNotContain("weight_unit", step.Body.Keys);
            Assert.DoesNotContain("simbrief_overrides", step.Body.Keys);
        }
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("9223372036854775808")]
    public void An_aircraft_freight_that_is_not_a_whole_kilogram_count_never_leaves_the_machine(string value)
    {
        var w = new GoldenWorld();
        var aircraft = Create(ResourceKind.Aircraft, ("Name", "Plane"), ("Registration", "QA-TEST"), ("Fleet ID", "4"), ("Freight", value));
        Assert.Equal("ApiInvalid", BlockedWith(() => w.Api.Plan(aircraft)));
    }

    [Fact]
    public void Zero_freight_is_sent_as_zero()
    {
        var w = new GoldenWorld();
        var aircraft = Create(ResourceKind.Aircraft, ("Name", "Plane"), ("Registration", "QA-TEST"), ("Fleet ID", "4"), ("Freight", "0"));
        Assert.Equal(0L, Convert.ToInt64(w.Api.Plan(aircraft).Single().Body["cargo"]));
    }

    [Fact]
    public void A_csv_import_keeps_a_fractional_capacity_as_text_so_the_validator_can_flag_it_online()
    {
        var csv = new CsvAdapter();
        var rows = csv.Map(csv.Parse("Name,Max Freight\r\nNew,1.5\r\n"), new Dictionary<string, string>());
        Assert.Equal("1.5", rows.Single().Get("Max Freight"));
    }

    [Fact]
    public void An_update_sends_only_the_changed_field_and_keeps_the_raw_record_with_the_unsent_weight_properties()
    {
        var w = new GoldenWorld();
        using var raw = JsonDocument.Parse("""{"id":8,"airline_id":7,"fleet_id":4,"name":"Plane","registration":"QA-TEST","weight_unit":"lbs","simbrief_overrides":{"paxwgt":85}}""");
        var baseline = w.Api.FromJson(ResourceKind.Aircraft, raw.RootElement);
        var item = new ChangeItem
        {
            Resource = ResourceKind.Aircraft, Kind = ChangeKind.Update, Before = baseline, After = baseline.With("Name", "Changed"),
            Fields = new() { ["Name"] = new(FieldIntent.Set, "Changed") }
        };
        Assert.Single(w.Api.Plan(item).Single().Body);
        Assert.Contains("simbrief_overrides", baseline.RawApiJson!, StringComparison.Ordinal);
    }

    // ---- evidence of an unknown write -------------------------------------------------------------------------

    [Fact]
    public async Task Recovery_never_resends_and_keeps_the_original_write_error_even_when_its_own_reads_fail()
    {
        var w = new GoldenWorld();
        var failReads = false;
        w.Remote.Intercept = r =>
            r.Method == "PUT" ? FakeResponse.WithStatus(500, """{"message":"Server Error","error_id":"fixture-server-error"}""")
            : failReads && r.Method == "GET" ? FakeResponse.WithStatus(500, """{"message":"Read failed","error_id":"fixture-read-error"}""")
            : null;
        w.Edit(ResourceKind.Fleets, "4", ("Name", "Changed"));

        await w.RunAsync("first run");
        var item = w.Job!.Items.Single();
        Assert.Equal(ItemState.Unknown, item.State);
        Assert.Equal("fixture-server-error", item.DiagnosticDetails!.ErrorId);

        await w.ResumeAsync("recovery that reads the unchanged record");
        Assert.Equal(ItemState.Unknown, item.State);
        Assert.Equal("fixture-server-error", item.DiagnosticDetails!.ErrorId);
        Assert.Equal("GET", item.ReadbackDetails!.Method);
        Assert.Contains(item.VerificationDifferences, d => d.Field == "name");

        failReads = true;
        var reads = Count(w, "GET");
        await w.ResumeAsync("recovery whose reads fail");
        Assert.True(Count(w, "GET") > reads, "the failing read was not attempted");
        Assert.Equal(ItemState.Unknown, item.State);
        Assert.Equal("fixture-server-error", item.DiagnosticDetails!.ErrorId);
        Assert.Equal("fixture-read-error", item.ReadbackDetails!.ErrorId);
        Assert.Equal(1, Count(w, "PUT"));
    }

    // ---- scheduled fleet sets ---------------------------------------------------------------------------------

    [Fact]
    public async Task Scheduled_fleet_sets_go_from_one_fleet_to_two_to_one_and_back_with_string_ids_in_the_readback()
    {
        var w = new GoldenWorld();
        var stringReads = 0;
        w.Remote.Intercept = r =>
        {
            if (r.Method != "GET" || !r.Path.EndsWith("/routes/10", StringComparison.Ordinal))
                return null;
            var answer = w.Remote.Respond(r);
            var node = JsonNode.Parse(answer.Json!)!.AsObject();
            var ids = (JsonArray)node["data"]!["fleet_ids"]!;
            node["data"]!["fleet_ids"] = new JsonArray(ids.Select(v => (JsonNode?)JsonValue.Create(v!.ToString())).ToArray());
            stringReads++;
            return FakeResponse.Ok(node.ToJsonString());
        };
        foreach (var value in new[] { "4,5", "5", "4" })
        {
            w.Edit(ResourceKind.Routes, "10", ("Fleet IDs", value));
            await w.RunAsync("set " + value);
            var item = w.Job!.Items.Single();
            Assert.True(item.State == ItemState.Succeeded, item.Message);
        }
        Assert.True(stringReads >= 3, "the string-valued readback was not used");
        Assert.Equal(3, Count(w, "PUT"));
        Assert.Contains("Routes:Update:FleetIds:Scheduled", w.Workspace.VerifiedOperations);
        Assert.DoesNotContain("Routes:Update", w.Workspace.VerifiedOperations);

        var empty = EditItem(w, ResourceKind.Routes, "10", "Fleet IDs", "");
        Assert.Equal("ApiClear", BlockedWith(() => w.Api.Plan(empty)));
    }

    [Fact]
    public void A_generic_route_update_verification_does_not_lift_the_sample_limit_of_scheduled_fleet_sets()
    {
        var w = new GoldenWorld();
        var items = Enumerable.Range(0, 4).Select(_ => EditItem(w, ResourceKind.Routes, "10", "Fleet IDs", "5")).ToList();
        var plan = ReviewPlanBuilder.Build(items, RunMode.Online, true, new HashSet<string> { "Routes:Update" }, _ => { });
        Assert.Equal(3, plan.SubmitItems.Count);
        Assert.Equal(1, plan.Entries.Count(e => e.Disposition == ReviewDisposition.Deferred));
    }

    // ---- airline parameter approval ---------------------------------------------------------------------------

    [Theory]
    [InlineData("a fleet missing from the approved list")]
    [InlineData("a refused parameter read")]
    [InlineData("a parameter of another airline")]
    public async Task A_fleet_set_without_approval_is_stopped_before_any_write_while_unrelated_writes_go_on(string situation)
    {
        var w = new GoldenWorld();
        w.Remote.Intercept = r => r.Method == "GET" && r.Path.EndsWith("/parameters", StringComparison.Ordinal)
            ? situation switch
            {
                "a fleet missing from the approved list" => Parameters(7),
                "a refused parameter read" => FakeResponse.WithStatus(403, "{}"),
                _ => Parameters(8, 4, 5, 6)
            }
            : null;
        w.Edit(ResourceKind.Routes, "10", ("Fleet IDs", "5"));
        w.Edit(ResourceKind.Airports, "101", ("Name", "Heathrow"));

        await w.RunAsync();

        var route = w.Job!.Items.Single(i => i.Resource == ResourceKind.Routes);
        var airport = w.Job.Items.Single(i => i.Resource == ResourceKind.Airports);
        Assert.Contains(w.Remote.Server.Requests, r => r.Path.EndsWith("/parameters", StringComparison.Ordinal));
        Assert.Equal(ItemState.Failed, route.State);
        Assert.DoesNotContain(w.Remote.Server.Requests, r => IsWrite(r) && r.Path.EndsWith("/routes/10", StringComparison.Ordinal));
        Assert.Equal(ItemState.Succeeded, airport.State);
        Assert.Equal(1, Count(w, "PUT"));
        Assert.NotNull(await w.Api.FindAsync(ResourceKind.Fleets, "4", Ct));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("extra")]
    [InlineData("duplicate")]
    public async Task A_scheduled_readback_with_a_missing_extra_or_repeated_fleet_is_not_believed(string mismatch)
    {
        var w = new GoldenWorld();
        var corrupted = 0;
        w.Remote.Intercept = r =>
        {
            // Only the answers after the write are altered; the preflight read sees an intact record.
            if (r.Method != "GET" || !r.Path.EndsWith("/routes/10", StringComparison.Ordinal) || Count(w, "PUT") == 0)
                return null;
            var node = JsonNode.Parse(w.Remote.Respond(r).Json!)!.AsObject();
            var members = (JsonArray)node["data"]!["fleet_ids"]!;
            if (mismatch == "missing") members.Clear();
            else members.Add(mismatch == "extra" ? 99 : members[0]!.DeepClone());
            corrupted++;
            return FakeResponse.Ok(node.ToJsonString());
        };
        w.Edit(ResourceKind.Routes, "10", ("Fleet IDs", "5"));

        await w.RunAsync();

        var item = w.Job!.Items.Single();
        Assert.True(corrupted > 0, "the altered readback was never served");
        Assert.Equal(ItemState.Unknown, item.State);
        Assert.NotEmpty(item.VerificationDifferences);
        Assert.Equal(1, Count(w, "PUT"));
    }

    [Fact]
    public async Task A_planned_fleet_has_to_name_a_parameter_unless_it_was_saved_before_the_rule_existed()
    {
        var w = new GoldenWorld();
        w.New(ResourceKind.Fleets, ("Name", "New"), ("Type Code", "B738"), ("Type (pax/cargo/...)", "cargo"), ("Max Freight", "1000"));
        var planned = new ChangePlanner().PlanWorkspace(w.Workspace).Single(i => i.Resource == ResourceKind.Fleets);
        Assert.True(planned.RequiresAirlineParameter);
        Assert.Equal("ApiParameterRequired", BlockedWith(() => w.Api.Plan(planned)));
        Assert.False(JsonSerializer.Deserialize<ChangeItem>("""{"Resource":1,"Kind":0}""")!.RequiresAirlineParameter);

        planned.After = planned.After.With("Parameter IDs", "1");
        planned.Fields["Parameter IDs"] = new(FieldIntent.Set, "1");
        var job = new BatchJob { Mode = RunMode.Online, Items = [planned] };
        w.Workspace.Jobs.Add(job);
        w.Workspace.Resources[ResourceKind.Fleets].Draft.Clear();
        w.Workspace.Resources[ResourceKind.Fleets].Draft.Add(planned.After);
        await new OperationsBatchExecutor(w.Api, w.Workspace).ExecuteAsync(job, () => Task.CompletedTask, Ct);
        Assert.True(planned.State == ItemState.Succeeded, planned.Message);
        using var body = JsonDocument.Parse(w.Remote.Server.RequestsFor(HttpMethod.Post, "fleet").Single().Body!);
        Assert.Equal(JsonValueKind.String, body.RootElement.GetProperty("parameter_ids")[0].ValueKind);
    }

    [Fact]
    public void Existing_fleets_cannot_be_given_parameters_or_allowed_prefixes()
    {
        var w = new GoldenWorld();
        Assert.Equal("ApiParameterCreateOnly", BlockedWith(() => w.Api.Plan(EditItem(w, ResourceKind.Fleets, "4", "Parameter IDs", "1"))));
        Assert.Equal("ApiUnsupported", BlockedWith(() => w.Api.Plan(EditItem(w, ResourceKind.Fleets, "4", "Allowed Prefix IDs", "1"))));
    }

    // ---- jumpseats --------------------------------------------------------------------------------------------

    static void NewJumpseat(GoldenWorld w)
        => w.New(ResourceKind.Routes, ("Type", "jumpseat"), ("Callsign", "CAO1234"), ("Flight Number", "CA1234"),
            (Dep, "EGKK"), (Arr, "EGCC"), ("Remarks", "fixture jumpseat"), ("Is Hidden", "TRUE"));

    [Fact]
    public async Task A_created_jumpseat_sends_its_identity_and_comes_back_with_explicit_nulls_that_the_workspace_keeps_valid()
    {
        var w = new GoldenWorld();
        NewJumpseat(w);
        await w.RunAsync();
        var item = w.Job!.Items.Single();
        Assert.True(item.State == ItemState.Succeeded, item.Message);
        Assert.Equal("CAO1234", item.ApiExpectedValues!["callsign"].GetString());
        using var request = JsonDocument.Parse(w.Remote.Server.RequestsFor(HttpMethod.Post, "routes").Single().Body!);
        Assert.Equal("CAO1234", request.RootElement.GetProperty("callsign").GetString());
        var stored = w.Workspace.Resources[ResourceKind.Routes].Draft.Single(r => r.Identity?.RemoteId == "90");
        Assert.Equal("", stored.Get("Callsign"));
        Assert.DoesNotContain(Schemas.Validate(w.Workspace, ResourceKind.Routes), i => i.RowId == item.After.LocalId);
    }

    [Theory]
    [InlineData("another callsign")]
    [InlineData("callsign absent")]
    public async Task A_created_jumpseat_whose_readback_has_the_wrong_identity_stays_unknown(string fault)
    {
        var w = new GoldenWorld();
        var altered = false;
        w.Remote.Intercept = r =>
        {
            if (r.Method != "POST" || !r.Path.EndsWith("/routes", StringComparison.Ordinal))
                return null;
            var answer = w.Remote.Respond(r);
            var row = w.Remote.Rows["routes/90"];
            if (fault == "another callsign") row["callsign"] = "OTHER"; else row.Remove("callsign");
            altered = true;
            return new FakeResponse(answer.Status, row.ToJsonString());
        };
        NewJumpseat(w);
        await w.RunAsync();
        Assert.True(altered);
        Assert.Equal(ItemState.Unknown, w.Job!.Items.Single().State);
    }

    [Theory]
    [InlineData("none", true)]
    [InlineData("existing", false)]
    [InlineData("old", false)]
    [InlineData("missing", false)]
    public async Task A_jumpseat_post_with_a_lost_answer_is_recovered_by_a_new_id_only_with_candidate_and_time_evidence(string invalid, bool recovered)
    {
        var w = new GoldenWorld();
        w.Remote.Intercept = r =>
        {
            if (r.Method != "POST" || !r.Path.EndsWith("/routes", StringComparison.Ordinal))
                return null;
            w.Remote.Respond(r);                 // the server did create the record (routes/90 is registered) ...
            return Status(500);                  // ... but the answer was an error
        };
        NewJumpseat(w);
        await w.RunAsync("first run");
        var item = w.Job!.Items.Single();
        Assert.Equal(ItemState.Unknown, item.State);
        Assert.Null(item.RemoteId);
        Assert.NotNull(item.PreCreateCandidateIds);
        Assert.True(w.Remote.Rows.ContainsKey("routes/90"));
        var started = item.ApiSteps[0].StartedAt!.Value;
        w.Remote.Rows["routes/90"]["created_at"] = (invalid == "old" ? started.AddYears(-6) : started.AddSeconds(1)).ToString("O");
        if (invalid == "existing") item.PreCreateCandidateIds!.Add("90");
        if (invalid == "missing") item.ApiSteps[0].StartedAt = null;
        w.Remote.Intercept = null;

        item.ProposeRecoveryId("90");
        await w.ResumeAsync("recovery");

        Assert.Contains("GET routes/90", Calls(w));
        Assert.Equal(recovered ? ItemState.Succeeded : ItemState.Unknown, item.State);
        Assert.Equal(1, Count(w, "POST"));
    }

    // ---- update timestamps ------------------------------------------------------------------------------------

    [Fact]
    public async Task A_changed_updated_at_alone_is_not_a_conflict_but_a_changed_business_field_is()
    {
        var w = new GoldenWorld();
        w.Remote.Rows["fleet/4"]["updated_at"] = "2026-10-20T12:27:34Z";
        w.Edit(ResourceKind.Fleets, "4", ("Name", "Changed"));
        await w.RunAsync("independent edit");
        var item = w.Job!.Items.Single();
        Assert.Equal(ItemState.Succeeded, item.State);
        Assert.Empty(item.PreflightComparison!.BusinessFields);
        Assert.NotEqual(item.PreflightComparison.PreviousUpdatedAt, item.PreflightComparison.CurrentUpdatedAt);

        // a refresh whose only difference is the timestamp keeps the local edit and raises no conflict
        var row = w.Workspace.Resources[ResourceKind.Fleets].Snapshot.Single(r => r.Identity!.RemoteId == "4");
        var data = new ResourceData { Snapshot = [row.Dup()], Draft = [row.Dup().With("Name", "Local")] };
        w.Remote.Rows["fleet/4"]["updated_at"] = "2026-10-21T00:00:00Z";
        using var doc = JsonDocument.Parse(w.Remote.Rows["fleet/4"].ToJsonString());
        var incoming = w.Api.FromJson(ResourceKind.Fleets, doc.RootElement);
        Assert.NotEqual(row.RawApiJson, incoming.RawApiJson);
        SnapshotMerger.Merge(ResourceKind.Fleets, data, [incoming]);
        Assert.Empty(data.Conflicts);
        Assert.Equal("Local", data.Draft[0].Get("Name"));

        w.Edit(ResourceKind.Fleets, "4", ("Name", "Local"));
        w.Remote.Rows["fleet/4"]["name"] = "Remote";
        await w.RunAsync("business conflict");
        var conflict = w.Job!.Items.Single();
        Assert.Equal(ItemState.Conflict, conflict.State);
        Assert.Contains(conflict.PreflightComparison!.BusinessFields, f => f.Field == "Name");
    }
}
