using System.Text.RegularExpressions;

namespace VamSys.UnitTests.Inventory;

// The regression ledger (docs/REGRESSION-LEDGER.md, R7 step 5) gives each fixed problem its test. Every row must name at
// least one test and everything it names must exist, so a test cannot be renamed or deleted away from the problem it guards.
public class RegressionLedgerTests
{
    static readonly string Ledger = Path.Combine(Golden.RepoRoot, "docs", "REGRESSION-LEDGER.md");

    static IEnumerable<(string Id, string Tests)> Rows() =>
        SourceScan.Read(Ledger).Split('\n').Where(l => Regex.IsMatch(l, @"^\| R\d\d ")).Select(l => l.Split('|')).Select(c => (c[1].Trim(), c[4]));

    [Fact]
    public void Every_fixed_problem_names_a_test_that_exists()
    {
        var classes = CoverageMatrixTests.TestClasses();
        var rows = Rows().ToList();
        Assert.True(rows.Count >= 40, $"Only {rows.Count} rows were read from the ledger");
        var problems = new List<string>();
        foreach (var (id, tests) in rows)
        {
            var names = CoverageMatrixTests.Resolve(tests, classes).ToList();
            if (names.Count == 0)
                problems.Add($"{id}: names no test");
            problems.AddRange(names.Where(n => n.Problem != null).Select(n => $"{id}: {n.Name} ({n.Problem})"));
        }
        Assert.True(problems.Count == 0, "The ledger names what does not exist:\n" + string.Join("\n", problems));
    }

    [Fact]
    public void Row_numbers_are_unique()
    {
        var ids = Rows().Select(r => r.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }
}
