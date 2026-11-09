using System.Text.Json;

namespace VamSys.Infrastructure;

// Where the data lives cannot be stored in the database it points to, so a small file in the default directory holds it.
// Directory is null for the default location. Retire lists earlier locations whose database still has to be set aside.
public sealed record DataLocationState(string? Directory, IReadOnlyList<string> Retire)
{
    public static DataLocationState Default { get; } = new(null, []);
}

public static class DataLocationFile
{
    public const string FileName = "data-location.json";

    // The default state when there is no file; throws when the file cannot be understood, because guessing a location
    // could open an empty database and look like lost data.
    public static DataLocationState Read(string path)
    {
        if (!File.Exists(path))
            return DataLocationState.Default;
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        var directory = root.TryGetProperty("directory", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
        var retire = root.TryGetProperty("retire", out var r) && r.ValueKind == JsonValueKind.Array
            ? r.EnumerateArray().Select(e => e.GetString()).OfType<string>().ToList()
            : [];
        return new(directory, retire);
    }

    // Replaces the file in one step, so a crash leaves the old location or the new one. The default state needs no file.
    public static void Write(string path, DataLocationState state)
    {
        if (state.Directory is null && state.Retire.Count == 0)
        {
            File.Delete(path);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new { directory = state.Directory, retire = state.Retire }, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, overwrite: true);
    }
}
