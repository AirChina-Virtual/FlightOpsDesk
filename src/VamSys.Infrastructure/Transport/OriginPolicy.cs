using VamSys.Core;

namespace VamSys.Infrastructure;

// Requests are HTTPS, same origin as the Operations base address, below its path, without credentials in the URL.
internal static class OriginPolicy
{
    public static bool SameOrigin(Uri a, Uri b) => a.Scheme == b.Scheme && a.Host == b.Host && a.Port == b.Port;
    public static void Ensure(Uri origin, Uri uri)
    {
        if (uri.Scheme != "https" && !OperationsEndpoints.AllowsPlainHttp(uri) || !SameOrigin(origin, uri) || uri.UserInfo != "" || (origin.AbsolutePath != "/" && !uri.AbsolutePath.StartsWith(origin.AbsolutePath, StringComparison.Ordinal)))
            throw new InvalidOperationException(Messages.Define("Text_A69A8C3CDE"));
    }
}
