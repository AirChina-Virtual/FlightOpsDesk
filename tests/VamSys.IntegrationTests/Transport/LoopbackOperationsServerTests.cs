using System.Net;
using VamSys.Infrastructure;
using VamSys.TestSupport;

namespace VamSys.IntegrationTests.Transport;

/// <summary>4.1: the mock server the window tests point the QA package at, over real HTTP on this machine.</summary>
public class LoopbackOperationsServerTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task It_answers_over_http_on_the_loopback_address_like_the_fake_server()
    {
        var remote = new FakeRemote();
        remote.AddRow("fleet/4", """{"id":4,"airline_id":7,"name":"Fleet 4","code":"B738","type":"pax"}""");
        using var server = new LoopbackOperationsServer(remote.Server);
        using var client = new HttpClient { BaseAddress = server.Origin };
        Assert.True(server.Origin.IsLoopback);
        Assert.Equal("http", server.Origin.Scheme);

        using var form = new FormUrlEncodedContent([new("grant_type", "client_credentials")]);
        using var token = await client.PostAsync("oauth/token", form, Ct);
        Assert.Equal(HttpStatusCode.OK, token.StatusCode);
        Assert.Contains("access_token", await token.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        var list = await client.GetStringAsync("api/v3/operations/fleet?page%5Bsize%5D=100", Ct);
        Assert.Contains("\"Fleet 4\"", list, StringComparison.Ordinal);
        using var body = new StringContent("""{"name":"Renamed"}""", System.Text.Encoding.UTF8, "application/json");
        using var put = await client.PutAsync("api/v3/operations/fleet/4", body, Ct);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal("Renamed", remote.Rows["fleet/4"]["name"]!.ToString());
        Assert.Equal(["GET", "PUT"], remote.Server.Requests.Select(r => r.Method));
        Assert.Equal("""{"name":"Renamed"}""", remote.Server.Requests[1].Body);
    }

    [Fact]
    public async Task A_scripted_transport_failure_drops_the_connection()
    {
        var remote = new FakeRemote { Intercept = _ => throw new TimeoutException("simulated") };
        using var server = new LoopbackOperationsServer(remote.Server);
        using var client = new HttpClient { BaseAddress = server.Origin };
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetStringAsync("api/v3/operations/fleet", Ct));
        Assert.IsType<TimeoutException>(Assert.Single(server.Failures));
    }

    [Fact]
    public void Outside_the_QA_package_plain_http_is_still_refused_even_on_this_machine()
    {
        Assert.Equal("https://vamsys.io/", OperationsEndpoints.Origin.AbsoluteUri);
        var refused = Assert.Throws<InvalidOperationException>(() => OriginPolicy.Ensure(new Uri("http://localhost:5000/"), new Uri("http://localhost:5000/api/v3/operations/fleet")));
        Assert.NotNull(refused);
    }
}
