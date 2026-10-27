using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using VamSys.Core;
using VamSys.Infrastructure;

// Backs the --export-diagnostic-replay command line entry: simulated 500/422 answers become a sanitized diagnostic export
// plus its SHA-256, which the window scripts and packaging checks read. Nothing here is asserted as a test scenario.
static class DiagnosticReplayExport
{
    public static async Task ExportDiagnostics(string path)
    {
        using var client = new HttpClient(new SimulatedFailures());
        var time = InstantTime.Requests();
        var transport = new OperationsTransport(client, OperationsAdapter.BaseUri, "export-fixture",
            TokenProvider.ClientCredentials(client, new("fixture-client", "fixture-secret"), time.Clock), time);
        var unknown = new ChangeItem { Resource = ResourceKind.Fleets, Kind = ChangeKind.Update, State = ItemState.Unknown };
        var rejected = new ChangeItem { Resource = ResourceKind.Routes, State = ItemState.Failed };
        foreach (var (item, target) in new[] { (unknown, "fleet/4"), (rejected, "routes/10") })
            try
            { await transport.SendAsync(HttpMethod.Put, new(OperationsAdapter.BaseUri, target), new { fleet_ids = new[] { 4 } }, default); }
            catch (ApiResponseException error)
            { item.DiagnosticDetails = error.Details; item.Diagnostic = error.Diagnostic; }
        var json = ApiDiagnosticExport.Serialize(new BatchJob { Mode = RunMode.Online, Items = [unknown, rejected] });
        if (json.Contains("fixture-secret") || json.Contains("fixture-client") || json.Contains("fixture-token"))
            throw new InvalidOperationException("The diagnostic export leaked a credential");
        File.WriteAllText(path, json);
        File.WriteAllText(path + ".sha256", Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
    }

    sealed class SimulatedFailures : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath == "/oauth/token")
                return Task.FromResult(ScriptedHttp.Json("""{"access_token":"fixture-token","token_type":"Bearer","expires_in":3600}"""));
            var server = request.RequestUri.AbsolutePath.EndsWith("fleet/4", StringComparison.Ordinal);
            var body = server
                ? JsonSerializer.Serialize(new { message = "Server Error", error_id = "fixture-server-error" })
                : JsonSerializer.Serialize(new
                {
                    message = "fixture-secret fixture-client fixture-token", error_id = "fixture-error",
                    errors = new Dictionary<string, string[]> { { "fleet_ids.0", [new string('x', 9000)] }, { "late", ["last validation message"] } },
                    nested = new { access_token = "fixture-token", client_secret = "fixture-secret" }
                });
            var response = ScriptedHttp.Json(body, server ? HttpStatusCode.InternalServerError : HttpStatusCode.UnprocessableEntity);
            response.Headers.Date = DateTimeOffset.UtcNow;
            response.Headers.Add("CF-Ray", "fixture-correlation");
            return Task.FromResult(response);
        }
    }
}
