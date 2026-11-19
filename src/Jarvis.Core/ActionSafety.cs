// ActionSafety.cs: the approval gate. Before a tool runs, ToolSafetyService.Assess looks at the tool name and the command text
// and returns one of three decisions (SafetyDecisionKind):
//   Allow           - run it now (reads, status checks, ordinary UI actions).
//   RequireApproval - pause, show the user what is about to happen, and wait for "approve" or "deny".
//   Block           - refuse outright (disk formatting, boot settings, changes inside Windows/Program Files/HKLM).
// The paused action is saved in a PendingApprovalStore (a small JSON file) so the next user message can approve or deny it.
// Caller: JarvisAssistant.ExecuteToolWithSafetyAsync asks Assess before every tool runs, and handles the saved approval on the next turn.
// A second rule, the prompt-injection guard (see OutsideContentTools): once a turn has read outside text (a web page, mail, the screen),
// any later tool that acts, rather than just reads, also asks first. This file only decides; it never runs anything itself.
// All rules are text matching on the command, not a sandbox, so they are a safety net for mistakes and prompt injection.
using System.Text.Json;

namespace Jarvis.Core;

// One tool call that was paused and is waiting for the user to say approve or deny. Saved to disk so it survives until the next message.
public sealed record PendingApprovalAction(
    string ToolName,
    string NormalizedCommand,
    string OriginalInput,
    string Summary,
    string ApprovalPrompt,
    DateTimeOffset CreatedAtUtc);

// Where the single pending approval is kept between turns.
public interface IPendingApprovalStore
{
    Task<PendingApprovalAction?> GetAsync(CancellationToken cancellationToken);

    Task SaveAsync(PendingApprovalAction action, CancellationToken cancellationToken);

    Task ClearAsync(CancellationToken cancellationToken);
}

// Pending-approval store that keeps one action in a JSON file. A semaphore makes reads, writes and deletes take turns.
public sealed class JsonPendingApprovalStore : IPendingApprovalStore
{
    // Web-style camelCase JSON, indented so the file is readable by hand.
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _filePath;
    // Lock so two callers never touch the file at the same time (SemaphoreSlim works with await, unlike lock).
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonPendingApprovalStore(string filePath)
    {
        _filePath = filePath;
    }

    public async Task<PendingApprovalAction?> GetAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);

        try
        {
            if (!File.Exists(_filePath))
            {
                return null;
            }

            await using var stream = File.OpenRead(_filePath);
            return await JsonSerializer.DeserializeAsync<PendingApprovalAction>(stream, JsonOptions, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(PendingApprovalAction action, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);

        try
        {
            EnsureParentDirectoryExists();

            await using var stream = File.Create(_filePath);
            await JsonSerializer.SerializeAsync(stream, action, JsonOptions, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);

        try
        {
            if (File.Exists(_filePath))
            {
                File.Delete(_filePath);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    // Creates the folder for the file if it does not exist yet, so the first save does not fail.
    private void EnsureParentDirectoryExists()
    {
        var directory = Path.GetDirectoryName(_filePath);

        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }
}

// The three possible outcomes of a safety check.
internal enum SafetyDecisionKind
{
    Allow,
    RequireApproval,
    Block
}

// The result of a safety check. For RequireApproval and Block the three texts are: what to tell the user, a note recorded for the model
// so it knows the tool did not run, and a short summary label.
internal sealed record SafetyDecision(
    SafetyDecisionKind Kind,
    string ResponseText = "",
    string VerificationText = "",
    string SummaryText = "");

// Decides whether a tool call may run, needs approval, or is blocked. Settings come from JarvisOptions (SafetyRequireApprovalForMajorActions,
// SafetyBlockProtectedSystemPaths). Texts are compared in a flattened, lowercase form padded with spaces (see NormalizeCommand).
public sealed class ToolSafetyService
{
    // Shell commands that are always refused: they wipe or reformat disks, edit boot settings, delete backups or toggle disk encryption.
    private static readonly string[] AlwaysBlockedSignals =
    {
        " diskpart ",
        " format-volume ",
        " clear-disk ",
        " initialize-disk ",
        " bcdedit ",
        " cipher /w",
        " vssadmin delete ",
        " wbadmin delete ",
        " manage-bde -off ",
        " manage-bde -on "
    };

    // Substrings that mean a shell command changes something (deletes, writes, installs, starts or stops things, downloads). The spaces
    // around each word make sure "rm" does not match inside a word like "format". Used to find risky commands and to describe them.
    private static readonly string[] StateChangingSignals =
    {
        " remove-item ",
        " del ",
        " erase ",
        " rm ",
        " rmdir ",
        " rd ",
        " clear-content ",
        " set-content ",
        " add-content ",
        " out-file ",
        " new-item ",
        " move-item ",
        " copy-item ",
        " rename-item ",
        " set-item ",
        " clear-item ",
        " set-itemproperty ",
        " new-itemproperty ",
        " remove-itemproperty ",
        " reg add ",
        " reg delete ",
        " reg import ",
        " reg export ",
        " start-process ",
        " stop-process ",
        " taskkill ",
        " stop-service ",
        " start-service ",
        " restart-service ",
        " new-service ",
        " set-service ",
        " sc.exe ",
        " sc ",
        " shutdown ",
        " restart-computer ",
        " stop-computer ",
        " set-executionpolicy ",
        " netsh ",
        " winget ",
        " choco ",
        " msiexec ",
        " npm install ",
        " npm uninstall ",
        " pnpm add ",
        " pnpm remove ",
        " yarn add ",
        " yarn remove ",
        " pip install ",
        " pip uninstall ",
        " dotnet add ",
        " dotnet remove ",
        " dotnet tool install ",
        " dotnet tool uninstall ",
        " git clone ",
        " git checkout ",
        " git switch ",
        " git clean ",
        " git reset ",
        " git stash pop ",
        " invoke-webrequest ",
        " curl ",
        " bitsadmin ",
        " schtasks /create ",
        " schtasks /delete ",
        " attrib ",
        " icacls ",
        " takeown "
    };

    // Shell commands that only look at things. A pipeline of only these (and Get-* cmdlets) runs without asking.
    private static readonly HashSet<string> ReadOnlyCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "dir", "ls", "gci", "pwd", "cd", "whoami", "hostname", "echo", "write-output", "type", "cat", "gc",
        "select-string", "sls", "where", "where-object", "test-path", "resolve-path", "measure-object", "measure",
        "compare-object", "sort-object", "sort", "group-object", "group", "select-object", "select",
        "format-table", "ft", "format-list", "fl", "out-string"
    };

    // Whole commands (exact text) that print a version or list and are safe to run without asking.
    private static readonly HashSet<string> ReadOnlyWholeCommands = new(StringComparer.Ordinal)
    {
        "dotnet --info", "dotnet --version", "dotnet --list-sdks", "dotnet --list-runtimes", "node -v", "node --version",
        "npm -v", "npm --version", "python --version", "py --version", "wsl -l", "wsl --list", "wsl -l -v",
        "wsl --list --verbose"
    };

    private readonly JarvisOptions _options;
    private readonly string[] _protectedFragments;

    public ToolSafetyService(JarvisOptions options)
    {
        _options = options;
        _protectedFragments = BuildProtectedFragments();
    }

    // Main entry point. Routes by tool name to a specialised check; tools with no special rule are allowed. The "run" (shell) tool is checked
    // in the steps below: blocked list first, then protected system paths, then read-only commands pass, and anything else asks for approval.
    internal SafetyDecision Assess(string toolName, string normalizedCommand)
    {
        if (string.Equals(toolName, "chrome", StringComparison.OrdinalIgnoreCase))
        {
            return AssessChromeCommand(normalizedCommand);
        }

        if (toolName is "write file" or "append file" or "replace in file" or "move path" or "copy path" or "delete path")
        {
            return AssessFileMutationCommand(toolName, normalizedCommand);
        }

        if (string.Equals(toolName, "close app", StringComparison.OrdinalIgnoreCase))
        {
            return AssessCloseApp(normalizedCommand);
        }

        if (string.Equals(toolName, "send hotkey", StringComparison.OrdinalIgnoreCase))
        {
            return AssessSendHotkey(normalizedCommand);
        }

        if (string.Equals(toolName, "open path", StringComparison.OrdinalIgnoreCase))
        {
            return AssessOpenPath(normalizedCommand);
        }

        if (string.Equals(toolName, "type into", StringComparison.OrdinalIgnoreCase))
        {
            return AssessSensitiveUiTyping(normalizedCommand);
        }

        if (string.Equals(toolName, "clipboard", StringComparison.OrdinalIgnoreCase))
        {
            return AssessClipboardCommand(normalizedCommand);
        }

        if (string.Equals(toolName, "code", StringComparison.OrdinalIgnoreCase)
            || string.Equals(toolName, "discord", StringComparison.OrdinalIgnoreCase)
            || string.Equals(toolName, "slack", StringComparison.OrdinalIgnoreCase))
        {
            return AssessSensitiveAppTextEntry(toolName, normalizedCommand);
        }

        if (!string.Equals(toolName, "run", StringComparison.OrdinalIgnoreCase))
        {
            return new SafetyDecision(SafetyDecisionKind.Allow);
        }

        // Shell path: pull the command text out after "run " or "shell ".
        var shellCommand = ExtractShellCommand(normalizedCommand);

        if (string.IsNullOrWhiteSpace(shellCommand))
        {
            return new SafetyDecision(SafetyDecisionKind.Allow);
        }

        // Step 1: flatten to one lowercase line so the substring lists can match.
        var normalized = NormalizeCommand(shellCommand);

        if (ContainsAny(normalized, AlwaysBlockedSignals))
        {
            return Block(
                shellCommand,
                "That shell command is blocked because it uses a high-risk system operation.",
                "Blocked dangerous shell command.");
        }

        // Step 2: a state-changing command aimed at Windows, System32, Program Files, ProgramData or HKLM is blocked (when that setting is on).
        if (_options.SafetyBlockProtectedSystemPaths
            && TargetsProtectedLocation(normalized)
            && LooksStateChanging(normalized))
        {
            return Block(
                shellCommand,
                "That shell command is blocked because it would change a protected system location or HKLM registry scope.",
                "Blocked command targeting a protected system location.");
        }

        // Step 3: read-only commands run freely; with the approval setting off, everything else does too.
        if (!_options.SafetyRequireApprovalForMajorActions || IsReadOnlyCommand(normalized, shellCommand))
        {
            return new SafetyDecision(SafetyDecisionKind.Allow);
        }

        // Step 4: everything else pauses and tells the user what kind of change it is, using the command text.
        var impact = DescribeImpact(normalized);
        var summary = $"Approval required for a major shell action: {impact}.";
        var response = string.Join(
            Environment.NewLine,
            "Approval required before I run that shell command.",
            $"Planned change: {impact}.",
            $"Command: {TrimForDisplay(shellCommand)}",
            "Reply `approve` to continue or `deny` to cancel.");

        return new SafetyDecision(
            SafetyDecisionKind.RequireApproval,
            response,
            $"Execution is paused pending user approval for `{TrimForInline(shellCommand)}`.",
            summary);
    }

    // Prompt-injection guard. Tools whose output is text written by outsiders (web pages, mail, the screen, files...).
    // A planner turn that has read one is "marked"; after that, only IsReadOnlyCall tools run without asking.
    // clipboard and calendar are multi-action, so ReadsOutsideContent handles them by action.
    private static readonly HashSet<string> OutsideContentTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "browse", "search web", "research", "page", "get element text", "read file", "screen", "analyze image",
        "analyze document", "lecture notes", "brain", "run"
    };

    // Reads and status tools that never act. Anything not listed here, or not read-only by action below, counts as acting.
    private static readonly HashSet<string> ReadOnlyTools = new(OutsideContentTools.Concat(
    [
        "list files", "find files", "windows", "app state", "computer", "apps", "status", "time", "help", "weather",
        "network", "system", "recall", "notes", "preferences", "scroll", "wait"
    ]), StringComparer.OrdinalIgnoreCase);

    // Whether a call reads text that outsiders wrote (web page, mail, screen). Chrome, clipboard and calendar depend on the action used.
    internal static bool ReadsOutsideContent(string toolName, string normalizedCommand) => toolName.ToLowerInvariant() switch
    {
        "clipboard" => UiCommandParsing.TryParseClipboardCommand(normalizedCommand, out var clip, out _) && clip == "show",
        "calendar" => IsReadOnlyCall(toolName, normalizedCommand),
        _ => OutsideContentTools.Contains(toolName)
    };

    // When in doubt (unparsed command, unknown tool) this says false, so the call asks.
    internal static bool IsReadOnlyCall(string toolName, string normalizedCommand)
    {
        var tail = ToolInputHelper.GetCommandTail(normalizedCommand, toolName).Trim();

        switch (toolName.ToLowerInvariant())
        {
            case "chrome":
                // ponytail: navigation passes, so a page can still smuggle data out in a URL; scope the allowed hosts if that matters.
                return AppIntegrationParsing.TryParseChrome(normalizedCommand, out var chrome)
                    && chrome.Action is "status" or "open-url" or "navigate-current" or "new-tab" or "new-window" or "back" or "forward" or "reload"
                        or "next-tab" or "previous-tab" or "history" or "downloads" or "bookmarks" or "find";
            case "clipboard":
                return UiCommandParsing.TryParseClipboardCommand(normalizedCommand, out var clip, out _) && clip == "show";
            case "calendar":
                return !tail.Equals("add", StringComparison.OrdinalIgnoreCase)
                    && !tail.StartsWith("add ", StringComparison.OrdinalIgnoreCase)
                    && !tail.Equals("connect", StringComparison.OrdinalIgnoreCase);
            case "lecture notes":
                return !tail.StartsWith("start", StringComparison.OrdinalIgnoreCase) && !tail.StartsWith("stop", StringComparison.OrdinalIgnoreCase);
            case "vpn":
                return tail is "" or "status";
            case "reminders":
                return tail is "" or "show" or "list" or "show reminders" or "list reminders";
            default:
                return ReadOnlyTools.Contains(toolName);
        }
    }

    // Builds the approval request used when a read of outside content is followed by an action. See the prompt-injection note above.
    internal static SafetyDecision RequireApprovalAfterOutsideRead(string toolName, string normalizedCommand) =>
        RequireUiApproval(
            $"run `{TrimForDisplay(normalizedCommand)}`",
            $"Execution is paused pending approval because this request read outside content: `{TrimForInline(normalizedCommand)}`.",
            $"This request read outside content (a web page, email or the screen), so I'm checking before I run `{toolName}`.",
            "Approval required after reading outside content.");

    // Asks first when the text typed into a window looks like a password or key.
    private static SafetyDecision AssessSensitiveUiTyping(string normalizedCommand)
    {
        if (!UiCommandParsing.TryParseTypeInto(normalizedCommand, out var target, out var text)
            || string.IsNullOrWhiteSpace(text)
            || !LooksLikeSensitiveUiPayload(text))
        {
            return new SafetyDecision(SafetyDecisionKind.Allow);
        }

        var response = string.Join(
            Environment.NewLine,
            "Approval required before I type that sensitive text into a window.",
            $"Target: {TrimForDisplay(target)}",
            $"Detected content: {DescribeSensitivePayload(text)}",
            "Reply `approve` to continue or `deny` to cancel.");

        return new SafetyDecision(
            SafetyDecisionKind.RequireApproval,
            response,
            $"Typing sensitive text into `{TrimForInline(target)}` is paused pending approval.",
            "Approval required for sensitive UI typing.");
    }

    // Asks first before closing an app window, since unsaved work could be lost.
    private SafetyDecision AssessCloseApp(string normalizedCommand)
    {
        if (!_options.SafetyRequireApprovalForMajorActions
            || !UiCommandParsing.TryParseCloseTarget(normalizedCommand, out var target)
            || string.IsNullOrWhiteSpace(target))
        {
            return new SafetyDecision(SafetyDecisionKind.Allow);
        }

        return RequireUiApproval(
            $"close `{TrimForDisplay(target)}`",
            $"Closing `{TrimForInline(target)}` is paused pending approval.",
            "Approval required before I close that app window.",
            "Approval required for closing an app window.");
    }

    // "open app setup" must not run the setup.exe sitting in Downloads: a lone match opens by itself only if it is a folder or a
    // document. Anything else, including a shortcut or script, is listed for the user to pick.
    private static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".doc", ".docx", ".rtf", ".odt", ".pdf", ".txt", ".md", ".csv", ".log", ".json", ".xml", ".html", ".htm", ".xls", ".xlsx", ".ppt", ".pptx",
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".mp3", ".wav", ".m4a", ".mp4", ".mov", ".mkv"
    };

    // No extension is a folder word ("downloads", "~\Documents") or a file Windows asks how to open. The full path is what opens:
    // Windows drops a trailing dot, so "setup.exe." runs setup.exe.
    public static bool OpensWithoutAsking(string path)
    {
        var extension = Path.GetExtension(Path.GetFullPath(Environment.ExpandEnvironmentVariables(path)));
        return Directory.Exists(path) || extension.Length == 0 || DocumentExtensions.Contains(extension);
    }

    // "open path" runs whatever the shell would run, so without this check "open path setup.exe" would get around the approval
    // the run tool asks for. A folder or document still opens freely.
    private SafetyDecision AssessOpenPath(string normalizedCommand)
    {
        var target = ToolInputHelper.GetCommandTail(normalizedCommand, "open path", "open file", "open folder").Trim().Trim('"');

        if (!_options.SafetyRequireApprovalForMajorActions || target.Length == 0 || OpensWithoutAsking(target))
        {
            return new SafetyDecision(SafetyDecisionKind.Allow);
        }

        return RequireUiApproval(
            $"open `{TrimForDisplay(target)}`, which may run as a program",
            $"Opening `{TrimForInline(target)}` is paused pending approval.",
            "Approval required before I open that, because it may run as a program.",
            "Approval required for opening a program or script.");
    }

    // Asks first before sending a keyboard shortcut to another window.
    private SafetyDecision AssessSendHotkey(string normalizedCommand)
    {
        if (!_options.SafetyRequireApprovalForMajorActions
            || !UiCommandParsing.TryParseHotkeyCommand(normalizedCommand, out var target, out var hotkey)
            || string.IsNullOrWhiteSpace(target)
            || string.IsNullOrWhiteSpace(hotkey))
        {
            return new SafetyDecision(SafetyDecisionKind.Allow);
        }

        return RequireUiApproval(
            $"send `{TrimForDisplay(hotkey)}` to `{TrimForDisplay(target)}`",
            $"Sending `{TrimForInline(hotkey)}` to `{TrimForInline(target)}` is paused pending approval.",
            "Approval required before I send that hotkey.",
            "Approval required for a UI hotkey action.");
    }

    // Clipboard: copying something that looks secret, or pasting into a window, needs approval.
    private SafetyDecision AssessClipboardCommand(string normalizedCommand)
    {
        if (!UiCommandParsing.TryParseClipboardCommand(normalizedCommand, out var action, out var payload))
        {
            return new SafetyDecision(SafetyDecisionKind.Allow);
        }

        if (string.Equals(action, "set", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(payload)
            && LooksLikeSensitiveUiPayload(payload))
        {
            return RequireUiApproval(
                $"copy sensitive-looking text to the clipboard ({DescribeSensitivePayload(payload)})",
                "Copying sensitive-looking text to the clipboard is paused pending approval.",
                "Approval required before I copy that sensitive text to the clipboard.",
                "Approval required for sensitive clipboard content.");
        }

        if (!_options.SafetyRequireApprovalForMajorActions
            || !string.Equals(action, "paste", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(payload))
        {
            return new SafetyDecision(SafetyDecisionKind.Allow);
        }

        return RequireUiApproval(
            $"paste clipboard contents into `{TrimForDisplay(payload)}`",
            $"Pasting clipboard contents into `{TrimForInline(payload)}` is paused pending approval.",
            "Approval required before I paste clipboard contents into that window.",
            "Approval required for a clipboard paste action.");
    }

    // For code editors, Discord and Slack: asks first when the text to enter looks like a password or key.
    private static SafetyDecision AssessSensitiveAppTextEntry(string toolName, string normalizedCommand)
    {
        if (!AppIntegrationParsing.TryExtractSensitiveTextPayload(toolName, normalizedCommand, out var targetApp, out var text)
            || !LooksLikeSensitiveUiPayload(text))
        {
            return new SafetyDecision(SafetyDecisionKind.Allow);
        }

        return RequireUiApproval(
            $"send sensitive-looking text to `{TrimForDisplay(targetApp)}` ({DescribeSensitivePayload(text)})",
            $"Sending sensitive-looking text to `{TrimForInline(targetApp)}` is paused pending approval.",
            "Approval required before I send that sensitive text into the app.",
            "Approval required for sensitive app text entry.");
    }

    // Chrome: asks first for logins and sensitive form fill.
    private SafetyDecision AssessChromeCommand(string normalizedCommand)
    {
        if (!AppIntegrationParsing.TryParseChrome(normalizedCommand, out var command))
        {
            return new SafetyDecision(SafetyDecisionKind.Allow);
        }

        if (command.Action == "login" && !string.IsNullOrWhiteSpace(command.Payload))
        {
            return RequireUiApproval(
                "fill login credentials into Chrome",
                "Chrome login automation is paused pending approval.",
                "Approval required before I fill login credentials in Chrome.",
                "Approval required for Chrome login automation.");
        }

        if ((command.Action == "fill" || command.Action == "form")
            && !string.IsNullOrWhiteSpace(command.Payload)
            && LooksLikeSensitiveUiPayload(command.Payload))
        {
            return RequireUiApproval(
                $"fill sensitive-looking text into Chrome ({DescribeSensitivePayload(command.Payload)})",
                "Sensitive Chrome form-fill content is paused pending approval.",
                "Approval required before I fill that sensitive text in Chrome.",
                "Approval required for sensitive Chrome form fill.");
        }

        return new SafetyDecision(SafetyDecisionKind.Allow);
    }

    // File tools (write, append, replace, move, copy, delete): protected system paths are blocked, everything else asks first.
    // If the command cannot be parsed it is allowed here, because the tool itself will reject it.
    private SafetyDecision AssessFileMutationCommand(string toolName, string normalizedCommand)
    {
        var hasValidPayload = toolName switch
        {
            "write file" => AutomationWorkflowParsing.TryParsePathAndContent(normalizedCommand, out _, out _, "write file", "create file", "save file"),
            "append file" => AutomationWorkflowParsing.TryParsePathAndContent(normalizedCommand, out _, out _, "append file", "add to file"),
            "replace in file" => AutomationWorkflowParsing.TryParseReplaceInFile(normalizedCommand, out _, out _, out _),
            "move path" => AutomationWorkflowParsing.TryParseSourceAndDestination(normalizedCommand, out _, out _, "move path", "move file", "move folder"),
            "copy path" => AutomationWorkflowParsing.TryParseSourceAndDestination(normalizedCommand, out _, out _, "copy path", "copy file", "copy folder"),
            "delete path" => !string.IsNullOrWhiteSpace(ToolInputHelper.GetCommandTail(normalizedCommand, "delete path", "delete file", "delete folder")),
            _ => false
        };

        if (!hasValidPayload)
        {
            return new SafetyDecision(SafetyDecisionKind.Allow);
        }

        var flattened = NormalizeCommand(normalizedCommand);

        if (_options.SafetyBlockProtectedSystemPaths
            && TargetsProtectedLocation(flattened))
        {
            return new SafetyDecision(
                SafetyDecisionKind.Block,
                string.Join(
                    Environment.NewLine,
                    "Blocked for safety.",
                    "That file operation targets a protected system location.",
                    $"Command: {TrimForDisplay(normalizedCommand)}"),
                $"The runtime blocked `{TrimForInline(normalizedCommand)}`.",
                "Blocked protected file operation.");
        }

        if (!_options.SafetyRequireApprovalForMajorActions)
        {
            return new SafetyDecision(SafetyDecisionKind.Allow);
        }

        var impact = toolName switch
        {
            "write file" => "create or overwrite a file",
            "append file" => "append to a file",
            "replace in file" => "edit file contents",
            "move path" => "move a file or folder",
            "copy path" => "copy a file or folder",
            "delete path" => "delete a file or folder",
            _ => "change files on disk"
        };

        var response = string.Join(
            Environment.NewLine,
            "Approval required before I change files on disk.",
            $"Planned change: {impact}.",
            $"Command: {TrimForDisplay(normalizedCommand)}",
            "Reply `approve` to continue or `deny` to cancel.");

        return new SafetyDecision(
            SafetyDecisionKind.RequireApproval,
            response,
            $"The file change `{TrimForInline(normalizedCommand)}` is paused pending approval.",
            "Approval required for a file mutation command.");
    }

    // Heuristic for "this text is a secret": the shared memory check first, then a single word of 10+ characters mixing digits, lowercase
    // and an uppercase letter or symbol (the shape of a password or token).
    private static bool LooksLikeSensitiveUiPayload(string value)
    {
        if (MemoryAutomation.LooksSensitive(value))
        {
            return true;
        }

        var trimmed = value.Trim();

        if (trimmed.Length < 10 || trimmed.Any(char.IsWhiteSpace))
        {
            return false;
        }

        var hasLower = trimmed.Any(char.IsLower);
        var hasUpper = trimmed.Any(char.IsUpper);
        var hasDigit = trimmed.Any(char.IsDigit);
        var hasSymbol = trimmed.Any(character => char.IsPunctuation(character) || char.IsSymbol(character));

        return hasDigit && hasLower && (hasUpper || hasSymbol);
    }

    // Builds a RequireApproval decision with the standard approve/deny wording.
    private static SafetyDecision RequireUiApproval(string impact, string verificationText, string header, string summary)
    {
        var response = string.Join(
            Environment.NewLine,
            header,
            $"Planned action: {impact}.",
            "Reply `approve` to continue or `deny` to cancel.");

        return new SafetyDecision(
            SafetyDecisionKind.RequireApproval,
            response,
            verificationText,
            summary);
    }

    // Whether the user's reply means yes. Only exact short phrases count, so a long sentence never approves by accident.
    public bool LooksLikeApprovalResponse(string input)
    {
        var normalized = NormalizeInput(input);
        return normalized is "approve"
            or "approved"
            or "yes"
            or "yes do it"
            or "yes proceed"
            or "proceed"
            or "go ahead"
            or "do it"
            or "run it"
            or "continue";
    }

    // Whether the user's reply means no, using the same exact-phrase approach.
    public bool LooksLikeDenialResponse(string input)
    {
        var normalized = NormalizeInput(input);
        return normalized is "deny"
            or "denied"
            or "no"
            or "no stop"
            or "stop"
            or "cancel"
            or "cancel it"
            or "dont do it"
            or "do not do it";
    }

    // Builds a Block decision with an explanation and the standing rule about protected locations.
    private SafetyDecision Block(string shellCommand, string reason, string summary)
    {
        var response = string.Join(
            Environment.NewLine,
            "Blocked for safety.",
            reason,
            $"Command: {TrimForDisplay(shellCommand)}",
            "Jarvis will not use shell access to change Windows, System32, Program Files, ProgramData, or HKLM registry locations.");

        return new SafetyDecision(
            SafetyDecisionKind.Block,
            response,
            $"The runtime blocked `{TrimForInline(shellCommand)}`.",
            summary);
    }

    // Returns the command after a leading "run " or "shell ", or empty if neither is there.
    private static string ExtractShellCommand(string normalizedCommand)
    {
        if (normalizedCommand.StartsWith("run ", StringComparison.OrdinalIgnoreCase))
        {
            return normalizedCommand["run ".Length..].Trim();
        }

        if (normalizedCommand.StartsWith("shell ", StringComparison.OrdinalIgnoreCase))
        {
            return normalizedCommand["shell ".Length..].Trim();
        }

        return string.Empty;
    }

    // Flattens a command to one lowercase line with single spaces, padded with a space at each end, so " word " style matching works.
    private static string NormalizeCommand(string command)
    {
        var flattened = command.ReplaceLineEndings(" ");
        var tokens = flattened.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return $" {string.Join(" ", tokens).ToLowerInvariant()} ";
    }

    // Cleans a user reply for yes/no matching: lowercase, single spaces, surrounding punctuation removed.
    private static string NormalizeInput(string input)
    {
        var flattened = input.ReplaceLineEndings(" ");
        var tokens = flattened
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(token => token.Trim(' ', '\t', '\r', '\n', '.', ',', '!', '?', ':', ';', '"', '\''))
            .Where(token => !string.IsNullOrWhiteSpace(token))
            .ToArray();
        return string.Join(" ", tokens).ToLowerInvariant();
    }

    private static bool ContainsAny(string normalized, IEnumerable<string> patterns) =>
        patterns.Any(pattern => normalized.Contains(pattern, StringComparison.Ordinal));

    // True if the text mentions a protected location. A match must end at a space or backslash, so "c:\windows" matches but
    // "c:\windowsfoo" does not.
    private bool TargetsProtectedLocation(string normalized)
    {
        // Quotes become spaces and / becomes \ so quoted or slash-style paths match; a fragment must end at a space or \.
        var text = normalized.Replace('\'', ' ').Replace('"', ' ').Replace('/', '\\');
        return _protectedFragments.Any(fragment =>
        {
            for (var at = text.IndexOf(fragment, StringComparison.Ordinal); at >= 0; at = text.IndexOf(fragment, at + 1, StringComparison.Ordinal))
            {
                var next = at + fragment.Length;
                if (next >= text.Length || text[next] is ' ' or '\\')
                {
                    return true;
                }
            }

            return false;
        });
    }

    // Any redirect (> or >>) or any word from the state-changing list counts.
    private static bool LooksStateChanging(string normalized)
    {
        if (normalized.Contains(" >>", StringComparison.Ordinal)
            || normalized.Contains(" >", StringComparison.Ordinal))
        {
            return true;
        }

        return ContainsAny(normalized, StateChangingSignals);
    }

    // Auto-allow only what can be fully accounted for: plain ASCII, no shell metacharacters, and every pipeline
    // stage a known read-only command. Anything else falls through to approval.
    // ponytail: Get-* cmdlets are trusted as read-only and remote parameters such as -ComputerName are not screened.
    private static bool IsReadOnlyCommand(string normalized, string originalCommand)
    {
        if (LooksStateChanging(normalized)
            || originalCommand.Any(c => c < ' ' || c > '~')
            || originalCommand.IndexOfAny(UnsafeShellChars) >= 0
            || originalCommand.Contains(@"\\", StringComparison.Ordinal)
            || originalCommand.Contains("//", StringComparison.Ordinal))
        {
            return false;
        }

        return originalCommand.Split('|').All(segment => IsReadOnlySegment(segment.Trim()));
    }

    // Characters that let a shell chain or hide commands (; & ` ( ) { } [ ] < > @), so any command containing one is not trusted as read-only.
    private static readonly char[] UnsafeShellChars = ";&`(){}[]<>@".ToCharArray();

    // One pipeline stage is read-only if it is a Get-* cmdlet, a listed read-only command, git status/diff/log/show (without options that
    // write files or run programs), rg without options that run a program, or an exact listed version command.
    private static bool IsReadOnlySegment(string segment)
    {
        var tokens = segment.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return false;
        }

        var name = tokens[0];
        var args = tokens.Skip(1).Select(token => token.Replace("'", "").Replace("\"", ""));

        if (name.Length > 4
            && name.StartsWith("get-", StringComparison.OrdinalIgnoreCase)
            && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
        {
            return true;
        }

        if (ReadOnlyCommands.Contains(name))
        {
            return true;
        }

        if (name.Equals("git", StringComparison.OrdinalIgnoreCase))
        {
            return tokens.Length > 1
                && tokens[1] is "status" or "diff" or "log" or "show"
                && !args.Skip(1).Any(arg => arg.StartsWith("--output", StringComparison.OrdinalIgnoreCase)
                    || arg.StartsWith("--ext-diff", StringComparison.OrdinalIgnoreCase));
        }

        if (name.Equals("rg", StringComparison.OrdinalIgnoreCase))
        {
            return !args.Any(arg => arg.StartsWith("--pre", StringComparison.OrdinalIgnoreCase)
                || arg.StartsWith("--hostname-bin", StringComparison.OrdinalIgnoreCase));
        }

        return ReadOnlyWholeCommands.Contains(string.Join(" ", tokens).ToLowerInvariant());
    }

    // Lists in plain words what kinds of change a command makes, for the approval message.
    private static string DescribeImpact(string normalized)
    {
        var categories = new List<string>();

        if (ContainsAny(normalized, new[] { " remove-item ", " del ", " erase ", " rm ", " rmdir ", " rd " }))
        {
            categories.Add("delete files or folders");
        }

        if (ContainsAny(normalized, new[] { " set-content ", " add-content ", " out-file ", " new-item ", " move-item ", " copy-item ", " rename-item ", " >", " >>" }))
        {
            categories.Add("create or modify files");
        }

        if (ContainsAny(normalized, new[] { " winget ", " choco ", " msiexec ", " npm install ", " npm uninstall ", " pnpm add ", " pnpm remove ", " yarn add ", " yarn remove ", " pip install ", " pip uninstall ", " dotnet add ", " dotnet remove ", " dotnet tool install ", " dotnet tool uninstall " }))
        {
            categories.Add("install or remove software");
        }

        if (ContainsAny(normalized, new[] { " reg add ", " reg delete ", " reg import ", " set-itemproperty ", " new-itemproperty ", " remove-itemproperty ", " hklm: ", " registry::hkey_local_machine " }))
        {
            categories.Add("change registry settings");
        }

        if (ContainsAny(normalized, new[] { " start-process ", " stop-process ", " taskkill ", " stop-service ", " start-service ", " restart-service ", " new-service ", " set-service ", " sc.exe ", " sc ", " schtasks /create ", " schtasks /delete " }))
        {
            categories.Add("change running apps, processes, or services");
        }

        if (ContainsAny(normalized, new[] { " shutdown ", " restart-computer ", " stop-computer ", " set-executionpolicy ", " netsh ", " bcdedit " }))
        {
            categories.Add("change system or network behavior");
        }

        if (categories.Count == 0)
        {
            categories.Add("change the machine state");
        }

        return string.Join(", ", categories.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    // Describes secret text by length only, so the approval message never repeats the secret.
    private static string DescribeSensitivePayload(string value)
    {
        var count = value.Trim().Length;
        return count == 1
            ? "1 character of sensitive-looking text"
            : $"{count} characters of sensitive-looking text";
    }

    // Builds the list of protected path fragments: fixed Windows locations plus the real folders on this machine (in case Windows is not on C:).
    private string[] BuildProtectedFragments()
    {
        var fragments = new HashSet<string>(StringComparer.Ordinal)
        {
            " c:\\windows",
            " c:\\windows\\system32",
            " c:\\windows\\syswow64",
            " $env:windir",
            " ${env:windir}",
            " %windir%",
            " system32",
            " syswow64",
            " hklm:",
            " registry::hkey_local_machine"
        };

        AddFolderFragment(fragments, Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        AddFolderFragment(fragments, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32"));
        AddFolderFragment(fragments, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        AddFolderFragment(fragments, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
        AddFolderFragment(fragments, Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));

        fragments.Add(" c:\\program files");
        fragments.Add(" c:\\program files (x86)");
        fragments.Add(" c:\\programdata");
        fragments.Add(" $env:programfiles");
        fragments.Add(" ${env:programfiles}");
        fragments.Add(" %programfiles%");
        fragments.Add(" $env:programdata");
        fragments.Add(" ${env:programdata}");
        fragments.Add(" %programdata%");

        return fragments.ToArray();
    }

    // Adds a folder to the protected set in lowercase, with a leading space to match the padded command text.
    private static void AddFolderFragment(ISet<string> target, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var normalized = path.Trim().Replace('/', '\\').ToLowerInvariant();
        target.Add($" {normalized}");
    }

    // Shortens text for the multi-line approval message.
    private static string TrimForDisplay(string shellCommand) => shellCommand.Clip(320);

    // Shortens text for the one-line note recorded for the model.
    private static string TrimForInline(string shellCommand) => shellCommand.Clip(120);
}
