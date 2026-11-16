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
    public static AppWindow Start(string name, Action<string>? seed = null, Package package = Package.Qa) => Start(name, seed, package, null);

    /// <summary>
    /// As <see cref="Start(string, Action{string}?, Package)"/>, with the QA package pointed at a mock Operations server on
    /// this machine (VAMSYS_QA_OPERATIONS_ORIGIN, read only by the QA build): the dialogs that need a connection can open.
    /// </summary>
    /// <remarks>The seed returns the fake server; it is served on the loopback address for as long as <paramref name="server"/> lives.</remarks>
    public static AppWindow StartConnected(string name, Func<string, FakeOperationsServer> seed, out LoopbackOperationsServer server)
    {
        LoopbackOperationsServer? served = null;
        try
        {
            var app = Start(name, directory => served = new LoopbackOperationsServer(seed(directory)), Package.Qa, () => served!.Origin);
            server = served!;
            return app;
        }
        catch
        {
            served?.Dispose();
            throw;
        }
    }

    static AppWindow Start(string name, Action<string>? seed, Package package, Func<Uri>? operations)
    {
        // A process that just exited stays listed while something (UI Automation) still holds a handle to it, so the previous
        // test's window gets a moment to go away; a window that is really open fails.
        if (Poll(() => OpenWindows() == 0 ? "none" : null, TimeSpan.FromSeconds(5)) is null)
            throw new InvalidOperationException("An existing FlightOps Desk window is open; isolate QA before running.");
        var directory = Path.Combine(Repo.Artifacts("ui-tests", name), "data-" + Guid.NewGuid().ToString("N"));
        DataUiFixture.RequireEmptyDirectory(directory);
        seed?.Invoke(directory);
        return Open(directory, package, operations: operations?.Invoke());
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
    public AppWindow Reopen() => reopenPackage is { } qa ? Open(DataDirectory, qa, defaultDirectory, pipeless: true) : Open(DataDirectory, package, defaultDirectory);

    /// <summary>
    /// Starts the QA package without VAMSYS_DATA_DIR, with its default directory redirected into the test's own folder, so
    /// the data location can be changed in the settings. <paramref name="target"/> is an empty place to move the data to.
    /// </summary>
    public static AppWindow StartWithDefaultDirectory(string name, Action<string> seed, out string defaults, out string target)
    {
        if (Poll(() => OpenWindows() == 0 ? "none" : null, TimeSpan.FromSeconds(5)) is null)
            throw new InvalidOperationException("An existing FlightOps Desk window is open; isolate QA before running.");
        var root = Path.Combine(Repo.Artifacts("ui-tests", name), "run-" + Guid.NewGuid().ToString("N"));
        defaults = Path.Combine(root, "default");
        target = Path.Combine(root, "chosen");
        Directory.CreateDirectory(target);
        DataUiFixture.RequireEmptyDirectory(defaults);
        seed(defaults);
        // The seed ran in this process: its pooled connection would hold the database the program later sets aside.
        using (var seeded = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = Path.Combine(defaults, "workspaces.db") }.ToString()))
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(seeded);
        return Open(defaults, Package.Qa, defaults);
    }

    /// <summary>Waits for this window to close after a change of data location and returns the window that replaced it.</summary>
    public AppWindow FollowRestart(string directory)
    {
        WaitForExit();
        writer?.Dispose();
        reader?.Dispose();
        pipe?.Dispose();
        writer = null;
        reader = null;
        var next = Wait(() => Process.GetProcessesByName("FlightOpsDesk").FirstOrDefault(p => p.Id != process.Id && !p.HasExited), "the restarted program");
        return new AppWindow(next, Package.Product, null, directory) { defaultDirectory = defaultDirectory, reopenPackage = package };
    }

    string? defaultDirectory;
    Package? reopenPackage;

    // Pipeless: a start that may stop at the data location problem window never opens the QA pipe.
    static AppWindow Open(string directory, Package package, string? defaults = null, bool pipeless = false, Uri? operations = null)
    {
        var executable = package == Package.Qa ? Repo.QaExecutable : Repo.ProductExecutable;
        if (!File.Exists(executable))
            throw new FileNotFoundException("Build the packages first: scripts/build.ps1 -Publish, and with -UiVerification for the QA package.", executable);
        var pipeName = package == Package.Qa && !pipeless ? "flightops-ui-" + Guid.NewGuid().ToString("N") : null;
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)! };
        if (defaults is null)
            start.Environment["VAMSYS_DATA_DIR"] = directory;
        else
        {
            start.Environment.Remove("VAMSYS_DATA_DIR");
            start.Environment["VAMSYS_QA_DEFAULT_DIR"] = defaults;
        }
        start.Environment.Remove("VAMSYS_QA_PIPE");
        start.Environment.Remove("VAMSYS_QA_OPERATIONS_ORIGIN");
        if (operations is not null)
            start.Environment["VAMSYS_QA_OPERATIONS_ORIGIN"] = operations.AbsoluteUri;
        if (pipeName is not null)
            start.Environment["VAMSYS_QA_PIPE"] = pipeName;
        var window = new AppWindow(Process.Start(start)!, package, pipeName, directory) { defaultDirectory = defaults };
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
    public void Close(TimeSpan? limit = null)
    {
        if (writer is not null)
        {
            // A dialog's task can complete after its button's Invoke returns; the program rightly refuses to close until then.
            Until(() => Query("state").Bool("idle"), "the window is idle before closing", limit);
            Query("close", limit);
            if (!process.WaitForExit(limit ?? TimeSpan.FromSeconds(30)))
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

    /// <summary>Polls until <paramref name="until"/> returns a value, or returns null after the limit.</summary>
    public static T? Poll<T>(Func<T?> until, TimeSpan limit) where T : class
    {
        var end = DateTime.UtcNow + limit;
        while (true)
        {
            try
            {
                if (until() is { } value)
                    return value;
            }
            catch (Exception e) when (e is ElementNotAvailableException or ElementNotEnabledException or COMException) { }
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

    /// <summary>The process's working set now, in bytes.</summary>
    public long WorkingSetBytes
    {
        get
        {
            process.Refresh();
            return process.WorkingSet64;
        }
    }

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

    /// <summary>Like <see cref="WaitText"/>, for the test's own data or text composed at run time: not a product string, so the AutomationId inventory leaves it out.</summary>
    public void WaitData(string text) => WaitText(text);

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
        return Wait(() => ButtonOrNull(name) ?? FromMenus(name), $"button '{name}'");
    }

    // The data page keeps a few commands in menus (more actions, refreshing one resource); another page may still have a
    // command bar's overflow.
    static readonly string[] CommandMenus = ["DataMoreMenu", "DataRefresh", "MoreButton"];

    /// <summary>Opens each command menu that is on screen and enabled in turn and returns the item with this name.</summary>
    AutomationElement? FromMenus(string name)
    {
        foreach (var id in CommandMenus)
        {
            if (Find(id).FirstOrDefault() is not { } menu || !menu.Current.IsEnabled)
                continue;
            var expand = menu.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var pattern) ? (ExpandCollapsePattern)pattern : null;
            if (expand != null)
                expand.Expand();
            else
                menu.Invoke();
            if (Poll(() => FindNamed(name, ControlType.MenuItem).FirstOrDefault() ?? ButtonOrNull(name), TimeSpan.FromSeconds(1)) is { } found)
                return found;
            if (expand?.Current.ExpandCollapseState == ExpandCollapseState.Expanded)
                expand.Collapse();
        }
        return null;
    }

    /// <summary>Shows the resource with this name (its tab's name starts with it; a pending count may follow).</summary>
    public void SelectResource(string name) =>
        Wait(() => Below(One("ResourcePicker"), Condition.TrueCondition).FirstOrDefault(e => e.Current.Name.StartsWith(name, StringComparison.Ordinal)
            && e.GetSupportedPatterns().Contains(SelectionItemPattern.Pattern)), $"resource tab '{name}'").Select();

    public void Click(string name) => Button(name).Invoke();

    /// <summary>Opens the command bar's overflow.</summary>
    public void ClickMore() => Wait(() => Find("MoreButton").FirstOrDefault(), "the overflow button").Invoke();

    /// <summary>Invokes the first element with this name that can be invoked, whatever its control type (overflow commands).</summary>
    public void InvokeNamed(string name) =>
        Wait(() => FindAll(new PropertyCondition(AutomationElement.NameProperty, name)).FirstOrDefault(e => e.GetSupportedPatterns().Contains(InvokePattern.Pattern)), $"invokable '{name}'").Invoke();

    public void SelectItem(string name) => First(name, ControlType.ListItem).Select();

    public void Edit(string name, string value) => First(name, ControlType.Edit).SetText(value);

    public string Value(string name) => First(name, ControlType.Edit).Text();

    // ---- dialogs ----------------------------------------------------------------------------------------------------

    /// <summary>Waits for the open dialog's primary button with this name (the page may have a button of the same name behind it).</summary>
    public AutomationElement DialogPrimary(string name) => DialogButton("PrimaryButton", name);

    /// <summary>Waits for the open dialog's secondary button with this name.</summary>
    public AutomationElement DialogSecondary(string name) => DialogButton("SecondaryButton", name);

    AutomationElement DialogButton(string automationId, string name) =>
        Wait(() => Find(automationId).FirstOrDefault(b => b.Current.Name == name), $"dialog button '{name}'");

    /// <summary>Cancels the open dialog with its close button and waits for it to go.</summary>
    public void DialogCancel()
    {
        Wait(() => Find("CloseButton").FirstOrDefault(), "the dialog's close button").Invoke();
        DialogClosed();
    }

    /// <summary>
    /// Clicks a button until its dialog opens, identified by its primary button. A dialog's task can complete after its button's
    /// Invoke returns, and a command can still be disabled just after a selection, so the click repeats.
    /// </summary>
    public AutomationElement ClickUntilDialog(string button, string primary) => Wait(() =>
    {
        Click(button);
        return Poll(() => Find("PrimaryButton").FirstOrDefault(b => b.Current.Name == primary), PageLayoutWait);
    }, $"'{button}' opens the dialog with '{primary}'");

    /// <summary>The data table's rows on the current page.</summary>
    public AutomationElement[] Rows() => ListItemsNamed("RowViewModel");

    /// <summary>Waits until the status bar says something starting with this text, and returns all it says.</summary>
    public string StatusStarting(string start) =>
        Wait(() => Name("StatusLabel") is var status && status.StartsWith(start, StringComparison.Ordinal) ? status : null, $"status '{start}'");

    /// <summary>Waits until no dialog is open.</summary>
    public void DialogClosed() => Until(() => Find("CloseButton").Length == 0, "the dialog closes");

    // The common file dialog's controls have these AutomationIds in every Windows language.
    const string FileNameBox = "1148", FolderNameBox = "1152", FileOpen = "1", FileCancel = "2";

    // Only a file dialog this window owns; another program's dialog on the desktop is never touched.
    AutomationElement? FilePickerOrNull()
    {
        var dialogs = new PropertyCondition(AutomationElement.ClassNameProperty, "#32770");
        return FindAll(dialogs).FirstOrDefault() ?? AutomationElement.RootElement.FindAll(TreeScope.Children, dialogs).Cast<AutomationElement>()
            .FirstOrDefault(d => GetWindow(new IntPtr(d.Current.NativeWindowHandle), OwnerWindow) == Handle);
    }

    AutomationElement FilePicker() => Wait(FilePickerOrNull, "the file picker");

    /// <summary>
    /// Clicks a button that opens the file picker. A dialog's task can complete after its button's Invoke returns, and the
    /// program rightly ignores clicks until then, so the click repeats until the picker is up.
    /// </summary>
    public void ClickUntilFilePicker(string button) => Wait(() =>
    {
        Click(button);
        return Poll(FilePickerOrNull, PageLayoutWait);
    }, "the file picker opens");

    // The picker's window appears before its controls do.
    AutomationElement PickerControl(string automationId, Func<AutomationElement, bool> usable)
    {
        var found = Poll(() => Below(FilePicker(), new PropertyCondition(AutomationElement.AutomationIdProperty, automationId)).FirstOrDefault(usable), DefaultWait);
        return found ?? throw new TimeoutException($"Timeout: file picker control {automationId}; the picker holds: {DescribePicker()}");
    }

    string DescribePicker()
    {
        var picker = FilePickerOrNull();
        return picker is null ? "(no picker)" : $"[{picker.Current.Name} {picker.Current.ClassName}] " + string.Join(", ",
            Below(picker, Condition.TrueCondition).Where(e => e.Current.AutomationId.Length is > 0 and < 6).Select(e => $"{e.Current.AutomationId}:{e.Current.ControlType.ProgrammaticName.Replace("ControlType.", "", StringComparison.Ordinal)}"));
    }

    // The open button is a plain or a split button, depending on the dialog's state; other elements (the file list's) can
    // have the same small AutomationIds.
    static bool IsDialogButton(AutomationElement element) => element.Current.ControlType == ControlType.Button || element.Current.ControlType == ControlType.SplitButton;

    /// <summary>Picks this file in the system's file picker the window opened.</summary>
    public void ChooseFile(string path)
    {
        var box = PickerControl(FileNameBox, e => e.Current.ControlType == ControlType.Edit);
        box.SetText(path);
        // Depending on the dialog's state the open control is the button itself or a pane around it.
        var open = PickerControl(FileOpen, e => e.Current.ControlType != ControlType.ListItem);
        if ((IsDialogButton(open) ? open : Below(open, Condition.TrueCondition).FirstOrDefault(IsDialogButton)) is { } button)
            button.Invoke();
        else
        {
            box.SetFocus();
            Press(Keys.Enter);
        }
    }

    /// <summary>Picks this folder in the system's folder picker the window opened; its name box has its own AutomationId.</summary>
    public void ChooseFolder(string path)
    {
        PickerControl(FolderNameBox, e => e.Current.ControlType == ControlType.Edit).SetText(path);
        PickerControl(FileOpen, IsDialogButton).Invoke();
    }

    /// <summary>Cancels the system's file picker the window opened and waits for it to go.</summary>
    public void CancelFilePicker()
    {
        PickerControl(FileCancel, IsDialogButton).Invoke();
        Until(() => FilePickerOrNull() is null, "the file picker closes");
    }

    /// <summary>A combo box of the open dialog by its position, found from the primary button because it has no name of its own.</summary>
    public AutomationElement DialogComboBox(int index = 0) => Wait(() =>
    {
        var comboBoxes = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ComboBox);
        for (var node = Find("PrimaryButton").FirstOrDefault(); node is not null; node = TreeWalker.ControlViewWalker.GetParent(node))
            if (Below(node, comboBoxes) is { Length: > 0 } boxes)
                return boxes.ElementAtOrDefault(index);
        return null;
    }, $"the dialog's combo box {index}");

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

    /// <summary>
    /// Clicks the left mouse button at the centre of a rectangle given in physical pixels. A rectangle that is empty,
    /// not finite or whose centre is outside this window is refused: the click would land on another window. Prefer
    /// <see cref="ClickOn"/>, which waits until the element is laid out.
    /// </summary>
    public void ClickAt(System.Windows.Rect target) => MouseClick(Centre(target), MouseLeftDown, MouseLeftUp);

    /// <summary>As <see cref="ClickAt"/>, with the right mouse button.</summary>
    public void RightClickAt(System.Windows.Rect target) => MouseClick(Centre(target), MouseRightDown, MouseRightUp);

    /// <summary>
    /// Waits until the element has a usable rectangle on screen (laid out, inside this window, and the point at its centre
    /// belongs to this program), then clicks its centre. On timeout the failure says what was found instead.
    /// </summary>
    public void ClickOn(AutomationElement element, string what) => MouseClick(Target(element, what), MouseLeftDown, MouseLeftUp);

    /// <summary>As <see cref="ClickOn"/>, with the right mouse button.</summary>
    public void RightClickOn(AutomationElement element, string what) => MouseClick(Target(element, what), MouseRightDown, MouseRightUp);

    static void MouseClick((int X, int Y) point, uint down, uint up)
    {
        SetCursorPos(point.X, point.Y);
        mouse_event(down, 0, 0, 0, 0);
        mouse_event(up, 0, 0, 0, 0);
    }

    (int X, int Y) Centre(System.Windows.Rect target) =>
        Problem(target, out var point) is { } problem ? throw new InvalidOperationException($"Refusing to click: {problem}{Describe()}") : point;

    // Why a click at the rectangle's centre would not reach this window, or null with the point to click.
    string? Problem(System.Windows.Rect target, out (int X, int Y) point)
    {
        point = default;
        if (target.IsEmpty || !double.IsFinite(target.X) || !double.IsFinite(target.Y) || !double.IsFinite(target.Width) || !double.IsFinite(target.Height)
            || target.Width <= 0 || target.Height <= 0)
            return $"the rectangle {target} is empty or not finite (element not laid out)";
        if (!GetWindowRect(Handle, out var bounds))
            return $"the window's bounds cannot be read (rectangle {target})";
        var x = target.X + target.Width / 2;
        var y = target.Y + target.Height / 2;
        if (x < bounds.Left || x >= bounds.Right || y < bounds.Top || y >= bounds.Bottom)
            return $"the rectangle {target} has its centre outside the window [{bounds.Left},{bounds.Top} - {bounds.Right},{bounds.Bottom}]";
        point = ((int)x, (int)y);
        return null;
    }

    (int X, int Y) Target(AutomationElement element, string what)
    {
        var observed = "nothing read yet";
        var found = Poll<object>(() =>
        {
            var rect = element.Current.BoundingRectangle;
            if (Problem(rect, out var point) is { } problem)
            { observed = problem; return null; }
            var hit = AutomationElement.FromPoint(new System.Windows.Point(point.X, point.Y)).Current;
            if (hit.ProcessId != process.Id)
            {
                observed = $"the point {point.X},{point.Y} of rectangle {rect} belongs to another program: {hit.ControlType.ProgrammaticName} '{hit.Name}' class {hit.ClassName} pid {hit.ProcessId}";
                return null;
            }
            observed = $"rectangle {rect}, point {point.X},{point.Y} on {hit.ControlType.ProgrammaticName} '{hit.Name}' class {hit.ClassName}";
            return point;
        }, DefaultWait);
        if (found is ValueTuple<int, int> target)
            return target;
        var foreground = GetForegroundWindow();
        throw new TimeoutException($"Timeout: a usable click target for {what}; last seen: {observed}; foreground window {(foreground == Handle ? "is this window" : $"0x{foreground:X} is not this window")}{Describe()}");
    }

    /// <summary>Presses and releases a key (a virtual-key code such as <see cref="Keys.Enter"/>).</summary>
    public static void Press(byte key)
    {
        keybd_event(key, 0, 0, 0);
        keybd_event(key, 0, KeyUp, 0);
        Thread.Sleep(50);
    }

    /// <summary>Presses a key with Ctrl held.</summary>
    public static void PressWithControl(byte key)
    {
        const byte control = 0x11;
        keybd_event(control, 0, 0, 0);
        Press(key);
        keybd_event(control, 0, KeyUp, 0);
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

    const uint KeyUp = 0x0002, MouseLeftDown = 0x0002, MouseLeftUp = 0x0004, MouseRightDown = 0x0008, MouseRightUp = 0x0010, PrintFullContent = 2;

    const uint OwnerWindow = 4;

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetWindow(IntPtr window, uint relation);

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
