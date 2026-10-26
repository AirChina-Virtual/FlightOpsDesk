using VamSys.Core;

namespace VamSys.UnitTests.Application;

public class MaintenanceServiceTests
{
    sealed class Fixture
    {
        public readonly FakeStorage Storage = new();
        public readonly InteractionGate Gate = new();
        public readonly WorkspaceSession Session;
        public readonly MaintenanceService Service;
        public readonly FakeOnline Online = new();
        public readonly List<Guid> Replaced = [];
        public Fixture(bool canClear = true)
        {
            Storage.Seed("live");
            Session = new(Storage, Gate, new FakeTimer());
            Session.Open("default");
            var w = Session.Workspace;
            Storage.Preview = new(w.Id, w.Name, "C:/data", 7, 1, 2, 3, 4, 5, canClear);
            Storage.Revision = 7;
            Service = new(Session, Online);
        }
    }

    [Fact]
    public async Task Preparing_saves_the_workspace_and_returns_what_would_be_cleared()
    {
        var f = new Fixture();
        f.Session.Workspace.Name = "changed";
        var preview = await f.Service.PrepareBackupAsync();
        Assert.Equal(7, preview.Revision);
        Assert.Equal("changed", f.Storage.Read(f.Session.Workspace.Id).Name);
    }

    [Fact]
    public async Task Unresolved_writes_make_clearing_unsafe()
    {
        var f = new Fixture(canClear: false);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.PrepareBackupAsync());
        Assert.Equal("MaintenanceUnsafe", MessageErrors.Describe(error).Code);
    }

    [Fact]
    public async Task A_failed_save_before_the_preview_changes_nothing()
    {
        var f = new Fixture();
        var before = f.Session.Workspace;
        f.Storage.FailSaves = true;
        await Assert.ThrowsAsync<IOException>(() => f.Service.PrepareBackupAsync());
        Assert.Same(before, f.Session.Workspace);
        Assert.DoesNotContain(f.Storage.Calls, c => c.StartsWith("backup"));
    }

    [Fact]
    public async Task A_backup_replaces_the_open_workspace_with_the_cleared_one_and_tells_the_caller()
    {
        var f = new Fixture();
        var cleared = new Workspace { Id = f.Session.Workspace.Id, Name = "cleared" };
        f.Storage.BackupResult = () => Task.FromResult(new MaintenanceOutcome(cleared, "C:/backup.db"));
        var preview = await f.Service.PrepareBackupAsync();
        var result = await f.Service.BackupAndClearAsync(preview, f.Replaced.Add);
        Assert.Equal("C:/backup.db", result.BackupDatabasePath);
        Assert.Same(cleared, f.Session.Workspace);
        Assert.Equal([cleared.Id], f.Replaced);
        Assert.Contains("backup:7", f.Storage.Calls);
    }

    [Fact]
    public async Task A_backup_that_failed_before_committing_keeps_the_live_workspace()
    {
        var f = new Fixture();
        var live = f.Session.Workspace;
        f.Storage.BackupResult = () => throw new IOException("backup failed");
        var preview = await f.Service.PrepareBackupAsync();
        await Assert.ThrowsAsync<IOException>(() => f.Service.BackupAndClearAsync(preview, f.Replaced.Add));
        Assert.Same(live, f.Session.Workspace);
        Assert.Empty(f.Replaced);
        Assert.False(f.Gate.StorageBlocked);
        // The lock was released: another save still works.
        await f.Session.SaveAsync();
    }

    [Fact]
    public async Task A_failure_after_the_commit_reloads_what_the_database_holds_offline()
    {
        var f = new Fixture();
        var preview = await f.Service.PrepareBackupAsync();
        f.Storage.BackupResult = () => throw new IOException("after commit");
        f.Storage.Revision = 8;
        f.Storage.Stored[f.Session.Workspace.Id] = new Workspace { Id = f.Session.Workspace.Id, Name = "from disk", Mode = RunMode.Online }.Serialize();
        await Assert.ThrowsAsync<IOException>(() => f.Service.BackupAndClearAsync(preview, f.Replaced.Add));
        Assert.Equal("from disk", f.Session.Workspace.Name);
        Assert.Equal(RunMode.Offline, f.Session.Workspace.Mode);
        Assert.Equal([f.Session.Workspace.Id], f.Replaced);
    }

    [Fact]
    public async Task When_even_the_reload_fails_saving_stops_until_restart()
    {
        var f = new Fixture();
        var preview = await f.Service.PrepareBackupAsync();
        f.Storage.BackupResult = () => throw new IOException("after commit");
        f.Storage.Revision = 8;
        f.Storage.FailLoad = () => new IOException("unreadable");
        await Assert.ThrowsAsync<IOException>(() => f.Service.BackupAndClearAsync(preview, f.Replaced.Add));
        Assert.True(f.Gate.StorageBlocked);
    }

    static AirlineParameter Parameter(string id, bool approved = true, bool restricted = false) => new(id, "7", "ICAO" + id, "IA" + id, approved, restricted, []);

    [Fact]
    public async Task Loading_the_parameters_needs_an_online_workspace_and_reports_how_many_there_are()
    {
        var f = new Fixture();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.LoadParametersAsync());
        Assert.Equal("ApiConnectFirst", MessageErrors.Describe(error).Code);
        f.Session.Workspace.Mode = RunMode.Online;
        f.Online.For(f.Session.Workspace).Parameters = [Parameter("1"), Parameter("2")];
        Assert.Equal(2, await f.Service.LoadParametersAsync());
        Assert.Contains("parameters:True", f.Online.For(f.Session.Workspace).Calls);
    }

    [Fact]
    public async Task Only_approved_unrestricted_parameters_can_be_chosen_for_a_new_row_while_online()
    {
        var f = new Fixture();
        f.Session.Workspace.Mode = RunMode.Online;
        f.Online.For(f.Session.Workspace).Parameters = [Parameter("1"), Parameter("2", approved: false), Parameter("3", restricted: true), Parameter("4")];
        var row = new DataRow { Fields = new Dictionary<string, string> { ["Name"] = "new" } };
        var choices = await f.Service.ChoosableParametersAsync(row);
        Assert.Equal(["1", "4"], choices.Select(p => p.Id));

        f.Online.For(f.Session.Workspace).Parameters = [Parameter("2", approved: false)];
        Assert.Equal("ApiParameterRequired", MessageErrors.Describe(await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.ChoosableParametersAsync(row))).Code);

        f.Session.Workspace.Mode = RunMode.Offline;
        Assert.Equal("ApiConnectFirst", MessageErrors.Describe(await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.ChoosableParametersAsync(row))).Code);
    }

    [Fact]
    public async Task A_parameter_cannot_be_chosen_for_a_record_that_already_exists_on_the_server()
    {
        var f = new Fixture();
        f.Session.Workspace.Mode = RunMode.Online;
        var existing = new DataRow { Fields = new Dictionary<string, string> { ["Name"] = "old" }, Identity = new ResourceIdentity("1", "7") };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.ChoosableParametersAsync(existing));
        Assert.Equal("ApiParameterCreateOnly", MessageErrors.Describe(error).Code);
    }

    [Fact]
    public async Task Assigning_a_parameter_replaces_the_row_and_saves_and_the_same_value_changes_nothing()
    {
        var f = new Fixture();
        var row = new DataRow { Fields = new Dictionary<string, string> { ["Name"] = "fleet", ["Parameter IDs"] = "" } };
        var data = f.Session.Workspace.Resources[ResourceKind.Fleets];
        data.Draft.Add(row);
        Assert.True(await f.Service.AssignParameterAsync(ResourceKind.Fleets, row, "5"));
        Assert.Equal("5", data.FindDraftRow(row.LocalId)!.Get("Parameter IDs"));
        Assert.Equal(1, data.Undo.Count);
        Assert.Equal("journal:All", f.Storage.Calls.Last());
        var calls = f.Storage.Calls.Count;
        Assert.False(await f.Service.AssignParameterAsync(ResourceKind.Fleets, data.FindDraftRow(row.LocalId)!, "5"));
        Assert.Equal(calls, f.Storage.Calls.Count);
    }

    [Fact]
    public async Task A_failed_save_after_assigning_leaves_the_database_without_the_parameter()
    {
        var f = new Fixture();
        var row = new DataRow { Fields = new Dictionary<string, string> { ["Name"] = "fleet", ["Parameter IDs"] = "" } };
        f.Session.Workspace.Resources[ResourceKind.Fleets].Draft.Add(row);
        f.Storage.Stored[f.Session.Workspace.Id] = f.Session.Workspace.Serialize();
        f.Storage.FailSaves = true;
        await Assert.ThrowsAsync<IOException>(() => f.Service.AssignParameterAsync(ResourceKind.Fleets, row, "5"));
        Assert.Equal("", f.Storage.Read(f.Session.Workspace.Id).Resources[ResourceKind.Fleets].Draft[0].Get("Parameter IDs"));
    }
}