using Microsoft.Data.Sqlite;
using VamSys.Core;
using static VamSys.Infrastructure.Sql;

namespace VamSys.Infrastructure;

// Moving the data to another directory. The database arrives under its real name only after it verified, so a directory
// holding workspaces.db always holds a complete copy. The source is never changed here: its database is set aside at the
// next start, once the new location has opened.
public sealed partial class WorkspaceStore
{
    const string DatabaseName = "workspaces.db";

    public long DataBytes() => Files(DataDirectory).Sum(f => new FileInfo(f.Source).Length) + DatabaseFiles(DataDirectory).Sum(f => new FileInfo(f).Length);

    public Task CopyDataToAsync(string target) => Task.Run(() =>
    {
        using var lease = new DataDirectoryLease(target);
        lock (context.WriteLock)
        {
            if (File.Exists(Path.Combine(target, DatabaseName)))
                throw StorageErrors.Block("DataLocationFailed");
            foreach (var (source, relative) in Files(DataDirectory))
            {
                var destination = Path.Combine(target, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                if (!File.Exists(destination))
                    File.Copy(source, destination);
            }
            var moving = Path.Combine(target, DatabaseName + ".moving");
            File.Delete(moving);
            using (var from = Open())
            using (var to = SqliteConnectionFactory.OpenBackupTarget(moving))
            {
                from.BackupDatabase(to);
                if (!Equals(Scalar(to, null, "PRAGMA integrity_check"), "ok") || !SameRowCounts(from, to))
                    throw StorageErrors.Block("DataLocationFailed");
                Execute(to, null, "PRAGMA wal_checkpoint(TRUNCATE)");
            }
            File.Move(moving, Path.Combine(target, DatabaseName));
            // Nothing writes after this; the next process can set the old database aside.
            SqliteConnection.ClearPool(new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(DataDirectory, DatabaseName) }.ToString()));
        }
    });

    static bool SameRowCounts(SqliteConnection a, SqliteConnection b)
    {
        var tables = new List<string>();
        using (var command = a.CreateCommand())
        {
            command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'";
            using var reader = command.ExecuteReader();
            while (reader.Read())
                tables.Add(reader.GetString(0));
        }
        return tables.All(t => Equals(Scalar(a, null, $"SELECT COUNT(*) FROM \"{t}\""), Scalar(b, null, $"SELECT COUNT(*) FROM \"{t}\"")));
    }

    // What goes along with the database: backups and timing records. Logs and the lock belong to the old directory.
    static IEnumerable<(string Source, string Relative)> Files(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "workspaces-before-v4-*.db"))
            yield return (file, Path.GetFileName(file));
        foreach (var folder in new[] { "Backups", "performance" })
        {
            var root = Path.Combine(directory, folder);
            if (Directory.Exists(root))
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                    yield return (file, Path.GetRelativePath(directory, file));
        }
    }

    static IEnumerable<string> DatabaseFiles(string directory)
        => new[] { "", "-wal", "-shm" }.Select(suffix => Path.Combine(directory, DatabaseName + suffix)).Where(File.Exists);

    // Refuses a database this version could not open, before the program restarts onto it.
    public static void RequireOpenable(string directory)
    {
        using var lease = new DataDirectoryLease(directory);
        var path = Path.Combine(directory, DatabaseName);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        try
        {
            connection.Open();
            if ((long)Scalar(connection, null, "PRAGMA user_version")! > SchemaV4.Version || !Equals(Scalar(connection, null, "PRAGMA integrity_check"), "ok"))
                throw StorageErrors.Block("DataLocationInvalid");
        }
        catch (SqliteException) { throw StorageErrors.Block("DataLocationInvalid"); }
    }

    // Renames the database of a location the data has left, so it cannot be opened by mistake later and continue from an
    // old state; nothing is deleted. False when it cannot be done now, and it is tried again at the next start.
    public static bool TrySetAside(string directory)
    {
        if (!Directory.Exists(directory))
            return false;
        try
        {
            using var lease = new DataDirectoryLease(directory);
            var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            foreach (var file in DatabaseFiles(directory).ToList())
                File.Move(file, Path.Combine(directory, "workspaces-moved-" + stamp + ".db" + Path.GetFileName(file)[DatabaseName.Length..]));
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException) { return false; }
    }
}
