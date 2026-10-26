using System.Globalization;
using VamSys.Core;
using static VamSys.Infrastructure.ApiErrors;

namespace VamSys.Infrastructure;

// Where a record lives: the collection path of a resource and the path of one row. Every id that reaches a path is
// a positive integer, so nothing else can be injected into a request address.
internal sealed class ResourcePaths(ApiContext ctx)
{
    public static string Collection(ResourceKind k) => k switch
    {
        ResourceKind.Airports => "airports",
        ResourceKind.Fleets => "fleet",
        ResourceKind.Routings => "routings",
        ResourceKind.Routes => "routes",
        _ => throw new ArgumentException()
    };

    // Aircraft have no collection of their own: they live under their fleet. preview: a fleet that is still a local
    // draft stands for a placeholder id, because a preview plan is shown before anything has been created.
    public string Path(ResourceKind kind, DataRow row, string? remoteId = null, bool preview = false)
    {
        var identity = row.Identity;
        if (identity != null && identity.ConnectionId != ctx.ConnectionId)
            throw Block("ApiWrongVa");
        var id = remoteId ?? identity?.RemoteId;
        if (kind == ResourceKind.Aircraft)
        {
            var parent = identity?.ParentFleetId ?? row.Get("Fleet ID");
            if (preview && LocalReference.IsLocal(parent))
                parent = ctx.Identity.PreviewReference(parent, ResourceKind.Fleets).ToString(CultureInfo.InvariantCulture);
            PositiveId(parent);
            return $"fleet/{parent}/aircraft" + (id == null ? "" : "/" + PositiveId(id));
        }
        return Collection(kind) + (id == null ? "" : "/" + PositiveId(id));
    }
}
