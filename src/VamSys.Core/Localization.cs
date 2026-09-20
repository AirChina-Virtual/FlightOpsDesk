using System.Globalization;
using System.Resources;
using System.Text.RegularExpressions;

namespace VamSys.Core;

public sealed record MessageArgument
{
    public string? Text { get; init; }
    public decimal? Number { get; init; }
    public DateTimeOffset? Date { get; init; }
    public MessageDescriptor? Message { get; init; }
    public static MessageArgument From(object? value) => value switch
    {
        MessageDescriptor m => new() { Message = m },
        DateTimeOffset d => new() { Date = d },
        DateTime d => new() { Date = new DateTimeOffset(d) },
        byte or short or int or long or float or double or decimal => new() { Number = Convert.ToDecimal(value, CultureInfo.InvariantCulture) },
        _ => new() { Text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "" }
    };
}
public sealed record MessageDescriptor
{
    public string? Code { get; init; }
    public Dictionary<string, MessageArgument> Arguments { get; init; } = [];
    public string? Literal { get; init; }
    public override string ToString() => Messages.Chinese.Format(this);
    public static implicit operator string(MessageDescriptor message) => message.ToString();
    public static implicit operator MessageDescriptor(string text) => new() { Literal = text };
}
public interface ILocalizationService
{
    string Language { get; }
    CultureInfo DisplayCulture { get; }
    event EventHandler? LanguageChanged;
    string Get(string key, params (string Name, object? Value)[] arguments);
    string Format(MessageDescriptor message);
    void SetLanguage(string language);
}
public sealed class LocalizationService : ILocalizationService
{
    public static readonly ResourceManager Resources = new("VamSys.Core.Resources.Strings", typeof(LocalizationService).Assembly);
    public string Language { get; private set; } = "zh-CN";
    public CultureInfo DisplayCulture => CultureInfo.GetCultureInfo(Language);
    public event EventHandler? LanguageChanged;
    public event Action<string>? MissingResource;
    public void SetLanguage(string language)
    {
        var target = language == "en-US" ? language : "zh-CN";
        if (target == Language) return;
        Language = target; LanguageChanged?.Invoke(this, EventArgs.Empty);
    }
    public string Get(string key, params (string Name, object? Value)[] arguments) => Format(Messages.Define(key, arguments));
    public string Format(MessageDescriptor message)
    {
        if (message.Code is null) return message.Literal ?? "";
        var resourceCulture = Language == "en-US" ? DisplayCulture : CultureInfo.InvariantCulture;
        var template = Resources.GetResourceSet(resourceCulture, true, false)?.GetString(message.Code);
        if (template is null)
        {
            MissingResource?.Invoke(message.Code);
            System.Diagnostics.Trace.TraceWarning("Missing localization resource: {0}/{1}", Language, message.Code);
            template = Resources.GetString(message.Code, CultureInfo.InvariantCulture) ?? message.Literal ?? $"[{message.Code}]";
        }
        // Single pass: parameter values (including user data with braces) are never reinterpreted.
        return Regex.Replace(template, @"\{(?<name>\w+)(?::(?<format>[^}]+))?\}", match =>
        {
            if (!message.Arguments.TryGetValue(match.Groups["name"].Value, out var arg)) return match.Value;
            var format = match.Groups["format"].Success ? match.Groups["format"].Value : null;
            if (arg.Message is not null) return Format(arg.Message);
            if (arg.Number is decimal n) return n.ToString(format, DisplayCulture);
            if (arg.Date is DateTimeOffset d) return d.ToString(format, DisplayCulture);
            return arg.Text ?? "";
        });
    }
    public string Legacy(string value) => Format(Messages.FromLegacy(value));
}
public static class Messages
{
    internal static readonly LocalizationService Chinese = new();
    public static MessageDescriptor Define(string code, params (string Name, object? Value)[] args) => new() { Code = code, Arguments = args.ToDictionary(a => a.Name, a => MessageArgument.From(a.Value)) };
    static readonly Lazy<Dictionary<string, string>> LegacyKeys = new(() => LocalizationService.Resources.GetResourceSet(CultureInfo.InvariantCulture, true, true)!
        .Cast<System.Collections.DictionaryEntry>().Where(p => !((string)p.Value!).Contains('{')).GroupBy(p => (string)p.Value!).ToDictionary(g => g.Key, g => (string)g.First().Key));
    public static MessageDescriptor FromLegacy(string value) => LegacyKeys.Value.TryGetValue(value, out var key) ? Define(key) : new() { Literal = value };
}
public static class MessageErrors
{
    public static T Attach<T>(T error, MessageDescriptor message) where T : Exception { error.Data[nameof(MessageDescriptor)] = message; return error; }
    public static MessageDescriptor Describe(Exception error) => error.Data[nameof(MessageDescriptor)] as MessageDescriptor ?? Messages.FromLegacy(error.Message);
}
public enum JobStatus { Pending, Running, Canceled, Paused, Completed, NeedsAttention, AwaitingImport }
