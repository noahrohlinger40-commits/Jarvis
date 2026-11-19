// Win32UiAutomation.Advanced.cs: second slice of Win32UiAutomation. It lists windows and sends media keys (play, volume...).
// Called from DesktopAutomationService (DesktopAutomation.cs); it reuses the window matching, key-sending and name helpers from Win32UiAutomation.cs.
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Jarvis.Core;

internal static partial class Win32UiAutomation
{
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

    // GetForegroundWindow: the window the user is currently using.
    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    // GetWindowThreadProcessId: given a window, returns the id of the process that owns it.
    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

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

                var processName = TryGetProcessName(handle, out var ownerId);

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

    // Looks up which program owns a window and its process id; empty name when Windows will not tell us (for example, protected system processes).
    internal static string TryGetProcessName(IntPtr handle, out int processId)
    {
        processId = 0;

        try
        {
            _ = GetWindowThreadProcessId(handle, out var rawProcessId);

            if (rawProcessId == 0)
            {
                return string.Empty;
            }

            using var process = Process.GetProcessById((int)rawProcessId);
            processId = (int)rawProcessId;
            return process.ProcessName;
        }
        catch
        {
            return string.Empty;
        }
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

    // Text like "800x600 @ 100,50" (size @ top-left position).
    private static string DescribeBounds(RECT bounds)
    {
        var width = Math.Max(0, bounds.Right - bounds.Left);
        var height = Math.Max(0, bounds.Bottom - bounds.Top);
        return $"{width}x{height} @ {bounds.Left},{bounds.Top}";
    }
}
