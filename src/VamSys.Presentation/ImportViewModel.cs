using CommunityToolkit.Mvvm.ComponentModel;
using VamSys.Core;

namespace VamSys.Presentation;

// The import page: how CSV import and the API refresh fill the current resource. It is rebuilt on every render;
// the window refreshes the shown one when the language changes.
public sealed class ImportViewModel(ILocalizationService language) : ObservableObject
{
    public string Title => language.Get("Text_D030C51D78");
    public string Help => language.Get("Text_3618171B0A");
    public string WeightHint => language.Get("WeightCsvHint");
    public string ImportLabel => language.Get("Text_5A2BBDEA04");
    public string RefreshLabel => language.Get("ApiRefresh");

    public void RefreshLanguage()
    {
        foreach (var name in new[] { nameof(Title), nameof(Help), nameof(WeightHint), nameof(ImportLabel), nameof(RefreshLabel) })
            OnPropertyChanged(name);
    }
}
