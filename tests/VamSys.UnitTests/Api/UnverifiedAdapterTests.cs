using VamSys.Core;
using VamSys.Infrastructure;

namespace VamSys.UnitTests.Api;

public class UnverifiedAdapterTests
{
    [Fact]
    public void Every_documented_resource_is_declared_readable_and_writable()
    {
        Assert.Equal(Enum.GetValues<ResourceKind>().Length, ApiContracts.Capabilities.Count);
        Assert.All(ApiContracts.Capabilities, c => { Assert.True(c.CanRead); Assert.True(c.CanWrite); });
    }

    [Fact]
    public async Task The_legacy_unverified_adapter_rejects_a_write_for_every_resource()
    {
        foreach (var kind in Enum.GetValues<ResourceKind>())
            await Assert.ThrowsAsync<NotSupportedException>(() =>
                new UnverifiedOperationsAdapter().WriteAsync(new ChangeItem { Resource = kind, Kind = ChangeKind.Delete }, TestContext.Current.CancellationToken));
    }
}
