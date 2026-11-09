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
    const string FarFuture = "2099-01-01 00:00:00";
    const string EndDateChange = $"End Date: {FarFuture} → ";

    static void PickResource(AppWindow app, string resource)
    {
        app.SelectItem("Data management");
        app.SelectResource(resource);
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
        var before = DateTime.UtcNow;
        using var app = Start("dialog-expire", DataUiFixture.SeedBatch, Package.Product);
        app.Context = () => app.Name("StatusLabel");

        PickResource(app, "Routes");
        SelectRow(app, 0);
        app.ClickUntilDialog("Expire routes", "Set end date");
        app.WaitText("Expire routes (CSV)");
        app.WaitData(EndDateChange);
        app.DialogCancel();
        Assert.All(Stored(app.DataDirectory).Resources[ResourceKind.Routes].Draft, r => Assert.Equal(FarFuture, r.Get("End Date")));
        SelectRow(app, 0);
        app.ClickUntilDialog("Expire routes", "Set end date");
        app.DialogPrimary("Set end date").Invoke();
        app.DialogClosed();
        app.Close();

        AssertOneRouteExpiredOneDayBack(app, before);
    }

    [WindowFact]
    public void The_expire_batch_rule_sets_the_same_end_date_as_the_toolbar()
    {
        var before = DateTime.UtcNow;
        using var app = Start("dialog-expire-rule", DataUiFixture.SeedBatch, Package.Product);
        app.Context = () => app.Name("StatusLabel");

        PickResource(app, "Routes");
        SelectRow(app, 0);
        app.ClickUntilDialog("Batch rules", "Preview");
        app.DialogComboBox(1).Expand();
        app.First("Expire routes", ControlType.ListItem).Select();
        app.DialogPrimary("Preview").Invoke();
        app.WaitText("Review rule impact");
        app.WaitData(EndDateChange);
        app.DialogPrimary("Apply to drafts").Invoke();
        app.DialogClosed();
        app.Close();

        AssertOneRouteExpiredOneDayBack(app, before);
    }

    [WindowFact]
    public void Shifting_days_reports_bad_input_skips_a_route_without_a_date_and_only_applying_changes_the_draft()
    {
        using var app = Start("dialog-shift-days", DataUiFixture.SeedUndatedRoute, Package.Product);
        app.Context = () => app.Name("StatusLabel");
        const string Skip = "1 record(s) skipped: the date is empty or invalid.";
        void ShiftBy(string days)
        {
            app.ClickUntilDialog("Batch rules", "Preview");
            app.DialogComboBox(0).Expand();
            app.First("End Date", ControlType.ListItem).Select();
            app.DialogComboBox(1).Expand();
            app.First("Shift date (days)", ControlType.ListItem).Select();
            app.Edit("New value / offset", days);
            app.DialogPrimary("Preview").Invoke();
        }
        void PreviewBothRows()
        {
            SelectRow(app, 0);
            app.Rows()![1].AddToSelection();
            ShiftBy("7");
            app.WaitText("Review rule impact");
            app.WaitData("Selected 2 · Skipped 1 · Unchanged 0 · Changed 1");
            // The whole text: the status bar behind the dialog still ends with this reason.
            app.First(Skip, ControlType.Text);
            app.WaitData($"End Date: {FarFuture} → 2099-01-08 00:00:00");
        }

        PickResource(app, "Routes");
        SelectRow(app, 0);
        ShiftBy("soon");
        app.StatusStarting("Shift days must be a whole number. Nothing was changed.");

        // Nothing can shift, so no preview opens and the status says why.
        SelectRow(app, 1);
        ShiftBy("7");
        app.StatusStarting($"Selected 1 · Skipped 1 · Unchanged 0 · Changed 0 {Skip}");

        PreviewBothRows();
        app.DialogCancel();
        Assert.Equal([FarFuture, ""], Stored(app.DataDirectory).Resources[ResourceKind.Routes].Draft.Select(r => r.Get("End Date")));

        PreviewBothRows();
        app.DialogPrimary("Apply to drafts").Invoke();
        app.DialogClosed();
        // The table has refreshed once the new date is in it: the result must still be in the status bar.
        app.Until(() => app.FindNamed("End Date", ControlType.Edit).Any(c => c.Text() == "2099-01-08 00:00:00"), "the table shows the new date");
        Assert.StartsWith("Selected 2 · Skipped 1 · Unchanged 0 · Changed 1", app.Name("StatusLabel"), StringComparison.Ordinal);
        // A query the user starts reports as usual.
        app.Edit("Search all fields", "2099-01-08");
        app.StatusStarting("Loaded 1 matching records.");
        app.Close();

        var routes = Stored(app.DataDirectory).Resources[ResourceKind.Routes];
        Assert.Equal(["2099-01-08 00:00:00", ""], routes.Draft.Select(r => r.Get("End Date")));
        Assert.Equal([FarFuture, ""], routes.Snapshot.Select(r => r.Get("End Date")));
    }

    // The first route now ends one day before the moment it was expired, in UTC; the second is untouched.
    static void AssertOneRouteExpiredOneDayBack(AppWindow app, DateTime before)
    {
        var after = DateTime.UtcNow;
        var routes = Stored(app.DataDirectory).Resources[ResourceKind.Routes].Draft;
        Assert.Equal(FarFuture, routes[1].Get("End Date"));
        var ended = DateTime.ParseExact(routes[0].Get("End Date"), "yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        // The stored value has no fraction of a second.
        Assert.InRange(ended, before.AddDays(-1).AddSeconds(-1), after.AddDays(-1));
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
