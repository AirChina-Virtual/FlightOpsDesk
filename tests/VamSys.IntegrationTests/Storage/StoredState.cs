using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace VamSys.IntegrationTests.Storage;

/// <summary>
/// Everything a database holds about workspaces, as comparable text: every table in a fixed order, every column of every row,
/// GUIDs and times unmasked. Two databases that were written by different routes (journal save and full save) must produce
/// the same text. Only the save counter is left out: the workspace header repeats it (StorageRevision) and workspace_revisions
/// holds it, and a route that skips saves with nothing to write legitimately has a lower one.
/// </summary>
static partial class StoredState
{
    static readonly (string Table, string Order)[] Tables =
    [
        ("workspaces", "id"),
        ("secrets", "id"),
        ("app_settings", "key"),
        ("batch_jobs", "workspace_id,ordinal"),
        ("batch_items", "workspace_id,(SELECT j.ordinal FROM batch_jobs j WHERE j.workspace_id=batch_items.workspace_id AND j.id=batch_items.job_id),ordinal"),
        ("resource_metadata", "workspace_id,resource"),
        ("resource_rows", "workspace_id,resource,collection,ordinal"),
        ("resource_history_state", "workspace_id,resource"),
        ("resource_history_steps", "workspace_id,resource,step_id"),
    ];

    [GeneratedRegex("\"StorageRevision\":\\d+")]
    private static partial Regex Revision();

    public static string Dump(string directory, bool withRevision = false)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "workspaces.db"), Pooling = false, Mode = SqliteOpenMode.ReadOnly }.ToString());
        c.Open();
        var text = new StringBuilder();
        var tables = withRevision ? Tables.Append(("workspace_revisions", "workspace_id")).ToArray() : Tables;
        foreach (var (table, order) in tables)
        {
            text.Append("## ").AppendLine(table);
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"SELECT * FROM {table} ORDER BY {order}";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                for (var i = 0; i < r.FieldCount; i++)
                {
                    var value = r.IsDBNull(i) ? "<null>" : r.GetValue(i) is byte[] bytes ? Convert.ToHexString(bytes) : Convert.ToString(r.GetValue(i), System.Globalization.CultureInfo.InvariantCulture)!;
                    if (table == "workspaces" && r.GetName(i) == "json")
                        value = Revision().Replace(value, "\"StorageRevision\":0");
                    text.Append(r.GetName(i)).Append('=').Append(value).Append('\t');
                }
                text.AppendLine();
            }
        }
        return text.ToString();
    }

    // Number of data-changing statements the database ran since the triggers were installed (see InstallWriteCounters).
    public static void InstallWriteCounters(string directory)
    {
        using var c = Open(directory);
        var sql = new StringBuilder("CREATE TABLE writes(tbl TEXT);");
        foreach (var table in new[] { "workspaces", "batch_jobs", "batch_items", "resource_metadata", "resource_rows", "resource_history_state", "resource_history_steps" })
            foreach (var op in new[] { "INSERT", "UPDATE", "DELETE" })
                sql.Append($"CREATE TRIGGER w_{table}_{op} AFTER {op} ON {table} BEGIN INSERT INTO writes VALUES('{table}'); END;");
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql.ToString();
        cmd.ExecuteNonQuery();
    }

    public static Dictionary<string, int> Writes(string directory, bool reset = true)
    {
        using var c = Open(directory);
        var result = new Dictionary<string, int>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT tbl,count(*) FROM writes GROUP BY tbl";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                result[r.GetString(0)] = r.GetInt32(1);
        }
        if (reset)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "DELETE FROM writes";
            cmd.ExecuteNonQuery();
        }
        return result;
    }

    public static long Revision(string directory)
    {
        using var c = Open(directory);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(revision),0) FROM workspace_revisions";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    static SqliteConnection Open(string directory)
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "workspaces.db"), Pooling = false }.ToString());
        c.Open();
        return c;
    }
}
