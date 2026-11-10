using System.Text.Json;
using VamSys.Core;

namespace VamSys.Infrastructure;

public record AccessToken(string Value, DateTimeOffset ExpiresAt);
public sealed class TokenProvider(Func<CancellationToken, Task<AccessToken>> acquire, TimeProvider? timeProvider = null)
{
    internal string[] CredentialValues { get; private init; } = [];
    internal IEnumerable<string> SensitiveValues => cached is null ? CredentialValues : CredentialValues.Append(cached.Value);
    readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    readonly SemaphoreSlim gate = new(1); AccessToken? cached;
    public async Task<string> GetAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        { if (cached is null || cached.ExpiresAt <= clock.GetUtcNow().AddMinutes(1)) cached = await acquire(ct); return cached.Value; }
        finally { gate.Release(); }
    }
    public void Invalidate() => cached = null;
    public static TokenProvider ClientCredentials(HttpClient client, OperationsCredentials credentials, TimeProvider? timeProvider = null) => new(async ct =>
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, OperationsEndpoints.Token);
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "client_credentials", ["client_id"] = credentials.ClientId, ["client_secret"] = credentials.Secret, ["scope"] = "*" });
        request.Headers.Accept.Add(new("application/json"));
        PerformanceRun.Current?.Request("OAuth", "POST");
        using var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new ApiAccessException(Messages.Define("ApiAuth"));
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var token = json.RootElement.GetProperty("access_token").GetString();
        var seconds = json.RootElement.GetProperty("expires_in").GetInt64();
        if (string.IsNullOrWhiteSpace(token) || seconds <= 0 || !json.RootElement.GetProperty("token_type").GetString()!.Equals("Bearer", StringComparison.OrdinalIgnoreCase))
            throw new ApiAccessException(Messages.Define("ApiAuth"));
        return new AccessToken(token, (timeProvider ?? TimeProvider.System).GetUtcNow().AddSeconds(seconds));
        // A numeric Client ID is a public identifier that collides with record IDs and numbers in
        // diagnostic bodies, so it is masked by key (client_id) only. Secret and token stay literal.
    }, timeProvider)
    { CredentialValues = credentials.ClientId.All(char.IsAsciiDigit) ? [credentials.Secret] : [credentials.ClientId, credentials.Secret] };
}
