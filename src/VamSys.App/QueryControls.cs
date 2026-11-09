using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using VamSys.Presentation;

namespace VamSys.App;

// The data page's filter as a row of choices that are all in view, the one in force pressed. It keeps the selector's
// SelectedIndex and SelectionChanged, which the query code and the window scripts use.
sealed class ChoiceChips
{
    readonly List<ToggleButton> chips = [];
    int selected;
    public StackPanel Panel { get; } = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
    public event Action? SelectionChanged;

    public ChoiceChips(int count)
    {
        for (var index = 0; index < count; index++)
        {
            var chip = new ToggleButton { MinWidth = 0 };
            // Pressing one chooses it (by pointer, keyboard or automation); pressing the chosen one again keeps it chosen.
            var chosen = index;
            chip.Checked += (_, _) => { if (!showing) SelectedIndex = chosen; };
            var at = index;
            chip.Unchecked += (_, _) => { if (!showing && at == selected) Show(); };
            chips.Add(chip);
            Panel.Children.Add(chip);
        }
        Show();
    }
    public ToggleButton Chip(int index) => chips[index];
    public int SelectedIndex
    {
        get => selected;
        set
        {
            var changed = value != selected;
            selected = value;
            Show();
            if (changed)
                SelectionChanged?.Invoke();
        }
    }
    public bool IsEnabled { set { foreach (var chip in chips) chip.IsEnabled = value; } }
    public void SetLabels(IReadOnlyList<string> labels)
    {
        for (var n = 0; n < chips.Count && n < labels.Count; n++)
            if (chips[n].Content as string != labels[n])
            {
                chips[n].Content = labels[n];
                AutomationProperties.SetName(chips[n], labels[n]);
            }
    }
    bool showing;
    void Show()
    {
        showing = true;
        try
        {
            for (var n = 0; n < chips.Count; n++)
                chips[n].IsChecked = n == selected;
        }
        finally { showing = false; }
    }
}

// The sort as a menu: no sort or a column, and the direction. SelectedItem is the stored sort (DataSort), and Items the
// columns, as with the selector it replaces; ItemsSource gives the columns of the resource shown.
sealed class SortPicker
{
    readonly Func<string, string> label;
    readonly Func<string, string> text;
    readonly MenuFlyout menu = new();
    List<string> columns = [];
    string? selected;
    public DropDownButton Button { get; } = new();
    public event Action? SelectionChanged;

    // label: a column's display name; text: a resource text by key.
    public SortPicker(Func<string, string> label, Func<string, string> text)
    {
        this.label = label;
        this.text = text;
        AutomationProperties.SetAutomationId(Button, "DataSort");
        Button.Flyout = menu;
        menu.Opening += (_, _) => Fill();
        Refresh();
    }
    public IReadOnlyList<string> Items => columns;
    public List<string> ItemsSource { set { columns = [.. value.Where(c => c != "_delete")]; Refresh(); } }
    public int SelectedIndex { set { if (value < 0) SelectedItem = null; } }
    public object? SelectedItem
    {
        get => selected;
        set
        {
            var next = value as string;
            if (next == selected)
                return;
            selected = next;
            Refresh();
            SelectionChanged?.Invoke();
        }
    }
    public (string? Field, bool Descending) Current => DataSort.Parse(selected);
    public void Refresh()
    {
        var (field, descending) = Current;
        var shown = field is null ? text("TableSort") : text("TableSortBy") + label(field) + (descending ? " ↓" : " ↑");
        Button.Content = shown;
        AutomationProperties.SetName(Button, shown);
    }
    void Fill()
    {
        menu.Items.Clear();
        var (field, descending) = Current;
        RadioMenuFlyoutItem Choice(string shown, bool on, string group, Action pick)
        {
            var item = new RadioMenuFlyoutItem { Text = shown, IsChecked = on, GroupName = group };
            item.Click += (_, _) => pick();
            return item;
        }
        menu.Items.Add(Choice(text("TableSortNone"), field is null, "column", () => SelectedItem = null));
        foreach (var column in columns)
            menu.Items.Add(Choice(label(column), column == field, "column", () => SelectedItem = DataSort.Format(column, field == column && descending)));
        menu.Items.Add(new MenuFlyoutSeparator());
        var ascending = Choice(text("TableSortAscending"), field != null && !descending, "direction", () => SelectedItem = DataSort.Format(Current.Field, false));
        var down = Choice(text("TableSortDescending"), field != null && descending, "direction", () => SelectedItem = DataSort.Format(Current.Field, true));
        ascending.IsEnabled = down.IsEnabled = field != null;
        menu.Items.Add(ascending);
        menu.Items.Add(down);
    }
}
