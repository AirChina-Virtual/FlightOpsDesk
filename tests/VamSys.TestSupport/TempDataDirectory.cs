using Microsoft.Data.Sqlite;

namespace VamSys.TestSupport;

/// <summary>An isolated, empty data directory that is deleted on dispose.</summary>
public sealed class TempDataDirectory : IDisposable
{
    public string Path { get; }
    TempDataDirectory(string path) => Path = path;
    public static TempDataDirectory Create(string prefix = "vamsys-test-")
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return new(path);
    }
    public string File(string name) => System.IO.Path.Combine(Path, name);
    public void Dispose()
    {
        // Release pooled SQLite handles so the database files can be removed on Windows.
        SqliteConnection.ClearAllPools();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            { if (Directory.Exists(Path)) Directory.Delete(Path, true); return; }
            catch (IOException) { Thread.Sleep(50); }
            catch (UnauthorizedAccessException) { Thread.Sleep(50); }
        }
    }
}
