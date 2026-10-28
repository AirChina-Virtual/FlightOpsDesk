using VamSys.Core;
using VamSys.Presentation;
using VamSys.UnitTests.Application;

namespace VamSys.UnitTests.Presentation;

public class SettingsViewModelTests
{
    sealed class Fixture
    {
        public readonly FakeStorage Storage = new();
        public readonly InteractionGate Gate = new();
        public readonly WorkspaceSession Session;
        public readonly LocalizationService Language = new();
        public readonly List<Guid> Released = [];
        public Fixture()
        {
            var seeded = Storage.Seed();
            seeded.ClientId = "stored-client";
            seeded.LocalRouteTimes = true;
            Storage.Save(seeded);
            Session = new(Storage, Gate, new FakeTimer());
            Session.Open("default");
        }
        public SettingsViewModel Model(IUserSettings? settings = null)
            => new(Session, Gate, settings ?? Storage, Language, "https://vamsys.io/api/v3/operations", Released.Add);
    }

    sealed class FailingSettings : IUserSettings
    {
        public string LoadLanguage() => "zh-CN";
        public void SaveLanguage(string language) => throw new IOException("injected");
        public string LoadTableDensity() => "comfortable";
        public void SaveTableDensity(string density) { }
    }

    [Fact]
    public void The_page_opens_with_the_stored_client_id_route_time_choice_and_endpoint()
    {
        var model = new Fixture().Model();
        Assert.Equal("stored-client", model.ClientId);
        Assert.True(model.LocalRouteTimes);
        Assert.Equal("https://vamsys.io/api/v3/operations", model.EndpointUrl);
        Assert.True(model.CanUpdateCredentials);
    }

    [Fact]
    public void Updating_credentials_is_offered_only_when_no_exclusive_work_runs_as_the_page_is_built()
    {
        var fixture = new Fixture();
        using (fixture.Gate.TryBeginWork())
            Assert.False(fixture.Model().CanUpdateCredentials);
        Assert.True(fixture.Model().CanUpdateCredentials);
    }

    [Fact]
    public async Task Saving_stores_what_was_typed_shows_the_stored_client_id_and_drops_the_connection()
    {
        var fixture = new Fixture();
        fixture.Storage.StoredClientId = id => id.Trim();
        var model = fixture.Model();
        model.ClientId = "  typed-client ";
        model.LocalRouteTimes = false;
        Assert.Equal("Text_DEADA9F3CC", await model.SaveConnectionAsync("typed-secret"));
        Assert.Equal((fixture.Session.Workspace.Id, "  typed-client ", "typed-secret"), fixture.Storage.Credentials.Single());
        Assert.False(fixture.Session.Workspace.LocalRouteTimes);
        Assert.Equal("typed-client", model.ClientId);
        Assert.Equal([fixture.Session.Workspace.Id], fixture.Released);
        // The secret is passed through, never kept by the model.
        Assert.DoesNotContain(typeof(SettingsViewModel).GetProperties(), p => p.PropertyType == typeof(string) && (string?)p.GetValue(model) == "typed-secret");
    }

    [Fact]
    public async Task Updating_credentials_stores_them_and_reports_its_own_message()
    {
        var fixture = new Fixture();
        var model = fixture.Model();
        model.ClientId = "rotated-client";
        Assert.Equal("ApiCredentialsUpdated", await model.UpdateCredentialsAsync("rotated-secret"));
        Assert.Contains("credentials", fixture.Storage.Calls);
        Assert.Equal("rotated-client", fixture.Session.Workspace.ClientId);
        Assert.Equal([fixture.Session.Workspace.Id], fixture.Released);
    }

    [Fact]
    public async Task Nothing_is_saved_or_released_while_exclusive_work_runs()
    {
        var fixture = new Fixture();
        var model = fixture.Model();
        model.ClientId = "typed-client";
        using (fixture.Gate.TryBeginWork())
        {
            Assert.Null(await model.SaveConnectionAsync("secret"));
            Assert.Null(await model.UpdateCredentialsAsync("secret"));
        }
        Assert.Empty(fixture.Storage.Credentials);
        Assert.Empty(fixture.Released);
        Assert.Equal("typed-client", model.ClientId);
    }

    [Fact]
    public void Choosing_a_language_stores_it_first_and_then_switches()
    {
        var fixture = new Fixture();
        var model = fixture.Model();
        Assert.Equal(0, model.LanguageIndex);
        model.SelectLanguage(1);
        Assert.Equal("en-US", fixture.Storage.Language);
        Assert.Equal("en-US", fixture.Language.Language);
        Assert.Equal(1, model.LanguageIndex);
        // Choosing the current language stores nothing.
        fixture.Storage.Language = "untouched";
        model.SelectLanguage(1);
        Assert.Equal("untouched", fixture.Storage.Language);
    }

    [Fact]
    public void A_language_that_cannot_be_stored_keeps_the_current_one_and_puts_the_picker_back()
    {
        var fixture = new Fixture();
        var model = fixture.Model(new FailingSettings());
        var changed = new List<string?>();
        model.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        Assert.Throws<IOException>(() => model.SelectLanguage(1));
        Assert.Equal("zh-CN", fixture.Language.Language);
        Assert.Equal(0, model.LanguageIndex);
        Assert.Equal([nameof(SettingsViewModel.LanguageIndex)], changed);
    }

    [Fact]
    public void A_language_refresh_translates_the_texts_and_keeps_the_unsaved_input()
    {
        var fixture = new Fixture();
        var model = fixture.Model();
        model.ClientId = "unsaved-language-test";
        model.LocalRouteTimes = false;
        var chinese = model.SaveLabel;
        var changed = new List<string?>();
        model.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        fixture.Language.SetLanguage("en-US");
        model.RefreshLanguage();
        Assert.Equal("Save connection settings", model.SaveLabel);
        Assert.NotEqual(chinese, model.SaveLabel);
        Assert.Contains(nameof(SettingsViewModel.Target), changed);
        Assert.Contains(nameof(SettingsViewModel.LanguageIndex), changed);
        Assert.DoesNotContain(nameof(SettingsViewModel.ClientId), changed);
        Assert.DoesNotContain(nameof(SettingsViewModel.LocalRouteTimes), changed);
        Assert.Equal("unsaved-language-test", model.ClientId);
        Assert.False(model.LocalRouteTimes);
    }

    [Fact]
    public void The_target_and_verified_operations_describe_the_current_workspace()
    {
        var fixture = new Fixture();
        var model = fixture.Model();
        fixture.Language.SetLanguage("en-US");
        Assert.Contains("?", model.Target, StringComparison.Ordinal);
        fixture.Session.Workspace.AirlineId = "77";
        fixture.Session.Workspace.VerifiedOperations.Add("Fleets:Create");
        Assert.Contains("77", model.Target, StringComparison.Ordinal);
        Assert.Contains("Fleets:Create", model.Verified, StringComparison.Ordinal);
    }
}
