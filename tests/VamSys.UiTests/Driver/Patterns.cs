using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows.Automation;

namespace VamSys.UiTests.Driver;

/// <summary>The UI Automation patterns the tests use, as a user or screen reader operates the controls.</summary>
public static class Patterns
{
    public static void Invoke(this AutomationElement element) =>
        ((InvokePattern)element.GetCurrentPattern(InvokePattern.Pattern)).Invoke();

    public static void SetText(this AutomationElement element, string text) =>
        ((ValuePattern)element.GetCurrentPattern(ValuePattern.Pattern)).SetValue(text);

    public static string Text(this AutomationElement element) =>
        ((ValuePattern)element.GetCurrentPattern(ValuePattern.Pattern)).Current.Value;

    public static bool IsReadOnly(this AutomationElement element) =>
        ((ValuePattern)element.GetCurrentPattern(ValuePattern.Pattern)).Current.IsReadOnly;

    public static void Expand(this AutomationElement element) =>
        ((ExpandCollapsePattern)element.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Expand();

    /// <summary>Collapses the element unless it already is.</summary>
    public static void Collapse(this AutomationElement element)
    {
        var pattern = (ExpandCollapsePattern)element.GetCurrentPattern(ExpandCollapsePattern.Pattern);
        if (pattern.Current.ExpandCollapseState != ExpandCollapseState.Collapsed)
            pattern.Collapse();
    }

    public static void Select(this AutomationElement element) =>
        ((SelectionItemPattern)element.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();

    public static bool IsSelected(this AutomationElement element) =>
        ((SelectionItemPattern)element.GetCurrentPattern(SelectionItemPattern.Pattern)).Current.IsSelected;

    public static void Toggle(this AutomationElement element) =>
        ((TogglePattern)element.GetCurrentPattern(TogglePattern.Pattern)).Toggle();

    public static ToggleState ToggleState(this AutomationElement element) =>
        ((TogglePattern)element.GetCurrentPattern(TogglePattern.Pattern)).Current.ToggleState;

    public static ScrollPattern Scroll(this AutomationElement element) =>
        (ScrollPattern)element.GetCurrentPattern(ScrollPattern.Pattern);

    // ---- QA pipe answers ----------------------------------------------------------------------------------------------

    public static int Int(this JsonNode node, string key) => int.Parse(node[key]!.ToString(), CultureInfo.InvariantCulture);

    public static double Number(this JsonNode node, string key) => double.Parse(node[key]!.ToString(), CultureInfo.InvariantCulture);

    public static string? Str(this JsonNode node, string key) => node[key]?.ToString();

    public static bool Bool(this JsonNode node, string key) => node[key]?.GetValue<bool>() ?? false;
}
