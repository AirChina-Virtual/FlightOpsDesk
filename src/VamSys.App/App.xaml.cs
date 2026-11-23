using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VamSys.Core;
using VamSys.Infrastructure;

namespace VamSys.App;

public partial class App : Application
{
    Window? window;
    DataDirectoryLease? dataLease;
    public App()
    {
        LocalizationDiagnostics.EnableTrace();
        InitializeComponent();
        UnhandledException += (_, e) => RecordFailure(e.Exception);
    }
    // The chosen data directory may be the very thing that is unavailable; the log then goes to the default one.
    static void RecordFailure(Exception e)
    {
        var entry = $"{DateTimeOffset.UtcNow:O} {e.GetType().Name} {e.Message}\n{e.StackTrace}\n";
        foreach (var directory in new[] { AppPaths.DataDirectory, AppPaths.DefaultDirectory })
            try
            {
                Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Combine(directory, "startup-error.log"), entry);
                return;
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { }
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args) => Start();

    void Start()
    {
        if (AppPaths.CheckLocation() is var problem and not DataLocationProblem.None)
        {
            ShowLocationProblem(problem);
            return;
        }
        try
        {
            var directory = AppPaths.DataDirectory;
            dataLease = new(directory);
            if (!AppPaths.ManagedByEnvironment)
            {
                DataLocationStartup.FinishMoves(AppPaths.LocationFile, AppPaths.Chosen, directory);
                // What was set aside is finished; a later change of location must not see it as still pending.
                AppPaths.ReloadLocation();
            }
            var main = new MainWindow();
            window = main;
            // The first workspace is read after the window appears; a failed read is reported like any startup failure.
            main.StartupFailed += ShowStartupFailure;
            main.Closed += (_, _) =>
            {
                dataLease?.Dispose();
                dataLease = null;
                // Started only now: the new process needs the lease and the old database released.
                if (main.RestartRequested)
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false });
            };
            main.Activate();
        }
        catch (Exception e)
        {
            ShowStartupFailure(e);
        }
    }

    void ShowStartupFailure(Exception e)
    {
        dataLease?.Dispose();
        dataLease = null;
        RecordFailure(e);
        var message = MessageErrors.Describe(e);
        ShowStartupWindow(new TextBlock { Text = Both(language => language.Format(message)), TextWrapping = TextWrapping.Wrap });
    }

    static string Both(Func<LocalizationService, string> text)
    {
        var english = new LocalizationService();
        english.SetLanguage("en-US");
        return text(new LocalizationService()) + "\n\n" + text(english);
    }

    void ShowStartupWindow(UIElement content)
    {
        var shown = new Window { Title = "FlightOps Desk", Content = new ScrollViewer { Content = content, Padding = UiTokens.StartupErrorMargin } };
        shown.AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "FlightOpsDesk.ico"));
        shown.Activate();
        var previous = window;
        window = shown;
        previous?.Close();
    }

    // The data has not gone anywhere, so the choice stays with the person: plug the drive back in and retry, or go on
    // from the default location.
    void ShowLocationProblem(DataLocationProblem problem)
    {
        var path = AppPaths.Chosen.Directory ?? AppPaths.LocationFile;
        var key = problem == DataLocationProblem.Unreadable ? "DataLocationUnreadable" : "DataLocationMissing";
        var panel = new StackPanel { Spacing = UiTokens.SpacingLarge };
        panel.Children.Add(new TextBlock { Text = Both(language => language.Get(key, ("path", path))), TextWrapping = TextWrapping.Wrap });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = UiTokens.SpacingMedium };
        var retry = new Button { Content = Both(language => language.Get("DataLocationRetry")).Replace("\n\n", " / ", StringComparison.Ordinal) };
        retry.Click += (_, _) => Start();
        var useDefault = new Button { Content = Both(language => language.Get("DataLocationUseDefault")).Replace("\n\n", " / ", StringComparison.Ordinal) };
        useDefault.Click += (_, _) =>
        {
            try
            {
                if (problem == DataLocationProblem.Unreadable)
                    File.Delete(AppPaths.LocationFile);
                else
                    DataLocationStartup.UseDefault();
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { RecordFailure(failure); }
            Start();
        };
        buttons.Children.Add(retry);
        buttons.Children.Add(useDefault);
        panel.Children.Add(buttons);
        ShowStartupWindow(panel);
    }
}
