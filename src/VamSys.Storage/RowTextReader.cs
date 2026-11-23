using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace VamSys.Infrastructure;

// Reads the rows of one query as SQLite holds them, on the caller's connection and so inside its transaction: integer
// columns as numbers and text columns as their stored UTF-8 bytes, which stay valid until the next row. A data reader
// would turn every row's JSON into a .NET string first (and the JSON reader would turn it back into UTF-8). Used only for
// the row JSON of a workspace read (2026-11-23, stage 7 item 3). Busy and locked results before the first row are retried
// for as long as a SqliteCommand would wait (its default timeout); later ones throw, as a data reader would.
internal sealed class RowTextReader : IDisposable
{
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    readonly sqlite3 db;
    readonly sqlite3_stmt statement;
    bool started;

    public RowTextReader(SqliteConnection connection, string sql, params string[] parameters)
    {
        db = connection.Handle ?? throw new InvalidOperationException("The connection is not open.");
        SqliteException.ThrowExceptionForRC(raw.sqlite3_prepare_v2(db, sql, out statement), db);
        try
        {
            for (var n = 0; n < parameters.Length; n++)
                SqliteException.ThrowExceptionForRC(raw.sqlite3_bind_text(statement, n + 1, parameters[n]), db);
        }
        catch { statement.Dispose(); throw; }
    }

    public bool Read()
    {
        long? busySince = null;
        while (true)
        {
            var rc = raw.sqlite3_step(statement);
            if (rc == raw.SQLITE_ROW)
            {
                started = true;
                return true;
            }
            if (rc == raw.SQLITE_DONE)
                return false;
            if (!started && (rc == raw.SQLITE_BUSY || rc == raw.SQLITE_LOCKED) && System.Diagnostics.Stopwatch.GetElapsedTime(busySince ??= System.Diagnostics.Stopwatch.GetTimestamp()) < Timeout)
            {
                raw.sqlite3_reset(statement);
                Thread.Sleep(150);
                continue;
            }
            SqliteException.ThrowExceptionForRC(rc, db);
            return false;
        }
    }

    // The same conversion SqliteDataReader.GetInt32 makes.
    public int Int32(int column) => checked((int)raw.sqlite3_column_int64(statement, column));
    public ReadOnlySpan<byte> Text(int column) => raw.sqlite3_column_blob(statement, column);
    public void Dispose() => statement.Dispose();
}
