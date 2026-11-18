using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace VamSys.App;

// The hint of a table cell (a problem, the server's value, why the cell is read-only) in a tooltip made when the pointer
// first rests on the cell's control. A bound tooltip on every cell tripled the time of a page change: every rebind
// replaced thousands of tooltips. The tooltip reads the hint when it opens, so a recycled cell shows its current row's
// hint. Used by the text cells (EditableCell); the fleet cells bind theirs (FleetCell).
sealed class CellHint(Control owner, Func<CellViewModel?> cell, Func<bool> suppressed)
{
    ToolTip? tip;
    bool pointerOver;
    // A tooltip opened from code stays open until code closes it (the service only closes the ones it opened): at most one
    // hint is open, and it closes when the pointer leaves, editing starts, the cell unloads or another hint opens.
    static ToolTip? open;
    public static void CloseOpen()
    {
        if (open is { } shown)
        { open = null; shown.IsOpen = false; }
    }
    public void Close()
    {
        if (tip is { } own && ReferenceEquals(open, own))
            CloseOpen();
        else if (tip is { IsOpen: true } opened)
            opened.IsOpen = false;
    }
    public void Unloaded() { pointerOver = false; Close(); }
    public void Exited() { pointerOver = false; Close(); }
    public void Entered()
    {
        pointerOver = true;
        if (tip != null || cell()?.Hint is null)
            return;
        var made = tip = new ToolTip();
        made.Opened += (_, _) =>
        {
            if (cell()?.Hint is { } hint)
                made.Content = hint;
            else
                made.IsOpen = false;
        };
        ToolTipService.SetToolTip(owner, made);
        // The service starts listening only now, after this entry: open the first one after the usual pause.
        var timer = owner.DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(UiTokens.HintDelayMilliseconds);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            if (!pointerOver || suppressed() || cell()?.Hint is null || !owner.IsLoaded)
                return;
            CloseOpen();
            open = made;
            made.IsOpen = true;
        };
        timer.Start();
    }
}

// 2.5: a fleet cell (Fleet ID, Fleet IDs, Parameter IDs) is a button that opens the picker. Its hint is a plain bound tooltip
// (FleetTemplate) left to the tooltip service: one per row, not one per cell. Making the tooltip on hover, as CellHint does,
// crashed the window (a COM "catastrophic failure") when the pointer swept over several fleet buttons.
public static class FleetCell
{
    // The outline of a cell with a problem (CellViewModel.Look 2), as CellIssueBoxStyle marks a text cell.
    public static Visibility IssueShown(int look) => look == 2 ? Visibility.Visible : Visibility.Collapsed;
}
