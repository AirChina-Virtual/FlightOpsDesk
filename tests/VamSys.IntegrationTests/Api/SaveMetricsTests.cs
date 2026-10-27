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
}
