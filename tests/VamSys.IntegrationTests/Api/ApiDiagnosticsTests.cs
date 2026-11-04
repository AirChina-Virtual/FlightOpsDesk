using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VamSys.Core;
using VamSys.Infrastructure;
using VamSys.IntegrationTests.Transport;
using VamSys.TestSupport;

namespace VamSys.IntegrationTests.Api;

/// <summary>
/// What a failed exchange leaves behind as evidence, and that none of it carries a credential: the full 422 field list, the
/// error id, timing and trace headers, bounded bodies, header-like text in any shape, the export, and the retained real
/// responses of 2026-10-20 replayed through the transport (a write is sent once, whatever the answer).
/// </summary>
public class ApiDiagnosticsTests
{
    static readonly HttpMethod Put = HttpMethod.Put;

    [Fact]
    public async Task A_422_keeps_every_field_error_the_error_id_timing_and_trace_headers_without_any_credential()
    {
        using var server = new FakeOperationsServer { RecordTokenRequests = true };
        server.On(HttpMethod.Post, "/oauth/token", FakeResponse.Ok("""{"access_token":"fixture-token","token_type":"Bearer","expires_in":3600}"""));
        server.On(Put, "fleet/4", FakeResponse.WithStatus(422, JsonSerializer.Serialize(new
        {
            message = "fixture-secret fixture-client fixture-token",
            error_id = "fixture-error",
            errors = new Dictionary<string, string[]> { { "fleet_ids.0", [new string('x', 9000)] }, { "late", ["last validation message"] } },
            nested = new { access_token = "fixture-token", client_secret = "fixture-secret" }
        })) with { Headers = new Dictionary<string, string> { ["CF-Ray"] = "fixture-correlation", ["Date"] = "Tue, 20 Oct 2026 12:24:10 GMT" } });
        using var client = server.CreateClient();
        var time = InstantTime.Requests();
        var transport = new OperationsTransport(client, OperationsAdapter.BaseUri, "diagnostic",
            TokenProvider.ClientCredentials(client, new("fixture-client", "fixture-secret"), time.Clock), time);

        var error = await Assert.ThrowsAsync<ApiResponseException>(() => transport.SendAsync(Put, TransportFixture.Url("fleet/4"), new { client_secret = "fixture-secret" }, TestContext.Current.CancellationToken));

        var d = error.Details!;
        Assert.Equal(422, d.Status);
        Assert.Equal("PUT", d.Method);
        Assert.Equal("fixture-error", d.ErrorId);
        Assert.Equal(2, d.FieldErrors.Count);
        Assert.Equal("last validation message", d.FieldErrors["late"][0]);
        Assert.True(d.ResponseBody!.Length > 8000);
        Assert.False(d.BodyTruncated);
        Assert.Equal("fixture-correlation", d.TraceHeaders["cf-ray"]);
        Assert.NotNull(d.ServerDate);
        Assert.True(d.FinishedAt >= d.StartedAt);
        var json = JsonSerializer.Serialize(d);
        Assert.DoesNotContain("fixture-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("fixture-client", json, StringComparison.Ordinal);
        Assert.DoesNotContain("fixture-token", json, StringComparison.Ordinal);
        // the sign-in is a request of its own and is never the captured business operation
        Assert.Equal(["POST /oauth/token", "PUT /api/v3/operations/fleet/4"], server.Requests.Select(r => r.Method + " " + r.Path));
    }

    static async Task<ApiDiagnostic> RefusedPut(string clientId, string url, string body)
    {
        using var server = new FakeOperationsServer();
        server.On(HttpMethod.Post, "/oauth/token", FakeResponse.Ok("""{"access_token":"fixture-token","token_type":"Bearer","expires_in":3600}"""));
        server.On(Put, "fleet/3", FakeResponse.WithStatus(422, body));
        using var client = server.CreateClient();
        var time = InstantTime.Requests();
        var transport = new OperationsTransport(client, OperationsAdapter.BaseUri, "diagnostic",
            TokenProvider.ClientCredentials(client, new(clientId, "fixture-secret"), time.Clock), time);
        var error = await Assert.ThrowsAsync<ApiResponseException>(() => transport.SendAsync(Put, TransportFixture.Url(url), new { }, TestContext.Current.CancellationToken));
        return error.Details!;
    }

    // A short Client ID such as 3 once turned every 3 in the answer into [REDACTED], error ids included.
    [Fact]
    public async Task A_numeric_client_id_is_left_in_the_answer_while_the_secret_is_still_masked()
    {
        var details = await RefusedPut("3", "fleet/3", """{"message":"route 3 refused fixture-secret","error_id":"3"}""");

        Assert.Equal("route 3 refused [REDACTED]", details.ServerMessage);
        Assert.Equal("3", details.ErrorId);
    }

    // The path once went through the same masking, and a Client ID equal to "/v3/" or a record ID broke the delete proofs.
    [Fact]
    public async Task A_client_id_found_in_the_path_stays_there_and_is_masked_in_the_query()
    {
        var details = await RefusedPut("fleet", "fleet/3?note=fleet", """{"message":"refused"}""");

        Assert.Equal("/api/v3/operations/fleet/3?note=[REDACTED]", details.Path);
    }

    [Fact]
    public void A_huge_body_is_cut_on_a_character_boundary_with_a_hash_of_the_whole_and_without_secrets()
    {
        var bounded = ApiDiagnosticCapture.Body(new(), JsonSerializer.Serialize(new { message = new string('界', 500000), access_token = "hidden" }));
        Assert.True(bounded.BodyTruncated);
        Assert.True(Encoding.UTF8.GetByteCount(bounded.ResponseBody!) <= 1024 * 1024);
        Assert.Equal(64, bounded.BodySha256!.Length);
        Assert.DoesNotContain("hidden", bounded.ResponseBody!, StringComparison.Ordinal);
    }

    [Fact]
    public void Header_like_credentials_are_masked_in_text_that_is_not_json_and_in_json_messages()
    {
        var plain = ApiDiagnosticCapture.Redact("Cookie: session=private-cookie; second=secret\r\nSet-Cookie: jwt=private-set-cookie; HttpOnly\r\nProxy-Authorization: Basic private-proxy\r\n{invalid JSON");
        Assert.DoesNotContain("private-", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("second=secret", plain, StringComparison.Ordinal);
        var echoed = ApiDiagnosticCapture.Redact(JsonSerializer.Serialize(new { message = "Proxy-Authorization: Basic private-proxy\nCookie: session=private-cookie" }));
        Assert.DoesNotContain("private-", echoed, StringComparison.Ordinal);
    }

    [Fact]
    public void Exporting_an_already_sanitized_body_changes_neither_its_text_nor_its_hash()
    {
        var saved = ApiDiagnosticCapture.Body(new(), """{"message":"safe","client_secret":"hidden","errors":{"late":["retain me"]}}""");
        using var export = JsonDocument.Parse(ApiDiagnosticExport.Serialize(new BatchJob { Items = [new() { DiagnosticDetails = saved }] }));
        var body = export.RootElement.GetProperty("items")[0].GetProperty("DiagnosticDetails").GetProperty("ResponseBody").GetString()!;
        Assert.Contains("retain me", body, StringComparison.Ordinal);
        Assert.DoesNotContain("hidden", body, StringComparison.Ordinal);
        Assert.Equal(saved.BodySha256, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))));
    }

    // ---- retained real responses ------------------------------------------------------------------------------

    public static TheoryData<string> ReplayCases()
    {
        var data = new TheoryData<string>();
        using var file = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "vcca-weight-20261020.json")));
        foreach (var sample in file.RootElement.GetProperty("cases").EnumerateArray())
            data.Add(sample.GetProperty("name").GetString()!);
        return data;
    }

    [Theory]
    [MemberData(nameof(ReplayCases))]
    public async Task A_retained_live_response_is_read_as_the_live_server_sent_it_and_the_write_is_sent_once(string name)
    {
        using var file = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "vcca-weight-20261020.json")));
        var sample = file.RootElement.GetProperty("cases").EnumerateArray().Single(c => c.GetProperty("name").GetString() == name);
        var path = sample.GetProperty("path").GetString()!;
        var status = sample.GetProperty("status").GetInt32();
        var f = new TransportFixture();
        f.Server.On(Put, path, FakeResponse.WithStatus(status, sample.GetProperty("response").GetRawText()) with
        { Headers = new Dictionary<string, string> { ["CF-Ray"] = sample.GetProperty("cfRay").GetString()!, ["Date"] = sample.GetProperty("serverDate").GetString()! } });

        if (status == 200)
        {
            using var result = await f.Transport.SendAsync(Put, TransportFixture.Url(path), sample.GetProperty("request"), TestContext.Current.CancellationToken);
            var data = result.RootElement.TryGetProperty("data", out var wrapped) ? wrapped : result.RootElement;
            var aircraft = path.Contains("aircraft", StringComparison.Ordinal);
            var row = new OperationsAdapter(f.Transport, new Workspace { AirlineId = "7" }).FromJson(aircraft ? ResourceKind.Aircraft : ResourceKind.Fleets, data);
            if (aircraft)
                Assert.Equal(data.GetProperty("cargo").GetInt64().ToString(), row.Get("Freight"));
            else
            {
                Assert.Equal("22046", row.Get("Max Freight"));
                Assert.Equal("kg", data.GetProperty("weight_unit").GetString());
            }
            Assert.Contains("simbrief_overrides", row.RawApiJson!, StringComparison.Ordinal);
        }
        else
        {
            var error = await Assert.ThrowsAsync<ApiResponseException>(() => f.Transport.SendAsync(Put, TransportFixture.Url(path), sample.GetProperty("request"), TestContext.Current.CancellationToken));
            Assert.Equal(status, (int)error.Status);
            Assert.Equal(sample.GetProperty("response").GetProperty("error_id").GetString(), error.Details!.ErrorId);
        }
        Assert.Single(f.Server.Requests);
        Assert.NotNull(f.Transport.LastResponse!.ServerDate);
    }
}
