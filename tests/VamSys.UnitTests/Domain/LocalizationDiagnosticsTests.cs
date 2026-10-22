using System.Collections.Concurrent;
using System.Diagnostics;
using VamSys.Core;
using VamSys.Infrastructure;

namespace VamSys.UnitTests.Domain;

public class LocalizationDiagnosticsTests
{
    [Fact]
    public void Host_logs_each_missing_resource_once_after_the_instance_notification()
    {
        const string key = "R2DiagnosticsMissingKey";
        var events = new ConcurrentQueue<string>();
        using var listener = new MissingResourceListener(key, events);
        Trace.Listeners.Add(listener);
        try
        {
            LocalizationDiagnostics.EnableTrace();
            LocalizationDiagnostics.EnableTrace();
            var language = new LocalizationService();
            language.SetLanguage("en-US");
            language.MissingResource += missing => { if (missing == key) events.Enqueue("instance"); };
            Assert.Equal("[R2DiagnosticsMissingKey]", language.Get(key));
            Assert.Equal(["instance", "Missing localization resource: en-US/R2DiagnosticsMissingKey"], events.ToArray());
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }
    }

    sealed class MissingResourceListener(string key, ConcurrentQueue<string> events) : TraceListener
    {
        public override void Write(string? message) { }
        public override void WriteLine(string? message) { }
        public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, string? format, params object?[]? args)
        {
            if (args is [_, string missing] && missing == key)
                events.Enqueue(string.Format(System.Globalization.CultureInfo.InvariantCulture, format!, args));
        }
    }
}
