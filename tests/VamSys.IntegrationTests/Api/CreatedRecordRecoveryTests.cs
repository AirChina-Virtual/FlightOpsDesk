using VamSys.Core;
using VamSys.IntegrationTests.Golden.Requests;
using VamSys.TestSupport;
using static VamSys.IntegrationTests.Api.PipelineHarness;

namespace VamSys.IntegrationTests.Api;

/// <summary>
/// E1 + G5: a create whose answer was lost is searched for, read-only. Exactly one record that is new since the request,
/// matches the change and was created within the request window (on the server's clock) is bound by the program; none or
/// several are only listed for the user. Nothing is ever posted again.
/// </summary>
public class CreatedRecordRecoveryTests
{
    static void NewFleet(GoldenWorld w)
        => w.New(ResourceKind.Fleets, ("Name", "Fleet 9"), ("Type Code", "A320"), ("Type (pax/cargo/...)", "cargo"), ("Parameter IDs", "1"));

    const string Twin = """{"id":91,"airline_id":7,"name":"Fleet 9","code":"A320","type":"cargo","parameter_ids":[1]}""";

    // The server creates the fleet (fleet/90) with created_at on its own clock, and the answer is lost.
    static void LoseTheAnswer(GoldenWorld w, TimeSpan serverAhead, TimeSpan createdAfter, bool twin = false)
    {
        var posted = false;
        w.Remote.Intercept = r =>
        {
            if (r.Method == "POST" && !posted)
            {
                posted = true;
                w.Remote.Respond(r);
                w.Remote.Rows["fleet/90"]["created_at"] = (DateTimeOffset.UtcNow + serverAhead + createdAfter).ToString("O");
                if (twin)
                    w.Remote.AddRow("fleet/91", Twin)["created_at"] = (DateTimeOffset.UtcNow + serverAhead).ToString("O");
                throw new TimeoutException("simulated: created, answer lost");
            }
            if (serverAhead == TimeSpan.Zero)
                return null;
            var answer = w.Remote.Respond(r);
            return answer with { Headers = new Dictionary<string, string> { ["Date"] = (DateTimeOffset.UtcNow + serverAhead).ToString("R") } };
        };
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]   // the server's clock is five minutes ahead of this machine's (G5)
    [InlineData(-5)]
    public async Task Exactly_one_new_matching_record_created_during_the_request_is_bound_without_asking(int serverMinutesAhead)
    {
        var w = new GoldenWorld();
        NewFleet(w);
        LoseTheAnswer(w, TimeSpan.FromMinutes(serverMinutesAhead), TimeSpan.Zero);

        await w.RunAsync();

        var item = w.Job!.Items.Single();
        Assert.Equal(ItemState.Succeeded, item.State);
        Assert.Equal("90", item.RemoteId);
        var attempt = Assert.Single(item.RecoveryCandidates);
        Assert.Equal(("90", "Automatic"), (attempt.RemoteId, attempt.Source));
        Assert.Equal(["90"], item.FoundCreateCandidates);
        Assert.Equal(1, Count(w, "POST"));
        Assert.All(w.Remote.Server.Requests.SkipWhile(r => r.Method != "POST").Skip(1), r => Assert.Equal("GET", r.Method));
    }

    // A one-step create of every other kind (the POST sends all its values) is bound and verified as before. The fleet is the
    // test above; an aircraft with internal remarks is two steps (AircraftLostPostTests).
    [Theory]
    [InlineData("aircraft")]
    [InlineData("aircraft-remarks-dropped")]
    [InlineData("airport")]
    [InlineData("route")]
    public async Task A_one_step_create_of_any_kind_whose_answer_was_lost_is_bound_and_verified_as_before(string kind)
    {
        var w = new GoldenWorld();
        w.Remote.AircraftCreateDropsInternalRemarks = kind == "aircraft-remarks-dropped";
        if (kind.StartsWith("aircraft", StringComparison.Ordinal))
            w.New(ResourceKind.Aircraft, ("Name", "Plane Two"), ("Registration", "QA-2"), ("Fleet ID", "4"));
        else if (kind == "airport")
            w.New(ResourceKind.Airports, ("ICAO/IATA", "ZZZZ"), ("Name", "New field"));
        else
            w.New(ResourceKind.Routes, ("Departure Airport (ICAO/IATA)", "EGLL"), ("Arrival Airport (ICAO/IATA)", "EGKK"), ("Type", "scheduled"),
                ("Callsign", "CAO900"), ("Flight Number", "CA900"), ("Fleet IDs", "4"));
        var posted = false;
        w.Remote.Intercept = r =>
        {
            if (r.Method != "POST" || posted)
                return null;
            posted = true;
            w.Remote.Respond(r);
            w.Remote.Rows[w.Remote.Rows.Keys.Single(k => k.EndsWith("/90", StringComparison.Ordinal))]["created_at"] = DateTimeOffset.UtcNow.ToString("O");
            throw new TimeoutException("simulated: created, answer lost");
        };

        await w.RunAsync();

        var item = w.Job!.Items.Single();
        Assert.Single(item.ApiSteps);
        Assert.Equal(ItemState.Succeeded, item.State);
        Assert.Equal("90", item.RemoteId);
        Assert.True(item.CreateCompleted);
        Assert.False(item.CreateFoundWithoutAnswer);
        Assert.Equal(("90", "Automatic"), (item.RecoveryCandidates.Single().RemoteId, item.RecoveryCandidates.Single().Source));
        Assert.Equal(["POST"], w.Remote.Server.Requests.Where(r => r.Method != "GET").Select(r => r.Method));
    }

    [Fact]
    public async Task Two_records_that_could_be_the_create_are_listed_for_the_user_and_neither_is_bound()
    {
        var w = new GoldenWorld();
        NewFleet(w);
        LoseTheAnswer(w, TimeSpan.Zero, TimeSpan.Zero, twin: true);

        await w.RunAsync();

        var item = w.Job!.Items.Single();
        Assert.Equal(ItemState.Unknown, item.State);
        Assert.Equal("ApiCreateCandidates", Code(item));
        Assert.Equal(["90", "91"], item.FoundCreateCandidates!.Order(StringComparer.Ordinal));
        Assert.Null(item.RecoveryCandidateId);
        Assert.Null(item.RemoteId);
        Assert.Empty(item.RecoveryCandidates);
        Assert.True(item.CanProposeRecoveryId);
        Assert.Equal(1, Count(w, "POST"));
    }

    [Theory]
    [InlineData(-10)]
    [InlineData(10)]
    public async Task A_matching_record_created_outside_the_request_window_is_not_bound(int minutes)
    {
        var w = new GoldenWorld();
        NewFleet(w);
        LoseTheAnswer(w, TimeSpan.Zero, TimeSpan.FromMinutes(minutes));

        await w.RunAsync();

        var item = w.Job!.Items.Single();
        Assert.Equal(ItemState.Unknown, item.State);
        Assert.Empty(item.FoundCreateCandidates!);
        Assert.Null(item.RecoveryCandidateId);
        Assert.Equal("ApiUnknown", Code(item));
        Assert.Equal(1, Count(w, "POST"));
    }

    [Fact]
    public async Task A_record_that_differs_from_the_change_is_not_bound()
    {
        var w = new GoldenWorld();
        NewFleet(w);
        LoseTheAnswer(w, TimeSpan.Zero, TimeSpan.Zero);
        var intercept = w.Remote.Intercept!;
        w.Remote.Intercept = r =>
        {
            var answer = intercept(r);
            if (w.Remote.Rows.TryGetValue("fleet/90", out var created))
                created["code"] = "B738"; // same name, other aircraft type
            return answer;
        };

        await w.RunAsync();

        var item = w.Job!.Items.Single();
        Assert.Equal(ItemState.Unknown, item.State);
        Assert.Empty(item.FoundCreateCandidates!);
    }

    [Fact]
    public async Task A_create_saved_without_the_ids_that_existed_before_it_is_not_searched()
    {
        var w = new GoldenWorld();
        NewFleet(w);
        LoseTheAnswer(w, TimeSpan.Zero, TimeSpan.Zero);
        await w.RunAsync("first run");
        var item = w.Job!.Items.Single();
        Assert.Equal(ItemState.Succeeded, item.State);

        // as saved by an older version: no list of the ids before the request
        var old = new GoldenWorld();
        NewFleet(old);
        old.Remote.Intercept = r => r.Method == "POST" ? throw new TimeoutException("simulated") : null;
        await old.RunAsync("first run");
        var unknown = old.Job!.Items.Single();
        unknown.PreCreateCandidateIds = null;
        unknown.FoundCreateCandidates = null;
        var before = old.Remote.Server.Requests.Count;
        await old.ResumeAsync("recovery");
        Assert.Equal(ItemState.Unknown, unknown.State);
        Assert.Null(unknown.FoundCreateCandidates);
        Assert.Equal(before, old.Remote.Server.Requests.Count);
    }
}
