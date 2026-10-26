using System.Runtime.CompilerServices;
using VamSys.Core;
using static VamSys.Infrastructure.ApiErrors;

namespace VamSys.Infrastructure;

// Before a delete: does anything, here or on the server, still point at the record? Local drafts are checked by the
// deletion rules; the server's side is read through filters (routes and routings of an airport, aircraft and routes of a fleet).
internal sealed class DependencyChecker(ApiContext ctx)
{
    public async IAsyncEnumerable<(ResourceKind Kind, DataRow Row)> ReadDependencyCandidatesAsync(ChangeItem item, [EnumeratorCancellation] CancellationToken ct)
    {
        var id = item.Before?.Identity?.RemoteId ?? item.RemoteId ?? throw Block("ApiRefreshFirst");
        ctx.CheckConnection(item.Before ?? item.After);
        PositiveId(id);
        var seen = new HashSet<(ResourceKind, string)>();
        if (item.Resource == ResourceKind.Airports)
        {
            foreach (var kind in new[] { ResourceKind.Routes, ResourceKind.Routings })
                foreach (var side in new[] { "departure", "arrival" })
                    await foreach (var row in ctx.Reader.ReadFilteredAsync(kind, ResourcePaths.Collection(kind), new() { [side + (kind == ResourceKind.Routes ? "_id" : "_airport_id")] = id }, ct))
                        if (seen.Add((kind, row.Identity!.RemoteId)))
                            yield return (kind, row);
        }
        else if (item.Resource == ResourceKind.Fleets)
        {
            await foreach (var value in ctx.Transport.ReadPagesAsync(new(OperationsAdapter.BaseUri, $"fleet/{id}/aircraft"), ct))
            {
                var row = ctx.Mappers.For(ResourceKind.Aircraft).FromJson(value);
                if (seen.Add((ResourceKind.Aircraft, row.Identity!.RemoteId)))
                    yield return (ResourceKind.Aircraft, row);
            }
            await foreach (var row in ctx.Reader.ReadFilteredAsync(ResourceKind.Routes, "routes", new() { ["fleet_id"] = id }, ct))
                if (seen.Add((ResourceKind.Routes, row.Identity!.RemoteId)))
                    yield return (ResourceKind.Routes, row);
        }
    }

    public async Task<bool> HasDependenciesAsync(ChangeItem item, CancellationToken ct)
    {
        var service = new DeletionService();
        if (service.CheckReferences(ctx.Workspace, item.Resource, new HashSet<Guid> { item.After.LocalId }).Count > 0)
            return true;
        var context = new Workspace { AirlineId = ctx.Workspace.AirlineId };
        var target = (item.Before ?? item.After).WithLocalId(item.After.LocalId);
        context.Resources[item.Resource].Draft.Add(target);
        await foreach (var (kind, row) in ReadDependencyCandidatesAsync(item, ct))
        {
            PerformanceRun.Current?.Candidate();
            // At most one candidate is needed in the lightweight reference context.
            context.Resources[kind].Draft.Add(row);
            var blocked = service.CheckReferences(context, item.Resource, new HashSet<Guid> { target.LocalId }).Count > 0;
            context.Resources[kind].Draft.Clear();
            if (blocked)
                return true;
        }
        return false;
    }
}
