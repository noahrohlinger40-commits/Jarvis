// AppIntegrationTools.cs: tools that control specific desktop apps (Spotify, VS Code, Chrome, Discord).
// Each tool follows the same three steps: a parser in AppIntegrationParsing turns the typed command into an AppIntegrationCommand
// (an Action name plus a text Payload), the tool's switch picks a helper for that action, and AppIntegrationHelpers does the work by
// calling context.DesktopAutomation (focus a window, send a keyboard shortcut, send text, launch a program).
// Most actions are therefore simulated keystrokes into the real app, with short fixed pauses between steps; there is no app-specific API.
// ActionSafety.cs checks the same parsed commands to decide when to ask first (sensitive text).
using static Jarvis.Core.Win32UiAutomation;

namespace Jarvis.Core;

// Spotify: open or focus the app, show status, play/pause/skip/volume, or search. A bare "spotify <words>" is treated as a search.
public sealed class SpotifyTool : IAssistantTool
{
    public string Name => "spotify";

    public string Description => "Spotify adapter. Use `spotify`, `spotify status`, `spotify play`, `spotify next`, or `spotify search <query>`.";

    public bool CanHandle(string input) => AppIntegrationParsing.TryParseSpotify(input, out _);

    public async Task<ToolResult> ExecuteAsync(string input, ToolContext context, CancellationToken cancellationToken)
    {
        if (!AppIntegrationParsing.TryParseSpotify(input, out var command))
        {
            return new ToolResult("Usage: spotify [status|play|pause|next|previous|search <query>|playlist <name>]");
        }

        string result;

        // Each case is an action name produced by AppIntegrationParsing.TryParseSpotify.
        switch (command.Action)
        {
            case "open":
                result = await AppIntegrationHelpers.OpenOrFocusAsync("spotify", context, cancellationToken);
                break;
            case "status":
                result = await AppIntegrationHelpers.GetAppStatusAsync("Spotify", ["spotify"], context, cancellationToken);
                break;
            case "media":
                result = await context.DesktopAutomation.ControlMediaAsync(command.Payload, cancellationToken);
                break;
            case "search":
            {
                // A search term that matches a playlist Jarvis has learned is replaced by the playlist's name; then a spotify:search: link is opened.
                var query = await AppIntegrationHelpers.ResolveSpotifyQuery(command.Payload, context, cancellationToken);
                result = await context.DesktopAutomation.OpenApplicationAsync(
                    $"spotify:search:{Uri.EscapeDataString(query)}",
                    cancellationToken);
                break;
            }
            default:
                result = "Usage: spotify [status|play|pause|next|previous|search <query>|playlist <name>]";
                break;
        }

        var success = !UiCommandParsing.LooksLikeFailure(result);
        return new ToolResult(result, VerificationText: result, SummaryText: success ? "Handled Spotify action." : "Spotify action failed.");
    }
}

// VS Code: open or focus, open a file or folder, jump to file:line:column, or send the search / command-palette shortcut and type into it.
public sealed class CodeTool : IAssistantTool
{
    public string Name => "code";

    public string Description => "VS Code adapter. Use `code`, `code status`, `code open <path>`, `code goto <file:line[:col]>`, `code search <query>`, or `code command <query>`.";

    public bool CanHandle(string input) => AppIntegrationParsing.TryParseCode(input, out _);

    public async Task<ToolResult> ExecuteAsync(string input, ToolContext context, CancellationToken cancellationToken)
    {
        if (!AppIntegrationParsing.TryParseCode(input, out var command))
        {
            return new ToolResult("Usage: code [status|open <path>|file <path>|folder <path>|workspace <path>|goto <file:line[:col]>|search <query>|command <query>]");
        }

        string result;

        // Each case is an action name produced by AppIntegrationParsing.TryParseCode.
        switch (command.Action)
        {
            case "open":
                result = await AppIntegrationHelpers.OpenOrFocusAsync("code", context, cancellationToken);
                break;
            case "status":
                result = await AppIntegrationHelpers.GetAppStatusAsync("VS Code", ["code", "visual studio code"], context, cancellationToken);
                break;
            case "open-path":
            {
                var resolvedPath = AppIntegrationHelpers.ResolvePath(command.Payload, context.WorkspaceRoot);
                result = await context.DesktopAutomation.LaunchApplicationAsync("code", $"\"{resolvedPath}\"", cancellationToken);
                break;
            }
            case "goto":
            {
                var gotoTarget = AppIntegrationHelpers.ResolveCodeGotoTarget(command.Payload, context.WorkspaceRoot);
                result = await context.DesktopAutomation.LaunchApplicationAsync("code", $"--goto \"{gotoTarget}\"", cancellationToken);
                break;
            }
            case "search":
                result = await AppIntegrationHelpers.RunTextEntryWorkflowAsync("code", "ctrl+shift+f", command.Payload, context, cancellationToken);
                break;
            case "command":
                result = await AppIntegrationHelpers.RunTextEntryWorkflowAsync("code", "ctrl+shift+p", command.Payload, context, cancellationToken);
                break;
            default:
                result = "Usage: code [status|open <path>|file <path>|folder <path>|workspace <path>|goto <file:line[:col]>|search <query>|command <query>]";
                break;
        }

        var success = !UiCommandParsing.LooksLikeFailure(result);
        return new ToolResult(result, VerificationText: result, SummaryText: success ? "Handled VS Code action." : "VS Code action failed.");
    }
}

// Chrome: normal browser control by keyboard shortcut and launch flags.
public sealed class ChromeTool : IAssistantTool
{
    // Help text shown for a bad Chrome command.
    private const string Usage = "Usage: chrome [status|open <url-or-query>|search <query>|go <url>|new window [target]|new tab [target]|incognito [target]|back|forward|reload|close tab|reopen tab|duplicate tab|next tab|previous tab|history|downloads|bookmarks|devtools|find <query>|submit]";

    // Actions that are just one Chrome shortcut: the key combo to press and the line reported back.
    private static readonly Dictionary<string, (string Hotkey, string Message)> HotkeyActions = new()
    {
        ["back"] = ("alt+left", "Moved back in Chrome."),
        ["forward"] = ("alt+right", "Moved forward in Chrome."),
        ["reload"] = ("ctrl+r", "Reloaded the current Chrome tab."),
        ["close-tab"] = ("ctrl+w", "Closed the current Chrome tab."),
        ["reopen-tab"] = ("ctrl+shift+t", "Reopened the last closed Chrome tab."),
        ["next-tab"] = ("ctrl+tab", "Moved to the next Chrome tab."),
        ["previous-tab"] = ("ctrl+shift+tab", "Moved to the previous Chrome tab."),
        ["devtools"] = ("ctrl+shift+i", "Opened Chrome DevTools."),
        ["focus-address-bar"] = ("ctrl+l", "Focused the Chrome address bar."),
        ["submit"] = ("enter", "Submitted the active Chrome form.")
    };

    public string Name => "chrome";

    public string Description => "Chrome adapter for navigation, tabs, history, downloads, bookmarks and find-on-page, using keyboard shortcuts.";

    public bool CanHandle(string input) => AppIntegrationParsing.TryParseChrome(input, out _);

    public async Task<ToolResult> ExecuteAsync(string input, ToolContext context, CancellationToken cancellationToken)
    {
        if (!AppIntegrationParsing.TryParseChrome(input, out var command))
        {
            return new ToolResult(Usage);
        }

        string result;

        // Each case is an action name produced by AppIntegrationParsing.TryParseChrome.
        switch (command.Action)
        {
            case "open":
                result = await AppIntegrationHelpers.OpenOrFocusAsync("chrome", context, cancellationToken);
                break;
            case "status":
                result = await AppIntegrationHelpers.GetAppStatusAsync("Chrome", ["chrome", "google chrome"], context, cancellationToken);
                break;
            case "open-url":
            {
                var target = AppIntegrationHelpers.NormalizeBrowserTarget(command.Payload);
                result = await context.DesktopAutomation.LaunchApplicationAsync("chrome", target, cancellationToken);
                break;
            }
            case "new-window":
            {
                var target = AppIntegrationHelpers.NormalizeBrowserTarget(command.Payload);
                result = await context.DesktopAutomation.LaunchApplicationAsync("chrome", $"--new-window {target}", cancellationToken);
                break;
            }
            case "incognito":
            {
                var target = AppIntegrationHelpers.NormalizeBrowserTarget(command.Payload);
                result = await context.DesktopAutomation.LaunchApplicationAsync("chrome", $"--incognito {target}", cancellationToken);
                break;
            }
            case "navigate-current":
                result = await AppIntegrationHelpers.NavigateBrowserAsync("chrome", command.Payload, openNewTab: false, context, cancellationToken);
                break;
            case "new-tab":
                result = await AppIntegrationHelpers.NavigateBrowserAsync("chrome", command.Payload, openNewTab: true, context, cancellationToken);
                break;
            case "duplicate-tab":
                result = await AppIntegrationHelpers.DuplicateBrowserTabAsync("chrome", context, cancellationToken);
                break;
            case "history":
                result = await AppIntegrationHelpers.NavigateBrowserAsync("chrome", "chrome://history", openNewTab: false, context, cancellationToken);
                break;
            case "downloads":
                result = await AppIntegrationHelpers.NavigateBrowserAsync("chrome", "chrome://downloads", openNewTab: false, context, cancellationToken);
                break;
            case "bookmarks":
                result = await AppIntegrationHelpers.NavigateBrowserAsync("chrome", "chrome://bookmarks", openNewTab: false, context, cancellationToken);
                break;
            case "find":
                result = await AppIntegrationHelpers.RunBrowserFindAsync("chrome", command.Payload, context, cancellationToken);
                break;
            default:
                result = HotkeyActions.TryGetValue(command.Action, out var hotkeyAction)
                    ? await AppIntegrationHelpers.RunBrowserHotkeyAsync("chrome", hotkeyAction.Hotkey, context, cancellationToken, hotkeyAction.Message)
                    : Usage;
                break;
        }

        var success = !UiCommandParsing.LooksLikeFailure(result);
        return new ToolResult(result, VerificationText: result, SummaryText: success ? "Handled Chrome action." : "Chrome action failed.");
    }
}

// Discord: open or focus, status, and two shortcut-driven searches: the quick switcher (Ctrl+K) to jump to a channel or person, and message search (Ctrl+F).
public sealed class DiscordTool : IAssistantTool
{
    // The two Discord searches differ only in the shortcut that opens the box: Ctrl+K is the quick switcher (jump), Ctrl+F is message search.
    private static readonly Dictionary<string, string> SearchHotkeys = new() { ["jump"] = "ctrl+k", ["search"] = "ctrl+f" };

    public string Name => "discord";

    public string Description => "Discord adapter. Use `discord`, `discord status`, `discord jump <query>`, or `discord search <query>`.";

    public bool CanHandle(string input) => AppIntegrationParsing.TryParseDiscord(input, out _);

    public async Task<ToolResult> ExecuteAsync(string input, ToolContext context, CancellationToken cancellationToken)
    {
        if (!AppIntegrationParsing.TryParseDiscord(input, out var command))
        {
            return new ToolResult("Usage: discord [status|jump <query>|search <query>]");
        }

        string result;

        // Each case is an action name produced by AppIntegrationParsing.TryParseDiscord.
        switch (command.Action)
        {
            case "open":
                result = await AppIntegrationHelpers.OpenOrFocusAsync("discord", context, cancellationToken);
                break;
            case "status":
                result = await AppIntegrationHelpers.GetAppStatusAsync("Discord", ["discord"], context, cancellationToken);
                break;
            default:
                result = SearchHotkeys.TryGetValue(command.Action, out var hotkey)
                    ? await AppIntegrationHelpers.RunTextEntryWorkflowAsync("discord", hotkey, command.Payload, context, cancellationToken)
                    : "Usage: discord [status|jump <query>|search <query>]";
                break;
        }

        var success = !UiCommandParsing.LooksLikeFailure(result);
        return new ToolResult(result, VerificationText: result, SummaryText: success ? "Handled Discord action." : "Discord action failed.");
    }
}

// A parsed app command: Action is a short name such as "open", "search" or "jump"; Payload is the remaining text.
internal sealed record AppIntegrationCommand(string Action, string Payload = "");

// Shared building blocks for the app tools above. Every step reports back as text, and UiCommandParsing.LooksLikeFailure decides whether that text means failure.
internal static class AppIntegrationHelpers
{
    // Brings the app forward, launching it if needed. Tries in order: already the active app, visible in the window list, focus request, then launch.
    public static async Task<string> OpenOrFocusAsync(string appTarget, ToolContext context, CancellationToken cancellationToken)
    {
        var displayName = ResolveFriendlyAppName(appTarget);
        var matchTerms = ResolveMatchTerms(appTarget, displayName);
        var snapshot = await context.DesktopAutomation.GetContextSnapshotAsync(cancellationToken);
        var active = MatchesAny(snapshot.ActiveApplication, matchTerms) || MatchesAny(snapshot.ActiveWindow, matchTerms);

        if (active)
        {
            return $"{displayName} is already active.";
        }

        // Look for the app among the visible windows.
        var visible = snapshot.VisibleApplications.Where(value => MatchesAny(value, matchTerms)).Take(2).ToArray();

        if (visible.Length > 0)
        {
            var visibleFocusResult = await context.DesktopAutomation.FocusApplicationAsync(appTarget, cancellationToken);

            if (!UiCommandParsing.LooksLikeFailure(visibleFocusResult))
            {
                return $"{displayName} is already open. Bringing it to the foreground.";
            }
        }

        // Not seen in the window list: still try to focus it, in case it is running with a different window name.
        var focusResult = await context.DesktopAutomation.FocusApplicationAsync(appTarget, cancellationToken);

        if (!UiCommandParsing.LooksLikeFailure(focusResult))
        {
            return $"{displayName} is already running. Bringing it forward.";
        }

        // Not running: launch it.
        var openResult = await context.DesktopAutomation.OpenApplicationAsync(appTarget, cancellationToken);

        if (UiCommandParsing.LooksLikeFailure(openResult))
        {
            return openResult;
        }

        // If launching actually just focused a running copy, say so.
        return openResult.StartsWith("Focused application:", StringComparison.OrdinalIgnoreCase)
            ? $"{displayName} is already running. Bringing it forward."
            : $"Launching {displayName}.";
    }

    // Reports whether an app is active or visible, using the desktop snapshot and a few name terms to match window and app names.
    public static async Task<string> GetAppStatusAsync(
        string displayName,
        string[] matchTerms,
        ToolContext context,
        CancellationToken cancellationToken)
    {
        var snapshot = await context.DesktopAutomation.GetContextSnapshotAsync(cancellationToken);
        var active = MatchesAny(snapshot.ActiveApplication, matchTerms) || MatchesAny(snapshot.ActiveWindow, matchTerms);
        var visible = snapshot.VisibleApplications.Where(value => MatchesAny(value, matchTerms)).Take(4).ToArray();

        if (visible.Length == 0)
        {
            return $"{displayName} is not visible right now. Active app: {OrUnavailable(snapshot.ActiveApplication)}.";
        }

        var lines = new List<string>
        {
            $"{displayName} status:",
            $"- Active now: {(active ? "yes" : "no")}",
            $"- Visible windows: {string.Join(" | ", visible)}"
        };

        if (!string.IsNullOrWhiteSpace(snapshot.ActiveWindow))
        {
            lines.Add($"- Foreground window: {snapshot.ActiveWindow}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    // Focuses the app, presses a shortcut that opens its search box (for example Ctrl+K), then types the text. Stops at the first failed step.
    public static async Task<string> RunTextEntryWorkflowAsync(
        string appTarget,
        string hotkey,
        string text,
        ToolContext context,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "A search or jump query is required.";
        }

        var openResult = await OpenOrFocusAsync(appTarget, context, cancellationToken);

        if (UiCommandParsing.LooksLikeFailure(openResult))
        {
            return openResult;
        }

        // Short fixed pauses (seconds) between steps give the app time to react to the previous one.
        await context.DesktopAutomation.WaitAsync(0.35, cancellationToken);

        var hotkeyResult = await context.DesktopAutomation.SendHotkeyAsync(appTarget, hotkey, cancellationToken);

        if (UiCommandParsing.LooksLikeFailure(hotkeyResult))
        {
            return hotkeyResult;
        }

        await context.DesktopAutomation.WaitAsync(0.2, cancellationToken);

        var textResult = await context.DesktopAutomation.SendTextAsync(appTarget, text, cancellationToken);

        if (UiCommandParsing.LooksLikeFailure(textResult))
        {
            return textResult;
        }

        return $"{openResult}{Environment.NewLine}{hotkeyResult}{Environment.NewLine}{textResult}";
    }

    // Focuses a browser and presses one keyboard shortcut.
    public static async Task<string> RunBrowserHotkeyAsync(
        string appTarget,
        string hotkey,
        ToolContext context,
        CancellationToken cancellationToken,
        string successSummary)
    {
        var openResult = await OpenOrFocusAsync(appTarget, context, cancellationToken);

        if (UiCommandParsing.LooksLikeFailure(openResult))
        {
            return openResult;
        }

        await context.DesktopAutomation.WaitAsync(0.25, cancellationToken);

        var hotkeyResult = await context.DesktopAutomation.SendHotkeyAsync(appTarget, hotkey, cancellationToken);

        if (UiCommandParsing.LooksLikeFailure(hotkeyResult))
        {
            return hotkeyResult;
        }

        return string.Join(
            Environment.NewLine,
            openResult,
            hotkeyResult,
            successSummary);
    }

    // Focuses the browser, optionally opens a new tab (Ctrl+T), then focuses the address bar (Ctrl+L), types the address and presses Enter.
    public static async Task<string> NavigateBrowserAsync(
        string appTarget,
        string target,
        bool openNewTab,
        ToolContext context,
        CancellationToken cancellationToken)
    {
        var openResult = await OpenOrFocusAsync(appTarget, context, cancellationToken);

        if (UiCommandParsing.LooksLikeFailure(openResult))
        {
            return openResult;
        }

        await context.DesktopAutomation.WaitAsync(0.3, cancellationToken);

        if (openNewTab)
        {
            var newTabResult = await context.DesktopAutomation.SendHotkeyAsync(appTarget, "ctrl+t", cancellationToken);

            if (UiCommandParsing.LooksLikeFailure(newTabResult))
            {
                return newTabResult;
            }

            if (string.IsNullOrWhiteSpace(target))
            {
                return string.Join(Environment.NewLine, openResult, newTabResult, "Opened a new Chrome tab.");
            }
        }

        if (string.IsNullOrWhiteSpace(target))
        {
            return openResult;
        }

        var addressResult = await context.DesktopAutomation.SendHotkeyAsync(appTarget, "ctrl+l", cancellationToken);

        if (UiCommandParsing.LooksLikeFailure(addressResult))
        {
            return addressResult;
        }

        await context.DesktopAutomation.WaitAsync(0.15, cancellationToken);

        var textResult = await context.DesktopAutomation.SendTextAsync(appTarget, NormalizeBrowserTarget(target), cancellationToken);

        if (UiCommandParsing.LooksLikeFailure(textResult))
        {
            return textResult;
        }

        await context.DesktopAutomation.WaitAsync(0.1, cancellationToken);

        var enterResult = await context.DesktopAutomation.SendHotkeyAsync(appTarget, "enter", cancellationToken);

        if (UiCommandParsing.LooksLikeFailure(enterResult))
        {
            return enterResult;
        }

        return string.Join(
            Environment.NewLine,
            openResult,
            addressResult,
            textResult,
            enterResult,
            openNewTab ? "Opened a new Chrome tab and navigated it." : "Navigated the current Chrome tab.");
    }

    // Duplicates the current tab: focus the address bar, then Alt+Enter, which opens the address in a new tab.
    public static async Task<string> DuplicateBrowserTabAsync(
        string appTarget,
        ToolContext context,
        CancellationToken cancellationToken)
    {
        var openResult = await OpenOrFocusAsync(appTarget, context, cancellationToken);

        if (UiCommandParsing.LooksLikeFailure(openResult))
        {
            return openResult;
        }

        await context.DesktopAutomation.WaitAsync(0.25, cancellationToken);

        var addressResult = await context.DesktopAutomation.SendHotkeyAsync(appTarget, "ctrl+l", cancellationToken);

        if (UiCommandParsing.LooksLikeFailure(addressResult))
        {
            return addressResult;
        }

        await context.DesktopAutomation.WaitAsync(0.1, cancellationToken);

        var duplicateResult = await context.DesktopAutomation.SendHotkeyAsync(appTarget, "alt+enter", cancellationToken);

        if (UiCommandParsing.LooksLikeFailure(duplicateResult))
        {
            return duplicateResult;
        }

        return string.Join(Environment.NewLine, openResult, addressResult, duplicateResult, "Duplicated the current Chrome tab.");
    }

    // Opens the browser's find-in-page box (Ctrl+F) and types the query.
    public static async Task<string> RunBrowserFindAsync(
        string appTarget,
        string query,
        ToolContext context,
        CancellationToken cancellationToken)
    {
        return await RunTextEntryWorkflowAsync(appTarget, "ctrl+f", query, context, cancellationToken);
    }

    // Maps what the user said to a playlist remembered in structured memory (best name match), otherwise keeps their words.
    public static async Task<string> ResolveSpotifyQuery(string query, ToolContext context, CancellationToken cancellationToken)
    {
        var trimmed = query.Trim();

        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return trimmed;
        }

        var normalizedQuery = NormalizeForMatching(trimmed);
        var profile = await context.MemoryStore.GetProfileAsync(cancellationToken);
        var match = profile.StructuredMemories
            .Where(item => string.Equals(item.Kind, "playlist", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => ScoreEntity(normalizedQuery, item.Name, item.Summary))
            .FirstOrDefault(item => ScoreEntity(normalizedQuery, item.Name, item.Summary) > 0);

        return match is null ? trimmed : match.Name;
    }

    // Turns user text into a full path: special folder words (desktop, downloads...), environment variables, a leading ~ for the home folder,
    // relative paths measured from the workspace folder.
    public static string ResolvePath(string target, string workspaceRoot)
    {
        var trimmed = target.Trim().Trim('"');

        if (TryResolveSpecialFolder(trimmed, workspaceRoot, out var specialFolderPath))
        {
            return specialFolderPath;
        }

        var expanded = Environment.ExpandEnvironmentVariables(trimmed);

        if (expanded.StartsWith('~'))
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            expanded = Path.Combine(userProfile, expanded[1..].TrimStart('\\', '/'));
        }

        if (Path.IsPathRooted(expanded))
        {
            return Path.GetFullPath(expanded);
        }

        return Path.GetFullPath(Path.Combine(workspaceRoot, expanded));
    }

    // Prepares the VS Code "--goto file:line:column" argument: resolves the file part and keeps the line and column numbers.
    // The checks on the colons avoid mistaking the drive letter in "C:\x.cs" for a line number.
    public static string ResolveCodeGotoTarget(string target, string workspaceRoot)
    {
        var trimmed = target.Trim().Trim('"');

        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return string.Empty;
        }

        var lastColon = trimmed.LastIndexOf(':');

        if (lastColon <= 0)
        {
            return ResolvePath(trimmed, workspaceRoot);
        }

        if (!int.TryParse(trimmed[(lastColon + 1)..], out var trailingNumber))
        {
            return ResolvePath(trimmed, workspaceRoot);
        }

        var secondLastColon = trimmed.LastIndexOf(':', lastColon - 1);

        if (secondLastColon > 1 && int.TryParse(trimmed[(secondLastColon + 1)..lastColon], out var lineNumber))
        {
            var pathPart = ResolvePath(trimmed[..secondLastColon], workspaceRoot);
            return $"{pathPart}:{lineNumber}:{trailingNumber}";
        }

        var resolvedPath = ResolvePath(trimmed[..lastColon], workspaceRoot);
        return $"{resolvedPath}:{trailingNumber}";
    }

    // Makes a browser address from text: full URLs stay as they are, "www." and word.word get https://, and anything else becomes a Bing search.
    public static string NormalizeBrowserTarget(string target)
    {
        var trimmed = target.Trim();

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var absoluteUri))
        {
            return absoluteUri.ToString();
        }

        if (trimmed.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
        {
            return $"https://{trimmed}";
        }

        if (trimmed.Contains('.') && !trimmed.Contains(' '))
        {
            return $"https://{trimmed}";
        }

        return $"https://www.bing.com/search?q={Uri.EscapeDataString(trimmed)}";
    }

    // The friendly name used in replies, for example "VS Code" for the target "code".
    private static string ResolveFriendlyAppName(string appTarget)
    {
        return NormalizeForMatching(appTarget) switch
        {
            "code" => "VS Code",
            "chrome" => "Chrome",
            "spotify" => "Spotify",
            "discord" => "Discord",
            _ => string.IsNullOrWhiteSpace(appTarget) ? "The app" : appTarget.Trim()
        };
    }

    // Words to look for in window and app names; some apps appear under longer names such as "Visual Studio Code".
    private static string[] ResolveMatchTerms(string appTarget, string displayName)
    {
        return NormalizeForMatching(appTarget) switch
        {
            "code" => ["code", "visual studio code", "vs code"],
            "chrome" => ["chrome", "google chrome"],
            "spotify" => ["spotify"],
            "discord" => ["discord"],
            _ => [appTarget, displayName]
        };
    }

    // Handles words such as desktop and downloads, using the real folder locations of this Windows user.
    private static bool TryResolveSpecialFolder(string target, string workspaceRoot, out string path)
    {
        if (target.Equals("workspace", StringComparison.OrdinalIgnoreCase))
        {
            path = workspaceRoot;
            return true;
        }

        if (target.Equals("desktop", StringComparison.OrdinalIgnoreCase))
        {
            path = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            return true;
        }

        if (target.Equals("documents", StringComparison.OrdinalIgnoreCase))
        {
            path = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            return true;
        }

        if (target.Equals("downloads", StringComparison.OrdinalIgnoreCase))
        {
            path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            return true;
        }

        if (target.Equals("pictures", StringComparison.OrdinalIgnoreCase))
        {
            path = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            return true;
        }

        if (target.Equals("music", StringComparison.OrdinalIgnoreCase))
        {
            path = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
            return true;
        }

        if (target.Equals("videos", StringComparison.OrdinalIgnoreCase))
        {
            path = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            return true;
        }

        path = string.Empty;
        return false;
    }

    // Scores how well a remembered item matches a query by adding points: exact name 100, name contains the query 60 (so an exact match gets both),
    // summary contains the query 30, and 12 for each query word found in the name. A score of 0 means no match.
    private static int ScoreEntity(string normalizedQuery, string name, string summary)
    {
        var normalizedName = NormalizeForMatching(name);
        var normalizedSummary = NormalizeForMatching(summary);
        var score = 0;

        if (normalizedName == normalizedQuery)
        {
            score += 100;
        }

        if (normalizedName.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
        {
            score += 60;
        }

        if (!string.IsNullOrWhiteSpace(normalizedSummary)
            && normalizedSummary.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
        {
            score += 30;
        }

        foreach (var token in normalizedQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (normalizedName.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                score += 12;
            }
        }

        return score;
    }

    // True if the value contains any of the terms, after simplifying both.
    private static bool MatchesAny(string value, IEnumerable<string> terms)
    {
        var normalizedValue = NormalizeForMatching(value);
        return terms.Any(term =>
        {
            var normalizedTerm = NormalizeForMatching(term);
            return !string.IsNullOrWhiteSpace(normalizedTerm)
                && normalizedValue.Contains(normalizedTerm, StringComparison.OrdinalIgnoreCase);
        });
    }
}

// Parsers: each TryParse... reads a command like "chrome new tab" and fills in the AppIntegrationCommand. They return false when the
// text is not for that tool. Several are catch-alls: once the tool word matches, unrecognised text becomes a default action
// (Spotify: search; VS Code: open path; Chrome: open url or search; Discord: jump).
internal static class AppIntegrationParsing
{
    public static bool TryParseSpotify(string input, out AppIntegrationCommand command)
    {
        if (!TryGetTail(input, out var tail, "spotify"))
        {
            command = new AppIntegrationCommand(string.Empty);
            return false;
        }

        if (string.IsNullOrWhiteSpace(tail))
        {
            command = new AppIntegrationCommand("open");
            return true;
        }

        if (IsSimpleAction(tail, out var simpleAction))
        {
            command = new AppIntegrationCommand(simpleAction);
            return true;
        }

        if (TryParsePrefixedPayload(tail, out command,
                ("search", "search"),
                ("playlist", "search"),
                ("album", "search"),
                ("artist", "search"),
                ("track", "search")))
        {
            return true;
        }

        // "spotify play <name>" is a search too; a bare "play" is a media control (handled just below).
        if (tail.StartsWith("play ", StringComparison.OrdinalIgnoreCase))
        {
            command = new AppIntegrationCommand("search", tail["play ".Length..].Trim());
            return true;
        }

        if (TryMapMediaAction(tail, out var mediaAction))
        {
            command = new AppIntegrationCommand("media", mediaAction);
            return true;
        }

        command = new AppIntegrationCommand("search", tail);
        return true;
    }

    public static bool TryParseCode(string input, out AppIntegrationCommand command)
    {
        if (!TryGetTail(input, out var tail, "code", "vscode", "vs code"))
        {
            command = new AppIntegrationCommand(string.Empty);
            return false;
        }

        if (string.IsNullOrWhiteSpace(tail))
        {
            command = new AppIntegrationCommand("open");
            return true;
        }

        if (IsSimpleAction(tail, out var simpleAction))
        {
            command = new AppIntegrationCommand(simpleAction);
            return true;
        }

        if (TryParsePrefixedPayload(tail, out command,
                ("open", "open-path"),
                ("file", "open-path"),
                ("folder", "open-path"),
                ("workspace", "open-path"),
                ("goto", "goto"),
                ("search", "search"),
                ("command", "command"),
                ("palette", "command")))
        {
            return true;
        }

        command = new AppIntegrationCommand("open-path", tail);
        return true;
    }

    // Chrome commands are checked in order: simple open/status, prefix commands that carry text, then fixed phrases.
    public static bool TryParseChrome(string input, out AppIntegrationCommand command)
    {
        if (!TryGetTail(input, out var tail, "chrome"))
        {
            command = new AppIntegrationCommand(string.Empty);
            return false;
        }

        if (string.IsNullOrWhiteSpace(tail))
        {
            command = new AppIntegrationCommand("open");
            return true;
        }

        if (IsSimpleAction(tail, out var simpleAction))
        {
            command = new AppIntegrationCommand(simpleAction);
            return true;
        }

        if (TryParsePrefixedPayload(tail, out command,
                ("open", "open-url"),
                ("search", "open-url"),
                ("go", "navigate-current"),
                ("navigate", "navigate-current"),
                ("current", "navigate-current"),
                ("new window", "new-window"),
                ("new tab", "new-tab"),
                ("find", "find"),
                ("incognito", "incognito")))
        {
            return true;
        }

        var normalized = tail.Trim();

        switch (normalized.ToLowerInvariant())
        {
            case "back":
                command = new AppIntegrationCommand("back");
                return true;
            case "forward":
                command = new AppIntegrationCommand("forward");
                return true;
            case "reload":
            case "refresh":
                command = new AppIntegrationCommand("reload");
                return true;
            case "new tab":
                command = new AppIntegrationCommand("new-tab");
                return true;
            case "close tab":
                command = new AppIntegrationCommand("close-tab");
                return true;
            case "reopen tab":
            case "reopen closed tab":
                command = new AppIntegrationCommand("reopen-tab");
                return true;
            case "duplicate tab":
                command = new AppIntegrationCommand("duplicate-tab");
                return true;
            case "next tab":
                command = new AppIntegrationCommand("next-tab");
                return true;
            case "previous tab":
            case "prev tab":
                command = new AppIntegrationCommand("previous-tab");
                return true;
            case "history":
                command = new AppIntegrationCommand("history");
                return true;
            case "downloads":
                command = new AppIntegrationCommand("downloads");
                return true;
            case "bookmarks":
                command = new AppIntegrationCommand("bookmarks");
                return true;
            case "devtools":
            case "developer tools":
                command = new AppIntegrationCommand("devtools");
                return true;
            case "focus address bar":
            case "address bar":
                command = new AppIntegrationCommand("focus-address-bar");
                return true;
            case "submit":
                command = new AppIntegrationCommand("submit");
                return true;
        }

        // The removed form/login/fill commands must not fall through to the search below, or a typed password would end up in a Bing address.
        // The first word is compared without trailing punctuation, so "login, user :: pass" (speech often adds the comma) is caught too.
        var firstWord = tail.Split(' ', 2)[0].TrimEnd(',', ';', ':', '.');
        if (firstWord.Equals("fill", StringComparison.OrdinalIgnoreCase)
            || firstWord.Equals("form", StringComparison.OrdinalIgnoreCase)
            || firstWord.Equals("login", StringComparison.OrdinalIgnoreCase))
        {
            command = new AppIntegrationCommand(string.Empty);
            return false;
        }

        // Anything else after "chrome" is treated as a URL or search text.
        command = new AppIntegrationCommand("open-url", tail);
        return true;
    }

    public static bool TryParseDiscord(string input, out AppIntegrationCommand command)
    {
        if (!TryGetTail(input, out var tail, "discord"))
        {
            command = new AppIntegrationCommand(string.Empty);
            return false;
        }

        if (string.IsNullOrWhiteSpace(tail))
        {
            command = new AppIntegrationCommand("open");
            return true;
        }

        if (IsSimpleAction(tail, out var simpleAction))
        {
            command = new AppIntegrationCommand(simpleAction);
            return true;
        }

        if (TryParsePrefixedPayload(tail, out command,
                ("jump", "jump"),
                ("switch", "jump"),
                ("search", "search")))
        {
            return true;
        }

        command = new AppIntegrationCommand("jump", tail);
        return true;
    }

    // Used by ActionSafety.cs. Pulls out the text that would be typed into an app, so it can be checked for passwords or keys before it is sent.
    public static bool TryExtractSensitiveTextPayload(string toolName, string input, out string targetApp, out string text)
    {
        targetApp = string.Empty;
        text = string.Empty;

        AppIntegrationCommand command;

        if (string.Equals(toolName, "code", StringComparison.OrdinalIgnoreCase)
            && TryParseCode(input, out command)
            && command.Action is "search" or "command")
        {
            targetApp = "code";
            text = command.Payload;
            return !string.IsNullOrWhiteSpace(text);
        }

        if (string.Equals(toolName, "discord", StringComparison.OrdinalIgnoreCase)
            && TryParseDiscord(input, out command)
            && command.Action is "jump" or "search")
        {
            targetApp = "discord";
            text = command.Payload;
            return !string.IsNullOrWhiteSpace(text);
        }

        return false;
    }

    // True if the input starts with one of the tool's names; the rest of the text is returned in tail.
    private static bool TryGetTail(string input, out string tail, params string[] prefixes)
    {
        foreach (var prefix in prefixes)
        {
            if (!ToolInputHelper.StartsWithCommand(input, prefix))
            {
                continue;
            }

            tail = ToolInputHelper.GetCommandTail(input, prefix);
            return true;
        }

        tail = string.Empty;
        return false;
    }

    // Empty, "open" or "focus" means open; "status" means status.
    private static bool IsSimpleAction(string tail, out string action)
    {
        if (string.IsNullOrWhiteSpace(tail))
        {
            action = "open";
            return true;
        }

        var normalized = tail.Trim();

        if (normalized.Equals("open", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("focus", StringComparison.OrdinalIgnoreCase))
        {
            action = "open";
            return true;
        }

        if (normalized.Equals("status", StringComparison.OrdinalIgnoreCase))
        {
            action = "status";
            return true;
        }

        action = string.Empty;
        return false;
    }

    // Matches "<prefix> <text>" against a list of (prefix, action) pairs; the first match wins and the text becomes the payload.
    private static bool TryParsePrefixedPayload(
        string tail,
        out AppIntegrationCommand command,
        params (string Prefix, string Action)[] patterns)
    {
        foreach (var (prefix, action) in patterns)
        {
            if (!ToolInputHelper.StartsWithCommand(tail, prefix))
            {
                continue;
            }

            command = new AppIntegrationCommand(action, ToolInputHelper.GetCommandTail(tail, prefix));
            return true;
        }

        command = new AppIntegrationCommand(string.Empty);
        return false;
    }

    // Recognises media controls and passes them through as written.
    private static bool TryMapMediaAction(string tail, out string command)
    {
        var normalized = tail.Trim();

        if (normalized.StartsWith("volume up", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("volume down", StringComparison.OrdinalIgnoreCase))
        {
            command = normalized;
            return true;
        }

        switch (normalized.ToLowerInvariant())
        {
            case "play":
            case "pause":
            case "resume":
            case "toggle":
            case "stop":
            case "next":
            case "next track":
            case "previous":
            case "previous track":
            case "prev":
            case "prev track":
            case "mute":
            case "volume up":
            case "volume down":
                command = normalized;
                return true;
            default:
                command = string.Empty;
                return false;
        }
    }
}
