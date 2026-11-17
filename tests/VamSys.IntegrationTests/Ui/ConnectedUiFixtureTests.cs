using VamSys.Core;
using VamSys.Infrastructure;
using VamSys.TestSupport;
using Scenario = VamSys.TestSupport.ConnectedUiFixture.Scenario;

namespace VamSys.IntegrationTests.Ui;

/// <summary>4.1: every connected window-test scenario seeds a task in the state its dialog needs, and stores credentials.</summary>
public class ConnectedUiFixtureTests
{
    public static TheoryData<Scenario> Scenarios() => [.. Enum.GetValues<Scenario>()];

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void Each_scenario_leaves_its_task_and_the_credentials_on_disk(Scenario scenario)
    {
        using var dir = TempDataDirectory.Create("vamsys-connected-ui-");
        var target = Path.Combine(dir.Path, "data");
        var remote = ConnectedUiFixture.Seed(target, scenario);

        var store = new WorkspaceStore(target);
        var workspace = store.Load(store.List().Single(w => w.Name == "QA connected").Id);
        Assert.Equal("7", workspace.AirlineId);
        Assert.Equal(ConnectedUiFixture.ClientId, workspace.ClientId);
        Assert.NotNull(store.LoadSecret(workspace.Id));
        if (scenario == Scenario.DependentsOfUnreadableCreate)
        {
            Assert.Empty(workspace.Jobs);
            var fleet = workspace.Resources[ResourceKind.Fleets].Draft.Single(r => r.Get("Name") == "Fleet 9");
            var reference = LocalReference.Format(fleet.LocalId);
            Assert.Equal(reference, workspace.Resources[ResourceKind.Aircraft].Draft.Single(r => r.Get("Registration") == "QA-NINE").Get("Fleet ID"));
            Assert.Equal(reference, workspace.Resources[ResourceKind.Routes].Draft.Single(r => r.Get("Callsign") == ConnectedUiFixture.DependentRouteCallsign).Get("Fleet IDs"));
            Assert.Empty(remote.Server.Requests);
            return;
        }
        if (scenario == Scenario.UnapprovedFleet)
        {
            Assert.Empty(workspace.Jobs);
            Assert.Equal("4,6", workspace.Resources[ResourceKind.Routes].Draft.Single(r => r.Identity?.RemoteId == "10").Get("Fleet IDs"));
            return;
        }
        if (scenario == Scenario.TwoDeletesWithoutReceiptGone)
        {
            Assert.All(workspace.Jobs.Single().Items, i => Assert.Equal((ItemState.Unknown, false), (i.State, i.WriteAccepted)));
            Assert.DoesNotContain("airports/103", remote.Rows.Keys);
            Assert.DoesNotContain("airports/104", remote.Rows.Keys);
            return;
        }
        var item = workspace.Jobs.Single().Items.Single();
        Assert.Equal(scenario == Scenario.RouteRefusedForFleets ? ItemState.Failed : ItemState.Unknown, item.State);
        Assert.Equal(scenario switch
        {
            Scenario.DeleteAnsweredButReadable => true,
            Scenario.ServerKeepsOtherValue or Scenario.ServerValueNowMatches => true,
            _ => false
        }, item.WriteAccepted);
        if (scenario == Scenario.CreateWithTwoCandidates)
            Assert.Equal(["90", "91"], item.FoundCreateCandidates!.Order(StringComparer.Ordinal));
        if (scenario == Scenario.RouteRefusedForFleets)
            Assert.Equal(["5", "6"], PrefixRejection.DeniedFleetIds(item));
        Assert.Equal(scenario == Scenario.NoReceiptGone, !remote.Rows.ContainsKey("airports/103"));
    }

    // The window's review of the seeded drafts, built as ReviewService builds it once connected: the stored workspace, an
    // adapter connected to the same mock (airline identified, parameters read) and its PreviewPlan for each change.
    [Fact]
    public async Task The_seeded_drafts_of_the_same_round_scenario_are_all_ready_in_the_online_review()
    {
        using var dir = TempDataDirectory.Create("vamsys-connected-ui-");
        var target = Path.Combine(dir.Path, "data");
        var remote = ConnectedUiFixture.Seed(target, Scenario.DependentsOfUnreadableCreate);
        var store = new WorkspaceStore(target);
        var workspace = store.Load(store.List().Single(w => w.Name == "QA connected").Id);
        workspace.Mode = RunMode.Online;
        var requests = InstantTime.Requests();
        var tokens = new TokenProvider(_ => Task.FromResult(new AccessToken("t", requests.Clock.GetUtcNow().AddHours(1))), requests.Clock);
        using var client = new HttpClient(remote.Server, disposeHandler: false);
        var api = new OperationsAdapter(new OperationsTransport(client, OperationsAdapter.BaseUri, "ui-review", tokens, requests), workspace);
        Assert.True(await api.IdentifyAirlineAsync(TestContext.Current.CancellationToken));
        await api.ReadParametersAsync(TestContext.Current.CancellationToken, true);

        var plan = ReviewPlanBuilder.BuildForWorkspace(new ChangePlanner(), workspace, RunMode.Online, true,
            workspace.VerifiedOperations.ToHashSet(StringComparer.Ordinal), c => api.PreviewPlan(c));

        var entries = string.Join("; ", plan.Entries.Select(e => $"{e.Planned.Resource} {e.Planned.Kind} {e.Disposition} {e.Reason?.Code} {string.Join(",", e.Reason?.Arguments.Select(a => $"{a.Key}={a.Value}") ?? [])}"));
        Assert.True(plan.Blockers.Count == 0, "blockers: " + string.Join(", ", plan.Blockers.Select(b => b.Code)));
        Assert.True(plan.Entries.Count == 3 && plan.Entries.All(e => e.Disposition == ReviewDisposition.Ready), entries);
        Assert.Equal(3, plan.SubmitCount);
        Assert.All(remote.Server.Requests, r => Assert.Equal("GET", r.Method));
    }

    // Headless counterpart of ConnectedDialogTests.Dependents_of_a_create_settled_at_the_end_of_the_round_run_in_that_round:
    // the same seeded drafts and misbehaving server, planned and run in-process with the real executor (no review page,
    // no task center). The fleet's answer carries no ID; the end-of-round lookup binds it and the aircraft and route then
    // run in the same round, each write sent once.
    [Fact]
    public async Task Dependents_of_a_create_settled_at_the_end_of_the_round_run_in_that_round()
    {
        using var dir = TempDataDirectory.Create("vamsys-connected-ui-");
        var target = Path.Combine(dir.Path, "data");
        var remote = ConnectedUiFixture.Seed(target, Scenario.DependentsOfUnreadableCreate);
        var store = new WorkspaceStore(target);
        var workspace = store.Load(store.List().Single(w => w.Name == "QA connected").Id);

        var job = await ConnectedUiFixture.SubmitInProcess(remote, workspace);

        Assert.Equal(JobStatus.Completed, job.StatusCode);
        Assert.Equal(3, job.Items.Count);
        Assert.All(job.Items, i => Assert.Equal(ItemState.Succeeded, i.State));
        var fleet = job.Items.Single(i => i.Resource == ResourceKind.Fleets);
        var found = Assert.Single(fleet.RecoveryCandidates);
        Assert.Equal(("90", "Automatic"), (found.RemoteId, found.Source));
        Assert.Equal("90", fleet.RemoteId);
        var writes = remote.Server.Requests.Where(r => r.Method != "GET").ToList();
        Assert.All(writes, r => Assert.Equal("POST", r.Method));
        Assert.Equal(3, writes.Count);
        Assert.EndsWith("/fleet", writes[0].Path, StringComparison.Ordinal);
        Assert.EndsWith("/fleet/90/aircraft", writes[1].Path, StringComparison.Ordinal);
        Assert.EndsWith("/routes", writes[2].Path, StringComparison.Ordinal);
    }
}
