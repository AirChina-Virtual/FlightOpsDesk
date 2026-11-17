using VamSys.Core;

namespace VamSys.UnitTests.Application;

public class ConnectionServiceTests
{
    sealed class Fixture
    {
        public readonly FakeStorage Storage = new();
        public readonly InteractionGate Gate = new();
        public readonly FakeOnline Online = new();
        public readonly WorkspaceSession Session;
        public readonly ConnectionService Service;
        public Fixture(RunMode mode = RunMode.Offline)
        {
            Storage.Seed("va", mode);
            Session = new(Storage, Gate, new FakeTimer());
            Session.Open("default");
            Session.Workspace.Mode = mode;
            Service = new(Session, Online);
        }
        public FakeOnlineSession Api => Online.For(Session.Workspace);
        public Workspace Stored => Storage.Read(Session.Workspace.Id);
    }

    static string? Code(Exception e) => MessageErrors.Describe(e).Code;

    [Fact]
    public async Task Connecting_goes_online_records_the_contract_and_saves_only_the_header()
    {
        var f = new Fixture();
        f.Session.Workspace.ContractHash = "old";
        f.Session.Workspace.VerifiedOperations.Add("Fleets:Create");
        var parameters = await f.Service.ConnectAsync(CancellationToken.None);
        Assert.True(parameters);
        var w = f.Session.Workspace;
        Assert.Equal(RunMode.Online, w.Mode);
        Assert.NotNull(w.ConnectedAt);
        Assert.Equal("hash-1", w.ContractHash);
        Assert.Empty(w.VerifiedOperations);
        Assert.Equal("journal:Header", f.Storage.Calls.Last());
        Assert.Equal(["identify", "parameters:True"], f.Api.Calls);
        Assert.Equal(RunMode.Online, f.Stored.Mode);
    }

    [Fact]
    public async Task The_verified_operations_survive_a_reconnect_under_the_same_contract()
    {
        var f = new Fixture();
        f.Session.Workspace.ContractHash = "hash-1";
        f.Session.Workspace.VerifiedOperations.Add("Fleets:Create");
        await f.Service.ConnectAsync(CancellationToken.None);
        Assert.Equal(["Fleets:Create"], f.Session.Workspace.VerifiedOperations);
    }

    [Fact]
    public async Task A_server_that_does_not_identify_the_airline_leaves_the_workspace_offline()
    {
        var f = new Fixture();
        f.Api.Identifies = false;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.ConnectAsync(CancellationToken.None));
        Assert.Equal("ApiConnectFirst", Code(error));
        Assert.Equal(RunMode.Offline, f.Session.Workspace.Mode);
        Assert.DoesNotContain("journal:Header", f.Storage.Calls);
    }

    [Fact]
    public async Task Missing_credentials_stop_the_connection_before_any_request()
    {
        var f = new Fixture();
        f.Online.NoCredentials = true;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.ConnectAsync(CancellationToken.None));
        Assert.Equal("ApiConnectFirst", Code(error));
        Assert.Empty(f.Online.Sessions);
    }

    [Fact]
    public async Task A_refused_parameter_read_keeps_the_connection_but_an_access_error_stops_it()
    {
        var f = new Fixture();
        f.Api.ParametersError = new InvalidOperationException("no parameters");
        Assert.False(await f.Service.ConnectAsync(CancellationToken.None));
        Assert.Equal(RunMode.Online, f.Session.Workspace.Mode);
        Assert.Contains("invalidate", f.Api.Calls);

        var g = new Fixture();
        g.Api.ParametersError = new ApiAccessException("forbidden", 403);
        await Assert.ThrowsAsync<ApiAccessException>(() => g.Service.ConnectAsync(CancellationToken.None));
        Assert.DoesNotContain("invalidate", g.Api.Calls);
    }

    [Fact]
    public async Task A_failed_header_save_surfaces_and_the_database_still_holds_the_offline_workspace()
    {
        var f = new Fixture();
        f.Storage.FailSaves = true;
        await Assert.ThrowsAsync<IOException>(() => f.Service.ConnectAsync(CancellationToken.None));
        Assert.Equal(RunMode.Offline, f.Stored.Mode);
        Assert.Null(f.Stored.ConnectedAt);
        Assert.DoesNotContain("parameters:True", f.Api.Calls);
    }

    [Fact]
    public void A_refresh_needs_a_connected_workspace_and_no_open_online_task()
    {
        var offline = new Fixture();
        Assert.False(offline.Service.CanRefresh());

        var online = new Fixture(RunMode.Online);
        Assert.True(online.Service.CanRefresh());
        online.Session.Workspace.Jobs.Add(new BatchJob { Mode = RunMode.Online, Items = [new ChangeItem { State = ItemState.Unknown }] });
        Assert.Equal("ApiPendingJob", Code(Assert.Throws<InvalidOperationException>(() => online.Service.CanRefresh())));

        var finished = new Fixture(RunMode.Online);
        finished.Session.Workspace.Jobs.Add(new BatchJob { Mode = RunMode.Online, Items = [new ChangeItem { State = ItemState.Succeeded }] });
        Assert.True(finished.Service.CanRefresh());
    }

    // A task whose only item not yet final is a skipped one: a new fleet (in the given state) and an aircraft skipped
    // because of it. The window asks CanRefresh before both "从 API 刷新" and the single-resource refresh
    // (MainWindow.RefreshApi(only) calls it the same way for both) and shows the refusal's message.
    static BatchJob SkippedBehind(ItemState dependency, RunMode mode = RunMode.Online)
    {
        var fleet = new ChangeItem { State = dependency, Resource = ResourceKind.Fleets, Kind = ChangeKind.Create };
        var aircraft = new ChangeItem { State = ItemState.Skipped, Resource = ResourceKind.Aircraft, Kind = ChangeKind.Create, Dependencies = [fleet.Id] };
        return new BatchJob { Mode = mode, Items = [fleet, aircraft, new ChangeItem { State = ItemState.Succeeded }] };
    }

    [Theory]
    [InlineData(ItemState.Succeeded)]   // "Continue" would send the aircraft now
    [InlineData(ItemState.Unknown)]     // the fleet may still be settled, and the aircraft sent after it
    public void A_skipped_item_that_continue_can_still_send_refuses_a_refresh_with_the_pending_task_message(ItemState dependency)
    {
        var f = new Fixture(RunMode.Online);
        f.Session.Workspace.Jobs.Add(SkippedBehind(dependency));
        var refused = Assert.Throws<InvalidOperationException>(() => f.Service.CanRefresh());
        Assert.Equal("ApiPendingJob", Code(refused));
        var language = new LocalizationService();
        var shown = language.Format(MessageErrors.Describe(refused));
        Assert.Equal(language.Get("ApiPendingJob"), shown);
        Assert.DoesNotContain("[ApiPendingJob]", shown, StringComparison.Ordinal);
        Assert.NotEmpty(shown);
    }

    [Theory]
    [InlineData(ItemState.Failed)]
    [InlineData(ItemState.Conflict)]
    public void A_skipped_item_that_can_never_run_does_not_hold_back_a_refresh(ItemState dependency)
    {
        var f = new Fixture(RunMode.Online);
        f.Session.Workspace.Jobs.Add(SkippedBehind(dependency));
        Assert.True(f.Service.CanRefresh());
    }

    [Fact]
    public void A_demo_task_never_holds_back_a_refresh()
    {
        var f = new Fixture(RunMode.Online);
        f.Session.Workspace.Jobs.Add(SkippedBehind(ItemState.Unknown, RunMode.Demo));
        f.Session.Workspace.Jobs.Add(new BatchJob { Mode = RunMode.Demo, Items = [new ChangeItem { State = ItemState.Pending }] });
        Assert.True(f.Service.CanRefresh());
    }

    [Fact]
    public async Task A_refresh_saves_each_staged_snapshot_and_reports_progress_then_reads_the_parameters()
    {
        var f = new Fixture(RunMode.Online);
        f.Api.Stage = _ => Task.CompletedTask;
        var progress = new List<(ResourceKind, int)>();
        var staged = new Workspace { Id = f.Session.Workspace.Id, Name = "staged" };
        f.Api.Stage = w => { staged = w; return Task.CompletedTask; };
        var parameters = await f.Service.RefreshAsync((k, n) => progress.Add((k, n)), CancellationToken.None);
        Assert.True(parameters);
        Assert.Equal([(ResourceKind.Airports, 3)], progress);
        Assert.Equal("saveAll", f.Storage.Calls.Last(c => c != "parameters:True"));
        Assert.Equal(["refresh", "parameters:True"], f.Api.Calls);
    }

    // E5: the airline parameters belong to fleets and routes; a refresh of only another resource leaves them alone.
    [Theory]
    [InlineData(ResourceKind.Routes, true)]
    [InlineData(ResourceKind.Fleets, true)]
    [InlineData(ResourceKind.Airports, false)]
    [InlineData(ResourceKind.Aircraft, false)]
    [InlineData(ResourceKind.Routings, false)]
    public async Task A_refresh_of_one_resource_reads_the_parameters_only_for_fleets_and_routes(ResourceKind kind, bool parameters)
    {
        var f = new Fixture(RunMode.Online);
        Assert.True(await f.Service.RefreshAsync((_, _) => { }, CancellationToken.None, kind));
        Assert.Equal(parameters ? ["refresh:" + kind, "parameters:True"] : ["refresh:" + kind], f.Api.Calls);
    }

    [Fact]
    public async Task A_failed_refresh_leaves_the_workspace_as_it_was()
    {
        var f = new Fixture(RunMode.Online);
        var before = f.Session.Workspace.Serialize();
        f.Api.RefreshError = new HttpRequestException("down");
        await Assert.ThrowsAsync<HttpRequestException>(() => f.Service.RefreshAsync((_, _) => { }, CancellationToken.None));
        Assert.Equal(before, f.Session.Workspace.Serialize());
        Assert.DoesNotContain("saveAll", f.Storage.Calls);
    }
}
