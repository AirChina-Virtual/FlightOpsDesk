using System.Windows.Automation;
using VamSys.Core;
using VamSys.TestSupport;
using VamSys.UiTests.Driver;
using static VamSys.UiTests.Driver.AppWindow;
using static VamSys.UiTests.Driver.Disk;

namespace VamSys.UiTests;

/// <summary>
/// A new fleet's airline parameter: the cell button refuses what it must, without reaching the server, and the picker (over
/// made-up parameters, since the real list needs the server) writes the choice to the draft.
/// </summary>
public class ParameterPickerTests
{
    const string Second = "BBB / BB · 12";

    [WindowFact]
    public void The_cell_refuses_existing_fleets_and_an_unconnected_workspace_and_the_picker_sets_a_new_fleets_parameter()
    {
        using var app = Start("parameters", DataUiFixture.SeedParameters);
        app.Context = () => app.Name("StatusLabel");
        void Status(string text) => app.Until(() => app.Name("StatusLabel").Contains(text, StringComparison.Ordinal), $"status '{text}'");
        // The window refuses to start while it queries or saves, as it does for a person.
        void OpenPicker()
        {
            app.Until(() => app.Query("state").Bool("idle"), "the window is idle");
            app.Query("parameter-dialog");
        }
        // A closed combo box has not created its items, so the selection can be read only while open.
        AutomationElement OpenPickerShowing(string selected)
        {
            var box = app.DialogComboBox();
            box.Expand();
            app.Until(() => app.First(selected, ControlType.ListItem).IsSelected(), $"'{selected}' is selected");
            return box;
        }

        app.Query("prepare");
        var cells = app.Wait(() => app.FindNamed("Airline parameter", ControlType.Button) is { Length: 2 } b ? b : null, "a parameter button per row");
        cells[0].Invoke();
        Status("Parameter associations are supported only when creating a fleet.");
        cells[1].Invoke();
        Status("Save credentials and test the connection in Settings first.");

        // Cancelling changes nothing.
        OpenPicker();
        app.WaitText("Select one approved, unrestricted airline parameter for the new fleet");
        app.DialogCancel();

        // The first is offered by default; the choice goes to the draft.
        OpenPicker();
        var picker = OpenPickerShowing("AAA / AA · 11");
        app.First(Second, ControlType.ListItem).Select();
        picker.Collapse();
        app.DialogPrimary("Confirm").Invoke();
        app.DialogClosed();

        // The row's parameter is what opens next.
        OpenPicker();
        OpenPickerShowing(Second).Collapse();
        app.DialogCancel();
        app.Close();

        var fleets = Stored(app.DataDirectory).Resources[ResourceKind.Fleets];
        Assert.Equal("12", fleets.Draft.Single(r => r.Identity is null).Get("Parameter IDs"));
        Assert.Equal("1", fleets.Draft.Single(r => r.Identity is not null).Get("Parameter IDs"));
    }
}
