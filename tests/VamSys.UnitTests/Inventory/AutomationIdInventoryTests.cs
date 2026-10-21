using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace VamSys.UnitTests.Inventory;

/// <summary>
/// I12: window scripts find controls by AutomationId and by accessible name. The WinUI app cannot be loaded in a unit
/// test, so both sides are collected by scanning source text.
/// </summary>
public partial class AutomationIdInventoryTests
{
    // --- app side ---
    [GeneratedRegex(@"SetAutomationId\(\s*\w+\s*,\s*""(?<id>[^""]+)""\s*\)")]
    private static partial Regex SetIdLiteral();

    // Helper methods that forward a trailing id literal to SetAutomationId (DataButton(key, action, "DataFirstPage", icon), SelectionItem(key, action, "DataSelectAll")).
    [GeneratedRegex(@"\b(?:DataButton|SelectionItem)\(.*""(?<id>[A-Za-z]\w*)""\s*(?:,\s*Symbol\.\w+)?\s*\)\s*;")]
    private static partial Regex ForwardedId();

    [GeneratedRegex(@"SetName\(\s*[\w.]+\s*,\s*(?:L\(\s*""(?<key>[^""]+)""|""(?<text>[^""]+)"")")]
    private static partial Regex SetNameLiteral();

    static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    static List<string> AppIds(out HashSet<string> ids, out string sourceText)
    {
        var lines = new List<string>();
        ids = new HashSet<string>(StringComparer.Ordinal);
        var all = new System.Text.StringBuilder();
        var app = Path.Combine(SourceScan.Src, "VamSys.App");
        foreach (var file in SourceScan.CsFiles(app))
        {
            var text = SourceScan.Read(file);
            all.AppendLine(text);
            foreach (var line in text.Split('\n'))
            {
                foreach (Match m in SetIdLiteral().Matches(line))
                { ids.Add(m.Groups["id"].Value); lines.Add($"id:{m.Groups["id"].Value} => code {SourceScan.Relative(file)}"); }
                foreach (Match m in ForwardedId().Matches(line))
                { ids.Add(m.Groups["id"].Value); lines.Add($"id:{m.Groups["id"].Value} => code {SourceScan.Relative(file)}"); }
                foreach (Match m in SetNameLiteral().Matches(line))
                    lines.Add(m.Groups["key"].Success ? $"name-key:{m.Groups["key"].Value} => code {SourceScan.Relative(file)}" : $"name:{m.Groups["text"].Value} => code {SourceScan.Relative(file)}");
            }
        }
        foreach (var file in SourceScan.XamlFiles(app))
        {
            var text = SourceScan.Read(file);
            all.AppendLine(text);
            foreach (var element in XDocument.Parse(text).Descendants())
            {
                // WinUI exposes x:Name as the AutomationId when none is set explicitly.
                var xName = (string?)element.Attribute(X + "Name");
                if (xName != null)
                { ids.Add(xName); lines.Add($"id:{xName} => xaml x:Name {SourceScan.Relative(file)}"); }
                foreach (var attribute in element.Attributes())
                {
                    var local = attribute.Name.LocalName;
                    if (local == "AutomationProperties.AutomationId" && !attribute.Value.StartsWith('{'))
                    { ids.Add(attribute.Value); lines.Add($"id:{attribute.Value} => xaml AutomationId {SourceScan.Relative(file)}"); }
                    if (local == "AutomationProperties.Name")
                        lines.Add($"name:{attribute.Value} => xaml {SourceScan.Relative(file)}");
                }
            }
        }
        sourceText = all.ToString();
        return lines;
    }

    // --- script side ---
    [GeneratedRegex(@"AutomationId\s+-(?:eq|ceq|like)\s+['""](?<id>[^'""]+)['""]")]
    private static partial Regex ScriptIdEq();

    [GeneratedRegex(@"AutomationId\s+-in\s+@\((?<list>[^)]*)\)")]
    private static partial Regex ScriptIdIn();

    [GeneratedRegex(@"\b(?:Click|Named|SelectItem|WaitText)\s+['""](?<name>[^'""$]+)['""]")]
    private static partial Regex ScriptNameCall();

    [GeneratedRegex(@"\.Name\s+-eq\s+['""](?<name>[^'""$]+)['""]|\.Name\.(?:Contains|StartsWith)\(['""](?<name2>[^'""$]+)['""]\)|\$name\s+-eq\s+['""](?<name3>[^'""$]+)['""]|\.Current\.Name\)?\s+-eq\s+['""](?<name4>[^'""$]+)['""]")]
    private static partial Regex ScriptNameCompare();

    [GeneratedRegex(@"case\s+""(?<command>[^""]+)""\s*:")]
    private static partial Regex VerificationCommand();

    static (SortedSet<string> Ids, SortedSet<string> Names, SortedSet<string> Commands) ScriptLookups()
    {
        var ids = new SortedSet<string>(StringComparer.Ordinal);
        var names = new SortedSet<string>(StringComparer.Ordinal);
        var commands = new SortedSet<string>(StringComparer.Ordinal);
        var sources = Directory.EnumerateFiles(Path.Combine(Golden.RepoRoot, "scripts"), "*.ps1").Concat(Directory.EnumerateFiles(Path.Combine(Golden.RepoRoot, "tests", "UiVerification"), "*.cs"));
        foreach (var file in sources.Order(StringComparer.Ordinal))
        {
            var text = SourceScan.Read(file);
            foreach (Match m in ScriptIdEq().Matches(text))
                ids.Add(m.Groups["id"].Value);
            foreach (Match m in ScriptIdIn().Matches(text))
            foreach (var literal in SourceScan.Literals(m.Groups["list"].Value.Replace('\'', '"')))
                ids.Add(literal);
            foreach (Match m in ScriptNameCall().Matches(text))
                names.Add(m.Groups["name"].Value);
            foreach (Match m in ScriptNameCompare().Matches(text))
                foreach (var group in new[] { "name", "name2", "name3", "name4" })
                if (m.Groups[group].Success)
                    names.Add(m.Groups[group].Value);
            if (file.EndsWith(".cs", StringComparison.Ordinal))
            {
                // C# verification code: AutomationId / Name lookups and the named pipe commands scripts send to it.
                foreach (Match m in Regex.Matches(text, @"AutomationId\w*[^;\n]*?""(?<id>[^""]+)"""))
                    ids.Add(m.Groups["id"].Value);
                foreach (Match m in VerificationCommand().Matches(text))
                    commands.Add(m.Groups["command"].Value);
            }
        }
        return (ids, names, commands);
    }

    [Fact]
    public void App_automation_ids_and_names_are_locked()
    {
        Golden.Verify("AutomationIds.App.txt", AppIds(out _, out _));
    }

    [Fact]
    public void Script_lookups_are_locked_and_missing_ones_reported()
    {
        AppIds(out var appIds, out var appText);
        var (ids, names, commands) = ScriptLookups();
        var resx = ResourceKeyInventoryTests.Load("Strings.resx").Values.Concat(ResourceKeyInventoryTests.Load("Strings.en-US.resx").Values).ToList();

        var lines = new List<string>();
        lines.AddRange(ids.Select(i => $"script-id:{i} => {(appIds.Contains(i) ? "found in app source" : "NOT FOUND IN APP SOURCE")}"));
        lines.AddRange(names.Select(n => $"script-name:{n} => {(appText.Contains(n, StringComparison.Ordinal) || resx.Any(v => v.Contains(n, StringComparison.Ordinal)) || SchemaOrCoreText().Contains(n, StringComparison.Ordinal) ? "found in source or resx" : "NOT FOUND IN SOURCE OR RESX")}"));
        lines.AddRange(commands.Select(c => $"verification-command:{c} => tests/UiVerification"));
        Golden.Verify("AutomationIds.Scripts.txt", lines);
    }

    static string? coreText;
    static string SchemaOrCoreText() => coreText ??= string.Join("\n", SourceScan.CsFiles(SourceScan.Src).Select(SourceScan.Read));
}
