using VamSys.Core;
using VamSys.Infrastructure;
using VamSys.IntegrationTests.Golden.Requests;
using VamSys.TestSupport;
using static VamSys.IntegrationTests.Api.PipelineHarness;

namespace VamSys.IntegrationTests.Api;

/// <summary>
/// The write-safety state table (R7 step 3): every resource and operation against every way its write can end, and every
/// point a run can stop at, instead of one test per historical problem. The rule under test (I01, F15): a write is sent at
/// most once unless the server provably did not process it (it was throttled or refused authentication), and anything
/// that may have reached the server is only ever read afterwards. A throttled write is sent once more in the same round
/// (E7, D5); a refused login pauses the job until the user resumes it.
/// </summary>
[Collection(StoredWorldCollection.Name)]
public class WriteSafetyTableTests
{
    const string Dep = "Departure Airport (ICAO/IATA)", Arr = "Arrival Airport (ICAO/IATA)";

    // One change per resource and operation, made on the standard world, and the method of its (first) write.
    public static readonly Dictionary<string, (Action<GoldenWorld> Change, string Method)> Operations = new()
    {
        ["airport create"] = (w => w.New(ResourceKind.Airports, ("ICAO/IATA", "EGSS"), ("Name", "Stansted")), "POST"),
        ["airport update"] = (w => w.Edit(ResourceKind.Airports, "101", ("Name", "Heathrow")), "PUT"),
        ["airport delete"] = (w => w.MarkDelete(ResourceKind.Airports, "103"), "DELETE"),
        ["fleet create"] = (w => w.New(ResourceKind.Fleets, ("Name", "Fleet 9"), ("Type Code", "A320"), ("Type (pax/cargo/...)", "cargo"), ("Parameter IDs", "1")), "POST"),
        ["fleet update"] = (w => w.Edit(ResourceKind.Fleets, "6", ("Name", "Fleet 6 renamed")), "PUT"),
        ["fleet delete"] = (w => w.MarkDelete(ResourceKind.Fleets, "6"), "DELETE"),
        ["aircraft create"] = (w => w.New(ResourceKind.Aircraft, ("Fleet ID", "4"), ("Name", "Plane Two"), ("Registration", "QA-TEST")), "POST"),
        ["aircraft update"] = (w => w.Edit(ResourceKind.Aircraft, "8", ("Name", "Plane renamed")), "PUT"),
        ["aircraft delete"] = (w => w.MarkDelete(ResourceKind.Aircraft, "8"), "DELETE"),
        ["routing create"] = (w => w.New(ResourceKind.Routings, (Dep, "EGLL"), (Arr, "EGKK"), ("Route String", "UL2")), "POST"),
        ["routing update"] = (w => w.Edit(ResourceKind.Routings, "9", ("Remarks", "updated remarks")), "PUT"),
        ["routing delete"] = (w => w.MarkDelete(ResourceKind.Routings, "9"), "DELETE"),
        ["route create"] = (w => w.New(ResourceKind.Routes, ("Type", "jumpseat"), ("Callsign", "CAO1234"), ("Flight Number", "CA1234"), (Dep, "EGKK"), (Arr, "EGCC"), ("Remarks", "fixture jumpseat")), "POST"),
        ["route update"] = (w => w.Edit(ResourceKind.Routes, "10", ("Remarks", "scheduled remarks changed")), "PUT"),
        ["route delete"] = (w => w.MarkDelete(ResourceKind.Routes, "11"), "DELETE"),
    };

    public static TheoryData<string> OperationNames() => [.. Operations.Keys];

    static int Writes(StoredWorld s, string method) => s.Remote.Server.Requests.Count(r => r.Method == method);
    static int AllWrites(StoredWorld s) => s.Remote.Server.Requests.Count(r => r.Method != "GET");

    // ---- table A: how the write ended × resource and operation --------------------------------------------------------

    // How the server answers the item's first write. Throttled and refused-authentication answers prove the write was not
    // processed; every other failure may have reached the server.
    public enum Outcome { FieldError, Throttled, AuthRefused, ServerError, TimeoutNotApplied, TimeoutApplied }

    static bool ProvenNotProcessed(Outcome outcome) => outcome is Outcome.Throttled or Outcome.AuthRefused;

    public static TheoryData<string, Outcome> OperationsByOutcome()
    {
        var data = new TheoryData<string, Outcome>();
        foreach (var operation in Operations.Keys)
            foreach (var outcome in Enum.GetValues<Outcome>())
                data.Add(operation, outcome);
        return data;
    }

    // Answers the first write of the given method as the outcome says; later requests reach the fake server normally.
    static void Answer(StoredWorld s, string method, Outcome outcome)
    {
        var answered = false;
        s.Remote.Intercept = r =>
        {
            if (r.Method != method || answered)
                return null;
            answered = true;
            switch (outcome)
            {
                case Outcome.FieldError: return FakeResponse.WithStatus(422, """{"message":"The given data was invalid.","errors":{"name":["The name is invalid."]}}""");
                case Outcome.Throttled: return RateLimited();
                case Outcome.AuthRefused: return Status(401);
                case Outcome.ServerError: return Status(500);
                case Outcome.TimeoutApplied: s.Remote.Respond(r); throw new TimeoutException("simulated: applied, answer lost");
                default: throw new TimeoutException("simulated: lost before the server applied it");
            }
        };
    }

    [Theory]
    [MemberData(nameof(OperationsByOutcome))]
    public async Task A_write_is_resent_only_when_the_server_provably_did_not_process_it_and_otherwise_only_read(string operation, Outcome outcome)
    {
        using var s = new StoredWorld();
        var (change, method) = Operations[operation];
        change(s.World);
        var job = s.Submit();
        Answer(s, method, outcome);

        await s.Run(job);

        var item = Assert.Single(job.Items);
        // A write the server applied is read once more at the end of the round (E2): an update or a delete that reads as
        // intended is settled there (a soft delete without its 204 as "without receipt", D3). A create has no id to read;
        // the record it made is searched for (E1), but this fixture's created_at lies outside the request window.
        var settledByRead = item.Kind != ChangeKind.Create;
        var expected = outcome switch
        {
            Outcome.FieldError => ItemState.Failed,
            Outcome.Throttled => ItemState.Succeeded,
            Outcome.AuthRefused => ItemState.Pending,
            Outcome.TimeoutApplied when settledByRead => ItemState.Succeeded,
            _ => ItemState.Unknown
        };
        Assert.True(item.State == expected, $"{operation} {outcome}: {item.State} {Code(item)}");
        Assert.Equal(outcome == Outcome.Throttled ? 2 : 1, Writes(s, method));
        Assert.Equal(item.State, s.Disk.Jobs.Single().Items.Single().State);
        if (outcome == Outcome.FieldError)
            Assert.Equal(["The name is invalid."], item.DiagnosticDetails!.FieldErrors["name"]);
        var before = s.Remote.Server.Requests.Count;

        // Resuming the task (as after a restart or the Resume command).
        await s.Run(job);

        var later = s.Remote.Server.Requests.Skip(before).ToList();
        if (ProvenNotProcessed(outcome))
        {
            Assert.Equal(ItemState.Succeeded, item.State);
            Assert.Equal(2, Writes(s, method));
        }
        else
        {
            Assert.Equal(1, Writes(s, method));
            Assert.All(later, r => Assert.Equal("GET", r.Method));
            // A refused write stays failed; a write that may have reached the server stays unknown until a read proves it took
            // effect, and one the server never applied is never taken for done.
            if (outcome == Outcome.FieldError)
                Assert.Equal(ItemState.Failed, item.State);
            else if (outcome == Outcome.TimeoutApplied)
                Assert.Contains(item.State, new[] { ItemState.Unknown, ItemState.Succeeded });
            else
                Assert.Equal(ItemState.Unknown, item.State);
        }
        Assert.Equal(item.State, s.Disk.Jobs.Single().Items.Single().State);
    }

    // ---- table B: where the run stopped × resource and operation -------------------------------------------------------

    // The run stops because a checkpoint cannot be written: the one that marks the write in flight (so it is never sent),
    // or the one that records its answer (so the disk still says in flight although the server has it).
    public enum Stop { BeforeSending, AfterTheAnswer }

    public static TheoryData<string, Stop> OperationsByStop()
    {
        var data = new TheoryData<string, Stop>();
        foreach (var operation in Operations.Keys)
            foreach (var stop in Enum.GetValues<Stop>())
                data.Add(operation, stop);
        return data;
    }

    [Theory]
    [MemberData(nameof(OperationsByStop))]
    public async Task After_a_run_stops_at_any_point_the_restarted_program_never_sends_the_write_twice(string operation, Stop stop)
    {
        using var s = new StoredWorld();
        var (change, method) = Operations[operation];
        change(s.World);
        var job = s.Submit();
        var failing = new FailingWriter(s.Writer, checkpoint => checkpoint.Item?.ApiSteps is { } steps && (stop == Stop.BeforeSending
            ? steps.Any(step => step.State == BatchStepState.InFlight) && Writes(s, method) == 0
            : Writes(s, method) == 1 && steps.Any(step => step.State == BatchStepState.ResponseReceived)));

        // The run stops on the first checkpoint it cannot write and sends nothing after it.
        await Assert.ThrowsAsync<PersistenceException>(() => s.Run(job, failing));

        Assert.Equal(1, failing.Hits);
        Assert.Equal(stop == Stop.BeforeSending ? 0 : 1, Writes(s, method));

        // The program starts again from what is on disk, with nothing from the stopped run in memory.
        var disk = s.Disk;
        var stored = disk.Jobs.Single();
        await new OperationsBatchExecutor(s.Adapter(disk), disk).ExecuteAsync(stored, s.Store.CheckpointWriter(disk), Ct);

        var item = stored.Items.Single();
        Assert.Equal(1, Writes(s, method));
        if (stop == Stop.BeforeSending)
            Assert.Equal(ItemState.Succeeded, item.State);
        else
            Assert.Contains(item.State, new[] { ItemState.Unknown, ItemState.Succeeded });
        Assert.Equal(item.State, s.Store.Load(disk.Id).Jobs.Single().Items.Single().State);
    }

    [Theory]
    [MemberData(nameof(OperationNames))]
    public async Task An_accepted_write_succeeds_with_one_request_and_a_second_run_sends_nothing(string operation)
    {
        using var s = new StoredWorld();
        var (change, method) = Operations[operation];
        change(s.World);
        var job = s.Submit();

        await s.Run(job);

        var item = Assert.Single(job.Items);
        Assert.True(item.State == ItemState.Succeeded, $"{operation}: {item.State} {Code(item)} {item.Message}");
        Assert.Equal(1, Writes(s, method));
        var writes = AllWrites(s);

        await s.Run(job);

        Assert.Equal(writes, AllWrites(s));
        Assert.Equal(ItemState.Succeeded, s.Disk.Jobs.Single().Items.Single().State);
    }
}
