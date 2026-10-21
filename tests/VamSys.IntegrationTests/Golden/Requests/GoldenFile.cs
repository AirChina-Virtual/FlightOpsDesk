using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using VamSys.TestSupport;

namespace VamSys.IntegrationTests.Golden.Requests;

/// <summary>
/// A named group of requests, for scenarios that run the same job more than once (resume, recovery).
/// Orders stay global to the server, so the groups together show one continuous sequence.
/// </summary>
public sealed record RequestRun(string Label, IReadOnlyList<RecordedRequest> Requests);

/// <summary>
/// Golden (characterization) files for recorded API request sequences.
/// The file holds, per request, the order, method, path with query and the exact body text that went on the wire.
/// Headers, tokens and timestamps are not part of it. Set VAMSYS_UPDATE_GOLDEN=1 to rewrite the expected files.
/// </summary>
public static class GoldenFile
{
    public const string UpdateVariable = "VAMSYS_UPDATE_GOLDEN";
    const string RelativeExpected = "tests/VamSys.IntegrationTests/Golden/Requests/Expected";

    static readonly JsonSerializerOptions Writer = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        NewLine = "\n"
    };

    /// <summary>Stable text for a request sequence. Same input always gives the same bytes.</summary>
    public static string Serialize(string scenario, IReadOnlyList<RequestRun> runs, IEnumerable<string>? outcomes = null)
    {
        var root = new JsonObject
        {
            ["scenario"] = scenario,
            ["requestCount"] = runs.Sum(r => r.Requests.Count),
            ["outcomes"] = new JsonArray((outcomes ?? []).Select(o => (JsonNode?)JsonValue.Create(o)).ToArray()),
            ["runs"] = new JsonArray(runs.Select(run => (JsonNode?)new JsonObject
            {
                ["label"] = run.Label,
                ["requestCount"] = run.Requests.Count,
                ["requests"] = new JsonArray(run.Requests.Select(Describe).ToArray())
            }).ToArray())
        };
        return root.ToJsonString(Writer) + "\n";
    }

    static JsonNode Describe(RecordedRequest request)
    {
        var item = new JsonObject
        {
            ["order"] = request.Order,
            ["method"] = request.Method,
            ["pathAndQuery"] = request.PathAndQuery,
            // Exact text as sent; bodyJson below is only a readable copy of it.
            ["body"] = request.Body
        };
        if (request.Body is { Length: > 0 })
        {
            try
            { item["bodyJson"] = JsonNode.Parse(request.Body); }
            catch (JsonException) { /* not JSON: the exact text above is all there is */ }
        }
        return item;
    }

    /// <summary>Compare with Expected/&lt;name&gt;.json, or write it when VAMSYS_UPDATE_GOLDEN=1.</summary>
    public static void Verify(string name, string actual)
    {
        var path = Path.Combine(ExpectedDirectory(), name + ".json");
        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, actual, new UTF8Encoding(false));
            return;
        }
        if (!File.Exists(path))
            Assert.Fail($"Golden file missing: {path}\nRun with {UpdateVariable}=1 to create it, then review and commit it.");
        var expected = Normalize(File.ReadAllText(path, Encoding.UTF8));
        actual = Normalize(actual);
        if (expected == actual)
            return;
        Assert.Fail($"Request golden '{name}' differs from {path}\n{Diff(expected, actual)}\nIf the change is intended, regenerate with {UpdateVariable}=1 and review the diff in git.");
    }

    static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    /// <summary>Expected files live in the source tree: found from the output folder by walking up to the solution.</summary>
    public static string ExpectedDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "VamSys.slnx")))
                return Path.Combine(dir.FullName, RelativeExpected.Replace('/', Path.DirectorySeparatorChar));
        throw new DirectoryNotFoundException("VamSys.slnx not found above " + AppContext.BaseDirectory);
    }

    /// <summary>Line diff (longest common subsequence) with a few lines of context, limited in size.</summary>
    public static string Diff(string expected, string actual)
    {
        var a = expected.Split('\n');
        var b = actual.Split('\n');
        var table = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
            for (var j = b.Length - 1; j >= 0; j--)
                table[i, j] = a[i] == b[j] ? table[i + 1, j + 1] + 1 : Math.Max(table[i + 1, j], table[i, j + 1]);
        var ops = new List<(char Op, int Line, string Text)>();
        for (int i = 0, j = 0; i < a.Length || j < b.Length;)
        {
            if (i < a.Length && j < b.Length && a[i] == b[j])
            { ops.Add((' ', i + 1, a[i])); i++; j++; }
            else if (j >= b.Length || (i < a.Length && table[i + 1, j] >= table[i, j + 1]))
            { ops.Add(('-', i + 1, a[i])); i++; }
            else
            { ops.Add(('+', j + 1, b[j])); j++; }
        }
        var changed = ops.Select((o, n) => (o, n)).Where(p => p.o.Op != ' ').Select(p => p.n).ToList();
        var shown = new SortedSet<int>();
        foreach (var n in changed)
            for (var k = Math.Max(0, n - 2); k <= Math.Min(ops.Count - 1, n + 2); k++)
                shown.Add(k);
        var text = new StringBuilder();
        text.AppendLine($"--- expected ({a.Length} lines)  +++ actual ({b.Length} lines)  changed lines: {changed.Count}");
        var last = -2;
        var written = 0;
        foreach (var n in shown)
        {
            if (written++ > 120)
            { text.AppendLine("... diff truncated ..."); break; }
            if (n != last + 1)
                text.AppendLine("@@");
            var (op, line, content) = ops[n];
            text.AppendLine($"{op} {line,4}: {content}");
            last = n;
        }
        return text.ToString();
    }
}
