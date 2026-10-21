using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using VamSys.App;
using VamSys.Core;
using VamSys.Infrastructure;

namespace VamSys.IntegrationTests.RealData;

/// <summary>
/// Read-only regression check against a COPY of a real data directory. Skipped unless VAMSYS_REAL_DB_DIR is set.
/// VAMSYS_REAL_DB_EXPECTED names the expected summary (kept outside the repository); VAMSYS_UPDATE_GOLDEN=1 writes it.
/// The summary holds counts and SHA-256 hashes only: no names, ids, credentials or ciphertext. No request is ever sent.
/// </summary>
public class RealDatabaseCopyTests
{
    const string DirVar = "VAMSYS_REAL_DB_DIR", ExpectedVar = "VAMSYS_REAL_DB_EXPECTED", UpdateVar = "VAMSYS_UPDATE_GOLDEN";
    public static bool Enabled => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(DirVar));

    [Fact(SkipUnless = nameof(Enabled), SkipType = typeof(RealDatabaseCopyTests), Skip = DirVar + " is not set")]
    public void Copy_loads_with_the_same_summary_as_the_expected_file()
    {
        var dir = Path.GetFullPath(Environment.GetEnvironmentVariable(DirVar)!);
        var expectedPath = Environment.GetEnvironmentVariable(ExpectedVar);
        Assert.False(string.IsNullOrWhiteSpace(expectedPath), ExpectedVar + " must name the expected summary file.");
        Assert.True(File.Exists(Path.Combine(dir, "workspaces.db")), "workspaces.db is missing from the copied directory.");

        var actual = BuildSummary(dir).ToJsonString(new JsonSerializerOptions { WriteIndented = true }).ReplaceLineEndings("\n") + "\n";
        if (Environment.GetEnvironmentVariable(UpdateVar) == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(expectedPath!))!);
            File.WriteAllText(expectedPath!, actual, new UTF8Encoding(false));
            return;
        }
        Assert.True(File.Exists(expectedPath), "Expected summary not found; run once with " + UpdateVar + "=1.");
        var expected = File.ReadAllText(expectedPath!).ReplaceLineEndings("\n");
        if (expected != actual) Assert.Fail("Real-data summary changed:\n" + Diff(expected, actual));
    }

    static JsonObject BuildSummary(string dir)
    {
        var store = new WorkspaceStore(dir);
        var ids = store.List().Select(w => w.Id).ToList();
        var raw = ReadRaw(dir);
        var planner = new ChangePlanner();
        var summary = new JsonObject { ["schemaUserVersion"] = raw.UserVersion, ["workspaceCount"] = ids.Count };
        var workspaces = new List<(string Hash, JsonObject Node)>();
        foreach (var id in ids)
        {
            var w = store.Load(id);
            var hash = Sha(w.Id.ToString()); var node = new JsonObject
            {
                ["id"] = hash, ["name"] = Sha(w.Name), ["mode"] = w.Mode.ToString(),
                ["hasCredentials"] = raw.Secrets.Contains(w.Id.ToString()) || !string.IsNullOrEmpty(w.ClientId),
                ["hasAirlineId"] = !string.IsNullOrEmpty(w.AirlineId),
                ["contractHash"] = Sha(w.ContractHash ?? ""), ["verifiedOperations"] = w.VerifiedOperations.Count,
            };
            var resources = new JsonObject();
            foreach (var (kind, data) in w.Resources.OrderBy(p => (int)p.Key))
            {
                raw.Rows.TryGetValue((w.Id.ToString(), (int)kind), out var rows);
                resources[kind.ToString()] = new JsonObject
                {
                    ["snapshot"] = data.Snapshot.Count, ["draft"] = data.Draft.Count, ["columns"] = data.Columns.Count,
                    ["conflicts"] = data.Conflicts.Count, ["undo"] = data.Undo.Count, ["redo"] = data.Redo.Count,
                    ["snapshotRowsSha256"] = RowsHash(rows?.Snapshot), ["draftRowsSha256"] = RowsHash(rows?.Draft),
                };
            }
            node["resources"] = resources;

            var jobs = new JsonArray(); var open = new JsonArray();
            foreach (var job in w.Jobs)
            {
                var jobHash = Sha(job.Id.ToString());
                var states = new JsonObject();
                foreach (var g in job.Items.GroupBy(i => i.State).OrderBy(g => (int)g.Key)) states[g.Key.ToString()] = g.Count();
                jobs.Add(new JsonObject { ["id"] = jobHash, ["status"] = job.StatusCode.ToString(), ["items"] = job.Items.Count, ["byState"] = states });
                for (var n = 0; n < job.Items.Count; n++)
                    if (job.Items[n].State is ItemState.Pending or ItemState.Unknown or ItemState.Running)
                        open.Add(new JsonObject { ["job"] = jobHash, ["index"] = n, ["resource"] = job.Items[n].Resource.ToString(), ["kind"] = job.Items[n].Kind.ToString(), ["state"] = job.Items[n].State.ToString() });
            }
            node["jobCount"] = w.Jobs.Count; node["jobs"] = jobs; node["openItems"] = open;

            // Local preview only: never connected, no preview callback, so ReviewPlanBuilder cannot send a request.
            var changes = planner.PlanWorkspace(w);
            node["plannedChanges"] = changes.Count;
            node["reviewAsStored"] = Plan(ReviewPlanBuilder.Build(changes, w.Mode, false, new HashSet<string>(), null, Blockers(w, false), w.ContractHash));
            node["reviewAsDemo"] = Plan(ReviewPlanBuilder.Build(changes, RunMode.Demo, false, new HashSet<string>(), null, Blockers(w, true), w.ContractHash));
            workspaces.Add((hash, node));
        }
        var array = new JsonArray();
        foreach (var (_, node) in workspaces.OrderBy(x => x.Hash, StringComparer.Ordinal)) array.Add(node);
        summary["workspaces"] = array;
        return summary;
    }

    static List<MessageDescriptor> Blockers(Workspace w, bool validate)
    {
        var blockers = new List<MessageDescriptor>();
        if (w.Resources.Values.Any(d => d.Conflicts.Count > 0)) blockers.Add(Messages.Define("ApiConflict"));
        if (validate)
            foreach (var (kind, data) in w.Resources.Where(p => p.Value.Draft.Count > 0))
                foreach (var issue in Schemas.Validate(w, kind))
                {
                    var row = data.FindDraftRow(issue.RowId);
                    blockers.Add(Messages.Define("ReviewValidationIssue", ("resource", Schemas.All[kind].Name),
                        ("key", row is null ? issue.RowId.ToString()[..8] : Schemas.Key(kind, row)), ("field", issue.Field), ("reason", issue.Description)));
                }
        return blockers;
    }

    static JsonObject Plan(WorkspaceReviewPlan plan) => new()
    {
        ["contentKey"] = plan.ContentKey(), ["entries"] = plan.Entries.Count, ["blockers"] = plan.Blockers.Count,
        ["submit"] = plan.SubmitItems.Count,
        ["ready"] = plan.Entries.Count(e => e.Disposition == ReviewDisposition.Ready),
        ["deferred"] = plan.Entries.Count(e => e.Disposition == ReviewDisposition.Deferred),
        ["blocked"] = plan.Entries.Count(e => e.Disposition == ReviewDisposition.Blocked),
        ["awaitingConnection"] = plan.Entries.Count(e => e.Disposition == ReviewDisposition.AwaitingConnection),
    };

    sealed record RawRows(List<string> Snapshot, List<string> Draft);
    sealed record RawDb(long UserVersion, HashSet<string> Secrets, Dictionary<(string, int), RawRows> Rows);

    // A second, read-only connection to the copy: row JSON exactly as stored, in stored order. Secret values are never selected.
    static RawDb ReadRaw(string dir)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(dir, "workspaces.db"), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        c.Open();
        long Scalar(string sql) { using var q = c.CreateCommand(); q.CommandText = sql; return Convert.ToInt64(q.ExecuteScalar()); }
        var secrets = new HashSet<string>();
        using (var q = c.CreateCommand()) { q.CommandText = "SELECT id FROM secrets"; using var r = q.ExecuteReader(); while (r.Read()) secrets.Add(r.GetString(0)); }
        var rows = new Dictionary<(string, int), RawRows>();
        using (var q = c.CreateCommand())
        {
            q.CommandText = "SELECT workspace_id,resource,collection,json FROM resource_rows ORDER BY workspace_id,resource,collection,ordinal";
            using var r = q.ExecuteReader();
            while (r.Read())
            {
                var key = (r.GetString(0), r.GetInt32(1));
                if (!rows.TryGetValue(key, out var set)) rows[key] = set = new([], []);
                (r.GetInt32(2) == 0 ? set.Snapshot : set.Draft).Add(r.GetString(3));
            }
        }
        return new(Scalar("PRAGMA user_version"), secrets, rows);
    }

    static string RowsHash(List<string>? rows)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var json in rows ?? []) { var b = Encoding.UTF8.GetBytes(json); h.AppendData(BitConverter.GetBytes(b.Length)); h.AppendData(b); }
        return Convert.ToHexString(h.GetHashAndReset());
    }

    static string Sha(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    static string Diff(string expected, string actual)
    {
        var a = expected.Split('\n'); var b = actual.Split('\n'); var sb = new StringBuilder(); var shown = 0;
        for (var i = 0; i < Math.Max(a.Length, b.Length) && shown < 60; i++)
        {
            var x = i < a.Length ? a[i] : "<missing>"; var y = i < b.Length ? b[i] : "<missing>";
            if (x == y) continue;
            sb.Append($"line {i + 1}\n  expected: {x.Trim()}\n  actual:   {y.Trim()}\n"); shown++;
        }
        return sb.ToString();
    }
}
