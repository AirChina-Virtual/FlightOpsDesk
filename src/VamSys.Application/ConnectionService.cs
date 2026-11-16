namespace VamSys.Core;

// Connecting a workspace to the server, refreshing its snapshots, and the read-only airline parameters.
public sealed class ConnectionService(WorkspaceSession session, IOnlineGateway online)
{
    // A documented list call checks the token, permissions and airline identity. The workspace becomes online and the
    // connection state is saved on its own (header only) before the optional parameters are read.
    // Returns whether the airline parameters could be read.
    public async Task<bool> ConnectAsync(CancellationToken ct)
    {
        var workspace = session.Workspace;
        if (!await online.Session(workspace).IdentifyAirlineAsync(ct))
            throw ApplicationBlocks.Block("ApiConnectFirst");
        workspace.ConnectedAt = DateTimeOffset.UtcNow;
        workspace.Mode = RunMode.Online;
        if (workspace.ContractHash != online.ContractHash)
            workspace.VerifiedOperations.Clear();
        workspace.ContractHash = online.ContractHash;
        await session.SaveHeaderAsync();
        return await ReadOptionalParametersAsync(ct);
    }

    // False when the workspace is not connected (the person has to connect first); throws while online tasks are
    // pending, running or unknown or have skipped items "Continue" can still send (ApiOperationPolicy.OpenItems), because
    // a refresh would compare against records those tasks may change.
    public bool CanRefresh()
    {
        var workspace = session.Workspace;
        if (workspace.Mode != RunMode.Online)
            return false;
        if (ApiOperationPolicy.HasOpenOnlineItems(workspace))
            throw ApplicationBlocks.Block("ApiPendingJob");
        return true;
    }

    // Reads every resource from the server (or one and what it refers to, E5) and stages it, saved once all was read.
    // Returns whether the airline parameters could be read; they belong to fleets and routes, so a refresh of only
    // airports, aircraft or routings leaves them as they were.
    public async Task<bool> RefreshAsync(Action<ResourceKind, int> progress, CancellationToken ct, ResourceKind? only = null)
    {
        var api = online.Session(session.Workspace);
        await api.RefreshSnapshotsAsync(session.SaveStagedAsync, ct, progress, only is { } kind ? [kind] : null);
        if (only is ResourceKind.Airports or ResourceKind.Aircraft or ResourceKind.Routings)
            return true;
        return await ReadOptionalParametersAsync(ct);
    }

    // A server that refuses the parameters leaves the connection usable; only an access error stops the connection.
    async Task<bool> ReadOptionalParametersAsync(CancellationToken ct)
    {
        try
        { await online.Session(session.Workspace).ReadParametersAsync(ct, true); return true; }
        catch (ApiAccessException) { throw; }
        catch { online.Session(session.Workspace).InvalidateParameters(); return false; }
    }

    // The settings page changed the credentials or the connection settings: the old session must not be reused.
    public void DropSession(Guid workspaceId) => online.Release(workspaceId);
}
