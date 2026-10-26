using System.Net;
using System.Text.Json;
using VamSys.Core;
using VamSys.Infrastructure;
using VamSys.TestSupport;

namespace VamSys.UnitTests.Api;

/// <summary>
/// Sending the planned steps of one item. The save order is the write-safety rule: a step is saved as in flight before
/// it is sent, the created id is saved before the answer is parsed any further, and every answered step is saved before the next.
/// </summary>
public class StepWriterTests
{
    const string Dep = "Departure Airport (ICAO/IATA)", Arr = "Arrival Airport (ICAO/IATA)";

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static ApiKit Kit()
    {
        var kit = new ApiKit();
        kit.LoadStandardAirportsAndFleets();
        return kit;
    }

    // What the item and the server looked like at one save: step states, requests the server has seen, the item's remote id and flags.
    sealed record Saved(string Steps, int Requests, string? RemoteId, bool CreateCompleted, bool WriteAccepted, bool HasIdentity);

    static (List<Saved> Saves, Func<Task> Persist) Recorder(ApiKit kit, ChangeItem item)
    {
        var saves = new List<Saved>();
        return (saves, () =>
        {
            saves.Add(new(string.Join(',', item.ApiSteps.Select(s => s.State)), kit.Server.Requests.Count, item.RemoteId, item.CreateCompleted, item.WriteAccepted, item.After.Identity != null));
            return Task.CompletedTask;
        });
    }

    static List<ApiStep> TwoStepFleetCreate() =>
    [
        new(HttpMethod.Post, "fleet", new() { ["name"] = "Fleet X", ["code"] = "B738", ["type"] = "pax" }),
        new(HttpMethod.Put, "{created}", new() { ["max_pax"] = 180L })
    ];

    const string Created = """{"id":90,"airline_id":7,"name":"Fleet X","code":"B738","type":"pax"}""";

    // ---- one step ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_step_is_saved_in_flight_before_it_is_sent_and_answered_after_it()
    {
        var kit = Kit();
        kit.Server.On(HttpMethod.Put, "airports/101", ApiKit.Data(ApiKit.Egll));
        var item = ApiKit.Update(ResourceKind.Airports, kit.Map(ResourceKind.Airports, ApiKit.Egll), ("Name", "Heathrow"));
        var (saves, persist) = Recorder(kit, item);
        var id = await kit.Api.WriteStepsAsync(item, persist, Ct);
        Assert.Equal("101", id);
        Assert.Equal(2, saves.Count);
        Assert.Equal(new Saved("InFlight", 0, null, false, false, true), saves[0]);
        Assert.Equal(new Saved("ResponseReceived", 1, null, false, true, true), saves[1]);
        var step = item.ApiSteps[0];
        Assert.Equal("PUT", step.Method);
        Assert.Equal("airports/101", step.Path);
        Assert.True(step.StartedAt <= step.FinishedAt);
        Assert.Equal("Writing", item.DiagnosticDetails!.Phase);
        Assert.Equal(200, item.DiagnosticDetails.Status);
        Assert.Equal("""{"name":"Heathrow"}""", Assert.Single(kit.Server.Requests).Body);
    }

    [Fact]
    public async Task A_delete_is_sent_without_a_body()
    {
        var kit = Kit();
        kit.Server.On(HttpMethod.Delete, "airports/103", new FakeResponse(HttpStatusCode.NoContent, null));
        var airport = kit.Map(ResourceKind.Airports, """{"id":103,"airline_id":7,"icao":"EGCC","iata":"MAN","name":"Manchester"}""");
        var item = ApiKit.Delete(ResourceKind.Airports, airport);
        await kit.Api.WriteStepsAsync(item, () => Task.CompletedTask, Ct);
        var request = Assert.Single(kit.Server.Requests);
        Assert.Equal("DELETE", request.Method);
        Assert.Null(request.Body);
        Assert.Equal(204, item.DiagnosticDetails!.Status);
        Assert.True(item.WriteAccepted);
    }

    [Fact]
    public async Task A_failed_save_before_the_send_means_nothing_was_sent()
    {
        var kit = Kit();
        var item = ApiKit.Update(ResourceKind.Airports, kit.Map(ResourceKind.Airports, ApiKit.Egll), ("Name", "Heathrow"));
        await Assert.ThrowsAsync<IOException>(() => kit.Api.WriteStepsAsync(item, () => throw new IOException("disk full"), Ct));
        Assert.Empty(kit.Server.Requests);
    }

    [Fact]
    public async Task A_step_the_server_rejects_stays_in_flight_and_is_never_marked_answered()
    {
        var kit = Kit();
        kit.Server.On(HttpMethod.Put, "airports/101", FakeResponse.WithStatus(500));
        var item = ApiKit.Update(ResourceKind.Airports, kit.Map(ResourceKind.Airports, ApiKit.Egll), ("Name", "Heathrow"));
        await Assert.ThrowsAsync<ApiResponseException>(() => kit.Api.WriteStepsAsync(item, () => Task.CompletedTask, Ct));
        Assert.Equal(BatchStepState.InFlight, item.ApiSteps[0].State);
        Assert.NotNull(item.ApiSteps[0].FinishedAt);
        Assert.False(item.WriteAccepted);
        Assert.Single(kit.Server.Requests);
    }

    [Fact]
    public async Task A_request_that_never_left_is_reported_as_not_sent()
    {
        var kit = Kit();
        var item = ApiKit.Update(ResourceKind.Airports, kit.Map(ResourceKind.Airports, ApiKit.Egll), ("Name", "Heathrow"));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAsync<RequestNotSentException>(() => kit.Api.WriteStepsAsync(item, () => Task.CompletedTask, cancelled.Token));
        Assert.Empty(kit.Server.Requests);
        // it was saved as in flight first, which is why the executor prepares it again when it learns that nothing was sent
        Assert.Equal(BatchStepState.InFlight, item.ApiSteps[0].State);
    }

    [Fact]
    public async Task Values_the_program_never_sends_stop_the_step_before_it_is_saved_or_sent()
    {
        var kit = Kit();
        var item = ApiKit.Update(ResourceKind.Fleets, kit.Map(ResourceKind.Fleets, ApiKit.Fleet4), ("Name", "x"));
        var steps = new List<ApiStep> { new(HttpMethod.Put, "fleet/4", new() { ["weight_unit"] = "lb" }) };
        var saves = 0;
        var error = await Assert.ThrowsAnyAsync<Exception>(() => kit.Api.WriteStepsAsync(item, steps, () => { saves++; return Task.CompletedTask; }, Ct));
        Assert.Equal("ApiKnownWeightRestriction", MessageErrors.Describe(error).Code);
        Assert.Equal(0, saves);
        Assert.Empty(kit.Server.Requests);
    }

    // ---- creates ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_created_id_is_saved_before_the_answer_is_parsed_and_the_second_step_goes_to_the_new_record()
    {
        var kit = Kit();
        kit.Server.On(HttpMethod.Post, "fleet", FakeResponse.WithStatus(201, Created));
        kit.Server.On(HttpMethod.Put, "fleet/90", ApiKit.Data(Created));
        var item = ApiKit.Create(ResourceKind.Fleets, ("Name", "Fleet X"));
        var (saves, persist) = Recorder(kit, item);
        var id = await kit.Api.WriteStepsAsync(item, TwoStepFleetCreate(), persist, Ct);
        Assert.Equal("90", id);
        Assert.Equal(5, saves.Count);
        // POST in flight, nothing sent yet
        Assert.Equal(new Saved("InFlight,Prepared", 0, null, false, false, false), saves[0]);
        // the POST was answered: the id and the fact that the record exists are saved before the row is built
        Assert.Equal(new Saved("ResponseReceived,Prepared", 1, "90", true, false, false), saves[1]);
        // then the row follows
        Assert.Equal(new Saved("ResponseReceived,Prepared", 1, "90", true, false, true), saves[2]);
        // the PUT is in flight, and its address is the new record
        Assert.Equal(new Saved("ResponseReceived,InFlight", 1, "90", true, false, true), saves[3]);
        Assert.Equal(new Saved("ResponseReceived,ResponseReceived", 2, "90", true, true, true), saves[4]);
        Assert.Equal("fleet/90", item.ApiSteps[1].Path);
        Assert.Equal(["POST", "PUT"], kit.Server.Requests.Select(r => r.Method));
        Assert.Equal("90", item.After.Identity!.RemoteId);
    }

    [Fact]
    public async Task A_post_that_already_created_its_record_is_never_sent_again()
    {
        var kit = Kit();
        kit.Server.On(HttpMethod.Put, "fleet/90", ApiKit.Data(Created));
        var item = ApiKit.Create(ResourceKind.Fleets, ("Name", "Fleet X"));
        var steps = TwoStepFleetCreate();
        OperationsAdapter.PrepareSteps(item, steps);
        item.ApiSteps[0].State = BatchStepState.ResponseReceived;
        item.CreateCompleted = true;
        item.RemoteId = "90";
        await kit.Api.WriteStepsAsync(item, steps, () => Task.CompletedTask, Ct);
        var request = Assert.Single(kit.Server.Requests);
        Assert.Equal("PUT", request.Method);
        Assert.Equal("""{"max_pax":180}""", request.Body);
    }

    [Fact]
    public async Task An_answer_with_an_id_but_an_unreadable_record_still_leaves_the_id_saved()
    {
        var kit = Kit();
        // no airline_id: the full parse fails after the id was parsed
        kit.Server.On(HttpMethod.Post, "fleet", FakeResponse.WithStatus(201, """{"id":90,"name":"Fleet X"}"""));
        var item = ApiKit.Create(ResourceKind.Fleets, ("Name", "Fleet X"));
        var (saves, persist) = Recorder(kit, item);
        await Assert.ThrowsAnyAsync<Exception>(() => kit.Api.WriteStepsAsync(item, TwoStepFleetCreate(), persist, Ct));
        Assert.True(item.CreateCompleted);
        Assert.Equal("90", item.RemoteId);
        Assert.Equal(BatchStepState.ResponseReceived, item.ApiSteps[0].State);
        Assert.Equal(2, saves.Count);
        Assert.Equal("90", saves[1].RemoteId);
    }

    [Fact]
    public async Task An_answer_without_an_id_leaves_the_post_in_flight_and_the_item_unconfirmed()
    {
        var kit = Kit();
        kit.Server.On(HttpMethod.Post, "fleet", FakeResponse.WithStatus(201, """{"unexpected":true}"""));
        var item = ApiKit.Create(ResourceKind.Fleets, ("Name", "Fleet X"));
        await Assert.ThrowsAnyAsync<Exception>(() => kit.Api.WriteStepsAsync(item, TwoStepFleetCreate(), () => Task.CompletedTask, Ct));
        Assert.False(item.CreateCompleted);
        Assert.Null(item.RemoteId);
        Assert.Equal(BatchStepState.InFlight, item.ApiSteps[0].State);
    }

    // ---- jumpseat creates -------------------------------------------------------------------------------------

    static ChangeItem NewRoute(string type)
        => type == "jumpseat"
            ? ApiKit.Create(ResourceKind.Routes, ("Type", "jumpseat"), (Dep, "EGLL"), (Arr, "EGKK"), ("Callsign", "CAO1234"), ("Flight Number", "CA1234"))
            : ApiKit.Create(ResourceKind.Routes, ("Type", "scheduled"), (Dep, "EGLL"), (Arr, "EGKK"), ("Callsign", "CAO1234"), ("Flight Number", "CA1234"), ("Fleet IDs", "4"));

    [Fact]
    public async Task A_jumpseat_create_records_the_real_start_and_deadline_of_its_request()
    {
        var kit = Kit();
        kit.Server.On(HttpMethod.Post, "routes", FakeResponse.WithStatus(201, """{"id":90,"departure_id":101,"arrival_id":102,"type":"jumpseat","callsign":null,"flight_number":null}"""));
        var item = NewRoute("jumpseat");
        var (saves, persist) = Recorder(kit, item);
        await kit.Api.WriteStepsAsync(item, persist, Ct);
        // in flight, start of the request (with the deadline), id, answered
        Assert.Equal(4, saves.Count);
        var step = item.ApiSteps[0];
        Assert.NotNull(step.DeadlineAt);
        Assert.Equal(TimeSpan.FromSeconds(100), step.DeadlineAt - step.StartedAt);
    }

    [Fact]
    public async Task Other_creates_need_no_request_window()
    {
        var kit = Kit();
        kit.Server.On(HttpMethod.Post, "routes", FakeResponse.WithStatus(201, """{"id":90,"departure_id":101,"arrival_id":102,"type":"scheduled","callsign":"CAO1234","flight_number":"CA1234","fleet_ids":[4]}"""));
        var item = NewRoute("scheduled");
        var (saves, persist) = Recorder(kit, item);
        await kit.Api.WriteStepsAsync(item, persist, Ct);
        Assert.Equal(3, saves.Count);
        Assert.Null(item.ApiSteps[0].DeadlineAt);
    }

    [Fact]
    public void Prepared_steps_carry_the_method_the_path_and_the_body_as_json()
    {
        var item = ApiKit.Create(ResourceKind.Fleets, ("Name", "x"));
        OperationsAdapter.PrepareSteps(item, TwoStepFleetCreate());
        Assert.Equal(["POST", "PUT"], item.ApiSteps.Select(s => s.Method));
        Assert.Equal(["fleet", "{created}"], item.ApiSteps.Select(s => s.Path));
        Assert.Equal(JsonValueKind.Number, item.ApiSteps[1].Body["max_pax"].ValueKind);
        Assert.All(item.ApiSteps, s => Assert.Equal(BatchStepState.Prepared, s.State));
    }
}
