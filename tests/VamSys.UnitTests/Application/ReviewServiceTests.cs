using VamSys.Core;

namespace VamSys.UnitTests.Application;

public class ReviewServiceTests
{
    sealed class Fixture
    {
        public readonly FakeStorage Storage = new();
        public readonly FakeOnline Online = new();
        public readonly WorkspaceSession Session;
        public readonly ReviewService Service;
        public int Locked;
        public readonly IWorkerLock Locker;
        public Fixture(RunMode mode = RunMode.Demo)
        {
            Storage.Seed("va", mode);
            Session = new(Storage, new InteractionGate(), new FakeTimer());
            Session.Open("default");
            Session.Workspace.Mode = mode;
            Service = new(Session, Online);
            Locker = new CountingLock(this);
        }
        sealed class CountingLock(Fixture owner) : IWorkerLock
        {
            public Task<T> RunAsync<T>(Func<T> work) { owner.Locked++; return Task.Run(work); }
        }
        public ResourceData Airports => Session.Workspace.Resources[ResourceKind.Airports];
        public void AddAirport(string code) => Airports.Draft.Add(new DataRow { Fields = new Dictionary<string, string> { ["ICAO/IATA"] = code, ["Name"] = code, ["_delete"] = "FALSE" } });
    }

    [Fact]
    public void A_demo_workspace_reviews_its_changes_as_ready_without_a_connection()
    {
        var f = new Fixture();
        f.AddAirport("EGLL");
        var plan = f.Service.Build();
        Assert.Equal(1, plan.SubmitCount);
        Assert.All(plan.Entries, e => Assert.Equal(ReviewDisposition.Ready, e.Disposition));
    }

    [Fact]
    public void An_offline_workspace_awaits_the_connection_and_never_asks_the_server()
    {
        var f = new Fixture(RunMode.Offline);
        f.AddAirport("EGLL");
        var plan = f.Service.Build();
        Assert.Equal(0, plan.SubmitCount);
        Assert.All(plan.Entries, e => Assert.Equal(ReviewDisposition.AwaitingConnection, e.Disposition));
        Assert.Empty(f.Online.Sessions);
    }

    [Fact]
    public void A_connected_workspace_previews_every_change_with_the_server_session()
    {
        var f = new Fixture(RunMode.Online);
        f.AddAirport("EGLL");
        var api = f.Online.For(f.Session.Workspace);
        api.AirlineId = "7";
        var plan = f.Service.Build();
        Assert.Equal(["preview"], api.Calls);
        Assert.Equal(1, plan.SubmitCount);

        api.PreviewError = new InvalidOperationException("unsupported");
        var blocked = f.Service.Build();
        Assert.Equal(0, blocked.SubmitCount);
        Assert.Equal(ReviewDisposition.Blocked, blocked.Entries.Single().Disposition);
    }

    [Fact]
    public void A_session_that_has_not_identified_the_airline_is_not_used_for_the_preview()
    {
        var f = new Fixture(RunMode.Online);
        f.AddAirport("EGLL");
        var api = f.Online.For(f.Session.Workspace);
        f.Service.Build();
        Assert.Empty(api.Calls);
    }

    [Fact]
    public async Task The_shown_plan_may_be_submitted_while_the_workspace_still_yields_the_same_review()
    {
        var f = new Fixture();
        f.AddAirport("EGLL");
        f.Service.Show(f.Service.Build(), f.Session.Workspace.Id, f.Session.Workspace.AirlineId, RunMode.Demo);
        var check = await f.Service.CheckCurrentAsync(RunMode.Demo, f.Locker);
        Assert.False(check.Changed);
        Assert.Same(f.Service.Shown!.Plan, check.Plan);
        Assert.Equal(1, f.Locked);
    }

    [Fact]
    public async Task Any_change_after_the_page_was_shown_makes_it_stale()
    {
        var f = new Fixture();
        f.AddAirport("EGLL");
        f.Service.Show(f.Service.Build(), f.Session.Workspace.Id, f.Session.Workspace.AirlineId, RunMode.Demo);
        f.Airports.ReplaceDraftRows([f.Airports.Draft[0].With("Name", "changed")]);
        var check = await f.Service.CheckCurrentAsync(RunMode.Demo, f.Locker);
        Assert.True(check.Changed);
        Assert.Null(check.Plan);
    }

    [Fact]
    public async Task A_plan_shown_for_another_mode_airline_or_workspace_is_stale()
    {
        var f = new Fixture();
        f.AddAirport("EGLL");
        var plan = f.Service.Build();
        f.Service.Show(plan, f.Session.Workspace.Id, f.Session.Workspace.AirlineId, RunMode.Demo);
        Assert.True((await f.Service.CheckCurrentAsync(RunMode.Online, f.Locker)).Changed);
        f.Service.Show(plan, f.Session.Workspace.Id, "other-airline", RunMode.Demo);
        Assert.True((await f.Service.CheckCurrentAsync(RunMode.Demo, f.Locker)).Changed);
        f.Service.Show(plan, Guid.NewGuid(), f.Session.Workspace.AirlineId, RunMode.Demo);
        Assert.True((await f.Service.CheckCurrentAsync(RunMode.Demo, f.Locker)).Changed);
    }

    [Fact]
    public async Task Nothing_shown_yet_is_stale()
    {
        var f = new Fixture();
        f.AddAirport("EGLL");
        Assert.True((await f.Service.CheckCurrentAsync(RunMode.Demo, f.Locker)).Changed);
    }

    [Fact]
    public async Task A_current_plan_without_anything_to_submit_is_neither_stale_nor_submittable()
    {
        var f = new Fixture();
        f.Service.Show(f.Service.Build(), f.Session.Workspace.Id, f.Session.Workspace.AirlineId, RunMode.Demo);
        var check = await f.Service.CheckCurrentAsync(RunMode.Demo, f.Locker);
        Assert.False(check.Changed);
        Assert.Null(check.Plan);
    }

    [Fact]
    public async Task A_workspace_conflict_blocks_a_current_plan()
    {
        var f = new Fixture();
        f.AddAirport("EGLL");
        f.Airports.Conflicts.Add(new(f.Airports.Draft[0].LocalId, "Name", "a", "b", "c"));
        f.Service.Show(f.Service.Build(), f.Session.Workspace.Id, f.Session.Workspace.AirlineId, RunMode.Demo);
        var check = await f.Service.CheckCurrentAsync(RunMode.Demo, f.Locker);
        Assert.False(check.Changed);
        Assert.Null(check.Plan);
        Assert.Equal(0, f.Service.Shown!.Plan.SubmitCount);
    }

    [Fact]
    public void A_load_that_was_overtaken_is_dropped_and_leaving_the_page_forgets_the_plan()
    {
        var f = new Fixture();
        var first = f.Service.BeginLoad();
        var second = f.Service.BeginLoad();
        Assert.False(f.Service.IsCurrent(first));
        Assert.True(f.Service.IsCurrent(second));
        f.Service.Show(f.Service.Build(), f.Session.Workspace.Id, null, RunMode.Demo);
        f.Service.Invalidate(forget: false);
        Assert.False(f.Service.IsCurrent(second));
        Assert.NotNull(f.Service.Shown);
        f.Service.Invalidate();
        Assert.Null(f.Service.Shown);
    }

    [Fact]
    public void Reviewing_never_saves_or_changes_the_workspace()
    {
        var f = new Fixture();
        f.AddAirport("EGLL");
        var before = f.Session.Workspace.Serialize();
        f.Service.Build();
        Assert.Equal(before, f.Session.Workspace.Serialize());
        Assert.DoesNotContain(f.Storage.Calls, c => c.StartsWith("journal", StringComparison.Ordinal) || c == "rows");
    }
}
