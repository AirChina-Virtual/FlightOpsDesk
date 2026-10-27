using VamSys.Core;
using VamSys.Infrastructure;
using VamSys.TestSupport;
using static VamSys.UnitTests.Review.ReviewPlanFixture;

namespace VamSys.UnitTests.Review;

public class ReviewPlanGuardTests
{
    static ChangeItem Change(ResourceKind resource, int n) => new()
    {
        Resource = resource,
        Kind = ChangeKind.Create,
        After = Row(n, ("Name", "row " + n)),
        Fields = new() { ["Name"] = new(FieldIntent.Set, "row " + n) }
    };

    [Theory]
    [InlineData("ApiConflict")]
    [InlineData("ApiPendingJob")]
    public void A_workspace_conflict_or_pending_job_blocks_every_ready_item(string code)
    {
        var plan = ReviewPlanBuilder.Build([Change(ResourceKind.Airports, 1), Change(ResourceKind.Fleets, 2)], RunMode.Online, true, new HashSet<string>(), _ => { }, [Messages.Define(code)]);
        Assert.Empty(plan.SubmitItems);
        Assert.All(plan.Entries, e => Assert.Equal(ReviewDisposition.Blocked, e.Disposition));
    }

    [Fact]
    public void Values_moving_between_adjacent_fields_or_into_a_field_name_do_not_collide_in_the_content_key()
    {
        static string Key(params (string, string)[] fields)
        {
            var w = new Workspace();
            w.Resources[ResourceKind.Fleets].Draft.Add(new DataRow { Fields = fields.ToDictionary(f => f.Item1, f => f.Item2) });
            return ReviewPlanBuilder.Build(Planner.PlanWorkspace(w), RunMode.Online, true, new HashSet<string>(), _ => { }).ContentKey();
        }
        Assert.NotEqual(Key(("Name", "ab"), ("Type Code", "")), Key(("Name", "a"), ("Type Code", "b")));
        Assert.NotEqual(Key(("Name", "a")), Key(("Namea", "")));
    }

    [Fact]
    public void The_content_key_is_computed_once_per_plan_and_is_a_sha256_hex_string()
    {
        var plan = Build(Mixed());
        Assert.Same(plan.ContentKey(), plan.ContentKey());
        Assert.Equal(64, plan.ContentKey().Length);
    }

    [Fact]
    public void Building_the_review_against_the_real_adapter_sends_no_request_and_asks_for_no_token()
    {
        var w = new Workspace { AirlineId = "7" };
        w.Resources[ResourceKind.Airports].Draft.Add(new DataRow { Fields = new Dictionary<string, string> { ["ICAO/IATA"] = "EGLL", ["Name"] = "London" } });
        using var server = new FakeOperationsServer();
        var tokens = 0;
        using var client = server.CreateClient();
        var api = new OperationsAdapter(new OperationsTransport(client, OperationsAdapter.BaseUri, "review",
            new TokenProvider(_ => { tokens++; throw new InvalidOperationException("Preview requested token"); }), InstantTime.Requests()), w);

        var plan = ReviewPlanBuilder.Build(Planner.PlanWorkspace(w), RunMode.Online, true, new HashSet<string>(), c => api.PreviewPlan(c));

        Assert.Single(plan.SubmitItems);
        Assert.Empty(server.Requests);
        Assert.Equal(0, tokens);
    }
}
