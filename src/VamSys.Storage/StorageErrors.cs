using VamSys.Core;

namespace VamSys.Infrastructure;

// Storage reports user-visible failures through the same localized message keys as the API layer.
internal static class StorageErrors
{
    public static InvalidOperationException Block(string key, string? field = null)
        => MessageErrors.Attach(new InvalidOperationException(Messages.Define(key, ("field", field))), Messages.Define(key, ("field", field)));
}
