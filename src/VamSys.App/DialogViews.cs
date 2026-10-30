using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VamSys.Presentation;

namespace VamSys.App;

public sealed partial class MessageDialogView : UserControl
{
    public MessageDialogView(MessageDialogViewModel model) { Model = model; InitializeComponent(); }
    public MessageDialogViewModel Model { get; }
}

public sealed partial class ListDialogView : UserControl
{
    public ListDialogView(ListDialogViewModel model) { Model = model; InitializeComponent(); }
    public ListDialogViewModel Model { get; }
}

public sealed partial class TextInputDialogView : UserControl
{
    public TextInputDialogView(TextInputDialogViewModel model) { Model = model; InitializeComponent(); }
    public TextInputDialogViewModel Model { get; }
}

public sealed partial class ChoiceDialogView : UserControl
{
    public ChoiceDialogView(ChoiceDialogViewModel model) { Model = model; InitializeComponent(); }
    public ChoiceDialogViewModel Model { get; }
}

public sealed partial class TaskDetailsDialogView : UserControl
{
    public TaskDetailsDialogView(TaskDetailsDialogViewModel model) { Model = model; InitializeComponent(); }
    public TaskDetailsDialogViewModel Model { get; }
}

// The dialog views' sizes and modes, for their compiled bindings.
public static class DialogViews
{
    public static double MaxHeight(DialogHeight height) => height switch
    {
        DialogHeight.List => UiTokens.DialogListMaxHeight,
        DialogHeight.ReviewDetail => UiTokens.ReviewDetailMaxHeight,
        DialogHeight.FleetPreview => UiTokens.FleetPreviewMaxHeight,
        DialogHeight.BatchPreview => UiTokens.BatchPreviewMaxHeight,
        DialogHeight.TaskDetail => UiTokens.TaskDetailMaxHeight,
        _ => double.PositiveInfinity
    };
    public static ListViewSelectionMode Selection(bool selectable) => selectable ? ListViewSelectionMode.Single : ListViewSelectionMode.None;
    public static ScrollMode ScrollMode(bool scrolls) => scrolls ? Microsoft.UI.Xaml.Controls.ScrollMode.Auto : Microsoft.UI.Xaml.Controls.ScrollMode.Disabled;
    public static ScrollBarVisibility ScrollBar(bool scrolls) => scrolls ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;

    // The view for a dialog model; dialogs not yet given a model of their own pass their content directly.
    public static UIElement For(DialogViewModel dialog) => dialog switch
    {
        MessageDialogViewModel message => new MessageDialogView(message),
        ListDialogViewModel list => new ListDialogView(list),
        TextInputDialogViewModel input => new TextInputDialogView(input),
        ChoiceDialogViewModel choice => new ChoiceDialogView(choice),
        TaskDetailsDialogViewModel details => new TaskDetailsDialogView(details),
        _ => throw new ArgumentException($"No view for {dialog.GetType().Name}", nameof(dialog))
    };
}
