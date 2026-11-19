// Win32UiAutomation.cs: the core of how Jarvis controls other programs' windows by talking to Windows directly (the Win32 API).
// DesktopAutomationService (DesktopAutomation.cs) calls these methods for focus, close, minimise/maximise, click, type, read text, scroll and hotkeys.
// This is the simple "old Windows controls" approach: windows and their child controls are found by title and class name, not through UI Automation.
// Other slices of the same class: Win32UiAutomation.Advanced.cs (move/resize/snap, media keys, clipboard, window listing) and Win32UiAutomation.Text.cs.
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Jarvis.Core;

// Static helper class holding the Win32 calls and the matching logic. Partial: split across three files.
internal static partial class Win32UiAutomation
{
    // Windows message numbers. A "message" is a small request sent to a window; the window's owner program reacts to it.
    // WM_CLOSE asks a window to close itself. WM_LBUTTONDOWN / WM_LBUTTONUP are a left mouse button press and release. WM_SETTEXT sets a control's text,
    // WM_GETTEXT / WM_GETTEXTLENGTH read it, WM_VSCROLL scrolls vertically. BM_CLICK clicks a button. SB_LINEDOWN / SB_LINEUP mean "scroll one line down / up".
    private const uint WM_CLOSE = 0x0010;
    private const uint WM_LBUTTONDOWN = 0x0201;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_SETTEXT = 0x000C;
    private const uint WM_GETTEXT = 0x000D;
    private const uint WM_GETTEXTLENGTH = 0x000E;
    private const uint WM_VSCROLL = 0x0115;
    private const uint BM_CLICK = 0x00F5;
    private const uint SB_LINEDOWN = 1;
    private const uint SB_LINEUP = 0;
    // ShowWindow commands: show normally (5), minimise (6), restore (9), maximise (3).
    private const int SwShow = 5;
    private const int SwMinimize = 6;
    private const int SwRestore = 9;
    private const int SwMaximize = 3;
    // SendInput values: input type 1 means "keyboard event", and the key-up flag 0x0002 means "key released" (without it the event is a key press).
    private const uint InputKeyboard = 1;
    private const uint KeyeventfKeyup = 0x0002;

    // Table of key names ("ctrl", "enter", "f5"...) to Windows virtual-key codes, built once.
    private static readonly IReadOnlyDictionary<string, ParsedKeyDefinition> NamedKeys = BuildNamedKeys();

    // P/Invoke declarations: each [DllImport] line lets C# call a function exported by Windows' user32.dll (the window manager). The comment above each says what the Windows function does.
    // SetForegroundWindow: asks Windows to bring this window to the front and give it keyboard focus (Windows may refuse if it thinks another app should keep focus).
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    // SendMessage: sends a message to a window and waits until that window has handled it; returns the window's answer.
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    // A second declaration of SendMessage with a text buffer, used to read text back (WM_GETTEXT).
    // EntryPoint is needed because user32.dll has no function called SendMessageText; Unicode picks SendMessageW.
    [DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageText(IntPtr hWnd, uint msg, int nMaxCount, StringBuilder lpString);

    // GetClientRect: fills in the size of a window's drawable area (the part inside the borders).
    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, ref RECT lpRect);

    // EnumChildWindows: calls our function once for every control (button, text box...) inside a window.
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumChildWindows(IntPtr hWndParent, EnumChildProc lpEnumFunc, IntPtr lParam);

    // GetClassName: gets a control's class name (for example "Edit" for a text box or "Button"), which tells us what kind of control it is.
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    // GetWindowText: copies a window's title or a control's text into a buffer.
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    // GetWindowTextLength: how many characters that text has.
    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    // ShowWindowAsync: change a window's state (minimise, maximise, restore) without waiting for the other program.
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    // IsIconic: true when a window is minimised.
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    // PostMessage: like SendMessage but just drops the message in the window's queue and returns at once (used for close, so a window that shows a "save?" prompt does not block us).
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    // SendInput: injects real keyboard (or mouse) events into Windows as if the user typed them. Returns how many events were accepted.
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    // Tool: bring a window to the front.
    public static Task<string> FocusWindowAsync(string targetApp)
    {
        if (!TryResolveWindow(targetApp, out var window))
        {
            return Task.FromResult($"Window matching '{targetApp}' was not found.");
        }

        return Task.FromResult(
            TryBringWindowToFront(window.Handle)
                ? $"Focused '{DescribeWindow(window)}'."
                : $"Unable to focus '{DescribeWindow(window)}'.");
    }

    // Tool: ask a window to close. It uses WM_CLOSE, the polite request, so programs can still ask "save changes?" instead of being killed.
    public static Task<string> CloseWindowAsync(string targetApp)
    {
        if (!TryResolveWindow(targetApp, out var window))
        {
            return Task.FromResult($"Window matching '{targetApp}' was not found.");
        }

        _ = TryBringWindowToFront(window.Handle);
        var closed = PostMessage(window.Handle, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

        return Task.FromResult(
            closed
                ? $"Sent a close request to '{DescribeWindow(window)}'."
                : $"Unable to close '{DescribeWindow(window)}'.");
    }

    // Tool: minimise, maximise or restore a window.
    public static Task<string> SetWindowStateAsync(string targetApp, string state)
    {
        if (!TryResolveWindow(targetApp, out var window))
        {
            return Task.FromResult($"Window matching '{targetApp}' was not found.");
        }

        if (!TryResolveWindowState(state, out var command, out var description))
        {
            return Task.FromResult("Usage: window state <window> <minimize|maximize|restore>");
        }

        _ = ShowWindowAsync(window.Handle, command);

        if (command is SwRestore or SwMaximize)
        {
            _ = TryBringWindowToFront(window.Handle);
        }

        return Task.FromResult($"{description} '{DescribeWindow(window)}'.");
    }

    // Tool: click. With no element name it clicks the middle of the window; with a name it finds that control and clicks it. Buttons get BM_CLICK; anything else gets a mouse press and release sent to its centre.
    public static Task<string> ClickElementAsync(string targetApp, string elementName)
    {
        if (!TryResolveWindow(targetApp, out var window))
        {
            return Task.FromResult($"Window matching '{targetApp}' was not found.");
        }

        _ = TryBringWindowToFront(window.Handle);

        var targetHandle = window.Handle;
        var targetDescription = DescribeWindow(window);

        if (!string.IsNullOrWhiteSpace(elementName))
        {
            if (!TryResolveElement(window.Handle, elementName, out var element))
            {
                return Task.FromResult($"Element matching '{elementName.Trim()}' was not found in '{DescribeWindow(window)}'.");
            }

            targetHandle = element.Handle;
            targetDescription = DescribeElement(element);

            if (element.IsButtonLike)
            {
                _ = SendMessage(targetHandle, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                return Task.FromResult($"Clicked '{targetDescription}' in '{DescribeWindow(window)}'.");
            }
        }

        // Find the centre of the control's drawable area, then pack it into the single number Windows expects as the mouse position (see EncodePoint).
        var rect = new RECT();
        _ = GetClientRect(targetHandle, ref rect);
        var point = new POINT { X = rect.Right / 2, Y = rect.Bottom / 2 };

        _ = SendMessage(targetHandle, WM_LBUTTONDOWN, IntPtr.Zero, EncodePoint(point));
        _ = SendMessage(targetHandle, WM_LBUTTONUP, IntPtr.Zero, EncodePoint(point));

        return Task.FromResult(
            string.IsNullOrWhiteSpace(elementName)
                ? $"Clicked the center of '{DescribeWindow(window)}'."
                : $"Clicked '{targetDescription}' in '{DescribeWindow(window)}'.");
    }

    // Tool: put text into a named control, replacing its contents.
    public static Task<string> TypeIntoElementAsync(string targetApp, string elementName, string text)
    {
        // Without a named field, type at the cursor like a keyboard. WM_SETTEXT below replaces the
        // control's entire contents (a whole Notepad document), and when it finds no edit control it
        // renames the window instead, reporting success either way.
        if (string.IsNullOrWhiteSpace(elementName))
        {
            return SendTextAsync(targetApp, text);
        }

        if (!TryResolveWindow(targetApp, out var window))
        {
            return Task.FromResult($"Window matching '{targetApp}' was not found.");
        }

        _ = TryBringWindowToFront(window.Handle);

        if (!TryResolveElement(window.Handle, elementName, out var element))
        {
            return Task.FromResult($"Element matching '{elementName.Trim()}' was not found in '{DescribeWindow(window)}'.");
        }

        var targetHandle = element.Handle;
        var targetDescription = DescribeElement(element);
        // WM_SETTEXT needs a pointer to the text in unmanaged memory, so the string is copied there, sent, and freed in the finally block even if the send fails.
        var textHandle = Marshal.StringToHGlobalUni(text);

        try
        {
            _ = SendMessage(targetHandle, WM_SETTEXT, IntPtr.Zero, textHandle);
        }
        finally
        {
            Marshal.FreeHGlobal(textHandle);
        }

        return Task.FromResult($"Typed {DescribeTextLength(text)} into '{targetDescription}' in '{DescribeWindow(window)}'.");
    }

    // Tool: read the text of a control or, with no name, of the most text-like control in the window (see FindBestTextHandle).
    public static Task<string> GetElementTextAsync(string targetApp, string elementName)
    {
        if (!TryResolveWindow(targetApp, out var window))
        {
            return Task.FromResult($"Window matching '{targetApp}' was not found.");
        }

        var targetHandle = FindBestTextHandle(window.Handle);
        var targetDescription = DescribeWindow(window);

        if (!string.IsNullOrWhiteSpace(elementName))
        {
            if (!TryResolveElement(window.Handle, elementName, out var element))
            {
                return Task.FromResult($"Element matching '{elementName.Trim()}' was not found in '{DescribeWindow(window)}'.");
            }

            targetHandle = element.Handle;
            targetDescription = DescribeElement(element);
        }

        // Ask the control how long its text is, then copy that many characters into a buffer (+1 for the end-of-string character).
        var length = (int)SendMessage(targetHandle, WM_GETTEXTLENGTH, IntPtr.Zero, IntPtr.Zero);

        if (length == 0)
        {
            return Task.FromResult(
                string.IsNullOrWhiteSpace(elementName)
                    ? $"No text detected in '{DescribeWindow(window)}'."
                    : $"No text detected in '{targetDescription}' in '{DescribeWindow(window)}'.");
        }

        var builder = new StringBuilder(length + 1);
        _ = SendMessageText(targetHandle, WM_GETTEXT, builder.Capacity, builder);
        return Task.FromResult(builder.ToString());
    }

    // Tool: scroll the window's main text area one line up or down.
    public static Task<string> ScrollWindowAsync(string targetApp, string direction)
    {
        if (!TryResolveWindow(targetApp, out var window))
        {
            return Task.FromResult($"Window matching '{targetApp}' was not found.");
        }

        _ = TryBringWindowToFront(window.Handle);
        var targetHandle = FindBestTextHandle(window.Handle);
        var scrollCode = string.Equals(direction, "down", StringComparison.OrdinalIgnoreCase)
            ? SB_LINEDOWN
            : SB_LINEUP;

        _ = SendMessage(targetHandle, WM_VSCROLL, (IntPtr)scrollCode, IntPtr.Zero);
        return Task.FromResult($"Scrolled '{DescribeWindow(window)}' {direction}.");
    }

    // Tool: press a key combination in a window. The window is focused first, then a 120 ms pause lets focus settle before the keys are injected.
    public static async Task<string> SendHotkeyAsync(string targetApp, string hotkey)
    {
        if (!TryResolveWindow(targetApp, out var window))
        {
            return $"Window matching '{targetApp}' was not found.";
        }

        if (!TryParseHotkey(hotkey, out var parsedHotkey, out var parseError))
        {
            return parseError;
        }

        if (!TryBringWindowToFront(window.Handle))
        {
            return $"Unable to focus '{DescribeWindow(window)}' before sending the hotkey.";
        }

        // A short delay because focus changes are not instant; sending keys too early would go to the previous window.
        await Task.Delay(120).ConfigureAwait(false);

        if (!TrySendHotkey(parsedHotkey, out var sendError))
        {
            return sendError;
        }

        return $"Sent {parsedHotkey.DisplayText} to '{DescribeWindow(window)}'.";
    }

    // Tool: pause between steps of a workflow.
    public static async Task<string> WaitAsync(double seconds, CancellationToken cancellationToken)
    {
        await Task.Delay((int)(seconds * 1000), cancellationToken);
        return $"Waited {seconds}s";
    }

    // These structs mirror Windows' own C structures byte for byte. [StructLayout] tells .NET to lay the fields out the way the Windows function expects.
    // RECT is a rectangle (left, top, right, bottom); POINT is an x/y position.
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    // INPUT is one entry for SendInput. It holds a type and a union of the possible event shapes (mouse, keyboard).
    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    // MOUSEINPUT is the union's largest member. Without it INPUT is 32 bytes instead of 40 on 64-bit
    // Windows, and SendInput rejects every call because cbSize is wrong.
    // Explicit layout: both members start at offset 0 and overlap in memory, exactly like a C union.
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MOUSEINPUT mi;

        [FieldOffset(0)]
        public KEYBDINPUT ki;
    }

    // Mouse event shape; only declared so the union has the right size.
    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    // Keyboard event shape: wVk is the virtual-key code (which key), dwFlags says press or release.
    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    // A window found by name: its handle (numeric id), title, program name, screen rectangle, and whether it is the front window.
    private readonly record struct WindowMatch(IntPtr Handle, string Title, string ProcessName, RECT Bounds, bool IsForeground);
    // A control inside a window: handle, class name, text, and whether it behaves like a button.
    private readonly record struct ElementMatch(IntPtr Handle, string ClassName, string Text, bool IsButtonLike);

    // One key: its virtual-key code, whether it is a modifier (Ctrl/Shift/Alt/Win) and the name to show back.
    private readonly record struct ParsedKeyDefinition(ushort VirtualKey, bool IsModifier, string DisplayText);

    // A fully parsed shortcut: modifier keys, one main key, and text like "Ctrl+Shift+T" for messages.
    private sealed record ParsedHotkey(
        IReadOnlyList<ParsedKeyDefinition> Modifiers,
        ParsedKeyDefinition Key,
        string DisplayText);

    // The shape of the callback EnumChildWindows calls for each control; returning true means "keep going".
    private delegate bool EnumChildProc(IntPtr hWnd, IntPtr lParam);

    // Finds the open window that best matches a spoken name by scoring every top-level window (see ScoreWindow). Returns false when nothing scores above zero.
    private static bool TryResolveWindow(string target, out WindowMatch window)
    {
        window = default;

        if (string.IsNullOrWhiteSpace(target))
        {
            return false;
        }

        var normalizedTarget = NormalizeForMatching(target);
        var bestScore = 0;

        foreach (var candidate in EnumerateTopLevelWindows())
        {
            var score = ScoreWindow(target, normalizedTarget, candidate.Title, candidate.ProcessName);

            if (score <= bestScore)
            {
                continue;
            }

            window = candidate;
            bestScore = score;
        }

        return bestScore > 0;
    }

    // Scores how well a window matches the name: exact title 120, exact program name 110, normalised title 100, normalised program name 95, title contains it 80, program name contains it 72. Highest wins.
    private static int ScoreWindow(string rawTarget, string normalizedTarget, string title, string processName)
    {
        var normalizedTitle = NormalizeForMatching(title);
        var normalizedProcessName = NormalizeForMatching(processName);
        var score = 0;

        if (string.Equals(title, rawTarget, StringComparison.OrdinalIgnoreCase))
        {
            score = Math.Max(score, 120);
        }

        if (string.Equals(processName, rawTarget, StringComparison.OrdinalIgnoreCase))
        {
            score = Math.Max(score, 110);
        }

        if (string.Equals(normalizedTitle, normalizedTarget, StringComparison.OrdinalIgnoreCase))
        {
            score = Math.Max(score, 100);
        }

        if (string.Equals(normalizedProcessName, normalizedTarget, StringComparison.OrdinalIgnoreCase))
        {
            score = Math.Max(score, 95);
        }

        if (!string.IsNullOrWhiteSpace(normalizedTitle)
            && normalizedTitle.Contains(normalizedTarget, StringComparison.OrdinalIgnoreCase))
        {
            score = Math.Max(score, 80);
        }

        if (!string.IsNullOrWhiteSpace(normalizedProcessName)
            && normalizedProcessName.Contains(normalizedTarget, StringComparison.OrdinalIgnoreCase))
        {
            score = Math.Max(score, 72);
        }

        return score;
    }

    // Finds the control most likely to hold the document text: an "Edit" control beats a "text" class, which beats any control that has text.
    private static IntPtr FindBestTextHandle(IntPtr rootHandle)
    {
        var bestHandle = rootHandle;
        var bestScore = 0;

        _ = EnumChildWindows(
            rootHandle,
            (childHandle, _) =>
            {
                var className = ReadClassName(childHandle);
                var text = ReadWindowText(childHandle);
                var score = ScoreChildHandle(className, text);

                if (score > bestScore)
                {
                    bestScore = score;
                    bestHandle = childHandle;
                }

                return true;
            },
            IntPtr.Zero);

        return bestHandle;
    }

    // Finds the control best matching a name the user gave, by scoring every child control (see ScoreElementHandle).
    private static bool TryResolveElement(IntPtr rootHandle, string elementName, out ElementMatch element)
    {
        element = default;

        if (string.IsNullOrWhiteSpace(elementName))
        {
            return false;
        }

        var rawTarget = elementName.Trim();
        var normalizedTarget = NormalizeForMatching(rawTarget);
        var bestScore = 0;
        var bestElement = default(ElementMatch);

        _ = EnumChildWindows(
            rootHandle,
            (childHandle, _) =>
            {
                var className = ReadClassName(childHandle);
                var text = ReadWindowText(childHandle);
                var score = ScoreElementHandle(rawTarget, normalizedTarget, className, text);

                if (score <= bestScore)
                {
                    return true;
                }

                bestElement = new ElementMatch(
                    childHandle,
                    className,
                    text,
                    IsButtonLikeClass(className));
                bestScore = score;
                return true;
            },
            IntPtr.Zero);

        element = bestElement;
        return bestScore > 0;
    }

    // Scores a control as a text area: edit boxes 100, text-ish classes 70, anything with text 40.
    private static int ScoreChildHandle(string className, string text)
    {
        var normalizedClassName = NormalizeForMatching(className);
        var score = 0;

        if (normalizedClassName.Contains("edit", StringComparison.OrdinalIgnoreCase))
        {
            score = Math.Max(score, 100);
        }

        if (normalizedClassName.Contains("text", StringComparison.OrdinalIgnoreCase))
        {
            score = Math.Max(score, 70);
        }

        if (!string.IsNullOrWhiteSpace(text))
        {
            score = Math.Max(score, 40);
        }

        return score;
    }

    // Scores a control against a spoken name. Its text equal to the name scores highest (160/150), containing it 120, a role word match ("button", "field", "text") 90-100, shared words 15 each, a class name containing it 70.
    private static int ScoreElementHandle(string rawTarget, string normalizedTarget, string className, string text)
    {
        var normalizedText = NormalizeForMatching(text);
        var normalizedClassName = NormalizeForMatching(className);
        var score = 0;

        if (!string.IsNullOrWhiteSpace(text))
        {
            if (string.Equals(text, rawTarget, StringComparison.OrdinalIgnoreCase))
            {
                score = Math.Max(score, 160);
            }

            if (string.Equals(normalizedText, normalizedTarget, StringComparison.OrdinalIgnoreCase))
            {
                score = Math.Max(score, 150);
            }

            if (!string.IsNullOrWhiteSpace(normalizedTarget)
                && normalizedText.Contains(normalizedTarget, StringComparison.OrdinalIgnoreCase))
            {
                score = Math.Max(score, 120);
            }
        }

        // Words like "button", "tab", "menu", "link" in the request hint at buttons; "field", "search", "input" at edit boxes; "label", "text" at plain text.
        // A role hint boosts controls of the matching kind even if the control's own text does not match.
        var roleHint = ResolveElementRoleHint(normalizedTarget);

        if (roleHint == "button" && IsButtonLikeClass(className))
        {
            score = Math.Max(score, 100);
        }
        else if (roleHint == "field" && IsEditableClass(className))
        {
            score = Math.Max(score, 100);
        }
        else if (roleHint == "text" && IsTextDisplayClass(className))
        {
            score = Math.Max(score, 90);
        }

        if (!string.IsNullOrWhiteSpace(normalizedText)
            && !string.IsNullOrWhiteSpace(normalizedTarget))
        {
            var targetTokens = normalizedTarget.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var matchingTokens = targetTokens.Count(token =>
                token.Length >= 3
                && normalizedText.Contains(token, StringComparison.OrdinalIgnoreCase));

            score = Math.Max(score, matchingTokens * 15);
        }

        if (!string.IsNullOrWhiteSpace(normalizedClassName)
            && !string.IsNullOrWhiteSpace(normalizedTarget)
            && normalizedClassName.Contains(normalizedTarget, StringComparison.OrdinalIgnoreCase))
        {
            score = Math.Max(score, 70);
        }

        return score;
    }

    // Brings a window to the front: restores it first if it is minimised (a minimised window cannot take focus), then makes it the foreground window.
    private static bool TryBringWindowToFront(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            _ = ShowWindowAsync(handle, IsIconic(handle) ? SwRestore : SwShow);
            return SetForegroundWindow(handle);
        }
        catch
        {
            return false;
        }
    }

    // Maps the words minimize/maximize/restore (and their past forms) to the ShowWindow command and a word for the reply.
    private static bool TryResolveWindowState(string state, out int command, out string description)
    {
        var normalized = NormalizeForMatching(state);

        switch (normalized)
        {
            case "minimize":
            case "minimized":
                command = SwMinimize;
                description = "Minimized";
                return true;
            case "maximize":
            case "maximized":
                command = SwMaximize;
                description = "Maximized";
                return true;
            case "restore":
            case "restored":
                command = SwRestore;
                description = "Restored";
                return true;
            default:
                command = 0;
                description = string.Empty;
                return false;
        }
    }

    // Turns text like "ctrl+shift+t" into a parsed shortcut. Needs at least one non-modifier key and allows only one (several key presses must be done as separate steps).
    private static bool TryParseHotkey(string hotkey, out ParsedHotkey parsedHotkey, out string error)
    {
        parsedHotkey = null!;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(hotkey))
        {
            error = "Usage: send hotkey <window> <key-combo>";
            return false;
        }

        var tokens = NormalizeHotkey(hotkey)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (tokens.Length == 0)
        {
            error = "Usage: send hotkey <window> <key-combo>";
            return false;
        }

        var resolvedKeys = new List<ParsedKeyDefinition>();

        foreach (var token in tokens)
        {
            if (!TryResolveKey(token, out var key))
            {
                error = $"Unsupported hotkey token '{token}'.";
                return false;
            }

            resolvedKeys.Add(key);
        }

        var modifiers = resolvedKeys
            .Where(key => key.IsModifier)
            .DistinctBy(key => key.VirtualKey)
            .ToArray();
        var nonModifiers = resolvedKeys
            .Where(key => !key.IsModifier)
            .ToArray();

        if (nonModifiers.Length == 0)
        {
            error = "A hotkey must include at least one non-modifier key.";
            return false;
        }

        if (nonModifiers.Length > 1)
        {
            error = "Only single-key shortcuts are supported here. Chain multiple key presses with a workflow such as `focus app ...; send hotkey ...; wait 0.2; send hotkey ...`.";
            return false;
        }

        var displayParts = modifiers
            .Select(key => key.DisplayText)
            .Concat([nonModifiers[0].DisplayText]);

        parsedHotkey = new ParsedHotkey(modifiers, nonModifiers[0], string.Join('+', displayParts));
        return true;
    }

    // Looks up a key by name, or accepts a single letter or digit (their virtual-key codes equal the capital letter's character code).
    private static bool TryResolveKey(string token, out ParsedKeyDefinition key)
    {
        if (NamedKeys.TryGetValue(token, out key))
        {
            return true;
        }

        if (token.Length == 1 && char.IsLetterOrDigit(token[0]))
        {
            var character = char.ToUpperInvariant(token[0]);
            key = new ParsedKeyDefinition(character, false, character.ToString());
            return true;
        }

        key = default;
        return false;
    }

    // Presses keys in order: modifiers down, main key down and up, then modifiers up in reverse, all sent in one SendInput call so no other input can slip in between.
    // If Windows accepts fewer events than were sent, something blocked the input and the call is reported as failed.
    private static bool TrySendHotkey(ParsedHotkey hotkey, out string error)
    {
        var inputs = new List<INPUT>();

        foreach (var modifier in hotkey.Modifiers)
        {
            inputs.Add(CreateKeyboardInput(modifier.VirtualKey, keyUp: false));
        }

        inputs.Add(CreateKeyboardInput(hotkey.Key.VirtualKey, keyUp: false));
        inputs.Add(CreateKeyboardInput(hotkey.Key.VirtualKey, keyUp: true));

        foreach (var modifier in hotkey.Modifiers.Reverse())
        {
            inputs.Add(CreateKeyboardInput(modifier.VirtualKey, keyUp: true));
        }

        var sent = SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());

        if (sent != inputs.Count)
        {
            error = $"Windows only dispatched {sent} of {inputs.Count} keyboard events for {hotkey.DisplayText}.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    // Builds one keyboard event: which virtual key, and a key-up flag when it is a release.
    private static INPUT CreateKeyboardInput(ushort virtualKey, bool keyUp)
    {
        return new INPUT
        {
            type = InputKeyboard,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = virtualKey,
                    dwFlags = keyUp ? KeyeventfKeyup : 0
                }
            }
        };
    }

    // The key name table: virtual-key codes from Windows (0x11 Ctrl, 0x10 Shift, 0x12 Alt, 0x5B left Windows key, 0x70 is F1 and so on up to F24).
    private static IReadOnlyDictionary<string, ParsedKeyDefinition> BuildNamedKeys()
    {
        var keys = new Dictionary<string, ParsedKeyDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["ctrl"] = new(0x11, true, "Ctrl"),
            ["shift"] = new(0x10, true, "Shift"),
            ["alt"] = new(0x12, true, "Alt"),
            ["win"] = new(0x5B, true, "Win"),
            ["tab"] = new(0x09, false, "Tab"),
            ["enter"] = new(0x0D, false, "Enter"),
            ["esc"] = new(0x1B, false, "Esc"),
            ["space"] = new(0x20, false, "Space"),
            ["backspace"] = new(0x08, false, "Backspace"),
            ["delete"] = new(0x2E, false, "Delete"),
            ["del"] = new(0x2E, false, "Delete"),
            ["insert"] = new(0x2D, false, "Insert"),
            ["home"] = new(0x24, false, "Home"),
            ["end"] = new(0x23, false, "End"),
            ["pageup"] = new(0x21, false, "PageUp"),
            ["pagedown"] = new(0x22, false, "PageDown"),
            ["up"] = new(0x26, false, "Up"),
            ["down"] = new(0x28, false, "Down"),
            ["left"] = new(0x25, false, "Left"),
            ["right"] = new(0x27, false, "Right")
        };

        for (var index = 1; index <= 24; index++)
        {
            keys[$"f{index}"] = new((ushort)(0x70 + index - 1), false, $"F{index}");
        }

        return keys;
    }

    // Cleans hotkey text: lower-case, swaps spoken words for key names (see HotkeyReplacements), and treats + and - as spaces.
    private static string NormalizeHotkey(string hotkey)
    {
        var normalized = hotkey.Trim().ToLowerInvariant();

        foreach (var replacement in HotkeyReplacements)
        {
            normalized = normalized.Replace(replacement.From, replacement.To, StringComparison.Ordinal);
        }

        normalized = normalized.Replace('+', ' ');
        normalized = normalized.Replace('-', ' ');

        return string.Join(' ', normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    // Spoken or alternative key names mapped to the names in the key table. "command" maps to the Windows key because there is no Command key on Windows.
    private static readonly (string From, string To)[] HotkeyReplacements =
    [
        ("control", "ctrl"),
        ("windows", "win"),
        ("command", "win"),
        ("option", "alt"),
        ("return", "enter"),
        ("escape", "esc"),
        ("page up", "pageup"),
        ("page down", "pagedown"),
        ("arrow up", "up"),
        ("arrow down", "down"),
        ("arrow left", "left"),
        ("arrow right", "right")
    ];

    // Reads a control's class name into a 128-character buffer.
    private static string ReadClassName(IntPtr handle)
    {
        var builder = new StringBuilder(128);
        _ = GetClassName(handle, builder, builder.Capacity);
        return builder.ToString();
    }

    // Reads a control's text.
    private static string ReadWindowText(IntPtr handle)
    {
        var length = GetWindowTextLength(handle);

        if (length <= 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(length + 1);
        _ = GetWindowText(handle, builder, builder.Capacity);
        return builder.ToString();
    }

    // Guesses what kind of control the user means from words in the request (see ScoreElementHandle): returns "button", "field", "text" or empty.
    private static string ResolveElementRoleHint(string normalizedTarget)
    {
        if (string.IsNullOrWhiteSpace(normalizedTarget))
        {
            return string.Empty;
        }

        if (normalizedTarget.Contains("button", StringComparison.OrdinalIgnoreCase)
            || normalizedTarget.Contains("tab", StringComparison.OrdinalIgnoreCase)
            || normalizedTarget.Contains("menu", StringComparison.OrdinalIgnoreCase)
            || normalizedTarget.Contains("link", StringComparison.OrdinalIgnoreCase))
        {
            return "button";
        }

        if (normalizedTarget.Contains("field", StringComparison.OrdinalIgnoreCase)
            || normalizedTarget.Contains("input", StringComparison.OrdinalIgnoreCase)
            || normalizedTarget.Contains("textbox", StringComparison.OrdinalIgnoreCase)
            || normalizedTarget.Contains("text box", StringComparison.OrdinalIgnoreCase)
            || normalizedTarget.Contains("search", StringComparison.OrdinalIgnoreCase)
            || normalizedTarget.Contains("edit", StringComparison.OrdinalIgnoreCase))
        {
            return "field";
        }

        if (normalizedTarget.Contains("label", StringComparison.OrdinalIgnoreCase)
            || normalizedTarget.Contains("text", StringComparison.OrdinalIgnoreCase)
            || normalizedTarget.Contains("caption", StringComparison.OrdinalIgnoreCase))
        {
            return "text";
        }

        return string.Empty;
    }

    // Control class name helpers: button-like classes contain "button", "tab" or "menu"; editable ones contain "edit", "combo" or "rich" (rich-text box);
    // text display adds "static" (labels) and "text".
    private static bool IsButtonLikeClass(string className)
    {
        var normalizedClassName = NormalizeForMatching(className);
        return normalizedClassName.Contains("button", StringComparison.OrdinalIgnoreCase)
            || normalizedClassName.Contains("tab", StringComparison.OrdinalIgnoreCase)
            || normalizedClassName.Contains("menu", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsEditableClass(string className)
    {
        var normalizedClassName = NormalizeForMatching(className);
        return normalizedClassName.Contains("edit", StringComparison.OrdinalIgnoreCase)
            || normalizedClassName.Contains("combo", StringComparison.OrdinalIgnoreCase)
            || normalizedClassName.Contains("rich", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTextDisplayClass(string className)
    {
        var normalizedClassName = NormalizeForMatching(className);
        return normalizedClassName.Contains("static", StringComparison.OrdinalIgnoreCase)
            || normalizedClassName.Contains("text", StringComparison.OrdinalIgnoreCase)
            || IsEditableClass(className);
    }

    // Name to use when talking about a control: its text, or its class name, or "unnamed control".
    private static string DescribeElement(ElementMatch element)
    {
        if (!string.IsNullOrWhiteSpace(element.Text))
        {
            return element.Text;
        }

        return string.IsNullOrWhiteSpace(element.ClassName)
            ? "unnamed control"
            : element.ClassName;
    }

    // Name to use when talking about a window: its title, or the program name when it has no title.
    private static string DescribeWindow(WindowMatch window)
    {
        return string.IsNullOrWhiteSpace(window.Title)
            ? window.ProcessName
            : window.Title;
    }

    // Makes names comparable: lower-case, punctuation becomes spaces, repeated spaces collapse.
    private static string NormalizeForMatching(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var chars = value
            .ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) || char.IsWhiteSpace(character) ? character : ' ')
            .ToArray();

        return string.Join(' ', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    // "1 character" or "N characters" for the reply.
    private static string DescribeTextLength(string text)
    {
        return text.Length == 1
            ? "1 character"
            : $"{text.Length} characters";
    }

    // Packs a point into the single number mouse messages expect: x in the low 16 bits and y in the high 16 bits.
    private static IntPtr EncodePoint(POINT point)
    {
        var x = point.X & 0xFFFF;
        var y = (point.Y & 0xFFFF) << 16;
        return (IntPtr)(x | y);
    }
}
