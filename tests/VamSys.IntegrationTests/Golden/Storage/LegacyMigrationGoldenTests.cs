using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using VamSys.Core;
using VamSys.Infrastructure;
using VamSys.TestSupport;

namespace VamSys.IntegrationTests.Golden.Storage;

/// <summary>
/// I08: databases written by older releases, migrated by the current WorkspaceStore, dumped and compared with
/// Expected/legacy/*.json. The legacy fixtures are built from fixed DDL and old-format JSON (same shapes as the old suite's
/// StorageScenarios.Legacy, StorageScenarios.FixedLegacy and StorageV3Scenarios.V2), never by the v4 serializer.
/// </summary>
public class LegacyMigrationGoldenTests
{
    static readonly Guid FixedId = Guid.Parse("c02a7c69-04d6-4ab8-8fb9-dc06e91a47ce");

    [Fact]
    public void Legacy_databases_migrate_to_the_same_v4_content()
    {
        var golden = new GoldenSession("Storage", "legacy");
        var dump = new SqliteDump(new Masker());
        var cases = new (string Name, int Version, Action<string> Create)[]
        {
            ("v0-unversioned-fixed", 0, dir => FixedLegacy(dir, 0)),
            ("v0-unversioned-with-jobs", 0, dir => { using var c = Legacy(dir, JobsFixture()); }),
            ("v2-fixed", 2, dir => FixedLegacy(dir, 2)),
            ("v2-with-jobs", 2, dir => V2(dir, JobsFixture())),
            ("v3-fixed", 3, dir => FixedLegacy(dir, 3)),
        };
        foreach (var (name, version, create) in cases)
        {
            using var dir = TempDataDirectory.Create("vamsys-legacy-");
            create(dir.Path);
            var store = new WorkspaceStore(dir.Path);
            Xunit.Assert.NotNull(store.MigrationBackup);
            using (var backup = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = store.MigrationBackup, Pooling = false }.ToString()))
            {
                backup.Open();
                Xunit.Assert.Equal((long)version, Scalar(backup, "PRAGMA user_version"));
                Xunit.Assert.Equal("ok", Scalar(backup, "PRAGMA integrity_check"));
            }
            var id = store.List().Single().Id;
            Xunit.Assert.NotEmpty(store.Load(id).Name);
            golden.CheckText($"{name}.json", dump.Dump(dir.File("workspaces.db"), maskBlobs: false));
            Xunit.Assert.Null(new WorkspaceStore(dir.Path).MigrationBackup); // a second start does not migrate again
        }
        golden.Finish();
    }

    static object? Scalar(SqliteConnection c, string sql) { using var cmd = c.CreateCommand(); cmd.CommandText = sql; return cmd.ExecuteScalar(); }
    static void Sql(SqliteConnection c, string sql, params (string, object)[] values)
    { using var cmd = c.CreateCommand(); cmd.CommandText = sql; foreach (var (k, v) in values) cmd.Parameters.AddWithValue(k, v); cmd.ExecuteNonQuery(); }
    static SqliteConnection Open(string dir)
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(dir, "workspaces.db"), Pooling = false }.ToString());
        c.Open();
        return c;
    }

    // ---- fixtures ----------------------------------------------------------------------------------------------------

    static Workspace JobsFixture()
    {
        var w = new Workspace { Name = "Legacy fixture", AirlineId = "7", Mode = RunMode.Online };
        var data = w.Resources[ResourceKind.Fleets];
        data.Draft.Add(new DataRow { Fields = new() { ["Name"] = "New", ["Type Code"] = "B738", ["Type (pax/cargo/...)"] = "pax", ["Max Passengers"] = "180", ["Unknown"] = "001" } });
        data.Edit([], () => { });
        w.Jobs.Add(new() { Mode = RunMode.Online, Items = new ChangePlanner().Plan(ResourceKind.Fleets, data) });
        var item = w.Jobs[0].Items[0];
        item.State = ItemState.Unknown; item.RemoteId = "102"; item.ProposeRecoveryId("103"); item.Message = "历史原文";
        w.Jobs[0].Status = "旧状态";
        w.Jobs.Add(new() { Items = [new() { Message = "second item" }, new() { Message = "third item" }] });
        return w;
    }

    /// <summary>Pre-versioning database: no user_version, workspace JSON holds jobs and full undo/redo snapshots.</summary>
    static SqliteConnection Legacy(string dir, Workspace w)
    {
        var c = Open(dir);
        Sql(c, "PRAGMA journal_mode=WAL; CREATE TABLE workspaces(id TEXT PRIMARY KEY,name TEXT NOT NULL,json TEXT NOT NULL); CREATE TABLE secrets(id TEXT PRIMARY KEY,value BLOB NOT NULL); CREATE TABLE app_settings(key TEXT PRIMARY KEY,value TEXT NOT NULL);");
        var node = JsonNode.Parse(LegacyJson(w))!;
        node.AsObject().Remove("StorageRevision");
        foreach (var j in node["Jobs"]!.AsArray()) foreach (var i in j!["Items"]!.AsArray()) i!.AsObject().Remove("ApiSteps");
        Sql(c, "INSERT INTO workspaces VALUES($id,$name,$json)", ("$id", w.Id.ToString()), ("$name", w.Name), ("$json", node.ToJsonString()));
        Sql(c, "INSERT INTO secrets VALUES($id,$value)", ("$id", w.Id.ToString()), ("$value", new byte[] { 1, 2, 3 }));
        Sql(c, "INSERT INTO app_settings VALUES('language','en-US')");
        return c;
    }

    static void V2(string dir, Workspace w)
    {
        using var c = Legacy(dir, w);
        Sql(c, "ALTER TABLE workspaces ADD COLUMN format_version INTEGER DEFAULT 2; CREATE TABLE workspace_revisions(workspace_id TEXT PRIMARY KEY,revision INTEGER); CREATE TABLE batch_jobs(workspace_id TEXT,id TEXT,ordinal INTEGER,json TEXT,PRIMARY KEY(workspace_id,id)); CREATE TABLE batch_items(workspace_id TEXT,job_id TEXT,id TEXT,ordinal INTEGER,json TEXT,PRIMARY KEY(workspace_id,job_id,id)); PRAGMA user_version=2;");
        var root = JsonNode.Parse(LegacyJson(w))!; root.AsObject().Remove("Jobs");
        Sql(c, "UPDATE workspaces SET json=$j", ("$j", root.ToJsonString()));
        Sql(c, "INSERT INTO workspace_revisions VALUES($w,7)", ("$w", w.Id.ToString()));
        foreach (var (j, n) in w.Jobs.Select((j, n) => (j, n)))
        {
            var header = JsonNode.Parse(JsonSerializer.Serialize(j))!; header.AsObject().Remove("Items");
            Sql(c, "INSERT INTO batch_jobs VALUES($w,$id,$n,$j)", ("$w", w.Id.ToString()), ("$id", j.Id.ToString()), ("$n", n), ("$j", header.ToJsonString()));
            foreach (var (i, k) in j.Items.Select((i, k) => (i, k)))
                Sql(c, "INSERT INTO batch_items VALUES($w,$job,$id,$n,$j)", ("$w", w.Id.ToString()), ("$job", j.Id.ToString()), ("$id", i.Id.ToString()), ("$n", k), ("$j", JsonSerializer.Serialize(i)));
        }
    }

    static List<DataRow> Clone(IEnumerable<DataRow> rows) => rows.Select(r => r.Copy()).ToList();
    static string LegacyJson(Workspace w)
    {
        var root = JsonNode.Parse(w.Serialize())!;
        foreach (var (kind, data) in w.Resources)
        {
            var undo = new List<List<DataRow>>(); var redo = new List<List<DataRow>>();
            var probe = data.ShallowCopy(); while (probe.Undo.Count > 0) { probe.UndoEdit(); undo.Insert(0, Clone(probe.Draft)); }
            probe = data.ShallowCopy(); while (probe.Redo.Count > 0) { probe.RedoEdit(); redo.Insert(0, Clone(probe.Draft)); }
            var node = root["Resources"]![kind.ToString()]!.AsObject(); node.Remove("History");
            node["Undo"] = JsonSerializer.SerializeToNode(undo); node["Redo"] = JsonSerializer.SerializeToNode(redo);
        }
        return root.ToJsonString();
    }

    static string FixedRow(string value) => "{\"LocalId\":\"11111111-1111-1111-1111-111111111111\",\"Identity\":{\"RemoteId\":\"9\",\"ConnectionId\":\"7\",\"ParentFleetId\":null},\"RawApiJson\":\"{raw}\",\"Fields\":{\"Name\":\"" + value + "\",\"empty\":\"\"}}";

    /// <summary>Fixed old DDL and old full-snapshot JSON for the given user_version (0, 2 or 3).</summary>
    static void FixedLegacy(string dir, int version)
    {
        using var c = Open(dir);
        Sql(c, "PRAGMA journal_mode=WAL;CREATE TABLE workspaces(id TEXT PRIMARY KEY,name TEXT NOT NULL,json TEXT NOT NULL" + (version >= 2 ? ",format_version INTEGER NOT NULL" : "") + ");CREATE TABLE secrets(id TEXT PRIMARY KEY,value BLOB NOT NULL);CREATE TABLE app_settings(key TEXT PRIMARY KEY,value TEXT NOT NULL);");
        var resources = new JsonObject();
        foreach (var kind in Enum.GetValues<ResourceKind>())
            resources[kind.ToString()] = JsonNode.Parse(kind == ResourceKind.Fleets
                ? "{\"Snapshot\":[],\"Draft\":[" + FixedRow("current") + "],\"Columns\":[\"Name\"],\"SnapshotAt\":null,\"Conflicts\":[],\"Undo\":[[" + FixedRow("start") + "],[" + FixedRow("start") + "]],\"Redo\":[[" + FixedRow("future2") + "],[" + FixedRow("future1") + "]]}"
                : "{\"Snapshot\":[],\"Draft\":[],\"Columns\":[],\"SnapshotAt\":null,\"Conflicts\":[],\"Undo\":[],\"Redo\":[]}");
        var root = new JsonObject { ["Id"] = FixedId.ToString(), ["Name"] = "fixed legacy", ["Mode"] = 0, ["AirlineId"] = "7", ["VerifiedOperations"] = new JsonArray("Fleets:Create"), ["Resources"] = resources.DeepClone(), ["Jobs"] = new JsonArray() };
        if (version >= 2)
        {
            root.Remove("Jobs");
            Sql(c, "CREATE TABLE workspace_revisions(workspace_id TEXT PRIMARY KEY,revision INTEGER NOT NULL);CREATE TABLE batch_jobs(workspace_id TEXT,id TEXT,ordinal INTEGER,json TEXT,PRIMARY KEY(workspace_id,id));CREATE TABLE batch_items(workspace_id TEXT,job_id TEXT,id TEXT,ordinal INTEGER,json TEXT,PRIMARY KEY(workspace_id,job_id,id));");
            Sql(c, "INSERT INTO workspace_revisions VALUES($id,7)", ("$id", FixedId.ToString()));
        }
        if (version == 3)
        {
            root.Remove("Resources");
            Sql(c, "CREATE TABLE resource_metadata(workspace_id TEXT,resource INTEGER,columns_json TEXT,conflicts_json TEXT,snapshot_at TEXT,PRIMARY KEY(workspace_id,resource));CREATE TABLE resource_rows(workspace_id TEXT,resource INTEGER,collection INTEGER,local_id TEXT,ordinal INTEGER,json TEXT,PRIMARY KEY(workspace_id,resource,collection,local_id));CREATE UNIQUE INDEX resource_row_order ON resource_rows(workspace_id,resource,collection,ordinal);CREATE TABLE resource_history(workspace_id TEXT,resource INTEGER,undo_json TEXT,redo_json TEXT,PRIMARY KEY(workspace_id,resource));");
            foreach (var kind in Enum.GetValues<ResourceKind>())
            {
                var data = resources[kind.ToString()]!;
                Sql(c, "INSERT INTO resource_metadata VALUES($w,$r,$cols,'[]',NULL)", ("$w", FixedId.ToString()), ("$r", (int)kind), ("$cols", data["Columns"]!.ToJsonString()));
                Sql(c, "INSERT INTO resource_history VALUES($w,$r,$u,$d)", ("$w", FixedId.ToString()), ("$r", (int)kind), ("$u", data["Undo"]!.ToJsonString()), ("$d", data["Redo"]!.ToJsonString()));
            }
            Sql(c, "INSERT INTO resource_rows VALUES($w,1,1,'11111111-1111-1111-1111-111111111111',0,$j)", ("$w", FixedId.ToString()), ("$j", FixedRow("current")));
        }
        Sql(c, "INSERT INTO workspaces VALUES($id,'fixed legacy',$j" + (version >= 2 ? "," + version : "") + ")", ("$id", FixedId.ToString()), ("$j", root.ToJsonString()));
        Sql(c, "INSERT INTO secrets VALUES($id,X'010203');INSERT INTO app_settings VALUES('language','en-US');PRAGMA user_version=" + version, ("$id", FixedId.ToString()));
        if (version >= 2)
            Sql(c, $"CREATE TRIGGER workspace_format_update BEFORE UPDATE ON workspaces WHEN NEW.format_version!={version} BEGIN SELECT RAISE(ABORT,'old format');END;CREATE TRIGGER workspace_format_insert BEFORE INSERT ON workspaces WHEN NEW.format_version!={version} BEGIN SELECT RAISE(ABORT,'old format');END;");
    }
}
