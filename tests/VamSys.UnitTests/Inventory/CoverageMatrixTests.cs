using System.Text.RegularExpressions;

namespace VamSys.UnitTests.Inventory;

// The requirement coverage matrix (docs/TEST-COVERAGE.md, R7 step 1) names the tests that cover each requirement. Every
// requirement row must name at least one test, and every test, class, window test and window script it names must exist, so the
// matrix cannot drift from the tests it describes.
public partial class CoverageMatrixTests
{
    static readonly string Matrix = Path.Combine(Golden.RepoRoot, "docs", "TEST-COVERAGE.md");

    // Short prefixes used in the matrix, longest first.
    static readonly (string Short, string Full)[] Prefixes =
    [
        ("VamSys.UnitTests.", "VamSys.UnitTests."), ("VamSys.IntegrationTests.", "VamSys.IntegrationTests."), ("VamSys.UiTests.", "VamSys.UiTests."),
        ("UnitTests.", "VamSys.UnitTests."), ("IntegrationTests.", "VamSys.IntegrationTests."), ("UiTests.", "VamSys.UiTests."),
        ("Domain.", "VamSys.UnitTests.Domain."), ("Application.", "VamSys.UnitTests.Application."), ("Transport.", "VamSys.UnitTests.Transport."),
        ("Inventory.", "VamSys.UnitTests.Inventory."), ("Api.", "VamSys.UnitTests.Api."), ("Review.", "VamSys.UnitTests.Review."),
        ("Presentation.", "VamSys.UnitTests.Presentation."), ("Storage.", "VamSys.UnitTests.Storage.")
    ];

    // Requirements that so far have only command-line measurements (the benchmarks). R7 gives
    // them tests; once one has a test it must leave this list.
    static readonly HashSet<string> KnownGaps = ["I14", "J07"];

    [GeneratedRegex(@"(?<![\w.])((?:VamSys\.)?(?:UnitTests|IntegrationTests|UiTests|Domain|Application|Transport|Inventory|Api|Review|Presentation|Storage)(?:\.[A-Za-z_]\w*)+\*?)(?:\.\{([^}]*)\})?")]
    private static partial Regex Reference();
    [GeneratedRegex(@"\btest-[a-z0-9-]+-ui\b")]
    private static partial Regex Script();
    [GeneratedRegex(@"^namespace\s+([\w.]+)\s*;", RegexOptions.Multiline)]
    private static partial Regex Namespace();
    [GeneratedRegex(@"\b(?:class|record)\s+(\w+)")]
    private static partial Regex ClassName();
    [GeneratedRegex(@"\[(?:Fact|Theory|WindowFact|WindowTheory)\b[^\n]*\n(?:\s*\[[^\n]*\n)*\s*public\s+(?:async\s+)?(?:void|Task)\s+(\w+)\s*\(")]
    private static partial Regex TestMethod();

    // Test classes by full name, with their test methods. A file's test methods belong to the classes it declares
    // (test files declare one test class; helper types in the same file only widen the match).
    static Dictionary<string, HashSet<string>> TestClasses()
    {
        var classes = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var project in new[] { "VamSys.UnitTests", "VamSys.IntegrationTests", "VamSys.UiTests" })
            foreach (var file in SourceScan.CsFiles(Path.Combine(Golden.RepoRoot, "tests", project)))
            {
                var text = SourceScan.Read(file);
                if (Namespace().Match(text) is not { Success: true } ns)
                    continue;
                var methods = TestMethod().Matches(text).Select(m => m.Groups[1].Value).ToList();
                foreach (Match type in ClassName().Matches(text))
                {
                    var name = ns.Groups[1].Value + "." + type.Groups[1].Value;
                    if (!classes.TryGetValue(name, out var set))
                        classes[name] = set = new(StringComparer.Ordinal);
                    set.UnionWith(methods);
                }
            }
        return classes;
    }

    static string Expand(string reference)
    {
        foreach (var (prefix, full) in Prefixes)
            if (reference.StartsWith(prefix, StringComparison.Ordinal))
                return full + reference[prefix.Length..];
        return reference;
    }

    // Requirement rows of sections 3.1–3.3: the requirement and its "new tests" and "window and other checks" cells.
    static IEnumerable<(string Requirement, string Tests, string Other)> Rows()
    {
        var section = "";
        foreach (var line in SourceScan.Read(Matrix).Split('\n'))
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
                section = line;
            if (!section.StartsWith("## 3.", StringComparison.Ordinal) || !Regex.IsMatch(line, @"^\| [FIJ]\d\d "))
                continue;
            var cells = line.Split('|');
            yield return (cells[1].Trim(), cells[3], cells[4]);
        }
    }

    // Every class, method and wildcard a cell names, with what is wrong with it (null when it exists).
    static IEnumerable<(string Name, string? Problem)> Resolve(string cell, Dictionary<string, HashSet<string>> classes)
    {
        foreach (Match match in Reference().Matches(cell))
        {
            var full = Expand(match.Groups[1].Value);
            if (match.Groups[2].Success)
            {
                if (!classes.TryGetValue(full, out var methods))
                { yield return (full, "class not found"); continue; }
                foreach (var item in match.Groups[2].Value.Split([',', '，'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    var method = Regex.Match(item, @"^\w+\*?").Value;
                    var found = method.EndsWith('*') ? methods.Any(m => m.StartsWith(method[..^1], StringComparison.Ordinal)) : methods.Contains(method);
                    yield return (full + "." + method, found ? null : "method not found");
                }
            }
            else if (classes.ContainsKey(full))
                yield return (full, null);
            else
            {
                var split = full.LastIndexOf('.');
                var owner = full[..split];
                var member = full[(split + 1)..];
                var found = classes.TryGetValue(owner, out var methods) && (member.EndsWith('*')
                    ? methods.Any(m => m.StartsWith(member[..^1], StringComparison.Ordinal)) : methods.Contains(member));
                yield return (full, found ? null : methods is null ? "class or method not found" : "method not found");
            }
        }
    }

    [Fact]
    public void Every_requirement_names_at_least_one_test()
    {
        var classes = TestClasses();
        var rows = Rows().ToList();
        Assert.True(rows.Count >= 48, $"Only {rows.Count} requirement rows were read from the matrix");
        var uncovered = rows.Where(r => !Resolve(r.Tests, classes).Any(n => n.Problem is null)).Select(r => r.Requirement.Split(' ')[0]).ToHashSet();
        Assert.True(uncovered.SetEquals(KnownGaps),
            "Requirements without a test that exists: " + string.Join(", ", uncovered.Order()) + "; known gaps: " + string.Join(", ", KnownGaps.Order())
            + ". Give a new gap a test, or remove a covered requirement from KnownGaps.");
    }

    [Fact]
    public void Every_test_class_method_and_window_script_the_matrix_names_exists()
    {
        var classes = TestClasses();
        var problems = new List<string>();
        foreach (var (requirement, tests, other) in Rows())
        {
            var id = requirement.Split(' ')[0];
            problems.AddRange(Resolve(tests + other, classes).Where(n => n.Problem != null).Select(n => $"{id}: {n.Name} ({n.Problem})"));
            foreach (Match script in Script().Matches(tests + other))
                if (!File.Exists(Path.Combine(Golden.RepoRoot, "scripts", script.Value + ".ps1")))
                    problems.Add($"{id}: scripts/{script.Value}.ps1 (script not found)");
        }
        Assert.True(problems.Count == 0, "The matrix names what does not exist:\n" + string.Join("\n", problems.Distinct()));
    }
}
