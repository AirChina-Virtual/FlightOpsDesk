using VamSys.Core;
using VamSys.Infrastructure;
using VamSys.TestSupport;

namespace VamSys.IntegrationTests.Storage;

/// <summary>
/// A workspace read tells its observer each step and how many rows it has read (the window shows that as progress), on
/// the thread that reads, and an observer changes nothing in what is read. (2026-11-23, stage 7 item 3.)
/// </summary>
public class WorkspaceLoadObserverTests
{
    sealed class Recorder : IWorkspaceLoadObserver
    {
        public readonly List<string> Calls = [];
        public readonly HashSet<int> Threads = [];
        public void Stage(WorkspaceLoadStage stage) { Calls.Add(stage.ToString()); Threads.Add(Environment.CurrentManagedThreadId); }
        public void Rows(int read, int total) { Calls.Add($"{read}/{total}"); Threads.Add(Environment.CurrentManagedThreadId); }
    }

    static Workspace Seeded()
    {
        var w = new Workspace { Name = "observed" };
        var routes = w.Resources[ResourceKind.Routes];
        for (var n = 0; n < 2500; n++)
        {
            var row = new DataRow { Fields = new Dictionary<string, string> { ["ID"] = n.ToString(), ["Flight Number"] = "AC" + n } };
            routes.Snapshot.Add(row);
            routes.Draft.Add(row);
        }
        var first = routes.Draft[0];
        routes.Edit([first.LocalId], () => routes.ReplaceDraftRows([first.With("Flight Number", "EDITED")]));
        return w;
    }

    [Fact]
    public async Task A_read_reports_every_step_and_its_rows_on_its_own_thread_and_reads_the_same_workspace()
    {
        using var directory = TempDataDirectory.Create();
        var store = new WorkspaceStore(directory.Path);
        var saved = Seeded();
        store.Save(saved);
        var plain = store.Load(saved.Id);
        var recorder = new Recorder();
        var observed = await Task.Run(() => store.Load(saved.Id, recorder), TestContext.Current.CancellationToken);

        Assert.Equal(plain.Serialize(), observed.Serialize());
        Assert.Single(recorder.Threads);
        Assert.Equal(["Open", "Header", "Metadata", "Rows", "2000/5000", "4000/5000", "5000/5000", "Share"], recorder.Calls.Take(8));
        var perResource = Enum.GetValues<ResourceKind>().SelectMany(_ => new[] { "History", "Validate", "Index" });
        Assert.Equal([.. perResource, "Journal"], recorder.Calls.Skip(8));
        // The baseline was recorded after the observer's steps exactly as without one: a save right away writes nothing.
        Assert.True(observed.Journal().Plan(observed, RowSerializer.HeaderKey(observed), observed.StorageRevision, JournalScope.All)!.IsEmpty);
    }
}
