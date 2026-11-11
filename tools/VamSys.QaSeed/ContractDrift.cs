using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

// 4.3: whether the official OpenAPI documents changed since the copies pinned in docs/contracts (manifest.json holds their
// source URL and SHA-256). Read only: it fetches each source once with a plain GET, compares, and reports. It never replaces
// the pinned copy or the contract the adapter embeds; a changed document is saved under artifacts/contract-drift for review.
public static class ContractDrift
{
    // What the comparison leaves out, as in the 2026-10-20 comparison (API-CONTRACTS.md): descriptions, examples, defaults and titles.
    static readonly HashSet<string> TextKeys = new(StringComparer.Ordinal) { "description", "summary", "example", "examples", "default", "title" };
    static readonly string[] Methods = ["get", "put", "post", "delete", "patch", "head", "options", "trace"];

    public sealed record Report(string Name, string PinnedHash, string CurrentHash)
    {
        public bool HashChanged => !string.Equals(PinnedHash, CurrentHash, StringComparison.OrdinalIgnoreCase);
        public List<string> Header { get; } = [];
        public List<string> AddedOperations { get; } = [];
        public List<string> RemovedOperations { get; } = [];
        // Operation → the parts that differ (parameters, requestBody, responses, ...).
        public SortedDictionary<string, List<string>> ChangedOperations { get; } = new(StringComparer.Ordinal);
        public List<string> AddedComponents { get; } = [];
        public List<string> RemovedComponents { get; } = [];
        public List<string> ChangedComponents { get; } = [];
        // Operations whose own text did not change but that reference a changed or removed component, directly or not.
        public SortedDictionary<string, List<string>> AffectedOperations { get; } = new(StringComparer.Ordinal);
        public bool StructureChanged => Header.Count + AddedOperations.Count + RemovedOperations.Count + ChangedOperations.Count
            + AddedComponents.Count + RemovedComponents.Count + ChangedComponents.Count > 0;
    }

    public static string Sha256(byte[] content) => Convert.ToHexString(SHA256.HashData(content));

    public static Report Compare(string name, byte[] pinned, byte[] current)
    {
        var report = new Report(name, Sha256(pinned), Sha256(current));
        if (!report.HashChanged)
            return report;
        var before = JsonNode.Parse(pinned)!.AsObject();
        var after = JsonNode.Parse(current)!.AsObject();

        foreach (var key in new[] { "openapi" })
            if (Text(before[key]) != Text(after[key]))
                report.Header.Add($"{key}: {Text(before[key])} → {Text(after[key])}");
        if (Text(before["info"]?["version"]) != Text(after["info"]?["version"]))
            report.Header.Add($"info.version: {Text(before["info"]?["version"])} → {Text(after["info"]?["version"])}");
        foreach (var key in new[] { "servers", "security" })
            if (Canonical(before[key]) != Canonical(after[key]))
                report.Header.Add($"{key} changed");

        var componentsBefore = Components(before);
        var componentsAfter = Components(after);
        report.AddedComponents.AddRange(componentsAfter.Keys.Except(componentsBefore.Keys).Order(StringComparer.Ordinal));
        report.RemovedComponents.AddRange(componentsBefore.Keys.Except(componentsAfter.Keys).Order(StringComparer.Ordinal));
        report.ChangedComponents.AddRange(componentsBefore.Keys.Intersect(componentsAfter.Keys)
            .Where(k => Canonical(componentsBefore[k]) != Canonical(componentsAfter[k])).Order(StringComparer.Ordinal));

        var operationsBefore = Operations(before);
        var operationsAfter = Operations(after);
        report.AddedOperations.AddRange(operationsAfter.Keys.Except(operationsBefore.Keys).Order(StringComparer.Ordinal));
        report.RemovedOperations.AddRange(operationsBefore.Keys.Except(operationsAfter.Keys).Order(StringComparer.Ordinal));
        var touched = report.ChangedComponents.Concat(report.RemovedComponents).ToHashSet(StringComparer.Ordinal);
        foreach (var key in operationsBefore.Keys.Intersect(operationsAfter.Keys))
        {
            var a = operationsBefore[key];
            var b = operationsAfter[key];
            var parts = a.Select(p => p.Key).Union(b.Select(p => p.Key))
                .Where(part => !TextKeys.Contains(part) && Canonical(a[part]) != Canonical(b[part])).Order(StringComparer.Ordinal).ToList();
            if (parts.Count > 0)
            {
                report.ChangedOperations[key] = parts;
                continue;
            }
            var reached = Reachable(a, componentsBefore).Where(touched.Contains).Order(StringComparer.Ordinal).ToList();
            if (reached.Count > 0)
                report.AffectedOperations[key] = reached;
        }
        return report;
    }

    public static IEnumerable<string> Format(Report report)
    {
        if (!report.HashChanged)
        {
            yield return $"{report.Name}: unchanged (SHA-256 {report.PinnedHash})";
            yield break;
        }
        yield return $"{report.Name}: CHANGED (pinned {report.PinnedHash}, now {report.CurrentHash})";
        if (!report.StructureChanged)
        {
            yield return "  Only descriptions, examples, defaults or titles differ; operations and schemas are the same.";
            yield break;
        }
        foreach (var line in report.Header)
            yield return "  " + line;
        foreach (var (title, items) in new[] { ("Added operations", report.AddedOperations), ("Removed operations", report.RemovedOperations),
            ("Added components", report.AddedComponents), ("Removed components", report.RemovedComponents), ("Changed components", report.ChangedComponents) })
        {
            if (items.Count == 0)
                continue;
            yield return $"  {title} ({items.Count}):";
            foreach (var item in items)
                yield return "    " + item;
        }
        foreach (var (title, items) in new[] { ("Changed operations", report.ChangedOperations), ("Operations using a changed component", report.AffectedOperations) })
        {
            if (items.Count == 0)
                continue;
            yield return $"  {title} ({items.Count}):";
            foreach (var (operation, parts) in items)
                yield return $"    {operation}: {string.Join(", ", parts)}";
        }
    }

    // The command: fetch each manifest source, compare with the pinned copy, print the report. 0 = all unchanged,
    // 1 = some document changed (saved under artifacts/contract-drift), 2 = the check could not run.
    public static async Task<int> RunAsync(string repository, HttpMessageHandler? handler = null, TextWriter? output = null)
    {
        output ??= Console.Out;
        var contracts = Path.Combine(repository, "docs", "contracts");
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(contracts, "manifest.json")))!;
        using var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.Timeout = TimeSpan.FromSeconds(30);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("FlightOpsDesk-contract-check");
        var changed = false;
        foreach (var entry in manifest["contracts"]!.AsArray())
        {
            var name = Text(entry!["name"]);
            var source = new Uri(Text(entry["source"]));
            if (source.Scheme != Uri.UriSchemeHttps || !string.Equals(source.Host, "vamsys.io", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Refusing contract source outside https://vamsys.io: {source}");
            var pinned = await File.ReadAllBytesAsync(Path.Combine(contracts, name + ".json"));
            var recorded = Text(entry["sha256"]);
            if (!string.Equals(Sha256(pinned), recorded, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"docs/contracts/{name}.json does not match its SHA-256 in manifest.json.");
            using var response = await http.GetAsync(source);
            response.EnsureSuccessStatusCode();
            var current = await response.Content.ReadAsByteArrayAsync();
            var report = Compare(name, pinned, current);
            foreach (var line in Format(report))
                await output.WriteLineAsync(line);
            if (!report.HashChanged)
                continue;
            changed = true;
            var saved = Path.Combine(repository, "artifacts", "contract-drift", $"{name}-{DateTime.UtcNow:yyyyMMddTHHmmssZ}.json");
            Directory.CreateDirectory(Path.GetDirectoryName(saved)!);
            await File.WriteAllBytesAsync(saved, current);
            await output.WriteLineAsync($"  Fetched copy saved to {Path.GetRelativePath(repository, saved).Replace('\\', '/')}");
        }
        return changed ? 1 : 0;
    }

    static string Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var s) ? s : node?.ToJsonString() ?? "";

    // "components/schemas/Name" → the component, for every component kind.
    static Dictionary<string, JsonNode?> Components(JsonObject document)
    {
        var result = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        if (document["components"] is JsonObject components)
            foreach (var (kind, group) in components)
                if (group is JsonObject members)
                    foreach (var (member, node) in members)
                        result[$"components/{kind}/{member}"] = node;
        return result;
    }

    // "POST /airports" → the operation, with the path-level parameters folded in so a change there shows on each operation.
    static Dictionary<string, JsonObject> Operations(JsonObject document)
    {
        var result = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        if (document["paths"] is not JsonObject paths)
            return result;
        foreach (var (path, item) in paths)
        {
            if (item is not JsonObject members)
                continue;
            foreach (var method in Methods)
                if (members[method] is JsonObject operation)
                {
                    var copy = operation.DeepClone().AsObject();
                    if (members["parameters"] is { } shared)
                        copy["pathParameters"] = shared.DeepClone();
                    result[$"{method.ToUpperInvariant()} {path}"] = copy;
                }
        }
        return result;
    }

    // The components an operation references, following references inside components too.
    static HashSet<string> Reachable(JsonNode operation, Dictionary<string, JsonNode?> components)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<JsonNode?>([operation]);
        while (pending.Count > 0)
            foreach (var reference in References(pending.Pop()))
                if (seen.Add(reference) && components.TryGetValue(reference, out var target))
                    pending.Push(target);
        return seen;
    }

    static IEnumerable<string> References(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject members:
                foreach (var (key, value) in members)
                {
                    if (key == "$ref" && Text(value) is var reference && reference.StartsWith("#/", StringComparison.Ordinal))
                        yield return reference[2..];
                    foreach (var inner in References(value))
                        yield return inner;
                }
                break;
            case JsonArray items:
                foreach (var item in items)
                    foreach (var inner in References(item))
                        yield return inner;
                break;
        }
    }

    // The node as sorted, compact JSON without the text-only keys, so key order and wording do not count as change.
    static string Canonical(JsonNode? node)
    {
        var builder = new StringBuilder();
        Write(node, builder, null);
        return builder.ToString();
    }

    // Under "properties" the keys are field names (a resource may well have a "title" or "description" field), so all are kept.
    static void Write(JsonNode? node, StringBuilder builder, string? parent)
    {
        switch (node)
        {
            case null:
                builder.Append("null");
                break;
            case JsonObject members:
                builder.Append('{');
                var first = true;
                foreach (var (key, value) in members.Where(p => parent == "properties" || !TextKeys.Contains(p.Key)).OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    if (!first)
                        builder.Append(',');
                    first = false;
                    builder.Append(JsonSerializer.Serialize(key)).Append(':');
                    Write(value, builder, parent == "properties" ? null : key);
                }
                builder.Append('}');
                break;
            case JsonArray items:
                builder.Append('[');
                for (var i = 0; i < items.Count; i++)
                {
                    if (i > 0)
                        builder.Append(',');
                    Write(items[i], builder, null);
                }
                builder.Append(']');
                break;
            default:
                builder.Append(node.ToJsonString());
                break;
        }
    }
}
