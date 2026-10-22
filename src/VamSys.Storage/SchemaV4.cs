using Microsoft.Data.Sqlite;
using static VamSys.Infrastructure.Sql;

namespace VamSys.Infrastructure;

// One schema object exactly as sqlite_master stores it (Type is sqlite_master.type). Idempotent objects are created with IF NOT EXISTS during an
// upgrade (SQLite does not keep that clause in sqlite_master.sql).
internal sealed record SchemaObject(string Name, string Type, string Sql, bool Idempotent = false)
{
    public string CreateSql => !Idempotent ? Sql : Type == "index"
        ? Sql.Replace("CREATE UNIQUE INDEX ", "CREATE UNIQUE INDEX IF NOT EXISTS ", StringComparison.Ordinal)
        : Sql.Replace("CREATE TABLE ", "CREATE TABLE IF NOT EXISTS ", StringComparison.Ordinal);
}

// Every CREATE statement of the v4 database file, in creation order. Nothing else in the code base spells out schema SQL.
internal static class SchemaV4
{
    public const int Version = 4;
    public const string InsertTrigger = "workspace_format_insert";
    public const string UpdateTrigger = "workspace_format_update";

    public static readonly SchemaObject Workspaces = new("workspaces", "table",
        "CREATE TABLE workspaces(id TEXT PRIMARY KEY,name TEXT NOT NULL,json TEXT NOT NULL,format_version INTEGER NOT NULL)");

    // Tables that already existed in v2/v3 databases.
    static readonly SchemaObject[] core =
    [
        new("secrets", "table", "CREATE TABLE secrets(id TEXT PRIMARY KEY,value BLOB NOT NULL)", true),
        new("app_settings", "table", "CREATE TABLE app_settings(key TEXT PRIMARY KEY,value TEXT NOT NULL)", true),
        new("workspace_revisions", "table", "CREATE TABLE workspace_revisions(workspace_id TEXT PRIMARY KEY REFERENCES workspaces(id) ON DELETE CASCADE,revision INTEGER NOT NULL)", true),
        new("batch_jobs", "table", "CREATE TABLE batch_jobs(workspace_id TEXT NOT NULL REFERENCES workspaces(id) ON DELETE CASCADE,id TEXT NOT NULL,ordinal INTEGER NOT NULL,json TEXT NOT NULL,PRIMARY KEY(workspace_id,id))", true),
        new("batch_items", "table", "CREATE TABLE batch_items(workspace_id TEXT NOT NULL,job_id TEXT NOT NULL,id TEXT NOT NULL,ordinal INTEGER NOT NULL,json TEXT NOT NULL,PRIMARY KEY(workspace_id,job_id,id),FOREIGN KEY(workspace_id,job_id) REFERENCES batch_jobs(workspace_id,id) ON DELETE CASCADE)", true),
    ];

    // Per-resource tables; the two history tables are new in v4 and must not exist before an upgrade.
    static readonly SchemaObject[] resources =
    [
        new("resource_metadata", "table", "CREATE TABLE resource_metadata(workspace_id TEXT NOT NULL REFERENCES workspaces(id) ON DELETE CASCADE,resource INTEGER NOT NULL,columns_json TEXT NOT NULL,conflicts_json TEXT NOT NULL,snapshot_at TEXT,PRIMARY KEY(workspace_id,resource))", true),
        new("resource_rows", "table", "CREATE TABLE resource_rows(workspace_id TEXT NOT NULL,resource INTEGER NOT NULL,collection INTEGER NOT NULL,local_id TEXT NOT NULL,ordinal INTEGER NOT NULL,json TEXT NOT NULL,PRIMARY KEY(workspace_id,resource,collection,local_id),FOREIGN KEY(workspace_id,resource) REFERENCES resource_metadata(workspace_id,resource) ON DELETE CASCADE)", true),
        new("resource_row_order", "index", "CREATE UNIQUE INDEX resource_row_order ON resource_rows(workspace_id,resource,collection,ordinal)", true),
        new("resource_history_state", "table", "CREATE TABLE resource_history_state(workspace_id TEXT NOT NULL,resource INTEGER NOT NULL,undo_json TEXT NOT NULL,redo_json TEXT NOT NULL,PRIMARY KEY(workspace_id,resource),FOREIGN KEY(workspace_id,resource) REFERENCES resource_metadata(workspace_id,resource) ON DELETE CASCADE)"),
        new("resource_history_steps", "table", "CREATE TABLE resource_history_steps(workspace_id TEXT NOT NULL,resource INTEGER NOT NULL,step_id TEXT NOT NULL,json TEXT NOT NULL,PRIMARY KEY(workspace_id,resource,step_id),FOREIGN KEY(workspace_id,resource) REFERENCES resource_metadata(workspace_id,resource) ON DELETE CASCADE)"),
    ];

    // Old programs cannot write another format_version into a v4 database.
    static readonly SchemaObject[] triggers =
    [
        new(InsertTrigger, "trigger", "CREATE TRIGGER workspace_format_insert BEFORE INSERT ON workspaces WHEN NEW.format_version!=4 BEGIN SELECT RAISE(ABORT,'unsupported workspace format'); END"),
        new(UpdateTrigger, "trigger", "CREATE TRIGGER workspace_format_update BEFORE UPDATE ON workspaces WHEN NEW.format_version!=4 BEGIN SELECT RAISE(ABORT,'unsupported workspace format'); END"),
    ];

    // Everything a freshly created database contains, in creation order.
    public static IReadOnlyList<SchemaObject> All { get; } = [Workspaces, .. core, .. resources, .. triggers];

    public static void CreateWorkspacesTable(SqliteConnection c, SqliteTransaction tx) => Execute(c, tx, Workspaces.CreateSql);
    public static void DropTriggers(SqliteConnection c, SqliteTransaction tx)
        => Execute(c, tx, $"DROP TRIGGER IF EXISTS {InsertTrigger}; DROP TRIGGER IF EXISTS {UpdateTrigger};");
    public static void CreateCoreTables(SqliteConnection c, SqliteTransaction tx)
    { foreach (var item in core) Execute(c, tx, item.CreateSql); }
    public static void CreateResourceTables(SqliteConnection c, SqliteTransaction tx)
    { foreach (var item in resources) Execute(c, tx, item.CreateSql); }
    public static void DropLegacyHistory(SqliteConnection c, SqliteTransaction tx) => Execute(c, tx, "DROP TABLE IF EXISTS resource_history");
    public static void CreateTriggers(SqliteConnection c, SqliteTransaction tx)
    { foreach (var item in triggers) Execute(c, tx, item.CreateSql); }
}
