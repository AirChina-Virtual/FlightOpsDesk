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
    static IEnumerable<string> Describe(Assembly assembly)
    {
        foreach (var type in assembly.GetTypes().Where(t => t.IsEnum))
        {
            var underlying = Enum.GetUnderlyingType(type).Name;
            var flags = type.GetCustomAttribute<FlagsAttribute>() != null ? " [Flags]" : "";
            var visibility = type.IsVisible ? "public" : "non-public";
            yield return $"{assembly.GetName().Name}:{type.FullName} => {visibility} {underlying}{flags}";
            foreach (var name in Enum.GetNames(type))
                yield return $"{assembly.GetName().Name}:{type.FullName}.{name} => {Convert.ToDecimal(Enum.Parse(type, name), System.Globalization.CultureInfo.InvariantCulture)}";
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
        var lines = Describe(typeof(Workspace).Assembly).Concat(Describe(typeof(WorkspaceStore).Assembly)).Concat(DescribeApp()).ToList();
        Assert.Contains(lines, l => l.StartsWith("VamSys.Core:VamSys.Core.ItemState.ManuallyConfirmed => 7"));
        Golden.Verify("Enums.txt", lines);
    }
}
