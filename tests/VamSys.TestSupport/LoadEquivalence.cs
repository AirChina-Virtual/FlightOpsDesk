using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using VamSys.Core;
using VamSys.Infrastructure;

namespace VamSys.TestSupport;

// Compares what a workspace read produces in two builds of the program. --load-equivalence-seed writes a set of
// databases once (each in its own data directory); --load-equivalence copies each directory, reads every workspace in it
// and writes, per workspace, what the read must keep exactly: the serialized workspace (rows, identities, raw API JSON,
// columns, conflicts, snapshot time, history, jobs), the stored revision, the journal baseline (the plan of a save
// right after the read: which rows it would write and why), the history's unsaved changes, which draft rows are the
// snapshot's row object (81fae24) and whether every field name is the shared instance. Large values are hashed. Run with
// the program before and after a change against the same seeded directories; the files must be identical. What is
// expected to change (how many distinct string instances the values use) goes to a separate .info.json file.
// (2026-11-23, stage 7 item 3: workspace load.)
public static class LoadEquivalence
{
    static Guid Id(int n) => new(n, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1);

    static DataRow Row(int n, params (string Field, string Value)[] fields)
    {
        var builder = new DataRow.Builder(Id(n));
        foreach (var (field, value) in fields) builder[field] = value;
        return builder.ToRow();
    }

    // Everything a row and a workspace can hold that the row reader must reproduce: identities with and without a parent
    // fleet, raw API JSON, imported columns the field table does not know, values that need escaping, non-ASCII text,
    // empty values, a draft in another order than the snapshot, deleted and created rows, edits, conflicts, undo and redo
    // steps, a task with items, an online header.
    static Workspace Edge()
    {
        var w = new Workspace { Id = Id(900), Name = "edge \"quoted\" 名称", Mode = RunMode.Online, AirlineId = "7", InstanceUrl = "https://example.test/" };
        var fleets = w.Resources[ResourceKind.Fleets];
        for (var n = 0; n < 6; n++)
        {
            var row = Row(100 + n, ("ID", (10 + n).ToString()), ("Name", n switch { 0 => "Boeing \"737\"", 1 => "line\nbreak\ttab", 2 => "中文名称 ✈ 😀", 3 => "back\\slash </script>", 4 => "", _ => "plain" }),
                ("Type Code", "B738"), ("Type (pax/cargo/...)", "pax"), ("Max Passengers", "189"), ("Imported Only Column", "x" + n), ("另一个导入列", n % 2 == 0 ? "" : "值"));
            row = row.WithIdentity(new ResourceIdentity((500 + n).ToString(), "7", n % 2 == 0 ? null : "3"));
            if (n % 3 == 0) row = row.WithRawApiJson("{\"id\":" + (500 + n) + ",\"name\":\"raw \\\"" + n + "\\\"\"}");
            fleets.Snapshot.Add(row);
        }
        fleets.Columns = ["ID", "Name", "Type Code", "Type (pax/cargo/...)", "Max Passengers", "Imported Only Column", "另一个导入列"];
        fleets.SnapshotAt = new DateTimeOffset(2026, 11, 20, 8, 30, 0, TimeSpan.FromHours(8));
        // Draft: unchanged rows in another order, one edited, one deleted, one created.
        fleets.Draft.AddRange([fleets.Snapshot[2], fleets.Snapshot[0], fleets.Snapshot[1].With("Name", "edited 编辑"), fleets.Snapshot[4], fleets.Snapshot[5],
            Row(199, ("Name", "created"), ("Type Code", "A320"), ("Imported Only Column", "new"))]);
        fleets.Conflicts.Add(new(fleets.Snapshot[1].LocalId, "Name", "line\nbreak\ttab", "edited 编辑", "server 值"));
        var airports = w.Resources[ResourceKind.Airports];
        foreach (var (code, n) in new[] { ("ZBAA", 0), ("ZSPD", 1), ("EGLL", 2) })
        {
            var row = Row(300 + n, ("ICAO/IATA", code), ("Name", "Airport " + code));
            airports.Snapshot.Add(row.WithIdentity(new ResourceIdentity((700 + n).ToString(), "7")));
            airports.Draft.Add(airports.Snapshot[^1]);
        }
        // Undo and redo steps on the airports, the last undo moved to redo.
        for (var n = 0; n < 3; n++)
        {
            var row = airports.Draft[n];
            airports.Edit([row.LocalId], () => airports.ReplaceDraftRows([row.With("Name", "step " + n)]));
        }
        airports.UndoEdit();
        w.Jobs.Add(new() { Mode = RunMode.Online, Items = new ChangePlanner().Plan(ResourceKind.Fleets, fleets) });
        return w;
    }

    public static void Seed(string directory)
    {
        Directory.CreateDirectory(directory);
        foreach (var count in new[] { 20000, 50000 })
            DataUiFixture.Seed(Path.Combine(directory, "routes-" + count), count);
        var mixed = Path.Combine(directory, "mixed");
        var store = new WorkspaceStore(mixed);
        store.Save(Edge());
        store.Save(ReviewEquivalence.Mixed(RunMode.Online));
        var demo = ReviewEquivalence.Mixed(RunMode.Demo);
        store.Save(new Workspace { Id = Id(901), Name = demo.Name + " demo", Mode = demo.Mode, AirlineId = demo.AirlineId, Resources = demo.Resources });
        store.Save(new Workspace { Id = Id(902), Name = "empty" });
        // A task checkpoint or an older program can leave gaps in the stored positions; the read marks those rows for a rewrite.
        var gaps = Path.Combine(directory, "gaps");
        new WorkspaceStore(gaps).Save(Edge());
        using (var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(gaps, "workspaces.db"), Pooling = false }.ToString()))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE resource_rows SET ordinal=ordinal+100 WHERE resource=$r AND collection=1; UPDATE resource_rows SET ordinal=(ordinal-100)*2 WHERE resource=$r AND collection=1";
            cmd.Parameters.AddWithValue("$r", (int)ResourceKind.Fleets);
            cmd.ExecuteNonQuery();
        }
        // Old databases, read after their upgrade.
        var legacy = Path.Combine(directory, "legacy");
        Directory.CreateDirectory(legacy);
        StorageScenarios.Legacy(legacy, StorageScenarios.Fixture()).Dispose();
        var fixedLegacy = Path.Combine(directory, "legacy-v1");
        Directory.CreateDirectory(fixedLegacy);
        StorageScenarios.FixedLegacy(fixedLegacy, 1);
        // The upgrade gives converted history steps new IDs; it runs here once, so every run reads the same upgraded file.
        foreach (var upgraded in new[] { legacy, fixedLegacy })
            _ = new WorkspaceStore(upgraded).List();
        foreach (var seeded in Directory.GetDirectories(directory))
            ClearPool(seeded);
        Console.WriteLine($"Seeded the load equivalence databases in {directory}");
    }

    static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    static object Plan(Workspace w)
    {
        var plan = w.Journal().Plan(w, RowSerializer.HeaderKey(w), w.StorageRevision, JournalScope.All);
        if (plan is null)
            return new { Tracking = w.Journal().IsTracking(w.StorageRevision), FullSave = true };
        static object List(RowListChange l) => new
        {
            Mode = l.Mode.ToString(), Deleted = l.Deleted.Select(i => i.ToString()), Moved = l.Moved.Select(m => $"{m.Id}@{m.Ordinal}"),
            Replaced = l.Replaced.Select(p => $"{p.Row.LocalId}@{p.Ordinal}"), Added = l.Added.Select(p => $"{p.Row.LocalId}@{p.Ordinal}"), All = l.All.Count
        };
        return new
        {
            Tracking = w.Journal().IsTracking(w.StorageRevision), plan.IsEmpty, plan.HeaderChanged, Jobs = plan.Jobs.Mode.ToString(),
            Resources = plan.Resources.Select(r => new { Kind = r.Kind.ToString(), Snapshot = List(r.Snapshot), Draft = List(r.Draft), r.MetadataChanged })
        };
    }

    // A history step's row images keep their fields in an ImmutableDictionary, whose order (and so its JSON) depends on the
    // process's string hashing; FieldOrder holds the row's real order. Their fields are written sorted, everything else as is.
    static string Step(HistoryStep step)
    {
        static void Sort(System.Text.Json.Nodes.JsonNode? node)
        {
            if (node is System.Text.Json.Nodes.JsonArray array)
                foreach (var item in array) Sort(item);
            if (node is not System.Text.Json.Nodes.JsonObject o)
                return;
            if (o["FieldOrder"] is not null && o["Fields"] is System.Text.Json.Nodes.JsonObject fields)
            {
                var sorted = fields.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => (p.Key, Value: p.Value?.DeepClone())).ToList();
                fields.Clear();
                foreach (var (key, value) in sorted) fields[key] = value;
            }
            foreach (var (_, value) in o.ToList()) Sort(value);
        }
        var node = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(step));
        Sort(node);
        return node!.ToJsonString();
    }

    // The workspace as the database format holds it: header, jobs, and per resource its rows in order, columns, conflicts,
    // snapshot time and history steps.
    static string Serialize(Workspace w)
    {
        var text = new StringBuilder();
        text.Append(RowSerializer.Encode(w, true)).Append('\n').Append(JsonSerializer.Serialize(w.Jobs)).Append('\n');
        foreach (var (kind, data) in w.Resources.OrderBy(p => p.Key))
        {
            text.Append(kind).Append('\n').Append(JsonSerializer.Serialize(data.Snapshot)).Append('\n').Append(JsonSerializer.Serialize(data.Draft)).Append('\n')
                .Append(JsonSerializer.Serialize(data.Columns)).Append('\n').Append(JsonSerializer.Serialize(data.Conflicts)).Append('\n')
                .Append(JsonSerializer.Serialize(data.SnapshotAt)).Append('\n');
            foreach (var step in data.Undo) text.Append("undo ").Append(Step(step)).Append('\n');
            foreach (var step in data.Redo) text.Append("redo ").Append(Step(step)).Append('\n');
        }
        return text.ToString();
    }

    static object Describe(Workspace w)
    {
        var serialized = Serialize(w);
        var resources = new SortedDictionary<string, object>(StringComparer.Ordinal);
        foreach (var (kind, data) in w.Resources.OrderBy(p => p.Key))
        {
            var snapshot = new Dictionary<DataRow, int>(ReferenceEqualityComparer.Instance);
            for (var n = 0; n < data.Snapshot.Count; n++) snapshot.TryAdd(data.Snapshot[n], n);
            var shared = new StringBuilder();
            var sharedCount = 0;
            for (var n = 0; n < data.Draft.Count; n++)
                if (snapshot.TryGetValue(data.Draft[n], out var at)) { shared.Append(n).Append('=').Append(at).Append(';'); sharedCount++; }
            var changes = data.History.Changes();
            resources[kind.ToString()] = new
            {
                Snapshot = data.Snapshot.Count, Draft = data.Draft.Count, Undo = data.Undo.Count, Redo = data.Redo.Count,
                HistoryUnsaved = new { Upserts = changes.Upserts.Length, Deletes = changes.Deletes.Length, changes.OrderChanged },
                SharedDraftRows = sharedCount, SharedDraftRowsHash = Hash(shared.ToString()),
                DraftIndex = data.Draft.Count == 0 || ReferenceEquals(data.FindDraftRow(data.Draft[^1].LocalId), data.Draft[^1])
            };
        }
        var names = new HashSet<string>(ReferenceEqualityComparer.Instance);
        var unshared = 0;
        foreach (var data in w.Resources.Values)
            foreach (var row in data.Snapshot.Concat(data.Draft))
                foreach (var pair in row.FieldEntries())
                {
                    names.Add(pair.Key);
                    if (!ReferenceEquals(FieldNames.Share(pair.Key), pair.Key)) unshared++;
                }
        return new
        {
            Id = w.Id.ToString(), w.Name, w.StorageRevision, Mode = w.Mode.ToString(), Jobs = w.Jobs.Count, Items = w.Jobs.Sum(j => j.Items.Count),
            SerializedLength = serialized.Length, SerializedHash = Hash(serialized), Serialized = serialized.Length <= 200_000 ? serialized : null,
            Journal = Plan(w), Resources = resources, FieldNameInstances = names.Count, KnownFieldNamesNotShared = unshared
        };
    }

    static object Values(Workspace w)
    {
        var instances = new HashSet<string>(ReferenceEqualityComparer.Instance);
        var distinct = new HashSet<string>(StringComparer.Ordinal);
        foreach (var data in w.Resources.Values)
            foreach (var row in data.Snapshot.Concat(data.Draft))
                foreach (var pair in row.FieldEntries())
                { instances.Add(pair.Value); distinct.Add(pair.Value); }
        return new { Id = w.Id.ToString(), ValueInstances = instances.Count, DistinctValues = distinct.Count };
    }

    static void ClearPool(string directory)
    {
        using var pooled = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "workspaces.db") }.ToString());
        SqliteConnection.ClearPool(pooled);
    }

    static void Copy(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from))
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
    }

    public static void Run(string seeded, string path)
    {
        var compared = new SortedDictionary<string, object>(StringComparer.Ordinal);
        var info = new SortedDictionary<string, object>(StringComparer.Ordinal);
        foreach (var source in Directory.GetDirectories(seeded).OrderBy(d => d, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(source);
            var copy = Path.Combine(Path.GetTempPath(), "vamsys-load-equivalence-" + Guid.NewGuid().ToString("N"));
            try
            {
                Copy(source, copy);
                var store = new WorkspaceStore(copy);
                var loaded = store.List().OrderBy(x => x.Id).Select(x => store.Load(x.Id)).ToList();
                compared[name] = loaded.Select(Describe).ToList();
                info[name] = loaded.Select(Values).ToList();
                Console.WriteLine($"{name}: {loaded.Count} workspaces, {loaded.Sum(w => w.Resources.Values.Sum(d => d.Snapshot.Count + d.Draft.Count)):N0} rows");
            }
            finally
            {
                ClearPool(copy);
                try { Directory.Delete(copy, true); } catch (IOException) { }
            }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(path, JsonSerializer.Serialize(compared, options));
        File.WriteAllText(Path.ChangeExtension(path, ".info.json"), JsonSerializer.Serialize(info, options));
    }
}
