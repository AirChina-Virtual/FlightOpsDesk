using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace VamSys.UnitTests.Inventory;

/// <summary>
/// I10: resource keys (Text_..., MessageDescriptor codes) and their placeholder names are saved in tasks
/// and must never be renamed or removed; they may only be added.
/// </summary>
public partial class ResourceKeyInventoryTests
{
    [GeneratedRegex(@"\{(?<name>\w+)(?::(?<format>[^}]+))?\}")]
    private static partial Regex Placeholder();

    // Same direct lookup helpers as src/VamSys.Core/Localization.cs and the app: Messages.Define, L(...), localization.Get(...), Block(...).
    [GeneratedRegex(@"(?:\bDefine|\bL|\b(?:localization|language|Localization|Chinese)\s*\.\s*Get|\bBlock)\(\s*""(?<key>[^""\\]+)""")]
    private static partial Regex DirectLookup();

    [GeneratedRegex(@"\bCode\s*(?:==|!=)\s*""(?<key>[^""\\]+)""")]
    private static partial Regex CodeComparison();

    static string ResourceFile(string name) => Path.Combine(SourceScan.Src, "VamSys.Core", "Resources", name);

    public static Dictionary<string, string> Load(string file)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var data in XDocument.Load(ResourceFile(file)).Root!.Elements("data"))
        {
            var key = (string)data.Attribute("name")!;
            Assert.True(result.TryAdd(key, (string?)data.Element("value") ?? ""), $"Duplicate key {key} in {file}");
        }
        return result;
    }

    static string[] PlaceholderNames(string value) => Placeholder().Matches(value).Select(m => m.Groups["name"].Value).Distinct().Order(StringComparer.Ordinal).ToArray();
    static string[] PlaceholderSpecs(string value) => Placeholder().Matches(value).Select(m => m.Value.Trim('{', '}')).Distinct().Order(StringComparer.Ordinal).ToArray();

    static IEnumerable<string> Describe(string culture, Dictionary<string, string> resources)
        => resources.Select(p => $"{culture}:{p.Key} => {string.Join(",", PlaceholderSpecs(p.Value))}");

    [Fact]
    public void Every_key_and_placeholder_per_culture_is_locked()
    {
        var neutral = Load("Strings.resx");
        var english = Load("Strings.en-US.resx");
        Golden.Verify("ResourceKeys.txt", Describe("neutral(zh-CN)", neutral).Concat(Describe("en-US", english)));
    }

    [Fact]
    public void Culture_differences_in_keys_and_placeholders_are_locked()
    {
        var neutral = Load("Strings.resx");
        var english = Load("Strings.en-US.resx");
        var lines = new List<string>();
        foreach (var key in neutral.Keys.Except(english.Keys))
            lines.Add($"{key} => only in neutral(zh-CN)");
        foreach (var key in english.Keys.Except(neutral.Keys))
            lines.Add($"{key} => only in en-US");
        foreach (var key in neutral.Keys.Intersect(english.Keys))
            if (!PlaceholderNames(neutral[key]).SequenceEqual(PlaceholderNames(english[key])))
                lines.Add($"{key} => placeholders differ: neutral[{string.Join(",", PlaceholderNames(neutral[key]))}] en-US[{string.Join(",", PlaceholderNames(english[key]))}]");
        Golden.Verify("ResourceCultureDifferences.txt", lines);
    }

    [Fact]
    public void Keys_referenced_from_source_are_locked()
    {
        var keys = Load("Strings.resx").Keys.ToHashSet(StringComparer.Ordinal);
        var direct = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in SourceScan.CsFiles(SourceScan.Src))
            foreach (var line in SourceScan.Read(file).Split('\n'))
            {
                foreach (Match m in DirectLookup().Matches(line))
                    direct.Add(m.Groups["key"].Value);
                foreach (Match m in CodeComparison().Matches(line))
                    direct.Add(m.Groups["key"].Value);
            }
        // Keys that reach the lookup through a helper or a variable: any literal that is exactly a resource key.
        var indirect = SourceScan.AllLiterals(SourceScan.Src).Select(l => l.Value).Where(v => keys.Contains(v) && !direct.Contains(v)).ToHashSet(StringComparer.Ordinal);

        var lines = new List<string>();
        // A literal that is not a key but prefixes keys is a dynamically built key ("Enum_" + name).
        string Status(string k) => keys.Contains(k) ? "in resx" : keys.Count(x => x.StartsWith(k, StringComparison.Ordinal)) is var n && n > 0 ? $"dynamic prefix of {n} resx keys" : "NOT IN RESX";
        lines.AddRange(direct.Select(k => $"direct:{k} => {Status(k)}"));
        lines.AddRange(indirect.Select(k => $"indirect:{k} => in resx"));
        Golden.Verify("ResourceReferencedKeys.txt", lines);
    }
}
