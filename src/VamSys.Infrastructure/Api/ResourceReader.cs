using System.Net;
using System.Runtime.CompilerServices;
using VamSys.Core;
using static VamSys.Infrastructure.ApiErrors;

namespace VamSys.Infrastructure;

// Every GET that turns server records into rows: whole collections, filtered collections, one record, and the
// airline lookup that names the VA behind the credentials. It never writes.
internal sealed class ResourceReader(ApiContext ctx)
{
    public static Uri FilterUri(string path, IEnumerable<KeyValuePair<string, string>> filters)
        => new(OperationsAdapter.BaseUri, path + "?" + string.Join('&', filters.Select(p => Uri.EscapeDataString("filter[" + p.Key + "]") + "=" + Uri.EscapeDataString(p.Value))));

    public async IAsyncEnumerable<DataRow> ReadAllAsync(ResourceKind kind, [EnumeratorCancellation] CancellationToken ct)
    {
        if (kind == ResourceKind.Aircraft)
        {
            await foreach (var row in ReadAircraftAsync(ReadAllAsync(ResourceKind.Fleets, ct), ct))
                yield return row;
        }
        else
            await foreach (var element in ctx.Transport.ReadPagesAsync(new(OperationsAdapter.BaseUri, ResourcePaths.Collection(kind)), ct))
                yield return ctx.Mappers.For(kind).FromJson(element);
    }

    // Aircraft have no collection endpoint; they are read fleet by fleet.
    public async IAsyncEnumerable<DataRow> ReadAircraftAsync(IAsyncEnumerable<DataRow> fleets, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var fleet in fleets.WithCancellation(ct))
            await foreach (var element in ctx.Transport.ReadPagesAsync(new(OperationsAdapter.BaseUri, $"fleet/{fleet.Identity!.RemoteId}/aircraft"), ct))
                yield return ctx.Mappers.For(ResourceKind.Aircraft).FromJson(element);
    }

    public async IAsyncEnumerable<DataRow> ReadFilteredAsync(ResourceKind kind, string path, Dictionary<string, string> filters, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var value in ctx.Transport.ReadPagesAsync(FilterUri(path, filters), ct))
            yield return ctx.Mappers.For(kind).FromJson(value);
    }

    public async Task<DataRow?> FindAsync(ResourceKind kind, string id, CancellationToken ct)
    {
        var row = ctx.Identity.Known(kind, id);
        if (kind == ResourceKind.Aircraft && row == null)
            throw Block("ApiRefreshFirst");
        return await FindRowAsync(kind, row ?? new DataRow { Identity = new(id, ctx.ConnectionId) }, id, ct);
    }

    // One GET of a record; 404 is the only status that means "not there". A record of another VA is refused.
    public async Task<DataRow?> FindRowAsync(ResourceKind kind, DataRow row, string? id, CancellationToken ct)
    {
        try
        {
            using var json = await ctx.Transport.GetAsync(new(OperationsAdapter.BaseUri, ctx.Paths.Path(kind, row, id)), ct);
            var root = json.RootElement;
            var found = ctx.Mappers.For(kind).FromJson(kind == ResourceKind.Routings ? root : root.GetProperty("data"));
            if (found.Identity!.RemoteId != (id ?? row.Identity?.RemoteId))
                throw Block("ApiWrongVa");
            return found;
        }
        catch (ApiResponseException ex) when (ex.Status == HttpStatusCode.NotFound) { return null; }
    }

    // Fleets or airports name the VA. A new, empty VA has neither; ranks and containers also carry airline_id.
    public async Task<bool> IdentifyAirlineAsync(CancellationToken ct)
    {
        await foreach (var _ in ReadAllAsync(ResourceKind.Fleets, ct))
            break;
        if (ctx.SessionAirlineId == null)
            await foreach (var _ in ReadAllAsync(ResourceKind.Airports, ct))
                break;
        foreach (var path in new[] { "ranks", "containers" })
        {
            if (ctx.SessionAirlineId != null)
                break;
            try
            { await foreach (var value in ctx.Transport.ReadPagesAsync(new(OperationsAdapter.BaseUri, path), ct)) { ctx.Owner(value); break; } }
            catch (Exception e) when (e is ApiAccessException or ApiResponseException) { }
        }
        return ctx.SessionAirlineId != null;
    }

    // Reads that need to know which VA they talk to call this first.
    public async Task EnsureReadConnectionAsync(CancellationToken ct)
    {
        if (ctx.SessionAirlineId == ctx.ConnectionId)
            return;
        await IdentifyAirlineAsync(ct);
        if (ctx.SessionAirlineId != ctx.ConnectionId)
            throw Block("ApiConnectFirst");
    }
}
