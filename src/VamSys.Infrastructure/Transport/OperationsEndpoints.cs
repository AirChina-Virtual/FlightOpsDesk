namespace VamSys.Infrastructure;

// The server the program talks to: vamsys.io. Only the QA build can point it at a mock server on this machine
// (VAMSYS_QA_OPERATIONS_ORIGIN, e.g. http://127.0.0.1:5123/) so the window tests can open the dialogs that appear only
// while connected. The product build has no such switch, and the package check makes sure of it.
public static class OperationsEndpoints
{
    public static readonly Uri Origin = TestOrigin() ?? new("https://vamsys.io/");
    public static readonly Uri Operations = new(Origin, "api/v3/operations/");
    public static readonly Uri Token = new(Origin, "oauth/token");

    // Plain http only for the QA mock server on the loopback address; everything else must be https.
    internal static bool AllowsPlainHttp(Uri uri) => uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback && Origin.Scheme == Uri.UriSchemeHttp;

    static Uri? TestOrigin()
    {
#if VAMSYS_TEST_SUPPORT
        if (Environment.GetEnvironmentVariable("VAMSYS_QA_OPERATIONS_ORIGIN") is { Length: > 0 } text
            && Uri.TryCreate(text, UriKind.Absolute, out var origin) && origin.IsLoopback && origin.AbsolutePath == "/")
            return origin;
#endif
        return null;
    }
}
