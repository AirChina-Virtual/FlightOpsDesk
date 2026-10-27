using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using VamSys.Core;
using VamSys.Infrastructure;
using VamSys.TestSupport;

namespace VamSys.IntegrationTests.Api;

/// <summary>
/// Backup-and-clear through the real store: what it keeps (protected secret, every other workspace byte for byte), and every
/// situation in which it must refuse or fail without touching the stored workspace.
/// </summary>
public class MaintenanceBackupTests
{
    static Workspace Fixture(string name = "Maintenance fixture")
    {
        var workspace = new Workspace { Name = name, AirlineId = "7", Mode = RunMode.Online };
        workspace.Resources[ResourceKind.Fleets].Draft.Add(new DataRow { Fields = new Dictionary<string, string>
            { ["Name"] = "New", ["Type Code"] = "B738", ["Type (pax/cargo/...)"] = "pax", ["Max Passengers"] = "180" } });
        return workspace;
    }

    static string Code(Exception error) => MessageErrors.Describe(error).Code ?? error.Message;

    static async Task<string> RefusedWith(WorkspaceStore store, Workspace workspace, long revision)
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => store.BackupAndClearAsync(workspace.Id, revision, TestContext.Current.CancellationToken));
        return Code(error);
    }

    [Fact]
    public async Task Clearing_one_workspace_backs_up_its_database_and_keeps_its_secret_and_every_other_workspace()
    {
        using var dir = TempDataDirectory.Create("vamsys-maint-");
        var store = new WorkspaceStore(dir.Path);
        var a = Fixture("A");
        var b = Fixture("B");
        store.Save(a);
        store.Save(b);
        store.SaveSecret(a.Id, "fixture-protected-secret");
        var otherBefore = store.Load(b.Id).Serialize();

        var preview = store.PreviewMaintenance(a.Id);
        Assert.True(preview.CanClear);
        Assert.Equal(1, preview.Drafts);
        var result = await store.BackupAndClearAsync(a.Id, preview.Revision, TestContext.Current.CancellationToken);

        Assert.All(result.Workspace.Resources.Values, data => { Assert.Empty(data.Draft); Assert.Empty(data.Snapshot); });
        Assert.Equal(RunMode.Offline, result.Workspace.Mode);
        Assert.Equal("fixture-protected-secret", store.LoadSecret(a.Id));
        Assert.Equal(otherBefore, store.Load(b.Id).Serialize());
        Assert.Single(a.Resources[ResourceKind.Fleets].Draft);

        Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(result.Backup.DatabasePath))), result.Backup.Sha256);
        Assert.True(File.Exists(result.Backup.ManifestPath));
        using var backup = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = result.Backup.DatabasePath, Pooling = false }.ToString());
        backup.Open();
        Assert.Equal("ok", Scalar(backup, "PRAGMA integrity_check"));
        // the backup still holds the rows that the live database just lost
        Assert.True(Convert.ToInt32(Scalar(backup, "SELECT count(*) FROM resource_rows WHERE workspace_id=$id", ("$id", a.Id.ToString()))) > 0);
    }

    static object? Scalar(SqliteConnection connection, string sql, params (string Name, object Value)[] values)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in values)
            command.Parameters.AddWithValue(name, value);
        return command.ExecuteScalar();
    }

    [Theory]
    [InlineData("Maintenance:BeforeBackup")]
    [InlineData("Maintenance:AfterBackup")]
    [InlineData("Maintenance:BeforeCommit")]
    public async Task A_failure_before_the_commit_leaves_the_stored_workspace_untouched(string point)
    {
        using var dir = TempDataDirectory.Create("vamsys-maint-");
        var store = new WorkspaceStore(dir.Path);
        var workspace = Fixture();
        store.Save(workspace);
        var before = store.Load(workspace.Id).Serialize();
        var reached = false;
        store.Barrier = p => { if (p == point) { reached = true; throw new IOException("injected maintenance failure"); } };

        await Assert.ThrowsAsync<IOException>(() => store.BackupAndClearAsync(workspace.Id, workspace.StorageRevision, TestContext.Current.CancellationToken));

        Assert.True(reached);
        Assert.Equal(before, store.Load(workspace.Id).Serialize());
    }

    [Fact]
    public async Task A_stale_revision_is_refused()
    {
        using var dir = TempDataDirectory.Create("vamsys-maint-");
        var store = new WorkspaceStore(dir.Path);
        var workspace = Fixture();
        store.Save(workspace);

        Assert.Equal("StorageStale", await RefusedWith(store, workspace, workspace.StorageRevision - 1));
        Assert.Single(store.Load(workspace.Id).Resources[ResourceKind.Fleets].Draft);
    }

    public static TheoryData<string> UnsafeJobs() => new() { "running", "unknown", "created but failed", "demo step in flight" };

    [Theory]
    [MemberData(nameof(UnsafeJobs))]
    public async Task Unresolved_writes_make_clearing_unsafe(string situation)
    {
        using var dir = TempDataDirectory.Create("vamsys-maint-");
        var store = new WorkspaceStore(dir.Path);
        var workspace = Fixture();
        workspace.Jobs = [situation switch
        {
            "running" => new BatchJob { Mode = RunMode.Online, Items = [new() { State = ItemState.Running }] },
            "unknown" => new BatchJob { Mode = RunMode.Online, Items = [new() { State = ItemState.Unknown }] },
            "created but failed" => new BatchJob { Mode = RunMode.Online, Items = [new() { State = ItemState.Failed, CreateCompleted = true }] },
            _ => new BatchJob { Mode = RunMode.Demo, Items = [new() { State = ItemState.Pending, ApiSteps = [new() { State = BatchStepState.InFlight }] }] }
        }];
        store.Save(workspace);

        Assert.False(store.PreviewMaintenance(workspace.Id).CanClear);
        Assert.Equal("MaintenanceUnsafe", await RefusedWith(store, workspace, workspace.StorageRevision));
        Assert.Single(store.Load(workspace.Id).Resources[ResourceKind.Fleets].Draft);
    }

    [Fact]
    public async Task An_edit_saved_by_another_store_while_the_backup_runs_is_not_cleared()
    {
        using var dir = TempDataDirectory.Create("vamsys-maint-");
        var store = new WorkspaceStore(dir.Path);
        var workspace = Fixture();
        store.Save(workspace);
        var revision = workspace.StorageRevision;
        var edited = false;
        store.Barrier = point =>
        {
            if (point != "Maintenance:AfterBackup")
                return;
            var other = new WorkspaceStore(store.DataDirectory);
            var copy = other.Load(workspace.Id);
            copy.Name = "Concurrent edit";
            other.Save(copy);
            edited = true;
        };

        Assert.Equal("StorageStale", await RefusedWith(store, workspace, revision));

        Assert.True(edited);
        var kept = store.Load(workspace.Id);
        Assert.Equal("Concurrent edit", kept.Name);
        Assert.Single(kept.Resources[ResourceKind.Fleets].Draft);
    }
}
