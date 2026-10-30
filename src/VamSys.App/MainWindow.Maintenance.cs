using Microsoft.UI.Xaml.Controls;
using VamSys.Core;
using VamSys.Infrastructure;

namespace VamSys.App;

public sealed partial class MainWindow
{
    Task LoadParameters() => LocalOperation(async () =>
    {
        var count = await maintenance.LoadParametersAsync();
        Status(() => L("ApiParametersLoaded", ("count", count)));
    });
    Task ChooseParameter(DataRow row) => LocalOperation(async () =>
    {
        var parameters = await maintenance.ChoosableParametersAsync(row);
        var picker = new ComboBox { MinWidth = UiTokens.WorkspaceChoiceMinWidth };
        foreach (var parameter in parameters)
            picker.Items.Add(new ComboBoxItem { Content = parameter.Label, Tag = parameter.Id });
        picker.SelectedIndex = 0;
        for (var i = 0; i < picker.Items.Count; i++)
        if ((string)((ComboBoxItem)picker.Items[i]).Tag == row.Get("Parameter IDs"))
            picker.SelectedIndex = i;
        if (!await Confirm(L("AirlineParameter"), Stack(Text(() => L("ParameterSelect")), picker), L("Confirm")))
            return;
        await maintenance.AssignParameterAsync(resource, row, (string)((ComboBoxItem)picker.SelectedItem).Tag);
    });    Task BackupAndClearWorkspace() => LocalOperation(async () =>
    {
        var preview = await maintenance.PrepareBackupAsync();
        if (!await ConfirmMessage(L("BackupClear"), L("BackupClearPreview", ("name", preview.Name), ("path", preview.DataDirectory),
            ("snapshots", preview.Snapshots), ("drafts", preview.Drafts), ("jobs", preview.Jobs), ("conflicts", preview.Conflicts), ("history", preview.HistorySteps)), L("Confirm")))
            return;
        var result = await maintenance.BackupAndClearAsync(preview, owner =>
        {
            online.Release(owner);
            ClearMaintenanceViewState(owner);
            InvalidateDataViews();
        });
        Status(() => L("MaintenanceDone", ("path", result.BackupDatabasePath)));
    });
    void ClearMaintenanceViewState(Guid owner)
    {
        foreach (var key in dataStates.Keys.Where(k => k.Item1 == owner).ToArray())
            dataStates.Remove(key);
        taskScroll.Remove(owner);
        reviews.Invalidate();
        shownReviewPage = null;
    }
}
