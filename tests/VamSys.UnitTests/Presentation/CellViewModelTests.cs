using VamSys.Presentation;
using VamSys.Core;
using VamSys.TestSupport;

namespace VamSys.UnitTests.Presentation;

public class CellViewModelTests
{
    [Fact]
    public void Null_jumpseat_presentation_and_immutable_cells_survive_row_recycling_without_edits()
    {
        var commits = 0;
        var serverNull = true;
        var cell = new CellViewModel("Callsign", "", _ => commits++) { ShowServerNull = () => serverNull, ServerNullLabel = () => "Server did not retain", ReadOnlyForRow = r => r.Identity != null };
        var row = new DataRow { Identity = new("9", "7") };
        var model = new RowViewModel(row, [cell]);
        model.Rebind(row);
        Assert.Equal("Server did not retain", cell.VisibleText);
        Assert.True(cell.IsReadOnly);

        cell.Value = "blocked";
        Assert.Equal(0, commits);
        Assert.Equal("", cell.Value);

        serverNull = false;
        model.Rebind(new DataRow { Fields = new Dictionary<string, string> { ["Callsign"] = "ACA123" } });
        cell.Value = "ACA124";
        Assert.False(cell.IsReadOnly);
        Assert.Equal("ACA124", cell.VisibleText);
        Assert.Equal(1, commits);
    }

    [Fact]
    public void Cell_label_notifications_skip_unchanged_text_and_preserve_language_updates()
    {
        var label = "Fleet";
        var name = "Choose";
        var notices = new List<string?>();
        var cell = new CellViewModel("Fleet IDs", "1", _ => { }) { DisplayText = () => label, FleetName = () => name };
        cell.PropertyChanged += (_, args) => notices.Add(args.PropertyName);
        cell.RefreshLanguage();
        Assert.Equal(2, notices.Count);
        cell.RefreshLanguage();
        Assert.Equal(2, notices.Count);

        label = "机型";
        name = "选择";
        cell.RefreshLanguage();
        Assert.Equal(4, notices.Count);
        Assert.Equal(label, cell.DisplayValue);
        Assert.Equal(name, cell.FleetAutomationName);
    }

    [Fact]
    public void Reused_page_cells_synchronize_without_commits_and_edit_the_current_row_only()
    {
        var original = new DataRow { Fields = new Dictionary<string, string> { ["Remarks"] = "original" } };
        var replacement = new DataRow { Fields = new Dictionary<string, string> { ["Remarks"] = "replacement" } };
        var commits = 0;
        var committed = new List<(DataRow Row, string Value)>();
        RowViewModel? model = null;
        var cell = new CellViewModel("Remarks", "original", value => { commits++; committed.Add((model!.Row, value)); }) { CanEdit = () => !Schemas.Deleted(model!.Row) };
        model = new(original, [cell]);
        model.Rebind(replacement);
        Assert.Equal(0, commits);
        Assert.Equal("replacement", cell.Value);
        Assert.Same(replacement, model.Row);

        cell.Value = "typed";
        Assert.Equal(1, commits);
        Assert.Equal("typed", committed[0].Value);
        Assert.Same(replacement, committed[0].Row);
        Assert.Equal("original", original.Get("Remarks"));

        var deleted = replacement.With("_delete", "TRUE");
        model.Rebind(deleted);
        cell.Value = "rejected";
        Assert.True(cell.IsReadOnly);
        Assert.Equal(1, commits);
        Assert.Single(committed);
    }

    [Fact]
    public void The_UI_fixture_refuses_existing_files_before_creating_or_modifying_a_database()
    {
        var directory = Path.Combine(Path.GetTempPath(), "vamsys-seed-refusal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var marker = Path.Combine(directory, "existing.txt");
        File.WriteAllText(marker, "must remain unchanged");
        try
        {
            Assert.Throws<InvalidOperationException>(() => DataUiFixture.Seed(directory, 20000));
            Assert.Equal("must remain unchanged", File.ReadAllText(marker));
            Assert.Single(Directory.GetFiles(directory));
        }
        finally { Directory.Delete(directory, true); }
    }
}
