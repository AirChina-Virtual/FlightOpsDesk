using VamSys.Core;
using static VamSys.Infrastructure.ApiErrors;

namespace VamSys.Infrastructure;

// Everything about an aircraft fleet move that is not planning: the target fleet must exist before the PATCH, and the
// move is read back at the target fleet's path (the old path no longer exists once the move succeeded).
internal sealed class MovementVerifier(ApiContext ctx)
{
    public async Task PreflightAsync(ChangeItem item, CancellationToken ct)
    {
        if (!IndependentApiEvidence.IsMove(item))
            return;
        var target = await ctx.Reader.FindAsync(ResourceKind.Fleets, item.After.Get("Fleet ID"), ct);
        if (target is null)
            throw Block("ApiMoveTarget");
        ctx.CheckConnection(target);
    }

    // The GET that decides an item's outcome. A move reads the aircraft under its new fleet; everything else reads the record itself.
    public Task<DataRow?> FindResultAsync(ChangeItem item, string? id, CancellationToken ct)
    {
        var row = item.After;
        if (IndependentApiEvidence.IsMove(item))
            row = row.WithIdentity(new(id ?? item.Before!.Identity!.RemoteId, ctx.ConnectionId, item.After.Get("Fleet ID")));
        return ctx.Reader.FindRowAsync(item.Resource, row, id, ct);
    }
}
