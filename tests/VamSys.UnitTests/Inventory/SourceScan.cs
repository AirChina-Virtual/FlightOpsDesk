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
    public static string Relative(string path)
    {
        var relative = Path.GetRelativePath(Golden.RepoRoot, path).Replace('\\', '/');
        // Keep R0 inventory provenance for these explicit R2 moves. New/unlisted files keep
        // their real paths, so this mapping cannot hide newly introduced source tokens.
        return relative switch
        {
            "src/VamSys.Domain/ApiEvidence.cs" => "src/VamSys.Core/ApiEvidence.cs",
            "src/VamSys.Domain/ChangePlanner.cs" => "src/VamSys.Core/ChangePlanner.cs",
            "src/VamSys.Domain/CsvAdapter.cs" => "src/VamSys.Core/CsvAdapter.cs",
            "src/VamSys.Domain/DeletionService.cs" => "src/VamSys.Core/DeletionService.cs",
            "src/VamSys.Domain/EditHistory.cs" => "src/VamSys.Core/EditHistory.cs",
            "src/VamSys.Domain/FieldSpec.cs" => "src/VamSys.Core/FieldSpec.cs",
            "src/VamSys.Domain/FleetAssignmentService.cs" => "src/VamSys.Core/FleetAssignmentService.cs",
            "src/VamSys.Domain/IndependentApiEvidence.cs" => "src/VamSys.Core/IndependentApiEvidence.cs",
            "src/VamSys.Domain/LocalReference.cs" => "src/VamSys.Core/LocalReference.cs",
            "src/VamSys.Domain/Localization.cs" => "src/VamSys.Core/Localization.cs",
            "src/VamSys.Domain/Models.cs" => "src/VamSys.Core/Models.cs",
            "src/VamSys.Domain/Schemas.cs" => "src/VamSys.Core/Schemas.cs",
            "src/VamSys.Domain/SnapshotMerger.cs" => "src/VamSys.Core/SnapshotMerger.cs",
            "src/VamSys.Application/BatchCheckpoint.cs" => "src/VamSys.Core/BatchCheckpoint.cs",
            "src/VamSys.Application/BatchExecutor.cs" => "src/VamSys.Core/BatchExecutor.cs",
            "src/VamSys.Application/DraftEdits.cs" => "src/VamSys.Core/DraftEdits.cs",
            "src/VamSys.Application/WindowCloseController.cs" => "src/VamSys.Core/WindowCloseController.cs",
            "src/VamSys.Infrastructure/AppPaths.cs" => "src/VamSys.Core/AppPaths.cs",
            _ => relative
        };
    }
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
