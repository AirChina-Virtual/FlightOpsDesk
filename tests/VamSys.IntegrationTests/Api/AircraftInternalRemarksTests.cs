using System.Text.Json;
using System.Text.Json.Nodes;
using VamSys.Core;
using VamSys.IntegrationTests.Golden.Requests;
using VamSys.TestSupport;
using static VamSys.IntegrationTests.Api.PipelineHarness;

namespace VamSys.IntegrationTests.Api;

/// <summary>
/// A new aircraft's internal remarks (artifacts/write-evidence/aircraft-remarks.json, run FODESK-WE 20261118T005911Z): the
/// server does not keep them from the POST, only from a PUT. The create is a POST without them, then, once the POST's answer
/// gave the id, a PUT of that field alone to the new record. The aircraft is never created twice and the PUT never goes to
/// an id the program did not get from the POST's answer. The fake server is switched to behave like the live one.
/// </summary>
public class AircraftInternalRemarksTests
{
    const string Created = "fleet/4/aircraft/90";

    static GoldenWorld World(string remarks = "hangar 3")
    {
        var w = new GoldenWorld();
        w.Remote.AircraftCreateDropsInternalRemarks = true;
        w.New(ResourceKind.Aircraft, ("Name", "Plane Two"), ("Registration", "QA-2"), ("Fleet ID", "4"), ("Internal Remarks", remarks));
        return w;
    }

    static string[] Writes(GoldenWorld w) => Calls(w).Where(c => !c.StartsWith("GET ", StringComparison.Ordinal)).ToArray();
    static JsonObject Body(GoldenWorld w, string method) => JsonNode.Parse(w.Remote.Server.Requests.Single(r => r.Method == method).Body!)!.AsObject();

    [Fact]
    public async Task A_new_aircraft_gets_its_internal_remarks_from_a_put_after_the_post_and_verifies()
    {
        var w = World();
        await w.RunAsync();
        var item = w.Job!.Items.Single();
        Assert.Equal(ItemState.Succeeded, item.State);
        Assert.Equal(["POST fleet/4/aircraft", "PUT " + Created], Writes(w));
        Assert.False(Body(w, "POST").ContainsKey("internal_remarks"));
        Assert.Equal("""{"internal_remarks":"hangar 3"}""", Body(w, "PUT").ToJsonString());
        Assert.Equal("hangar 3", w.Remote.Rows[Created]["internal_remarks"]!.GetValue<string>());
        Assert.Equal("hangar 3", w.Workspace.Resources[ResourceKind.Aircraft].Draft.Single(r => r.Identity?.RemoteId == "90").Get("Internal Remarks"));
    }

    [Fact]
    public async Task A_new_aircraft_without_internal_remarks_is_one_post()
    {
        var w = World("");
        await w.RunAsync();
        Assert.Equal(ItemState.Succeeded, w.Job!.Items.Single().State);
        Assert.Equal(["POST fleet/4/aircraft"], Writes(w));
    }

    [Fact]
    public async Task A_rate_limited_put_is_sent_again_on_continue_without_a_second_post()
    {
        var w = World();
        var limited = 0;
        w.Remote.Intercept = r => r.Method == "PUT" && limited++ == 0 ? RateLimited() : null;
        await w.RunAsync();
        var item = w.Job!.Items.Single();
        // the record exists, so the item is not sent again in this round: it waits for Continue
        Assert.Equal(ItemState.Unknown, item.State);
        Assert.Equal("ApiResumeCreate", Code(item));
        Assert.True(item.CreateCompleted);
        Assert.Equal(BatchStepState.Prepared, item.ApiSteps[1].State);
        // Continue verifies the record against the POST, then sends the PUT only
        await w.ResumeAsync("continue");
        Assert.Equal(ItemState.Succeeded, item.State);
        Assert.Equal(["POST fleet/4/aircraft", "PUT " + Created, "PUT " + Created], Writes(w));
        Assert.Equal("hangar 3", w.Remote.Rows[Created]["internal_remarks"]!.GetValue<string>());
    }

    // The program stopped after the POST was answered and saved, before the PUT left the machine.
    static (GoldenWorld W, BatchJob Job, ChangeItem Item) Interrupted()
    {
        var w = new GoldenWorld();
        w.Remote.AircraftCreateDropsInternalRemarks = true;
        w.Remote.AddRow(Created, """{"id":90,"airline_id":7,"fleet_id":4,"name":"Plane Two","registration":"QA-2","internal_remarks":null}""");
        var draft = w.New(ResourceKind.Aircraft, ("Name", "Plane Two"), ("Registration", "QA-2"), ("Fleet ID", "4"), ("Internal Remarks", "hangar 3"));
        var item = new ChangeItem
        {
            Resource = ResourceKind.Aircraft,
            Kind = ChangeKind.Create,
            After = draft,
            Fields = draft.Fields.ToDictionary(p => p.Key, p => new FieldChange(FieldIntent.Set, p.Value)),
            State = ItemState.Running,
            CreateCompleted = true,
            RemoteId = "90",
            ApiSteps =
            [
                new BatchStep { Method = "POST", Path = "fleet/4/aircraft", State = BatchStepState.ResponseReceived, RemoteId = "90",
                    Body = new() { ["name"] = JsonSerializer.SerializeToElement("Plane Two"), ["registration"] = JsonSerializer.SerializeToElement("QA-2") } },
                new BatchStep { Method = "PUT", Path = "{created}", State = BatchStepState.Prepared, Body = new() { ["internal_remarks"] = JsonSerializer.SerializeToElement("hangar 3") } }
            ]
        };
        var job = new BatchJob { Mode = RunMode.Online, Items = [item] };
        w.Workspace.Jobs.Add(job);
        return (w, job, item);
    }

    [Fact]
    public async Task A_put_left_open_by_an_interruption_is_finished_on_continue_without_a_second_post()
    {
        var (w, job, item) = Interrupted();
        await Execute(w, job);
        Assert.Equal(ItemState.Succeeded, item.State);
        Assert.Equal(["GET " + Created, "PUT " + Created, "GET " + Created], Calls(w));
        Assert.Equal("hangar 3", w.Remote.Rows[Created]["internal_remarks"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_interrupted_create_whose_record_changed_since_gets_no_put()
    {
        var (w, job, item) = Interrupted();
        w.Remote.Rows[Created]["name"] = "Renamed by somebody";
        await Execute(w, job);
        Assert.Equal(ItemState.Unknown, item.State);
        Assert.Empty(Writes(w));
    }

    [Fact]
    public async Task A_put_whose_answer_was_lost_is_never_repeated_and_is_completed_when_the_record_shows_it()
    {
        var w = World();
        w.Remote.Intercept = r => r.Method == "PUT" ? Applied(w, r) : null;
        await w.RunAsync();
        // the end-of-round check reads the record, which shows the remarks: done, with the PUT sent once
        var item = w.Job!.Items.Single();
        Assert.Equal(ItemState.Succeeded, item.State);
        Assert.Equal(["POST fleet/4/aircraft", "PUT " + Created], Writes(w));
        Assert.Equal("hangar 3", w.Remote.Rows[Created]["internal_remarks"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_put_that_timed_out_unapplied_stays_unknown_and_is_not_repeated()
    {
        var w = World();
        w.Remote.Intercept = r => r.Method == "PUT" ? throw new TimeoutException("simulated") : null;
        await w.RunAsync();
        w.Remote.Intercept = null;
        await w.ResumeAsync("continue");
        var item = w.Job!.Items.Single();
        Assert.Equal(ItemState.Unknown, item.State);
        Assert.Equal(["POST fleet/4/aircraft", "PUT " + Created], Writes(w));
        Assert.True(item.CreateCompleted);
    }

    [Fact]
    public async Task Without_the_posts_answer_no_put_is_ever_sent()
    {
        var w = World();
        w.Remote.Intercept = r => r.Method == "POST" ? Applied(w, r) : null;
        await w.RunAsync();
        var item = w.Job!.Items.Single();
        Assert.Equal(ItemState.Unknown, item.State);
        Assert.False(item.CreateCompleted);
        w.Remote.Intercept = null;
        await w.ResumeAsync("continue");
        Assert.Equal(ItemState.Unknown, item.State);
        Assert.Equal(["POST fleet/4/aircraft"], Writes(w));
        Assert.Null(w.Remote.Rows[Created]["internal_remarks"]);
    }

    [Fact]
    public async Task A_put_the_server_answered_but_did_not_keep_stays_unknown_and_is_not_repeated()
    {
        var w = World();
        w.Remote.Intercept = r => r.Method == "PUT" ? FakeResponse.Ok("""{"data":{}}""") : null;
        await w.RunAsync();
        var item = w.Job!.Items.Single();
        Assert.Equal(ItemState.Unknown, item.State);
        Assert.Equal("ApiReadbackMismatch", Code(item));
        Assert.Equal("internal_remarks", Assert.Single(item.VerificationDifferences).Field);
        w.Remote.Intercept = null;
        await w.ResumeAsync("continue");
        Assert.Equal(ItemState.Unknown, item.State);
        Assert.Equal(1, Count(w, "PUT"));
        Assert.Equal(1, Count(w, "POST"));
    }

    // The server applies the request, then the answer is lost.
    static FakeResponse Applied(GoldenWorld w, RecordedRequest r)
    {
        w.Remote.Respond(r);
        throw new TimeoutException("answer lost");
    }
}
