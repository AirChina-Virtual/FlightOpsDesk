using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

// I14: compares measurements of the current build with the R0 baseline taken on the same machine, item by item, with the
// limit "no more than 10% slower" (docs/PERFORMANCE.md). It reads only files that were already measured; it never measures.
// Recognised files (any name, found in the given files or directories, searched recursively):
//  - the data page's benchmark.json (UiTests.DataPerformanceTests, R0 test-data-performance-ui): "rows", "opened", "benchmark";
//  - baseline.json (UiTests.BaselineMeasurements, R0 test-baseline-ui): "rows", "startup", "workspaceLoad";
//  - the core benchmark (--benchmark-core, --benchmark-core-load): "Cases".
// Every value is a time or a memory size, so lower is better. A core case whose baseline median is under 1 ms is listed but
// not judged (as in the R8 comparison: timer noise dominates there).
public static class PerformanceComparison
{
    public const int Passed = 0, Exceeded = 1, CannotCompare = 2, Missing = 3;
    public const double DefaultLimitPercent = 10;
    const double CoreFloorMs = 1;

    // JudgedFrom: a baseline under this value is listed but not judged. Order: the order it was read in (the report keeps it).
    public sealed record Metric(string Id, string Label, double Value, string Unit, double JudgedFrom = 0, string Source = "", int Order = 0);

    public enum Verdict { Pass, Exceeded, Missing, NotJudged, New }

    public sealed record Row(string Id, string Label, string Unit, double? Baseline, double? Current, Verdict Verdict)
    {
        public double? ChangePercent => Baseline is > 0 && Current is { } now ? (now / Baseline.Value - 1) * 100 : null;
    }

    public sealed record Result(IReadOnlyList<Row> Rows, int ExitCode)
    {
        public IEnumerable<Row> With(Verdict verdict) => Rows.Where(r => r.Verdict == verdict);
    }

    // Reads every recognised measurement under the paths. Throws InvalidDataException when a path does not exist or the
    // same item is measured twice (two runs in one folder: point at one of them).
    public static IReadOnlyDictionary<string, Metric> Read(IEnumerable<string> paths)
    {
        var metrics = new Dictionary<string, Metric>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            IEnumerable<string> files = File.Exists(path) ? [path]
                : Directory.Exists(path) ? Directory.EnumerateFiles(path, "*.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
                : throw new InvalidDataException($"找不到 {path}");
            foreach (var file in files)
            {
                JsonNode? root;
                try { root = JsonNode.Parse(File.ReadAllText(file)); }
                catch (JsonException) { continue; }
                if (root is not JsonObject json)
                    continue;
                foreach (var metric in FromJson(json))
                {
                    var located = metric with { Source = file, Order = metrics.Count };
                    if (metrics.TryGetValue(metric.Id, out var earlier))
                        throw new InvalidDataException($"{metric.Label} 测了两次：{earlier.Source} 和 {file}。每一侧只给一次运行的结果。");
                    metrics[metric.Id] = located;
                }
            }
        }
        return metrics;
    }

    // The metrics one file holds, or none when it is not a recognised measurement.
    public static IEnumerable<Metric> FromJson(JsonObject json)
    {
        if (json["Cases"] is JsonArray cases)
            return Core(cases);
        if (json["rows"] is not { } rowsNode || !TryNumber(rowsNode, out var rowCount))
            return [];
        var rows = (int)rowCount;
        if (json["opened"] is JsonObject && json["benchmark"] is JsonObject)
            return DataPage(json, rows);
        if (json["startup"] is JsonArray && json["workspaceLoad"] is JsonArray)
            return Baseline(json, rows);
        return [];
    }

    static IEnumerable<Metric> Core(JsonArray cases)
    {
        foreach (var item in cases.OfType<JsonObject>())
        {
            var name = item["Name"]?.GetValue<string>();
            if (name is null || item["Metrics"]?["MedianMs"] is not { } median || !TryNumber(median, out var ms))
                continue;
            var rows = item["Rows"]?.GetValue<int>() ?? 0;
            var percent = item["Percent"]?.GetValue<int>() ?? 0;
            var k = item["K"]?.GetValue<int>() ?? 0;
            yield return new($"core:{name}:{rows}:{percent}:{k}", $"核心基准 {name}（{rows} 行，{percent}%，K={k}）中位数", ms, "ms", CoreFloorMs);
        }
    }

    static IEnumerable<Metric> DataPage(JsonObject json, int rows)
    {
        var prefix = $"data-{rows}:";
        var label = $"数据页 {rows} 行：";
        var found = new List<Metric>();
        void Add(string id, string text, JsonNode? node, string unit = "ms", double scale = 1)
        {
            if (node is not null && TryNumber(node, out var value))
                found.Add(new(prefix + id, label + text, value * scale, unit));
        }
        Add("open", "打开数据页", json["opened"]?["milliseconds"]);
        Add("search", "全字段搜索", json["search"]?["milliseconds"]);
        Add("paging-p95", "翻页 P95", json["benchmark"]?["paging"]?["p95"]);
        Add("language-p95", "切换语言 P95", json["benchmark"]?["language"]?["p95"]);
        Add("edit-p95", "单格编辑 P95", json["benchmark"]?["edit"]?["p95"]);
        Add("undo-median", "带保存的撤销，中位数", json["undoWithSave"]?["totalMedian"]);
        Add("undo-p95", "带保存的撤销，P95", json["undoWithSave"]?["totalP95"]);
        Add("redo-median", "带保存的重做，中位数", json["undoWithSave"]?["redoMedian"]);
        Add("redo-p95", "带保存的重做，P95", json["undoWithSave"]?["redoP95"]);
        Add("working-set", "结束时工作集", json["workingSetBytes"], "MiB", 1.0 / (1024 * 1024));
        return found;
    }

    static IEnumerable<Metric> Baseline(JsonObject json, int rows)
    {
        var prefix = $"baseline-{rows}:";
        var label = $"综合基线 {rows} 行：";
        var found = new List<Metric>();
        void Add(string id, string text, double? value, string unit = "ms")
        {
            if (value is { } v)
                found.Add(new(prefix + id, label + text, v, unit));
        }
        // R0 wrote the startup times directly in each launch, the current build under "startup".
        Add("startup-median", "启动到首帧，中位数", Median(json["startup"]!.AsArray().OfType<JsonObject>()
            .Select(l => l["startup"]?["firstFrameMilliseconds"] ?? l["firstFrameMilliseconds"])));
        Add("workspace-load-median", "读取工作区，中位数", Median(json["workspaceLoad"]!.AsArray().OfType<JsonObject>()
            .SelectMany(l => l["samples"] is JsonArray samples ? samples : [])));
        Add("data-open", "打开数据页", Number(json["dataOpen"]?["milliseconds"]));
        if (json["workspaceSwitch"] is JsonArray switches)
        {
            Add("switch-away-median", "离开工作区，中位数", Median(switches.OfType<JsonObject>().Select(s => s["awayMilliseconds"])));
            Add("switch-back-median", "返回工作区，中位数", Median(switches.OfType<JsonObject>().Select(s => s["backMilliseconds"])));
        }
        foreach (var page in new[] { "page200", "page500" })
            Add($"scroll-{page}-p95", $"{page[4..]} 行页面向下滚动每帧 P95", Number(json["scroll"]?[page]?["down"]?["p95"]));
        if (json["review"] is JsonArray reviews)
            foreach (var review in reviews.OfType<JsonObject>())
            {
                if (review["changed"] is not { } changedNode || !TryNumber(changedNode, out var changed))
                    continue;
                var size = ((int)changed).ToString(CultureInfo.InvariantCulture);
                Add($"review-{size}-plan", $"{size} 项审阅计划，中位数", Median(Values(review["planMilliseconds"])));
                Add($"review-{size}-render", $"{size} 项审阅渲染，中位数", Median(Values(review["renderMilliseconds"])));
                Add($"review-{size}-check", $"{size} 项提交前检查，中位数", Median(Values(review["submitCheckMilliseconds"])));
                Add($"review-{size}-check-stall", $"{size} 项提交前检查最长停顿，中位数", Median(Values(review["submitCheckStallMilliseconds"])));
            }
        foreach (var (key, text) in new[] { ("dataPage", "打开数据页后工作集"), ("review", "审阅页工作集") })
            Add($"memory-{key}", text, Number(json["memory"]?[key]?["workingSetBytes"]) / (1024 * 1024), "MiB");
        return found;
    }

    static IEnumerable<JsonNode?> Values(JsonNode? node) => node is JsonArray array ? array : [];

    static double? Number(JsonNode? node) => node is not null && TryNumber(node, out var value) ? value : null;

    static bool TryNumber(JsonNode node, out double value)
    {
        value = 0;
        return node is JsonValue v && v.GetValueKind() == JsonValueKind.Number && v.TryGetValue(out value);
    }

    static double? Median(IEnumerable<JsonNode?> nodes)
    {
        var values = nodes.Select(Number).OfType<double>().Order().ToArray();
        if (values.Length == 0)
            return null;
        var mid = values.Length / 2;
        return values.Length % 2 == 1 ? values[mid] : (values[mid - 1] + values[mid]) / 2;
    }

    // Item by item: pass, more than the limit slower, missing from the current measurements, not judged (a core case under
    // 1 ms in the baseline), or new (only in the current measurements; listed, never judged).
    public static Result Compare(IReadOnlyDictionary<string, Metric> baseline, IReadOnlyDictionary<string, Metric> current, double limitPercent = DefaultLimitPercent)
    {
        if (baseline.Count == 0)
            return new([], CannotCompare);
        var rows = new List<Row>();
        foreach (var (id, before) in baseline.OrderBy(p => Group(p.Key)).ThenBy(p => p.Value.Order))
        {
            if (!current.TryGetValue(id, out var now))
            { rows.Add(new(id, before.Label, before.Unit, before.Value, null, Verdict.Missing)); continue; }
            var verdict = before.Value < before.JudgedFrom ? Verdict.NotJudged
                : now.Value > before.Value * (1 + limitPercent / 100) ? Verdict.Exceeded : Verdict.Pass;
            rows.Add(new(id, before.Label, before.Unit, before.Value, now.Value, verdict));
        }
        foreach (var (id, now) in current.Where(p => !baseline.ContainsKey(p.Key)).OrderBy(p => Group(p.Key)).ThenBy(p => p.Value.Order))
            rows.Add(new(id, now.Label, now.Unit, null, now.Value, Verdict.New));
        var exit = rows.Any(r => r.Verdict == Verdict.Exceeded) ? Exceeded : rows.Any(r => r.Verdict == Verdict.Missing) ? Missing : Passed;
        return new(rows, exit);
    }

    // The data page first, then the window baseline, then the core benchmark.
    static int Group(string id) => id.StartsWith("data-", StringComparison.Ordinal) ? 0 : id.StartsWith("baseline-", StringComparison.Ordinal) ? 1 : 2;

    public static string Format(Result result, double limitPercent = DefaultLimitPercent)
    {
        var text = new StringBuilder();
        string Value(double? v, string unit) => v is { } x ? x.ToString(x >= 100 ? "0" : "0.0##", CultureInfo.InvariantCulture) + " " + unit : "—";
        string Name(Verdict v) => v switch
        {
            Verdict.Pass => "通过",
            Verdict.Exceeded => $"超过 {limitPercent.ToString(CultureInfo.InvariantCulture)}%",
            Verdict.Missing => "缺项",
            Verdict.NotJudged => "不判定（基线不足 1 ms）",
            _ => "新增（基线没有）"
        };
        text.AppendLine("| 结果 | 项目 | 基线 | 当前 | 变化 |");
        text.AppendLine("|---|---|---:|---:|---:|");
        foreach (var row in result.Rows)
            text.AppendLine($"| {Name(row.Verdict)} | {row.Label} | {Value(row.Baseline, row.Unit)} | {Value(row.Current, row.Unit)} | {(row.ChangePercent is { } c ? c.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture) + "%" : "—")} |");
        text.AppendLine();
        text.AppendLine($"共 {result.Rows.Count} 项：通过 {result.With(Verdict.Pass).Count()}，超过 {limitPercent.ToString(CultureInfo.InvariantCulture)}% {result.With(Verdict.Exceeded).Count()}，缺项 {result.With(Verdict.Missing).Count()}，不判定 {result.With(Verdict.NotJudged).Count()}，新增 {result.With(Verdict.New).Count()}。");
        text.Append(result.ExitCode switch
        {
            Passed => "结论：没有项目比基线慢超过门槛。",
            Exceeded => "结论：有项目比基线慢超过门槛，需逐项查看（单次运行波动较大，可在空闲机器上重测这些项）。",
            Missing => "结论：没有项目超过门槛，但当前测量缺少基线里的项目。",
            _ => "结论：无法比对。"
        });
        return text.ToString();
    }

    // --compare-performance --baseline <路径> [--baseline <路径>] --current <路径> [--current <路径>] [--limit 10]
    public static int Run(IReadOnlyList<string> args, TextWriter output, TextWriter error)
    {
        var baseline = new List<string>();
        var current = new List<string>();
        var limit = DefaultLimitPercent;
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == "--baseline" && i + 1 < args.Count) baseline.Add(args[++i]);
            else if (args[i] == "--current" && i + 1 < args.Count) current.Add(args[++i]);
            else if (args[i] == "--limit" && i + 1 < args.Count && double.TryParse(args[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out var l) && l >= 0) limit = l;
            else { error.WriteLine("未知参数 " + args[i] + "。用法：--compare-performance --baseline <路径> --current <路径> [--limit 10]"); return CannotCompare; }
        }
        if (baseline.Count == 0 || current.Count == 0)
        { error.WriteLine("用法：--compare-performance --baseline <路径> --current <路径> [--limit 10]（每侧可重复给出多个文件或目录）"); return CannotCompare; }
        try
        {
            var before = Read(baseline);
            var now = Read(current);
            if (before.Count == 0)
            { error.WriteLine("基线路径里没有可识别的测量结果。"); return CannotCompare; }
            if (now.Count == 0)
            { error.WriteLine("当前路径里没有可识别的测量结果。"); return CannotCompare; }
            var result = Compare(before, now, limit);
            output.WriteLine(Format(result, limit));
            return result.ExitCode;
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        { error.WriteLine("无法比对：" + e.Message); return CannotCompare; }
    }
}
