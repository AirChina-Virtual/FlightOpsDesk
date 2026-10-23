using VamSys.Core;

namespace VamSys.Infrastructure;

public static class ApiContracts
{
    public const string Version = "3.0.0";
    public const string Sha256 = "1EC1A869FC3C6F4D70A7A6ECD50170D5D915551794E796F9F34D5AFB944D78C2";
    public static IReadOnlyList<ResourceCapability> Capabilities => Enum.GetValues<ResourceKind>()
        .Select(k => new ResourceCapability(k, true, true, Messages.Define("ApiCapability"))).ToArray();
}
public sealed class UnverifiedOperationsAdapter : IResourceReader, IResourceWriter
{
    public async IAsyncEnumerable<DataRow> ReadAllAsync(ResourceKind kind, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    { await Task.FromException(new NotSupportedException()); yield break; }
    public Task<DataRow?> FindAsync(ResourceKind kind, string id, CancellationToken ct) => throw new NotSupportedException();
    public Task<string?> WriteAsync(ChangeItem change, CancellationToken ct) => throw new NotSupportedException();
}
