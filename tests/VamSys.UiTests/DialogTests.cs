using System.Windows.Automation;
using VamSys.Core;
using VamSys.TestSupport;
using VamSys.UiTests.Driver;
using static VamSys.UiTests.Driver.AppWindow;
using static VamSys.UiTests.Driver.Disk;

namespace VamSys.UiTests;

/// <summary>
/// The message, list and text dialogs no other window test opens, in the product package and without the server: each one
/// opens on its real trigger, cancelling changes nothing, and confirming does what it says. A workspace always opens offline until it
/// connects, so the online validation and the recovery dialogs that act on the server cannot open here; IntegrationTests and
/// Application.TaskRecoveryServiceTests cover those.
/// </summary>
public class DialogTests
{
    // Composed from the resource name at run time.
    const string ExportAirports = "Export Airports CSV";

    static void PickResource(AppWindow app, string resource)
    {
        app.SelectItem("Data management");
        app.One("ResourcePicker").Expand();
        app.SelectItem(resource);
    }

    // Repeats while a closing dialog still locks the page.
    static void SelectRow(AppWindow app, int index) => app.Wait(() =>
    {
        if (app.Rows() is not { } rows || rows.Length <= index)
            return null;
        rows[index].Select();
        return rows[index];
    }, $"data row {index} is selected");

    [WindowFact]
    public void A_new_workspace_takes_its_name_from_the_dialog_and_cancelling_creates_nothing()
    {
        using var app = Start("dialog-new-workspace", DataUiFixture.SeedImport, Package.Product);
        app.Context = () => app.Name("StatusLabel");

        app.SelectItem("Workspaces");
        app.ClickUntilDialog("New workspace", "Confirm");
        app.DialogCancel();
        app.ClickUntilDialog("New workspace", "Confirm");
        app.WaitText("Create separate workspace");
        app.Edit("Airline name", "QA second airline");
        app.DialogPrimary("Confirm").Invoke();
        app.DialogClosed();
        app.Close();

        Assert.Equal("QA second airline", Stored(app.DataDirectory, name => name == "QA second airline").Name);
        Assert.Equal("QA import", Stored(app.DataDirectory, name => name == "QA import").Name);
    }

    [WindowFact]
    public void Validation_issues_show_in_the_row_results_the_review_list_and_the_export()
    {
        using var app = Start("dialog-validation", DataUiFixture.SeedInvalid, Package.Product);
        app.Context = () => app.Name("StatusLabel");

        PickResource(app, "Airports");
        app.ClickUntilDialog("Validate", "Close");
        app.WaitText("Row validation results");
        app.DialogPrimary("Close").Invoke();
        app.DialogClosed();
        app.StatusStarting("Found 4 issues");

        app.SelectItem("Change review");
        var count = 4;
        app.ClickUntilDialog($"View all {count} validation issues", "Close");
        app.DialogPrimary("Close").Invoke();
        app.DialogClosed();

        app.ClickUntilDialog(ExportAirports, "Close");
        app.WaitText("Resolve validation issues first");
        app.DialogPrimary("Close").Invoke();
        app.DialogClosed();
        app.Close();

        Assert.Empty(Stored(app.DataDirectory).Jobs);
    }

    [WindowFact]
    public void Deleting_an_airport_that_routes_use_is_refused_with_the_dependencies()
    {
        using var app = Start("dialog-delete-refused", DataUiFixture.SeedBatch, Package.Product);
        app.Context = () => app.Name("StatusLabel");

        PickResource(app, "Airports");
        SelectRow(app, 0);
        app.ClickUntilDialog("Delete selected", "Close");
        app.WaitText("Dependencies prevent deletion");
        app.DialogPrimary("Close").Invoke();
        app.DialogClosed();
        app.Close();

        Assert.All(Stored(app.DataDirectory).Resources[ResourceKind.Airports].Draft, r => Assert.False(Schemas.Deleted(r)));
    }

    [WindowFact]
    public void A_batch_rule_previews_its_effect_and_only_applying_changes_the_draft()
    {
        using var app = Start("dialog-batch-rule", DataUiFixture.SeedBatch, Package.Product);
        app.Context = () => app.Name("StatusLabel");
        void OpenPreview()
        {
            SelectRow(app, 1);
            app.ClickUntilDialog("Batch rules", "Preview");
            app.DialogComboBox().Expand();
            app.First("Name", ControlType.ListItem).Select();
            app.Edit("New value / offset", "Ruled");
            app.DialogPrimary("Preview").Invoke();
            app.WaitText("Review rule impact");
            app.WaitData("Name: Gatwick → Ruled");
        }

        PickResource(app, "Airports");
        OpenPreview();
        app.DialogCancel();
        OpenPreview();
        app.DialogPrimary("Apply to drafts").Invoke();
        app.DialogClosed();
        app.Close();

        var airports = Stored(app.DataDirectory).Resources[ResourceKind.Airports];
        Assert.Equal(["Heathrow", "Ruled"], airports.Draft.Select(r => r.Get("Name")));
        Assert.Equal(["Heathrow", "Gatwick"], airports.Snapshot.Select(r => r.Get("Name")));
    }

    [WindowFact]
    public void Expiring_a_route_previews_the_end_date_and_only_applying_changes_the_draft()
    {
        using var app = Start("dialog-expire", DataUiFixture.SeedBatch, Package.Product);
        app.Context = () => app.Name("StatusLabel");

        PickResource(app, "Routes");
        SelectRow(app, 0);
        app.ClickUntilDialog("Expire routes", "Set end date");
        app.WaitText("Expire routes (CSV)");
        app.DialogCancel();
        SelectRow(app, 0);
        app.ClickUntilDialog("Expire routes", "Set end date");
        app.DialogPrimary("Set end date").Invoke();
        app.DialogClosed();
        app.Close();

        var routes = Stored(app.DataDirectory).Resources[ResourceKind.Routes].Draft;
        Assert.Equal(1, routes.Count(r => string.CompareOrdinal(r.Get("End Date"), DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)) < 0));
        Assert.Equal(1, routes.Count(r => r.Get("End Date") == "2099-01-01 00:00:00"));
    }

    [WindowFact]
    public void Removing_a_scheduled_routes_last_fleet_is_refused_and_changes_nothing()
    {
        using var app = Start("dialog-fleet-refused", DataUiFixture.SeedBatch, Package.Product);
        app.Context = () => app.Name("StatusLabel");

        PickResource(app, "Routes");
        SelectRow(app, 0);
        app.ClickUntilDialog("Assign fleets", "Preview changes");
        app.Wait(() => app.FindAll(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.CheckBox))
            .FirstOrDefault(c => c.Current.Name.Contains("B738", StringComparison.Ordinal)), "the fleet's check box").Toggle();
        app.DialogPrimary("Preview changes").Invoke();
        app.WaitText("Fleet assignment validation failed");
        app.DialogPrimary("Close").Invoke();
        app.DialogClosed();
        app.Close();

        Assert.All(Stored(app.DataDirectory).Resources[ResourceKind.Routes].Draft, r => Assert.Equal("1", r.Get("Fleet IDs")));
    }

    [WindowFact]
    public void An_export_with_deletions_asks_first_and_writes_the_files_to_the_chosen_folder()
    {
        using var app = Start("dialog-export", DataUiFixture.SeedSubmission, Package.Product);
        app.Context = () => app.Name("StatusLabel");
        var folder = Directory.CreateDirectory(Path.Combine(app.OutputDirectory, "export-" + Guid.NewGuid().ToString("N"))).FullName;

        app.SelectItem("Change review");
        // Cancelling the warning, then the folder picker, writes nothing.
        app.ClickUntilDialog(ExportAirports, "Export deletion instructions");
        app.WaitText("Export includes deletions");
        app.DialogCancel();
        app.ClickUntilDialog(ExportAirports, "Export deletion instructions").Invoke();
        app.CancelFilePicker();
        Assert.Empty(Directory.GetFiles(folder));

        app.ClickUntilDialog(ExportAirports, "Export deletion instructions").Invoke();
        app.ChooseFolder(folder);
        app.Until(() => Directory.GetFiles(folder, "Airports-*.csv").Length > 0, "the export is written");
        app.Close();

        Assert.Contains(Stored(app.DataDirectory).Jobs, j => j.StatusCode == JobStatus.AwaitingImport);
    }

    [WindowFact]
    public void Discarding_unsent_items_asks_first_and_leaves_unknown_results_alone()
    {
        using var app = Start("dialog-discard", DataUiFixture.SeedRecovery, Package.Product);
        app.Context = () => app.Name("StatusLabel");

        app.SelectItem("连接与设置");
        app.Language("English");
        app.SelectItem("Task center");
        app.ClickUntilDialog("Discard unsent items", "Discard unsent items");
        app.DialogCancel();
        app.ClickUntilDialog("Discard unsent items", "Discard unsent items").Invoke();
        app.StatusStarting("Discarded 2 unsent items");
        app.Close();

        var items = Stored(app.DataDirectory).Jobs.Single().Items;
        Assert.Equal(2, items.Count(i => i.State == ItemState.Failed));
        Assert.Equal(1, items.Count(i => i.State == ItemState.Unknown));
    }

    [WindowFact]
    public void Backing_up_and_clearing_asks_first_keeps_a_backup_and_empties_the_workspace()
    {
        using var app = Start("dialog-backup", DataUiFixture.SeedImport, Package.Product);
        app.Context = () => app.Name("StatusLabel");

        app.SelectItem("Connection & settings");
        app.ClickUntilDialog("Back up, then clear this workspace", "Confirm");
        app.WaitData("Back up the entire local database first");
        app.DialogCancel();
        Assert.False(app.HasText("Workspace cleared"), "Cancelling cleared the workspace");
        app.ClickUntilDialog("Back up, then clear this workspace", "Confirm").Invoke();
        var status = app.StatusStarting("Workspace cleared");
        app.Close();

        var backup = status[(status.IndexOf("Backup: ", StringComparison.Ordinal) + "Backup: ".Length)..status.IndexOf(". Connect", StringComparison.Ordinal)];
        Assert.True(File.Exists(backup), $"No backup at '{backup}'");
        var airports = Stored(app.DataDirectory).Resources[ResourceKind.Airports];
        Assert.Empty(airports.Snapshot);
        Assert.Empty(airports.Draft);
    }

    [WindowFact]
    public void Offline_every_unknown_result_can_only_be_looked_at()
    {
        using var app = Start("dialog-unknown", DataUiFixture.SeedUnknownResults, Package.Product);
        app.Context = () => app.Name("StatusLabel");

        app.SelectItem("Task center");
        // One dialog per result, whatever the server did with it: its ways out all need a connection.
        app.ClickUntilDialog("Review unknown results", "Close");
        foreach (var airport in new[] { "EGLL", "EGKK", "EGCC" })
        {
            app.WaitData(airport);
            Assert.Empty(app.Find("SecondaryButton"));
            app.DialogPrimary("Close").Invoke();
            app.Until(() => !app.HasText(airport), $"the dialog for {airport} closes");
        }
        app.DialogClosed();
        app.Close();

        Assert.All(Stored(app.DataDirectory).Jobs.Single().Items, i => Assert.Equal(ItemState.Unknown, i.State));
    }
}
