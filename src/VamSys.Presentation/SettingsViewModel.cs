using CommunityToolkit.Mvvm.ComponentModel;
using VamSys.Core;

namespace VamSys.Presentation;

// The connection and settings page of one workspace. The window keeps one per workspace, so unsaved input survives
// navigation and a language switch; the window calls RefreshLanguage, the localization service holds no reference.
// The client secret never enters this model: the page passes it from its password box to the save calls.
public sealed class SettingsViewModel : ObservableObject
{
    readonly WorkspaceSession session;
    readonly IUserSettings settings;
    readonly ILocalizationService language;
    readonly Action<Guid> releaseConnection;
    string clientId;
    bool localRouteTimes;

    public SettingsViewModel(WorkspaceSession session, InteractionGate gate, IUserSettings settings, ILocalizationService language, string endpointUrl, Action<Guid> releaseConnection)
    {
        this.session = session;
        this.settings = settings;
        this.language = language;
        this.releaseConnection = releaseConnection;
        EndpointUrl = endpointUrl;
        clientId = session.Workspace.ClientId ?? "";
        localRouteTimes = session.Workspace.LocalRouteTimes;
        // Fixed when the page is built, as before; the page is rebuilt with the workspace views.
        CanUpdateCredentials = gate.CanRequestOnline;
        Gate = gate;
    }

    InteractionGate Gate { get; }
    Workspace Workspace => session.Workspace;
    string L(string key, params (string, object?)[] arguments) => language.Get(key, arguments);

    public string EndpointUrl { get; }
    public string ClientId { get => clientId; set => SetProperty(ref clientId, value); }
    public bool LocalRouteTimes { get => localRouteTimes; set => SetProperty(ref localRouteTimes, value); }
    public bool CanUpdateCredentials { get; }

    public static IReadOnlyList<string> Languages { get; } = ["简体中文", "English"];
    public int LanguageIndex => language.Language == "en-US" ? 1 : 0;

    public string LanguageHeader => L("Language");
    public string ApiSettingsTitle => L("ApiSettings");
    public string ApiSettingsHelp => L("ApiSettingsHelp");
    public string EndpointHeader => L("Text_0251B89662");
    public string SecretHeader => L("Text_86032C09C5");
    public string LocalRouteTimesLabel => L("Text_92BD49D337");
    public string SaveLabel => L("Text_760143E568");
    public string UpdateCredentialsLabel => L("ApiUpdateCredentials");
    public string CredentialsHelp => L("ApiCredentialsHelp");
    public string ConnectLabel => L("ApiConnect");
    public string RefreshLabel => L("ApiRefresh");
    public string Capability => L("ApiCapability");
    public string IndependentScope => L("ApiIndependentScope");
    public string Verified => L("ApiVerified", ("operations", string.Join(", ", Workspace.VerifiedOperations)));
    public string Target => L("ApiTarget", ("va", Workspace.Name), ("id", Workspace.AirlineId ?? "?"));
    public string LoadParametersLabel => L("ApiParametersLoad");
    public string MaintenanceTitle => L("MaintenanceTitle");
    public string BackupClearLabel => L("BackupClear");

    static readonly string[] Texts = [nameof(LanguageIndex), nameof(LanguageHeader), nameof(ApiSettingsTitle), nameof(ApiSettingsHelp), nameof(EndpointHeader),
        nameof(SecretHeader), nameof(LocalRouteTimesLabel), nameof(SaveLabel), nameof(UpdateCredentialsLabel), nameof(CredentialsHelp), nameof(ConnectLabel),
        nameof(RefreshLabel), nameof(Capability), nameof(IndependentScope), nameof(Verified), nameof(Target), nameof(LoadParametersLabel),
        nameof(MaintenanceTitle), nameof(BackupClearLabel)];

    // Texts follow the current language; the inputs keep what the person typed.
    public void RefreshLanguage()
    {
        foreach (var name in Texts)
            OnPropertyChanged(name);
    }

    // The language takes effect at once and is stored first: a failed save keeps the current language and puts the
    // picker back.
    public void SelectLanguage(int index)
    {
        var target = index == 1 ? "en-US" : "zh-CN";
        if (target == language.Language)
            return;
        try
        { settings.SaveLanguage(target); }
        catch { OnPropertyChanged(nameof(LanguageIndex)); throw; }
        language.SetLanguage(target);
    }

    // Saves the connection settings and returns the status message key, or null when the window may not act.
    // The stored client ID is shown afterwards (it is normalized on save) and the open connection is dropped.
    public async Task<string?> SaveConnectionAsync(string secret)
    {
        if (!Gate.CanAct)
            return null;
        await session.SaveConnectionSettingsAsync(ClientId, secret, EndpointUrl, LocalRouteTimes);
        Saved();
        return "Text_DEADA9F3CC";
    }

    public async Task<string?> UpdateCredentialsAsync(string secret)
    {
        if (!Gate.CanAct)
            return null;
        await session.UpdateCredentialsAsync(ClientId, secret);
        Saved();
        return "ApiCredentialsUpdated";
    }

    void Saved()
    {
        ClientId = Workspace.ClientId ?? "";
        releaseConnection(Workspace.Id);
    }
}
