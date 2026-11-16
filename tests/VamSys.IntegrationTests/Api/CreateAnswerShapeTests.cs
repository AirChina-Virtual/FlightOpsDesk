using VamSys.Core;
using VamSys.IntegrationTests.Golden.Requests;
using VamSys.TestSupport;
using static VamSys.IntegrationTests.Api.PipelineHarness;

namespace VamSys.IntegrationTests.Api;

/// <summary>
/// The ID of a created record is read from the create's own answer in either shape the server uses: the bare record
/// (routings) or the record wrapped in "data" (airports, fleets, aircraft, routes since 2026-11-16, docs/API-CONTRACTS.md).
/// An item that waits for a create in the same job then goes ahead in the same pass. When a create's answer cannot be
/// used at all, the end-of-round lookup settles it and the items skipped because of it run in that same round.
/// </summary>
public class CreateAnswerShapeTests
{
    const string Dep = "Departure Airport (ICAO/IATA)", Arr = "Arrival Airport (ICAO/IATA)";

    static void NewRecord(GoldenWorld w, ResourceKind kind)
    {
        switch (kind)
        {
            case ResourceKind.Airports: w.New(kind, ("ICAO/IATA", "EGPH"), ("Name", "Edinburgh")); break;
            case ResourceKind.Fleets: w.New(kind, ("Name", "Fleet 9"), ("Type Code", "A320"), ("Type (pax/cargo/...)", "cargo"), ("Parameter IDs", "1")); break;
            case ResourceKind.Aircraft: w.New(kind, ("Fleet ID", "4"), ("Name", "Plane Three"), ("Registration", "QA-NEW")); break;
            case ResourceKind.Routings: w.New(kind, (Dep, "EGLL"), (Arr, "EGKK"), ("Route String", "UL2")); break;
            case ResourceKind.Routes: w.New(kind, (Dep, "EGLL"), (Arr, "EGKK"), ("Type", "scheduled"), ("Callsign", "CAO900"), ("Flight Number", "CA900"), ("Fleet IDs", "4")); break;
        }
    }

    // swapped: false = the live shape (wrapped, routings bare), true = the other shape, which must still be understood.
    [Theory]
    [InlineData(ResourceKind.Airports, false)]
    [InlineData(ResourceKind.Airports, true)]
    [InlineData(ResourceKind.Fleets, false)]
    [InlineData(ResourceKind.Fleets, true)]
    [InlineData(ResourceKind.Aircraft, false)]
    [InlineData(ResourceKind.Aircraft, true)]
    [InlineData(ResourceKind.Routings, false)]
    [InlineData(ResourceKind.Routings, true)]
    [InlineData(ResourceKind.Routes, false)]
    [InlineData(ResourceKind.Routes, true)]
    public async Task The_created_id_is_read_from_the_answer_in_either_shape(ResourceKind kind, bool swapped)
    {
        var w = new GoldenWorld();
        w.Remote.SwapCreateShapes = swapped;
        NewRecord(w, kind);

        await w.RunAsync();

        var item = w.Job!.Items.Single();
        Assert.Equal(ItemState.Succeeded, item.State);
        Assert.Equal("90", item.RemoteId);
        var post = Assert.Single(item.ApiSteps, s => s.Method == "POST");
        Assert.Equal(BatchStepState.ResponseReceived, post.State);
        Assert.Equal("90", post.RemoteId);
        Assert.True(item.CreateCompleted);
        Assert.True(item.WriteAccepted);
        Assert.Empty(item.RecoveryCandidates);
        Assert.Equal(1, Count(w, "POST"));
    }

    // The live run of 2026-11-16: an aircraft and a scheduled route that use a fleet created in the same job.
    [Fact]
    public async Task Items_that_use_a_fleet_created_in_the_same_job_go_ahead_in_the_same_pass()
    {
        var w = new GoldenWorld();
        var fleet = w.New(ResourceKind.Fleets, ("Name", "Fleet 9"), ("Type Code", "A320"), ("Type (pax/cargo/...)", "cargo"), ("Parameter IDs", "1"));
        w.New(ResourceKind.Aircraft, ("Name", "Plane Three"), ("Registration", "QA-NEW"), ("Fleet ID", LocalReference.Format(fleet.LocalId)));
        w.New(ResourceKind.Routes, (Dep, "EGLL"), (Arr, "EGKK"), ("Type", "scheduled"), ("Callsign", "CAO900"), ("Flight Number", "CA900"), ("Fleet IDs", LocalReference.Format(fleet.LocalId)));

        await w.RunAsync();

        Assert.All(w.Job!.Items, i => Assert.Equal(ItemState.Succeeded, i.State));
        Assert.All(w.Job.Items, i => Assert.Empty(i.RecoveryCandidates));
        Assert.Equal(JobStatus.Completed, w.Job.StatusCode);
        Assert.Equal(3, Count(w, "POST"));
        Assert.Contains(w.Remote.Server.Requests, r => r.Method == "POST" && r.Path.EndsWith("/fleet/90/aircraft", StringComparison.Ordinal));
    }

    // The fleet's create answer cannot be used (no id in either shape): the fleet ends the pass Unknown and its
    // dependents are skipped; the end-of-round lookup binds the fleet, and the dependents then run in the same round.
    [Fact]
    public async Task Items_skipped_for_a_create_the_end_of_round_lookup_settles_run_in_the_same_round()
    {
        var w = new GoldenWorld();
        w.Remote.Intercept = r =>
        {
            if (r.Method != "POST" || !r.Path.EndsWith("/fleet", StringComparison.Ordinal))
                return null;
            w.Remote.Respond(r);
            w.Remote.Rows["fleet/90"]["created_at"] = DateTimeOffset.UtcNow.ToString("O");
            return FakeResponse.WithStatus(201, """{"result":"created"}""");
        };
        var fleet = w.New(ResourceKind.Fleets, ("Name", "Fleet 9"), ("Type Code", "A320"), ("Type (pax/cargo/...)", "cargo"), ("Parameter IDs", "1"));
        w.New(ResourceKind.Aircraft, ("Name", "Plane Three"), ("Registration", "QA-NEW"), ("Fleet ID", LocalReference.Format(fleet.LocalId)));
        w.New(ResourceKind.Routes, (Dep, "EGLL"), (Arr, "EGKK"), ("Type", "scheduled"), ("Callsign", "CAO900"), ("Flight Number", "CA900"), ("Fleet IDs", LocalReference.Format(fleet.LocalId)));

        await w.RunAsync();

        var items = w.Job!.Items;
        Assert.All(items, i => Assert.Equal(ItemState.Succeeded, i.State));
        Assert.Equal(JobStatus.Completed, w.Job.StatusCode);
        Assert.Equal(("90", "Automatic"), (Assert.Single(items[0].RecoveryCandidates).RemoteId, items[0].RecoveryCandidates[0].Source));
        // every create was sent exactly once; the fleet was only read after its POST
        Assert.Equal(3, Count(w, "POST"));
        Assert.Equal(1, w.Remote.Server.Requests.Count(r => r.Method == "POST" && r.Path.EndsWith("/fleet", StringComparison.Ordinal)));
        var requests = w.Remote.Server.Requests.ToList();
        var fleetPost = requests.FindIndex(r => r.Method == "POST" && r.Path.EndsWith("/fleet", StringComparison.Ordinal));
        var aircraftPost = requests.FindIndex(r => r.Method == "POST" && r.Path.EndsWith("/fleet/90/aircraft", StringComparison.Ordinal));
        Assert.True(aircraftPost > fleetPost);
        Assert.All(requests.Skip(fleetPost + 1).Take(aircraftPost - fleetPost - 1), r => Assert.Equal("GET", r.Method));
    }

    // A dependent whose other dependency failed is not run: only the items that were waiting on the settled create go.
    [Fact]
    public async Task A_dependent_with_another_unfinished_dependency_stays_skipped()
    {
        var w = new GoldenWorld();
        w.Remote.Intercept = r =>
        {
            if (r.Method != "POST" || !r.Path.EndsWith("/fleet", StringComparison.Ordinal))
                return null;
            var answer = w.Remote.Respond(r);
            if (r.Body!.Contains("Fleet 9", StringComparison.Ordinal))
            {
                w.Remote.Rows["fleet/90"]["created_at"] = DateTimeOffset.UtcNow.ToString("O");
                return FakeResponse.WithStatus(201, """{"result":"created"}""");
            }
            return answer;
        };
        var settled = w.New(ResourceKind.Fleets, ("Name", "Fleet 9"), ("Type Code", "A320"), ("Type (pax/cargo/...)", "cargo"), ("Parameter IDs", "1"));
        // "Fleet 4" exists already: refused as a duplicate, never sent
        var failed = w.New(ResourceKind.Fleets, ("Name", "Fleet 4"), ("Type Code", "B738"), ("Type (pax/cargo/...)", "pax"), ("Max Passengers", "189"), ("Parameter IDs", "1"));
        w.New(ResourceKind.Routes, (Dep, "EGLL"), (Arr, "EGKK"), ("Type", "scheduled"), ("Callsign", "CAO900"), ("Flight Number", "CA900"),
            ("Fleet IDs", LocalReference.Format(settled.LocalId) + "," + LocalReference.Format(failed.LocalId)));

        await w.RunAsync();

        var items = w.Job!.Items;
        var route = items.Single(i => i.Resource == ResourceKind.Routes);
        Assert.Equal(ItemState.Succeeded, items.Single(i => i.After.Get("Name") == "Fleet 9").State);
        Assert.Equal(ItemState.Failed, items.Single(i => i.After.Get("Name") == "Fleet 4").State);
        Assert.Equal(ItemState.Skipped, route.State);
        Assert.Equal(1, Count(w, "POST"));
        Assert.DoesNotContain(w.Remote.Server.Requests, r => r.Method == "POST" && r.Path.EndsWith("/routes", StringComparison.Ordinal));
    }
}
