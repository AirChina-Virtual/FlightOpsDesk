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
}
