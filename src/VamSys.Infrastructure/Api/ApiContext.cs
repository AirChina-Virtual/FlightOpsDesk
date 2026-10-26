using System.Text.Json;
using VamSys.Core;
using static VamSys.Infrastructure.ApiErrors;

namespace VamSys.Infrastructure;

// What every API component shares for one connection: the transport, the workspace whose rows it reads and the
// airline the credentials belong to. The lower components (identity index, paths, mappers, reader) hang off it so
// that a read-only copy for a refresh is just a second context over a scratch workspace.
internal sealed class ApiContext
{
    public ApiContext(OperationsTransport transport, Workspace workspace)
    {
        Transport = transport;
        Workspace = workspace;
        Identity = new IdentityIndex(this);
        Paths = new ResourcePaths(this);
        Mappers = new ResourceMappers(this);
        Reader = new ResourceReader(this);
    }

    public OperationsTransport Transport { get; }
    public Workspace Workspace { get; }
    public IdentityIndex Identity { get; }
    public ResourcePaths Paths { get; }
    public ResourceMappers Mappers { get; }
    public ResourceReader Reader { get; }

    // The airline named by the records this session has read. Set by Owner, never guessed.
    public string? SessionAirlineId { get; set; }
    public string ConnectionId => Workspace.AirlineId ?? throw Block("ApiConnectFirst");

    public void CheckConnection(DataRow row)
    {
        if (row.Identity != null && row.Identity.ConnectionId != ConnectionId)
            throw Block("ApiWrongVa");
    }

    // The airline a record belongs to, or null when the record documents none. The first record binds an unbound
    // workspace; a record of another airline is refused.
    public string? Owner(JsonElement value)
    {
        if (!value.TryGetProperty("airline_id", out var owner) || owner.ValueKind == JsonValueKind.Null)
            return null;
        var airline = owner.ToString();
        if (Workspace.AirlineId != null && Workspace.AirlineId != airline)
            throw Block("ApiWrongVa");
        Workspace.AirlineId ??= airline;
        SessionAirlineId = airline;
        return airline;
    }
}
