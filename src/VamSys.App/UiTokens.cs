using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace VamSys.App;

// Typed access to Theme.xaml for pages that are still built in code. Every key here must exist in Theme.xaml
// (ThemeTokenTests); XAML references the same keys with StaticResource or ThemeResource.
public static class UiTokens
{
    static T Get<T>(string key) => (T)Application.Current.Resources[key];

    public static double BadgeFontSize => Get<double>("BadgeFontSize");
    public static double BodyFontSize => Get<double>("BodyFontSize");
    public static double SubtitleFontSize => Get<double>("SubtitleFontSize");
    public static double SubsectionFontSize => Get<double>("SubsectionFontSize");
    public static double SectionFontSize => Get<double>("SectionFontSize");
    public static double DataPageTitleFontSize => Get<double>("DataPageTitleFontSize");
    public static double PageTitleFontSize => Get<double>("PageTitleFontSize");

    public static double SpacingSmall => Get<double>("SpacingSmall");
    public static double SpacingMedium => Get<double>("SpacingMedium");
    public static double SpacingRelaxed => Get<double>("SpacingRelaxed");
    public static double SpacingLarge => Get<double>("SpacingLarge");
    public static double SpacingPage => Get<double>("SpacingPage");

    public static Thickness PageMargin => Get<Thickness>("PageMargin");
    public static Thickness DataPageMargin => Get<Thickness>("DataPageMargin");
    public static Thickness StartupErrorMargin => Get<Thickness>("StartupErrorMargin");
    public static Thickness WorkspaceCardPadding => Get<Thickness>("WorkspaceCardPadding");
    public static CornerRadius CardCornerRadius => Get<CornerRadius>("CardCornerRadius");
    public static Brush CardBackgroundBrush => Get<Brush>("CardBackgroundBrush");
    public static Style SecondaryTextStyle => Get<Style>("SecondaryTextStyle");
    public static Style CardStyle => Get<Style>("CardStyle");
    public static Style CardTitleStyle => Get<Style>("CardTitleStyle");
    public static double DetailsPaneWidth => Get<double>("DetailsPaneWidth");
    public static Thickness DetailsFieldsPadding => Get<Thickness>("DetailsFieldsPadding");
    public static Thickness DetailsLabelMargin => Get<Thickness>("DetailsLabelMargin");
    public static Thickness IconButtonPadding => Get<Thickness>("IconButtonPadding");
    public static double SpacingTight => Get<double>("SpacingTight");
    public static Style ColumnHeaderTextStyle => Get<Style>("ColumnHeaderTextStyle");
    public static Style StatusTextStyle => Get<Style>("StatusTextStyle");
    public static Style StatusErrorTextStyle => Get<Style>("StatusErrorTextStyle");
    public static Style ModeOfflineBadgeStyle => Get<Style>("ModeOfflineBadgeStyle");
    public static Style ModeDemoBadgeStyle => Get<Style>("ModeDemoBadgeStyle");
    public static Style ModeOnlineBadgeStyle => Get<Style>("ModeOnlineBadgeStyle");

    public static double DataRowHeight(bool compact) => Get<double>(compact ? "DataRowHeightCompact" : "DataRowHeightComfortable");
    public static double CellHeight(bool compact) => Get<double>(compact ? "CellHeightCompact" : "CellHeightComfortable");
    public static Thickness CellMargin(bool compact) => Get<Thickness>(compact ? "CellMarginCompact" : "CellMarginComfortable");
    public static Thickness CellEditorPadding => Get<Thickness>("CellEditorPadding");
    public static Thickness CellPickerPadding => Get<Thickness>("CellPickerPadding");
    public static double HintDelayMilliseconds => Get<double>("HintDelayMilliseconds");
    // The cell box for its look (CellViewModel.Look): plain, changed from the server's value, or with a problem.
    public static Style CellBoxStyle(int look) => Get<Style>(look switch { 2 => "CellIssueBoxStyle", 1 => "CellChangedBoxStyle", _ => "CellBoxStyle" });
    public static Thickness ColumnHeaderMargin => Get<Thickness>("ColumnHeaderMargin");
    public static Thickness DataRowPadding => Get<Thickness>("DataRowPadding");
    public static Thickness DataHeaderInset => Get<Thickness>("DataHeaderInset");
    public static Thickness HeaderCheckMargin => Get<Thickness>("HeaderCheckMargin");
    public static double LockedTableOpacity => Get<double>("LockedTableOpacity");

    public static double SearchBoxWidth => Get<double>("SearchBoxWidth");
    public static double FilterPickerWidth => Get<double>("FilterPickerWidth");
    public static double SortPickerWidth => Get<double>("SortPickerWidth");
    public static double DensityPickerWidth => Get<double>("DensityPickerWidth");
    public static double SettingsFieldMaxWidth => Get<double>("SettingsFieldMaxWidth");
    public static double LanguagePickerMinWidth => Get<double>("LanguagePickerMinWidth");
    public static double RuleFieldPickerMinWidth => Get<double>("RuleFieldPickerMinWidth");
    public static double WorkspaceChoiceMinWidth => Get<double>("WorkspaceChoiceMinWidth");
    public static double InlineProgressSize => Get<double>("InlineProgressSize");

    public static double FleetChoiceListHeight => Get<double>("FleetChoiceListHeight");
    public static double ImportMappingMaxHeight => Get<double>("ImportMappingMaxHeight");
    public static double DialogListMaxHeight => Get<double>("DialogListMaxHeight");
    public static double BatchPreviewMaxHeight => Get<double>("BatchPreviewMaxHeight");
    public static double FleetPreviewMaxHeight => Get<double>("FleetPreviewMaxHeight");
    public static double ReviewDetailMaxHeight => Get<double>("ReviewDetailMaxHeight");
    public static double TaskDetailMaxHeight => Get<double>("TaskDetailMaxHeight");
}
