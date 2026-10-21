using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using VamSys.Core;
using VamSys.Infrastructure;

namespace VamSys.UnitTests.Inventory;

/// <summary>
/// Locks the serialized property names and JSON ignore conditions of the persisted types, as
/// System.Text.Json sees them (JsonSerializerOptions.Default, the options used by WorkspaceStore for rows and jobs).
/// </summary>
public class PersistedJsonShapeTests
{
    static readonly Type[] Roots =
    [
        typeof(Workspace), typeof(ResourceData), typeof(DataRow), typeof(ChangeItem), typeof(BatchJob), typeof(BatchStep),
        typeof(ApiDiagnostic), typeof(HistoryStep), typeof(MergeConflict), typeof(OperationsCredentials)
    ];

    static string Name(Type type)
    {
        if (Nullable.GetUnderlyingType(type) is { } inner) return Name(inner) + "?";
        if (!type.IsGenericType) return type.IsArray ? Name(type.GetElementType()!) + "[]" : type.FullName ?? type.Name;
        var baseName = type.GetGenericTypeDefinition().FullName!;
        baseName = baseName[..baseName.IndexOf('`')];
        return baseName + "<" + string.Join(",", type.GetGenericArguments().Select(Name)) + ">";
    }

    static IEnumerable<Type> Referenced(Type type)
    {
        if (Nullable.GetUnderlyingType(type) is { } inner) { foreach (var t in Referenced(inner)) yield return t; yield break; }
        if (type.IsArray) { foreach (var t in Referenced(type.GetElementType()!)) yield return t; yield break; }
        if (type.IsGenericType)
        {
            foreach (var argument in type.GetGenericArguments()) foreach (var t in Referenced(argument)) yield return t;
            yield break;
        }
        if (type.Assembly.GetName().Name is "VamSys.Core" or "VamSys.Infrastructure" && !type.IsEnum && !typeof(Delegate).IsAssignableFrom(type)) yield return type;
    }

    static List<Type> Reachable()
    {
        var seen = new List<Type>();
        var queue = new Queue<Type>(Roots);
        while (queue.Count > 0)
        {
            var type = queue.Dequeue();
            if (seen.Contains(type)) continue;
            seen.Add(type);
            if (type.GetCustomAttribute<JsonConverterAttribute>() != null)
            {
                // Custom converter: members written by the converter are listed from its sample output instead.
                foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    foreach (var next in Referenced(property.PropertyType)) queue.Enqueue(next);
                continue;
            }
            foreach (var property in JsonSerializerOptions.Default.GetTypeInfo(type).Properties)
                foreach (var next in Referenced(property.PropertyType)) queue.Enqueue(next);
        }
        return seen;
    }

    static IEnumerable<string> DescribeType(Type type, JsonSerializerOptions options, string prefix = "")
    {
        if (type.GetCustomAttribute<JsonConverterAttribute>() is { } converter)
        {
            yield return $"{prefix}{Name(type)} => custom converter {Name(converter.ConverterType!)}";
            yield break;
        }
        var info = options.GetTypeInfo(type);
        yield return $"{prefix}{Name(type)} => kind {info.Kind}";
        foreach (var property in info.Properties)
        {
            var member = property.AttributeProvider as MemberInfo;
            var ignore = member?.GetCustomAttribute<JsonIgnoreAttribute>()?.Condition ?? JsonIgnoreCondition.Never;
            var access = (property.Get != null ? "get" : "") + (property.Set != null ? "/set" : "");
            yield return $"{prefix}{Name(type)}.{property.Name} => {Name(property.PropertyType)}; {access}; ignore={ignore}";
        }
        foreach (var skipped in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .Where(p => p.GetCustomAttribute<JsonIgnoreAttribute>() is { Condition: JsonIgnoreCondition.Always }))
            yield return $"{prefix}{Name(type)}.{skipped.Name} => not serialized (JsonIgnore Always)";
    }

    [Fact]
    public void Serialized_property_names_and_ignore_conditions_are_locked()
    {
        var lines = Reachable().SelectMany(t => DescribeType(t, JsonSerializerOptions.Default)).ToList();
        Assert.Contains(lines, l => l.StartsWith("VamSys.Core.ChangeItem.RequiresAirlineParameter => System.Boolean; get/set; ignore=WhenWritingDefault"));
        Golden.Verify("JsonShape.txt", lines);
    }

    [Fact]
    public void Header_options_without_collections_are_locked()
    {
        // WorkspaceStore stores Workspace and BatchJob headers without Jobs/Resources/Items.
        var field = typeof(WorkspaceStore).GetField("withoutCollections", BindingFlags.NonPublic | BindingFlags.Static);
        var lines = new List<string>();
        if (field?.GetValue(null) is JsonSerializerOptions options)
            foreach (var type in new[] { typeof(Workspace), typeof(BatchJob) })
                foreach (var property in options.GetTypeInfo(type).Properties) lines.Add($"{Name(type)}.{property.Name} => header property");
        else lines.Add("WorkspaceStore.withoutCollections => not found");
        Golden.Verify("JsonHeaderShape.txt", lines);
    }

    static void Walk(JsonElement element, string path, ISet<string> paths)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                paths.Add($"{path} : object");
                foreach (var property in element.EnumerateObject()) Walk(property.Value, path + "." + property.Name, paths);
                break;
            case JsonValueKind.Array:
                paths.Add($"{path} : array");
                foreach (var item in element.EnumerateArray()) Walk(item, path + "[]", paths);
                break;
            case JsonValueKind.True: case JsonValueKind.False:
                paths.Add($"{path} : bool");
                break;
            default:
                paths.Add($"{path} : {element.ValueKind.ToString().ToLowerInvariant()}");
                break;
        }
    }

    static ChangeItem SampleItem()
    {
        var row = new DataRow { Identity = new ResourceIdentity("7", "conn", "3"), RawApiJson = "{}" };
        row.Fields["Name"] = "A";
        var item = new ChangeItem
        {
            Resource = ResourceKind.Routes, Kind = ChangeKind.Update, Before = row.Copy(), After = row,
            State = ItemState.Unknown, RemoteId = "7", RequiresAirlineParameter = true, RecoveryCandidateId = "9",
            PreCreateCandidateIds = ["1"], Diagnostic = "d",
            ApiExpectedValues = new() { ["name"] = JsonSerializer.SerializeToElement("A") },
            ApiSteps = [new BatchStep { Method = "PUT", Path = "p", Body = new() { ["name"] = JsonSerializer.SerializeToElement(1) }, State = BatchStepState.InFlight, StartedAt = DateTimeOffset.UnixEpoch, FinishedAt = DateTimeOffset.UnixEpoch, DeadlineAt = DateTimeOffset.UnixEpoch, RemoteId = "7" }],
            RecoveryCandidates = [new RecoveryCandidateAttempt("9", DateTimeOffset.UnixEpoch)],
            DiagnosticDetails = new ApiDiagnostic { Method = "GET", FieldErrors = new() { ["f"] = ["e"] }, TraceHeaders = new() { ["h"] = "v" }, ServerDate = DateTimeOffset.UnixEpoch },
            ReadbackDetails = new ApiDiagnostic(),
            DeletionVerification = new ApiDeletionVerification("o", DateTimeOffset.UnixEpoch, 200, 404),
            ManualResolution = new ApiManualResolution("s", DateTimeOffset.UnixEpoch, 404, false),
            PreflightComparison = new ApiSnapshotComparison("a", "b", [new ApiFieldDifference("f", "x", "y")]),
            VerificationDifferences = [new ApiFieldDifference("f", "x", "y")],
        };
        item.Fields["Name"] = new FieldChange(FieldIntent.Set, "A");
        item.SetMessage(Messages.Define("ApiUnknown", ("n", 1), ("t", "x")));
        return item;
    }

    [Fact]
    public void Sample_serialization_paths_are_locked()
    {
        var item = SampleItem();
        var job = new BatchJob { Mode = RunMode.Online, Items = [item] };
        job.SetStatus(JobStatus.NeedsAttention, Messages.Define("ApiUnknown"));
        var workspace = new Workspace { Mode = RunMode.Online, VerifiedOperations = ["Routes:Read"], Jobs = [job] };
        var resource = workspace.Resources[ResourceKind.Fleets];
        resource.Snapshot.Add(item.After.Copy()); resource.Draft.Add(item.After.Copy());
        resource.Columns.Add("Name"); resource.SnapshotAt = DateTimeOffset.UnixEpoch;
        resource.Conflicts.Add(new MergeConflict(Guid.Empty, "Name", "a", "b", "c"));

        var paths = new SortedSet<string>(StringComparer.Ordinal);
        Walk(JsonDocument.Parse(workspace.Serialize()).RootElement, "$", paths);
        // Numeric enums: ItemState/ChangeKind/ResourceKind/RunMode/JobStatus are numbers, never strings.
        Assert.Contains("$.Jobs[].Items[].State : number", paths);
        Assert.Contains("$.Jobs[].StatusCode : number", paths);
        Golden.Verify("JsonSamplePaths.txt", paths);
    }
}
