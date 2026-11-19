// AppIntegrationTools.cs: tools that control specific desktop apps (Spotify, VS Code, Chrome, File Explorer, Office, Discord, Slack).
// Each tool follows the same three steps: a parser in AppIntegrationParsing turns the typed command into an AppIntegrationCommand
// (an Action name plus a text Payload), the tool's switch picks a helper for that action, and AppIntegrationHelpers does the work by
// calling context.DesktopAutomation (focus a window, send a keyboard shortcut, send text, launch a program).
// Most actions are therefore simulated keystrokes into the real app, with short fixed pauses between steps; there is no app-specific API.
// The exception is the "chrome dom ..." family, which talks to Chrome through DevTools (ChromeDevToolsAutomation).
// ActionSafety.cs checks the same parsed commands to decide when to ask first (logins, form fills, sensitive text).
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

// Chrome: normal browser control by keyboard shortcut and launch flags, plus "chrome dom ..." commands that use DevTools to read and click page elements.
public sealed class ChromeTool : IAssistantTool
{
    // Help text shown for a bad Chrome command.
    private const string Usage = "Usage: chrome [status|open <url-or-query>|search <query>|go <url>|new window [target]|new tab [target]|incognito [target]|back|forward|reload|close tab|reopen tab|duplicate tab|next tab|previous tab|history|downloads|bookmarks|devtools|find <query>|fill <text>|form <value1> ;; <value2>|login <username> :: <password> [:: submit]|submit|dom status|dom tabs|dom select <tab-index|tab-id|match>|dom open <url-or-query>|dom new tab [url-or-query]|dom page|dom read <css-selector>|dom click <css-selector>|dom type <css-selector> :: <text>|dom wait <css-selector> [:: <timeout-seconds>]|dom verify <css-selector> [:: <expected-text>]]";

    public string Name => "chrome";

    public string Description => "Chrome adapter. Use standard navigation commands or DevTools-backed DOM commands such as `chrome dom open <url>`, `chrome dom tabs`, `chrome dom click <css-selector>`, or `chrome dom verify <css-selector> [:: text]`.";

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
            case "back":
                result = await AppIntegrationHelpers.RunBrowserHotkeyAsync("chrome", "alt+left", context, cancellationToken, "Moved back in Chrome.");
                break;
            case "forward":
                result = await AppIntegrationHelpers.RunBrowserHotkeyAsync("chrome", "alt+right", context, cancellationToken, "Moved forward in Chrome.");
                break;
            case "reload":
                result = await AppIntegrationHelpers.RunBrowserHotkeyAsync("chrome", "ctrl+r", context, cancellationToken, "Reloaded the current Chrome tab.");
                break;
            case "close-tab":
                result = await AppIntegrationHelpers.RunBrowserHotkeyAsync("chrome", "ctrl+w", context, cancellationToken, "Closed the current Chrome tab.");
                break;
            case "reopen-tab":
                result = await AppIntegrationHelpers.RunBrowserHotkeyAsync("chrome", "ctrl+shift+t", context, cancellationToken, "Reopened the last closed Chrome tab.");
                break;
            case "duplicate-tab":
                result = await AppIntegrationHelpers.DuplicateBrowserTabAsync("chrome", context, cancellationToken);
                break;
            case "next-tab":
                result = await AppIntegrationHelpers.RunBrowserHotkeyAsync("chrome", "ctrl+tab", context, cancellationToken, "Moved to the next Chrome tab.");
                break;
            case "previous-tab":
                result = await AppIntegrationHelpers.RunBrowserHotkeyAsync("chrome", "ctrl+shift+tab", context, cancellationToken, "Moved to the previous Chrome tab.");
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
            case "devtools":
                result = await AppIntegrationHelpers.RunBrowserHotkeyAsync("chrome", "ctrl+shift+i", context, cancellationToken, "Opened Chrome DevTools.");
                break;
            case "focus-address-bar":
                result = await AppIntegrationHelpers.RunBrowserHotkeyAsync("chrome", "ctrl+l", context, cancellationToken, "Focused the Chrome address bar.");
                break;
            case "find":
                result = await AppIntegrationHelpers.RunBrowserFindAsync("chrome", command.Payload, context, cancellationToken);
                break;
            case "fill":
                result = await AppIntegrationHelpers.FillFocusedFieldAsync("chrome", command.Payload, context, cancellationToken);
                break;
            case "form":
                result = await AppIntegrationHelpers.FillBrowserFormAsync("chrome", command.Payload, context, cancellationToken);
                break;
            case "login":
                result = await AppIntegrationHelpers.FillBrowserLoginAsync("chrome", command.Payload, context, cancellationToken);
                break;
            case "submit":
                result = await AppIntegrationHelpers.RunBrowserHotkeyAsync("chrome", "enter", context, cancellationToken, "Submitted the active Chrome form.");
                break;
            // The dom-* actions return their own result directly; the rest set result and share the success check after the switch.
            case "dom-status":
                return await ChromeDevToolsAutomation.GetStatusAsync(context, cancellationToken);
            case "dom-tabs":
                return await ChromeDevToolsAutomation.ListTabsAsync(context, cancellationToken);
            case "dom-select":
                return await ChromeDevToolsAutomation.SelectTabAsync(command.Payload, context, cancellationToken);
            case "dom-open":
                return await ChromeDevToolsAutomation.OpenAsync(command.Payload, newTab: false, context, cancellationToken);
            case "dom-new-tab":
                return await ChromeDevToolsAutomation.OpenAsync(command.Payload, newTab: true, context, cancellationToken);
            case "dom-page":
                return await ChromeDevToolsAutomation.GetPageAsync(context, cancellationToken);
            case "dom-read":
                return await ChromeDevToolsAutomation.ReadAsync(command.Payload, context, cancellationToken);
            case "dom-click":
                return await ChromeDevToolsAutomation.ClickAsync(command.Payload, context, cancellationToken);
            case "dom-type":
                return await ChromeDevToolsAutomation.TypeAsync(command.Payload, context, cancellationToken);
            case "dom-wait":
                return await ChromeDevToolsAutomation.WaitForAsync(command.Payload, context, cancellationToken);
            case "dom-verify":
                return await ChromeDevToolsAutomation.VerifyAsync(command.Payload, context, cancellationToken);
            default:
                result = Usage;
                break;
        }

        var success = !UiCommandParsing.LooksLikeFailure(result);
        return new ToolResult(result, VerificationText: result, SummaryText: success ? "Handled Chrome action." : "Chrome action failed.");
    }
}

// File Explorer: open or focus, open a path, or open a folder with a file selected ("reveal").
public sealed class ExplorerAppTool : IAssistantTool
{
    public string Name => "explorer";

    public string Description => "Explorer adapter. Use `explorer`, `explorer status`, `explorer downloads`, `explorer open <path>`, or `explorer reveal <path>`.";

    public bool CanHandle(string input) => AppIntegrationParsing.TryParseExplorer(input, out _);

    public async Task<ToolResult> ExecuteAsync(string input, ToolContext context, CancellationToken cancellationToken)
    {
        if (!AppIntegrationParsing.TryParseExplorer(input, out var command))
        {
            return new ToolResult("Usage: explorer [status|open <path>|reveal <path>|downloads|desktop|documents|workspace]");
        }

        string result;

        // Each case is an action name produced by AppIntegrationParsing.TryParseExplorer.
        switch (command.Action)
        {
            case "open":
                result = await AppIntegrationHelpers.OpenOrFocusAsync("explorer", context, cancellationToken);
                break;
            case "status":
                result = await AppIntegrationHelpers.GetAppStatusAsync("Explorer", ["explorer", "file explorer"], context, cancellationToken);
                break;
            case "open-path":
                result = await context.DesktopAutomation.OpenPathAsync(command.Payload, cancellationToken);
                break;
            case "reveal":
            {
                var resolvedPath = AppIntegrationHelpers.ResolvePath(command.Payload, context.WorkspaceRoot);

                if (!File.Exists(resolvedPath) && !Directory.Exists(resolvedPath))
                {
                    result = $"Path not found: {resolvedPath}";
                    break;
                }

                result = await context.DesktopAutomation.LaunchApplicationAsync("explorer", $"/select,\"{resolvedPath}\"", cancellationToken);
                break;
            }
            default:
                result = "Usage: explorer [status|open <path>|reveal <path>|downloads|desktop|documents|workspace]";
                break;
        }

        var success = !UiCommandParsing.LooksLikeFailure(result);
        return new ToolResult(result, VerificationText: result, SummaryText: success ? "Handled Explorer action." : "Explorer action failed.");
    }
}

// Microsoft Office: Word, Excel, PowerPoint (open a file or just the app) and Outlook (open only), or a combined status.
public sealed class OfficeTool : IAssistantTool
{
    public string Name => "office";

    public string Description => "Office adapter. Use `office status`, `office word`, `office word <path>`, `office excel <path>`, `office powerpoint <path>`, or `office outlook`.";

    public bool CanHandle(string input) => AppIntegrationParsing.TryParseOffice(input, out _);

    public async Task<ToolResult> ExecuteAsync(string input, ToolContext context, CancellationToken cancellationToken)
    {
        if (!AppIntegrationParsing.TryParseOffice(input, out var command))
        {
            return new ToolResult("Usage: office [status|word [path]|excel [path]|powerpoint [path]|outlook]");
        }

        string result;

        // Action names are "status", or "open-<app>" / "status-<app>" for each Office app.
        switch (command.Action)
        {
            case "status":
                result = await AppIntegrationHelpers.GetSuiteStatusAsync(
                    "Office",
                    context,
                    cancellationToken,
                    ("Word", ["word", "microsoft word"]),
                    ("Excel", ["excel", "microsoft excel"]),
                    ("PowerPoint", ["powerpoint", "power point", "microsoft powerpoint"]),
                    ("Outlook", ["outlook", "microsoft outlook"]));
                break;
            case "open-word":
                result = await AppIntegrationHelpers.OpenFileInAppAsync("word", command.Payload, context, cancellationToken);
                break;
            case "status-word":
                result = await AppIntegrationHelpers.GetAppStatusAsync("Word", ["word", "microsoft word"], context, cancellationToken);
                break;
            case "open-excel":
                result = await AppIntegrationHelpers.OpenFileInAppAsync("excel", command.Payload, context, cancellationToken);
                break;
            case "status-excel":
                result = await AppIntegrationHelpers.GetAppStatusAsync("Excel", ["excel", "microsoft excel"], context, cancellationToken);
                break;
            case "open-powerpoint":
                result = await AppIntegrationHelpers.OpenFileInAppAsync("powerpoint", command.Payload, context, cancellationToken);
                break;
            case "status-powerpoint":
                result = await AppIntegrationHelpers.GetAppStatusAsync("PowerPoint", ["powerpoint", "power point", "microsoft powerpoint"], context, cancellationToken);
                break;
            case "open-outlook":
                result = await AppIntegrationHelpers.OpenOrFocusAsync("outlook", context, cancellationToken);
                break;
            case "status-outlook":
                result = await AppIntegrationHelpers.GetAppStatusAsync("Outlook", ["outlook", "microsoft outlook"], context, cancellationToken);
                break;
            default:
                result = "Usage: office [status|word [path]|excel [path]|powerpoint [path]|outlook]";
                break;
        }

        var success = !UiCommandParsing.LooksLikeFailure(result);
        return new ToolResult(result, VerificationText: result, SummaryText: success ? "Handled Office action." : "Office action failed.");
    }
}

// Discord: open or focus, status, and two shortcut-driven searches: the quick switcher (Ctrl+K) to jump to a channel or person, and message search (Ctrl+F).
public sealed class DiscordTool : IAssistantTool
{
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
            case "jump":
                result = await AppIntegrationHelpers.RunTextEntryWorkflowAsync("discord", "ctrl+k", command.Payload, context, cancellationToken);
                break;
            case "search":
                result = await AppIntegrationHelpers.RunTextEntryWorkflowAsync("discord", "ctrl+f", command.Payload, context, cancellationToken);
                break;
            default:
                result = "Usage: discord [status|jump <query>|search <query>]";
                break;
        }

        var success = !UiCommandParsing.LooksLikeFailure(result);
        return new ToolResult(result, VerificationText: result, SummaryText: success ? "Handled Discord action." : "Discord action failed.");
    }
}

// Slack: open or focus, status, and jump to a channel or person with the quick switcher (Ctrl+K).
public sealed class SlackTool : IAssistantTool
{
    public string Name => "slack";

    public string Description => "Slack adapter. Use `slack`, `slack status`, or `slack jump <query>`.";

    public bool CanHandle(string input) => AppIntegrationParsing.TryParseSlack(input, out _);

    public async Task<ToolResult> ExecuteAsync(string input, ToolContext context, CancellationToken cancellationToken)
    {
        if (!AppIntegrationParsing.TryParseSlack(input, out var command))
        {
            return new ToolResult("Usage: slack [status|jump <query>]");
        }

        string result;

        // Each case is an action name produced by AppIntegrationParsing.TryParseSlack.
        switch (command.Action)
        {
            case "open":
                result = await AppIntegrationHelpers.OpenOrFocusAsync("slack", context, cancellationToken);
                break;
            case "status":
                result = await AppIntegrationHelpers.GetAppStatusAsync("Slack", ["slack"], context, cancellationToken);
                break;
            case "jump":
                result = await AppIntegrationHelpers.RunTextEntryWorkflowAsync("slack", "ctrl+k", command.Payload, context, cancellationToken);
                break;
            default:
                result = "Usage: slack [status|jump <query>]";
                break;
        }

        var success = !UiCommandParsing.LooksLikeFailure(result);
        return new ToolResult(result, VerificationText: result, SummaryText: success ? "Handled Slack action." : "Slack action failed.");
    }
}

// A parsed app command: Action is a short name such as "open", "search" or "dom-click"; Payload is the remaining text.
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

    // Same as above for a group of apps (used for the Office suite).
    public static async Task<string> GetSuiteStatusAsync(
        string displayName,
        ToolContext context,
        CancellationToken cancellationToken,
        params (string AppName, string[] MatchTerms)[] apps)
    {
        var snapshot = await context.DesktopAutomation.GetContextSnapshotAsync(cancellationToken);
        var lines = new List<string> { $"{displayName} status:" };

        foreach (var (appName, matchTerms) in apps)
        {
            var active = MatchesAny(snapshot.ActiveApplication, matchTerms) || MatchesAny(snapshot.ActiveWindow, matchTerms);
            var visible = snapshot.VisibleApplications.Where(value => MatchesAny(value, matchTerms)).Take(2).ToArray();
            var state = visible.Length == 0
                ? "not visible"
                : active
                    ? "active"
                    : "visible";
            var detail = visible.Length == 0 ? string.Empty : $" ({string.Join(" | ", visible)})";

            lines.Add($"- {appName}: {state}{detail}");
        }

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

    // Types text into whatever field currently has focus in the app.
    public static async Task<string> FillFocusedFieldAsync(
        string appTarget,
        string text,
        ToolContext context,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "A field value is required.";
        }

        var openResult = await OpenOrFocusAsync(appTarget, context, cancellationToken);

        if (UiCommandParsing.LooksLikeFailure(openResult))
        {
            return openResult;
        }

        await context.DesktopAutomation.WaitAsync(0.2, cancellationToken);

        var textResult = await context.DesktopAutomation.SendTextAsync(appTarget, text, cancellationToken);

        if (UiCommandParsing.LooksLikeFailure(textResult))
        {
            return textResult;
        }

        return string.Join(Environment.NewLine, openResult, textResult, "Filled the focused field.");
    }

    // Parses "value ;; value ;; value [:: submit]" and fills the fields in order.
    public static async Task<string> FillBrowserFormAsync(
        string appTarget,
        string payload,
        ToolContext context,
        CancellationToken cancellationToken)
    {
        if (!TryParseFormValues(payload, out var values, out var submit))
        {
            return "Usage: chrome form <value1> ;; <value2> [;; <value3>] [:: submit]";
        }

        return await FillFieldSequenceAsync(appTarget, values, submit, context, cancellationToken, "Filled Chrome form fields.");
    }

    // Parses "username :: password [:: submit]" and fills the two fields. The password passes through as text; ActionSafety.cs asks for approval first.
    public static async Task<string> FillBrowserLoginAsync(
        string appTarget,
        string payload,
        ToolContext context,
        CancellationToken cancellationToken)
    {
        if (!TryParseLoginValues(payload, out var username, out var password, out var submit))
        {
            return "Usage: chrome login <username> :: <password> [:: submit]";
        }

        return await FillFieldSequenceAsync(appTarget, [username, password], submit, context, cancellationToken, "Filled Chrome login fields.");
    }

    // Types each value, pressing Tab between them to reach the next field, and optionally presses Enter at the end.
    private static async Task<string> FillFieldSequenceAsync(
        string appTarget,
        IReadOnlyList<string> values,
        bool submit,
        ToolContext context,
        CancellationToken cancellationToken,
        string successSummary)
    {
        var openResult = await OpenOrFocusAsync(appTarget, context, cancellationToken);

        if (UiCommandParsing.LooksLikeFailure(openResult))
        {
            return openResult;
        }

        var details = new List<string> { openResult };

        for (var index = 0; index < values.Count; index++)
        {
            await context.DesktopAutomation.WaitAsync(0.15, cancellationToken);
            var textResult = await context.DesktopAutomation.SendTextAsync(appTarget, values[index], cancellationToken);

            if (UiCommandParsing.LooksLikeFailure(textResult))
            {
                return textResult;
            }

            details.Add(textResult);

            if (index < values.Count - 1)
            {
                await context.DesktopAutomation.WaitAsync(0.08, cancellationToken);
                var tabResult = await context.DesktopAutomation.SendHotkeyAsync(appTarget, "tab", cancellationToken);

                if (UiCommandParsing.LooksLikeFailure(tabResult))
                {
                    return tabResult;
                }

                details.Add(tabResult);
            }
        }

        if (submit)
        {
            await context.DesktopAutomation.WaitAsync(0.08, cancellationToken);
            var submitResult = await context.DesktopAutomation.SendHotkeyAsync(appTarget, "enter", cancellationToken);

            if (UiCommandParsing.LooksLikeFailure(submitResult))
            {
                return submitResult;
            }

            details.Add(submitResult);
        }

        details.Add(successSummary);
        return string.Join(Environment.NewLine, details);
    }

    // Opens a file in an Office app. With no path it just opens or focuses the app; a missing file is reported.
    public static async Task<string> OpenFileInAppAsync(
        string appTarget,
        string path,
        ToolContext context,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return await OpenOrFocusAsync(appTarget, context, cancellationToken);
        }

        var resolvedPath = ResolvePath(path, context.WorkspaceRoot);

        if (!File.Exists(resolvedPath))
        {
            return $"Path not found: {resolvedPath}";
        }

        return await context.DesktopAutomation.LaunchApplicationAsync(appTarget, $"\"{resolvedPath}\"", cancellationToken);
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

    // Splits the form payload at ";;" and notes a trailing ":: submit" request.
    private static bool TryParseFormValues(string payload, out string[] values, out bool submit)
    {
        submit = false;
        values = [];

        if (string.IsNullOrWhiteSpace(payload))
        {
            return false;
        }

        var normalizedPayload = payload.Trim();
        const string submitMarker = ":: submit";

        if (normalizedPayload.EndsWith(submitMarker, StringComparison.OrdinalIgnoreCase))
        {
            submit = true;
            normalizedPayload = normalizedPayload[..^submitMarker.Length].TrimEnd();
        }

        values = normalizedPayload
            .Split([";;"], StringSplitOptions.None | StringSplitOptions.TrimEntries)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
        return values.Length > 0;
    }

    // Splits "username :: password :: submit" (the last part is optional; "submit" or "enter" turns it on).
    private static bool TryParseLoginValues(string payload, out string username, out string password, out bool submit)
    {
        username = string.Empty;
        password = string.Empty;
        submit = false;

        if (string.IsNullOrWhiteSpace(payload))
        {
            return false;
        }

        var parts = payload.Split(["::"], 3, StringSplitOptions.None | StringSplitOptions.TrimEntries);

        if (parts.Length < 2)
        {
            return false;
        }

        username = parts[0];
        password = parts[1];

        if (parts.Length > 2)
        {
            submit = parts[2].Equals("submit", StringComparison.OrdinalIgnoreCase)
                || parts[2].Equals("enter", StringComparison.OrdinalIgnoreCase);
        }

        return !string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(password);
    }

    // The friendly name used in replies, for example "VS Code" for the target "code".
    private static string ResolveFriendlyAppName(string appTarget)
    {
        return NormalizeForMatching(appTarget) switch
        {
            "code" => "VS Code",
            "chrome" => "Chrome",
            "explorer" => "Explorer",
            "spotify" => "Spotify",
            "discord" => "Discord",
            "slack" => "Slack",
            "word" => "Word",
            "excel" => "Excel",
            "powerpoint" => "PowerPoint",
            "outlook" => "Outlook",
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
            "explorer" => ["explorer", "file explorer"],
            "spotify" => ["spotify"],
            "discord" => ["discord"],
            "slack" => ["slack"],
            "word" => ["word", "microsoft word"],
            "excel" => ["excel", "microsoft excel"],
            "powerpoint" => ["powerpoint", "power point", "microsoft powerpoint"],
            "outlook" => ["outlook", "microsoft outlook"],
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

    // Lowercases and turns punctuation into spaces so names compare reliably.
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

    private static string OrUnavailable(string value) =>
        string.IsNullOrWhiteSpace(value) ? "Unavailable" : value.Trim();
}

// Parsers: each TryParse... reads a command like "chrome new tab" and fills in the AppIntegrationCommand. They return false when the
// text is not for that tool. Several are catch-alls: once the tool word matches, unrecognised text becomes a default action
// (Spotify: search; VS Code: open path; Chrome: open url or search; Discord and Slack: jump; Explorer: open path).
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

    // Chrome commands are checked in order: simple open/status, "dom ..." commands, prefix commands that carry text, then fixed phrases.
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

        if (TryParseChromeDomCommand(tail, out command))
        {
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
                ("fill", "fill"),
                ("form", "form"),
                ("login", "login"),
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

        // Anything else after "chrome" is treated as a URL or search text.
        command = new AppIntegrationCommand("open-url", tail);
        return true;
    }

    // Office: "office ...", or the app name on its own ("word report.docx", "excel", "ppt", "outlook").
    public static bool TryParseOffice(string input, out AppIntegrationCommand command)
    {
        if (TryGetTail(input, out var officeTail, "office"))
        {
            if (string.IsNullOrWhiteSpace(officeTail)
                || officeTail.Equals("status", StringComparison.OrdinalIgnoreCase))
            {
                command = new AppIntegrationCommand("status");
                return true;
            }

            if (TryParseOfficePrefixedCommand(officeTail, out command))
            {
                return true;
            }

            command = new AppIntegrationCommand(string.Empty);
            return false;
        }

        if (TryGetTail(input, out var wordTail, "word"))
        {
            command = ParseStandaloneOfficeApp(wordTail, "word");
            return true;
        }

        if (TryGetTail(input, out var excelTail, "excel"))
        {
            command = ParseStandaloneOfficeApp(excelTail, "excel");
            return true;
        }

        if (TryGetTail(input, out var powerpointTail, "powerpoint", "power point", "ppt"))
        {
            command = ParseStandaloneOfficeApp(powerpointTail, "powerpoint");
            return true;
        }

        if (TryGetTail(input, out var outlookTail, "outlook"))
        {
            command = ParseStandaloneOfficeApp(outlookTail, "outlook");
            return true;
        }

        command = new AppIntegrationCommand(string.Empty);
        return false;
    }

    public static bool TryParseExplorer(string input, out AppIntegrationCommand command)
    {
        if (!TryGetTail(input, out var tail, "explorer", "file explorer"))
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
                ("reveal", "reveal")))
        {
            return true;
        }

        command = new AppIntegrationCommand("open-path", tail);
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

    public static bool TryParseSlack(string input, out AppIntegrationCommand command)
    {
        if (!TryGetTail(input, out var tail, "slack"))
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
                ("switch", "jump")))
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

        if (string.Equals(toolName, "slack", StringComparison.OrdinalIgnoreCase)
            && TryParseSlack(input, out command)
            && command.Action == "jump")
        {
            targetApp = "slack";
            text = command.Payload;
            return !string.IsNullOrWhiteSpace(text);
        }

        if (string.Equals(toolName, "chrome", StringComparison.OrdinalIgnoreCase)
            && TryParseChrome(input, out command))
        {
            switch (command.Action)
            {
                case "fill":
                case "form":
                    targetApp = "chrome";
                    text = command.Payload;
                    return !string.IsNullOrWhiteSpace(text);
                case "login":
                    targetApp = "chrome";
                    text = command.Payload;
                    return !string.IsNullOrWhiteSpace(text);
                case "dom-type" when ChromeDevToolsAutomation.TryParseSelectorAndText(command.Payload, out _, out var typedText):
                    targetApp = "chrome";
                    text = typedText;
                    return !string.IsNullOrWhiteSpace(text);
            }
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

    // Parses "chrome dom <action> <text>". Bare "dom" means status.
    private static bool TryParseChromeDomCommand(string tail, out AppIntegrationCommand command)
    {
        if (!ToolInputHelper.StartsWithCommand(tail, "dom"))
        {
            command = new AppIntegrationCommand(string.Empty);
            return false;
        }

        var domTail = ToolInputHelper.GetCommandTail(tail, "dom");

        if (string.IsNullOrWhiteSpace(domTail)
            || domTail.Equals("status", StringComparison.OrdinalIgnoreCase))
        {
            command = new AppIntegrationCommand("dom-status");
            return true;
        }

        if (domTail.Equals("tabs", StringComparison.OrdinalIgnoreCase)
            || domTail.Equals("list tabs", StringComparison.OrdinalIgnoreCase))
        {
            command = new AppIntegrationCommand("dom-tabs");
            return true;
        }

        if (domTail.Equals("page", StringComparison.OrdinalIgnoreCase)
            || domTail.Equals("current page", StringComparison.OrdinalIgnoreCase)
            || domTail.Equals("current", StringComparison.OrdinalIgnoreCase))
        {
            command = new AppIntegrationCommand("dom-page");
            return true;
        }

        if (TryParsePrefixedPayload(domTail, out command,
                ("select", "dom-select"),
                ("open", "dom-open"),
                ("go", "dom-open"),
                ("navigate", "dom-open"),
                ("new tab", "dom-new-tab"),
                ("read", "dom-read"),
                ("click", "dom-click"),
                ("type", "dom-type"),
                ("fill", "dom-type"),
                ("wait", "dom-wait"),
                ("verify", "dom-verify")))
        {
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

    // Parses "office word ...", "office open word ..." and similar.
    private static bool TryParseOfficePrefixedCommand(string tail, out AppIntegrationCommand command)
    {
        if (TryResolveOfficeAppPrefix(tail, out var appName, out var remainder))
        {
            command = ParseStandaloneOfficeApp(remainder, appName);
            return true;
        }

        if (ToolInputHelper.StartsWithCommand(tail, "open"))
        {
            var openTail = ToolInputHelper.GetCommandTail(tail, "open");

            if (TryResolveOfficeAppPrefix(openTail, out appName, out remainder))
            {
                command = ParseStandaloneOfficeApp(remainder, appName);
                return true;
            }
        }

        command = new AppIntegrationCommand(string.Empty);
        return false;
    }

    // Finds which Office app name starts the text. PowerPoint's spellings are listed first so they win.
    private static bool TryResolveOfficeAppPrefix(string input, out string appName, out string remainder)
    {
        foreach (var prefix in new[]
                 {
                     ("powerpoint", "powerpoint"),
                     ("power point", "powerpoint"),
                     ("ppt", "powerpoint"),
                     ("outlook", "outlook"),
                     ("excel", "excel"),
                     ("word", "word")
                 })
        {
            if (!ToolInputHelper.StartsWithCommand(input, prefix.Item1))
            {
                continue;
            }

            appName = prefix.Item2;
            remainder = ToolInputHelper.GetCommandTail(input, prefix.Item1);
            return true;
        }

        appName = string.Empty;
        remainder = string.Empty;
        return false;
    }

    // Builds the action for one Office app: open (optionally with a file path) or status. Outlook ignores a path.
    private static AppIntegrationCommand ParseStandaloneOfficeApp(string tail, string appName)
    {
        var normalizedTail = tail.Trim();
        var openAction = $"open-{appName}";
        var statusAction = $"status-{appName}";

        if (string.IsNullOrWhiteSpace(normalizedTail)
            || normalizedTail.Equals("open", StringComparison.OrdinalIgnoreCase)
            || normalizedTail.Equals("focus", StringComparison.OrdinalIgnoreCase))
        {
            return new AppIntegrationCommand(openAction);
        }

        if (normalizedTail.Equals("status", StringComparison.OrdinalIgnoreCase))
        {
            return new AppIntegrationCommand(statusAction);
        }

        return appName == "outlook"
            ? new AppIntegrationCommand(openAction)
            : new AppIntegrationCommand(openAction, normalizedTail);
    }
}
