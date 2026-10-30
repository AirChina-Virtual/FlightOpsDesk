using VamSys.Presentation;

namespace VamSys.UnitTests.Presentation;

public class DialogTests
{
    sealed class Answering(DialogResult result) : IDialogService
    {
        public readonly List<DialogViewModel> Shown = [];
        public Task<DialogResult> ShowAsync(DialogViewModel dialog) { Shown.Add(dialog); return Task.FromResult(result); }
    }

    [Theory]
    [InlineData(DialogResult.Primary, true)]
    [InlineData(DialogResult.Secondary, false)]
    [InlineData(DialogResult.Closed, false)]
    public async Task Only_the_primary_action_confirms(DialogResult result, bool confirmed)
    {
        var dialogs = new Answering(result);
        var dialog = new MessageDialogViewModel { Title = "t", Message = "m", PrimaryLabel = "OK" };
        Assert.Equal(confirmed, await dialogs.ConfirmAsync(dialog));
        Assert.Same(dialog, Assert.Single(dialogs.Shown));
    }

    [Fact]
    public void Dialogs_offer_their_primary_action_and_lists_are_bounded_and_selectable_unless_said_otherwise()
    {
        var message = new MessageDialogViewModel { Title = "t", Message = "m", PrimaryLabel = "OK" };
        Assert.True(message.CanPrimary);
        Assert.Null(message.SecondaryLabel);
        Assert.False(message.Scrolls);
        Assert.Equal(DialogHeight.Unbounded, message.Height);

        var list = new ListDialogViewModel { Title = "t", PrimaryLabel = "OK", Items = ["a"] };
        Assert.Equal(DialogHeight.List, list.Height);
        Assert.True(list.Selectable);
        Assert.False(list.HasHeader);
        Assert.True(new ListDialogViewModel { Title = "t", PrimaryLabel = "OK", Items = [], Header = ["scope"] }.HasHeader);
    }

    [Fact]
    public void A_message_shows_a_second_paragraph_only_when_it_has_one()
    {
        Assert.False(new MessageDialogViewModel { Message = "m" }.HasDetail);
        Assert.False(new MessageDialogViewModel { Message = "m", Detail = "" }.HasDetail);
        Assert.True(new MessageDialogViewModel { Message = "m", Detail = "values" }.HasDetail);
    }

    [Fact]
    public void A_choice_reads_as_its_label_and_the_selection_is_the_chosen_option_or_none()
    {
        var choices = new[] { new DialogChoice("Standard", "1"), new DialogChoice("Charter", "2") };
        Assert.Equal("Charter", choices[1].ToString());
        var dialog = new ChoiceDialogViewModel { Choices = choices, SelectedIndex = 1 };
        Assert.Equal("2", dialog.Selected!.Value);
        dialog.SelectedIndex = -1;
        Assert.Null(dialog.Selected);
        dialog.SelectedIndex = 2;
        Assert.Null(dialog.Selected);
        Assert.Null(new ChoiceDialogViewModel().Selected);
    }

    [Fact]
    public void Text_input_keeps_what_was_typed_and_shows_paragraphs_only_when_given()
    {
        var input = new TextInputDialogViewModel { InputHeader = "Remote ID", Text = "102" };
        Assert.False(input.HasParagraphs);
        input.Text = "103";
        Assert.Equal("103", input.Text);
        Assert.True(new TextInputDialogViewModel { Paragraphs = ["review"] }.HasParagraphs);
    }
}
