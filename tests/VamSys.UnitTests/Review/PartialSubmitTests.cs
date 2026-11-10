using VamSys.Core;
using static VamSys.UnitTests.Review.ReviewPlanFixture;

namespace VamSys.UnitTests.Review;

/// <summary>3.5 (F9, D8): submitting only some resources never sends a change ahead of the create it needs.</summary>
public class PartialSubmitTests
{
    static WorkspaceReviewPlan Ready() => Build(Mixed(), verified: new HashSet<string> { "Routes:Update", "Fleets:Create" });

    [Fact]
    public void All_resources_submit_the_same_objects_as_the_whole_plan()
    {
        var plan = Ready();
        var all = Enum.GetValues<ResourceKind>().ToHashSet();
        Assert.Equal(plan.SubmitItems, plan.SubmitItemsFor(all));
    }

    [Fact]
    public void Leaving_out_fleets_also_leaves_out_routes_that_name_a_new_fleet()
    {
        var plan = Ready();
        var items = plan.SubmitItemsFor(new HashSet<ResourceKind> { ResourceKind.Routes, ResourceKind.Airports });
        Assert.DoesNotContain(items, i => i.Resource == ResourceKind.Fleets);
        Assert.DoesNotContain(items, i => Number(i.After.LocalId) is 36 or 38); // they name new fleets 11 and 14
        Assert.Contains(items, i => Number(i.After.LocalId) == 37);
        Assert.Contains(items, i => i.Resource == ResourceKind.Airports);
        Assert.All(items, i => Assert.Contains(i, plan.SubmitItems));
    }

    [Fact]
    public void Only_fleets_sends_the_fleets_alone()
    {
        var plan = Ready();
        var items = plan.SubmitItemsFor(new HashSet<ResourceKind> { ResourceKind.Fleets });
        Assert.Equal([11, 12, 13, 14], items.Select(i => Number(i.After.LocalId)).Order());
    }
}
