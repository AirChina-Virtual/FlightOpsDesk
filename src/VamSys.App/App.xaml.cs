using Microsoft.UI.Xaml;

namespace VamSys.App;
public partial class App : Application
{
    Window? window;
    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => RecordFailure(e.Exception);
    }
    static void RecordFailure(Exception e)
    {
        var directory = Environment.GetEnvironmentVariable("VAMSYS_DATA_DIR") ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VamSysBatch");
        System.IO.Directory.CreateDirectory(directory);
        System.IO.File.AppendAllText(System.IO.Path.Combine(directory, "startup-error.log"), $"{DateTimeOffset.UtcNow:O} {e.GetType().Name} {e.Message}\n{e.StackTrace}\n");
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try { window = new MainWindow(); window.Activate(); }
        catch (Exception e) { RecordFailure(e); throw; }
    }
}
