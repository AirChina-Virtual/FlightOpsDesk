using System.Diagnostics;
using VamSys.Core;

namespace VamSys.Infrastructure;

public static class LocalizationDiagnostics
{
    static readonly object Sync = new();
    static bool enabled;

    public static void EnableTrace()
    {
        lock (Sync)
        {
            if (enabled)
                return;
            LocalizationService.ResourceMissing += TraceMissingResource;
            enabled = true;
        }
    }

    static void TraceMissingResource(string language, string key)
        => Trace.TraceWarning("Missing localization resource: {0}/{1}", language, key);
}
