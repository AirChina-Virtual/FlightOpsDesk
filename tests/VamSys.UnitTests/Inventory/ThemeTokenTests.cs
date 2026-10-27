using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace VamSys.UnitTests.Inventory;

// Theme.xaml is the one place for sizes, spacing and brush aliases. A key the app asks for but the dictionary
// lacks would only fail when that page opens, so the sources are checked here.
public partial class ThemeTokenTests
{
    static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    static string App => Path.Combine(SourceScan.Src, "VamSys.App");
    static XDocument Theme() => XDocument.Load(Path.Combine(App, "Theme.xaml"));

    static HashSet<string> TopLevelKeys(XDocument theme)
        => theme.Root!.Elements().Select(e => (string?)e.Attribute(X + "Key")).OfType<string>().ToHashSet(StringComparer.Ordinal);

    static Dictionary<string, HashSet<string>> ThemeKeys(XDocument theme)
        => theme.Root!.Elements().Single(e => e.Name.LocalName == "ResourceDictionary.ThemeDictionaries").Elements()
            .ToDictionary(d => (string)d.Attribute(X + "Key")!, d => d.Elements().Select(e => (string)e.Attribute(X + "Key")!).ToHashSet(StringComparer.Ordinal));

    [GeneratedRegex("Get<\\w+>\\((?:compact \\? )?\"(\\w+)\"(?: : \"(\\w+)\")?\\)")]
    private static partial Regex TokenRead();

    [GeneratedRegex("\\{StaticResource (\\w+)\\}")]
    private static partial Regex StaticReference();

    [Fact]
    public void Light_dark_and_high_contrast_define_the_same_brush_aliases()
    {
        var themes = ThemeKeys(Theme());
        Assert.Equal(["Default", "HighContrast", "Light"], themes.Keys.Order(StringComparer.Ordinal));
        Assert.All(themes.Values, keys => Assert.Equal(themes["Default"].Order(StringComparer.Ordinal), keys.Order(StringComparer.Ordinal)));
    }

    [Fact]
    public void Every_token_the_code_reads_is_defined()
    {
        var theme = Theme();
        var defined = TopLevelKeys(theme);
        defined.UnionWith(ThemeKeys(theme)["Default"]);
        var read = TokenRead().Matches(SourceScan.Read(Path.Combine(App, "UiTokens.cs")))
            .SelectMany(m => new[] { m.Groups[1].Value, m.Groups[2].Value }).Where(k => k.Length > 0).ToList();
        Assert.NotEmpty(read);
        Assert.All(read, key => Assert.Contains(key, defined));
    }

    [Fact]
    public void Every_static_resource_in_the_app_is_a_theme_token()
    {
        var defined = TopLevelKeys(Theme());
        var used = SourceScan.XamlFiles(App).Concat(SourceScan.CsFiles(App))
            .SelectMany(file => StaticReference().Matches(SourceScan.Read(file)).Select(m => (File: SourceScan.Relative(file), Key: m.Groups[1].Value)))
            .ToList();
        Assert.NotEmpty(used);
        Assert.All(used, use => Assert.True(defined.Contains(use.Key), $"{use.File} uses {use.Key}, which Theme.xaml does not define"));
    }
}
