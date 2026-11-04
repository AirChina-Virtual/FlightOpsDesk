using System.Text.Json.Nodes;
using Microsoft.Win32;
using VamSys.TestSupport;
using VamSys.UiTests.Driver;
using static VamSys.UiTests.Driver.AppWindow;

namespace VamSys.UiTests;

/// <summary>
/// Baseline measurements that DataPerformanceTests does not take: process startup, workspace load and switch, the review
/// page with growing change sets, per-frame scrolling and memory. Measures only, with no limits, and writes baseline.json
/// to the output directory; takes minutes, so an ordinary window test run leaves it out (Category=Measurement, see the
/// README). VAMSYS_BASELINE_LAUNCHES sets the number of startups measured (5 by default). Replaces scripts/test-baseline-ui.ps1.
/// </summary>
public class BaselineMeasurements
{
    // The review of every row and the save after it take far longer than an ordinary command.
    static readonly TimeSpan Slow = TimeSpan.FromMinutes(3);

    static int Launches => int.TryParse(Environment.GetEnvironmentVariable("VAMSYS_BASELINE_LAUNCHES"), out var launches) ? launches : 5;

    static string? Cpu() => Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", null) as string;

    [WindowTheory]
    [Trait("Category", "Measurement")]
    [InlineData(20000)]
    [InlineData(50000)]
    public void Measure_the_baseline(int rows)
    {
        using var first = Start($"baseline-{rows}", d => DataUiFixture.Seed(d, rows));
        var startups = new JsonArray();
        var loads = new JsonArray();
        void MeasureLaunch(AppWindow window, int n)
        {
            var startup = window.Query("startup", Slow);
            var memoryAtStart = window.Query("memory", Slow);
            loads.Add(window.Query("workspace-load", Slow));
            startups.Add(new JsonObject { ["launch"] = n, ["startup"] = startup, ["memory"] = memoryAtStart });
            window.Close(Slow);
        }

        MeasureLaunch(first, 0);
        for (var n = 1; n < Launches; n++)
        {
            using var window = first.Reopen();
            MeasureLaunch(window, n);
        }

        using var app = first.Reopen();
        var memory = new JsonObject { ["start"] = app.Query("memory", Slow) };
        var opened = app.Query("data-open", Slow);
        memory["dataPage"] = app.Query("memory", Slow);
        var scroll200 = app.Query("scroll-bench:120", Slow);
        app.Query("data-size:500", Slow);
        var scroll500 = app.Query("scroll-bench:120", Slow);
        app.Query("data-size:200", Slow);
        memory["afterScroll"] = app.Query("memory", Slow);
        var switches = new JsonArray();
        for (var n = 0; n < 5; n++)
            switches.Add(app.Query("workspace-switch", Slow));
        memory["afterSwitch"] = app.Query("memory", Slow);
        var reviews = new JsonArray();
        foreach (var count in new[] { 1000, 10000, rows })
            reviews.Add(app.Query($"review-bench:{count}", Slow));
        memory["review"] = app.Query("memory", Slow);
        app.Close(Slow);

        File.WriteAllText(Path.Combine(app.OutputDirectory, "baseline.json"), new JsonObject
        {
            ["rows"] = rows,
            ["machine"] = new JsonObject { ["cpu"] = Cpu(), ["os"] = Environment.OSVersion.VersionString, ["logicalProcessors"] = Environment.ProcessorCount },
            ["startup"] = startups, ["workspaceLoad"] = loads, ["dataOpen"] = opened,
            ["scroll"] = new JsonObject { ["page200"] = scroll200, ["page500"] = scroll500 },
            ["workspaceSwitch"] = switches, ["review"] = reviews, ["memory"] = memory, ["dataDirectory"] = app.DataDirectory
        }.ToJsonString());
    }
}
