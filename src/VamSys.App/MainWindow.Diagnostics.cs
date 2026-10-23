using System.Security.Cryptography;
using System.Text;
using VamSys.Infrastructure;
using Windows.Storage.Pickers;

namespace VamSys.App;

public sealed partial class MainWindow
{
    Task ExportTaskDiagnostics(Guid jobId) => LocalOperation(async () =>
    {
        var persisted = store.LoadJob(workspace.Id, jobId) ?? throw new InvalidOperationException("The task is not stored.");
        var picker = new FileSavePicker { SuggestedFileName = $"FlightOps-diagnostic-{jobId:N}" };
        picker.FileTypeChoices.Add("JSON", new List<string> { ".json" });
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSaveFileAsync();
        if (file == null)
            return;
        var json = ApiDiagnosticExport.Serialize(persisted);
        await File.WriteAllTextAsync(file.Path, json, Encoding.UTF8);
        var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(file.Path)));
        await File.WriteAllTextAsync(file.Path + ".sha256", hash + "  " + Path.GetFileName(file.Path) + Environment.NewLine, Encoding.UTF8);
        Status(() => L("ApiEvidenceSaved", ("path", file.Path)));
    });
}
