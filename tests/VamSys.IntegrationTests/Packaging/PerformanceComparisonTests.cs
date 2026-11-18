using static PerformanceComparison;

namespace VamSys.IntegrationTests.Packaging;

// I14: the automatic comparison with the R0 baseline (tools/VamSys.QaSeed --compare-performance), on the archived R0 and R8
// measurements (Fixtures/performance, trimmed copies). Nothing here measures.
public sealed class PerformanceComparisonTests : IDisposable
{
    static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures", "performance");
    static readonly string R0 = Path.Combine(Fixtures, "r0"), R8 = Path.Combine(Fixtures, "r8");
    readonly string scratch = Path.Combine(Path.GetTempPath(), "vamsys-perf-compare-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(scratch))
            Directory.Delete(scratch, recursive: true);
    }

    static Result CompareDirectories(string baseline, string current, double limit = DefaultLimitPercent) => Compare(Read([baseline]), Read([current]), limit);

    // The command as the program runs it: exit code, standard output and standard error.
    static (int Code, string Output, string Error) Command(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = Run(args, output, error);
        return (code, output.ToString(), error.ToString());
    }

    static IEnumerable<string> Ids(Result result, Verdict verdict) => result.With(verdict).Select(r => r.Id).Order(StringComparer.Ordinal);

    // A copy of a fixture side without some of its files, as a run that skipped those measurements.
    string CopyWithout(string source, params string[] left)
    {
        var target = Path.Combine(scratch, Guid.NewGuid().ToString("N"));
        foreach (var file in Directory.EnumerateFiles(source, "*.json", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file).Replace('\\', '/');
            if (left.Any(l => relative.StartsWith(l, StringComparison.Ordinal)))
                continue;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(target, relative))!);
            File.Copy(file, Path.Combine(target, relative));
        }
        return target;
    }

    [Fact]
    public void The_R8_measurements_exceed_the_R0_baseline_exactly_where_the_hand_comparison_found_it()
    {
        var result = CompareDirectories(R0, R8);

        Assert.Equal(Exceeded, result.ExitCode);
        // The three bold items of the R8 table in PERFORMANCE.md, and the 20,000-row redo P95, which that table did not list.
        Assert.Equal(["baseline-50000:review-1000-check-stall", "data-20000:open", "data-20000:redo-p95", "data-20000:undo-p95"], Ids(result, Verdict.Exceeded));
        Assert.Empty(result.With(Verdict.Missing));
        // The core benchmark: 45 cases, none of the ones from 1 ms up more than 10% slower; the 28 under 1 ms are not judged.
        var core = result.Rows.Where(r => r.Id.StartsWith("core:", StringComparison.Ordinal)).ToList();
        Assert.Equal(45, core.Count);
        Assert.Equal(28, core.Count(r => r.Verdict == Verdict.NotJudged));
        Assert.All(core, r => Assert.NotEqual(Verdict.Exceeded, r.Verdict));
        // The R8 run also measured 20,000 rows in the window baseline; R0 has no such run: listed, not judged.
        Assert.Equal(21, result.With(Verdict.New).Count());
        Assert.All(result.With(Verdict.New), r => Assert.StartsWith("baseline-20000:", r.Id, StringComparison.Ordinal));

        // The values agree with the R8 table (rounded as there).
        double Value(string id, bool baseline) => Math.Round((baseline ? result.Rows.Single(r => r.Id == id).Baseline : result.Rows.Single(r => r.Id == id).Current)!.Value);
        Assert.Equal((313, 420), (Value("data-20000:open", true), Value("data-20000:open", false)));
        Assert.Equal((2102, 2084), (Value("baseline-50000:startup-median", true), Value("baseline-50000:startup-median", false)));
        Assert.Equal((998, 948), (Value("baseline-50000:workspace-load-median", true), Value("baseline-50000:workspace-load-median", false)));
        Assert.Equal((37, 123), (Value("baseline-50000:review-1000-check-stall", true), Value("baseline-50000:review-1000-check-stall", false)));
        Assert.Equal((519, 487), (Value("baseline-50000:memory-dataPage", true), Value("baseline-50000:memory-dataPage", false)));
        Assert.Equal((385, 169), (Value("core:plan:50000:100:0", true), Value("core:plan:50000:100:0", false)));
    }

    [Fact]
    public void The_same_measurements_on_both_sides_pass()
    {
        var result = CompareDirectories(R8, R8);
        Assert.Equal(Passed, result.ExitCode);
        Assert.All(result.Rows, r => Assert.Contains(r.Verdict, new[] { Verdict.Pass, Verdict.NotJudged }));
    }

    [Fact]
    public void Items_the_current_run_did_not_measure_are_missing()
    {
        var current = CopyWithout(R8, "data-performance-50000/", "core-load.json");
        var result = CompareDirectories(R8, current);

        Assert.Equal(Missing, result.ExitCode);
        var missing = result.With(Verdict.Missing).ToList();
        Assert.Equal(10 + 3, missing.Count);
        Assert.All(missing, r => Assert.Null(r.Current));
        Assert.Equal(10, missing.Count(r => r.Id.StartsWith("data-50000:", StringComparison.Ordinal)));
        Assert.Equal(3, missing.Count(r => r.Id.StartsWith("core:load-20:", StringComparison.Ordinal)));

        // Something slower as well: that decides the exit code.
        Assert.Equal(Exceeded, CompareDirectories(R0, CopyWithout(R8, "data-performance-50000/")).ExitCode);
    }

    [Fact]
    public void The_limit_is_more_than_ten_percent_slower_and_can_be_changed()
    {
        static IReadOnlyDictionary<string, Metric> One(double value, double floor = 0) => new Dictionary<string, Metric> { ["x"] = new("x", "x", value, "ms", floor) };
        Assert.Equal(Verdict.Pass, Compare(One(100), One(110)).Rows.Single().Verdict);
        Assert.Equal(Verdict.Exceeded, Compare(One(100), One(110.01)).Rows.Single().Verdict);
        Assert.Equal(Verdict.Pass, Compare(One(100), One(50)).Rows.Single().Verdict);
        Assert.Equal(Verdict.NotJudged, Compare(One(0.9, 1), One(5)).Rows.Single().Verdict);
        Assert.Equal(Verdict.Exceeded, Compare(One(1, 1), One(1.2)).Rows.Single().Verdict);

        // The R8 run's worst item is 235% slower: with a limit of 300% nothing exceeds it.
        Assert.Equal(Passed, CompareDirectories(R0, R8, 300).ExitCode);
    }

    [Fact]
    public void The_command_prints_each_item_and_answers_with_its_exit_code()
    {
        var (code, text, error) = Command("--baseline", R0, "--current", R8);
        Assert.Equal(Exceeded, code);
        Assert.Contains("| 超过 10% | 数据页 20000 行：打开数据页 | 313 ms | 420 ms | +34.2% |", text, StringComparison.Ordinal);
        Assert.Contains("| 通过 | 数据页 50000 行：打开数据页 |", text, StringComparison.Ordinal);
        Assert.Contains("| 不判定（基线不足 1 ms） | 核心基准 warm-index（1000 行，0%，K=100）中位数 |", text, StringComparison.Ordinal);
        Assert.Contains("共 107 项：通过 54，超过 10% 4，缺项 0，不判定 28，新增 21。", text, StringComparison.Ordinal);
        Assert.Empty(error);

        // Several paths per side: the core benchmark given as its own file, as --benchmark-core writes it anywhere.
        var withoutCore = CopyWithout(R8, "core.json");
        Assert.Equal(Passed, Command("--baseline", R8, "--current", withoutCore, "--current", Path.Combine(R8, "core.json")).Code);

        (code, text, _) = Command("--baseline", R8, "--current", withoutCore);
        Assert.Equal(Missing, code);
        Assert.Contains("| 缺项 | 核心基准 plan（1000 行，0%，K=0）中位数 | 0.329 ms | — | — |", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Paths_that_cannot_be_compared_end_with_exit_code_2()
    {
        var (code, _, error) = Command("--baseline", Path.Combine(scratch, "nowhere"), "--current", R8);
        Assert.Equal(CannotCompare, code);
        Assert.Contains("找不到", error, StringComparison.Ordinal);

        Directory.CreateDirectory(scratch);
        File.WriteAllText(Path.Combine(scratch, "other.json"), """{"rows": 5, "something": 1}""");
        File.WriteAllText(Path.Combine(scratch, "broken.json"), "{ not json");
        (code, _, error) = Command("--baseline", scratch, "--current", R8);
        Assert.Equal(CannotCompare, code);
        Assert.Contains("基线路径里没有可识别的测量结果", error, StringComparison.Ordinal);

        // Two runs of the same measurement on one side: which one is meant is not guessed.
        (code, _, error) = Command("--baseline", R0, "--current", R8, "--current", Path.Combine(R8, "core.json"));
        Assert.Equal(CannotCompare, code);
        Assert.Contains("测了两次", error, StringComparison.Ordinal);

        Assert.Equal(CannotCompare, Command("--baseline", R0).Code);
        Assert.Equal(CannotCompare, Command("--baseline", R0, "--current", R8, "--limit", "-1").Code);
    }
}
