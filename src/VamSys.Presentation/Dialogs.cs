namespace VamSys.Presentation;

// How a dialog was closed: its primary or secondary action, or the close (cancel) button / Escape.
public enum DialogResult { Closed, Primary, Secondary }

// The largest height a dialog's list or text takes before it scrolls, as named sizes the App maps to theme tokens.
public enum DialogHeight { Unbounded, List, ReviewDetail, FleetPreview, BatchPreview }

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
