using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.ComponentModel;
using Windows.System;
using Windows.UI.Core;
using VamSys.Core;

namespace VamSys.App;

// A table cell that shows its value as text and creates an editor only while it is being edited.
// A realized page holds thousands of cells; a TextBox per cell dominated scrolling and native memory.
// Automation still sees an edit control with the cell value, so screen readers and UIA scripts keep working.
//
// The editor follows the column's type: a list to pick from for fixed values, suggestions for airports and tags, a
// calendar for dates and day boxes for day lists; a yes/no cell toggles instead. The keyboard works as in a spreadsheet:
// Enter keeps the edit and moves down, Tab moves right, Escape takes the edit back, the arrows move between cells, and
// typing on a cell that is not being edited replaces its value.
public sealed partial class EditableCell : ContentControl
{
    public static readonly DependencyProperty CellProperty = DependencyProperty.Register(nameof(Cell), typeof(CellViewModel), typeof(EditableCell),
        new PropertyMetadata(null, (target, args) => ((EditableCell)target).CellChanged((CellViewModel?)args.OldValue, (CellViewModel?)args.NewValue)));
    public CellViewModel? Cell { get => (CellViewModel?)GetValue(CellProperty); set => SetValue(CellProperty, value); }
    UIElement? display;
    // The editor's root (a TextBox, a suggestion box, or a text box with a picker), and its text box when it has its own.
    FrameworkElement? editorRoot;
    TextBox? editorBox;
    internal TextBox? Editor => editorBox ?? (editorRoot is AutoSuggestBox box ? MainWindow.FindVisual<TextBox>(box) : null);
    internal bool IsEditing => editorRoot != null;
    // The value when the edit began, which Escape returns to.
    string startValue = "";
    bool syncing, pickerOpen;

    public EditableCell()
    {
        IsTabStop = true;
        UseSystemFocusVisuals = true;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        CharacterReceived += TypedOnCell;
        Unloaded += (_, _) => { pointerOver = false; CloseOwnHint(); };
    }
    // Recycled for another row: an open editor belongs to the previous cell.
    void CellChanged(CellViewModel? old, CellViewModel? next) => EndEdit(false, normalize: false);
    // Subscribed only while editing, so a long-lived cell model never keeps an idle control alive.
    CellViewModel? watched;
    void CellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (editorRoot is null || Cell is null)
            return;
        if (e.PropertyName == nameof(CellViewModel.IsReadOnly) && Cell.IsReadOnly)
            EndEdit(false, normalize: false);
        else if (e.PropertyName == nameof(CellViewModel.Value) && EditorText != Cell.Value)
            ShowInEditor(Cell.Value);
    }
    protected override void OnGotFocus(RoutedEventArgs e)
    {
        base.OnGotFocus(e);
        // Only tabbing into a cell edits it; focus restored on window activation, after Enter/Escape or by the arrow keys
        // does not.
        if (!ReferenceEquals(e.OriginalSource, this))
            return;
        // GotFocus is raised asynchronously, so the returned focus is consumed here.
        if (returningFocus)
        { returningFocus = false; return; }
        // A yes/no cell toggles on Space or Enter, not on arriving.
        if (FocusState == FocusState.Keyboard && Cell?.Type != FieldType.Bool)
            BeginEdit(FocusState.Keyboard);
    }
    bool returningFocus;
    // A click edits the cell, as clicking the former TextBox did, and does not toggle row selection.
    // The editor opens on release: the list item takes focus while handling the press.
    // Only the primary button (a touch or pen contact counts as one) edits; CellPress.StartsEdit says why and is tested.
    bool pressed;
    protected override void OnPointerPressed(PointerRoutedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Cell is null)
            return;
        var point = e.GetCurrentPoint(this).Properties;
        var press = new CellPress(point.IsLeftButtonPressed, point.IsRightButtonPressed, point.IsMiddleButtonPressed, point.IsBarrelButtonPressed, point.IsEraser);
        if (!CellPress.StartsEdit(press, editorRoot != null, Cell.IsReadOnly))
            return;
        e.Handled = true;
        pressed = CapturePointer(e.Pointer);
    }
    protected override void OnPointerReleased(PointerRoutedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (pressed)
        { pressed = false; ReleasePointerCapture(e.Pointer); e.Handled = true; BeginEdit(FocusState.Pointer); }
        else if (editorRoot != null)
            e.Handled = true;
    }
    protected override void OnPointerCaptureLost(PointerRoutedEventArgs e) { base.OnPointerCaptureLost(e); pressed = false; }

    // The hint (a problem, the server's value, why the cell is read-only) is shown in a tooltip made when the pointer first
    // rests on this cell control. A bound tooltip on every cell tripled the time of a page change: every rebind replaced
    // thousands of tooltips. The tooltip reads the hint when it opens, so a recycled cell shows its current row's hint.
    ToolTip? hintTip;
    bool pointerOver;
    // A tooltip opened from code stays open until code closes it (the service only closes the ones it opened): at most one
    // hint is open, and it closes when the pointer leaves, editing starts, the cell unloads or another hint opens.
    static ToolTip? openHint;
    internal static void CloseHint()
    {
        if (openHint is { } open)
        { openHint = null; open.IsOpen = false; }
    }
    void CloseOwnHint()
    {
        if (hintTip is { } tip && ReferenceEquals(openHint, tip))
            CloseHint();
        else if (hintTip is { IsOpen: true } own)
            own.IsOpen = false;
    }
    protected override void OnPointerEntered(PointerRoutedEventArgs e)
    {
        base.OnPointerEntered(e);
        pointerOver = true;
        if (hintTip != null || Cell?.Hint is null)
            return;
        var tip = hintTip = new ToolTip();
        tip.Opened += (_, _) =>
        {
            if (Cell?.Hint is { } hint)
                tip.Content = hint;
            else
                tip.IsOpen = false;
        };
        ToolTipService.SetToolTip(this, tip);
        // The service starts listening only now, after this entry: open the first one after the usual pause.
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(UiTokens.HintDelayMilliseconds);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            if (!pointerOver || editorRoot is not null || Cell?.Hint is null || !IsLoaded)
                return;
            CloseHint();
            openHint = tip;
            tip.IsOpen = true;
        };
        timer.Start();
    }
    protected override void OnPointerExited(PointerRoutedEventArgs e) { base.OnPointerExited(e); pointerOver = false; CloseOwnHint(); }
    protected override void OnTapped(TappedRoutedEventArgs e) { base.OnTapped(e); if (editorRoot != null) e.Handled = true; }

    internal void BeginEdit(FocusState focus = FocusState.Programmatic, string? replaceWith = null)
    {
        CloseOwnHint();
        if (editorRoot != null || Cell is not { IsReadOnly: false } cell)
            return;
        if (cell.Type == FieldType.Bool)
        {
            Toggle(cell);
            if (focus == FocusState.Pointer)
                FocusQuietly();
            return;
        }
        display = Content as UIElement;
        startValue = cell.Value;
        var text = replaceWith ?? cell.Value;
        FrameworkElement root;
        TextBox? box = null;
        AutoSuggestBox? suggest = null;
        if (cell.Suggest is { } source)
            root = suggest = SuggestionBox(cell, source, text);
        else
        {
            box = TextEditor(cell, text);
            root = cell.Type == FieldType.Date ? WithPicker(box, DatePicker(cell, box), "", cell.Label("CellPickDate"))
                : cell.Type == FieldType.DayList ? WithPicker(box, DaysPicker(cell, box), "", cell.Label("CellPickDays"))
                : box;
        }
        AutomationProperties.SetName(root, cell.Field);
        // Leaving the editor ends the edit; moving into its own picker or suggestion list does not.
        root.LostFocus += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            if (ReferenceEquals(editorRoot, root) && !pickerOpen && !FocusWithin(root))
                EndEdit(false);
        });
        editorRoot = root;
        editorBox = box;
        IsTabStop = false;
        Content = root;
        watched = cell;
        cell.PropertyChanged += CellPropertyChanged;
        // Typed over a value: the first character is already in the editor, so it is a change.
        if (replaceWith != null && replaceWith != startValue)
            Commit(cell, replaceWith);
        // Focus once loaded; pointer edits start on release, after the list item has handled the press.
        var state = focus == FocusState.Unfocused ? FocusState.Programmatic : focus;
        root.Loaded += (_, _) =>
        {
            if (!ReferenceEquals(editorRoot, root))
                return;
            if (box != null && box.Focus(state))
                box.SelectionStart = box.Text.Length;
            if (suggest != null && suggest.Focus(state) && Editor is { } inner)
            {
                inner.SelectionStart = inner.Text.Length;
                // A column with a fixed set of values shows them at once.
                if (cell.Choices.Count > 0 && suggest.ItemsSource is IReadOnlyList<CellSuggestion> { Count: > 0 })
                    suggest.IsSuggestionListOpen = true;
            }
            // Days are picked rather than typed: a click opens the day boxes.
            if (focus == FocusState.Pointer && cell.Type == FieldType.DayList && root is Grid { Children: [_, Button picker] })
                picker.Flyout?.ShowAt(picker);
        };
    }

    TextBox TextEditor(CellViewModel cell, string text)
    {
        var box = new TextBox { Text = text, FontSize = UiTokens.BodyFontSize, Padding = UiTokens.CellEditorPadding, MinHeight = 0, VerticalAlignment = VerticalAlignment.Stretch };
        AutomationProperties.SetName(box, cell.Field);
        if (cell.Type == FieldType.Integer || cell.Type == FieldType.KgInteger)
        {
            box.InputScope = new InputScope { Names = { new InputScopeName(InputScopeNameValue.Number) } };
            // Only digits (and a decimal point, which the checks then report where a whole number is needed).
            box.BeforeTextChanging += (_, e) => { if (!syncing && e.NewText.Any(c => !char.IsAsciiDigit(c) && c != '.')) e.Cancel = true; };
        }
        // Commit on every text change, synchronously, as the former TwoWay binding did.
        box.RegisterPropertyChangedCallback(TextBox.TextProperty, (_, _) => Commit(cell, box.Text));
        return box;
    }

    AutoSuggestBox SuggestionBox(CellViewModel cell, Func<string, IReadOnlyList<CellSuggestion>> source, string text)
    {
        var box = new AutoSuggestBox { Text = text, FontSize = UiTokens.BodyFontSize, MinHeight = 0, VerticalAlignment = VerticalAlignment.Stretch, UpdateTextOnSelect = false };
        box.ItemsSource = source(text);
        box.RegisterPropertyChangedCallback(AutoSuggestBox.TextProperty, (_, _) => Commit(cell, box.Text));
        box.TextChanged += (_, e) =>
        {
            if (e.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
                return;
            var items = source(box.Text);
            box.ItemsSource = items;
            box.IsSuggestionListOpen = items.Count > 0;
        };
        // The value goes into the cell, not the text shown in the list.
        box.SuggestionChosen += (_, e) => { if (e.SelectedItem is CellSuggestion chosen) ShowInEditor(chosen.Value); };
        box.QuerySubmitted += (_, e) =>
        {
            if (e.ChosenSuggestion is CellSuggestion chosen)
                ShowInEditor(chosen.Value);
            box.IsSuggestionListOpen = false;
        };
        return box;
    }

    Grid WithPicker(TextBox box, Flyout flyout, string glyph, string tip)
    {
        var picker = new Button
        {
            Content = new FontIcon { Glyph = glyph, FontSize = UiTokens.BodyFontSize - 2 },
            Padding = UiTokens.CellPickerPadding,
            MinWidth = 0,
            VerticalAlignment = VerticalAlignment.Stretch,
            IsTabStop = false,
            Flyout = flyout
        };
        AutomationProperties.SetName(picker, tip);
        ToolTipService.SetToolTip(picker, tip);
        flyout.Opened += (_, _) => pickerOpen = true;
        flyout.Closed += (_, _) =>
        {
            pickerOpen = false;
            if (editorBox is { } open && ReferenceEquals(open, box))
                box.Focus(FocusState.Programmatic);
        };
        var grid = new Grid { ColumnSpacing = 2 };
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        Grid.SetColumn(picker, 1);
        grid.Children.Add(box);
        grid.Children.Add(picker);
        return grid;
    }

    Flyout DatePicker(CellViewModel cell, TextBox box)
    {
        var calendar = new CalendarView { SelectionMode = CalendarViewSelectionMode.Single, IsTodayHighlighted = true };
        var flyout = new Flyout { Content = calendar, Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft };
        var choosing = false;
        flyout.Opening += (_, _) =>
        {
            choosing = true;
            calendar.SelectedDates.Clear();
            if (CellInput.DateOf(box.Text) is { } day)
            {
                var shown = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue));
                calendar.SelectedDates.Add(shown);
                calendar.SetDisplayDate(shown);
            }
            choosing = false;
        };
        calendar.SelectedDatesChanged += (_, e) =>
        {
            if (choosing || e.AddedDates.Count == 0)
                return;
            ShowInEditor(CellInput.WithDate(box.Text, DateOnly.FromDateTime(e.AddedDates[0].Date)));
            flyout.Hide();
        };
        return flyout;
    }

    Flyout DaysPicker(CellViewModel cell, TextBox box)
    {
        var panel = new StackPanel { Spacing = UiTokens.SpacingSmall };
        var boxes = new List<CheckBox>();
        var setting = false;
        void Apply() { if (!setting) ShowInEditor(CellInput.JoinDays(boxes.Where(b => b.IsChecked == true).Select(b => (string)b.Tag))); }
        void Set(IEnumerable<string> days)
        {
            var chosen = days.ToHashSet(StringComparer.Ordinal);
            setting = true;
            try { foreach (var day in boxes) day.IsChecked = chosen.Contains((string)day.Tag); }
            finally { setting = false; }
        }
        foreach (var day in Schemas.WeekdayOrder)
        {
            var check = new CheckBox { Content = cell.DayName(day), Tag = day, MinWidth = 0 };
            check.Checked += (_, _) => Apply();
            check.Unchecked += (_, _) => Apply();
            boxes.Add(check);
            panel.Children.Add(check);
        }
        var quick = new StackPanel { Orientation = Orientation.Horizontal, Spacing = UiTokens.SpacingSmall };
        foreach (var (key, days) in new[] { ("CellDaysEvery", Schemas.WeekdayOrder), ("CellDaysWorking", CellInput.WorkingDays), ("CellDaysNone", (IReadOnlyList<string>)[]) })
        {
            var button = new HyperlinkButton { Content = cell.Label(key), Padding = new Thickness(4, 2, 4, 2) };
            button.Click += (_, _) => { Set(days); Apply(); };
            quick.Children.Add(button);
        }
        panel.Children.Add(quick);
        var flyout = new Flyout { Content = panel, Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft };
        flyout.Opening += (_, _) => Set(CellInput.Days(box.Text));
        return flyout;
    }

    string EditorText => editorRoot is AutoSuggestBox suggest ? suggest.Text : editorBox?.Text ?? "";
    // Puts a value into the open editor; the editor's own change commits it.
    void ShowInEditor(string text)
    {
        if (editorRoot is AutoSuggestBox suggest)
        { if (suggest.Text != text) suggest.Text = text; }
        else if (editorBox is { } box && box.Text != text)
        {
            syncing = true;
            try { box.Text = text; }
            finally { syncing = false; }
            box.SelectionStart = box.Text.Length;
            if (watched is { } cell)
                Commit(cell, text);
        }
    }

    // Ends the edit. The typed text is first put into the column's format (a time typed as 930 becomes 09:30); an edit
    // taken back with Escape has already been returned to its starting value.
    void EndEdit(bool keepFocus, bool normalize = true, bool reverted = false)
    {
        if (editorRoot is null)
            return;
        var cell = watched;
        if (cell != null)
        {
            if (normalize && !reverted && !cell.IsReadOnly)
            {
                var normalized = CellInput.Normalize(cell.Type, cell.Value);
                if (normalized != cell.Value)
                    Commit(cell, normalized);
            }
            cell.PropertyChanged -= CellPropertyChanged;
            watched = null;
        }
        editorRoot = null;
        editorBox = null;
        pickerOpen = false;
        Content = display;
        display = null;
        IsTabStop = true;
        cell?.EditEnded?.Invoke(reverted);
        if (keepFocus)
            FocusQuietly();
    }
    // Focus on the cell without opening its editor.
    void FocusQuietly()
    {
        // Already focused: no GotFocus follows to consume the flag.
        if (FocusState != FocusState.Unfocused)
            return;
        returningFocus = true;
        if (!Focus(FocusState.Keyboard))
            returningFocus = false;
    }
    void Revert()
    {
        if (watched is { } cell && cell.Value != startValue)
        {
            ShowInEditor(startValue);
            Commit(cell, startValue);
        }
        EndEdit(true, reverted: true);
    }
    void Toggle(CellViewModel cell)
    {
        if (cell.IsReadOnly || !cell.CanEdit())
            return;
        cell.Value = cell.Value == "TRUE" ? "FALSE" : "TRUE";
        cell.EditEnded?.Invoke(false);
    }

    internal void Commit(string text) { if (Cell is { } cell) Commit(cell, text); }
    void Commit(CellViewModel cell, string text)
    {
        if (syncing || cell.Value == text)
            return;
        cell.Value = text;
        // Rejected (locked table, deleted row): show the value that was kept.
        if (ReferenceEquals(watched, cell) && editorRoot != null && cell.Value != text)
        {
            syncing = true;
            try
            {
                if (editorRoot is AutoSuggestBox suggest)
                    suggest.Text = cell.Value;
                else if (editorBox != null)
                    editorBox.Text = cell.Value;
            }
            finally { syncing = false; }
        }
    }

    // --- Keyboard ---
    static bool Down(VirtualKey key) => InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);
    protected override void OnPreviewKeyDown(KeyRoutedEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (Cell is not { } cell || e.Handled || XamlRoot is null)
            return;
        var shift = Down(VirtualKey.Shift);
        var control = Down(VirtualKey.Control);
        // Ctrl+Enter shows the whole record, from an open editor or not.
        if (e.Key == VirtualKey.Enter && control && cell.OpenDetails is { } details)
        {
            e.Handled = true;
            EndEdit(false);
            details();
            return;
        }
        if (editorRoot != null)
        {
            var listOpen = editorRoot is AutoSuggestBox { IsSuggestionListOpen: true };
            switch (e.Key)
            {
                case VirtualKey.Tab:
                    e.Handled = true;
                    Leave(shift ? Direction.Previous : Direction.Next, edit: true);
                    break;
                case VirtualKey.Enter when !listOpen:
                    e.Handled = true;
                    Leave(shift ? Direction.Up : Direction.Down, edit: false);
                    break;
                // One Escape takes the edit back, with the suggestion list open or not.
                case VirtualKey.Escape:
                    e.Handled = true;
                    if (editorRoot is AutoSuggestBox open)
                        open.IsSuggestionListOpen = false;
                    Revert();
                    break;
                case VirtualKey.Up or VirtualKey.Down when editorRoot is not AutoSuggestBox:
                    e.Handled = true;
                    Leave(e.Key == VirtualKey.Up ? Direction.Up : Direction.Down, edit: false);
                    break;
            }
            return;
        }
        if (FocusManager.GetFocusedElement(XamlRoot) is not EditableCell focused || !ReferenceEquals(focused, this))
            return;
        switch (e.Key)
        {
            case VirtualKey.Enter or VirtualKey.F2:
                e.Handled = true;
                BeginEdit(FocusState.Keyboard);
                break;
            case VirtualKey.Space when cell.Type == FieldType.Bool:
                e.Handled = true;
                Toggle(cell);
                break;
            case VirtualKey.Tab:
                e.Handled = true;
                Go(shift ? Direction.Previous : Direction.Next, edit: true);
                break;
            case VirtualKey.Up: e.Handled = true; Go(Direction.Up, edit: false); break;
            case VirtualKey.Down: e.Handled = true; Go(Direction.Down, edit: false); break;
            case VirtualKey.Left: e.Handled = true; Go(Direction.Left, edit: false); break;
            case VirtualKey.Right: e.Handled = true; Go(Direction.Right, edit: false); break;
            case VirtualKey.Delete or VirtualKey.Back when !control:
                e.Handled = true;
                // A required value is replaced by typing, not cleared.
                if (!cell.Required && !cell.IsReadOnly && cell.CanEdit() && cell.Value.Length > 0)
                { cell.Value = ""; cell.EditEnded?.Invoke(false); }
                break;
            case VirtualKey.C when control:
                e.Handled = true;
                var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
                package.SetText(cell.Value);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
                break;
            case VirtualKey.V when control:
                e.Handled = true;
                _ = PasteAsync(cell);
                break;
            case VirtualKey.D when control:
                e.Handled = true;
                if (cell.FillDown is { } fill)
                    _ = fill();
                break;
        }
    }
    async Task PasteAsync(CellViewModel cell)
    {
        var content = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
        if (!content.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
            return;
        var text = await content.GetTextAsync();
        var table = CellInput.Table(text);
        // One value goes into this cell as one step; a block goes through the window, which shows what it would change.
        if (table is [[var single]])
        {
            if (cell.IsReadOnly || !cell.CanEdit() || !ReferenceEquals(Cell, cell))
                return;
            cell.Value = CellInput.Normalize(cell.Type, single);
            cell.EditEnded?.Invoke(false);
        }
        else if (cell.Paste is { } paste)
            await paste(text);
    }
    // A character typed on a cell that is not being edited starts an edit that replaces the value with it.
    void TypedOnCell(UIElement sender, CharacterReceivedRoutedEventArgs e)
    {
        if (editorRoot != null || e.Character < ' ' || XamlRoot is null || Cell is not { IsReadOnly: false } cell || cell.Type == FieldType.Bool || Down(VirtualKey.Control))
            return;
        if (FocusManager.GetFocusedElement(XamlRoot) is not EditableCell focused || !ReferenceEquals(focused, this))
            return;
        e.Handled = true;
        BeginEdit(FocusState.Keyboard, e.Character.ToString());
    }

    // --- Moving between cells ---
    // Directions as constants: the enum inventory locks every enum in the assembly.
    static class Direction { public const int Left = 0, Right = 1, Up = 2, Down = 3, Next = 4, Previous = 5; }
    // Ends the edit and moves; without a cell to move to, the focus stays on this one.
    void Leave(int direction, bool edit)
    {
        var target = Neighbour(direction);
        EndEdit(keepFocus: target is null);
        if (target != null)
            Enter(target, edit);
    }
    void Go(int direction, bool edit)
    {
        if (Neighbour(direction) is { } target)
            Enter(target, edit);
    }
    static void Enter(Control target, bool edit)
    {
        target.StartBringIntoView();
        if (target is EditableCell cell && !edit)
            cell.FocusQuietly();
        else
            target.Focus(FocusState.Keyboard);
    }
    // The next cell that takes focus in that direction on the shown page: Tab and Shift+Tab continue on the next or
    // previous row, the arrows stop at the edge.
    Control? Neighbour(int direction)
    {
        if (VisualTreeHelper.GetParent(this) is not DataRowPresenter presenter)
            return null;
        var item = Ancestor<ListViewItem>(presenter);
        if (item is null || ItemsControl.ItemsControlFromItemContainer(item) is not ListViewBase list)
            return null;
        var row = list.IndexFromContainer(item);
        var column = presenter.Children.IndexOf(this);
        var count = presenter.Children.Count;
        switch (direction)
        {
            case Direction.Up or Direction.Down:
                for (var r = row + (direction == Direction.Up ? -1 : 1); r >= 0 && r < list.Items.Count; r += direction == Direction.Up ? -1 : 1)
                    if (CellAt(list, r, column) is { } above)
                        return above;
                return null;
            default:
                var step = direction is Direction.Left or Direction.Previous ? -1 : 1;
                var wraps = direction is Direction.Next or Direction.Previous;
                for (int r = row, c = column + step; r >= 0 && r < list.Items.Count;)
                {
                    if (c < 0 || c >= count)
                    {
                        if (!wraps)
                            return null;
                        r += step;
                        c = step > 0 ? 0 : count - 1;
                        continue;
                    }
                    if (CellAt(list, r, c) is { } found)
                        return found;
                    c += step;
                }
                return null;
        }
    }
    static Control? CellAt(ListViewBase list, int row, int column)
    {
        if (list.ContainerFromIndex(row) is not ListViewItem item)
        {
            list.ScrollIntoView(list.Items[row]);
            list.UpdateLayout();
            if (list.ContainerFromIndex(row) is not ListViewItem realized)
                return null;
            item = realized;
        }
        var presenter = MainWindow.FindVisual<DataRowPresenter>(item);
        if (presenter is null || column >= presenter.Children.Count)
            return null;
        return presenter.Children[column] switch
        {
            EditableCell cell => cell,
            Control { IsEnabled: true, IsTabStop: true } control => control,
            _ => null
        };
    }
    static T? Ancestor<T>(DependencyObject start) where T : DependencyObject
    {
        for (var node = VisualTreeHelper.GetParent(start); node != null; node = VisualTreeHelper.GetParent(node))
            if (node is T found)
                return found;
        return null;
    }
    bool FocusWithin(DependencyObject root)
    {
        if (XamlRoot is null)
            return false;
        for (var node = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject; node != null; node = VisualTreeHelper.GetParent(node))
            if (ReferenceEquals(node, root))
                return true;
        return false;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new EditableCellAutomationPeer(this);
}

sealed partial class EditableCellAutomationPeer(EditableCell owner) : FrameworkElementAutomationPeer(owner), IValueProvider
{
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Edit;
    protected override string GetClassNameCore() => nameof(EditableCell);
    protected override object? GetPatternCore(PatternInterface patternInterface) => patternInterface == PatternInterface.Value ? this : base.GetPatternCore(patternInterface);
    // The value is exposed by this element; the display text is not a separate child.
    protected override IList<AutomationPeer>? GetChildrenCore() => owner.IsEditing ? base.GetChildrenCore() : null;
    public bool IsReadOnly => owner.Cell?.IsReadOnly ?? true;
    public string Value => owner.Cell?.Value ?? "";
    public void SetValue(string value)
    {
        if (IsReadOnly)
            throw new ElementNotEnabledException();
        owner.Commit(value);
    }
}
