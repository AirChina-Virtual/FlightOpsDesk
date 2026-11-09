using VamSys.Core;
using VamSys.Infrastructure;
using VamSys.TestSupport;

namespace VamSys.IntegrationTests.Api;

/// <summary>The performance report of a run counts every save, failed ones too, and the bytes written.</summary>
public class SaveMetricsTests
{
    [Fact]
    public void A_failed_save_is_counted_as_an_attempt_and_a_failure_and_the_report_can_still_be_written()
    {
        using var dir = TempDataDirectory.Create();
        var store = new WorkspaceStore(dir.Path);
        var workspace = new Workspace();
        using var run = PerformanceRun.Begin(workspace.Id, Guid.NewGuid());
        store.Save(workspace);

        using (var db = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = Path.Combine(dir.Path, "workspaces.db") }.ToString()))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "CREATE TRIGGER fail_metrics BEFORE UPDATE ON workspaces BEGIN SELECT RAISE(ABORT,'injected'); END;";
            cmd.ExecuteNonQuery();
        }
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => store.Save(workspace));

        Assert.Equal((2, 1, 1), (run.SaveAttempts, run.SaveSuccesses, run.SaveFailures));
        Assert.True(run.JsonBytes > 0);
        Assert.Equal(2, run.Checkpoints["Full"]);
        run.Finish("Failed");
        Assert.True(run.TryWrite(Path.Combine(dir.Path, "reports")));
        Assert.Equal(2, run.SaveAttempts);
    }

    [Fact]
    public void The_report_counts_how_each_item_ended_by_resource_kind_state_and_reason_without_values()
    {
        using var dir = TempDataDirectory.Create();
        using var run = PerformanceRun.Begin(Guid.NewGuid(), Guid.NewGuid());
        static ChangeItem Item(ResourceKind resource, ChangeKind kind, ItemState state, string? code)
        {
            var item = new ChangeItem { Resource = resource, Kind = kind, State = state, RemoteId = "13498011" };
            if (code != null)
                item.SetMessage(Messages.Define(code));
            return item;
        }
        run.RecordItems([
            Item(ResourceKind.Routes, ChangeKind.Create, ItemState.Succeeded, null),
            Item(ResourceKind.Routes, ChangeKind.Create, ItemState.Unknown, "ApiReadbackMismatch"),
            Item(ResourceKind.Routes, ChangeKind.Create, ItemState.Unknown, "ApiReadbackMismatch"),
            Item(ResourceKind.Airports, ChangeKind.Delete, ItemState.Unknown, "ApiSoftDeleteEvidence"),
        ]);

        Assert.Equal(new Dictionary<string, long>
        {
            ["Airports/Delete/Unknown/ApiSoftDeleteEvidence"] = 1,
            ["Routes/Create/Succeeded/-"] = 1,
            ["Routes/Create/Unknown/ApiReadbackMismatch"] = 2,
        }, run.ItemResults);
        run.Finish("CompletedWithAttention");
        Assert.True(run.TryWrite(dir.Path));
        var json = File.ReadAllText(Directory.GetFiles(dir.Path, "*.json", SearchOption.AllDirectories).Single());
        Assert.Contains("\"Routes/Create/Unknown/ApiReadbackMismatch\": 2", json, StringComparison.Ordinal);
        Assert.DoesNotContain("13498011", json, StringComparison.Ordinal);
    }
}
