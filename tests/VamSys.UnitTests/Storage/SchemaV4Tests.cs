using Microsoft.Data.Sqlite;
using VamSys.Infrastructure;

namespace VamSys.UnitTests.Storage;

// SchemaV4 is the only place that spells out schema SQL; these prove it is also the whole truth about a new database.
public class SchemaV4Tests
{
    static List<(string Type, string Name, string Sql)> Objects(SqliteConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT type,name,sql FROM sqlite_master WHERE sql IS NOT NULL ORDER BY rowid";
        using var r = cmd.ExecuteReader();
        var list = new List<(string, string, string)>();
        while (r.Read())
            list.Add((r.GetString(0), r.GetString(1), r.GetString(2)));
        return list;
    }

    [Fact]
    public void A_freshly_created_database_contains_exactly_the_SchemaV4_statements_in_order()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vamsys-schema-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new WorkspaceStore(dir);
            Assert.Null(store.MigrationBackup);
            using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(dir, "workspaces.db"), Pooling = false }.ToString());
            c.Open();
            Assert.Equal(SchemaV4.All.Select(o => (o.Type, o.Name, o.Sql)).ToList(), Objects(c));
            using var version = c.CreateCommand();
            version.CommandText = "PRAGMA user_version";
            Assert.Equal((long)SchemaV4.Version, version.ExecuteScalar());
        }
        finally
        {
            // Only this test's pool: other tests may be using SQLite in parallel.
            using (var pooled = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(dir, "workspaces.db") }.ToString()))
                SqliteConnection.ClearPool(pooled);
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void SchemaV4_alone_builds_the_same_objects_as_the_store()
    {
        using var c = new SqliteConnection("Data Source=:memory:");
        c.Open();
        using var tx = c.BeginTransaction();
        SchemaV4.CreateWorkspacesTable(c, tx);
        SchemaV4.CreateCoreTables(c, tx);
        SchemaV4.CreateResourceTables(c, tx);
        SchemaV4.CreateTriggers(c, tx);
        tx.Commit();
        Assert.Equal(SchemaV4.All.Select(o => (o.Type, o.Name, o.Sql)).ToList(), Objects(c));
    }

    [Fact]
    public void Idempotent_creation_leaves_existing_objects_untouched()
    {
        using var c = new SqliteConnection("Data Source=:memory:");
        c.Open();
        using var tx = c.BeginTransaction();
        SchemaV4.CreateWorkspacesTable(c, tx);
        SchemaV4.CreateCoreTables(c, tx);
        SchemaV4.CreateCoreTables(c, tx);
        tx.Commit();
        Assert.Equal(SchemaV4.All.Where(o => o.Name == "workspaces" || o.Name is "secrets" or "app_settings" or "workspace_revisions" or "batch_jobs" or "batch_items")
            .Select(o => (o.Type, o.Name, o.Sql)).ToList(), Objects(c));
    }
}
