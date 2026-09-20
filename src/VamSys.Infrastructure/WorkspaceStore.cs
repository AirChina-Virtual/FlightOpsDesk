using Microsoft.Data.Sqlite;
using VamSys.Core;
using System.Security.Cryptography;
using System.Text;

namespace VamSys.Infrastructure;

public sealed class WorkspaceStore
{
    readonly string connectionString;
    public WorkspaceStore(string directory)
    {
        Directory.CreateDirectory(directory);
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "workspaces.db") }.ToString();
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE IF NOT EXISTS workspaces(id TEXT PRIMARY KEY, name TEXT NOT NULL, json TEXT NOT NULL); CREATE TABLE IF NOT EXISTS secrets(id TEXT PRIMARY KEY, value BLOB NOT NULL); CREATE TABLE IF NOT EXISTS app_settings(key TEXT PRIMARY KEY, value TEXT NOT NULL);";
        cmd.ExecuteNonQuery();
    }
    SqliteConnection Open() { var c = new SqliteConnection(connectionString); c.Open(); return c; }
    public string LoadLanguage()
    {
        using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT value FROM app_settings WHERE key='language'";
        return cmd.ExecuteScalar() as string == "en-US" ? "en-US" : "zh-CN";
    }
    public void SaveLanguage(string language)
    {
        using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "INSERT INTO app_settings VALUES('language',$value) ON CONFLICT(key) DO UPDATE SET value=$value";
        cmd.Parameters.AddWithValue("$value", language == "en-US" ? language : "zh-CN"); cmd.ExecuteNonQuery();
    }
    public List<(Guid Id, string Name)> List()
    {
        using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT id,name FROM workspaces ORDER BY name";
        using var r = cmd.ExecuteReader(); var result = new List<(Guid, string)>(); while (r.Read()) result.Add((Guid.Parse(r.GetString(0)), r.GetString(1))); return result;
    }
    public Workspace Load(Guid id)
    {
        using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT json FROM workspaces WHERE id=$id"; cmd.Parameters.AddWithValue("$id", id.ToString());
        return Workspace.Deserialize((string)(cmd.ExecuteScalar() ?? throw new KeyNotFoundException()));
    }
    public void Save(Workspace w) => SaveJson(w.Id, w.Name, w.Serialize());
    public void SaveJson(Guid id, string name, string json)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO workspaces VALUES($id,$name,$json) ON CONFLICT(id) DO UPDATE SET name=$name,json=$json";
        cmd.Parameters.AddWithValue("$id", id.ToString()); cmd.Parameters.AddWithValue("$name", name); cmd.Parameters.AddWithValue("$json", json); cmd.ExecuteNonQuery();
    }
    public void SaveSecret(Guid id, string secret)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), id.ToByteArray(), DataProtectionScope.CurrentUser);
        using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "INSERT INTO secrets VALUES($id,$value) ON CONFLICT(id) DO UPDATE SET value=$value";
        cmd.Parameters.AddWithValue("$id", id.ToString()); cmd.Parameters.AddWithValue("$value", encrypted); cmd.ExecuteNonQuery();
    }
    public string? LoadSecret(Guid id)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT value FROM secrets WHERE id=$id"; cmd.Parameters.AddWithValue("$id", id.ToString());
        return cmd.ExecuteScalar() is byte[] bytes ? Encoding.UTF8.GetString(ProtectedData.Unprotect(bytes, id.ToByteArray(), DataProtectionScope.CurrentUser)) : null;
    }
}
