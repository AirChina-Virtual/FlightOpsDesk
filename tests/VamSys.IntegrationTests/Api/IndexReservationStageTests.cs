using VamSys.Core;
using VamSys.Infrastructure;
using VamSys.IntegrationTests.Golden.Requests;
using VamSys.TestSupport;
using static VamSys.IntegrationTests.Api.PipelineHarness;

namespace VamSys.IntegrationTests.Api;

/// <summary>
/// The duplicate index across a whole job: names and airport aliases of writes whose outcome is unknown stay reserved (also
/// after the program restarts and whatever way a recovery read fails), and are released only by a confirmation.
/// </summary>
public class IndexReservationStageTests
{
    static (string, string)[] NewFleet(string name) => [("Name", name), ("Type Code", "B738"), ("Type (pax/cargo/...)", "pax"), ("Max Passengers", "189"), ("Parameter IDs", "1")];

    /// <summary>A new draft row placed first, so that the index is read before the rows that follow are submitted.</summary>
    static DataRow NewFirst(GoldenWorld w, ResourceKind kind, params (string, string)[] fields)
    {
        var row = new DataRow { Fields = fields.ToDictionary(p => p.Item1, p => p.Item2) };
        w.Workspace.Resources[kind].Draft.Insert(0, row);
        return row;
    }

    static int Lists(GoldenWorld w, string collection)
        => w.Remote.Server.Requests.Count(r => r.Method == "GET" && r.Path.EndsWith("/" + collection, StringComparison.Ordinal));

    static Task Run(Workspace workspace, OperationsAdapter api, BatchJob job, CancellationToken? ct = null)
        => new OperationsBatchExecutor(api, workspace).ExecuteAsync(job, () => Task.CompletedTask, ct ?? Ct);

    // ---- names -----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Renamed", false)]
    [InlineData("Fleet 4", true)]
    public async Task A_create_after_a_rename_is_checked_against_the_new_name_without_reading_the_list_again(string candidate, bool succeeds)
    {
        var w = new GoldenWorld();
        NewFirst(w, ResourceKind.Fleets, NewFleet("Seed"));
        w.Edit(ResourceKind.Fleets, "4", ("Name", "Renamed"));
        w.New(ResourceKind.Fleets, NewFleet(candidate));
        await w.RunAsync();
        var items = w.Job!.Items;
        Assert.Equal([ChangeKind.Create, ChangeKind.Update, ChangeKind.Create], items.Select(i => i.Kind));
        Assert.Equal(ItemState.Succeeded, items[0].State);
        Assert.Equal(ItemState.Succeeded, items[1].State);
        Assert.Equal(succeeds ? ItemState.Succeeded : ItemState.Failed, items[2].State);
        if (!succeeds)
            Assert.Equal("ApiDuplicate", Code(items[2]));
        Assert.Equal(succeeds ? 2 : 1, Count(w, "POST"));
        Assert.Equal(1, Lists(w, "fleet"));
        Assert.Equal(1, w.Remote.Rows.Count(p => p.Key.StartsWith("fleet/", StringComparison.Ordinal) && p.Key.Count(c => c == '/') == 1 && p.Value["name"]!.ToString() == "Renamed"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_unknown_rename_keeps_both_names_reserved_across_a_restart_until_it_is_resolved(bool appliedOnServer)
    {
        var w = new GoldenWorld();
        NewFirst(w, ResourceKind.Fleets, NewFleet("Seed"));
        w.Edit(ResourceKind.Fleets, "4", ("Name", "Renamed"));
        w.New(ResourceKind.Fleets, NewFleet("Fleet 4"));
        w.New(ResourceKind.Fleets, NewFleet("Renamed"));
        w.New(ResourceKind.Fleets, NewFleet("Independent"));
        w.Remote.Intercept = r =>
        {
            if (r.Method != "PUT")
                return null;
            if (appliedOnServer)
                w.Remote.Respond(r);
            throw new TimeoutException("simulated");
        };

        await w.RunAsync("first run");
        var job = w.Job!;
        Assert.Equal([ItemState.Succeeded, ItemState.Unknown, ItemState.Pending, ItemState.Pending, ItemState.Succeeded], job.Items.Select(i => i.State));
        Assert.Equal(2, Count(w, "POST"));
        Assert.Equal(1, Count(w, "PUT"));

        var (restored, api) = Restart(w, w.Workspace);
        var restoredJob = restored.Jobs.Single();
        if (!appliedOnServer)
        {
            // the record on the server still reads as before: nothing is proven, nothing is released, nothing is sent again
            await Run(restored, api, restoredJob);
            Assert.Equal(ItemState.Unknown, restoredJob.Items[1].State);
            Assert.Equal(2, Count(w, "POST"));
            Assert.Equal(1, Count(w, "PUT"));
        }

        // the user settles the uncertain update on the server (it is now named as asked)
        w.Remote.Intercept = null;
        w.Remote.Rows["fleet/4"]["name"] = "Renamed";
        await Run(restored, api, restoredJob);
        Assert.Equal([ItemState.Succeeded, ItemState.Succeeded, ItemState.Succeeded, ItemState.Failed, ItemState.Succeeded], restoredJob.Items.Select(i => i.State));
        Assert.Equal("ApiDuplicate", Code(restoredJob.Items[3]));
        Assert.Equal(3, Count(w, "POST")); // the old name is free again, the new one is not
        Assert.Equal(1, Count(w, "PUT"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task An_unknown_airport_create_blocks_every_later_airport_create_whatever_alias_it_uses_and_survives_a_restart(bool reverse, bool answerReceived)
    {
        var w = new GoldenWorld();
        w.New(ResourceKind.Airports, ("ICAO/IATA", reverse ? "KJFK" : "JFK"), ("Name", "Hub"));
        w.New(ResourceKind.Airports, ("ICAO/IATA", reverse ? "JFK" : "KJFK"), ("Name", "Hub"));
        w.New(ResourceKind.Fleets, NewFleet("Independent"));
        const string Created = """{"id":90,"airline_id":7,"icao":"KJFK","iata":"JFK","name":"Hub"}""";
        var readFails = true;
        w.Remote.Intercept = r =>
        {
            if (r.Method == "POST" && r.Path.EndsWith("/airports", StringComparison.Ordinal))
            {
                w.Remote.AddRow("airports/90", Created);
                return answerReceived ? FakeResponse.WithStatus(201, Created) : throw new TimeoutException("simulated");
            }
            if (readFails && r.Method == "GET" && r.Path.Contains("/airports/", StringComparison.Ordinal))
                throw new TimeoutException("simulated");
            return null;
        };

        await w.RunAsync("first run");
        var job = w.Job!;
        Assert.Equal([ItemState.Unknown, ItemState.Pending, ItemState.Succeeded], job.Items.Select(i => i.State));
        Assert.Equal("ApiAirportCreatePending", Code(job.Items[1]));
        Assert.Equal(1, w.Remote.Server.Requests.Count(r => r.Method == "POST" && r.Path.EndsWith("/airports", StringComparison.Ordinal)));

        var (restored, api) = Restart(w, w.Workspace);
        var restoredJob = restored.Jobs.Single();
        await Run(restored, api, restoredJob);
        Assert.Equal(ItemState.Unknown, restoredJob.Items[0].State);
        Assert.Equal(ItemState.Pending, restoredJob.Items[1].State);
        Assert.Equal(1, w.Remote.Server.Requests.Count(r => r.Method == "POST" && r.Path.EndsWith("/airports", StringComparison.Ordinal)));

        readFails = false;
        if (!answerReceived)
        {
            // an id that is not the airport sent proves nothing; the right one does
            restoredJob.Items[0].ProposeRecoveryId("102");
            await Run(restored, api, restoredJob);
            Assert.Equal(ItemState.Unknown, restoredJob.Items[0].State);
            Assert.Null(restoredJob.Items[0].After.Identity);
            Assert.Equal(ItemState.Pending, restoredJob.Items[1].State);
            restoredJob.Items[0].ProposeRecoveryId("90");
        }
        await Run(restored, api, restoredJob);
        Assert.Equal(ItemState.Succeeded, restoredJob.Items[0].State);
        Assert.Equal(ItemState.Failed, restoredJob.Items[1].State); // the other alias is the airport that now exists
        Assert.Equal(1, w.Remote.Server.Requests.Count(r => r.Method == "POST" && r.Path.EndsWith("/airports", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("cancel")]
    [InlineData("401")]
    [InlineData("403")]
    [InlineData("429")]
    [InlineData("500")]
    [InlineData("json")]
    public async Task A_recovery_read_that_fails_in_any_way_never_releases_a_reservation_or_repeats_a_write(string mode)
    {
        var w = new GoldenWorld();
        ChangeItem NewAirport(string code)
        {
            var row = new DataRow { Fields = new Dictionary<string, string> { ["ICAO/IATA"] = code, ["Name"] = "Hub" } };
            return new ChangeItem { Resource = ResourceKind.Airports, Kind = ChangeKind.Create, After = row, Fields = row.Fields.ToDictionary(p => p.Key, p => new FieldChange(FieldIntent.Set, p.Value)) };
        }
        var unknown = NewAirport("KJFK");
        unknown.State = ItemState.Unknown;
        unknown.ProposeRecoveryId("90");
        var job = new BatchJob { Mode = RunMode.Online, Items = [unknown, NewAirport("EGSS")] };
        w.Workspace.Jobs.Add(job);
        w.Remote.Intercept = r => mode switch
        {
            "timeout" => throw new TimeoutException("simulated"),
            "cancel" => throw new OperationCanceledException(),
            "json" => FakeResponse.Ok("invalid"),
            "429" => RateLimited(),
            _ => Status(int.Parse(mode))
        };

        var api = w.Api;
        var workspace = w.Workspace;
        for (var round = 0; round < 2; round++)
        {
            await Run(workspace, api, job);
            Assert.Equal(ItemState.Unknown, job.Items[0].State);
            Assert.Equal(ItemState.Pending, job.Items[1].State);
            Assert.All(w.Remote.Server.Requests, r => Assert.Equal("GET", r.Method));
            Assert.Equal(JobStatus.NeedsAttention, job.StatusCode);
            (workspace, api) = Restart(w, workspace);
            job = workspace.Jobs.Single();
        }

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var calls = w.Remote.Server.Requests.Count;
        await Run(workspace, api, job, canceled.Token);
        Assert.Equal(calls, w.Remote.Server.Requests.Count);
        Assert.Equal(ItemState.Unknown, job.Items[0].State);
        Assert.Equal(JobStatus.Canceled, job.StatusCode);
    }

    /// <summary>The program started again: the workspace comes back from its saved form and the adapter has no memory of the run.</summary>
    static (Workspace Workspace, OperationsAdapter Api) Restart(GoldenWorld w, Workspace current)
    {
        var restored = Workspace.Deserialize(current.Serialize());
        var requests = InstantTime.Requests();
        var tokens = new TokenProvider(_ => Task.FromResult(new AccessToken("golden-token", requests.Clock.GetUtcNow().AddHours(1))), requests.Clock);
        return (restored, new OperationsAdapter(new OperationsTransport(w.Remote.Server.CreateClient(), OperationsAdapter.BaseUri, "golden-va", tokens, requests), restored));
    }

    // ---- rows saved by an earlier version -----------------------------------------------------------------------------

    [Fact]
    public async Task A_completed_create_of_an_earlier_version_needs_no_raw_record_and_the_next_item_starts_a_fresh_index()
    {
        var w = new GoldenWorld();
        var old = new DataRow { Fields = new Dictionary<string, string> { ["ICAO/IATA"] = "LHR" } }.WithIdentity(new("101", "7"));
        var completed = new ChangeItem
        {
            Resource = ResourceKind.Airports, Kind = ChangeKind.Create, After = old, State = ItemState.Succeeded, CreateCompleted = true, RemoteId = "101",
            Fields = old.Fields.ToDictionary(p => p.Key, p => new FieldChange(FieldIntent.Set, p.Value))
        };
        var row = new DataRow { Fields = NewFleet("New").ToDictionary(p => p.Item1, p => p.Item2) };
        var next = new ChangeItem
        {
            Resource = ResourceKind.Fleets, Kind = ChangeKind.Create, After = row,
            Fields = row.Fields.ToDictionary(p => p.Key, p => new FieldChange(FieldIntent.Set, p.Value))
        };
        var job = new BatchJob { Mode = RunMode.Online, Items = [completed, next] };
        w.Workspace.Jobs.Add(job);
        await Execute(w, job);
        Assert.Equal(ItemState.Succeeded, next.State);
        Assert.Equal(1, Count(w, "POST"));
        Assert.Equal(1, Lists(w, "fleet"));
    }
}
