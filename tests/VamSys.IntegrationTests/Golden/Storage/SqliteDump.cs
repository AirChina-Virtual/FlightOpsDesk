using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace VamSys.IntegrationTests.Golden.Storage;

/// <summary>
/// Dumps a SQLite database to normalised JSON: user_version, journal mode, schema SQL (tables, indexes, triggers) and
/// every table's rows. Row order follows the primary key, or the stored ordinal where the key is a random GUID, so the
/// order never depends on a generated value. Columns keep their declared order and JSON text keeps its stored property
/// order (it is parsed and re-indented, never sorted).
/// </summary>
public sealed class SqliteDump(Masker masker)
{
    // Orders that avoid random GUID keys. Anything not listed falls back to rowid.
    static readonly Dictionary<string, string> Order = new()
    {
        ["workspaces"] = "id",
        ["secrets"] = "id",
        ["app_settings"] = "key",
        ["workspace_revisions"] = "workspace_id",
        ["batch_jobs"] = "workspace_id,ordinal",
        ["batch_items"] = "workspace_id,(SELECT j.ordinal FROM batch_jobs j WHERE j.workspace_id=batch_items.workspace_id AND j.id=batch_items.job_id),ordinal",
        ["resource_metadata"] = "workspace_id,resource",
        ["resource_rows"] = "workspace_id,resource,collection,ordinal",
        ["resource_history_state"] = "workspace_id,resource",
    };

    /// <param name="maskBlobs">True for DPAPI ciphertext (differs per user and per call); false prints bytes as hex.</param>
    public string Dump(string databasePath, bool maskBlobs)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false, Mode = SqliteOpenMode.ReadWrite }.ToString());
        c.Open();
        var root = new JsonObject
        {
            ["user_version"] = Convert.ToInt64(Scalar(c, "PRAGMA user_version")),
            ["journal_mode"] = (string?)Scalar(c, "PRAGMA journal_mode"),
        };

        var schema = new JsonArray();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT type,name,tbl_name,sql FROM sqlite_master ORDER BY type,name";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                schema.Add(new JsonObject { ["type"] = r.GetString(0), ["name"] = r.GetString(1), ["tbl_name"] = r.GetString(2), ["sql"] = r.IsDBNull(3) ? null : r.GetString(3) });
        }
        root["schema"] = schema;

        var tables = new JsonObject();
        foreach (var table in Tables(c))
            tables[table] = DumpTable(c, table, maskBlobs);
        root["tables"] = tables;
        return root.ToJsonString(Masker.Pretty) + "\n";
    }

    static List<string> Tables(SqliteConnection c)
    {
        var names = new List<string>();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            names.Add(r.GetString(0));
        return names;
    }

    JsonArray DumpTable(SqliteConnection c, string table, bool maskBlobs)
    {
        var rows = new List<JsonObject>();
        var keys = new List<(string Workspace, int Resource, int Position, string Step)>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = $"SELECT * FROM \"{table}\" ORDER BY {Order.GetValueOrDefault(table, "rowid")}";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var row = new JsonObject();
                for (var i = 0; i < r.FieldCount; i++)
                    row[r.GetName(i)] = Value(r, i, maskBlobs);
                rows.Add(row);
            }
        }
        if (table != "resource_history_steps")
            return new JsonArray(rows.Select(r => (JsonNode?)r).ToArray());

        // Step ids are random: list steps in the order of the undo/redo stacks that reference them.
        var position = new Dictionary<(string, int, string), int>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT workspace_id,resource,undo_json,redo_json FROM resource_history_state";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var n = 0;
                foreach (var json in new[] { r.GetString(2), r.GetString(3) })
                    foreach (var id in JsonNode.Parse(json)!.AsArray())
                        position[(r.GetString(0), r.GetInt32(1), id!.GetValue<string>())] = n++;
            }
        }
        // Original (unmasked) ids are needed for the lookup, so read them again in the same order.
        var ids = new List<(string, int, string)>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = $"SELECT workspace_id,resource,step_id FROM resource_history_steps ORDER BY {Order.GetValueOrDefault(table, "rowid")}";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                ids.Add((r.GetString(0), r.GetInt32(1), r.GetString(2)));
        }
        return new JsonArray(rows.Zip(ids).Select(p => (Row: p.First, Key: p.Second))
            .OrderBy(p => p.Key.Item1, StringComparer.Ordinal).ThenBy(p => p.Key.Item2)
            .ThenBy(p => position.GetValueOrDefault(p.Key, int.MaxValue)).ThenBy(p => p.Key.Item3, StringComparer.Ordinal)
            .Select(p => (JsonNode?)p.Row).ToArray());
    }

    JsonNode? Value(SqliteDataReader r, int i, bool maskBlobs)
    {
        if (r.IsDBNull(i))
            return null;
        switch (r.GetFieldType(i).Name)
        {
            case "Byte[]":
                return maskBlobs ? "<dpapi-ciphertext>" : "hex:" + Convert.ToHexString((byte[])r.GetValue(i));
            case "Int64":
                return r.GetInt64(i);
            case "Double":
                return r.GetDouble(i);
        }
        var text = r.GetString(i);
        var trimmed = text.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            try
            { return masker.Prepare(JsonNode.Parse(text)); }
            catch (System.Text.Json.JsonException) { }
        }
        return masker.Mask(text);
    }

    static object? Scalar(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }
}
