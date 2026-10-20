using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System.ComponentModel;

namespace VamSys.App;

// A table cell that shows its value as text and creates a TextBox only while it is being edited.
// A realized page holds thousands of cells; a TextBox per cell dominated scrolling and native memory.
// Automation still sees an edit control with the cell value, so screen readers and UIA scripts keep working.
public sealed partial class EditableCell : ContentControl
{
    public static readonly DependencyProperty CellProperty = DependencyProperty.Register(nameof(Cell), typeof(CellViewModel), typeof(EditableCell),
        new PropertyMetadata(null, (target, args) => ((EditableCell)target).CellChanged((CellViewModel?)args.OldValue, (CellViewModel?)args.NewValue)));
    public CellViewModel? Cell { get => (CellViewModel?)GetValue(CellProperty); set => SetValue(CellProperty, value); }
    UIElement? display;
    internal TextBox? Editor { get; private set; }
    bool syncing;

    public EditableCell()
    {
        IsTabStop = true; UseSystemFocusVisuals = true;
        HorizontalContentAlignment = HorizontalAlignment.Stretch; VerticalContentAlignment = VerticalAlignment.Stretch;
    }
    // Recycled for another row: an open editor belongs to the previous cell.
    void CellChanged(CellViewModel? old, CellViewModel? next) => EndEdit(false);
    // Subscribed only while editing, so a long-lived cell model never keeps an idle control alive.
    CellViewModel? watched;
    void CellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (Editor is null || Cell is null) return;
        if (e.PropertyName == nameof(CellViewModel.IsReadOnly) && Cell.IsReadOnly) EndEdit(false);
        else if (e.PropertyName == nameof(CellViewModel.Value) && Editor.Text != Cell.Value) { syncing = true; try { Editor.Text = Cell.Value; } finally { syncing = false; } }
    }
    protected override void OnGotFocus(RoutedEventArgs e)
    {
        base.OnGotFocus(e);
        // Only tabbing into a cell edits it; focus restored on window activation or after Enter/Escape does not.
        if (!ReferenceEquals(e.OriginalSource, this)) return;
        // GotFocus is raised asynchronously, so the returned focus is consumed here.
        if (returningFocus) { returningFocus = false; return; }
        if (FocusState == FocusState.Keyboard) BeginEdit(FocusState.Keyboard);
    }
    bool returningFocus;
    // A click edits the cell, as clicking the former TextBox did, and does not toggle row selection.
    // The editor opens on release: the list item takes focus while handling the press.
    bool pressed;
    protected override void OnPointerPressed(PointerRoutedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Editor != null || Cell is null || Cell.IsReadOnly) return;
        e.Handled = true; pressed = CapturePointer(e.Pointer);
    }
    protected override void OnPointerReleased(PointerRoutedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (pressed) { pressed = false; ReleasePointerCapture(e.Pointer); e.Handled = true; BeginEdit(FocusState.Pointer); }
        else if (Editor != null) e.Handled = true;
    }
    protected override void OnPointerCaptureLost(PointerRoutedEventArgs e) { base.OnPointerCaptureLost(e); pressed = false; }
    protected override void OnTapped(TappedRoutedEventArgs e) { base.OnTapped(e); if (Editor != null) e.Handled = true; }
    internal void BeginEdit(FocusState focus = FocusState.Programmatic)
    {
        if (Editor != null || Cell is not { IsReadOnly: false } cell) return;
        display = Content as UIElement;
        var editor = new TextBox { Text = cell.Value, FontSize = 14, Padding = new Thickness(8, 2, 8, 2), MinHeight = 0, VerticalAlignment = VerticalAlignment.Stretch };
        AutomationProperties.SetName(editor, cell.Field);
        // Commit on every text change, synchronously, as the former TwoWay binding did.
        editor.RegisterPropertyChangedCallback(TextBox.TextProperty, (_, _) => Commit(editor.Text));
        editor.LostFocus += (_, _) => EndEdit(false);
        editor.KeyDown += (_, e) => { if (e.Key is Windows.System.VirtualKey.Enter or Windows.System.VirtualKey.Escape) { e.Handled = true; EndEdit(true); } };
        Editor = editor; IsTabStop = false; Content = editor;
        watched = cell; cell.PropertyChanged += CellPropertyChanged;
        // Focus once loaded; pointer edits start on release, after the list item has handled the press.
        var state = focus == FocusState.Unfocused ? FocusState.Programmatic : focus;
        editor.Loaded += (_, _) => { if (ReferenceEquals(Editor, editor) && editor.Focus(state)) editor.SelectionStart = editor.Text.Length; };
    }
    void EndEdit(bool keepFocus)
    {
        if (Editor is null) return;
        if (watched != null) { watched.PropertyChanged -= CellPropertyChanged; watched = null; }
        Editor = null; Content = display; display = null; IsTabStop = true;
        if (!keepFocus) return;
        returningFocus = true;
        if (!Focus(FocusState.Keyboard)) returningFocus = false;
    }
    internal void Commit(string text)
    {
        if (syncing || Cell is not { } cell || cell.Value == text) return;
        cell.Value = text;
        // Rejected (locked table, deleted row): show the value that was kept.
        if (Editor != null && cell.Value != text) { syncing = true; try { Editor.Text = cell.Value; } finally { syncing = false; } }
    }
    protected override AutomationPeer OnCreateAutomationPeer() => new EditableCellAutomationPeer(this);
}

sealed partial class EditableCellAutomationPeer(EditableCell owner) : FrameworkElementAutomationPeer(owner), IValueProvider
{
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Edit;
    protected override string GetClassNameCore() => nameof(EditableCell);
    protected override object? GetPatternCore(PatternInterface patternInterface) => patternInterface == PatternInterface.Value ? this : base.GetPatternCore(patternInterface);
    // The value is exposed by this element; the display text is not a separate child.
    protected override IList<AutomationPeer>? GetChildrenCore() => owner.Editor is null ? null : base.GetChildrenCore();
    public bool IsReadOnly => owner.Cell?.IsReadOnly ?? true;
    public string Value => owner.Cell?.Value ?? "";
    public void SetValue(string value)
    {
        if (IsReadOnly) throw new ElementNotEnabledException();
        owner.Commit(value);
    }
}
