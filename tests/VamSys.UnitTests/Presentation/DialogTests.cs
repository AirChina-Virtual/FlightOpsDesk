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
}
