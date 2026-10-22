using Microsoft.Data.Sqlite;

namespace VamSys.Infrastructure;

// Thin helpers over one open connection/transaction; every statement is parameterized.
internal static class Sql
{
    public static SqliteCommand Command(SqliteConnection c, SqliteTransaction? tx, string sql, params (string, object?)[] values)
    {
        var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (key, value) in values)
            cmd.Parameters.AddWithValue(key, value ?? DBNull.Value);
        return cmd;
    }
    public static int Execute(SqliteConnection c, SqliteTransaction? tx, string sql, params (string, object?)[] values)
    { using var cmd = Command(c, tx, sql, values); return cmd.ExecuteNonQuery(); }
    public static object? Scalar(SqliteConnection c, SqliteTransaction? tx, string sql, params (string, object?)[] values)
    { using var cmd = Command(c, tx, sql, values); return cmd.ExecuteScalar(); }
}
