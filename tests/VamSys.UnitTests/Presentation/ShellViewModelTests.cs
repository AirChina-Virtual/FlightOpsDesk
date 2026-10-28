using System.Xml.Linq;
using VamSys.Core;
using VamSys.Presentation;

namespace VamSys.UnitTests.Presentation;

public class ShellViewModelTests
{
    [Fact]
    public void The_shell_opens_on_the_workspace_page_without_the_resource_bar()
    {
        var shell = new ShellViewModel(new InteractionGate());
        Assert.Equal(ShellPage.Workspace, shell.Page);
        Assert.Equal("Text_3797982942", shell.PageTitleKey);
        Assert.False(shell.ShowsResourceBar);
    }

    [Fact]
    public void Navigation_is_refused_while_a_local_operation_holds_the_lock_and_allowed_again_after_it()
    {
        var gate = new InteractionGate();
        var shell = new ShellViewModel(gate);
        var local = gate.TryBeginLocal()!;
        Assert.False(shell.TryNavigate(ShellPage.Review));
        Assert.Equal(ShellPage.Workspace, shell.Page);
        local.Dispose();
        Assert.True(shell.TryNavigate(ShellPage.Review));
        Assert.Equal(ShellPage.Review, shell.Page);
    }

    [Fact]
    public async Task Navigation_follows_the_gate_during_exclusive_work_a_review_lock_and_closing()
    {
        var gate = new InteractionGate();
        var shell = new ShellViewModel(gate);
        // Exclusive work does not lock the pane (the person may look at tasks while it runs).
        using (gate.TryBeginWork())
            Assert.True(shell.TryNavigate(ShellPage.Tasks));
        // A review worker reading the workspace locks it like a local operation.
        using (gate.BeginReviewLock())
            Assert.False(shell.TryNavigate(ShellPage.Data));
        Assert.Equal(ShellPage.Tasks, shell.Page);
        Assert.True(await gate.Closing.RequestAsync(false, () => Task.CompletedTask, _ => { }));
        Assert.False(shell.TryNavigate(ShellPage.Data));
        Assert.Equal(ShellPage.Tasks, shell.Page);
    }

    [Fact]
    public void The_submission_hand_over_reaches_the_task_page_while_the_lock_is_still_held()
    {
        var gate = new InteractionGate();
        var shell = new ShellViewModel(gate);
        Assert.True(shell.TryNavigate(ShellPage.Review));
        var changed = new List<string?>();
        shell.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        using (gate.TryBeginLocal())
        {
            // The pane selection that follows the hand-over is ignored while the lock is held.
            Assert.False(shell.TryNavigate(ShellPage.Tasks));
            shell.ShowTasksBeforeUnlock();
            Assert.Equal(ShellPage.Tasks, shell.Page);
        }
        Assert.Equal(ShellPage.Tasks, shell.Page);
        Assert.Equal([nameof(ShellViewModel.Page), nameof(ShellViewModel.PageTitleKey), nameof(ShellViewModel.ShowsResourceBar)], changed);
    }

    [Theory]
    [InlineData(ShellPage.Workspace, "workspace", "Text_3797982942", false)]
    [InlineData(ShellPage.Data, "data", "Text_9BB882C622", true)]
    [InlineData(ShellPage.Import, "import", "Text_D52C17622D", true)]
    [InlineData(ShellPage.Review, "review", "Text_10CCC8AAA2", false)]
    [InlineData(ShellPage.Tasks, "tasks", "Text_00B514C36A", false)]
    [InlineData(ShellPage.Settings, "settings", "Text_C2B958136D", false)]
    public void Each_page_has_its_tag_title_and_resource_bar(ShellPage page, string tag, string titleKey, bool resourceBar)
    {
        var shell = new ShellViewModel(new InteractionGate());
        shell.TryNavigate(page);
        Assert.Equal(tag, page.Tag());
        Assert.Equal(page, ShellPages.FromTag(tag));
        Assert.Equal(titleKey, shell.PageTitleKey);
        Assert.Equal(resourceBar, shell.ShowsResourceBar);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown")]
    public void A_missing_or_unknown_tag_means_the_workspace_page(string? tag)
        => Assert.Equal(ShellPage.Workspace, ShellPages.FromTag(tag));

    [Theory]
    [InlineData(RunMode.Demo, "Text_692F811146")]
    [InlineData(RunMode.Online, "Text_C4C1F8C6F4")]
    [InlineData(RunMode.Offline, "Text_6C352EE0B0")]
    public void The_mode_badge_names_the_run_mode(RunMode mode, string key)
        => Assert.Equal(key, ShellViewModel.ModeBadgeKey(mode));

    [Fact]
    public void Navigating_to_the_current_page_raises_no_change()
    {
        var shell = new ShellViewModel(new InteractionGate());
        var changed = 0;
        shell.PropertyChanged += (_, _) => changed++;
        Assert.True(shell.TryNavigate(ShellPage.Workspace));
        Assert.Equal(0, changed);
    }

    // The window selects pane items by ShellPage.PaneIndex, so the pane must list the pages in enum order with their tags.
    [Fact]
    public void The_window_pane_lists_the_pages_in_shell_order_with_their_tags()
    {
        var xaml = XDocument.Load(Path.Combine(Inventory.SourceScan.Src, "VamSys.App", "MainWindow.xaml"));
        var tags = xaml.Descendants().Where(e => e.Name.LocalName == "NavigationViewItem").Select(e => (string?)e.Attribute("Tag")).ToList();
        Assert.Equal(ShellPages.All.Select(p => p.Tag()), tags);
        Assert.All(ShellPages.All, p => Assert.Equal((int)p, p.PaneIndex()));
    }
}
