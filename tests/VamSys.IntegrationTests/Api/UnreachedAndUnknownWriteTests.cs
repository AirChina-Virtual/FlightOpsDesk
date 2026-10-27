using System.Text.Json;
using VamSys.Core;
using VamSys.Infrastructure;
using VamSys.TestSupport;
using static VamSys.IntegrationTests.Api.PipelineHarness;
using static VamSys.IntegrationTests.Api.StoredWorld;

namespace VamSys.IntegrationTests.Api;

/// <summary>
/// What happens to a write that never left the machine, to an unanswered write that the server may or may not have applied, and
/// to a create that was only half finished. The rule everywhere: a write is sent at most once unless it provably never reached the
/// server; recovery only reads.
/// </summary>
[Collection(StoredWorldCollection.Name)]
public class UnreachedAndUnknownWriteTests
{
    // ---- writes that never reach the server ------------------------------------------------------------------------

    [Theory]
    [InlineData("token-network")]
    [InlineData("token-auth")]
    [InlineData("http-401")]
    public async Task A_write_that_never_reaches_the_server_or_is_refused_by_authentication_returns_to_pending_and_runs_once_later(string failure)
    {
        using var s = new StoredWorld();
        Exception? tokenFailure = null;
        var tokenFailures = 0;
        // short-lived tokens are asked for again with every request, so a token failure can hit just the DELETE
        var api = s.Adapter(s.W, clock => new TokenProvider(_ =>
        {
            if (tokenFailure is { } error) { tokenFailures++; return Task.FromException<AccessToken>(error); }
            return Task.FromResult(new AccessToken("short", clock.GetUtcNow().AddSeconds(30)));
        }, clock));
        var refusals = 0;
        s.Remote.Intercept = r =>
        {
            // the failure starts after the preflight GET, so only the DELETE's token request fails
            if (r.Method == "GET" && r.Path.EndsWith("/routes/10", StringComparison.Ordinal) && failure != "http-401")
                tokenFailure = failure == "token-network" ? new HttpRequestException("token endpoint unavailable") : new ApiAccessException(Messages.Define("ApiAuth"));
            if (r.Method == "DELETE" && failure == "http-401") { refusals++; return Status(401); }
            return null;
        };
        s.World.MarkDelete(ResourceKind.Routes, "10");
        var job = s.Submit();
        var item = job.Items.Single();

        await s.Run(job, api: api);

        // the failure was reached: a token request failed, or the server answered 401 to the DELETE
        Assert.True(failure == "http-401" ? refusals == 1 : tokenFailures > 0, "the injected failure was never reached");
        Assert.Equal(ItemState.Pending, item.State);
        Assert.All(item.ApiSteps, step => Assert.Equal(BatchStepState.Prepared, step.State));
        Assert.Equal(failure == "http-401" ? 1 : 0, Count(s.World, "DELETE"));
        Assert.Equal(failure == "token-network" ? JobStatus.NeedsAttention : JobStatus.Paused, job.StatusCode);
        var disk = s.Disk;
        Assert.Equal(ItemState.Pending, disk.Jobs.Single().Items.Single().State);
        Assert.False(WorkspaceStore.HasUnresolvedWrites(disk));

        tokenFailure = null;
        s.Remote.Intercept = null;
        await s.Run(job, api: api);

        Assert.Equal(ItemState.Succeeded, item.State);
        Assert.Equal(failure == "http-401" ? 2 : 1, Count(s.World, "DELETE"));
    }

    // ---- unanswered writes: only a read-only proof sets them free ---------------------------------------------------

    [Theory]
    [InlineData("update", false)]
    [InlineData("update", true)]
    [InlineData("create", false)]
    [InlineData("create", true)]
    public async Task An_unanswered_write_leaves_the_unknown_state_only_when_a_read_proves_it_did_not_take_effect(string kind, bool applied)
    {
        using var s = new StoredWorld();
        var attempts = 0;
        var method = kind == "update" ? "PUT" : "POST";
        s.Remote.Intercept = r =>
        {
            if (r.Method != method) return null;
            attempts++;
            if (applied) s.Remote.Respond(r);
            throw new TimeoutException("simulated");
        };
        if (kind == "update")
            s.World.Edit(ResourceKind.Airports, "101", ("Name", "Heathrow"));
        else
            s.World.New(ResourceKind.Fleets, ("Name", "Fleet 9"), ("Type Code", "A320"), ("Type (pax/cargo/...)", "cargo"), ("Parameter IDs", "1"));
        var job = s.Submit();
        await s.Run(job);
        s.Remote.Intercept = null;
        Assert.Equal(1, attempts);
        var item = job.Items.Single();
        Assert.Equal(ItemState.Unknown, item.State);
        Assert.False(item.WriteAccepted);
        var requests = Calls(s.World).Length;

        var proven = await new OperationsUnknownResolver(s.World.Api, s.W).ResolveNotAppliedAsync(job, item, s.Writer, Ct);

        Assert.Equal(!applied, proven);
        Assert.Equal(applied ? ItemState.Unknown : ItemState.Failed, job.Items.Single().State);
        Assert.NotEmpty(Calls(s.World).Skip(requests));
        Assert.All(s.Remote.Server.Requests.Skip(requests), r => Assert.Equal("GET", r.Method));
        var disk = s.Disk;
        Assert.Equal(job.Items.Single().State, disk.Jobs.Single().Items.Single().State);
        Assert.Equal(applied, WorkspaceStore.HasUnresolvedWrites(disk));
        Assert.Equal(1, Count(s.World, method));
    }

    [Theory]
    [InlineData("accepted")]
    [InlineData("soft-delete")]
    public async Task An_accepted_write_or_a_soft_delete_cannot_be_disproved_by_reading_and_is_refused(string kind)
    {
        StoredWorld s;
        if (kind == "soft-delete")
            s = await AcceptedSoftDelete();
        else
        {
            s = new StoredWorld();
            var answered = 0;
            // the PUT is answered with success but changes nothing: accepted, and the read-back differs
            s.Remote.Intercept = r => r.Method == "PUT" ? (++answered > 0 ? FakeResponse.Ok("""{"data":{}}""") : null) : null;
            s.World.Edit(ResourceKind.Airports, "101", ("Name", "Heathrow"));
            var created = s.Submit();
            await s.Run(created);
            s.Remote.Intercept = null;
            Assert.Equal(1, answered);
        }
        using var owner = s;
        var job = s.W.Jobs.Single();
        var item = job.Items.Single();
        Assert.Equal(ItemState.Unknown, item.State);
        Assert.True(item.WriteAccepted);
        var before = Json(job.Items);
        var requests = Calls(s.World).Length;

        Assert.False(OperationsUnknownResolver.CanCheck(item));
        await Expect.BlockedAsync(() => new OperationsUnknownResolver(s.World.Api, s.W).ResolveNotAppliedAsync(job, item, s.Writer, Ct), "ApiUnknown");

        Assert.Equal(before, Json(job.Items));
        Assert.Equal(requests, Calls(s.World).Length);
    }

    // ---- a half finished two-step create -----------------------------------------------------------------------------

    [Fact]
    public async Task A_legacy_two_step_create_whose_second_step_may_have_been_sent_is_not_sent_again()
    {
        using var s = new StoredWorld();
        // the POST was answered (record 90 exists) and the PUT of the second step was saved as in flight, so it may have been sent
        s.Remote.AddRow("fleet/90", """{"id":90,"airline_id":7,"name":"Legacy fleet","code":"B738","type":"pax","max_pax":0}""");
        var draft = s.World.New(ResourceKind.Fleets, ("Name", "Legacy fleet"), ("Type Code", "B738"), ("Type (pax/cargo/...)", "pax"), ("Max Passengers", "180"));
        var item = new ChangeItem
        {
            Resource = ResourceKind.Fleets,
            Kind = ChangeKind.Create,
            After = draft,
            Fields = draft.Fields.ToDictionary(p => p.Key, p => new FieldChange(FieldIntent.Set, p.Value)),
            State = ItemState.Unknown,
            CreateCompleted = true,
            RemoteId = "90",
            ApiSteps =
            [
                new BatchStep { Method = "POST", Path = "fleet", State = BatchStepState.ResponseReceived, RemoteId = "90",
                    Body = new() { ["name"] = JsonSerializer.SerializeToElement("Legacy fleet"), ["code"] = JsonSerializer.SerializeToElement("B738"), ["type"] = JsonSerializer.SerializeToElement("pax") } },
                new BatchStep { Method = "PUT", Path = "fleet/90", State = BatchStepState.InFlight, Body = new() { ["max_pax"] = JsonSerializer.SerializeToElement(180) } }
            ]
        };
        var job = new BatchJob { Mode = RunMode.Online, Items = [item] };
        s.W.Jobs.Add(job);
        s.Store.Save(s.W);

        await s.Run(job);

        Assert.Empty(s.Writes());
        Assert.Contains("GET fleet/90", Calls(s.World));
        Assert.Equal(ItemState.Unknown, job.Items.Single().State);
        Assert.Equal(0, s.Remote.Rows["fleet/90"]["max_pax"]!.GetValue<int>());
    }

    // ---- a numeric Client ID --------------------------------------------------------------------------------------

    static Func<TimeProvider, TokenProvider> ClientCredentialsFor(StoredWorld s, string clientId)
        => clock => TokenProvider.ClientCredentials(s.Remote.Server.CreateClient(), new OperationsCredentials(clientId, "fixture-secret"), clock);

    [Theory]
    [InlineData("1", ResourceKind.Routes, "10")]
    [InlineData("10", ResourceKind.Routes, "10")]
    [InlineData("103", ResourceKind.Airports, "103")]
    public async Task A_short_numeric_client_id_inside_the_api_path_or_ids_never_breaks_the_deletion_proof(string clientId, ResourceKind kind, string id)
    {
        using var s = new StoredWorld();
        var api = s.Adapter(s.W, ClientCredentialsFor(s, clientId));
        s.World.MarkDelete(kind, id);
        var job = s.Submit();

        await s.Run(job, api: api);

        var item = job.Items.Single();
        Assert.True(item.State == ItemState.Succeeded, $"{item.State} {item.DeletionVerification?.Outcome} {item.Description?.Code} {item.Message}");
        Assert.Equal("ApiInvisible", item.DeletionVerification!.Outcome);
        Assert.Equal(1, Count(s.World, "DELETE"));
        // the numbers of the path stay readable in the diagnostics instead of being masked as the client id
        var path = OperationsAdapter.BaseUri.AbsolutePath + OperationsAdapter.Collection(kind) + "/" + id;
        Assert.Equal(path, item.DiagnosticDetails!.Path);
        Assert.Equal(path, item.ReadbackDetails!.Path);
    }

    [Fact]
    public async Task A_numeric_client_id_never_rewrites_numbers_in_diagnostic_bodies_while_the_secret_stays_redacted()
    {
        using var s = new StoredWorld();
        var api = s.Adapter(s.W, ClientCredentialsFor(s, "10"));
        var rejected = 0;
        s.Remote.Intercept = r =>
        {
            if (r.Method != "DELETE") return null;
            rejected++;
            return FakeResponse.WithStatus(422, """{"message":"route 10 rejected fixture-secret","error_id":"e-13"}""");
        };
        s.World.MarkDelete(ResourceKind.Routes, "10");
        var job = s.Submit();

        await s.Run(job, api: api);

        Assert.Equal(1, rejected);
        var item = job.Items.Single();
        var details = item.DiagnosticDetails!;
        Assert.Equal(ItemState.Failed, item.State);
        Assert.Equal("route 10 rejected [REDACTED]", details.ServerMessage);
        Assert.Equal("e-13", details.ErrorId);
        Assert.DoesNotContain("fixture-secret", details.ResponseBody!, StringComparison.Ordinal);
        Assert.Contains("e-13", details.ResponseBody!, StringComparison.Ordinal);
    }
}
