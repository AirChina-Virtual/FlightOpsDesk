using System.Text;

namespace VamSys.UnitTests.Inventory;

/// <summary>
/// Compares a generated, sorted text listing with a file in Inventory/Expected.
/// Set VAMSYS_UPDATE_GOLDEN=1 to rewrite the expected file instead of comparing.
/// Lines of the form "key => value" are reported as "changed" when only the value differs.
/// </summary>
internal static class Golden
{
    const string KeySeparator = " => ";

    static readonly Lazy<string> root = new(FindRoot);
    public static string RepoRoot => root.Value;

    static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "VamSys.slnx"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new InvalidOperationException("VamSys.slnx not found above " + AppContext.BaseDirectory);
    }

    public static string ExpectedPath(string name) => Path.Combine(RepoRoot, "tests", "VamSys.UnitTests", "Inventory", "Expected", name);

    public static void Verify(string name, IEnumerable<string> lines, bool sort = true)
    {
        var actual = lines.Select(l => l.Replace("\r", "").Replace("\n", "\\n")).ToList();
        if (sort) actual.Sort(StringComparer.Ordinal);
        var path = ExpectedPath(name);
        if (Environment.GetEnvironmentVariable("VAMSYS_UPDATE_GOLDEN") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, actual.Count == 0 ? "" : string.Join("\n", actual) + "\n", new UTF8Encoding(false));
            return;
        }
        Assert.True(File.Exists(path), $"Expected file missing: {path}. Run with VAMSYS_UPDATE_GOLDEN=1 to create it.");
        var content = File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n");
        var expected = content.Length == 0 ? [] : content.Split('\n').ToList();
        if (expected.Count > 0 && expected[^1] == "") expected.RemoveAt(expected.Count - 1);
        if (expected.SequenceEqual(actual, StringComparer.Ordinal)) return;
        Assert.Fail(Describe(name, expected, actual));
    }

    static string Describe(string name, List<string> expected, List<string> actual)
    {
        var removed = expected.Except(actual, StringComparer.Ordinal).ToList();
        var added = actual.Except(expected, StringComparer.Ordinal).ToList();
        static string? Key(string line) { var i = line.IndexOf(KeySeparator, StringComparison.Ordinal); return i < 0 ? null : line[..i]; }
        var changed = new List<string>();
        foreach (var old in removed.ToList())
        {
            var key = Key(old);
            if (key == null) continue;
            var replacement = added.FirstOrDefault(a => Key(a) == key);
            if (replacement == null) continue;
            changed.Add($"  {old}\n    -> {replacement}");
            removed.Remove(old); added.Remove(replacement);
        }
        var text = new StringBuilder($"Inventory '{name}' differs from Inventory/Expected/{name} (a persisted or externally relied-on identifier changed).\n");
        void Section(string title, List<string> items)
        {
            if (items.Count == 0) return;
            text.AppendLine($"{title} ({items.Count}):");
            foreach (var item in items.Take(60)) text.AppendLine("  " + item.TrimStart());
            if (items.Count > 60) text.AppendLine($"  ... {items.Count - 60} more");
        }
        Section("Removed", removed);
        Section("Added", added);
        if (changed.Count > 0) { text.AppendLine($"Changed ({changed.Count}):"); foreach (var c in changed.Take(60)) text.AppendLine(c); }
        if (removed.Count + added.Count + changed.Count == 0) text.AppendLine("Only ordering or duplicate lines differ.");
        return text.ToString();
    }
}
