using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VamSys.Core;
using Windows.Storage.Pickers;

namespace VamSys.App;

public sealed partial class MainWindow
{
    async Task Validate()
    {
        if (workspace.Mode == RunMode.Online)
        {
            var messages = importExport.ValidateOnline(resource).Select(L).ToList();
            await ConfirmMessage(L("Text_38833A8579"), messages.Count == 0 ? L("Text_AA9F647A82") : string.Join("\n", messages), L("Text_3FD47EDCE4"), scrolls: true);
            return;
        }
        await Work(async () =>
        {
            var issues = await importExport.ValidateAsync(resource);
            Status(() => issues.Count == 0 ? L("Text_AA9F647A82") : L("Text_8EEF5FE8E6", ("arg0", issues.Count)));
            if (issues.Count > 0)
                await ConfirmMessage(L("Text_38833A8579"), string.Join("\n", issues.Take(100).Select(i => L("Text_7560F012B2", ("arg0", Data.Draft.FindIndex(r => r.LocalId == i.RowId) + 1), ("arg1", i.Field), ("arg2", L(i.Description))))), L("Text_3FD47EDCE4"), scrolls: true);
        });
    }
    async Task ImportCsv()
    {
        if (!gate.CanAct)
            return;
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".csv");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSingleFileAsync();
        if (file is null)
            return;
        await Work(async () =>
        {
            var parsed = await importExport.ParseFileAsync(file.Path);
            var form = new ImportMappingDialogViewModel
            {
                Title = L("Text_E0FDE3C5CB"),
                PrimaryLabel = L("Text_C9B2630A18"),
                Summary = L("Text_42F0F930C9", ("arg0", parsed.Rows.Count)),
                Hint = L("WeightCsvHint"),
                BaselineLabel = L("Text_B91DE02936"),
                Baseline = importExport.BaselineByDefault(resource),
                CanBaseline = importExport.BaselineAvailable,
                Columns = parsed.Headers.Select(h => new ImportColumnViewModel(h)).ToList(),
                Sample = L("Text_793A76BFEA") + string.Join("\n", parsed.Rows.Take(3).Select(r => string.Join(" | ", r).Truncate(240)))
            };
            if (!await Confirm(form))
                return;
            if (importExport.BaselineNeedsWarning(resource, form.Baseline) && !await ConfirmMessage(L("Text_885B15B2F5"), L("Text_5B5C523423"), L("Text_131B13AA26")))
                return;
            var summary = await importExport.ImportAsync(resource, parsed, form.Mapping, form.Baseline);
            Render();
            Status(() => L("Text_D84A9B1171", ("arg0", summary.Rows), ("arg1", summary.Issues)));
        });
    }
    Task ExportCsv(ResourceKind kind) => LocalOperation(async () =>
    {
        if (!gate.CanAct)
            return;
        var outcome = await importExport.ExportAsync(kind, new ExportDialogs(this));
        if (outcome.NothingChanged)
            Status(() => L("Text_90484F47B3"));
        else if (outcome.Files > 0)
            Status(() => L("Text_B680DD9FD1", ("arg0", outcome.Files)));
    });

    sealed class ExportDialogs(MainWindow owner) : IExportPrompts
    {
        public Task ShowIssuesAsync(ResourceKind kind, IReadOnlyList<Issue> issues)
            => owner.ConfirmMessage(owner.L("Text_75843F167F"), string.Join("\n", issues.Take(60).Select(i => owner.L("Text_7560F012B2", ("arg0", owner.workspace.Resources[kind].Draft.FindIndex(r => r.LocalId == i.RowId) + 1), ("arg1", i.Field), ("arg2", owner.L(i.Description))))), owner.L("Text_3FD47EDCE4"), scrolls: true);
        public Task<bool> ConfirmDeletesAsync(int count)
            => owner.ConfirmMessage(owner.L("Text_5D6042B30E"), owner.L("Text_B83678AC3A", ("arg0", count)), owner.L("Text_6B20B383BC"));
        public async Task<string?> ChooseFolderAsync()
        {
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(owner));
            return (await picker.PickSingleFolderAsync())?.Path;
        }
    }
}
