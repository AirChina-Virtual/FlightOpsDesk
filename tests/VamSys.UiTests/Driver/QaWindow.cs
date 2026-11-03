using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Windows.Automation;
using VamSys.TestSupport;

namespace VamSys.UiTests.Driver;

/// <summary>
/// One run of the QA package against its own seeded data directory: the window reached through UI Automation, as a user
/// or screen reader reaches it, and the QA pipe (tests/UiVerification) for state that is not on screen. Disposing it kills
/// the window if a test left it open.
/// </summary>
public sealed partial class QaWindow : IDisposable
{
    public static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(15);
    static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(60);

    readonly Process process;
    readonly NamedPipeClientStream pipe;
    StreamReader? reader;
    StreamWriter? writer;

    /// <summary>The isolated data directory the window runs on (under artifacts/ui-tests).</summary>
    public string DataDirectory { get; }

    /// <summary>The test's output directory, for anything it saves next to the data directory.</summary>
    public string OutputDirectory { get; }

    /// <summary>Describes the window's state in failure messages, for example the data table state.</summary>
    public Func<string>? Context { get; set; }

    QaWindow(Process process, string pipeName, string dataDirectory, string outputDirectory)
    {
        this.process = process;
        pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
        DataDirectory = dataDirectory;
        OutputDirectory = outputDirectory;
    }

    /// <summary>Seeds a new data directory under artifacts/ui-tests/<paramref name="name"/> and starts the QA package on it.</summary>
    public static QaWindow Start(string name, Action<string> seed)
    {
        if (Process.GetProcessesByName("FlightOpsDesk").Length > 0)
            throw new InvalidOperationException("An existing FlightOps Desk window is open; isolate QA before running.");
        var executable = Repo.QaExecutable;
        if (!File.Exists(executable))
            throw new FileNotFoundException("Build the QA package first: scripts/build.ps1 -Publish -UiVerification", executable);
        var output = Repo.Artifacts("ui-tests", name);
        var directory = Path.Combine(output, "data-" + Guid.NewGuid().ToString("N"));
        DataUiFixture.RequireEmptyDirectory(directory);
        seed(directory);

        var pipeName = "flightops-ui-" + Guid.NewGuid().ToString("N");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)! };
        start.Environment["VAMSYS_DATA_DIR"] = directory;
        start.Environment["VAMSYS_QA_PIPE"] = pipeName;
        var window = new QaWindow(Process.Start(start)!, pipeName, directory, output);
        try
        {
            window.pipe.Connect((int)QueryTimeout.TotalMilliseconds);
            window.reader = new StreamReader(window.pipe);
            window.writer = new StreamWriter(window.pipe) { AutoFlush = true };
        }
        catch
        {
            window.Dispose();
            throw;
        }
        return window;
    }

    // ---- the QA pipe ----------------------------------------------------------------------------------------------

    /// <summary>Sends one QA command and returns its answer; an answer with an error fails the test.</summary>
    public JsonNode Query(string command)
    {
        writer!.WriteLine(command);
        var read = reader!.ReadLineAsync();
        if (!read.Wait(QueryTimeout))
            throw new TimeoutException($"Timed out: {command}");
        var line = read.Result ?? throw new InvalidOperationException($"The QA pipe closed during: {command}");
        var answer = JsonNode.Parse(line)!;
        if (answer is JsonObject o && o["error"] is { } error)
            throw new InvalidOperationException($"{command}: {error}");
        return answer;
    }

    /// <summary>Asks the window to close as the title bar's close button does and waits for the process to end.</summary>
    public void Close()
    {
        Query("close");
        if (!process.WaitForExit(30000))
            throw new TimeoutException("Close did not finish.");
    }

    // ---- waiting ----------------------------------------------------------------------------------------------------

    /// <summary>Polls until <paramref name="until"/> returns a value, then returns it.</summary>
    public T Wait<T>(Func<T?> until, string what, TimeSpan? limit = null) where T : class
    {
        var end = DateTime.UtcNow + (limit ?? DefaultWait);
        while (true)
        {
            if (until() is { } value)
                return value;
            if (DateTime.UtcNow >= end)
                throw new TimeoutException($"Timeout: {what}{Describe()}");
            Thread.Sleep(100);
        }
    }

    /// <summary>Polls until <paramref name="until"/> holds.</summary>
    public void Until(Func<bool> until, string what, TimeSpan? limit = null) =>
        Wait(() => until() ? this : null, what, limit);

    string Describe()
    {
        if (Context is null)
            return "";
        try { return " | state: " + Context(); }
        catch (Exception e) { return " | state unavailable: " + e.Message; }
    }

    // ---- UI Automation ----------------------------------------------------------------------------------------------

    public IntPtr Handle
    {
        get
        {
            process.Refresh();
            return process.MainWindowHandle;
        }
    }

    public AutomationElement Root => AutomationElement.FromHandle(Handle);

    /// <summary>Every element with this AutomationId, popups included.</summary>
    public AutomationElement[] Find(string automationId) =>
        FindAll(new PropertyCondition(AutomationElement.AutomationIdProperty, automationId));

    public AutomationElement[] FindAll(Condition condition) =>
        [.. Root.FindAll(TreeScope.Descendants, condition).Cast<AutomationElement>()];

    /// <summary>Waits for exactly one element with this AutomationId.</summary>
    public AutomationElement One(string automationId) =>
        Wait(() => Find(automationId) is [var e] ? e : null, $"one element {automationId}");

    /// <summary>Waits for exactly one element of this type with this name (for elements without an AutomationId).</summary>
    public AutomationElement Named(string name, ControlType type) =>
        Wait(() => FindAll(new AndCondition(new PropertyCondition(AutomationElement.NameProperty, name),
            new PropertyCondition(AutomationElement.ControlTypeProperty, type))) is [var e] ? e : null, $"{type.ProgrammaticName} '{name}'");

    /// <summary>Waits for the first element of this type with this name, where several may match (for example a list item
    /// that the open popup and the closed list both expose).</summary>
    public AutomationElement First(string name, ControlType type) =>
        Wait(() => Root.FindFirst(TreeScope.Descendants, new AndCondition(new PropertyCondition(AutomationElement.NameProperty, name),
            new PropertyCondition(AutomationElement.ControlTypeProperty, type))), $"first {type.ProgrammaticName} '{name}'");

    public bool Enabled(string automationId) => One(automationId).Current.IsEnabled;

    public string Name(string automationId) => One(automationId).Current.Name;

    // ---- keyboard -----------------------------------------------------------------------------------------------------

    /// <summary>Brings the window to the front; keyboard input fails the test rather than going to another window.</summary>
    public void Foreground()
    {
        SetForegroundWindow(Handle);
        Thread.Sleep(300);
        if (GetForegroundWindow() != Handle)
            throw new InvalidOperationException("QA window is not foreground; keep it active before retrying keyboard input.");
    }

    /// <summary>Presses and releases a key (a virtual-key code such as <see cref="Keys.Enter"/>).</summary>
    public static void Press(byte key)
    {
        keybd_event(key, 0, 0, 0);
        keybd_event(key, 0, KeyUp, 0);
        Thread.Sleep(50);
    }

    public static class Keys
    {
        public const byte Enter = 0x0D, Escape = 0x1B, Tab = 0x09;
    }

    const uint KeyUp = 0x0002;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr window);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial void keybd_event(byte key, byte scan, uint flags, nuint extra);

    public void Dispose()
    {
        writer?.Dispose();
        reader?.Dispose();
        pipe.Dispose();
        if (!process.HasExited)
        {
            process.Kill();
            process.WaitForExit();
        }
        process.Dispose();
    }
}
