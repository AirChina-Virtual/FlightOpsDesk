using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using VamSys.Core;
using VamSys.Infrastructure;
using VamSys.TestSupport;
using static VamSys.IntegrationTests.Storage.LegacyDatabases;

namespace VamSys.IntegrationTests.Storage;

/// <summary>
/// Opening a database written by an older release: the backup is made first and is complete, the upgrade is all or nothing,
/// business content, identifiers and order survive, an older writer can no longer write, and a newer format is refused.
/// (The exact v4 content of each legacy shape is pinned by Golden/Storage/LegacyMigrationGoldenTests.)
/// </summary>
public class StorageMigrationTests
{
    static Workspace FixtureWithId(Guid id) { var w = Fixture(); w.Id = id; return w; }

    [Fact]
    public void A_v0_upgrade_backs_up_the_committed_WAL_and_keeps_business_content_identifiers_and_order()
    {
        using var dir = TempDataDirectory.Create("vamsys-mig-");
        var a = Fixture();
        var b = Fixture();
        b.Name = "Second";
        var item = a.Jobs[0].Items[0];
        item.State = ItemState.Unknown;
        item.RemoteId = "102";
        item.ProposeRecoveryId("103");
        item.Message = "历史原文";
        a.Jobs[0].Status = "旧状态";
        a.Jobs.Add(new() { Items = [new() { Message = "second item" }, new() { Message = "third item" }] });
        using var legacy = Legacy(dir.Path, a, b);
        Sql(legacy, "INSERT INTO secrets VALUES($id,$value)", ("$id", a.Id.ToString()), ("$value", new byte[] { 1, 2, 3 }));

        var store = new WorkspaceStore(dir.Path);
        Assert.NotNull(store.MigrationBackup);
        using (var backup = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = store.MigrationBackup, Pooling = false }.ToString()))
        {
            backup.Open();
            Assert.Equal("ok", Text(backup, "PRAGMA integrity_check"));
            Assert.Equal(2, Number(backup, "SELECT count(*) FROM workspaces"));
            Assert.Equal(0, Number(backup, "PRAGMA user_version"));
            var old = Workspace.Deserialize(Text(backup, "SELECT json FROM workspaces WHERE id=$id", ("$id", a.Id.ToString())));
            Assert.Equal("103", old.Jobs[0].Items[0].RecoveryCandidateId);
        }

        var loaded = store.Load(a.Id);
        Assert.Equal(a.Jobs.Select(j => j.Id), loaded.Jobs.Select(j => j.Id));
        Assert.Equal(a.Jobs[1].Items.Select(i => i.Id), loaded.Jobs[1].Items.Select(i => i.Id));
        Assert.Equal(2, loaded.Jobs[0].Items[0].RecoveryCandidates.Count);
        Assert.Equal("历史原文", loaded.Jobs[0].Items[0].Message);
        Assert.Single(loaded.Resources[ResourceKind.Fleets].Undo);
        Assert.Equal("001", loaded.Resources[ResourceKind.Fleets].Draft[0].Get("Unknown"));
        Assert.Equal(2, Workspace.Deserialize(loaded.Serialize()).Jobs.Count);
        Assert.Equal("en-US", store.LoadLanguage());

        using var c = Open(dir.Path);
        Assert.False(JsonDocument.Parse(Text(c, "SELECT json FROM workspaces LIMIT 1")).RootElement.TryGetProperty("Jobs", out _)); // jobs now live in their own tables
        Assert.Equal(3, Number(c, "SELECT length(value) FROM secrets"));
        Assert.ThrowsAny<Exception>(() => Sql(c, "INSERT INTO workspaces VALUES('old','old','{}')")); // the old three-column writer is locked out
        Assert.Null(new WorkspaceStore(dir.Path).MigrationBackup); // a second start does not migrate again
    }

    [Theory]
    [InlineData("json")]
    [InlineData("duplicate")]
    [InlineData("barrier")]
    public void A_failed_upgrade_changes_nothing_in_any_workspace_and_keeps_the_backup(string corruption)
    {
        using var dir = TempDataDirectory.Create("vamsys-mig-");
        var a = Fixture();
        var b = Fixture();
        if (corruption == "duplicate")
            b.Jobs[0].Items.Add(b.Jobs[0].Items[0]);
        using var c = Legacy(dir.Path, a, b);
        if (corruption == "json")
            Sql(c, "UPDATE workspaces SET json='broken' WHERE id=$id", ("$id", b.Id.ToString()));

        Assert.ThrowsAny<Exception>(() => new WorkspaceStore(dir.Path, point => { if (corruption == "barrier" && point == "Migration:BeforeCommit") throw new IOException("injected"); }));

        Assert.Equal(0, Number(c, "PRAGMA user_version"));
        Assert.Equal(3, Number(c, "SELECT count(*) FROM pragma_table_info('workspaces')"));
        Assert.Equal(0, Number(c, "SELECT count(*) FROM sqlite_master WHERE name='batch_items'"));
        Assert.Single(Directory.GetFiles(dir.Path, "workspaces-before-v4-*.db"));
        // once the damage is repaired the same database upgrades, with every workspace
        Sql(c, "UPDATE workspaces SET json=$json WHERE id=$id", ("$id", b.Id.ToString()), ("$json", FixtureWithId(b.Id).Serialize()));
        Assert.Single(new WorkspaceStore(dir.Path).Load(a.Id).Jobs);
    }

    [Fact]
    public void A_database_from_a_newer_release_is_refused_and_left_alone()
    {
        using var dir = TempDataDirectory.Create("vamsys-mig-");
        new WorkspaceStore(dir.Path);
        using var c = Open(dir.Path);
        Sql(c, "PRAGMA user_version=5");
        var error = Assert.ThrowsAny<Exception>(() => new WorkspaceStore(dir.Path));
        Assert.Equal("StorageNewer", MessageErrors.Describe(error).Code);
        Assert.Equal(5, Number(c, "PRAGMA user_version"));
    }

    [Fact]
    public void One_instance_may_use_a_data_directory_and_leaving_releases_it()
    {
        using var dir = TempDataDirectory.Create("vamsys-lease-");
        using var other = TempDataDirectory.Create("vamsys-lease-");
        using (var lease = new DataDirectoryLease(dir.Path))
        {
            var error = Assert.ThrowsAny<Exception>(() => { using var second = new DataDirectoryLease(dir.Path); });
            Assert.Equal("StorageInUse", MessageErrors.Describe(error).Code);
            using var separate = new DataDirectoryLease(other.Path); // another directory is independent
        }
        using var reopened = new DataDirectoryLease(dir.Path);
    }

    // ---- v2 -> current ------------------------------------------------------------------------------------------------

    [Fact]
    public void A_v2_database_keeps_its_task_revision_and_recovery_candidate_and_old_writers_are_locked_out()
    {
        using var dir = TempDataDirectory.Create("vamsys-mig-");
        var w = Fixture();
        w.Jobs[0].Items[0].State = ItemState.Unknown;
        w.Jobs[0].Items[0].ProposeRecoveryId("10");
        V2(dir.Path, w);

        var store = new WorkspaceStore(dir.Path);
        var loaded = store.Load(w.Id);
        Assert.Equal(7, loaded.StorageRevision);
        Assert.Equal("10", loaded.Jobs[0].Items[0].RecoveryCandidateId);
        Assert.Single(loaded.Resources[ResourceKind.Fleets].Undo);
        using var c = Open(dir.Path);
        Assert.ThrowsAny<SqliteException>(() => Sql(c, "UPDATE workspaces SET format_version=2,json='{}'"));
        Assert.Equal(7, store.Load(w.Id).StorageRevision);
        using var backup = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = store.MigrationBackup, Pooling = false }.ToString());
        backup.Open();
        Assert.Equal(2, Number(backup, "PRAGMA user_version"));
    }

    [Fact]
    public void A_v2_database_with_a_duplicated_local_row_ID_is_refused_and_stays_v2()
    {
        using var dir = TempDataDirectory.Create("vamsys-mig-");
        var w = Fixture();
        V2(dir.Path, w);
        using (var broken = Open(dir.Path))
        {
            var node = JsonNode.Parse(Text(broken, "SELECT json FROM workspaces"))!;
            var rows = node["Resources"]!["Fleets"]!["Draft"]!.AsArray();
            rows.Add(rows[0]!.DeepClone());
            Sql(broken, "UPDATE workspaces SET json=$j", ("$j", node.ToJsonString()));
        }
        Assert.ThrowsAny<Exception>(() => new WorkspaceStore(dir.Path));
        using var c = Open(dir.Path);
        Assert.Equal(2, Number(c, "PRAGMA user_version"));
        Assert.Equal(0, Number(c, "SELECT count(*) FROM sqlite_master WHERE name='resource_rows'"));
    }

    // ---- fixed legacy databases: history conversion -----------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Fixed_legacy_databases_keep_every_undo_and_redo_node_and_the_cursor_position(int version)
    {
        using var dir = TempDataDirectory.Create("vamsys-mig-");
        FixedLegacy(dir.Path, version);
        var store = new WorkspaceStore(dir.Path);
        var w = store.Load(FixedId);
        var d = w.Resources[ResourceKind.Fleets];
        Assert.Equal((2, 2), (d.Undo.Count, d.Redo.Count));
        Assert.Equal(version >= 2 ? 7 : 0, w.StorageRevision);
        Assert.Equal("7", w.AirlineId);
        Assert.Equal("Fleets:Create", w.VerifiedOperations.Single());
        d.UndoEdit();
        Assert.Equal("start", d.Draft[0].Get("Name"));
        d.UndoEdit();
        Assert.Equal("start", d.Draft[0].Get("Name"));
        foreach (var value in new[] { "start", "current", "future1", "future2" })
        {
            d.RedoEdit();
            Assert.Equal(value, d.Draft[0].Get("Name"));
        }
        Assert.Equal("9", d.Draft[0].Identity!.RemoteId);
        Assert.Equal("{raw}", d.Draft[0].RawApiJson);
        Assert.True(d.Draft[0].Fields.ContainsKey("empty"));

        using var c = Open(dir.Path);
        Assert.Equal(4, Number(c, "PRAGMA user_version"));
        Assert.Equal(0, Number(c, "SELECT count(*) FROM sqlite_master WHERE name='resource_history'"));
        Assert.Equal(3, Number(c, "SELECT length(value) FROM secrets"));
        Assert.Equal("en-US", store.LoadLanguage());
        using var backup = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = store.MigrationBackup, Pooling = false }.ToString());
        backup.Open();
        Assert.Equal(version, Number(backup, "PRAGMA user_version"));
        Assert.Equal("ok", Text(backup, "PRAGMA integrity_check"));
        Assert.Null(new WorkspaceStore(dir.Path).MigrationBackup);
        Assert.Single(Directory.GetFiles(dir.Path, "workspaces-before-v4-*.db"));
    }

    [Theory]
    [InlineData("json")]
    [InlineData("duplicate")]
    [InlineData("missing")]
    public void A_damaged_v3_history_is_refused_before_anything_is_changed(string invalid)
    {
        using var dir = TempDataDirectory.Create("vamsys-mig-");
        FixedLegacy(dir.Path, 3);
        using var c = Open(dir.Path);
        var original = Text(c, "SELECT json FROM workspaces");
        if (invalid == "missing")
            Sql(c, "DELETE FROM resource_history WHERE resource=1");
        else
            Sql(c, "UPDATE resource_history SET undo_json=$j WHERE resource=1", ("$j", invalid == "json" ? "broken" : "[[" + FixedRow("x") + "," + FixedRow("x") + "]]"));
        Assert.ThrowsAny<Exception>(() => new WorkspaceStore(dir.Path));
        Assert.Equal(3, Number(c, "PRAGMA user_version"));
        Assert.Equal(original, Text(c, "SELECT json FROM workspaces"));
        Assert.Equal(0, Number(c, "SELECT count(*) FROM sqlite_master WHERE name='resource_history_steps'"));
    }
}
