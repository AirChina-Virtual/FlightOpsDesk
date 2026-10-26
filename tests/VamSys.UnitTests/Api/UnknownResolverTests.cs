using System.Text.Json;
using VamSys.Core;
using VamSys.Infrastructure;
using VamSys.TestSupport;

namespace VamSys.UnitTests.Api;

/// <summary>Read-only answers about writes whose outcome is not known, and the aircraft move checks.</summary>
public class UnknownResolverTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static ApiKit Kit()
    {
        var kit = new ApiKit();
        kit.LoadStandardAirportsAndFleets();
        return kit;
    }

    static string List(params string[] records) => "{\"data\":[" + string.Join(',', records) + "],\"meta\":{\"next_cursor_url\":null}}";

    // ---- finishing a two-step create ----------------------------------------------------------------------------

    static ChangeItem TwoStepCreate(BatchStepState first = BatchStepState.ResponseReceived, BatchStepState second = BatchStepState.Prepared)
    {
        var item = ApiKit.Create(ResourceKind.Fleets, ("Name", "New fleet"));
        item.CreateCompleted = true;
        item.RemoteId = "90";
        item.ApiSteps =
        [
            new BatchStep { Method = "POST", Path = "fleet", State = first, Body = new() { ["name"] = JsonSerializer.SerializeToElement("New fleet") } },
            new BatchStep { Method = "PUT", Path = "{created}", State = second, Body = new() { ["max_pax"] = JsonSerializer.SerializeToElement(180) } }
        ];
        return item;
    }

    [Fact]
    public void Only_a_create_whose_post_was_answered_and_whose_later_steps_never_left_may_finish()
    {
        Assert.True(OperationsAdapter.CanResumeCreate(TwoStepCreate()));
        Assert.False(OperationsAdapter.CanResumeCreate(TwoStepCreate(first: BatchStepState.InFlight)));
        Assert.False(OperationsAdapter.CanResumeCreate(TwoStepCreate(second: BatchStepState.InFlight)));
        Assert.False(OperationsAdapter.CanResumeCreate(TwoStepCreate(second: BatchStepState.ResponseReceived)));
        var accepted = TwoStepCreate();
        accepted.WriteAccepted = true;
        Assert.False(OperationsAdapter.CanResumeCreate(accepted));
        var noId = TwoStepCreate();
        noId.RemoteId = null;
        Assert.False(OperationsAdapter.CanResumeCreate(noId));
        var oneStep = TwoStepCreate();
        oneStep.ApiSteps.RemoveAt(1);
        Assert.False(OperationsAdapter.CanResumeCreate(oneStep));
        var update = TwoStepCreate();
        update.Kind = ChangeKind.Update;
        Assert.False(OperationsAdapter.CanResumeCreate(update));
    }

    [Fact]
    public void The_created_record_must_still_hold_what_the_answered_post_sent_and_nothing_more_is_asked_of_it()
    {
        var kit = Kit();
        var item = TwoStepCreate();
        var fine = kit.Map(ResourceKind.Fleets, """{"id":90,"airline_id":7,"name":"New fleet"}""");
        Assert.True(kit.Api.MatchesCreated(item, fine));
        var renamed = kit.Map(ResourceKind.Fleets, """{"id":90,"airline_id":7,"name":"Somebody renamed it"}""");
        Assert.False(kit.Api.MatchesCreated(item, renamed));
    }

    // ---- proving that an unknown write did not happen -----------------------------------------------------------

    [Fact]
    public async Task A_create_that_finds_no_duplicate_was_not_applied()
    {
        var kit = Kit();
        kit.Server.On(HttpMethod.Get, "fleet", FakeResponse.Ok(List(ApiKit.Fleet5)));
        var item = ApiKit.Create(ResourceKind.Fleets, ("Name", "Brand new fleet"));
        Assert.True(await kit.Api.ProveNotAppliedAsync(item, Ct));
    }

    [Fact]
    public async Task A_create_that_finds_its_duplicate_may_have_been_applied()
    {
        var kit = Kit();
        kit.Server.On(HttpMethod.Get, "fleet", FakeResponse.Ok(List(ApiKit.Fleet4)));
        Assert.False(await kit.Api.ProveNotAppliedAsync(ApiKit.Create(ResourceKind.Fleets, ("Name", "fleet 4")), Ct));
    }

    [Fact]
    public async Task An_update_that_still_reads_as_before_was_not_applied_and_the_evidence_is_kept()
    {
        var kit = Kit();
        kit.Server.On(HttpMethod.Get, "airports/101", ApiKit.Data(ApiKit.Egll));
        var item = ApiKit.Update(ResourceKind.Airports, kit.Map(ResourceKind.Airports, ApiKit.Egll), ("Name", "Heathrow"));
        Assert.True(await kit.Api.ProveNotAppliedAsync(item, Ct));
        Assert.Equal(200, item.ReadbackDetails!.Status);
    }

    [Fact]
    public async Task An_update_whose_record_changed_or_vanished_cannot_be_called_not_applied()
    {
        var kit = Kit();
        var item = ApiKit.Update(ResourceKind.Airports, kit.Map(ResourceKind.Airports, ApiKit.Egll), ("Name", "Heathrow"));
        kit.Server.On(HttpMethod.Get, "airports/101", ApiKit.Data("""{"id":101,"airline_id":7,"icao":"EGLL","iata":"LHR","name":"Heathrow","container_ids":[1]}"""));
        Assert.False(await kit.Api.ProveNotAppliedAsync(item, Ct));
        kit.Server.On(HttpMethod.Get, "airports/101", FakeResponse.WithStatus(404));
        Assert.False(await kit.Api.ProveNotAppliedAsync(item, Ct));
    }

    [Fact]
    public async Task Without_a_before_row_or_an_id_nothing_can_be_proved()
    {
        var kit = Kit();
        var noBefore = ApiKit.Update(ResourceKind.Airports, kit.Map(ResourceKind.Airports, ApiKit.Egll), ("Name", "x"));
        noBefore.Before = null;
        Assert.False(await kit.Api.ProveNotAppliedAsync(noBefore, Ct));
        Assert.Empty(kit.Server.Requests);
    }

    [Fact]
    public void An_unknown_write_resolver_only_accepts_items_that_cannot_have_been_applied()
    {
        var kit = Kit();
        var item = ApiKit.Update(ResourceKind.Airports, kit.Map(ResourceKind.Airports, ApiKit.Egll), ("Name", "x"));
        item.State = ItemState.Unknown;
        Assert.True(OperationsUnknownResolver.CanCheck(item));
        item.WriteAccepted = true;
        Assert.False(OperationsUnknownResolver.CanCheck(item));
        item.WriteAccepted = false;
        item.CreateCompleted = true;
        Assert.False(OperationsUnknownResolver.CanCheck(item));
        var softDelete = ApiKit.Delete(ResourceKind.Airports, kit.Map(ResourceKind.Airports, ApiKit.Egll));
        softDelete.State = ItemState.Unknown;
        Assert.False(OperationsUnknownResolver.CanCheck(softDelete));
    }

    // ---- aircraft move ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Only_a_move_looks_at_its_target_fleet_before_it_is_sent()
    {
        var kit = Kit();
        var aircraft = kit.Map(ResourceKind.Aircraft, ApiKit.Aircraft8);
        await kit.Api.PreflightMoveAsync(ApiKit.Update(ResourceKind.Aircraft, aircraft, ("Name", "x")), Ct);
        Assert.Empty(kit.Server.Requests);
        kit.Server.On(HttpMethod.Get, "fleet/5", ApiKit.Data(ApiKit.Fleet5));
        await kit.Api.PreflightMoveAsync(ApiKit.Update(ResourceKind.Aircraft, aircraft, ("Fleet ID", "5")), Ct);
        Assert.Equal("fleet/5", Assert.Single(kit.Server.Requests).Path[OperationsAdapter.BaseUri.AbsolutePath.Length..]);
    }

    [Fact]
    public async Task A_move_to_a_fleet_that_does_not_exist_is_refused_before_the_patch()
    {
        var kit = Kit();
        var aircraft = kit.Map(ResourceKind.Aircraft, ApiKit.Aircraft8);
        kit.Server.On(HttpMethod.Get, "fleet/5", FakeResponse.WithStatus(404));
        var error = await Assert.ThrowsAnyAsync<Exception>(() => kit.Api.PreflightMoveAsync(ApiKit.Update(ResourceKind.Aircraft, aircraft, ("Fleet ID", "5")), Ct));
        Assert.Equal("ApiMoveTarget", MessageErrors.Describe(error).Code);
    }

    [Fact]
    public async Task A_move_is_read_back_under_the_target_fleet_and_anything_else_under_its_own_path()
    {
        var kit = Kit();
        var aircraft = kit.Map(ResourceKind.Aircraft, ApiKit.Aircraft8);
        kit.Server.On(HttpMethod.Get, "fleet/5/aircraft/8", ApiKit.Data("""{"id":8,"airline_id":7,"fleet_id":5,"registration":"B-TEST","name":"Plane"}"""));
        kit.Server.On(HttpMethod.Get, "fleet/4/aircraft/8", ApiKit.Data(ApiKit.Aircraft8));
        var moved = await kit.Api.FindResult(ApiKit.Update(ResourceKind.Aircraft, aircraft, ("Fleet ID", "5")), "8", Ct);
        Assert.Equal("5", moved!.Identity!.ParentFleetId);
        var renamed = await kit.Api.FindResult(ApiKit.Update(ResourceKind.Aircraft, aircraft, ("Name", "x")), "8", Ct);
        Assert.Equal("4", renamed!.Identity!.ParentFleetId);
        Assert.Equal(["fleet/5/aircraft/8", "fleet/4/aircraft/8"], kit.Server.Requests.Select(r => r.Path[OperationsAdapter.BaseUri.AbsolutePath.Length..]).ToArray());
    }
}
