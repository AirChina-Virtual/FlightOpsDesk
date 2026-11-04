using System.Windows.Automation;
using VamSys.Core;
using VamSys.TestSupport;
using VamSys.UiTests.Driver;
using static VamSys.UiTests.Driver.AppWindow;
using static VamSys.UiTests.Driver.Disk;

namespace VamSys.UiTests;

/// <summary>
/// A simulated submission from the review page in the product package: the confirmation list and the separate deletion
/// confirmation can each cancel, and a confirmed one moves to the task center and runs only the new task.
/// </summary>
public class SubmissionHandoffTests
{
    const string Submit = "Simulate workspace submission";

    [WindowFact]
    public void A_confirmed_submission_moves_to_the_task_center_and_runs_only_its_own_task()
    {
        using var app = Start("submission", DataUiFixture.SeedSubmission, Package.Product);
        app.Context = () => app.Name("StatusLabel");
        void ClickSubmit() => app.Wait(() => app.FindNamed(Submit, ControlType.Button).FirstOrDefault(b => b.Current.IsEnabled), "the review is ready").Invoke();

        app.SelectItem("Change review");

        // Cancelling the list starts nothing and stays on the review.
        ClickSubmit();
        app.DialogPrimary(Submit);
        app.DialogCancel();
        Assert.True(app.First("Change review", ControlType.ListItem).IsSelected(), "A cancelled submission left the review");

        // So does cancelling the separate deletion confirmation.
        ClickSubmit();
        app.DialogPrimary(Submit).Invoke();
        app.WaitData("EGKK");
        app.DialogPrimary("Confirm");
        app.DialogCancel();
        Assert.True(app.First("Change review", ControlType.ListItem).IsSelected(), "A cancelled deletion left the review");

        ClickSubmit();
        app.DialogPrimary(Submit).Invoke();
        app.DialogPrimary("Confirm").Invoke();
        app.Until(() => app.First("Task center", ControlType.ListItem).IsSelected(), "the task center opens");
        app.Until(() => app.Name("StatusLabel").StartsWith("Demo: ", StringComparison.Ordinal), "the simulated task finishes");
        app.Close();

        var stored = Stored(app.DataDirectory);
        Assert.Equal(2, stored.Jobs.Count);
        Assert.Equal(ItemState.Pending, Assert.Single(stored.Jobs.Single(j => j.Items.Count == 1).Items).State);
        Assert.All(stored.Jobs.Single(j => j.Items.Count == 2).Items, i => Assert.Equal(ItemState.Succeeded, i.State));
        var airports = stored.Resources[ResourceKind.Airports];
        Assert.DoesNotContain(airports.Draft, r => r.Get("ICAO/IATA") == "EGKK");
        Assert.Equal("Renamed", airports.Snapshot.Single(r => r.Get("ICAO/IATA") == "EGLL").Get("Name"));
    }
}
