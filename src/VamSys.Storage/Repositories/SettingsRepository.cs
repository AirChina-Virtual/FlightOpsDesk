namespace VamSys.Infrastructure;

// app_settings: per-installation preferences that are not part of any workspace.
internal sealed class SettingsRepository(StorageContext context)
{
    public string LoadLanguage() => Load("language") == "en-US" ? "en-US" : "zh-CN";
    public void SaveLanguage(string language) => Save("language", language == "en-US" ? language : "zh-CN");
    public string LoadTableDensity() => Load("table_density") == "compact" ? "compact" : "comfortable";
    public void SaveTableDensity(string density) => Save("table_density", density == "compact" ? "compact" : "comfortable");

    string? Load(string key)
    {
        using var c = context.Connections.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT value FROM app_settings WHERE key=$key";
        cmd.Parameters.AddWithValue("$key", key);
        return cmd.ExecuteScalar() as string;
    }
    void Save(string key, string value)
    {
        lock (context.WriteLock)
        {
            using var c = context.Connections.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO app_settings VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=$value";
            cmd.Parameters.AddWithValue("$key", key);
            cmd.Parameters.AddWithValue("$value", value);
            cmd.ExecuteNonQuery();
        }
    }
}
