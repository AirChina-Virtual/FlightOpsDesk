using Windows.Storage.Pickers;

namespace VamSys.App;

public sealed partial class MainWindow
{
    Task ExportTaskDiagnostics(Guid jobId) => LocalOperation(async () =>
    {
        var path = await diagnostics.ExportAsync(jobId, async () =>
        {
            var picker = new FileSavePicker { SuggestedFileName = $"FlightOps-diagnostic-{jobId:N}" };
            picker.FileTypeChoices.Add("JSON", new List<string> { ".json" });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            return (await picker.PickSaveFileAsync())?.Path;
        });
        if (path != null)
            Status(() => L("ApiEvidenceSaved", ("path", path)));
    });
}
