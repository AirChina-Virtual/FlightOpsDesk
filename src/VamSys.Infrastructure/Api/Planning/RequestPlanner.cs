using System.Globalization;
using System.Text.Json;
using VamSys.Core;
using static VamSys.Infrastructure.ApiErrors;

namespace VamSys.Infrastructure;

// One change -> the requests that carry it out, for one resource. The base class owns the sequence every resource
// follows (preconditions, path, delete, field by field, body checks, create split into POST + PUT); the resource
// classes add only what is special to them and say so where they do. Nothing here sends anything: a plan is data,
// validated against the contract by ContractValidator before it can be used.
internal abstract class RequestPlanner(ApiContext ctx, RequestPlanners siblings, ResourceKind kind)
{
    public ResourceKind Kind => kind;
    protected ApiContext Ctx { get; } = ctx;

    public List<ApiStep> Plan(ChangeItem c, bool preview)
    {
        if (c.Before != null && c.Before.Identity == null)
            throw Block("ApiRefreshFirst");
        if (c.Kind != ChangeKind.Create && c.After.Identity == null)
            throw Block("ApiRefreshFirst");
        if (c.Kind == ChangeKind.Create && c.After.Get("ID") != "" && c.RemoteId == null)
            throw Block("ApiRefreshFirst");
        if (PlanSpecialOperation(c, preview) is { } special)
            return [special];
        var path = Ctx.Paths.Path(c.Resource, c.Before ?? c.After, c.RemoteId, preview);
        if (c.Kind == ChangeKind.Delete)
            return [new(HttpMethod.Delete, path, [])];
        bool create = c.Kind == ChangeKind.Create;
        CheckBeforeFields(c, create);
        var body = new Dictionary<string, object?>();
        foreach (var (field, intent) in c.Fields)
            PlanField(c, field, intent, create, preview, body);
        CheckBody(c, create, body);
        if (body.Count == 0)
            throw Block("ApiNoChanges");
        return Steps(c, create, preview, path, body);
    }

    // ---- what a resource may add -------------------------------------------------------------------------------

    // A change that is not a plain create / update / delete (the aircraft fleet move). Null for everything else.
    protected virtual ApiStep? PlanSpecialOperation(ChangeItem c, bool preview) => null;
    // Rules that apply before any field is looked at (a new fleet needs an airline parameter).
    protected virtual void CheckBeforeFields(ChangeItem c, bool create) { }
    // A column that is not an API field of its own (key, parent fleet, airports). True when the column was handled.
    public virtual bool TryPlanColumn(ChangeItem c, string field, string text, bool create, bool preview, Dictionary<string, object?> body) => false;
    // Rules for one API field that depend on the rest of the item (route type, fleet sets).
    protected virtual void CheckField(ChangeItem c, FieldSpec spec, string wire, string text, bool create) { }
    // Time and date columns convert with the stored value in mind; only routes have them.
    protected virtual object ConvertTemporal(ChangeItem c, FieldSpec spec, string field, string wire, string text) => text;
    // Rules for the finished body (a route needs fleets, a plausible type, flight number length, ordered dates).
    protected virtual void CheckBody(ChangeItem c, bool create, Dictionary<string, object?> body) { }
    // Create-schema fields the server does not keep from the POST: they go in the PUT to the created record instead.
    protected virtual IReadOnlySet<string> NotKeptByCreate => EmptyFields;
    static readonly IReadOnlySet<string> EmptyFields = new HashSet<string>();

    // ---- shared sequence ---------------------------------------------------------------------------------------

    void PlanField(ChangeItem c, string field, FieldChange intent, bool create, bool preview, Dictionary<string, object?> body)
    {
        if (intent.Intent == FieldIntent.Unspecified || field == "_delete")
            return;
        var text = intent.Value ?? "";
        if (create && text.Length == 0)
            return;
        if (field == "ID")
            throw Block("ApiImmutable", field);
        // Columns are matched by name, not by owner: an unknown CSV column that happens to carry another resource's
        // special name keeps the treatment it always had.
        if (TryPlanColumn(c, field, text, create, preview, body) || siblings.TryPlanForeignColumn(this, c, field, text, create, preview, body))
            return;
        var spec = FieldSpecs.Get(c.Resource).Find(field);
        if (spec?.Wire is not { } wire)
        { if (Schemas.All[c.Resource].Columns.Contains(field)) throw Block("ApiUnsupported", field); return; }
        if (spec.CreateOnly && !create)
            throw Block("ApiParameterCreateOnly");
        CheckField(c, spec, wire, text, create);
        if (text.Length == 0)
        {
            if (spec.ClearsToEmptyArray)
            { body[wire] = Array.Empty<long>(); return; }
            throw Block("ApiClear", field);
        }
        body[wire] = ConvertValue(c, spec, field, wire, text, preview);
    }

    object ConvertValue(ChangeItem c, FieldSpec spec, string field, string wire, string text, bool preview)
    {
        object result = text;
        if (spec.Type == FieldType.Integer || spec.Type == FieldType.KgInteger)
        { if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number)) throw Block("ApiInvalid", field); result = number; }
        if (spec.Type == FieldType.Bool)
        { if (!bool.TryParse(text, out var flag)) throw Block("ApiInvalid", field); result = flag; }
        if (spec.Type == FieldType.TagList || spec.Type == FieldType.DayList)
            result = Tokens(text).Distinct().ToArray();
        if (spec.Type == FieldType.IdList)
            result = Tokens(text).Select(t => preview && spec.Reference == ResourceKind.Fleets ? Ctx.Identity.PreviewReference(t, ResourceKind.Fleets) : PositiveId(t)).Distinct().ToArray();
        if (spec.Type == FieldType.ParameterIdList)
        {
            var ids = Tokens(text).Select(t => PositiveId(t).ToString(CultureInfo.InvariantCulture)).Distinct().ToArray();
            if (ids.Length != 1)
                throw Block("ApiInvalid", field);
            result = ids;
        }
        if (spec.Type == FieldType.Time || spec.Type == FieldType.Date)
            result = ConvertTemporal(c, spec, field, wire, text);
        return result;
    }

    static string[] Tokens(string text) => text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    // A create is a POST of the properties the create schema knows (less those the POST does not keep) plus, when there are
    // more, a PUT to the new record. The PUT is sent only after the POST's answer gave the id (StepWriter); after an
    // interruption only a PUT still saved as unsent is sent, and only to that id (UnknownResolver.CanResumeCreate).
    List<ApiStep> Steps(ChangeItem c, bool create, bool preview, string path, Dictionary<string, object?> body)
    {
        var steps = new List<ApiStep>();
        if (create)
        {
            var properties = ContractValidator.CreateProperties(c.Resource);
            bool InPost(string wire) => properties.TryGetProperty(wire, out _) && !NotKeptByCreate.Contains(wire);
            var first = body.Where(p => InPost(p.Key)).ToDictionary();
            var rest = body.Where(p => !InPost(p.Key)).ToDictionary();
            ContractValidator.ValidateBody(c.Resource, true, first);
            steps.Add(new(HttpMethod.Post, Ctx.Paths.Path(c.Resource, c.After, preview: preview), first));
            if (rest.Count > 0)
            { ContractValidator.ValidateBody(c.Resource, false, rest); steps.Add(new(HttpMethod.Put, "{created}", rest)); }
        }
        else
        { ContractValidator.ValidateBody(c.Resource, false, body); steps.Add(new(HttpMethod.Put, path, body)); }
        return steps;
    }
}

// The planner of each resource, and the plans that span them: checking a saved plan before it resumes.
internal sealed class RequestPlanners
{
    readonly RequestPlanner[] planners;
    readonly ApiContext ctx;

    public RequestPlanners(ApiContext ctx)
    {
        this.ctx = ctx;
        planners = new RequestPlanner[Enum.GetValues<ResourceKind>().Length];
        foreach (var planner in new RequestPlanner[] { new AirportPlanner(ctx, this), new FleetPlanner(ctx, this), new AircraftPlanner(ctx, this), new RoutingPlanner(ctx, this), new RoutePlanner(ctx, this) })
            planners[(int)planner.Kind] = planner;
    }

    public RequestPlanner For(ResourceKind kind) => planners[(int)kind];
    public List<ApiStep> Plan(ChangeItem c, bool preview = false) => For(c.Resource).Plan(c, preview);

    internal bool TryPlanForeignColumn(RequestPlanner caller, ChangeItem c, string field, string text, bool create, bool preview, Dictionary<string, object?> body)
    {
        foreach (var planner in planners)
            if (planner != caller && planner.TryPlanColumn(c, field, text, create, preview, body))
                return true;
        return false;
    }

    // The steps to send for an item: the current plan, or, once steps were saved (a resumed task), the saved steps.
    // Saved steps are only trusted when they still say exactly what the current plan would say; otherwise the item
    // must be refreshed first, so a task never sends a request that its rows no longer justify.
    public List<ApiStep> ExecutionPlan(ChangeItem item)
    {
        var current = Plan(item);
        if (item.ApiSteps.Count == 0)
            return current;
        var saved = item.ApiSteps.Select(ApiSteps.ToStep).ToList();
        var wanted = ApiSteps.ExpectedValues(current);
        var previous = ApiSteps.ExpectedValues(saved);
        if (wanted.Count != previous.Count || wanted.Any(p => !previous.TryGetValue(p.Key, out var v) || !JsonElement.DeepEquals(p.Value, v)))
            throw Block("ApiRefreshFirst");
        if (saved.Count > 2)
            throw Block("ApiRefreshFirst");
        for (var n = 0; n < saved.Count; n++)
        {
            var step = saved[n];
            var create = item.Kind == ChangeKind.Create && n == 0;
            var move = IndependentApiEvidence.IsMove(item);
            var method = move ? HttpMethod.Patch : create ? HttpMethod.Post : item.Kind == ChangeKind.Delete ? HttpMethod.Delete : HttpMethod.Put;
            var path = move ? current.Single().Path : item.Kind == ChangeKind.Create ? (n == 0 ? ctx.Paths.Path(item.Resource, Unsaved(item.After)) : "{created}") : ctx.Paths.Path(item.Resource, item.Before ?? item.After, item.RemoteId);
            if (step.Method != method || step.Path != path)
                throw Block("ApiRefreshFirst");
            if (move)
                ContractValidator.ValidateMoveBody(step.Body);
            else if (method != HttpMethod.Delete)
                ContractValidator.ValidateBody(item.Resource, create, step.Body);
        }
        return saved;
    }

    // A create's POST targets the collection even after the response stored the new identity.
    static DataRow Unsaved(DataRow row) => row.WithIdentity(null);
}

// Plans as saved data: the values a write is expected to leave on the server, and the steps stored on the item.
internal static class ApiSteps
{
    public static Dictionary<string, JsonElement> ExpectedValues(IEnumerable<ApiStep> steps)
        => steps.SelectMany(s => s.Body).GroupBy(p => p.Key).ToDictionary(g => g.Key, g => JsonSerializer.SerializeToElement(g.Last().Value));

    public static void PrepareSteps(ChangeItem change, List<ApiStep> steps)
        => change.ApiSteps = steps.Select(s => new BatchStep { Method = s.Method.Method, Path = s.Path, Body = s.Body.ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value)) }).ToList();

    public static ApiStep ToStep(BatchStep s) => new(new HttpMethod(s.Method), s.Path, s.Body.ToDictionary(p => p.Key, p => (object?)p.Value));
}
