using Microsoft.UI.Xaml;

namespace VamSys.App;

public partial class App : Application
{
    Window? window;
    VamSys.Infrastructure.DataDirectoryLease? dataLease;
    public App()
    {
        VamSys.Infrastructure.LocalizationDiagnostics.EnableTrace();
        InitializeComponent();
        UnhandledException += (_, e) => RecordFailure(e.Exception);
    }
    static void RecordFailure(Exception e)
    {
        var directory = VamSys.Core.AppPaths.DataDirectory;
        System.IO.Directory.CreateDirectory(directory);
        System.IO.File.AppendAllText(System.IO.Path.Combine(directory, "startup-error.log"), $"{DateTimeOffset.UtcNow:O} {e.GetType().Name} {e.Message}\n{e.StackTrace}\n");
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            var directory = VamSys.Core.AppPaths.DataDirectory;
            dataLease = new(directory);
            window = new MainWindow();
            window.Closed += (_, _) => { dataLease?.Dispose(); dataLease = null; };
            window.Activate();
        }
        catch (Exception e)
        {
            dataLease?.Dispose();
            dataLease = null;
            RecordFailure(e);
            var en = new VamSys.Core.LocalizationService();
            en.SetLanguage("en-US");
            var message = VamSys.Core.MessageErrors.Describe(e);
            window = new Window
            {
                Title = "FlightOps Desk",
                Content = new Microsoft.UI.Xaml.Controls.TextBlock
                {
                    Text = new VamSys.Core.LocalizationService().Format(message) + "\n\n" + en.Format(message),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(24)
                }
            };
            window.AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "FlightOpsDesk.ico"));
            window.Activate();
        }
    }
}
