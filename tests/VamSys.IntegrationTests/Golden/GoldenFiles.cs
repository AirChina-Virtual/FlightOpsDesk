using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace VamSys.IntegrationTests.Golden;

/// <summary>
/// Compares produced text or bytes with committed files under Golden/&lt;area&gt;/Expected/&lt;folder&gt;.
/// VAMSYS_UPDATE_GOLDEN=1 rewrites the folder. A session owns its folder: files it did not produce are reported (or removed on update).
/// </summary>
public sealed class GoldenSession
{
    readonly string folder;
    readonly List<string> failures = [];
    readonly HashSet<string> produced = new(StringComparer.Ordinal);

    public static bool Update => Environment.GetEnvironmentVariable("VAMSYS_UPDATE_GOLDEN") == "1";

    public GoldenSession(string area, string folder)
    {
        this.folder = Path.Combine(FindGoldenRoot(), area, "Expected", folder);
        if (Update)
        {
            if (Directory.Exists(this.folder)) Directory.Delete(this.folder, true);
            Directory.CreateDirectory(this.folder);
        }
    }

    static string FindGoldenRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "VamSys.slnx")))
                return Path.Combine(dir.FullName, "tests", "VamSys.IntegrationTests", "Golden");
        throw new InvalidOperationException("Repository root (VamSys.slnx) not found above " + AppContext.BaseDirectory);
    }

    public void CheckText(string name, string actual) => CheckBytes(name, new UTF8Encoding(false).GetBytes(actual.Replace("\r\n", "\n")), "\n");

    public void CheckBytes(string name, byte[] actual, string? _ = null)
    {
        if (!produced.Add(name)) { failures.Add($"{name}: produced twice in one session"); return; }
        var path = Path.Combine(folder, name);
        if (Update) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, actual); return; }
        if (!File.Exists(path)) { failures.Add($"{name}: expected file is missing (run with VAMSYS_UPDATE_GOLDEN=1 to create it)"); return; }
        var expected = File.ReadAllBytes(path);
        if (expected.AsSpan().SequenceEqual(actual)) return;
        failures.Add($"{name}: differs from the committed golden ({expected.Length} bytes expected, {actual.Length} actual)\n{Diff(Visible(expected), Visible(actual))}");
    }

    public void Finish()
    {
        if (!Update && Directory.Exists(folder))
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(folder, file).Replace('\\', '/');
                if (!produced.Contains(relative)) failures.Add($"{relative}: committed golden was not produced by the test (stale file)");
            }
        if (!Update && !Directory.Exists(folder) && produced.Count > 0) failures.Add($"golden folder {folder} does not exist");
        if (failures.Count > 0)
            Xunit.Assert.Fail($"{failures.Count} golden mismatch(es) in {folder}\n\n" + string.Join("\n\n", failures.Take(12)) + (failures.Count > 12 ? $"\n\n... and {failures.Count - 12} more" : ""));
    }

    static string Visible(byte[] bytes) => new UTF8Encoding(false).GetString(bytes).Replace("\r", "\\r");

    // Line diff reduced to the changed middle block, with a little context.
    static string Diff(string expected, string actual)
    {
        var a = expected.Split('\n'); var b = actual.Split('\n');
        var start = 0;
        while (start < a.Length && start < b.Length && a[start] == b[start]) start++;
        var endA = a.Length; var endB = b.Length;
        while (endA > start && endB > start && a[endA - 1] == b[endB - 1]) { endA--; endB--; }
        var sb = new StringBuilder();
        sb.AppendLine($"  first difference at line {start + 1}");
        for (var i = Math.Max(0, start - 2); i < start; i++) sb.AppendLine("    " + Clip(a[i]));
        foreach (var line in a.Skip(start).Take(Math.Min(endA - start, 25))) sb.AppendLine("  - " + Clip(line));
        if (endA - start > 25) sb.AppendLine($"  - ... {endA - start - 25} more expected line(s)");
        foreach (var line in b.Skip(start).Take(Math.Min(endB - start, 25))) sb.AppendLine("  + " + Clip(line));
        if (endB - start > 25) sb.AppendLine($"  + ... {endB - start - 25} more actual line(s)");
        return sb.ToString().TrimEnd();
    }
    static string Clip(string line) => line.Length > 240 ? line[..240] + "..." : line;
}

/// <summary>
/// Replaces values that change between runs by stable placeholders. Identity is preserved: the same GUID (or the same
/// wall-clock instant) always becomes the same placeholder, numbered by first appearance. Instants in 1970 come from
/// the virtual test clock and are deterministic, so they stay visible.
/// </summary>
public sealed partial class Masker
{
    readonly Dictionary<string, string> guids = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string> instants = new(StringComparer.Ordinal);

    [GeneratedRegex(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex GuidPattern();
    [GeneratedRegex(@"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:\d{2})")]
    private static partial Regex InstantPattern();

    public string Mask(string text)
    {
        text = GuidPattern().Replace(text, m => guids.TryGetValue(m.Value, out var p) ? p : guids[m.Value] = $"<guid-{guids.Count + 1:00}>");
        return InstantPattern().Replace(text, m => m.Value.StartsWith("1970-", StringComparison.Ordinal) ? m.Value
            : instants.TryGetValue(m.Value, out var p) ? p : instants[m.Value] = $"<time-{instants.Count + 1:00}>");
    }

    public static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        NewLine = "\n",
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Masks and re-indents JSON text. Property order is kept exactly as stored.</summary>
    public string MaskJson(string json) => Prepare(JsonNode.Parse(json))!.ToJsonString(Pretty) + "\n";

    /// <summary>Normalises, then masks, a parsed document (see <see cref="Normalize"/>).</summary>
    public JsonNode? Prepare(JsonNode? node) => MaskNode(Normalize(node));

    /// <summary>
    /// Undo/redo row images keep their fields in an ImmutableDictionary, whose enumeration order depends on the process-wide
    /// randomised string hash, so "Fields" is serialised in a different order on every run. The sibling "FieldOrder" array
    /// records the real order; "Fields" is rewritten in that order (any key missing from it follows, sorted). Nothing is dropped.
    /// </summary>
    static JsonNode? Normalize(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var key in o.Select(p => p.Key).ToList()) Normalize(o[key]);
                if (o["Fields"] is JsonObject fields && o["FieldOrder"] is JsonArray order)
                {
                    var ordered = order.Select(n => n!.GetValue<string>()).ToList();
                    var rest = fields.Select(p => p.Key).Except(ordered).Order(StringComparer.Ordinal);
                    var entries = ordered.Concat(rest).Where(k => fields.ContainsKey(k)).Select(k => (k, fields[k]!.DeepClone())).ToList();
                    fields.Clear();
                    foreach (var (k, v) in entries) fields[k] = v;
                }
                return o;
            case JsonArray a:
                foreach (var child in a) Normalize(child);
                return a;
            default:
                return node;
        }
    }

    // Masks decoded string values, so escaped characters such as "+" in an instant cannot hide it.
    JsonNode? MaskNode(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var key in o.Select(p => p.Key).ToList())
                    if (o[key] is { } child && MaskNode(child) is { } masked && !ReferenceEquals(child, masked)) o[key] = masked;
                return o;
            case JsonArray a:
                for (var i = 0; i < a.Count; i++)
                    if (a[i] is { } child && MaskNode(child) is { } masked && !ReferenceEquals(child, masked)) a[i] = masked;
                return a;
            case JsonValue v when v.TryGetValue<string>(out var s):
                return JsonValue.Create(Mask(s));
            default:
                return node;
        }
    }
}
