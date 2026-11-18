using VamSys.Core;

namespace VamSys.Infrastructure;

// app_settings: per-installation preferences that are not part of any workspace.
internal sealed class SettingsRepository(StorageContext context)
{
    public string LoadLanguage() => Load("language") == "en-US" ? "en-US" : "zh-CN";
    public void SaveLanguage(string language) => Save("language", language == "en-US" ? language : "zh-CN");
    public string LoadTableDensity() => Load("table_density") == "compact" ? "compact" : "comfortable";
    public void SaveTableDensity(string density) => Save("table_density", density == "compact" ? "compact" : "comfortable");

    // The columns hidden from the view menu, one key per resource ("hidden_columns.Routes"), as a JSON array of column
    // names. A value that is not such an array reads as nothing hidden. Which names still apply is decided by the caller.
    public IReadOnlyList<string> LoadHiddenColumns(ResourceKind kind)
    {
        var value = Load(HiddenColumnsKey(kind));
        if (value is null)
            return [];
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(value);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array
                || doc.RootElement.EnumerateArray().Any(e => e.ValueKind != System.Text.Json.JsonValueKind.String))
                return [];
            return doc.RootElement.EnumerateArray().Select(e => e.GetString()!).Distinct(StringComparer.Ordinal).ToList();
        }
        catch (System.Text.Json.JsonException) { return []; }
    }
    public void SaveHiddenColumns(ResourceKind kind, IReadOnlyCollection<string> columns)
        => Save(HiddenColumnsKey(kind), System.Text.Json.JsonSerializer.Serialize(columns.Distinct(StringComparer.Ordinal).ToArray()));
    static string HiddenColumnsKey(ResourceKind kind) => "hidden_columns." + kind;

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
