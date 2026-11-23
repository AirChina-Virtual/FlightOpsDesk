using System.Runtime.CompilerServices;
using System.Text.Json;
using VamSys.Core;
using VamSys.Infrastructure;
using VamSys.Presentation;

// Where a workspace review allocates, stage by stage, and what the plan leaves alive (2026-11-23, stage 7 item 2).
// The workspace is the window baseline's (DataUiFixture: 50,000 jumpseat routes with every field), with the first N
// routes' Remarks edited as review-bench does. Demo is the mode the window baseline runs in; Online gives the rows a
// remote identity and previews every change through a real OperationsAdapter whose transport refuses to send.
static class ReviewStageBenchmark
{
    const int Rows = 50000;

    public static Workspace Workspace(int rows, int changes, RunMode mode)
    {
        var workspace = new Workspace { Name = "review stages", Mode = mode };
        DataRow Row(params (string Field, string Value)[] fields) { var row = new DataRow.Builder(); foreach (var (f, v) in fields) row[f] = v; return row.ToRow(); }
        CsvAdapter.Import(ResourceKind.Airports, workspace.Resources[ResourceKind.Airports], [Row(("ICAO/IATA", "ZBAA"), ("Name", "Beijing")), Row(("ICAO/IATA", "ZSPD"), ("Name", "Shanghai"))], true);
        CsvAdapter.Import(ResourceKind.Fleets, workspace.Resources[ResourceKind.Fleets], [Row(("ID", "1"), ("Name", "Boeing 737-800"), ("Type Code", "B738"), ("Type (pax/cargo/...)", "pax"), ("Max Passengers", "189"))], true);
        var list = new List<DataRow>(rows);
        for (var n = 0; n < rows; n++)
        {
            var row = new DataRow.Builder(); foreach (var field in Schemas.All[ResourceKind.Routes].Columns) row[field] = "";
            foreach (var (field, value) in new (string, string)[] { ("ID", n.ToString()), ("Departure Airport (ICAO/IATA)", "ZBAA"), ("Arrival Airport (ICAO/IATA)", "ZSPD"), ("Type", "jumpseat"), ("Callsign", "QA" + n.ToString("D5")), ("Flight Number", n.ToString("D5")), ("Fleet IDs", ""), ("Start Date", "2026-10-01 00:00:00"), ("End Date", "2027-10-01 00:00:00"), ("Departure Time (HH:MM)", "10:00"), ("Arrival Time (HH:MM)", "12:00"), ("Remarks", "row " + n), ("Tags", "performance"), ("Is Hidden", "FALSE"), ("Altitude", "33000"), ("Cost Index", "AUTO"), ("_delete", "FALSE") })
                row[field] = value;
            list.Add(row.ToRow());
        }
        var data = workspace.Resources[ResourceKind.Routes];
        CsvAdapter.Import(ResourceKind.Routes, data, list, true);
        if (mode == RunMode.Online)
        {
            // Rows read from the server carry their identity; the online preview refuses rows without one.
            workspace.AirlineId = "bench-va";
            for (var n = 0; n < data.Snapshot.Count; n++)
            {
                var identified = data.Snapshot[n].WithIdentity(new ResourceIdentity((n + 1).ToString(), "bench-va"));
                data.Snapshot[n] = identified;
                data.Draft[n] = identified;
            }
        }
        for (var n = 0; n < changes; n++)
            data.Draft[n] = data.Draft[n].With("Remarks", "review " + n);
        return workspace;
    }

    sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("The review preview sent a request: " + request.RequestUri);
    }
    // One client for every review here; it never sends, so it never needs disposing.
    static readonly HttpClient NoRequests = new(new NoNetwork());

    static long Allocated() => GC.GetAllocatedBytesForCurrentThread();
    static long Live() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); return GC.GetTotalMemory(true); }

    public static void Run(string path)
    {
        var cases = new List<object>();
        foreach (var mode in new[] { RunMode.Demo, RunMode.Online })
            foreach (var changes in new[] { 0, 1000, 50000 })
                cases.Add(Measure(mode, changes));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(new { Runtime = Environment.Version.ToString(), Cases = cases }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("Review stage benchmark: " + path);
    }

    sealed class Setup(Workspace w, RunMode mode, HashSet<string> verified, Action<ChangeItem>? preview)
    {
        public readonly Workspace W = w; public readonly RunMode Mode = mode; public readonly HashSet<string> Verified = verified; public readonly Action<ChangeItem>? Preview = preview;
        public readonly ChangePlanner Planner = new();
        public WorkspaceReviewPlan Build() => ReviewPlanBuilder.BuildForWorkspace(Planner, W, Mode, Mode == RunMode.Online, Verified, Preview);
    }

    static Setup Prepare(RunMode mode, int changes)
    {
        var w = Workspace(Rows, changes, mode);
        var verified = mode == RunMode.Online ? new HashSet<string>(StringComparer.Ordinal) { "Routes:Update", "Routes:Create", "Routes:Delete" } : new HashSet<string>(StringComparer.Ordinal);
        Action<ChangeItem>? preview = null;
        if (mode == RunMode.Online)
        {
            var tokens = new TokenProvider(_ => Task.FromResult(new AccessToken("bench", DateTimeOffset.UtcNow.AddHours(1))), TimeProvider.System);
            var api = new OperationsAdapter(new OperationsTransport(NoRequests, OperationsAdapter.BaseUri, "bench-va", tokens, InstantTime.Requests()), w);
            preview = c => api.PreviewPlan(c);
        }
        return new(w, mode, verified, preview);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void WarmUp(Setup s)
    {
        for (var n = 0; n < 2; n++)
        {
            var plan = s.Build();
            _ = plan.SubmitItems;
            new ReviewViewModel(s.W, new LocalizationService()).Show(plan, true, false, _ => Task.CompletedTask);
        }
    }

    // The allocation of each stage on its own (this thread's allocated bytes, which do not depend on timing).
    [MethodImpl(MethodImplOptions.NoInlining)]
    static Dictionary<string, long> Stages(Setup s, out int issues)
    {
        var stages = new Dictionary<string, long>();
        var w = s.W; var mode = s.Mode;
        var a = Allocated(); var scanned = s.Planner.ScanWorkspace(w); stages["ScanWorkspace"] = Allocated() - a;
        a = Allocated();
        var kinds = scanned.Select(c => c.Resource).ToHashSet();
        issues = 0;
        if (scanned.Count > 0)
        {
            var pass = new ValidationPass(w);
            foreach (var (kind, _) in w.Resources.Where(p => kinds.Contains(p.Key)))
                issues += Schemas.Validate(w, kind, pass).Count;
        }
        stages["Validation"] = Allocated() - a;
        if (mode == RunMode.Online)
        {
            // The online parts of BuildPlanned on their own: a transient item per change (as 6292ec1 built it), the preview, the operation key and evidence.
            a = Allocated();
            var items = new List<ChangeItem>(scanned.Count);
            foreach (var change in scanned)
            {
                var fields = new Dictionary<string, FieldChange>(change.FieldCount, StringComparer.Ordinal);
                foreach (var field in change.Fields()) fields.Add(field.Key, field.Value);
                items.Add(new ChangeItem { Resource = change.Resource, Kind = change.Kind, Before = change.Before, After = change.After, RequiresAirlineParameter = change.RequiresAirlineParameter, Fields = fields });
            }
            stages["BuildPlanned: transient items (6292ec1 way)"] = Allocated() - a;
            a = Allocated(); foreach (var item in items) { try { s.Preview!(item); } catch (Exception) { } } stages["BuildPlanned: preview"] = Allocated() - a;
            a = Allocated(); foreach (var item in items) { _ = ApiOperationPolicy.Key(item); _ = IndependentApiEvidence.Covers(item, w.ContractHash); } stages["BuildPlanned: key+evidence"] = Allocated() - a;
        }
        a = Allocated(); var plan = ReviewPlanBuilder.BuildPlanned(scanned, mode, mode == RunMode.Online, s.Verified, s.Preview, null, w.ContractHash); stages["BuildPlanned"] = Allocated() - a;
        a = Allocated(); _ = plan.ContentKey(); stages["ContentKey"] = Allocated() - a;
        a = Allocated(); var built = s.Build(); stages["BuildForWorkspace (total)"] = Allocated() - a;
        stages["Conflicts + open tasks (remainder)"] = stages["BuildForWorkspace (total)"] - stages["ScanWorkspace"] - stages["Validation"] - stages["BuildPlanned"] - stages["ContentKey"];
        a = Allocated(); _ = built.SubmitItems; stages["SubmitItems (first)"] = Allocated() - a;
        a = Allocated(); _ = built.SubmitItems; stages["SubmitItems (again)"] = Allocated() - a;
        a = Allocated(); new ReviewViewModel(w, new LocalizationService()).Show(built, true, false, _ => Task.CompletedTask); stages["Row view models"] = Allocated() - a;
        return stages;
    }

    sealed class Held { public object? Scan, Plan, Model; }
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void HoldScan(Setup s, Held h) => h.Scan = s.Planner.ScanWorkspace(s.W);
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void HoldPlan(Setup s, Held h) => h.Plan = s.Build();
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void HoldSubmit(Held h) => _ = ((WorkspaceReviewPlan)h.Plan!).SubmitItems;
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void HoldRows(Setup s, Held h)
    {
        var model = new ReviewViewModel(s.W, new LocalizationService());
        model.Show((WorkspaceReviewPlan)h.Plan!, true, false, _ => Task.CompletedTask);
        h.Model = model;
    }

    // What stays alive: the scan alone; then the plan as the page keeps it, with the submission items the submit check
    // builds, and with the page's rows.
    [MethodImpl(MethodImplOptions.NoInlining)]
    static Dictionary<string, long> Retained(Setup s)
    {
        var live = new Dictionary<string, long>();
        var h = new Held();
        var baseline = Live();
        HoldScan(s, h); live["ScanWorkspace result"] = Live() - baseline; h.Scan = null;
        baseline = Live();
        HoldPlan(s, h); live["plan (BuildForWorkspace)"] = Live() - baseline;
        HoldSubmit(h); live["plan + SubmitItems"] = Live() - baseline;
        HoldRows(s, h); live["plan + SubmitItems + row view models"] = Live() - baseline;
        GC.KeepAlive(h);
        return live;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static object Measure(RunMode mode, int changes)
    {
        var s = Prepare(mode, changes);
        WarmUp(s);
        var stages = Stages(s, out var issues);
        var live = Retained(s);
        var plan = s.Build();
        var key = plan.ContentKey();
        var summary = $"{plan.Entries.Count} entries, {plan.Entries.Count(e => e.Disposition == ReviewDisposition.Ready)} ready, {plan.Entries.Count(e => e.Disposition == ReviewDisposition.Deferred)} deferred, {plan.Entries.Count(e => e.Disposition == ReviewDisposition.Blocked)} blocked, {issues} issues"
            + (plan.Entries.FirstOrDefault(e => e.Disposition == ReviewDisposition.Blocked)?.Reason is { } reason ? $"; first block {reason.Code}" : "");
        Console.WriteLine($"{mode} {changes,6:N0} changes ({summary})");
        foreach (var (stage, bytes) in stages) Console.WriteLine($"  {stage,-44} {bytes / 1048576.0,9:F2} MiB alloc");
        foreach (var (stage, bytes) in live) Console.WriteLine($"  live: {stage,-38} {bytes / 1048576.0,9:F2} MiB");
        return new { Mode = mode.ToString(), Rows, Changes = changes, Summary = summary, Key = key, AllocatedBytes = stages, LiveBytes = live };
    }
}
