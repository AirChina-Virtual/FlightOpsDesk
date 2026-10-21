using System.Text.RegularExpressions;

namespace VamSys.UnitTests.Inventory;

/// <summary>Text-level scanning of repository sources (the WinUI app cannot be loaded in a unit test).</summary>
internal static partial class SourceScan
{
    public static string Src => Path.Combine(Golden.RepoRoot, "src");

    static IEnumerable<string> Files(string directory, string pattern)
        => Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, pattern, SearchOption.AllDirectories)
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                .OrderBy(f => f, StringComparer.Ordinal)
            : [];

    public static IEnumerable<string> CsFiles(string directory) => Files(directory, "*.cs");
    public static IEnumerable<string> XamlFiles(string directory) => Files(directory, "*.xaml");
    public static string Relative(string path) => Path.GetRelativePath(Golden.RepoRoot, path).Replace('\\', '/');
    public static string Read(string path) => File.ReadAllText(path).Replace("\r\n", "\n");

    [GeneratedRegex("\"((?:[^\"\\\\\\r\\n]|\\\\.)*)\"")]
    private static partial Regex Literal();

    public static IEnumerable<string> Literals(string line) => Literal().Matches(line).Select(m => m.Groups[1].Value);

    /// <summary>All string literals in all C# files, with their file.</summary>
    public static IEnumerable<(string File, string Line, string Value)> AllLiterals(string directory)
    {
        foreach (var file in CsFiles(directory))
            foreach (var line in Read(file).Split('\n'))
                foreach (var value in Literals(line))
                    yield return (Relative(file), line, value);
    }
}
