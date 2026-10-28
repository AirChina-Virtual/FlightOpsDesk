using VamSys.Core;
using VamSys.Presentation;

namespace VamSys.UnitTests.Presentation;

public class ImportViewModelTests
{
    [Fact]
    public void The_page_shows_its_texts_in_the_current_language_and_follows_a_switch()
    {
        var language = new LocalizationService();
        var model = new ImportViewModel(language);
        var chinese = (model.Title, model.Help, model.WeightHint, model.ImportLabel, model.RefreshLabel);
        Assert.Equal(language.Get("ApiRefresh"), model.RefreshLabel);
        var changed = new List<string?>();
        model.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        language.SetLanguage("en-US");
        model.RefreshLanguage();
        Assert.Equal([nameof(ImportViewModel.Title), nameof(ImportViewModel.Help), nameof(ImportViewModel.WeightHint), nameof(ImportViewModel.ImportLabel), nameof(ImportViewModel.RefreshLabel)], changed);
        Assert.NotEqual(chinese, (model.Title, model.Help, model.WeightHint, model.ImportLabel, model.RefreshLabel));
        Assert.Equal(language.Get("Text_5A2BBDEA04"), model.ImportLabel);
        Assert.DoesNotContain("[", model.WeightHint, StringComparison.Ordinal);
    }
}
