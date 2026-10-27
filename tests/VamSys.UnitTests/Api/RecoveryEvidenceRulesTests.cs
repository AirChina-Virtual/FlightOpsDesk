using System.Text.Json;
using VamSys.Core;
using VamSys.Infrastructure;
using VamSys.TestSupport;

namespace VamSys.UnitTests.Api;

/// <summary>Small rules of the recovery evidence that need no server: what a move may be cleared to, and what the saved JSON looks like.</summary>
public class RecoveryEvidenceRulesTests
{
    [Fact]
    public void A_move_cleared_to_no_fleet_is_invalid_and_never_planned()
    {
        var kit = new ApiKit();
        kit.LoadStandardAirportsAndFleets();
        var aircraft = kit.Map(ResourceKind.Aircraft, ApiKit.Aircraft8);
        var move = ApiKit.Update(ResourceKind.Aircraft, aircraft, ("Fleet ID", "5"));
        move.After = move.After.With("Fleet ID", "");
        move.Fields["Fleet ID"] = new(FieldIntent.Clear, "");

        Assert.Equal("ApiInvalid", ApiKit.BlockedWith(() => kit.Api.Plan(move)));
        Assert.Empty(kit.Server.Requests);
    }

    [Fact]
    public void A_resumed_create_is_compared_on_its_post_values_alone_and_keeps_the_expectation_saved_with_it()
    {
        var kit = new ApiKit();
        kit.LoadStandardAirportsAndFleets();
        var created = ApiKit.Create(ResourceKind.Fleets, ("Name", "Fleet 4"));
        created.ApiSteps =
        [
            new BatchStep { Method = "POST", Path = "fleet", Body = new() { ["name"] = JsonSerializer.SerializeToElement("Fleet 4") } },
            new BatchStep { Method = "PUT", Path = "{created}", Body = new() { ["max_cargo"] = JsonSerializer.SerializeToElement(9) } }
        ];
        var expected = new Dictionary<string, JsonElement> { ["name"] = JsonSerializer.SerializeToElement("Fleet 4"), ["max_cargo"] = JsonSerializer.SerializeToElement(9) };
        created.ApiExpectedValues = expected;
        var fleet = kit.Workspace.Resources[ResourceKind.Fleets].Snapshot[0];

        // the record has the POST values but not the second step's: that is what a resumed create may be missing
        Assert.True(kit.Api.MatchesCreated(created, fleet));
        Assert.False(kit.Api.Matches(created, fleet, created.After, false));
        Assert.Same(expected, created.ApiExpectedValues);
    }

    [Fact]
    public void New_deletion_evidence_is_optional_json_and_old_tasks_keep_their_steps_and_state()
    {
        var item = new ChangeItem { State = ItemState.ManuallyConfirmed, ApiSteps = [new() { Method = "DELETE", Path = "routes/201" }] };
        var raw = JsonSerializer.Serialize(item);
        Assert.DoesNotContain("DeletionVerification", raw, StringComparison.Ordinal);

        var old = JsonSerializer.Deserialize<ChangeItem>(raw)!;
        Assert.Null(old.DeletionVerification);
        Assert.Equal("routes/201", old.ApiSteps.Single().Path);
        Assert.Equal(ItemState.ManuallyConfirmed, old.State);
        Assert.Equal(4, WorkspaceStore.FormatVersion);
    }

    [Fact]
    public async Task Connecting_with_credentials_of_another_airline_than_the_workspace_is_refused_by_reading_only()
    {
        var kit = new ApiKit(airline: "8");
        var served = 0;
        kit.Server.On(HttpMethod.Get, "fleet", _ => { served++; return FakeResponse.Ok("{\"data\":[" + ApiKit.Fleet4 + "],\"meta\":{\"next_cursor_url\":null}}"); });

        var error = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => kit.Api.IdentifyAirlineAsync(TestContext.Current.CancellationToken));

        Assert.Equal("ApiWrongVa", MessageErrors.Describe(error).Code);
        Assert.True(served > 0, "the fleet list was never read");
        Assert.All(kit.Server.Requests, r => Assert.Equal("GET", r.Method));
    }
}