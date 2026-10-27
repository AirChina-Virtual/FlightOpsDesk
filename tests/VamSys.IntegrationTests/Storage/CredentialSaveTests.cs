using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using VamSys.Core;
using VamSys.Infrastructure;
using VamSys.TestSupport;

namespace VamSys.IntegrationTests.Storage;

/// <summary>
/// Saving connection settings and credentials through the real store (the secret is DPAPI-protected, so these run on Windows):
/// both entry points normalize the OAuth client ID and keep the secret byte for byte, a failed save leaves header and secret
/// unchanged, an ordinary save keeps the stored secret, and a running task blocks full settings but not a credentials rotation.
/// </summary>
public class CredentialSaveTests
{
    sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(respond(request));
    }

    static void Save(WorkspaceStore s, Workspace w, bool only, string id, string secret)
    {
        if (only) s.UpdateOperationsCredentials(w, id, secret);
        else s.SaveConnectionSettings(w, id, secret, OperationsAdapter.BaseUri.AbsoluteUri, true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Both_credential_entries_normalize_the_OAuth_ID_keep_the_secret_exactly_and_roll_back_a_failed_save(bool credentialsOnly)
    {
        using var dir = TempDataDirectory.Create("vamsys-cred-");
        var s = new WorkspaceStore(dir.Path);
        var w = new Workspace { AirlineId = "7" };
        s.Save(w);

        Save(s, w, credentialsOnly, " 123 ", " secret ");

        var c = JsonSerializer.Deserialize<OperationsCredentials>(s.LoadSecret(w.Id)!)!;
        Assert.Equal("123", w.ClientId);
        Assert.Equal("123", s.Load(w.Id).ClientId);
        Assert.Equal("123", c.ClientId);
        Assert.Equal(" secret ", c.Secret);
        // what the stored credentials send to the token endpoint
        string[]? fields = null;
        using var handler = new Handler(r =>
        {
            fields = r.Content!.ReadAsStringAsync().Result.Split('&');
            return new(HttpStatusCode.OK) { Content = new StringContent("""{"access_token":"test","token_type":"Bearer","expires_in":60}""") };
        });
        using var http = new HttpClient(handler, false);
        await TokenProvider.ClientCredentials(http, c).GetAsync(TestContext.Current.CancellationToken);
        Assert.Contains("client_id=123", fields!);
        Assert.Contains("client_secret=+secret+", fields!);

        var before = w.Serialize();
        var secret = s.LoadSecret(w.Id);
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(dir.Path, "workspaces.db"), Pooling = false }.ToString()))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "CREATE TRIGGER fail_save BEFORE UPDATE ON workspaces BEGIN SELECT RAISE(ABORT,'injected failure'); END;";
            cmd.ExecuteNonQuery();
        }
        Assert.ThrowsAny<SqliteException>(() => Save(s, w, credentialsOnly, "456", "new-secret"));
        Assert.Equal(before, w.Serialize());
        Assert.Equal(before, s.Load(w.Id).Serialize());
        Assert.Equal(secret, s.LoadSecret(w.Id));
    }

    [Fact]
    public void An_ordinary_save_keeps_the_stored_secret_repairs_a_legacy_padded_client_ID_and_refuses_a_changed_ID_without_a_secret()
    {
        using var dir = TempDataDirectory.Create("vamsys-cred-");
        var s = new WorkspaceStore(dir.Path);
        var w = new Workspace { ClientId = "123" };
        s.Save(w);
        s.SaveSecret(w.Id, JsonSerializer.Serialize(new OperationsCredentials(" 123 ", " original ")));

        s.SaveConnectionSettings(w, " 123 ", "", OperationsAdapter.BaseUri.AbsoluteUri, false);

        var creds = JsonSerializer.Deserialize<OperationsCredentials>(s.LoadSecret(w.Id)!)!;
        Assert.Equal("123", creds.ClientId);
        Assert.Equal(" original ", creds.Secret);
        var before = w.Serialize();
        var secret = s.LoadSecret(w.Id);
        Assert.Throws<InvalidOperationException>(() => s.SaveConnectionSettings(w, "456", "", OperationsAdapter.BaseUri.AbsoluteUri, true));
        Assert.Equal(before, w.Serialize());
        Assert.Equal(before, s.Load(w.Id).Serialize());
        Assert.Equal(secret, s.LoadSecret(w.Id));
        Assert.Throws<InvalidOperationException>(() => s.UpdateOperationsCredentials(w, "123", "")); // a rotation needs a new secret
    }

    [Fact]
    public void Full_settings_stay_blocked_while_a_task_is_open_but_a_credentials_only_save_preserves_it()
    {
        using var dir = TempDataDirectory.Create("vamsys-cred-");
        var s = new WorkspaceStore(dir.Path);
        var w = new Workspace { AirlineId = "7" };
        w.Jobs.Add(new() { Mode = RunMode.Online, Items = [new() { State = ItemState.Unknown }] });
        s.Save(w);
        var jobs = JsonSerializer.Serialize(w.Jobs);

        var error = Assert.Throws<InvalidOperationException>(() => s.SaveConnectionSettings(w, "123", "secret", OperationsAdapter.BaseUri.AbsoluteUri, true));
        Assert.Equal("ApiPendingJob", MessageErrors.Describe(error).Code);

        s.UpdateOperationsCredentials(w, " 123 ", "secret");
        Assert.Equal("7", w.AirlineId);
        Assert.False(w.LocalRouteTimes);
        Assert.Equal(jobs, JsonSerializer.Serialize(w.Jobs));
    }
}
