// WindowsTutorHost.cs: the Windows implementation of ITutorHost (defined in Jarvis.Core), the screen half of
// the "teach me" tutor. TutorService (created in AppBootstrapper.cs) decides what to teach; this class does the
// Windows work: pick the target window, list its clickable controls through UI Automation, show the pointer
// (TutorPointerForm.cs) and report what the user clicked.
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using Jarvis.Core;

namespace Jarvis.App;

/// <summary>
/// The screen half of the "teach me" tutor: finds the window Noah was using, lists its controls through
/// UI Automation (so the model can only pick controls that exist), and runs the pointer window on a thread
/// of its own so it does not depend on the state of Jarvis's main window.
/// </summary>
internal sealed class WindowsTutorHost : ITutorHost
{
    // Kinds of UI Automation control the tutor may point at: things a person can click, pick or type into.
    private static readonly ControlType[] InteractableTypes =
    [
        ControlType.Button, ControlType.SplitButton, ControlType.MenuItem, ControlType.TabItem, ControlType.ListItem,
        ControlType.TreeItem, ControlType.Hyperlink, ControlType.CheckBox, ControlType.RadioButton, ControlType.ComboBox,
        ControlType.Edit, ControlType.DataItem
    ];

    // Window class names of the desktop, wallpaper and taskbar; these are never a tutoring target.
    private static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd"
    };

    // Guards creating the pointer window, so only one is ever made.
    private readonly object _uiLock = new();
    // The pointer overlay, created on first use by EnsureForm.
    private TutorPointerForm? _form;
    // Completed when the user clicks (with the position) or says "next" (with null); null when no step is waiting.
    private TaskCompletionSource<(int X, int Y)?>? _pending;
    // Jarvis's main window and its original position, remembered while it is moved aside during a lesson.
    private Form? _movedForm;
    private Point _movedFrom;

    // Pick the window to teach in: the foreground window if usable, otherwise the topmost usable window behind Jarvis.
    public TutorWindow? FindTargetWindow()
    {
        var foreground = GetForegroundWindow();

        if (IsCandidate(foreground))
        {
            return Describe(foreground);
        }

        // Jarvis has focus (the request was typed): the window Noah was using is the top one behind it.
        // Walk the windows from top of the stacking order downward (GW_HWNDNEXT = 2 means "the next window below").
        for (var window = GetTopWindow(IntPtr.Zero); window != IntPtr.Zero; window = GetWindow(window, 2 /* GW_HWNDNEXT */))
        {
            if (IsCandidate(window))
            {
                return Describe(window);
            }
        }

        return null;
    }

    // Called at the start of a lesson.
    public void Begin(TutorWindow window)
    {
        // The window to teach in may sit behind Jarvis. Slide Jarvis to the other half of that monitor for the lesson.
        var form = Application.OpenForms.OfType<JarvisMainForm>().FirstOrDefault();

        if (form is null || form.IsDisposed || !form.Visible || form.WindowState != FormWindowState.Normal)
        {
            return;
        }

        // Move Jarvis out of the way if it covers the target: to the screen half away from the target window.
        var target = GetWindowRect(window);
        var targetBounds = new Rectangle(target.Left, target.Top, target.Width, target.Height);
        // Jarvis's form belongs to the UI thread, so the move runs through Invoke.
        form.Invoke((MethodInvoker)(() =>
        {
            if (!form.Bounds.IntersectsWith(targetBounds))
            {
                return;
            }

            var work = Screen.FromRectangle(targetBounds).WorkingArea;
            // Which half of the screen is the target on?
            var targetOnLeft = targetBounds.Left + (targetBounds.Width / 2) < work.Left + (work.Width / 2);
            _movedFrom = form.Location;
            _movedForm = form;
            form.Location = new Point(targetOnLeft ? Math.Max(work.Left, work.Right - form.Width) : work.Left, form.Top);
        }));
    }

    // Called at the end of a lesson: put Jarvis back where it was.
    public void End()
    {
        // Back where it was, so the temporary spot is never what Jarvis remembers.
        var form = _movedForm;
        _movedForm = null;

        if (form is not null && !form.IsDisposed && form.IsHandleCreated)
        {
            try
            {
                form.Invoke((MethodInvoker)(() => form.Location = _movedFrom));
            }
            catch (Exception exception) when (exception is ObjectDisposedException or InvalidOperationException)
            {
                // Jarvis is closing.
            }
        }
    }

    // The window's rectangle in screen pixels, or an empty rectangle if Windows cannot report it.
    public TutorRect GetWindowRect(TutorWindow window) =>
        GetWindowRect(new IntPtr(window.Handle), out var rect) ? new TutorRect(rect.Left, rect.Top, rect.Right, rect.Bottom) : default;

    // List the controls the model may point at, nearest the window's top-left first.
    public IReadOnlyList<TutorElement> ListElements(TutorWindow window)
    {
        var windowRect = GetWindowRect(window);
        var screen = SystemInformation.VirtualScreen;
        // Process id that owns the window.
        _ = GetWindowThreadProcessId(new IntPtr(window.Handle), out var pid);
        var found = new List<TutorElement>();

        // The window itself, plus the other top-level windows of its process: Office opens its drop-downs and
        // galleries (the Table grid) as separate popup windows that UI Automation lists under the desktop.
        var roots = new List<AutomationElement>();

        try
        {
            roots.Add(AutomationElement.FromHandle(new IntPtr(window.Handle)));
        }
        catch (Exception exception) when (exception is ElementNotAvailableException or COMException or ArgumentException)
        {
            throw new TutorUnavailableException("the app window went away");
        }

        // Ask Windows for the other visible top-level windows of the same process (drop-down menus and galleries are separate windows).
        var popups = AutomationElement.RootElement.FindAll(
            TreeScope.Children,
            new AndCondition(
                new PropertyCondition(AutomationElement.ProcessIdProperty, pid),
                new PropertyCondition(AutomationElement.IsOffscreenProperty, false)));

        foreach (AutomationElement popup in popups)
        {
            try
            {
                // Skip the main window itself, which is already in the list.
                if (popup.Current.NativeWindowHandle != (int)window.Handle)
                {
                    roots.Add(popup);
                }
            }
            catch (Exception exception) when (exception is ElementNotAvailableException or COMException)
            {
                // A menu that closed mid-scan.
            }
        }

        // Fetch name, type, id and screen position for each match in one cross-process request, as plain data.
        var cache = new CacheRequest { TreeScope = TreeScope.Element, AutomationElementMode = AutomationElementMode.None };
        cache.Add(AutomationElement.NameProperty);
        cache.Add(AutomationElement.ControlTypeProperty);
        cache.Add(AutomationElement.AutomationIdProperty);
        cache.Add(AutomationElement.BoundingRectangleProperty);

        // Only controls that are on screen, enabled, and of an interactable type.
        var condition = new AndCondition(
            new PropertyCondition(AutomationElement.IsOffscreenProperty, false),
            new PropertyCondition(AutomationElement.IsEnabledProperty, true),
            new OrCondition(InteractableTypes.Select(type => (Condition)new PropertyCondition(AutomationElement.ControlTypeProperty, type)).ToArray()));

        // At most six windows are scanned, to bound the time it takes.
        foreach (var root in roots.Take(6))
        {
            AutomationElementCollection matches;

            try
            {
                // Activating the cache makes FindAll fetch the cached properties with the results.
                using (cache.Activate())
                {
                    matches = root.FindAll(TreeScope.Descendants, condition);
                }
            }
            catch (Exception exception) when (exception is ElementNotAvailableException or COMException)
            {
                continue;
            }

            foreach (AutomationElement element in matches)
            {
                var cached = element.Cached;
                var name = cached.Name?.Trim() ?? string.Empty;
                var box = cached.BoundingRectangle;

                // Drop unnamed or zero-size controls (the model cannot refer to them) and those entirely off every monitor.
                if (name.Length == 0 || box.IsEmpty || box.Width < 1 || box.Height < 1)
                {
                    continue;
                }

                var rect = new TutorRect((int)box.Left, (int)box.Top, (int)box.Right, (int)box.Bottom);

                if (!screen.IntersectsWith(new Rectangle(rect.Left, rect.Top, rect.Width, rect.Height)))
                {
                    continue;
                }

                found.Add(new TutorElement(name, ShortTypeName(cached.ControlType), cached.AutomationId ?? string.Empty, rect));
            }
        }

        // ponytail: nearest the window's top-left first and capped at 150 (a ribbon is at the top); a page with
        // more than that many controls can hide the one needed. Rank by the goal's words if that bites.
        return found
            .OrderBy(e => Math.Pow(e.Rect.Left - windowRect.Left, 2) + Math.Pow(e.Rect.Top - windowRect.Top, 2))
            .Take(TutorLogic.MaxElements)
            .ToList();
    }

    // Show the pointer for one step and wait for the user. Outcomes: Confirmed (clicked the pointed control or said "next" with a control shown), Next (a label-only step), or Corrected (clicked somewhere else, including which control).
    public async Task<TutorOutcome> PointAsync(TutorWindow window, TutorPointer pointer, CancellationToken cancellationToken)
    {
        var form = EnsureForm();
        // RunContinuationsAsynchronously so finishing it from the UI thread cannot run the awaiting code on that thread.
        var pending = new TaskCompletionSource<(int X, int Y)?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending = pending;
        Task<TutorElement?>? pressed = null;
        var rect = pointer.Rect;
        var windowRect = GetWindowRect(window);
        var area = rect ?? windowRect;
        // The monitor the pointer should cover: the one holding the control, or the window if there is no control.
        var monitor = Screen.FromRectangle(new Rectangle(area.Left, area.Top, Math.Max(1, area.Width), Math.Max(1, area.Height))).Bounds;
        var label = rect is null
            ? $"{(pointer.Label.Length > 0 ? pointer.Label : pointer.Say)}  (say \"next\" when done)"
            : pointer.Label.Length > 0 ? pointer.Label : pointer.Say;

        // The pointer window lives on its own thread, so work is handed over with BeginInvoke.
        form.BeginInvoke((MethodInvoker)(() => form.Arm(
            monitor,
            rect,
            label,
            new Rectangle(windowRect.Left, windowRect.Top, windowRect.Width, windowRect.Height),
            // Looked up at the press, before the app reacts to the release and redraws.
            rect is { } r ? (x, y) => { if (!r.Contains(x, y, TutorLogic.ClickMargin)) pressed = Task.Run(() => ElementAt(x, y)); } : null,
            rect is null ? null : (x, y) => pending.TrySetResult((x, y)))));

        // If the lesson is cancelled, cancel the wait.
        using var registration = cancellationToken.Register(() => pending.TrySetCanceled(cancellationToken));

        try
        {
            // Wait for a click or "next".
            var hit = await pending.Task;

            if (rect is not { } target)
            {
                return new TutorOutcome(TutorOutcomeKind.Next, 0, 0);
            }

            if (hit is not { } point)
            {
                // "next" on a step that has a control: count it as done at the control's centre.
                return new TutorOutcome(TutorOutcomeKind.Confirmed, (target.Left + target.Right) / 2, (target.Top + target.Bottom) / 2);
            }

            if (TutorLogic.Classify(target, point.X, point.Y) == TutorOutcomeKind.Confirmed)
            {
                return new TutorOutcome(TutorOutcomeKind.Confirmed, point.X, point.Y);
            }

            // If the wrong thing was clicked, find out what it was (looked up earlier at press time when possible).
            var clicked = pressed is null ? await Task.Run(() => ElementAt(point.X, point.Y)) : await pressed;
            return new TutorOutcome(TutorOutcomeKind.Corrected, point.X, point.Y, clicked);
        }
        finally
        {
            _pending = null;
            // Always hide the pointer afterwards, whatever happened.
            form.BeginInvoke((MethodInvoker)form.Disarm);
        }
    }

    // The spoken "next" command: finishes the current wait with no click.
    public void Next() => _pending?.TrySetResult(null);

    // Create the pointer window once, on its own background UI thread.
    private TutorPointerForm EnsureForm()
    {
        lock (_uiLock)
        {
            if (_form is not null)
            {
                return _form;
            }

            using var ready = new ManualResetEventSlim();
            // The window and its message loop must live on one dedicated thread (STA, required by WinForms); the caller waits until the window exists.
            var thread = new Thread(() =>
            {
                try
                {
                    var form = new TutorPointerForm();
                    // Reading Handle forces the window to be created on this thread, so BeginInvoke works from any other thread.
                    _ = form.Handle;
                    _form = form;
                }
                catch
                {
                    // _form stays null; the caller reports it.
                }
                finally
                {
                    ready.Set();
                }

                if (_form is not null)
                {
                    // Start the message loop that keeps the pointer window responsive; runs until the thread ends.
                    Application.Run(new ApplicationContext());
                }
            })
            { IsBackground = true, Name = "Tutor pointer" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            ready.Wait();
            return _form ?? throw new TutorUnavailableException("I couldn't create the pointer window");
        }
    }

    /// <summary>The control under a point, or the nearest named parent of it (the text inside a button is not the button).</summary>
    private static TutorElement? ElementAt(int x, int y)
    {
        try
        {
            var element = AutomationElement.FromPoint(new System.Windows.Point(x, y));

            for (var depth = 0; element is not null && depth < 4; depth++)
            {
                var info = element.Current;

                if (!string.IsNullOrWhiteSpace(info.Name))
                {
                    var box = info.BoundingRectangle;
                    return new TutorElement(
                        info.Name.Trim(),
                        ShortTypeName(info.ControlType),
                        info.AutomationId ?? string.Empty,
                        new TutorRect((int)box.Left, (int)box.Top, (int)box.Right, (int)box.Bottom));
                }

                element = TreeWalker.ControlViewWalker.GetParent(element);
            }
        }
        catch
        {
            // The element vanished or the app is not answering: the step is saved without one.
        }

        return null;
    }

    // Names like "ControlType.Button" become "Button".
    private static string ShortTypeName(ControlType type) => type.ProgrammaticName.Replace("ControlType.", string.Empty);

    // Is this a window a lesson could be about? Excludes hidden, minimized, tool windows, Jarvis itself, cloaked windows (on another virtual desktop or hidden UWP apps), and the desktop and taskbar.
    private static bool IsCandidate(IntPtr window)
    {
        if (window == IntPtr.Zero || !IsWindowVisible(window) || IsIconic(window)
            // GWL_EXSTYLE = -20: the extended style flags of the window.
            || (GetWindowLong(window, -20 /* GWL_EXSTYLE */) & TutorPointerForm.WsExToolWindow) != 0
            || GetWindowTextLength(window) == 0)
        {
            return false;
        }

        _ = GetWindowThreadProcessId(window, out var processId);

        if (processId == Environment.ProcessId
            // DWMWA_CLOAKED = 13: non-zero when Windows is hiding the window even though it counts as visible.
            || (DwmGetWindowAttribute(window, 13 /* DWMWA_CLOAKED */, out var cloaked, sizeof(int)) == 0 && cloaked != 0))
        {
            return false;
        }

        var className = new StringBuilder(64);
        _ = GetClassName(window, className, className.Capacity);
        return !ShellClasses.Contains(className.ToString());
    }

    // Window handle, process name (e.g. "WINWORD.exe") and title.
    private static TutorWindow Describe(IntPtr window)
    {
        _ = GetWindowThreadProcessId(window, out var processId);
        using var process = Process.GetProcessById((int)processId);
        var title = new StringBuilder(256);
        _ = GetWindowText(window, title, title.Capacity);
        return new TutorWindow(window.ToInt64(), process.ProcessName + ".exe", title.ToString());
    }

    // Win32 RECT: a rectangle's left, top, right and bottom in screen pixels.
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    // Win32 calls from user32.dll and dwmapi.dll: foreground and z-order lookup, visibility, style, title, class name, owning process, rectangle, and "cloaked" state.
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetTopWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr window, uint command);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr window, int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out RECT rect);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
}
