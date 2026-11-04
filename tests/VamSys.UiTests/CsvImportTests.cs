using System.Windows.Automation;
using VamSys.Core;
using VamSys.TestSupport;
using VamSys.UiTests.Driver;
using static VamSys.UiTests.Driver.AppWindow;
using static VamSys.UiTests.Driver.Disk;

namespace VamSys.UiTests;

/// <summary>
/// Importing a CSV in the product package through the real system file picker and the mapping dialog: a column renamed in
/// the dialog, an unknown column kept, the rows added as drafts and the snapshot left alone.
/// </summary>
public class CsvImportTests
{
    const string FileColumn = "Nom";

    [WindowFact]
    public void A_csv_imports_as_drafts_through_the_picker_and_the_mapping_and_either_can_be_cancelled()
    {
        using var app = Start("csv-import", DataUiFixture.SeedImport, Package.Product);
        app.Context = () => app.Name("StatusLabel");
        var csv = Path.Combine(app.OutputDirectory, $"import-{Guid.NewGuid():N}.csv");
        File.WriteAllText(csv, $"ICAO/IATA,{FileColumn},Internal Note\nEGSS,Stansted,first note\nEGCC,Manchester,second note\n");

        // The import goes to the resource picked on the data page.
        app.SelectItem("Data management");
        app.One("ResourcePicker").Expand();
        app.SelectItem("Airports");
        app.SelectItem("Import wizard");

        // Cancelling the picker leaves the page usable.
        app.ClickUntilFilePicker("Choose CSV and map columns");
        app.CancelFilePicker();

        // The mapping dialog shows the file; cancelling it imports nothing.
        app.ClickUntilFilePicker("Choose CSV and map columns");
        app.ChooseFile(csv);
        app.WaitData("2 records");
        app.WaitData("Stansted");
        app.DialogCancel();

        app.ClickUntilFilePicker("Choose CSV and map columns");
        app.ChooseFile(csv);
        app.WaitData("2 records");
        Assert.Equal(ToggleState.Off, app.First("Use as original snapshot (replace this resource's snapshot and drafts)", ControlType.CheckBox).ToggleState());
        app.First(FileColumn, ControlType.Edit).SetText("Name");
        app.DialogPrimary("Import into workspace").Invoke();
        app.DialogClosed();
        app.Until(() => app.Name("StatusLabel").StartsWith("Imported 2 records", StringComparison.Ordinal), "the import is reported");
        app.Close();

        var airports = Stored(app.DataDirectory).Resources[ResourceKind.Airports];
        Assert.Equal(["EGLL"], airports.Snapshot.Select(r => r.Get("ICAO/IATA")));
        Assert.Equal(["EGCC", "EGLL", "EGSS"], airports.Draft.Select(r => r.Get("ICAO/IATA")).Order(StringComparer.Ordinal));
        var imported = airports.Draft.Single(r => r.Get("ICAO/IATA") == "EGSS");
        Assert.Equal(("Stansted", "first note"), (imported.Get("Name"), imported.Get("Internal Note")));
    }
}
