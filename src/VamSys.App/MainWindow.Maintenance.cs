using Microsoft.UI.Xaml.Controls;
using VamSys.Core;
using VamSys.Infrastructure;

namespace VamSys.App;

public sealed partial class MainWindow
{
    async Task<bool> ReadOptionalParameters(CancellationToken ct)
    {
        try
        { await Api().ReadParametersAsync(ct, true); return true; }
        catch (ApiAccessException) { throw; }
        catch { Api().InvalidateParameters(); return false; }
    }
    Task LoadParameters() => LocalOperation(async () =>
    {
        if (workspace.Mode != RunMode.Online)
            throw OperationsAdapter.Block("ApiConnectFirst");
        var parameters = await Api().ReadParametersAsync(CancellationToken.None, true);
        Status(() => L("ApiParametersLoaded", ("count", parameters.Count)));
    });
    Task ChooseParameter(DataRow row) => LocalOperation(async () =>
    {
        if (row.Identity != null)
            throw OperationsAdapter.Block("ApiParameterCreateOnly");
        if (workspace.Mode != RunMode.Online)
            throw OperationsAdapter.Block("ApiConnectFirst");
        var parameters = await Api().ReadParametersAsync(CancellationToken.None, true);
        var picker = new ComboBox { MinWidth = 320 };
        foreach (var parameter in parameters.Where(p => p.Approved && !p.Restricted))
            picker.Items.Add(new ComboBoxItem { Content = parameter.Label, Tag = parameter.Id });
        if (picker.Items.Count == 0)
            throw OperationsAdapter.Block("ApiParameterRequired");
        picker.SelectedIndex = 0;
        for (var i = 0; i < picker.Items.Count; i++)
        if ((string)((ComboBoxItem)picker.Items[i]).Tag == row.Get("Parameter IDs"))
            picker.SelectedIndex = i;
        if (!await Confirm(L("AirlineParameter"), Stack(Text(() => L("ParameterSelect")), picker), L("Confirm")))
            return;
        var id = (string)((ComboBoxItem)picker.SelectedItem).Tag;
        if (id == row.Get("Parameter IDs"))
            return;
        var replacement = row.With("Parameter IDs", id);
        Data.Edit([row.LocalId], () => Data.ReplaceDraftRows([replacement]));
        await Save();
    });
    Task BackupAndClearWorkspace() => LocalOperation(async () =>
    {
        var preview = await maintenance.PrepareBackupAsync();
        if (!await Confirm(L("BackupClear"), Text(() => L("BackupClearPreview", ("name", preview.Name), ("path", preview.DataDirectory),
            ("snapshots", preview.Snapshots), ("drafts", preview.Drafts), ("jobs", preview.Jobs), ("conflicts", preview.Conflicts), ("history", preview.HistorySteps))), L("Confirm")))
            return;
        var result = await maintenance.BackupAndClearAsync(preview, owner =>
        {
            if (apiSessions.Remove(owner, out var api))
                api.Client.Dispose();
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
        reviewGeneration++;
        reviewPlan = null;
        reviewRows = [];
    }
}
