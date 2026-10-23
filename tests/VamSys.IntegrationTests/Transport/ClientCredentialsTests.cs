using System.Net;
using VamSys.Core;
using VamSys.Infrastructure;
using VamSys.TestSupport;

namespace VamSys.IntegrationTests.Transport;

/// <summary>OAuth client-credentials request and expires_in caching.</summary>
public class ClientCredentialsTests
{
    static string TokenJson(string token = "tok", long seconds = 3600, string type = "Bearer") => $$"""{"access_token":"{{token}}","token_type":"{{type}}","expires_in":{{seconds}}}""";

    static (FakeOperationsServer Server, TokenProvider Tokens, TestTime Time) Make(Func<RecordedRequest, FakeResponse> reply, string client = "client-a", string secret = "s&x")
    {
        var time = new TestTime();
        var server = new FakeOperationsServer { RecordTokenRequests = true };
        server.On(HttpMethod.Post, "/oauth/token", reply);
        return (server, TokenProvider.ClientCredentials(server.CreateClient(), new(client, secret), time), time);
    }

    [Fact]
    public async Task The_token_request_is_a_form_post_with_all_four_fields_including_the_wildcard_scope()
    {
        var (server, tokens, _) = Make(_ => FakeResponse.Ok(TokenJson()));
        Assert.Equal("tok", await tokens.GetAsync(TestContext.Current.CancellationToken));
        var request = Assert.Single(server.Requests);
        Assert.Equal("POST", request.Method);
        Assert.Equal("/oauth/token", request.Path);
        Assert.Equal("grant_type=client_credentials&client_id=client-a&client_secret=s%26x&scope=%2A", request.Body);
        Assert.Equal("application/json", request.Header("Accept"));
        Assert.StartsWith("application/x-www-form-urlencoded", request.Header("Content-Type"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_token_is_cached_until_one_minute_before_expiry()
    {
        var (server, tokens, time) = Make(_ => FakeResponse.Ok(TokenJson(seconds: 120)));
        await tokens.GetAsync(TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromSeconds(59));
        await tokens.GetAsync(TestContext.Current.CancellationToken);
        Assert.Single(server.Requests);
        time.Advance(TimeSpan.FromSeconds(2));
        await tokens.GetAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, server.Requests.Count);
    }

    [Fact]
    public async Task Each_token_response_decides_its_own_lifetime()
    {
        var n = 0;
        var (server, tokens, time) = Make(_ => FakeResponse.Ok(TokenJson("t" + (++n), n == 1 ? 3600 : 90)));
        Assert.Equal("t1", await tokens.GetAsync(TestContext.Current.CancellationToken));
        time.Advance(TimeSpan.FromMinutes(61));
        Assert.Equal("t2", await tokens.GetAsync(TestContext.Current.CancellationToken));
        time.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal("t3", await tokens.GetAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(401, """{"error":"invalid_client"}""")]
    [InlineData(500, "{}")]
    [InlineData(200, """{"access_token":"x","token_type":"Basic","expires_in":60}""")]
    [InlineData(200, """{"access_token":"x","token_type":"Bearer","expires_in":0}""")]
    [InlineData(200, """{"access_token":"x","token_type":"Bearer","expires_in":-5}""")]
    [InlineData(200, """{"access_token":" ","token_type":"Bearer","expires_in":60}""")]
    [InlineData(200, """{"access_token":null,"token_type":"Bearer","expires_in":60}""")]
    public async Task A_refused_or_unusable_token_response_is_an_authentication_failure(int status, string body)
    {
        var (_, tokens, _) = Make(_ => FakeResponse.WithStatus(status, body));
        await Assert.ThrowsAsync<ApiAccessException>(() => tokens.GetAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_failed_request_is_not_cached_and_the_next_call_asks_again()
    {
        var n = 0;
        var (server, tokens, _) = Make(_ => ++n == 1 ? FakeResponse.WithStatus(500) : FakeResponse.Ok(TokenJson()));
        await Assert.ThrowsAsync<ApiAccessException>(() => tokens.GetAsync(TestContext.Current.CancellationToken));
        Assert.Equal("tok", await tokens.GetAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, server.Requests.Count);
    }

    [Fact]
    public async Task Credentials_and_tokens_are_what_diagnostics_must_mask()
    {
        var (_, tokens, _) = Make(_ => FakeResponse.Ok(TokenJson("secret-token")), client: "client-a", secret: "shh");
        Assert.Equivalent(new[] { "client-a", "shh" }, tokens.SensitiveValues);
        await tokens.GetAsync(TestContext.Current.CancellationToken);
        Assert.Contains("secret-token", tokens.SensitiveValues);
        var (_, numeric, _) = Make(_ => FakeResponse.Ok(TokenJson()), client: "12345", secret: "shh");
        Assert.Equal(["shh"], numeric.SensitiveValues);
    }
}
