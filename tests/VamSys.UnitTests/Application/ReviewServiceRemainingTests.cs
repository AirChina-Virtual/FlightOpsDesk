using VamSys.Core;

namespace VamSys.UnitTests.Application;

/// <summary>3.2 (F4): what an online task that proved an operation leaves to continue with.</summary>
public class ReviewServiceRemainingTests
{
    static (ReviewService Service, Workspace W) Open()
    {
        var storage = new FakeStorage();
        storage.Seed("va", RunMode.Online);
        var session = new WorkspaceSession(storage, new InteractionGate(), new FakeTimer());
        session.Open("default");
        session.Workspace.Mode = RunMode.Online;
        return (new ReviewService(session, new FakeOnline()), session.Workspace);
    }

    static DataRow Airport(string code) => new() { Fields = new Dictionary<string, string> { ["ICAO/IATA"] = code, ["Name"] = code, ["_delete"] = "FALSE" } };

    [Fact]
    public void Changes_of_an_operation_the_completed_task_proved_are_counted()
    {
        var (service, w) = Open();
        var airports = w.Resources[ResourceKind.Airports];
        foreach (var code in new[] { "AAAA", "BBBB", "CCCC" })
        {
            var row = Airport(code).With("ID", code.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + code[0]);
            airports.Snapshot.Add(row.Dup());
            airports.Draft.Add(row.With("Name", code + " renamed"));
        }
        var done = new ChangeItem { Resource = ResourceKind.Airports, Kind = ChangeKind.Update, State = ItemState.Succeeded };
        var job = new BatchJob { Mode = RunMode.Online, Items = [done] };
        job.SetStatus(JobStatus.Completed, Messages.Define("Text_140197D868"));
        Assert.Equal(0, service.RemainingAfter(job)); // not proved yet
        w.VerifiedOperations.Add(ApiOperationPolicy.Key(done));
        Assert.Equal(3, service.RemainingAfter(job));

        job.SetStatus(JobStatus.NeedsAttention, Messages.Define("Text_E9008C53CF"));
        Assert.Equal(0, service.RemainingAfter(job));
    }
}
