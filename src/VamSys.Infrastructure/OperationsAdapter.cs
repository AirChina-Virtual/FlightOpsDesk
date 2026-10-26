using System.Text.Json;
using VamSys.Core;

namespace VamSys.Infrastructure;

public sealed record ApiStep(HttpMethod Method, string Path, Dictionary<string, object?> Body);
public interface IApiResourceAdapter
{
    DataRow FromJson(ResourceKind kind, JsonElement value);
    List<ApiStep> Plan(ChangeItem change);
}

// The one object the rest of the program holds for the Operations API. It owns no logic of its own: each call is
// answered by the component that is responsible for it (src/VamSys.Infrastructure/Api, see README there):
//   ResourceMapper     JSON -> row                       RequestPlanner     change -> request steps
//   ContractValidator  request bodies vs the contract    StepWriter         sending steps, saving each one first
//   ReadbackVerifier   did the server keep our values    DuplicateFinder    would a create duplicate a record
//   IdentityIndex      who is who (airport codes, rows)  DeletionVerifier   soft delete proof
//   UnknownResolver    reading to settle unknown writes  MovementVerifier   aircraft fleet move checks
// Keeping this surface lets the executor, the app and the tests stay as they are.
public sealed class OperationsAdapter : IResourceReader, IResourceWriter, IApiResourceAdapter
{
    public static readonly Uri BaseUri = new("https://vamsys.io/api/v3/operations/");

    readonly ApiContext ctx;
    readonly RequestPlanners planners;
    readonly ReadbackVerifier readback;
    readonly DuplicateFinder finder;
    readonly CandidateReservations reservations;
    readonly DependencyChecker dependencies;
    readonly DeletionVerifier deletion;
    readonly UnknownResolver unknown;
    readonly MovementVerifier movement;
    readonly AirlineParameters parameters;
    readonly StepWriter writer;
    readonly SnapshotRefresher refresher;

    public OperationsAdapter(OperationsTransport transport, Workspace workspace)
    {
        ctx = new ApiContext(transport, workspace);
        planners = new RequestPlanners(ctx);
        readback = new ReadbackVerifier(ctx, planners);
        finder = new DuplicateFinder(ctx);
        reservations = new CandidateReservations(ctx, finder);
        dependencies = new DependencyChecker(ctx);
        deletion = new DeletionVerifier(ctx, readback);
        unknown = new UnknownResolver(ctx, finder, readback);
        movement = new MovementVerifier(ctx);
        parameters = new AirlineParameters(ctx);
        writer = new StepWriter(ctx);
        refresher = new SnapshotRefresher(ctx);
    }

    public string? SessionAirlineId => ctx.SessionAirlineId;
    public string ConnectionId => ctx.ConnectionId;
    public static InvalidOperationException Block(string key, string? field = null) => ApiErrors.Block(key, field);
    public static string Collection(ResourceKind k) => ResourcePaths.Collection(k);
    public static Uri FilterUri(string path, IEnumerable<KeyValuePair<string, string>> filters) => ResourceReader.FilterUri(path, filters);

    // ---- reading ---------------------------------------------------------------------------------------------
    public DataRow FromJson(ResourceKind kind, JsonElement value) => ctx.Mappers.For(kind).FromJson(value);
    public Task<bool> IdentifyAirlineAsync(CancellationToken ct) => ctx.Reader.IdentifyAirlineAsync(ct);
    public IAsyncEnumerable<DataRow> ReadAllAsync(ResourceKind kind, CancellationToken ct) => ctx.Reader.ReadAllAsync(kind, ct);
    public Task RefreshSnapshotsAsync(Func<Workspace, Task> persist, CancellationToken ct, Action<ResourceKind, int>? progress = null) => refresher.RefreshAsync(persist, ct, progress);
    public void Forget(ResourceKind kind, string id) => ctx.Identity.Forget(kind, id);
    public Task<DataRow?> FindAsync(ResourceKind kind, string id, CancellationToken ct) => ctx.Reader.FindAsync(kind, id, ct);
    public Task<DataRow?> FindRow(ResourceKind kind, DataRow row, string? id, CancellationToken ct) => ctx.Reader.FindRowAsync(kind, row, id, ct);
    internal Task EnsureReadConnectionAsync(CancellationToken ct) => ctx.Reader.EnsureReadConnectionAsync(ct);
    // The GET that decides an item's outcome (a move is read under its new fleet).
    internal Task<DataRow?> FindResult(ChangeItem item, string? id, CancellationToken ct) => movement.FindResultAsync(item, id, ct);

    // ---- planning --------------------------------------------------------------------------------------------
    public List<ApiStep> Plan(ChangeItem c) => planners.Plan(c);
    public List<ApiStep> PreviewPlan(ChangeItem c) { var steps = planners.Plan(c, true); parameters.ValidatePreview(c); return steps; }
    internal List<ApiStep> ExecutionPlan(ChangeItem item) => planners.ExecutionPlan(item);
    public static void ValidateBody(ResourceKind kind, bool create, Dictionary<string, object?> body) => ContractValidator.ValidateBody(kind, create, body);
    internal static Dictionary<string, JsonElement> ExpectedValues(IEnumerable<ApiStep> steps) => ApiSteps.ExpectedValues(steps);
    internal static void PrepareSteps(ChangeItem change, List<ApiStep> steps) => ApiSteps.PrepareSteps(change, steps);

    // ---- airline parameters ----------------------------------------------------------------------------------
    public bool HasParameterMetadata => parameters.HasMetadata;
    public void InvalidateParameters() => parameters.Invalidate();
    public Task<IReadOnlyList<AirlineParameter>> ReadParametersAsync(CancellationToken ct, bool force = false) => parameters.ReadAsync(ct, force);
    public bool NeedsParameterPreflight(ChangeItem item) => parameters.NeedsPreflight(item);
    public void ValidateParameterPreview(ChangeItem item) => parameters.ValidatePreview(item);
    public Task PreflightParametersAsync(ChangeItem item, CancellationToken ct) => parameters.PreflightAsync(item, ct);

    // ---- writing ---------------------------------------------------------------------------------------------
    public async Task<string?> WriteAsync(ChangeItem change, CancellationToken ct) => await WriteStepsAsync(change, () => Task.CompletedTask, ct);
    public Task<string?> WriteStepsAsync(ChangeItem change, Func<Task> persist, CancellationToken ct) => writer.WriteAsync(change, planners.Plan(change), persist, ct);
    internal Task<string?> WriteStepsAsync(ChangeItem change, List<ApiStep> steps, Func<Task> persist, CancellationToken ct) => writer.WriteAsync(change, steps, persist, ct);

    // ---- checking what the server holds ------------------------------------------------------------------------
    public bool Matches(ChangeItem item, DataRow current, DataRow expected, bool before) => readback.Matches(item, current, expected, before);
    internal ReadbackVerification VerifyReadback(ChangeItem item, DataRow? current, DataRow expected, IReadOnlyDictionary<string, JsonElement> values) => readback.Verify(item, current, expected, values);
    internal bool MatchesCreated(ChangeItem item, DataRow current) => readback.MatchesCreated(item, current);
    internal void RecordReadback(ChangeItem item, DataRow? current) => readback.RecordReadback(item, current);
    internal void RecordPreflight(ChangeItem item, DataRow? current) => readback.RecordPreflight(item, current);
    internal List<ApiFieldDifference> BeforeDifferences(ChangeItem item, DataRow current) => readback.BeforeDifferences(item, current);
    internal static bool Equivalent(string field, string a, string b) => ReadbackVerifier.Equivalent(field, a, b);
    internal bool RecordDeletionVerification(ChangeItem item, DataRow? current) => deletion.Record(item, current);
    internal Task PreflightMoveAsync(ChangeItem item, CancellationToken ct) => movement.PreflightAsync(item, ct);
    internal static bool CanResumeCreate(ChangeItem item) => UnknownResolver.CanResumeCreate(item);
    internal Task<bool> ProveNotAppliedAsync(ChangeItem item, CancellationToken ct) => unknown.ProveNotAppliedAsync(item, ct);

    // ---- duplicates and dependencies ---------------------------------------------------------------------------
    internal bool IsDuplicate(ChangeItem item, DataRow current) => finder.IsDuplicate(item, current);
    public IAsyncEnumerable<DataRow> ReadDuplicateCandidatesAsync(ChangeItem item, CandidateQuerySession session, CancellationToken ct) => finder.ReadCandidatesAsync(item, session, ct);
    internal void ReserveCandidate(ChangeItem item, CandidateQuerySession session) => reservations.Reserve(item, session);
    internal void SeedCandidates(BatchJob job, CandidateQuerySession session) => reservations.Seed(job, session);
    internal string? CandidateBlock(ChangeItem item, CandidateQuerySession session) => reservations.BlockReason(item, session);
    internal void ConfirmCandidate(ChangeItem item, DataRow? row, CandidateQuerySession session) => reservations.Confirm(item, row, session);
    public IAsyncEnumerable<(ResourceKind Kind, DataRow Row)> ReadDependencyCandidatesAsync(ChangeItem item, CancellationToken ct) => dependencies.ReadDependencyCandidatesAsync(item, ct);
    public Task<bool> HasDeletionDependenciesAsync(ChangeItem item, CancellationToken ct) => dependencies.HasDependenciesAsync(item, ct);
}
