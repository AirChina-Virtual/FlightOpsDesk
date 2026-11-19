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

// 2.5: a fleet cell (Fleet ID, Fleet IDs, Parameter IDs) is a button that opens the picker. Its hint goes to the tooltip
// service, which opens and closes it. Making the tooltip on hover, as CellHint does, crashed the window (a COM "catastrophic
// failure") when the pointer swept over several fleet buttons. A plain bound tooltip (ToolTipService.ToolTip bound to the
// hint) made the service wrap every new hint text in a new tooltip, so a language switch replaced the tooltip of every
// fleet cell on the page and took 15-19% longer. Hint (bound in FleetTemplate) instead makes the button's tooltip once, when
// the first hint arrives, and later only changes its text. A cell without a hint has no tooltip, so nothing (not even an
// empty box) opens on it; when a hint comes back the button gets a new one, so a tooltip is made only when a cell goes from
// no hint to a hint. The tooltip is removed with ClearValue: after SetToolTip(button, null) no tooltip set later on that
// button opened again (the 2.5 window test failed).
public static class FleetCell
{
    // The outline of a cell with a problem (CellViewModel.Look 2), as CellIssueBoxStyle marks a text cell.
    public static Visibility IssueShown(int look) => look == 2 ? Visibility.Visible : Visibility.Collapsed;

    public static readonly DependencyProperty HintProperty =
        DependencyProperty.RegisterAttached("Hint", typeof(string), typeof(FleetCell), new PropertyMetadata(null, HintChanged));
    public static string? GetHint(DependencyObject target) => (string?)target.GetValue(HintProperty);
    public static void SetHint(DependencyObject target, string? value) => target.SetValue(HintProperty, value);

    static void HintChanged(DependencyObject target, DependencyPropertyChangedEventArgs change)
    {
        var hint = change.NewValue as string;
        var tip = ToolTipService.GetToolTip(target) as ToolTip;
        if (string.IsNullOrEmpty(hint))
        {
            if (tip != null)
                target.ClearValue(ToolTipService.ToolTipProperty);
        }
        else if (tip != null)
            tip.Content = hint;
        else
            ToolTipService.SetToolTip(target, new ToolTip { Content = hint });
    }
}
