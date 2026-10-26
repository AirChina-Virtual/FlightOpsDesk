using VamSys.Core;
using VamSys.Infrastructure;
using VamSys.TestSupport;

namespace VamSys.UnitTests.Api;

/// <summary>
/// The proof that a soft delete (airports, routes) took effect: the saved DELETE step, the 204 answer and a later 404 read
/// must agree end to end. Anything less leaves the item Unknown.
/// </summary>
public class DeletionVerifierTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    // The evidence of a delete that was sent and answered one minute ago.
    sealed class Scenario
    {
        public readonly ApiKit Kit = new();
        public readonly ChangeItem Item;
        public readonly DateTimeOffset Started = DateTimeOffset.UtcNow.AddMinutes(-1);

        public Scenario(ResourceKind kind = ResourceKind.Airports)
        {
            // Reading any airport names the airline of the session, which routes (they carry no airline_id) rely on.
            var airport = Kit.Map(ResourceKind.Airports, ApiKit.Egll);
            var before = kind == ResourceKind.Airports ? airport : Kit.Map(kind, ApiKit.Jumpseat11);
            Item = ApiKit.Delete(kind, before);
            Item.WriteAccepted = true;
            var path = ResourcePathOf(kind);
            Item.ApiSteps = [new BatchStep { Method = "DELETE", Path = path, State = BatchStepState.ResponseReceived, StartedAt = Started, FinishedAt = Started.AddSeconds(3) }];
            Item.DiagnosticDetails = new ApiDiagnostic { Method = "DELETE", Path = "/api/v3/operations/" + path, Status = 204, StartedAt = Started.AddSeconds(1), FinishedAt = Started.AddSeconds(2) };
            Kit.Server.On(HttpMethod.Get, path, FakeResponse.WithStatus(404));
        }

        static string ResourcePathOf(ResourceKind kind) => kind == ResourceKind.Airports ? "airports/101" : "routes/11";
        string Id => Item.Resource == ResourceKind.Routes ? "11" : "101";

        // Reads the record (404 unless the test says otherwise) and asks for the verdict.
        public async Task<bool> Record()
        {
            var current = await Kit.Api.FindRow(Item.Resource, Item.Before!, Id, Ct);
            return Kit.Api.RecordDeletionVerification(Item, current);
        }
    }
    [Theory]
    [InlineData(ResourceKind.Airports)]
    [InlineData(ResourceKind.Routes)]
    public async Task A_complete_chain_of_evidence_proves_the_delete(ResourceKind kind)
    {
        var s = new Scenario(kind);
        Assert.True(await s.Record());
        Assert.Equal("ApiInvisible", s.Item.DeletionVerification!.Outcome);
        Assert.Equal(204, s.Item.DeletionVerification.WriteStatus);
        Assert.Equal(404, s.Item.DeletionVerification.ReadStatus);
        Assert.False(s.Item.DeletionVerification.WasManuallyConfirmed);
        Assert.Empty(s.Item.VerificationDifferences);
    }

    [Fact]
    public async Task A_record_that_can_still_be_read_is_not_deleted()
    {
        var s = new Scenario();
        s.Kit.Server.On(HttpMethod.Get, "airports/101", ApiKit.Data(ApiKit.Egll));
        Assert.False(await s.Record());
        Assert.Equal("StillReadable", s.Item.DeletionVerification!.Outcome);
        Assert.Equal(200, s.Item.DeletionVerification.ReadStatus);
    }

    // Each mutation removes or spoils one link of the chain. None of them may count as proof.
    public static TheoryData<string> Spoiled => new()
    {
        "write not accepted", "step missing", "two steps", "step is not a delete", "step still in flight", "step path differs", "step has a body",
        "write answered 200", "write path differs", "write started before the step", "write finished after the step", "step not finished",
        "write response missing", "other airline", "ids differ", "not a soft delete resource"
    };

    [Theory]
    [MemberData(nameof(Spoiled))]
    public async Task One_broken_link_in_the_chain_leaves_the_delete_unproven(string spoil)
    {
        var s = new Scenario();
        var item = s.Item;
        var step = item.ApiSteps[0];
        var write = item.DiagnosticDetails!;
        switch (spoil)
        {
            case "write not accepted": item.WriteAccepted = false; break;
            case "step missing": item.ApiSteps = []; break;
            case "two steps": item.ApiSteps = [step, new BatchStep { Method = "DELETE", Path = "airports/101" }]; break;
            case "step is not a delete": step.Method = "PUT"; break;
            case "step still in flight": step.State = BatchStepState.InFlight; break;
            case "step path differs": step.Path = "airports/102"; break;
            case "step has a body": step.Body = new() { ["x"] = System.Text.Json.JsonSerializer.SerializeToElement(1) }; break;
            case "write answered 200": item.DiagnosticDetails = write with { Status = 200 }; break;
            case "write path differs": item.DiagnosticDetails = write with { Path = "/api/v3/operations/airports/102" }; break;
            case "write started before the step": item.DiagnosticDetails = write with { StartedAt = s.Started.AddSeconds(-5) }; break;
            case "write finished after the step": item.DiagnosticDetails = write with { FinishedAt = s.Started.AddSeconds(10) }; break;
            case "step not finished": step.FinishedAt = null; break;
            case "write response missing": item.DiagnosticDetails = null; break;
            case "other airline": item.After = item.After.WithIdentity(new("101", "9")); break;
            case "ids differ": item.After = item.After.WithIdentity(new("102", "7")); break;
            case "not a soft delete resource": item.Resource = ResourceKind.Fleets; break;
        }
        Assert.False(await s.Record());
        Assert.Equal("MissingWriteEvidence", s.Item.DeletionVerification!.Outcome);
    }

    [Fact]
    public async Task The_read_must_come_after_the_write_finished()
    {
        var s = new Scenario();
        // the write is said to have finished in the future, after the read that follows it
        s.Item.DiagnosticDetails = s.Item.DiagnosticDetails! with { FinishedAt = DateTimeOffset.UtcNow.AddHours(1) };
        s.Item.ApiSteps[0].FinishedAt = DateTimeOffset.UtcNow.AddHours(2);
        Assert.False(await s.Record());
    }

    [Fact]
    public async Task A_read_that_is_not_a_404_is_no_proof()
    {
        var s = new Scenario();
        s.Kit.Server.On(HttpMethod.Get, "airports/101", FakeResponse.WithStatus(410));
        await Assert.ThrowsAnyAsync<ApiResponseException>(() => s.Kit.Api.FindRow(ResourceKind.Airports, s.Item.After, "101", Ct));
        Assert.False(s.Kit.Api.RecordDeletionVerification(s.Item, null));
    }

    [Fact]
    public async Task A_delete_confirmed_by_hand_keeps_that_fact_in_the_evidence()
    {
        var s = new Scenario();
        s.Item.State = ItemState.ManuallyConfirmed;
        Assert.True(await s.Record());
        Assert.True(s.Item.DeletionVerification!.WasManuallyConfirmed);
        // a later verification of an item that was confirmed by hand before still remembers it
        s.Item.State = ItemState.Unknown;
        Assert.True(await s.Record());
        Assert.True(s.Item.DeletionVerification!.WasManuallyConfirmed);
    }
}
