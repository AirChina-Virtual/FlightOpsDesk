using System.Reflection;
using System.Text.RegularExpressions;
using VamSys.Core;
using VamSys.Infrastructure;

namespace VamSys.UnitTests.Inventory;

/// <summary>
/// I09: workspace JSON is written by System.Text.Json without a string-enum converter,
/// so enum numeric values are persisted. Members must never be reordered, removed or inserted.
/// </summary>
public partial class EnumInventoryTests
{
    // Enums that were declared in the WinUI app and now live in the application project.
    static readonly Dictionary<string, string> MovedFromApp = new() { ["VamSys.Core.ReviewDisposition"] = "src/VamSys.App/ReviewPlanBuilder.cs" };

    static IEnumerable<string> Describe(Assembly assembly)
    {
        // This inventory locks persisted enum names/numbers, not physical assembly placement.
        // ArchitectureBoundaryTests locks the real Domain/Application owners separately.
        var source = assembly == typeof(Workspace).Assembly || assembly == typeof(BatchExecutor).Assembly
            ? "VamSys.Core" : assembly.GetName().Name;
        foreach (var type in assembly.GetTypes().Where(t => t.IsEnum))
        {
            // Moved out of the app assembly by R4: keep the R0 provenance the expected file records.
            if (MovedFromApp.TryGetValue(type.FullName!, out var declaredIn))
            {
                yield return $"VamSys.App(source):{type.Name} => declared in {declaredIn}";
                foreach (var name in Enum.GetNames(type))
                    yield return $"VamSys.App(source):{type.Name}.{name} => {Convert.ToDecimal(Enum.Parse(type, name), System.Globalization.CultureInfo.InvariantCulture)}";
                continue;
            }
            var underlying = Enum.GetUnderlyingType(type).Name;
            var flags = type.GetCustomAttribute<FlagsAttribute>() != null ? " [Flags]" : "";
            var visibility = type.IsVisible ? "public" : "non-public";
            yield return $"{source}:{type.FullName} => {visibility} {underlying}{flags}";
            foreach (var name in Enum.GetNames(type))
                yield return $"{source}:{type.FullName}.{name} => {Convert.ToDecimal(Enum.Parse(type, name), System.Globalization.CultureInfo.InvariantCulture)}";
        }
    }

    [GeneratedRegex(@"\benum\s+(?<name>\w+)\s*(?::\s*\w+\s*)?\{(?<body>[^}]*)\}")]
    private static partial Regex EnumDeclaration();

    // The WinUI app assembly cannot be loaded here, so its enums are read from source.
    static IEnumerable<string> DescribeApp()
    {
        foreach (var file in SourceScan.CsFiles(Path.Combine(SourceScan.Src, "VamSys.App")))
            foreach (Match match in EnumDeclaration().Matches(SourceScan.Read(file)))
            {
                var name = match.Groups["name"].Value;
                long next = 0;
                yield return $"VamSys.App(source):{name} => declared in {SourceScan.Relative(file)}";
                foreach (var member in match.Groups["body"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var parts = member.Split('=', 2, StringSplitOptions.TrimEntries);
                    if (parts.Length == 2 && long.TryParse(parts[1], out var explicitValue))
                        next = explicitValue;
                    yield return $"VamSys.App(source):{name}.{parts[0]} => {next}";
                    next++;
                }
            }
    }

    [Fact]
    public void Enum_member_names_and_numeric_values_are_locked()
    {
        var assemblies = new[] { typeof(Workspace).Assembly, typeof(BatchExecutor).Assembly, typeof(OperationsAdapter).Assembly, typeof(WorkspaceStore).Assembly };
        var lines = assemblies.Distinct().SelectMany(Describe).Concat(DescribeApp()).ToList();
        Assert.Contains(lines, l => l.StartsWith("VamSys.Core:VamSys.Core.ItemState.ManuallyConfirmed => 7"));
        Golden.Verify("Enums.txt", lines);
    }
}
