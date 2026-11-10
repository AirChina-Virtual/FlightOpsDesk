using System.Text.Json;
using VamSys.Core;
using VamSys.Infrastructure;

namespace VamSys.TestSupport;

/// <summary>
/// 4.1: data for the window tests that need a connection. Each scenario builds a workspace from a <see cref="FakeRemote"/>,
/// produces its task by running the real executor against that same fake server (so every item carries the evidence the
/// program itself would have saved), and stores workspace, task and credentials in the test's data directory. The test
/// then serves the returned server to the QA package (<see cref="LoopbackOperationsServer"/>), connects, and opens the
/// dialog. Nothing here talks to a real server.
/// </summary>
public static class ConnectedUiFixture
{
    public const string ClientId = "ui-mock-client", Secret = "ui-mock-secret-not-real";

    /// <summary>The scenarios; the comments say which dialog each one leads to.</summary>
    public enum Scenario
    {
        // Data page, Validate while connected: a route whose fleet 6 is not approved for its prefixes (the parameter list
        // links fleets 4 and 5 only). The review then offers to remove fleet 6 (2.3).
        UnapprovedFleet,
        // A soft delete answered 204 whose record stays readable: "confirm by hand that it is deleted".
        DeleteAnsweredButReadable,
        // A soft delete never answered; the record still reads, but differently from before: "still readable".
        NoReceiptStillReadable,
        // A soft delete never answered; the record is gone now: "confirm the delete without a receipt".
        NoReceiptGone,
        // A soft delete never answered; the record reads exactly as before: "mark it as not applied".
        NoReceiptUnchanged,
        // An update never answered and not applied: "verify that it did not take effect".
        UpdateNotApplied,
        // An update answered 200 whose record keeps another value: "accept the server's value".
        ServerKeepsOtherValue,
        // As above, but the record has since come to read as intended: "cannot accept, it matches now".
        ServerValueNowMatches,
        // A create never answered; two records could be it (E1): the candidate list in the recovery ID dialog.
        CreateWithTwoCandidates,
        // A route refused with 422 because two of its fleets are not approved (2.4): the task center offers to remove them.
        RouteRefusedForFleets,
        // Two soft deletes never answered whose records are gone now (1.6): one dialog confirms both.
        TwoDeletesWithoutReceiptGone,
    }

    const string RefusedForFleets = """{"message":"Fleet 'Fleet 5' (ID: 5) is not approved for callsign prefix 'CAO' and flight number prefix 'CA'. (and 1 more error)","errors":{"fleet_ids.1":["Fleet 'Fleet 5' (ID: 5) is not approved for callsign prefix 'CAO' and flight number prefix 'CA'."],"fleet_ids.2":["Fleet 'Fleet 6 spare' (ID: 6) is not approved for callsign prefix 'CAO' and flight number prefix 'CA'."]}}""";

    /// <summary>Seeds <paramref name="directory"/> and returns the fake server the program must be pointed at.</summary>
    public static FakeRemote Seed(string directory, Scenario scenario)
    {
        DataUiFixture.RequireEmptyDirectory(directory);
        Directory.CreateDirectory(directory);
        var remote = new FakeRemote();
        remote.AddStandardData();
        if (scenario == Scenario.TwoDeletesWithoutReceiptGone)
            remote.AddRow("airports/104", """{"id":104,"airline_id":7,"icao":"EGPH","iata":"EDI","name":"Edinburgh"}""");
        var workspace = new Workspace { Name = "QA connected", AirlineId = "7", Mode = RunMode.Online };
        var requests = InstantTime.Requests();
        var tokens = new TokenProvider(_ => Task.FromResult(new AccessToken("seed-token", requests.Clock.GetUtcNow().AddHours(1))), requests.Clock);
        // The fake server outlives this client: the window test serves it to the program afterwards.
        using var client = new HttpClient(remote.Server, disposeHandler: false);
        var api = new OperationsAdapter(new OperationsTransport(client, OperationsAdapter.BaseUri, "ui-seed", tokens, requests), workspace);
        Load(remote, api, workspace);

        var store = new WorkspaceStore(directory);
        store.Save(workspace);
        // Credentials can only be saved while no task is open, so before the scenario runs.
        store.SaveConnectionSettings(workspace, ClientId, Secret, "", false);
        workspace.Mode = RunMode.Online;

        Run(scenario, remote, api, workspace).GetAwaiter().GetResult();
        remote.Intercept = null;
        After(scenario, remote);
        // The program opens the workspace offline and connects; the parameter list it reads then decides the prefixes.
        if (scenario is Scenario.UnapprovedFleet or Scenario.RouteRefusedForFleets)
            remote.Intercept = r => r.Method == "GET" && r.Path.EndsWith("/parameters", StringComparison.Ordinal) ? Parameters("4") : null;
        store.Save(workspace);
        using (var pooled = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "workspaces.db") }.ToString()))
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(pooled);
        return remote;
    }

    static FakeResponse Parameters(params string[] fleets)
        => FakeResponse.Ok($$$"""{"data":[{"id":1,"airline_id":7,"icao_code":"CAO","iata_code":"CA","approved":true,"restricted":false,"fleet_ids":[{{{string.Join(',', fleets)}}}]}],"meta":{"next_cursor_url":null}}""");

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

    // Every remote row as an unchanged snapshot and draft row, as a refresh would leave them.
    static void Load(FakeRemote remote, OperationsAdapter api, Workspace workspace)
    {
        foreach (var kind in Enum.GetValues<ResourceKind>())
        {
            var data = workspace.Resources[kind];
            foreach (var (_, json) in remote.Rows.Where(p => KindOf(p.Key) == kind).OrderBy(p => p.Value["id"]!.GetValue<int>()))
            {
                using var doc = JsonDocument.Parse(json.ToJsonString());
                var row = api.FromJson(kind, doc.RootElement);
                data.Snapshot.Add(row.Dup());
                data.Draft.Add(row.Dup());
            }
            data.Columns = Schemas.All[kind].Columns.ToList();
            data.SnapshotAt = DateTimeOffset.UtcNow;
        }
    }

    static void Edit(Workspace workspace, ResourceKind kind, string remoteId, params (string Field, string Value)[] edits)
    {
        var data = workspace.Resources[kind];
        var row = data.Draft.Single(r => r.Identity?.RemoteId == remoteId);
        data.ReplaceDraftRows([row.With(edits.Select(e => KeyValuePair.Create(e.Field, e.Value)))]);
    }

    static DataRow NewRow(params (string Field, string Value)[] fields)
    {
        var values = fields.ToDictionary(f => f.Field, f => f.Value);
        values.TryAdd("_delete", "FALSE");
        return new DataRow { Fields = values };
    }

    static async Task Execute(OperationsAdapter api, Workspace workspace)
    {
        var job = new BatchJob { Mode = RunMode.Online, Items = new ChangePlanner().PlanWorkspace(workspace) };
        workspace.Jobs.Add(job);
        await new OperationsBatchExecutor(api, workspace).ExecuteAsync(job, () => Task.CompletedTask, default);
    }

    // After the first write, reads fail for the rest of the run, so the program's own reads (read-back, the read at the
    // end of the round) cannot settle the item: it stays "unknown" for the person, which is what the dialogs are for.
    static Func<RecordedRequest, FakeResponse?> FirstWrite(string method, Func<RecordedRequest, FakeResponse?> answer)
    {
        var written = false;
        return r =>
        {
            if (written && r.Method == "GET")
                throw new TimeoutException("simulated read");
            if (r.Method != method || written)
                return null;
            written = true;
            return answer(r);
        };
    }

    static async Task Run(Scenario scenario, FakeRemote remote, OperationsAdapter api, Workspace workspace)
    {
        const string Dep = "Departure Airport (ICAO/IATA)", Arr = "Arrival Airport (ICAO/IATA)";
        switch (scenario)
        {
            case Scenario.UnapprovedFleet:
                Edit(workspace, ResourceKind.Routes, "10", ("Fleet IDs", "4,6"));
                return;
            case Scenario.DeleteAnsweredButReadable:
                Edit(workspace, ResourceKind.Airports, "103", ("_delete", "TRUE"));
                remote.Intercept = FirstWrite("DELETE", _ => new FakeResponse(System.Net.HttpStatusCode.NoContent, null));
                break;
            case Scenario.NoReceiptStillReadable or Scenario.NoReceiptGone or Scenario.NoReceiptUnchanged:
                Edit(workspace, ResourceKind.Airports, "103", ("_delete", "TRUE"));
                remote.Intercept = FirstWrite("DELETE", r =>
                {
                    if (scenario == Scenario.NoReceiptGone)
                        remote.Respond(r);
                    throw new TimeoutException("simulated: the answer was lost");
                });
                break;
            case Scenario.UpdateNotApplied:
                Edit(workspace, ResourceKind.Fleets, "4", ("Name", "Fleet 4 renamed"));
                remote.Intercept = FirstWrite("PUT", _ => throw new TimeoutException("simulated: lost before the server applied it"));
                break;
            case Scenario.ServerKeepsOtherValue or Scenario.ServerValueNowMatches:
                Edit(workspace, ResourceKind.Fleets, "4", ("Name", "Fleet 4 renamed"));
                remote.Intercept = r =>
                {
                    if (r.Method != "PUT")
                        return null;
                    // accepted, but the server keeps a value of its own
                    remote.Respond(r);
                    remote.Rows["fleet/4"]["name"] = "FLEET 4 RENAMED (server)";
                    return FakeResponse.Ok("""{"data":{}}""");
                };
                break;
            case Scenario.CreateWithTwoCandidates:
                workspace.Resources[ResourceKind.Fleets].Draft.Add(NewRow(("Name", "Fleet 9"), ("Type Code", "A320"), ("Type (pax/cargo/...)", "cargo"), ("Parameter IDs", "1")));
                remote.Intercept = r =>
                {
                    if (r.Method != "POST")
                        return null;
                    remote.Respond(r);
                    remote.Rows["fleet/90"]["created_at"] = DateTimeOffset.UtcNow.ToString("O");
                    remote.AddRow("fleet/91", """{"id":91,"airline_id":7,"name":"Fleet 9","code":"A320","type":"cargo","parameter_ids":[1]}""")["created_at"] = DateTimeOffset.UtcNow.ToString("O");
                    throw new TimeoutException("simulated: created, the answer was lost");
                };
                break;
            case Scenario.TwoDeletesWithoutReceiptGone:
                Edit(workspace, ResourceKind.Airports, "103", ("_delete", "TRUE"));
                Edit(workspace, ResourceKind.Airports, "104", ("_delete", "TRUE"));
                // Only a record already sent its DELETE cannot be read; the other one still passes its check before the write.
                var deleted = new HashSet<string>(StringComparer.Ordinal);
                remote.Intercept = r =>
                {
                    if (r.Method == "GET" && deleted.Contains(r.Path))
                        throw new TimeoutException("simulated read");
                    if (r.Method != "DELETE")
                        return null;
                    deleted.Add(r.Path);
                    remote.Respond(r);
                    throw new TimeoutException("simulated: the answer was lost");
                };
                break;
            case Scenario.RouteRefusedForFleets:
                workspace.Resources[ResourceKind.Routes].Draft.Add(NewRow(("Type", "repositioning"), ("Callsign", "CAO088"), ("Flight Number", "CA88"),
                    (Dep, "EGLL"), (Arr, "EGKK"), ("Fleet IDs", "4,5,6")));
                remote.Intercept = r => r.Method == "POST" && r.Path.EndsWith("/routes", StringComparison.Ordinal) ? FakeResponse.WithStatus(422, RefusedForFleets) : null;
                break;
        }
        await Execute(api, workspace);
        var expected = scenario == Scenario.RouteRefusedForFleets ? ItemState.Failed : ItemState.Unknown;
        foreach (var item in workspace.Jobs.Single().Items)
            if (item.State != expected)
                throw new InvalidOperationException($"Seed {scenario}: an item ended {item.State} ({item.Description?.Code}), not {expected}.");
    }

    // What changes on the server after the task ran and before the program connects.
    static void After(Scenario scenario, FakeRemote remote)
    {
        if (scenario == Scenario.NoReceiptStillReadable)
            remote.Rows["airports/103"]["name"] = "Manchester (edited elsewhere)";
        if (scenario == Scenario.ServerValueNowMatches)
            remote.Rows["fleet/4"]["name"] = "Fleet 4 renamed";
    }
}
