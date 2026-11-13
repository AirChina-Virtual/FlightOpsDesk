using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using VamSys.Core;
using VamSys.Presentation;
using Windows.System;

namespace VamSys.App;

public sealed partial class MainWindow
{
    Microsoft.UI.Dispatching.DispatcherQueueTimer? queryPause;
    readonly InfoBadge reviewBadge = new();
    bool badgeStepQueued;

    void AddShortcuts()
    {
        // The shortcuts live on the root: WinUI would otherwise show their key text ("Ctrl+Z") as a tooltip wherever the
        // pointer rests in the window. The toolbar buttons name their shortcuts in their own tooltips.
        RootGrid.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        void Add(VirtualKey key, VirtualKeyModifiers modifiers, Func<bool> run)
        {
            var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
            accelerator.Invoked += (_, e) => e.Handled = page == "data" && gate.OpenDialogs == 0 && run();
            RootGrid.KeyboardAccelerators.Add(accelerator);
        }
        // A text box keeps its own undo and redo: the shortcut is not handled here while one has focus.
        Add(VirtualKey.Z, VirtualKeyModifiers.Control, () => HistoryShortcut(undo: true));
        Add(VirtualKey.Y, VirtualKeyModifiers.Control, () => HistoryShortcut(undo: false));
        Add(VirtualKey.F, VirtualKeyModifiers.Control, () =>
        {
            if (searchBox?.Focus(FocusState.Keyboard) != true)
                return false;
            searchBox.SelectAll();
            return true;
        });
        Add(VirtualKey.F5, VirtualKeyModifiers.None, () =>
        {
            if (!CanEditData())
                return false;
            _ = ApplyQueryConditions();
            return true;
        });
    }

    bool HistoryShortcut(bool undo)
    {
        if (!gate.CanInvokeCommand || FocusManager.GetFocusedElement(RootGrid.XamlRoot) is TextBox)
            return false;
        _ = UserAction(() => MoveHistory(undo));
        return true;
    }

    // Set while the program itself fills the condition controls: a rebind, or new sort columns.
    bool settingQueryControls;

    // Applies the shown conditions once they have settled, when a person changed them. A language change replaces the
    // filter's items, which clears its selection for a moment; that is not a choice and must not start a query.
    void QueryConditionsChanged(bool wait)
    {
        if (settingQueryControls || filterBox?.SelectedIndex < 0)
            return;
        queryPause?.Stop();
        if (shownState is null || !ReferenceEquals(PageHost.Content, shownDataPage) || QueryConditionsApplied())
            return;
        if (!wait)
        {
            _ = ApplyQueryConditions();
            return;
        }
        if (queryPause is null)
        {
            queryPause = DispatcherQueue.CreateTimer();
            queryPause.Interval = TimeSpan.FromMilliseconds(300);
            queryPause.IsRepeating = false;
            // Something else may have applied them during the pause.
            queryPause.Tick += (_, _) => { if (!QueryConditionsApplied()) _ = ApplyQueryConditions(); };
        }
        queryPause.Start();
    }

    bool QueryConditionsApplied() => shownState is { } state && searchBox?.Text == state.AppliedSearch
        && filterBox?.SelectedIndex == state.AppliedFilter && (sortBox?.SelectedItem as string ?? "") == (state.AppliedSort ?? "");

    // Conditions that cannot apply now (a task is running) stay shown with the "not applied" notice.
    Task ApplyQueryConditions()
    {
        queryPause?.Stop();
        return searchBox is null || filterBox is null || sortBox is null || !CanEditData()
            ? Task.CompletedTask
            : UserAction(() => LoadRowsAsync(searchBox.Text, filterBox.SelectedIndex, sortBox.SelectedItem?.ToString()));
    }

    // The number on the review item. Counting a large resource takes a moment, so what is not known yet is counted in
    // the background of the dispatcher, one resource at a time.
    void RefreshReviewBadge()
    {
        var item = PaneItem(ShellPage.Review);
        var missing = workspace.Resources.Values.FirstOrDefault(d => !changeCounts.ContainsKey(d));
        if (missing is not null)
        {
            if (!badgeStepQueued)
                badgeStepQueued = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                {
                    badgeStepQueued = false;
                    if (gate.WorkInProgress)
                        return;
                    if (workspace.Resources.Values.FirstOrDefault(d => !changeCounts.ContainsKey(d)) is { } next)
                        CountChanges(next);
                    RefreshReviewBadge();
                });
            return;
        }
        var count = workspace.Resources.Values.Sum(d => changeCounts[d]);
        RefreshResourceTabs();
        RefreshCommandLabels();
        RefreshFilterLabels();
        reviewBadge.Value = count;
        if (count > 0 != item.InfoBadge is not null)
            item.InfoBadge = count > 0 ? reviewBadge : null;
    }
}
