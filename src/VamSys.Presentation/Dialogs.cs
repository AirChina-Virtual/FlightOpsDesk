namespace VamSys.Presentation;

// How a dialog was closed: its primary or secondary action, or the close (cancel) button / Escape.
public enum DialogResult { Closed, Primary, Secondary }

// The largest height a dialog's list or text takes before it scrolls, as named sizes the App maps to theme tokens.
public enum DialogHeight { Unbounded, List, ReviewDetail, FleetPreview, BatchPreview, TaskDetail }

// What every dialog shows around its content: a title and its actions. The close action (cancel) is always offered and
// is the default, so Enter never confirms by accident.
public abstract class DialogViewModel
{
    public string Title { get; init; } = "";
    public string PrimaryLabel { get; init; } = "";
    // No secondary action when null.
    public string? SecondaryLabel { get; init; }
    public bool CanPrimary { get; init; } = true;
}

// A message, e.g. a confirmation or a list of validation messages joined into text.
public sealed class MessageDialogViewModel : DialogViewModel
{
    public string Message { get; init; } = "";
    // A second paragraph under the message, when there is one.
    public string? Detail { get; init; }
    public bool HasDetail => !string.IsNullOrEmpty(Detail);
    // Long messages scroll; Height bounds the scrolling area.
    public bool Scrolls { get; init; }
    public DialogHeight Height { get; init; } = DialogHeight.Unbounded;
}

// A list of entries under optional header lines: issues, previews of the rows a batch change affects, the changes a
// submission sends. Entries are plain text, one per row.
public sealed class ListDialogViewModel : DialogViewModel
{
    public IReadOnlyList<string> Header { get; init; } = [];
    public IReadOnlyList<string> Items { get; init; } = [];
    public DialogHeight Height { get; init; } = DialogHeight.List;
    // A row can be selected (to read it more easily); previews of what will change cannot.
    public bool Selectable { get; init; } = true;
    public bool HasHeader => Header.Count > 0;
}

// A single line of text to enter, under optional paragraphs (a new workspace name, a remote ID). Text holds what was
// typed when the dialog closes.
public sealed class TextInputDialogViewModel : DialogViewModel
{
    public IReadOnlyList<string> Paragraphs { get; init; } = [];
    public bool HasParagraphs => Paragraphs.Count > 0;
    // The input's label, also its name for screen readers and UI Automation.
    public string InputHeader { get; init; } = "";
    public string Text { get; set; } = "";
    // No limit when 0.
    public int MaxLength { get; init; }
}

// One option of a choice dialog. Screen readers read the item itself, so it reads as its label.
public sealed record DialogChoice(string Label, string Value)
{
    public override string ToString() => Label;
}

// One of several options (an airline parameter). SelectedIndex holds the choice when the dialog closes.
public sealed class ChoiceDialogViewModel : DialogViewModel
{
    public string Prompt { get; init; } = "";
    public IReadOnlyList<DialogChoice> Choices { get; init; } = [];
    public int SelectedIndex { get; set; }
    public DialogChoice? Selected => SelectedIndex >= 0 && SelectedIndex < Choices.Count ? Choices[SelectedIndex] : null;
}

// A task's items with their state, evidence and diagnostics. The items are the task page's own models, so the list
// follows a task that is still running.
public sealed class TaskDetailsDialogViewModel(TaskJobViewModel job) : DialogViewModel
{
    public IReadOnlyList<TaskItemViewModel> Items => job.Items;
}

// Opens dialogs. One dialog is open at a time; while it is open the window takes no other input.
public interface IDialogService
{
    Task<DialogResult> ShowAsync(DialogViewModel dialog);
}

public static class DialogServiceExtensions
{
    // True only when the primary action was chosen.
    public static async Task<bool> ConfirmAsync(this IDialogService dialogs, DialogViewModel dialog) => await dialogs.ShowAsync(dialog) == DialogResult.Primary;
}
