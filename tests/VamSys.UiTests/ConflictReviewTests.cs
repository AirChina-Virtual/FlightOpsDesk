using System.Windows.Automation;
using VamSys.Core;
using VamSys.TestSupport;
using VamSys.UiTests.Driver;
using static VamSys.UiTests.Driver.AppWindow;
using static VamSys.UiTests.Driver.Disk;

namespace VamSys.UiTests;

/// <summary>
/// Settling the conflicts a refresh left, from the review page in the product package, offline. The refresh that finds
/// them needs the server and is covered in IntegrationTests.
/// </summary>
public class ConflictReviewTests
{
    [WindowFact]
    public void Conflicts_show_their_values_and_can_be_kept_replaced_or_left_for_later()
    {
        using var app = Start("conflicts", DataUiFixture.SeedConflicts, Package.Product);
        AutomationElement ConflictsButton(int count) =>
            app.Wait(() => app.FindNamed($"Review conflicts ({count})", ControlType.Button).FirstOrDefault(), $"the button for {count} conflicts");

        app.SelectItem("Change review");
        ConflictsButton(3).Invoke();

        // The first shows the three values; keeping the draft value closes it and the next one follows.
        app.WaitData("Airports · EGLL · Name");
        app.WaitData("mine one");
        app.WaitData("server one");
        app.DialogPrimary("Keep local changes").Invoke();

        // Stopping leaves this one and the rest open, and keeps what was settled.
        app.WaitData("Airports · EGKK · Name");
        app.DialogCancel();
        ConflictsButton(2).Invoke();

        app.WaitData("Airports · EGKK · Name");
        app.DialogSecondary("Use remote value").Invoke();

        // A record the server deleted cannot keep the local value.
        app.WaitData("Airports · EGCC · *");
        Assert.False(app.DialogPrimary("Keep local changes").Current.IsEnabled, "A deleted record offered to keep the local value");
        app.DialogSecondary("Use remote value").Invoke();
        app.DialogClosed();
        ConflictsButton(0);
        app.Close();

        var airports = Stored(app.DataDirectory).Resources[ResourceKind.Airports];
        Assert.Empty(airports.Conflicts);
        Assert.Equal(["EGLL", "EGKK"], airports.Draft.Select(r => r.Get("ICAO/IATA")));
        Assert.Equal(["mine one", "server two"], airports.Draft.Select(r => r.Get("Name")));
    }
}
