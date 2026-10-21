using System.Net;
using VamSys.Core;
using VamSys.Infrastructure;
using VamSys.TestSupport;

namespace VamSys.IntegrationTests.Golden.Storage;

/// <summary>
/// One workspace driven through the same calls the application makes: row-level autosave, full save, undo/redo,
/// snapshot refresh, task submission and execution. Remote data is a scripted FakeOperationsServer on virtual time.
/// </summary>
sealed class WorkspaceScenario : IDisposable
{
    public readonly TempDataDirectory Dir = TempDataDirectory.Create("vamsys-golden-");
    public readonly WorkspaceStore Store;
    public readonly TestTime Clock = new();
    public readonly DraftEdits Edits = new();
    public Workspace W { get; private set; }

    public WorkspaceScenario()
    {
        Store = new WorkspaceStore(Dir.Path);
        W = new Workspace { Name = "Golden workspace", AirlineId = "7", Mode = RunMode.Online };
    }

    public void Dispose() => Dir.Dispose();
    public string DatabasePath => Dir.File("workspaces.db");

    // ---- remote data ------------------------------------------------------------------------------------------------

    public static string Page(params string[] rows) => "{\"data\":[" + string.Join(",", rows) + "],\"meta\":{\"next_cursor_url\":null}}";

    public const string Heathrow = """{"id":101,"airline_id":7,"icao":"EGLL","iata":"LHR","name":"London Heathrow"}""";
    public const string Gatwick = """{"id":102,"airline_id":7,"icao":"EGKK","iata":"LGW","name":"London Gatwick"}""";
    public const string Beijing = """{"id":103,"airline_id":7,"icao":"ZBAA","iata":"PEK","name":"北京首都"}""";
    public const string Berlin = """{"id":104,"airline_id":7,"icao":"EDDB","iata":"BER","name":"Berlin Brandenburg"}""";
    public const string FleetAlpha = """{"id":4,"airline_id":7,"name":"Fleet Alpha","code":"B738","type":"pax","max_pax":180}""";
    public const string FleetBeta = """{"id":5,"airline_id":7,"name":"Cargo Beta","code":"B77F","type":"cargo","max_cargo":100000}""";
    public const string AlphaOne = """{"id":21,"airline_id":7,"fleet_id":4,"name":"Alpha One","registration":"B-1001"}""";
    public const string AlphaTwo = """{"id":22,"airline_id":7,"fleet_id":4,"name":"Alpha Two","registration":"B-1002"}""";
    public const string Routing = """{"id":31,"airline_id":7,"departure_airport_id":101,"arrival_airport_id":102,"route":"DCT"}""";
    public const string RouteOut = """{"id":41,"type":"scheduled","departure_id":101,"arrival_id":102,"callsign":"ACA123","flight_number":"AC123","fleet_ids":[4]}""";
    public const string RouteBack = """{"id":42,"type":"scheduled","departure_id":102,"arrival_id":101,"callsign":"ACA124","flight_number":"AC124","fleet_ids":[4,5],"remarks":"回程"}""";

    public static FakeOperationsServer ReadServer(string[] airports, string[] fleets, string[] aircraftOfFleet4, string[] routings, string[] routes)
        => new FakeOperationsServer()
            .On(HttpMethod.Get, "airports", FakeResponse.Ok(Page(airports)))
            .On(HttpMethod.Get, "fleet", FakeResponse.Ok(Page(fleets)))
            .On(HttpMethod.Get, "fleet/4/aircraft", FakeResponse.Ok(Page(aircraftOfFleet4)))
            .On(HttpMethod.Get, "fleet/5/aircraft", FakeResponse.Ok(Page()))
            .On(HttpMethod.Get, "routings", FakeResponse.Ok(Page(routings)))
            .On(HttpMethod.Get, "routes", FakeResponse.Ok(Page(routes)));

    OperationsAdapter Api(FakeOperationsServer server) => new(new OperationsTransport(server.CreateClient(), OperationsAdapter.BaseUri, "golden",
        new TokenProvider(_ => Task.FromResult(new AccessToken("test", Clock.GetUtcNow().AddHours(1))), Clock), new RequestCoordinator(Clock)), W);

    // ---- storage calls as the application makes them -----------------------------------------------------------------

    public void CreateWorkspace() => Store.Save(W);

    /// <summary>Main window Save(): the whole workspace.</summary>
    public async Task SaveAll()
    {
        var taken = Edits.Take();
        try { await Store.SaveAsync(W); }
        catch { Edits.Restore(taken); throw; }
    }

    /// <summary>Main window SaveEdits(), but the row-level path is required, not allowed to fall back.</summary>
    public async Task SaveRows()
    {
        var taken = Edits.Take();
        try
        {
            Xunit.Assert.True(taken.CanSaveRows(W), "edits require a full save");
            Xunit.Assert.True(await Store.TrySaveDraftRowsAsync(W, taken.Changes), "row-level save refused");
        }
        catch { Edits.Restore(taken); throw; }
    }

    public async Task Refresh(FakeOperationsServer server)
        => await Clock.Drive(Api(server).RefreshSnapshotsAsync(async staged => await Store.SaveAsync(staged), default));

    // ---- edits --------------------------------------------------------------------------------------------------------

    public ResourceData Data(ResourceKind kind) => W.Resources[kind];
    public DataRow Row(ResourceKind kind, string remoteId) => Data(kind).Draft.Single(r => r.Identity?.RemoteId == remoteId);
    public DataRow RowByName(ResourceKind kind, string name) => Data(kind).Draft.Single(r => r.Get("Name") == name);

    public void EditCell(ResourceKind kind, DataRow row, string field, string value, bool newStep = true)
    {
        var data = Data(kind);
        data.Edit([row.LocalId], () => row.Fields[field] = value, newStep: newStep);
        Edits.Cell(W, kind, row.LocalId, newStep);
    }

    /// <summary>Undo or redo; row-level save when the step replaced rows in place, otherwise a full save.</summary>
    public async Task Move(ResourceKind kind, bool undo)
    {
        var data = Data(kind);
        var move = undo ? data.UndoEdit() : data.RedoEdit();
        Xunit.Assert.True(move.Moved);
        if (move.ReplacedRows is { } rows) { Edits.Rows(W, kind, rows); await SaveRows(); }
        else await SaveAll();
    }

    public async Task<DataRow> AddRow(ResourceKind kind)
    {
        var data = Data(kind);
        var row = new DataRow();
        foreach (var f in Schemas.All[kind].Columns) row.Fields[f] = f == "_delete" ? "FALSE" : "";
        data.Edit([row.LocalId], () => data.Draft.Add(row), structural: true);
        data.Columns = data.Columns.Concat(row.Fields.Keys).Distinct().ToList();
        await SaveAll();
        return row;
    }

    public async Task MarkDeleted(ResourceKind kind, params DataRow[] rows)
    {
        var service = new DeletionService();
        var plan = service.Preview(W, kind, rows.Select(r => r.LocalId));
        Xunit.Assert.Empty(plan.Issues);
        service.Apply(W, plan);
        await SaveAll();
    }

    public async Task Restore(ResourceKind kind, params DataRow[] rows)
    {
        new DeletionService().Restore(Data(kind), rows.Select(r => r.LocalId));
        await SaveAll();
    }

    /// <summary>The batch edit page: preview a rule, then replace the changed rows in one undo step.</summary>
    public async Task BatchEdit(ResourceKind kind, EditRule rule, IEnumerable<DataRow> rows)
    {
        var data = Data(kind);
        var eligible = rows.Where(r => !Schemas.Deleted(r)).ToArray();
        var before = eligible.ToDictionary(r => r.LocalId);
        var changes = RuleEngine.Preview(eligible, rule).Where(r => r.Get(rule.Field) != before[r.LocalId].Get(rule.Field)).ToArray();
        Xunit.Assert.NotEmpty(changes);
        data.Edit(changes.Select(r => r.LocalId), () => data.ReplaceDraftRows(changes.Select(r => r.Copy())));
        if (!data.Columns.Contains(rule.Field)) data.Columns.Add(rule.Field);
        await SaveAll();
    }

    public async Task ImportCsv(ResourceKind kind, string csvText, Dictionary<string, string> mapping, bool baseline)
    {
        var adapter = new CsvAdapter();
        var rows = adapter.Map(adapter.Parse(csvText), mapping);
        CsvAdapter.Import(kind, Data(kind), rows, baseline);
        await SaveAll();
    }

    /// <summary>
    /// The conflict dialog: keep local, use remote, or (for rows removed remotely, field "*") drop the local row.
    /// The choice is made by the supplied function; every resolution is saved like the application does.
    /// </summary>
    public async Task ResolveConflicts(Func<ResourceKind, MergeConflict, bool> useRemote)
    {
        foreach (var kind in Enum.GetValues<ResourceKind>())
        {
            var data = Data(kind);
            foreach (var conflict in data.Conflicts.ToArray())
            {
                var row = data.FindDraftRow(conflict.RowId);
                if (row is null) { data.Conflicts.Remove(conflict); await SaveAll(); continue; }
                if (useRemote(kind, conflict))
                    data.Edit([row.LocalId], () => { if (conflict.Field == "*") data.Draft.Remove(row); else row.Fields[conflict.Field] = conflict.Remote ?? ""; }, newStep: false, structural: conflict.Field == "*");
                data.Conflicts.Remove(conflict);
                await SaveAll();
            }
        }
    }

    // ---- tasks --------------------------------------------------------------------------------------------------------

    /// <summary>Submit: plan the workspace, keep the new fleets, add the job as Pending and save.</summary>
    public async Task<BatchJob> SubmitFleetCreates()
    {
        // Plan(kind, data) like the old storage scenarios: PlanWorkspace would also demand an approved airline parameter
        // for Online fleet creates, which needs the parameters endpoints and is outside the storage characterisation.
        var items = new ChangePlanner().Plan(ResourceKind.Fleets, Data(ResourceKind.Fleets)).Where(i => i.Kind == ChangeKind.Create).ToList();
        Xunit.Assert.NotEmpty(items);
        var job = new BatchJob { Mode = RunMode.Online, Items = items };
        W.Jobs.Add(job);
        await SaveAll();
        return job;
    }

    static readonly Dictionary<string, int> NewFleetPax = new() { ["New Fleet Gamma"] = 150, ["CSV 新机队"] = 100 };

    /// <summary>Runs the job against the fake server; afterCheckpoint sees the database after each durable checkpoint.</summary>
    public async Task<FakeOperationsServer> Execute(BatchJob job, Action<string> afterCheckpoint)
    {
        // Created fleets get ids 50, 51, ... and read back with the fields the job wrote.
        var created = new Dictionary<string, string>();
        string Record(string id, string name, string code, string type) => $$"""{"id":{{id}},"airline_id":7,"name":"{{name}}","code":"{{code}}","type":"{{type}}","max_pax":{{NewFleetPax[name]}}}""";
        var server = new FakeOperationsServer()
            .On(HttpMethod.Post, "fleet", request =>
            {
                var body = System.Text.Json.JsonDocument.Parse(request.Body!).RootElement;
                var id = (50 + created.Count).ToString();
                created[id] = Record(id, body.GetProperty("name").GetString()!, body.GetProperty("code").GetString()!, body.GetProperty("type").GetString()!);
                return FakeResponse.WithStatus(201, created[id]);
            })
            .On(HttpMethod.Put, "fleet/50", _ => FakeResponse.Ok("{\"data\":" + created["50"] + "}"))
            .On(HttpMethod.Put, "fleet/51", _ => FakeResponse.Ok("{\"data\":" + created["51"] + "}"))
            .On(HttpMethod.Get, "fleet/50", _ => created.TryGetValue("50", out var r) ? FakeResponse.Ok("{\"data\":" + r + "}") : FakeResponse.WithStatus(404))
            .On(HttpMethod.Get, "fleet/51", _ => created.TryGetValue("51", out var r) ? FakeResponse.Ok("{\"data\":" + r + "}") : FakeResponse.WithStatus(404))
            .On(HttpMethod.Get, "fleet", _ => FakeResponse.Ok(Page([FleetAlpha, FleetBeta, ..created.Values])));
        var writer = new RecordingWriter(Store.CheckpointWriter(W), afterCheckpoint);
        await Clock.Drive(new OperationsBatchExecutor(Api(server), W).ExecuteAsync(job, writer, default));
        return server;
    }

    sealed class RecordingWriter(IBatchCheckpointWriter inner, Action<string> after) : IBatchCheckpointWriter
    {
        public async Task WriteAsync(BatchCheckpoint checkpoint, CancellationToken ct)
        {
            await inner.WriteAsync(checkpoint, ct);
            after(checkpoint.Kind.ToString());
        }
    }
}
