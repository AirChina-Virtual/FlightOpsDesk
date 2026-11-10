using VamSys.Core;
using VamSys.IntegrationTests.Golden.Requests;

namespace VamSys.IntegrationTests.Api;

/// <summary>3.3 (F3): the confirmation's request estimate against what the executor really sends to the fake server.</summary>
[Collection(StoredWorldCollection.Name)]
public class SubmissionEstimateTests
{
    public static TheoryData<string> Operations() => WriteSafetyTableTests.OperationNames();

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task One_operation_is_estimated_exactly(string operation)
    {
        var w = new GoldenWorld();
        WriteSafetyTableTests.Operations[operation].Change(w);
        var items = new ChangePlanner().PlanWorkspace(w.Workspace);
        var estimate = SubmissionEstimate.Requests(items, w.Workspace);
        await w.RunAsync();
        Assert.Equal(ItemState.Succeeded, w.Job!.Items.Single().State);
        Assert.Equal(w.Remote.Server.Requests.Count, estimate);
    }

    [Fact]
    public async Task A_mixed_task_is_estimated_within_ten_percent()
    {
        const string Dep = "Departure Airport (ICAO/IATA)", Arr = "Arrival Airport (ICAO/IATA)";
        var w = new GoldenWorld();
        w.Edit(ResourceKind.Airports, "101", ("Name", "Heathrow"));
        w.Edit(ResourceKind.Fleets, "4", ("Name", "Fleet 4 renamed"));
        w.Edit(ResourceKind.Routes, "10", ("Remarks", "changed"));
        w.Edit(ResourceKind.Routes, "12", ("Fleet IDs", "4"));
        w.MarkDelete(ResourceKind.Airports, "103");
        w.New(ResourceKind.Airports, ("ICAO/IATA", "EGSS"), ("Name", "Stansted"));
        w.New(ResourceKind.Fleets, ("Name", "Fleet 9"), ("Type Code", "A320"), ("Type (pax/cargo/...)", "cargo"), ("Parameter IDs", "1"));
        w.New(ResourceKind.Routings, (Dep, "EGLL"), (Arr, "EGKK"), ("Route String", "UL2"));
        w.New(ResourceKind.Routes, ("Type", "scheduled"), ("Callsign", "CAO300"), ("Flight Number", "CA300"), (Dep, "EGKK"), (Arr, "EGLL"), ("Fleet IDs", "4,5"));
        w.New(ResourceKind.Routes, ("Type", "scheduled"), ("Callsign", "CAO301"), ("Flight Number", "CA301"), (Dep, "EGKK"), (Arr, "EGLL"), ("Fleet IDs", "4"));
        var items = new ChangePlanner().PlanWorkspace(w.Workspace);
        var estimate = SubmissionEstimate.Requests(items, w.Workspace);
        await w.RunAsync();
        Assert.All(w.Job!.Items, i => Assert.Equal(ItemState.Succeeded, i.State));
        var actual = w.Remote.Server.Requests.Count;
        Assert.True(Math.Abs(estimate - actual) <= actual / 10.0, $"estimated {estimate}, sent {actual}");
    }
}
