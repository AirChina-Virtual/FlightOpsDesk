using VamSys.Core;
using VamSys.Infrastructure;
using VamSys.TestSupport;

namespace VamSys.IntegrationTests.Storage;

// Moving the data must never lose it or leave two databases that both look current.
public class DataLocationTests
{
    static Workspace Saved(WorkspaceStore store, string name = "moved")
    {
        var workspace = new Workspace { Name = name };
        store.Save(workspace);
        return workspace;
    }

    static string Target(TempDataDirectory dir) => Path.Combine(dir.Path, "target");

    [Fact]
    public async Task A_copy_opens_with_the_same_workspaces_and_secret_and_leaves_the_source_alone()
    {
        using var dir = TempDataDirectory.Create("vamsys-location-");
        var source = Path.Combine(dir.Path, "source");
        var store = new WorkspaceStore(source);
        var workspace = Saved(store);
        store.SaveSecret(workspace.Id, "not-a-real-secret");
        Directory.CreateDirectory(Path.Combine(source, "Backups"));
        File.WriteAllText(Path.Combine(source, "Backups", "kept.db"), "backup");

        await store.CopyDataToAsync(Target(dir));

        Assert.True(File.Exists(Path.Combine(source, "workspaces.db")));
        Assert.False(File.Exists(Path.Combine(Target(dir), "workspaces.db.moving")));
        Assert.True(File.Exists(Path.Combine(Target(dir), "Backups", "kept.db")));
        var moved = new WorkspaceStore(Target(dir));
        Assert.Contains(moved.List(), w => w.Id == workspace.Id && w.Name == "moved");
        // DPAPI is bound to the user and the workspace ID, not to the path.
        Assert.Equal("not-a-real-secret", moved.LoadSecret(workspace.Id));
    }

    [Fact]
    public async Task A_copy_never_replaces_a_database_that_is_already_there()
    {
        using var dir = TempDataDirectory.Create("vamsys-location-");
        var store = new WorkspaceStore(Path.Combine(dir.Path, "source"));
        Saved(store);
        var existing = new WorkspaceStore(Target(dir));
        var kept = Saved(existing, "already there");

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CopyDataToAsync(Target(dir)));

        Assert.Contains(new WorkspaceStore(Target(dir)).List(), w => w.Id == kept.Id);
    }

    [Fact]
    public void Setting_aside_renames_the_database_so_it_cannot_be_opened_by_mistake_and_deletes_nothing()
    {
        using var dir = TempDataDirectory.Create("vamsys-location-");
        var source = Path.Combine(dir.Path, "source");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "workspaces.db"), "database");
        File.WriteAllText(Path.Combine(source, "workspaces.db-wal"), "log");

        Assert.True(WorkspaceStore.TrySetAside(source));

        Assert.False(File.Exists(Path.Combine(source, "workspaces.db")));
        Assert.Equal("database", File.ReadAllText(Assert.Single(Directory.GetFiles(source, "workspaces-moved-*.db"))));
        Assert.Equal("log", File.ReadAllText(Assert.Single(Directory.GetFiles(source, "workspaces-moved-*.db-wal"))));
    }

    [Fact]
    public void A_directory_in_use_is_not_set_aside_and_stays_pending()
    {
        using var dir = TempDataDirectory.Create("vamsys-location-");
        var source = Path.Combine(dir.Path, "source");
        var file = Path.Combine(dir.Path, DataLocationFile.FileName);
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "workspaces.db"), "database");
        using var lease = new DataDirectoryLease(source);
        var opened = Path.Combine(dir.Path, "opened");

        DataLocationStartup.FinishMoves(file, new(opened, [source]), opened);

        Assert.True(File.Exists(Path.Combine(source, "workspaces.db")));
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void Finishing_a_move_clears_it_and_never_touches_the_directory_that_was_opened()
    {
        using var dir = TempDataDirectory.Create("vamsys-location-");
        var source = Path.Combine(dir.Path, "source");
        var opened = Path.Combine(dir.Path, "opened");
        var file = Path.Combine(dir.Path, DataLocationFile.FileName);
        foreach (var directory in new[] { source, opened })
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "workspaces.db"), "database");
        }

        DataLocationStartup.FinishMoves(file, new(opened, [source, opened]), opened);

        Assert.False(File.Exists(Path.Combine(source, "workspaces.db")));
        Assert.True(File.Exists(Path.Combine(opened, "workspaces.db")));
        Assert.Empty(DataLocationFile.Read(file).Retire);
        Assert.Equal(opened, DataLocationFile.Read(file).Directory);
    }

    [Fact]
    public void A_damaged_database_is_refused_before_the_program_switches_to_it()
    {
        using var dir = TempDataDirectory.Create("vamsys-location-");
        File.WriteAllText(dir.File("workspaces.db"), "this is not a database");
        Assert.Throws<InvalidOperationException>(() => WorkspaceStore.RequireOpenable(dir.Path));
    }

    [Fact]
    public void The_location_file_round_trips_and_the_default_needs_no_file()
    {
        using var dir = TempDataDirectory.Create("vamsys-location-");
        var file = dir.File(DataLocationFile.FileName);
        Assert.Equal(DataLocationState.Default.Directory, DataLocationFile.Read(file).Directory);

        DataLocationFile.Write(file, new(@"D:\Data", [@"C:\Old"]));
        var read = DataLocationFile.Read(file);
        Assert.Equal(@"D:\Data", read.Directory);
        Assert.Equal([@"C:\Old"], read.Retire);

        DataLocationFile.Write(file, DataLocationState.Default);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void A_damaged_location_file_is_an_error_not_a_silent_default()
    {
        using var dir = TempDataDirectory.Create("vamsys-location-");
        File.WriteAllText(dir.File(DataLocationFile.FileName), "{ not json");
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => DataLocationFile.Read(dir.File(DataLocationFile.FileName)));
    }

    static DataLocationPlan Plan(string chosen, string current, bool exact = false, long bytes = 0, IReadOnlyList<string>? retiring = null, bool network = false)
        => DataLocationPlanner.Plan(chosen, current, retiring ?? [], bytes, exact, _ => network);

    [Fact]
    public void An_empty_folder_is_used_as_it_is_and_one_with_other_files_gets_a_subfolder()
    {
        using var dir = TempDataDirectory.Create("vamsys-location-");
        var current = dir.File("current");
        var empty = dir.File("empty");
        Directory.CreateDirectory(empty);
        var plan = Plan(empty, current);
        Assert.Equal((DataLocationDecision.CopyHere, empty), (plan.Decision, plan.Target));

        var full = dir.File("documents");
        Directory.CreateDirectory(full);
        File.WriteAllText(Path.Combine(full, "letter.txt"), "");
        plan = Plan(full, current);
        Assert.Equal((DataLocationDecision.CopyHere, Path.Combine(full, "FlightOpsDesk")), (plan.Decision, plan.Target));
    }

    [Fact]
    public void A_folder_with_data_is_offered_as_it_is_and_an_exact_target_stays_exact()
    {
        using var dir = TempDataDirectory.Create("vamsys-location-");
        var data = dir.File("data");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "workspaces.db"), "");
        var plan = Plan(data, dir.File("current"));
        Assert.Equal((DataLocationDecision.UseExisting, data), (plan.Decision, plan.Target));

        var other = dir.File("other");
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "letter.txt"), "");
        Assert.Equal(other, Plan(other, dir.File("current"), exact: true).Target);
    }

    [Fact]
    public void Unsafe_targets_are_refused()
    {
        using var dir = TempDataDirectory.Create("vamsys-location-");
        var current = dir.File("current");
        Directory.CreateDirectory(current);
        Assert.Equal(DataLocationDecision.Same, Plan(current + Path.DirectorySeparatorChar, current, exact: true).Decision);
        Assert.Equal(DataLocationDecision.Nested, Plan(Path.Combine(current, "inner"), current).Decision);
        Assert.Equal(DataLocationDecision.Nested, Plan(dir.Path, current, exact: true).Decision);
        Assert.Equal(DataLocationDecision.Network, Plan(dir.File("share"), current, network: true).Decision);
        Assert.Equal(DataLocationDecision.BeingRetired, Plan(dir.File("old"), current, retiring: [dir.File("old")]).Decision);
        Assert.Equal(DataLocationDecision.Unusable, Plan("relative\\path", current).Decision);
        Assert.Equal(DataLocationDecision.NotEnoughSpace, Plan(dir.File("big"), current, bytes: long.MaxValue).Decision);
    }

    [Fact]
    public void A_network_path_is_recognized_without_any_help()
    {
        using var dir = TempDataDirectory.Create("vamsys-location-");
        Assert.Equal(DataLocationDecision.Network, DataLocationPlanner.Plan(@"\\server\share\data", dir.Path, [], 0, exact: true).Decision);
    }
}
