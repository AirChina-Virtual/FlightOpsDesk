using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Windows.Automation;
using VamSys.TestSupport;

namespace VamSys.UiTests.Driver;

/// <summary>Which build a window test runs: the QA package (with the QA pipe) or the product package (window only).</summary>
public enum Package { Qa, Product }

/// <summary>
/// One running window of the program on its own seeded data directory, reached through UI Automation, as a user or screen
/// reader reaches it, and in the QA package also through the QA pipe (tests/UiVerification) for state that is not on
/// screen. Disposing it kills the window if a test left it open.
/// </summary>
public sealed partial class AppWindow : IDisposable
{
    public static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(20);
    static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(60);
    static readonly TimeSpan PageLayoutWait = TimeSpan.FromSeconds(2);
    static readonly string[] DialogButtonIds = ["PrimaryButton", "SecondaryButton", "CloseButton"];

    readonly Process process;
    readonly Package package;
    readonly NamedPipeClientStream? pipe;
    StreamReader? reader;
    StreamWriter? writer;

    /// <summary>The isolated data directory the window runs on (under artifacts/ui-tests).</summary>
    public string DataDirectory { get; }

    /// <summary>The test's directory above the data directory, for screenshots and summaries (git-ignored).</summary>
    public string OutputDirectory => Path.GetDirectoryName(DataDirectory)!;

    /// <summary>Describes the window's state in failure messages, for example the data table state.</summary>
    public Func<string>? Context { get; set; }

    // UI Automation bounding rectangles and the cursor must both be in physical pixels, including at 150% scaling.
    static AppWindow()
    {
        if (!IsProcessDPIAware() && !SetProcessDPIAware())
            throw new InvalidOperationException("Cannot enable DPI awareness for physical mouse coordinates.");
    }

    AppWindow(Process process, Package package, string? pipeName, string dataDirectory)
    {
        this.process = process;
        this.package = package;
        if (pipeName is not null)
            pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
        DataDirectory = dataDirectory;
    }

    /// <summary>
    /// Creates a data directory under artifacts/ui-tests/<paramref name="name"/>, seeds it (without a seed the program starts
    /// on a new directory) and starts the package on it. Fails if any window of the program is already open.
    /// </summary>
    public static AppWindow Start(string name, Action<string>? seed = null, Package package = Package.Qa)
    {
        // A process that just exited stays listed while something (UI Automation) still holds a handle to it, so the previous
        // test's window gets a moment to go away; a window that is really open fails.
        if (Poll(() => OpenWindows() == 0 ? "none" : null, TimeSpan.FromSeconds(5)) is null)
            throw new InvalidOperationException("An existing FlightOps Desk window is open; isolate QA before running.");
        var directory = Path.Combine(Repo.Artifacts("ui-tests", name), "data-" + Guid.NewGuid().ToString("N"));
        DataUiFixture.RequireEmptyDirectory(directory);
        seed?.Invoke(directory);
        return Open(directory, package);
    }

    static int OpenWindows()
    {
        var processes = Process.GetProcessesByName("FlightOpsDesk");
        var open = processes.Count(p => !p.HasExited);
        foreach (var process in processes)
            process.Dispose();
        return open;
    }

    /// <summary>Starts the same package on the same data directory: a restart once this window is closed, otherwise a second instance.</summary>
    public AppWindow Reopen() => Open(DataDirectory, package);

    static AppWindow Open(string directory, Package package)
    {
        var executable = package == Package.Qa ? Repo.QaExecutable : Repo.ProductExecutable;
        if (!File.Exists(executable))
            throw new FileNotFoundException("Build the packages first: scripts/build.ps1 -Publish, and with -UiVerification for the QA package.", executable);
        var pipeName = package == Package.Qa ? "flightops-ui-" + Guid.NewGuid().ToString("N") : null;
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)! };
        start.Environment["VAMSYS_DATA_DIR"] = directory;
        start.Environment.Remove("VAMSYS_QA_PIPE");
        if (pipeName is not null)
            start.Environment["VAMSYS_QA_PIPE"] = pipeName;
        var window = new AppWindow(Process.Start(start)!, package, pipeName, directory);
        try
        {
            if (window.pipe is { } pipe)
            {
                pipe.Connect((int)QueryTimeout.TotalMilliseconds);
                window.reader = new StreamReader(pipe);
                window.writer = new StreamWriter(pipe) { AutoFlush = true };
            }
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
    public JsonNode Query(string command, TimeSpan? timeout = null)
    {
        if (writer is null)
            throw new InvalidOperationException("The product package has no QA pipe.");
        writer.WriteLine(command);
        var read = reader!.ReadLineAsync();
        if (!read.Wait(timeout ?? QueryTimeout))
            throw new TimeoutException($"Timed out: {command}");
        var line = read.Result ?? throw new InvalidOperationException($"The QA pipe closed during: {command}");
        var answer = JsonNode.Parse(line)!;
        if (answer is JsonObject o && o["error"] is { } error)
            throw new InvalidOperationException($"{command}: {error}");
        return answer;
    }

    /// <summary>Closes the window as the title bar's close button does and waits for the process to end.</summary>
    public void Close()
    {
        if (writer is not null)
        {
            Query("close");
            if (!process.WaitForExit(30000))
                throw new TimeoutException("Close did not finish.");
            return;
        }
        // A dialog's task can complete after its button's Invoke returns; the program rightly refuses to close until then.
        var end = DateTime.UtcNow.AddSeconds(10);
        do
        {
            if (process.HasExited)
                return;
            process.CloseMainWindow();
            if (process.WaitForExit(200))
                return;
        } while (DateTime.UtcNow < end);
        throw new TimeoutException("The window did not close.");
    }

    /// <summary>Ends the process without letting the program save or close, as a crash or power loss would.</summary>
    public void Kill()
    {
        process.Kill();
        process.WaitForExit();
    }

    /// <summary>Waits for the process to end after a close the test has already asked for.</summary>
    public void WaitForExit()
    {
        if (!process.WaitForExit(30000))
            throw new TimeoutException("Close did not finish.");
    }

    // ---- waiting ----------------------------------------------------------------------------------------------------

    /// <summary>Polls until <paramref name="until"/> returns a value, then returns it. An element that goes away during a poll is retried.</summary>
    public T Wait<T>(Func<T?> until, string what, TimeSpan? limit = null) where T : class =>
        Poll(until, limit ?? DefaultWait) ?? throw new TimeoutException($"Timeout: {what}{Describe()}");

    static T? Poll<T>(Func<T?> until, TimeSpan limit) where T : class
    {
        var end = DateTime.UtcNow + limit;
        while (true)
        {
            try
            {
                if (until() is { } value)
                    return value;
            }
            catch (Exception e) when (e is ElementNotAvailableException or COMException) { }
            if (DateTime.UtcNow >= end)
                return null;
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

    /// <summary>Every element in the window that meets the condition, popups included; none while the window is not up yet.</summary>
    public AutomationElement[] FindAll(Condition condition) =>
        Handle is var handle && handle == IntPtr.Zero ? [] : Below(AutomationElement.FromHandle(handle), condition);

    /// <summary>Every element under <paramref name="parent"/> that meets the condition.</summary>
    public static AutomationElement[] Below(AutomationElement parent, Condition condition) =>
        [.. parent.FindAll(TreeScope.Descendants, condition).Cast<AutomationElement>()];

    /// <summary>The window's title.</summary>
    public string Title => AutomationElement.FromHandle(Handle).Current.Name;

    /// <summary>Every list item whose name starts with this text (data rows and task cards are named after their view model).</summary>
    public AutomationElement[] ListItemsNamed(string start) => ListItems(start, FindAll);

    /// <summary>Every list item under <paramref name="parent"/>.</summary>
    public static AutomationElement[] ListItemsBelow(AutomationElement parent) => ListItems("", c => Below(parent, c));

    static AutomationElement[] ListItems(string start, Func<Condition, AutomationElement[]> find) =>
        [.. find(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem)).Where(e => e.Current.Name.StartsWith(start, StringComparison.Ordinal))];

    /// <summary>Every element with this AutomationId.</summary>
    public AutomationElement[] Find(string automationId) =>
        FindAll(new PropertyCondition(AutomationElement.AutomationIdProperty, automationId));

    /// <summary>Every element of this type with this name.</summary>
    public AutomationElement[] FindNamed(string name, ControlType type) =>
        FindAll(new AndCondition(new PropertyCondition(AutomationElement.NameProperty, name), new PropertyCondition(AutomationElement.ControlTypeProperty, type)));

    /// <summary>Waits for exactly one element with this AutomationId.</summary>
    public AutomationElement One(string automationId) =>
        Wait(() => Find(automationId) is [var e] ? e : null, $"one element {automationId}");

    /// <summary>Waits for exactly one element of this type with this name (for elements without an AutomationId).</summary>
    public AutomationElement Named(string name, ControlType type) =>
        Wait(() => FindNamed(name, type) is [var e] ? e : null, $"{type.ProgrammaticName} '{name}'");

    /// <summary>Waits for the first element of this type with this name, where several may match (for example a list item
    /// that the open popup and the closed list both expose).</summary>
    public AutomationElement First(string name, ControlType type) =>
        Wait(() => FindNamed(name, type).FirstOrDefault(), $"first {type.ProgrammaticName} '{name}'");

    public bool Enabled(string automationId) => One(automationId).Current.IsEnabled;

    public string Name(string automationId) => One(automationId).Current.Name;

    /// <summary>The element of this type with this name is on screen now.</summary>
    public bool Shown(string name, ControlType type) => FindNamed(name, type).Length > 0;

    /// <summary>Some element's name contains this text now.</summary>
    public bool HasText(string text) =>
        FindAll(Condition.TrueCondition).Any(e => e.Current.Name.Contains(text, StringComparison.Ordinal));

    /// <summary>Waits until some element's name contains this text, allowing for a slow startup.</summary>
    public void WaitText(string text) => Until(() => HasText(text), $"text '{text}'");

    // A dialog button wins over a page button of the same name behind the dialog.
    AutomationElement? ButtonOrNull(string name) =>
        FindNamed(name, ControlType.Button) is var buttons && buttons.FirstOrDefault(b => DialogButtonIds.Contains(b.Current.AutomationId)) is { } dialog ? dialog : buttons.FirstOrDefault();

    /// <summary>
    /// Waits for a button by name, opening the command bar's overflow if it is not on screen. A page selected a moment ago
    /// may not be laid out yet, and opening the overflow of a page still being replaced fails, so the button gets time first.
    /// </summary>
    public AutomationElement Button(string name)
    {
        if (Poll(() => ButtonOrNull(name), PageLayoutWait) is { } button)
            return button;
        ClickMore();
        return Wait(() => ButtonOrNull(name), $"button '{name}'");
    }

    public void Click(string name) => Button(name).Invoke();

    /// <summary>Opens the command bar's overflow.</summary>
    public void ClickMore() => Wait(() => Find("MoreButton").FirstOrDefault(), "the overflow button").Invoke();

    /// <summary>Invokes the first element with this name that can be invoked, whatever its control type (overflow commands).</summary>
    public void InvokeNamed(string name) =>
        Wait(() => FindAll(new PropertyCondition(AutomationElement.NameProperty, name)).FirstOrDefault(e => e.GetSupportedPatterns().Contains(InvokePattern.Pattern)), $"invokable '{name}'").Invoke();

    public void SelectItem(string name) => First(name, ControlType.ListItem).Select();

    public void Edit(string name, string value) => First(name, ControlType.Edit).SetText(value);

    public string Value(string name) => First(name, ControlType.Edit).Text();

    /// <summary>Picks a language on the settings page. The brand name in the title never changes with it.</summary>
    public void Language(string name)
    {
        One("LanguagePicker").Expand();
        SelectItem(name);
        Assert.Equal("FlightOps Desk", Title);
    }

    // ---- screenshots ------------------------------------------------------------------------------------------------

    /// <summary>Saves what the window shows, dialogs included, as a PNG in the test's output directory for a person to look at.</summary>
    public void Screenshot(string fileName)
    {
        Thread.Sleep(400);
        if (!GetWindowRect(Handle, out var bounds))
            throw new InvalidOperationException("Cannot read the window's bounds.");
        using var bitmap = new Bitmap(bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            var dc = graphics.GetHdc();
            try
            {
                if (!PrintWindow(Handle, dc, PrintFullContent))
                    throw new InvalidOperationException("Window capture failed.");
            }
            finally { graphics.ReleaseHdc(dc); }
        }
        bitmap.Save(Path.Combine(OutputDirectory, fileName), ImageFormat.Png);
    }

    // ---- keyboard and mouse -----------------------------------------------------------------------------------------

    /// <summary>Brings the window to the front; real input fails the test rather than going to another window.</summary>
    public void Foreground()
    {
        SetForegroundWindow(Handle);
        Thread.Sleep(300);
        if (GetForegroundWindow() != Handle)
            throw new InvalidOperationException("Window is not foreground; keep it active before retrying real input.");
    }

    /// <summary>Clicks the left mouse button at the centre of a rectangle given in physical pixels.</summary>
    public static void ClickAt(System.Windows.Rect target)
    {
        SetCursorPos((int)(target.X + target.Width / 2), (int)(target.Y + target.Height / 2));
        mouse_event(MouseLeftDown, 0, 0, 0, 0);
        mouse_event(MouseLeftUp, 0, 0, 0, 0);
    }

    /// <summary>Presses and releases a key (a virtual-key code such as <see cref="Keys.Enter"/>).</summary>
    public static void Press(byte key)
    {
        keybd_event(key, 0, 0, 0);
        keybd_event(key, 0, KeyUp, 0);
        Thread.Sleep(50);
    }

    /// <summary>Types digits. Digits only: an active Chinese IME composes letters and converts punctuation.</summary>
    public static void TypeDigits(string digits)
    {
        foreach (var digit in digits)
            Press(char.IsAsciiDigit(digit) ? (byte)digit : throw new ArgumentException("Digits only.", nameof(digits)));
    }

    public static class Keys
    {
        public const byte Enter = 0x0D, Escape = 0x1B, Tab = 0x09;
    }

    const uint KeyUp = 0x0002, MouseLeftDown = 0x0002, MouseLeftUp = 0x0004, PrintFullContent = 2;

    [StructLayout(LayoutKind.Sequential)]
    struct WindowBounds { public int Left, Top, Right, Bottom; }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(IntPtr window, out WindowBounds bounds);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PrintWindow(IntPtr window, IntPtr dc, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsProcessDPIAware();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetProcessDPIAware();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr window);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetCursorPos(int x, int y);

    [LibraryImport("user32.dll")]
    private static partial void mouse_event(uint flags, uint dx, uint dy, uint data, nuint extra);

    [LibraryImport("user32.dll")]
    private static partial void keybd_event(byte key, byte scan, uint flags, nuint extra);

    public void Dispose()
    {
        writer?.Dispose();
        reader?.Dispose();
        pipe?.Dispose();
        if (!process.HasExited)
        {
            process.Kill();
            process.WaitForExit();
        }
        process.Dispose();
    }
}
