using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using System.Runtime.CompilerServices;
using VamSys.Core;

namespace VamSys.App;

// Registrations live with their targets; language events keep only weak references.
// Detached pages and virtualized rows can be collected without unsubscribing each control.
internal static class UiLocalization
{
    sealed class Binding(object target, string property, Func<object?> value)
    {
        public void Refresh()
        {
            var member = target.GetType().GetProperty(property) ?? throw new InvalidOperationException(property);
            var index = target is ComboBox combo && property == "ItemsSource" ? combo.SelectedIndex : -1;
            member.SetValue(target, value());
            if (target is ComboBox picker && property == "ItemsSource" && index >= 0) picker.SelectedIndex = index;
        }
    }
    static readonly ConditionalWeakTable<object, Dictionary<string, Binding>> Targets = new();
    static readonly DependencyProperty BindingsProperty = DependencyProperty.RegisterAttached("LocalizationBindings", typeof(object), typeof(UiLocalization), new PropertyMetadata(null));
    static readonly ConditionalWeakTable<ILocalizationService, List<WeakReference<Binding>>> Services = new();
    public static T Localize<T>(this T target, ILocalizationService service, string property, Func<object?> value) where T : class
    {
        var registrations = Services.GetValue(service, s =>
        {
            var list = new List<WeakReference<Binding>>();
            s.LanguageChanged += (_, _) => { list.RemoveAll(w => !w.TryGetTarget(out _)); foreach (var w in list.ToArray()) if (w.TryGetTarget(out var b)) b.Refresh(); };
            return list;
        });
        var binding = new Binding(target, property, value);
        // WinUI can release a managed wrapper while retaining the native visual.
        // Keep registrations on the native dependency object, not only in a CWT.
        if (target is DependencyObject dependency)
        {
            var map = dependency.GetValue(BindingsProperty) as Dictionary<string, Binding>;
            if (map is null) { map = []; dependency.SetValue(BindingsProperty, map); }
            map[property] = binding;
        }
        else Targets.GetOrCreateValue(target)[property] = binding;
        registrations.Add(new(binding));
        if (registrations.Count % 256 == 0) registrations.RemoveAll(w => !w.TryGetTarget(out _));
        binding.Refresh(); return target;
    }
}
