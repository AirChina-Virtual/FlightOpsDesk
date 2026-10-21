using VamSys.Core;

namespace VamSys.Infrastructure;

public sealed partial class OperationsAdapter
{
    ApiStep MovePlan(ChangeItem item, bool preview)
    {
        if (item.Before?.Identity?.ParentFleetId is null || item.After.Identity?.RemoteId != item.Before.Identity.RemoteId || item.Before.LocalId != item.After.LocalId)
            throw Block("ApiRefreshFirst");
        CheckConnection(item.Before!);
        CheckConnection(item.After);
        if (item.Fields.Any(p => p.Key != "Fleet ID" && p.Key != "_delete" && p.Value.Intent != FieldIntent.Unspecified))
            throw Block("ApiMoveSeparate");
        var target = item.After.Get("Fleet ID");
        if (workspace.Resources[ResourceKind.Fleets].Draft.Any(r => r.Identity?.RemoteId == target && Schemas.Deleted(r)))
            throw Block("ApiMoveTarget");
        if (item.Fields["Fleet ID"].Intent != FieldIntent.Set || item.Fields["Fleet ID"].Value != target)
            throw Block("ApiInvalid", "Fleet ID");
        if (preview && LocalReference.IsLocal(target))
            PreviewReference(target, ResourceKind.Fleets);
        else
            PositiveId(target);
        if (target == item.Before!.Identity?.ParentFleetId)
            throw Block("ApiNoChanges");
        var fleet = LocalReference.IsLocal(target) ? LocalDraft(ResourceKind.Fleets, target) : Known(ResourceKind.Fleets, target);
        if (fleet is null || Schemas.Deleted(fleet))
            throw Block("ApiMoveTarget");
        CheckConnection(fleet);
        if (!LocalReference.IsLocal(target) && fleet.Identity?.RemoteId != target)
            throw Block("ApiMoveTarget");
        var body = new Dictionary<string, object?> { ["fleet_id"] = preview ? PreviewReference(target, ResourceKind.Fleets) : PositiveId(target) };
        ValidateMoveBody(body);
        return new(HttpMethod.Patch, $"aircraft/{PositiveId(item.Before.Identity!.RemoteId)}", body);
    }
    static void ValidateMoveBody(Dictionary<string, object?> body)
    {
        if (body.Count != 1 || !body.ContainsKey("fleet_id"))
            throw Block("ApiMoveSeparate");
        using var json = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(body));
        ValidateSchema(SchemasJson.GetProperty("MoveAircraftFleetData"), json.RootElement, "MoveAircraftFleetData");
    }
    internal async Task PreflightMoveAsync(ChangeItem item, CancellationToken ct)
    {
        if (!IndependentApiEvidence.IsMove(item))
            return;
        var target = await FindAsync(ResourceKind.Fleets, item.After.Get("Fleet ID"), ct);
        if (target is null)
            throw Block("ApiMoveTarget");
        CheckConnection(target);
    }
    internal Task<DataRow?> FindResult(ChangeItem item, string? id, CancellationToken ct)
    {
        var row = item.After;
        if (IndependentApiEvidence.IsMove(item))
        {
            row = row.Copy();
            row.Identity = new(id ?? item.Before!.Identity!.RemoteId, ConnectionId, item.After.Get("Fleet ID"));
        }
        return FindRow(item.Resource, row, id, ct);
    }
}
