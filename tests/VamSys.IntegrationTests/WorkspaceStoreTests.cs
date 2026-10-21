using VamSys.Core;
using VamSys.Infrastructure;
using VamSys.TestSupport;

namespace VamSys.IntegrationTests;

public class WorkspaceStoreTests
{
    [Fact]
    public void Save_then_load_round_trips_a_workspace_in_an_isolated_directory()
    {
        using var dir = TempDataDirectory.Create();
        var store = new WorkspaceStore(dir.Path);
        var workspace = new Workspace { Name = "Smoke", AirlineId = "7", Mode = RunMode.Online };

        store.Save(workspace);
        var loaded = new WorkspaceStore(dir.Path).Load(workspace.Id);

        Assert.Equal("Smoke", loaded.Name);
        Assert.Equal("7", loaded.AirlineId);
        Assert.Equal(RunMode.Online, loaded.Mode);
    }

    [Fact]
    public async Task Fake_server_records_requests_in_order()
    {
        var server = new FakeOperationsServer()
            .On(HttpMethod.Get, "fleet", FakeResponse.Ok("{\"data\":[]}"))
            .Enqueue(HttpMethod.Post, "fleet", FakeResponse.WithStatus(500), FakeResponse.Ok());
        using var client = server.CreateClient();

        await client.GetAsync("/api/v3/operations/fleet?page=1", TestContext.Current.CancellationToken);
        var first = await client.PostAsync("/api/v3/operations/fleet", new StringContent("{\"a\":1}"), TestContext.Current.CancellationToken);
        var second = await client.PostAsync("/api/v3/operations/fleet", new StringContent("{\"a\":2}"), TestContext.Current.CancellationToken);

        Assert.Equal(500, (int)first.StatusCode);
        Assert.Equal(200, (int)second.StatusCode);
        Assert.Equal(["GET /api/v3/operations/fleet?page=1", "POST /api/v3/operations/fleet", "POST /api/v3/operations/fleet"],
            server.Requests.Select(r => r.Method + " " + r.PathAndQuery));
        Assert.Equal("{\"a\":2}", server.Requests[2].Body);
    }
}

