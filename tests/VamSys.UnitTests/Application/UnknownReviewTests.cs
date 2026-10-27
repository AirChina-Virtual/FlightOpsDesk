using System.Text.Json;
using VamSys.Core;

namespace VamSys.UnitTests.Application;

public class UnknownReviewTests
{
    static ChangeItem Unknown() => new() { Resource = ResourceKind.Routes, Kind = ChangeKind.Create, State = ItemState.Unknown };

    [Fact]
    public async Task Review_skips_items_an_earlier_step_resolved_reviews_replacements_and_recovers_once_at_the_end()
    {
        var job = new BatchJob { Mode = RunMode.Online, Items = [Unknown(), Unknown(), Unknown(), new() { Resource = ResourceKind.Routes, Kind = ChangeKind.Delete }] };
        var (first, resolved, replaced) = (job.Items[0], job.Items[1], job.Items[2]);
        var replacement = JsonSerializer.Deserialize<ChangeItem>(JsonSerializer.Serialize(replaced))!;
        var reviewed = new List<ChangeItem>();
        var events = new List<string>();

        await UnknownReview.RunAsync(job, item =>
        {
            reviewed.Add(item);
            events.Add("review");
            if (item == first)
            {
                resolved.State = ItemState.Succeeded;
                job.Items[2] = replacement;
            }
            return Task.FromResult(true);
        }, () => { events.Add("recover"); return Task.CompletedTask; });

        Assert.Equal([first, replacement], reviewed);
        Assert.Equal(["review", "review", "recover"], events);
    }

    [Fact]
    public async Task Nothing_is_recovered_when_no_review_saved_a_recovery_id()
    {
        var job = new BatchJob { Mode = RunMode.Online, Items = [Unknown(), Unknown()] };
        var recovered = 0;
        await UnknownReview.RunAsync(job, _ => Task.FromResult(false), () => { recovered++; return Task.CompletedTask; });
        Assert.Equal(0, recovered);
    }
}
