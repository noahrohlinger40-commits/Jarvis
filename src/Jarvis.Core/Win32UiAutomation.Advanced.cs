// Win32UiAutomation.Advanced.cs: second slice of Win32UiAutomation. It lists windows, moves/resizes/snaps them, sends media keys
// (play, volume...) and reads/writes the Windows clipboard. Called from DesktopAutomationService (DesktopAutomation.cs); it reuses the
// window matching, key-sending and name helpers from Win32UiAutomation.cs. The clipboard code talks to the raw Windows clipboard API.
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Jarvis.Core;

internal static partial class Win32UiAutomation
{
    // MonitorFromWindow flag 2 = "if the window is on no screen, use the nearest one".
    private const uint MonitorDefaultToNearest = 2;
    // Clipboard format number 13 = CF_UNICODETEXT, plain Unicode text.
    private const uint CfUnicodeText = 13;
    // GlobalAlloc flag 0x0002 = GMEM_MOVEABLE; the clipboard requires memory allocated this way.
    private const uint GmemMoveable = 0x0002;
    // Longest clipboard text returned to the model, so a huge copy cannot flood it.
    private const int ClipboardPreviewLimit = 4000;
    // Virtual-key codes for the keyboard's media keys (0xAD mute, 0xAE volume down, 0xAF volume up, 0xB0 next, 0xB1 previous, 0xB2 stop, 0xB3 play/pause).
    private const ushort VkVolumeMute = 0xAD;
    private const ushort VkVolumeDown = 0xAE;
    private const ushort VkVolumeUp = 0xAF;
    private const ushort VkMediaNextTrack = 0xB0;
    private const ushort VkMediaPrevTrack = 0xB1;
    private const ushort VkMediaStop = 0xB2;
    private const ushort VkMediaPlayPause = 0xB3;

    // P/Invoke declarations (Windows API calls). EnumWindows: calls our function once for every top-level window on the desktop.
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    // IsWindowVisible: true if the window is shown (not hidden).
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    // GetWindowRect: the window's position and size on screen, as left/top/right/bottom.
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    // MoveWindow: sets a window's position and size in one call; the last argument asks it to repaint.
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveWindow(IntPtr hWnd, int x, int y, int nWidth, int nHeight, [MarshalAs(UnmanagedType.Bool)] bool bRepaint);

    // GetForegroundWindow: the window the user is currently using.
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    // GetWindowThreadProcessId: given a window, returns the id of the process that owns it.
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    // MonitorFromWindow: which screen (monitor) a window is on.
    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint dwFlags);

    // GetMonitorInfo: size of a screen, including its "work area" (the part not covered by the taskbar).
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    // The clipboard calls: OpenClipboard takes exclusive use of the clipboard (only one program may at a time) and CloseClipboard releases it.
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    // CloseClipboard: gives the clipboard back to other programs.
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    // EmptyClipboard: removes everything on the clipboard and makes us its owner.
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    // GetClipboardData: gets a handle to the clipboard contents in the given format.
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint uFormat);

    // SetClipboardData: puts our memory block on the clipboard; Windows owns the memory afterwards.
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    // IsClipboardFormatAvailable: true if the clipboard currently holds that kind of data (here: text).
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsClipboardFormatAvailable(uint format);

    // GlobalAlloc / GlobalLock / GlobalUnlock / GlobalFree: the old Windows memory calls the clipboard uses. Alloc reserves a block, Lock gives a usable pointer to it,
    // Unlock hands it back, Free releases it (only needed if the clipboard did not take ownership).
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr hMem);

    // Windows' MONITORINFO structure: rcMonitor is the whole screen, rcWork is the usable area without the taskbar. cbSize must be set to the struct's size before the call.
    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    // Callback shape for EnumWindows; returning true keeps the enumeration going.
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    // Tool: list visible windows (up to 20) with program name and size/position. With a name, only matching windows, best first; the active window is marked.
    public static Task<string> ListWindowsAsync(string target)
    {
        var windows = EnumerateTopLevelWindows();
        IEnumerable<WindowMatch> filtered = windows;

        if (!string.IsNullOrWhiteSpace(target))
        {
            var normalizedTarget = NormalizeForMatching(target);
            filtered = windows
                .Select(window => new
                {
                    Window = window,
                    Score = ScoreWindow(target, normalizedTarget, window.Title, window.ProcessName)
                })
                .Where(item => item.Score > 0)
                .OrderByDescending(item => item.Score)
                .ThenByDescending(item => item.Window.IsForeground)
                .ThenBy(item => item.Window.Title, StringComparer.OrdinalIgnoreCase)
                .Select(item => item.Window);
        }
        else
        {
            filtered = windows
                .OrderByDescending(window => window.IsForeground)
                .ThenBy(window => window.Title, StringComparer.OrdinalIgnoreCase);
        }

        var matches = filtered.Take(20).ToArray();

        if (matches.Length == 0)
        {
            return Task.FromResult(
                string.IsNullOrWhiteSpace(target)
                    ? "No visible desktop windows were found."
                    : $"No visible windows matched '{target.Trim()}'.");
        }

        var lines = new List<string>
        {
            string.IsNullOrWhiteSpace(target)
                ? "Open windows:"
                : $"Open windows matching '{target.Trim()}':"
        };

        foreach (var window in matches)
        {
            var bounds = DescribeBounds(window.Bounds);
            var active = window.IsForeground ? "[active] " : string.Empty;
            lines.Add($"- {active}{DescribeWindow(window)} | {window.ProcessName} | {bounds}");
        }

        if (filtered.Skip(matches.Length).Any())
        {
            lines.Add("- Additional windows omitted from the preview.");
        }

        return Task.FromResult(string.Join(Environment.NewLine, lines));
    }

    // Tool: move a window to x,y. It is restored first (a maximised window cannot be moved), and the size is kept (at least 120 pixels).
    public static Task<string> MoveWindowAsync(string targetApp, int x, int y)
    {
        if (!TryResolveWindow(targetApp, out var window))
        {
            return Task.FromResult($"Window matching '{targetApp}' was not found.");
        }

        var width = Math.Max(120, window.Bounds.Right - window.Bounds.Left);
        var height = Math.Max(120, window.Bounds.Bottom - window.Bounds.Top);

        _ = ShowWindowAsync(window.Handle, SwRestore);

        if (!MoveWindow(window.Handle, x, y, width, height, true))
        {
            return Task.FromResult($"Unable to move '{DescribeWindow(window)}'.");
        }

        _ = TryBringWindowToFront(window.Handle);
        return Task.FromResult($"Moved '{DescribeWindow(window)}' to {x},{y} with size {width}x{height}.");
    }

    // Tool: resize a window, keeping its top-left corner. Sizes are clamped to at least 160 x 120 so a window cannot vanish.
    public static Task<string> ResizeWindowAsync(string targetApp, int width, int height)
    {
        if (!TryResolveWindow(targetApp, out var window))
        {
            return Task.FromResult($"Window matching '{targetApp}' was not found.");
        }

        width = Math.Max(160, width);
        height = Math.Max(120, height);

        _ = ShowWindowAsync(window.Handle, SwRestore);

        if (!MoveWindow(window.Handle, window.Bounds.Left, window.Bounds.Top, width, height, true))
        {
            return Task.FromResult($"Unable to resize '{DescribeWindow(window)}'.");
        }

        _ = TryBringWindowToFront(window.Handle);
        return Task.FromResult($"Resized '{DescribeWindow(window)}' to {width}x{height}.");
    }

    // Tool: snap a window to a part of the screen of the monitor it is on (halves, corners, centre), like Windows' snap but exact.
    public static Task<string> SnapWindowAsync(string targetApp, string position)
    {
        if (!TryResolveWindow(targetApp, out var window))
        {
            return Task.FromResult($"Window matching '{targetApp}' was not found.");
        }

        if (!TryResolveSnapBounds(window.Handle, position, out var normalizedPosition, out var x, out var y, out var width, out var height))
        {
            return Task.FromResult("Usage: snap window <window> <left|right|top|bottom|top-left|top-right|bottom-left|bottom-right|center>");
        }

        _ = ShowWindowAsync(window.Handle, SwRestore);

        if (!MoveWindow(window.Handle, x, y, width, height, true))
        {
            return Task.FromResult($"Unable to snap '{DescribeWindow(window)}'.");
        }

        _ = TryBringWindowToFront(window.Handle);
        return Task.FromResult($"Snapped '{DescribeWindow(window)}' to {normalizedPosition} ({width}x{height} @ {x},{y}).");
    }

    // Tool: press a media key one or more times (up to 10, so "volume up 5" works).
    public static Task<string> ControlMediaAsync(string command)
    {
        if (!TryResolveMediaCommand(command, out var virtualKey, out var count, out var description))
        {
            return Task.FromResult("Usage: media <play|pause|toggle|stop|next|previous|volume up|volume down|mute> [count]");
        }

        for (var index = 0; index < count; index++)
        {
            if (!TrySendVirtualKey(virtualKey, out var error))
            {
                return Task.FromResult(error);
            }
        }

        var response = count == 1
            ? $"Sent media command: {description}."
            : $"Sent media command: {description} {count} times.";
        return Task.FromResult(response);
    }

    // Tool: read the clipboard text for the model, truncated at 4000 characters.
    public static Task<string> GetClipboardTextAsync()
    {
        if (!TryReadClipboardText(out var text, out var error))
        {
            return Task.FromResult(error);
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return Task.FromResult("Clipboard is empty or does not contain text.");
        }

        var preview = text.Length <= ClipboardPreviewLimit
            ? text
            : text[..ClipboardPreviewLimit].TrimEnd() + Environment.NewLine + "[clipboard text truncated]";
        return Task.FromResult($"Clipboard text ({text.Length} chars):{Environment.NewLine}{preview}");
    }

    // Tool: put text on the clipboard.
    public static Task<string> SetClipboardTextAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Task.FromResult("Usage: set clipboard <text>");
        }

        return Task.FromResult(
            TryWriteClipboardText(text, out var error)
                ? $"Copied {DescribeTextLength(text)} to the clipboard."
                : error);
    }

    // Tool: empty the clipboard.
    public static Task<string> ClearClipboardAsync()
    {
        return Task.FromResult(
            TryClearClipboard(out var error)
                ? "Cleared the clipboard."
                : error);
    }

    // Reads clipboard text for other code in the project (clipboard history), without the wording of the tool reply.
    public static bool TryGetClipboardText(out string text, out string error)
    {
        return TryReadClipboardText(out text, out error);
    }

    // Tool: paste into a window by focusing it and sending Ctrl+V. The 120 ms wait lets focus settle first.
    public static async Task<string> PasteClipboardAsync(string targetApp)
    {
        if (!TryResolveWindow(targetApp, out var window))
        {
            return $"Window matching '{targetApp}' was not found.";
        }

        if (!TryBringWindowToFront(window.Handle))
        {
            return $"Unable to focus '{DescribeWindow(window)}' before pasting.";
        }

        await Task.Delay(120).ConfigureAwait(false);

        if (!TryParseHotkey("ctrl+v", out var pasteHotkey, out var parseError))
        {
            return parseError;
        }

        if (!TrySendHotkey(pasteHotkey, out var sendError))
        {
            return sendError;
        }

        return $"Pasted clipboard contents into '{DescribeWindow(window)}'.";
    }

    // Collects all visible top-level windows that have a title and a known program. Windows without a title (hidden helper windows) are skipped.
    private static IReadOnlyList<WindowMatch> EnumerateTopLevelWindows()
    {
        var windows = new List<WindowMatch>();
        var foreground = GetForegroundWindow();

        // EnumWindows calls this lambda for each window; returning true tells Windows to keep going.
        _ = EnumWindows(
            (handle, _) =>
            {
                if (handle == IntPtr.Zero || !IsWindowVisible(handle))
                {
                    return true;
                }

                var title = ReadWindowText(handle);

                if (string.IsNullOrWhiteSpace(title) || !GetWindowRect(handle, out var bounds))
                {
                    return true;
                }

                var processName = TryGetProcessName(handle);

                if (string.IsNullOrWhiteSpace(processName))
                {
                    return true;
                }

                windows.Add(new WindowMatch(handle, title, processName, bounds, handle == foreground));
                return true;
            },
            IntPtr.Zero);

        return windows;
    }

    // Looks up which program owns a window; empty when Windows will not tell us (for example, protected system processes).
    private static string TryGetProcessName(IntPtr handle)
    {
        try
        {
            _ = GetWindowThreadProcessId(handle, out var processId);

            if (processId == 0)
            {
                return string.Empty;
            }

            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch
        {
            return string.Empty;
        }
    }

    // Works out the screen rectangle for a snap position on the window's monitor, using the work area so it never covers the taskbar. Half sizes have minimums so tiny screens still work.
    private static bool TryResolveSnapBounds(
        IntPtr handle,
        string position,
        out string normalizedPosition,
        out int x,
        out int y,
        out int width,
        out int height)
    {
        normalizedPosition = NormalizeSnapPosition(position);
        x = 0;
        y = 0;
        width = 0;
        height = 0;

        if (string.IsNullOrWhiteSpace(normalizedPosition) || !TryGetMonitorWorkArea(handle, out var workArea))
        {
            return false;
        }

        var fullWidth = Math.Max(320, workArea.Right - workArea.Left);
        var fullHeight = Math.Max(240, workArea.Bottom - workArea.Top);
        var halfWidth = Math.Max(200, fullWidth / 2);
        var halfHeight = Math.Max(160, fullHeight / 2);

        switch (normalizedPosition)
        {
            case "left":
                x = workArea.Left;
                y = workArea.Top;
                width = halfWidth;
                height = fullHeight;
                return true;
            case "right":
                x = workArea.Right - halfWidth;
                y = workArea.Top;
                width = halfWidth;
                height = fullHeight;
                return true;
            case "top":
                x = workArea.Left;
                y = workArea.Top;
                width = fullWidth;
                height = halfHeight;
                return true;
            case "bottom":
                x = workArea.Left;
                y = workArea.Bottom - halfHeight;
                width = fullWidth;
                height = halfHeight;
                return true;
            case "top-left":
                x = workArea.Left;
                y = workArea.Top;
                width = halfWidth;
                height = halfHeight;
                return true;
            case "top-right":
                x = workArea.Right - halfWidth;
                y = workArea.Top;
                width = halfWidth;
                height = halfHeight;
                return true;
            case "bottom-left":
                x = workArea.Left;
                y = workArea.Bottom - halfHeight;
                width = halfWidth;
                height = halfHeight;
                return true;
            case "bottom-right":
                x = workArea.Right - halfWidth;
                y = workArea.Bottom - halfHeight;
                width = halfWidth;
                height = halfHeight;
                return true;
            // "center" is 80% of the work area (at least 420 x 320) centred on screen.
            case "center":
                width = Math.Max(420, (int)Math.Round(fullWidth * 0.8));
                height = Math.Max(320, (int)Math.Round(fullHeight * 0.8));
                x = workArea.Left + Math.Max(0, (fullWidth - width) / 2);
                y = workArea.Top + Math.Max(0, (fullHeight - height) / 2);
                return true;
            default:
                return false;
        }
    }

    // Gets the usable area of the monitor the window is on.
    private static bool TryGetMonitorWorkArea(IntPtr handle, out RECT workArea)
    {
        workArea = default;
        var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);

        if (monitor == IntPtr.Zero)
        {
            return false;
        }

        var monitorInfo = new MONITORINFO
        {
            cbSize = Marshal.SizeOf<MONITORINFO>()
        };

        if (!GetMonitorInfo(monitor, ref monitorInfo))
        {
            return false;
        }

        workArea = monitorInfo.rcWork;
        return true;
    }

    // Accepts spoken variants ("upper left", "dock left", "middle") and returns the standard position name, or empty if unknown.
    private static string NormalizeSnapPosition(string position)
    {
        var normalized = NormalizeForMatching(position);

        return normalized switch
        {
            "left" or "snap left" or "dock left" => "left",
            "right" or "snap right" or "dock right" => "right",
            "top" or "up" => "top",
            "bottom" or "down" => "bottom",
            "top left" or "upper left" => "top-left",
            "top right" or "upper right" => "top-right",
            "bottom left" or "lower left" => "bottom-left",
            "bottom right" or "lower right" => "bottom-right",
            "center" or "middle" => "center",
            _ => string.Empty
        };
    }

    // Understands media words ("next", "louder", "volume up 3"). A trailing number is the repeat count, limited to 1..10; returns the key code and a description for the reply.
    private static bool TryResolveMediaCommand(string command, out ushort virtualKey, out int count, out string description)
    {
        var normalized = NormalizeForMatching(command);
        count = 1;

        if (string.IsNullOrWhiteSpace(normalized))
        {
            virtualKey = 0;
            description = string.Empty;
            return false;
        }

        var parts = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();

        if (parts.Count > 1
            && int.TryParse(parts[^1], out var parsedCount))
        {
            count = Math.Clamp(parsedCount, 1, 10);
            parts.RemoveAt(parts.Count - 1);
            normalized = string.Join(' ', parts);
        }

        switch (normalized)
        {
            case "play":
            case "pause":
            case "resume":
            case "toggle":
            case "play pause":
            case "toggle playback":
                virtualKey = VkMediaPlayPause;
                description = "play/pause";
                return true;
            case "stop":
                virtualKey = VkMediaStop;
                description = "stop";
                return true;
            case "next":
            case "next track":
            case "skip":
            case "skip track":
                virtualKey = VkMediaNextTrack;
                description = "next track";
                return true;
            case "previous":
            case "previous track":
            case "prev":
            case "prev track":
            case "back track":
                virtualKey = VkMediaPrevTrack;
                description = "previous track";
                return true;
            case "volume up":
            case "louder":
                virtualKey = VkVolumeUp;
                description = "volume up";
                return true;
            case "volume down":
            case "quieter":
                virtualKey = VkVolumeDown;
                description = "volume down";
                return true;
            case "mute":
            case "toggle mute":
            case "mute audio":
                virtualKey = VkVolumeMute;
                description = "mute";
                return true;
            default:
                virtualKey = 0;
                description = string.Empty;
                return false;
        }
    }

    // Presses and releases one key through SendInput (the real keyboard path, which the media keys need).
    private static bool TrySendVirtualKey(ushort virtualKey, out string error)
    {
        var inputs = new[]
        {
            CreateKeyboardInput(virtualKey, keyUp: false),
            CreateKeyboardInput(virtualKey, keyUp: true)
        };

        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());

        if (sent != inputs.Length)
        {
            error = $"Windows only dispatched {sent} of {inputs.Length} key events.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    // Reads the clipboard text. The clipboard must be opened, the data handle locked to get a real pointer, copied as a Unicode string, then unlocked and the clipboard closed.
    // If the clipboard has no text, it returns true with empty text rather than an error.
    private static bool TryReadClipboardText(out string text, out string error)
    {
        text = string.Empty;

        if (!TryOpenClipboard(out error))
        {
            return false;
        }

        try
        {
            if (!IsClipboardFormatAvailable(CfUnicodeText))
            {
                return true;
            }

            var handle = GetClipboardData(CfUnicodeText);

            if (handle == IntPtr.Zero)
            {
                error = "Clipboard text is currently unavailable.";
                return false;
            }

            var pointer = GlobalLock(handle);

            if (pointer == IntPtr.Zero)
            {
                error = "Unable to read clipboard text.";
                return false;
            }

            try
            {
                text = Marshal.PtrToStringUni(pointer) ?? string.Empty;
                return true;
            }
            finally
            {
                _ = GlobalUnlock(handle);
            }
        }
        finally
        {
            _ = CloseClipboard();
        }
    }

    // Writes text to the clipboard. The text (plus a terminating zero) is copied into a movable global memory block which is then handed to Windows with SetClipboardData.
    // After a successful hand-over Windows owns the memory, so globalHandle is cleared to avoid freeing it; otherwise the finally block frees it.
    private static bool TryWriteClipboardText(string text, out string error)
    {
        error = string.Empty;

        if (!TryOpenClipboard(out error))
        {
            return false;
        }

        IntPtr globalHandle = IntPtr.Zero;

        try
        {
            if (!EmptyClipboard())
            {
                error = "Unable to clear the clipboard before writing.";
                return false;
            }

            var bytes = Encoding.Unicode.GetBytes(text + '\0');
            globalHandle = GlobalAlloc(GmemMoveable, (UIntPtr)bytes.Length);

            if (globalHandle == IntPtr.Zero)
            {
                error = "Unable to allocate clipboard memory.";
                return false;
            }

            var target = GlobalLock(globalHandle);

            if (target == IntPtr.Zero)
            {
                error = "Unable to lock clipboard memory.";
                return false;
            }

            try
            {
                Marshal.Copy(bytes, 0, target, bytes.Length);
            }
            finally
            {
                _ = GlobalUnlock(globalHandle);
            }

            if (SetClipboardData(CfUnicodeText, globalHandle) == IntPtr.Zero)
            {
                error = "Unable to update clipboard text.";
                return false;
            }

            globalHandle = IntPtr.Zero;
            return true;
        }
        finally
        {
            if (globalHandle != IntPtr.Zero)
            {
                _ = GlobalFree(globalHandle);
            }

            _ = CloseClipboard();
        }
    }

    // Empties the clipboard.
    private static bool TryClearClipboard(out string error)
    {
        if (!TryOpenClipboard(out error))
        {
            return false;
        }

        try
        {
            if (!EmptyClipboard())
            {
                error = "Unable to clear the clipboard.";
                return false;
            }

            return true;
        }
        finally
        {
            _ = CloseClipboard();
        }
    }

    // Opens the clipboard, retrying up to 5 times 40 ms apart, because another program may be using it for a moment.
    private static bool TryOpenClipboard(out string error)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                error = string.Empty;
                return true;
            }

            Thread.Sleep(40);
        }

        error = "Unable to access the clipboard right now.";
        return false;
    }

    // Text like "800x600 @ 100,50" (size @ top-left position).
    private static string DescribeBounds(RECT bounds)
    {
        var width = Math.Max(0, bounds.Right - bounds.Left);
        var height = Math.Max(0, bounds.Bottom - bounds.Top);
        return $"{width}x{height} @ {bounds.Left},{bounds.Top}";
    }
}
