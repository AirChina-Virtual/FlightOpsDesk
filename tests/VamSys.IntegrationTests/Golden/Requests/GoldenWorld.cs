using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using VamSys.Core;
using VamSys.Infrastructure;
using VamSys.TestSupport;

namespace VamSys.IntegrationTests.Golden.Requests;

/// <summary>
/// A workspace connected to a <see cref="FakeRemote"/> through the real transport, adapter, planner and executor.
/// Load the remote rows once, edit the draft like a user would, then <see cref="RunAsync"/> a complete online job.
/// </summary>
public sealed class GoldenWorld
{
    public readonly FakeRemote Remote = new();
    public readonly Workspace Workspace = new() { AirlineId = "7", Mode = RunMode.Online };
    public readonly OperationsAdapter Api;
    public BatchJob? Job { get; private set; }
    readonly List<(string Label, int Start)> marks = [];
    readonly List<string> notes = [];

    public GoldenWorld(bool standard = true)
    {
        var requests = InstantTime.Requests();
        var tokens = new TokenProvider(_ => Task.FromResult(new AccessToken("golden-token", requests.Clock.GetUtcNow().AddHours(1))), requests.Clock);
        Api = new OperationsAdapter(new OperationsTransport(Remote.Server.CreateClient(), OperationsAdapter.BaseUri, "golden-va", tokens, requests), Workspace);
        if (standard)
        { StandardData(Remote); LoadRemote(); }
    }

    /// <summary>
    /// Airports 101 EGLL/LHR (container 1), 102 EGKK/LGW, 103 EGCC/MAN (unused); fleets 4 and 5 (parameter 1), 6 spare;
    /// aircraft 8 in fleet 4; routing 9; scheduled routes 10 (fleet 4) and 12 (fleets 4,5 with times); jumpseat route 11.
    /// </summary>
    public static void StandardData(FakeRemote r) => r.AddStandardData();

    static ResourceKind? KindOf(string path)
    {
        var parts = path.Split('/');
        return parts[0] switch
        {
            "airports" when parts.Length == 2 => ResourceKind.Airports,
            "fleet" when parts.Length == 2 => ResourceKind.Fleets,
            "fleet" when parts.Length == 4 => ResourceKind.Aircraft,
            "routings" when parts.Length == 2 => ResourceKind.Routings,
            "routes" when parts.Length == 2 => ResourceKind.Routes,
            _ => null
        };
    }

    /// <summary>Put every remote row into the workspace as an unchanged snapshot + draft row (no requests are sent).</summary>
    public void LoadRemote()
    {
        foreach (var kind in Enum.GetValues<ResourceKind>())
            foreach (var (path, json) in Remote.Rows.Where(p => KindOf(p.Key) == kind).OrderBy(p => p.Value["id"]!.GetValue<int>()))
            {
                using var doc = JsonDocument.Parse(json.ToJsonString());
                var row = Api.FromJson(kind, doc.RootElement);
                Workspace.Resources[kind].Snapshot.Add(row.Dup());
                Workspace.Resources[kind].Draft.Add(row.Dup());
            }
    }

    DataRow Draft(ResourceKind kind, string remoteId)
        => Workspace.Resources[kind].Draft.Single(r => r.Identity?.RemoteId == remoteId);

    /// <summary>Change fields of an existing draft row (replacing the row, never editing it in place).</summary>
    public DataRow Edit(ResourceKind kind, string remoteId, params (string Field, string Value)[] edits)
    {
        var copy = Draft(kind, remoteId).With(edits.Select(e => KeyValuePair.Create(e.Field, e.Value)));
        Workspace.Resources[kind].ReplaceDraftRows([copy]);
        return copy;
    }

    public void MarkDelete(ResourceKind kind, string remoteId) => Edit(kind, remoteId, ("_delete", "TRUE"));

    /// <summary>Add a new draft row; the planner turns it into a create.</summary>
    public DataRow New(ResourceKind kind, params (string Field, string Value)[] fields)
    {
        var row = new DataRow { Fields = fields.ToDictionary(p => p.Field, p => p.Value) };
        Workspace.Resources[kind].Draft.Add(row);
        return row;
    }

    public void Note(string text) => notes.Add(text);
    public void Begin(string label) => marks.Add((label, Remote.Server.Requests.Count));

    /// <summary>Plan the whole workspace and execute it as one online job.</summary>
    public async Task RunAsync(string label = "run")
    {
        Begin(label);
        Job = new BatchJob { Mode = RunMode.Online, Items = new ChangePlanner().PlanWorkspace(Workspace) };
        Workspace.Jobs.Add(Job);
        await new OperationsBatchExecutor(Api, Workspace).ExecuteAsync(Job, () => Task.CompletedTask, default);
    }

    /// <summary>Execute the same job again (resume after 429, recovery after Unknown).</summary>
    public async Task ResumeAsync(string label)
    {
        Begin(label);
        await new OperationsBatchExecutor(Api, Workspace).ExecuteAsync(Job!, () => Task.CompletedTask, default);
    }

    public IReadOnlyList<RequestRun> Runs()
    {
        var all = Remote.Server.Requests;
        var list = marks.Count == 0 ? [("all", 0)] : marks.Select(m => (m.Label, m.Start)).ToList();
        return list.Select((m, n) =>
        {
            var end = n + 1 < list.Count ? list[n + 1].Item2 : all.Count;
            return new RequestRun(m.Item1, all.Skip(m.Item2).Take(end - m.Item2).ToArray());
        }).ToArray();
    }

    public IEnumerable<string> Outcomes()
    {
        if (Job != null)
        {
            yield return "job " + Job.StatusCode;
            foreach (var item in Job.Items)
                yield return $"{item.Resource} {item.Kind} {item.State}" + (item.State == ItemState.Succeeded ? "" : " " + item.Description?.Code);
        }
        foreach (var note in notes)
            yield return note;
    }

    /// <summary>Compare with (or, with VAMSYS_UPDATE_GOLDEN=1, write) Expected/&lt;name&gt;.json.</summary>
    public void Verify(string name) => GoldenFile.Verify(name, GoldenFile.Serialize(name, Runs(), Outcomes()));
}
