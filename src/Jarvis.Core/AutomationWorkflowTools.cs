// AutomationWorkflowTools.cs: tools for changing files and for desktop notifications, plus the parsers that read their commands.
// The file tools parse the command text and hand the work to context.DesktopAutomation; ActionSafety.cs asks approval for them and
// re-uses AutomationWorkflowParsing to check a command is well formed. "::" splits a command into parts, e.g. "write file notes.txt :: hello".
// A "Usage:" or failure-looking reply from the desktop service counts as a failed result.
namespace Jarvis.Core;

// Creates a text file, or overwrites it if it exists.
public sealed class WriteFileTool : IAssistantTool
{
    public string Name => "write file";

    public string Description => "Create or overwrite a text file. Format: `write file <path> :: <content>`.";

    public bool CanHandle(string input) =>
        ToolInputHelper.StartsWithCommand(input, Name) ||
        ToolInputHelper.StartsWithCommand(input, "create file") ||
        ToolInputHelper.StartsWithCommand(input, "save file");

    public async Task<ToolResult> ExecuteAsync(string input, ToolContext context, CancellationToken cancellationToken)
    {
        if (!AutomationWorkflowParsing.TryParsePathAndContent(input, out var path, out var content, "write file", "create file", "save file"))
        {
            return new ToolResult("Usage: write file <path> :: <content>", Succeeded: false, SummaryText: "Missing file path or content.");
        }

        var result = await context.DesktopAutomation.WriteFileAsync(path, content, append: false, cancellationToken);
        var success = !UiCommandParsing.LooksLikeFailure(result);
        return new ToolResult(result, Succeeded: success, VerificationText: result, SummaryText: success ? "Wrote file." : "Write file failed.");
    }
}

// Adds text to the end of a file.
public sealed class AppendFileTool : IAssistantTool
{
    public string Name => "append file";

    public string Description => "Append text to a file. Format: `append file <path> :: <content>`.";

    public bool CanHandle(string input) =>
        ToolInputHelper.StartsWithCommand(input, Name) ||
        ToolInputHelper.StartsWithCommand(input, "add to file");

    public async Task<ToolResult> ExecuteAsync(string input, ToolContext context, CancellationToken cancellationToken)
    {
        if (!AutomationWorkflowParsing.TryParsePathAndContent(input, out var path, out var content, "append file", "add to file"))
        {
            return new ToolResult("Usage: append file <path> :: <content>", Succeeded: false, SummaryText: "Missing file path or content.");
        }

        var result = await context.DesktopAutomation.WriteFileAsync(path, content, append: true, cancellationToken);
        var success = !UiCommandParsing.LooksLikeFailure(result);
        return new ToolResult(result, Succeeded: success, VerificationText: result, SummaryText: success ? "Appended file." : "Append file failed.");
    }
}

// Replaces text inside a file: path :: text to find :: replacement.
public sealed class ReplaceInFileTool : IAssistantTool
{
    public string Name => "replace in file";

    public string Description => "Replace text in a file. Format: `replace in file <path> :: <find> :: <replace>`.";

    public bool CanHandle(string input) =>
        ToolInputHelper.StartsWithCommand(input, Name) ||
        ToolInputHelper.StartsWithCommand(input, "edit file");

    public async Task<ToolResult> ExecuteAsync(string input, ToolContext context, CancellationToken cancellationToken)
    {
        if (!AutomationWorkflowParsing.TryParseReplaceInFile(input, out var path, out var findText, out var replaceText))
        {
            return new ToolResult("Usage: replace in file <path> :: <find> :: <replace>", Succeeded: false, SummaryText: "Missing file replace arguments.");
        }

        var result = await context.DesktopAutomation.ReplaceInFileAsync(path, findText, replaceText, cancellationToken);
        var success = !UiCommandParsing.LooksLikeFailure(result);
        return new ToolResult(result, Succeeded: success, VerificationText: result, SummaryText: success ? "Edited file." : "Edit file failed.");
    }
}

// Moves a file or folder.
public sealed class MovePathTool : IAssistantTool
{
    public string Name => "move path";

    public string Description => "Move a file or folder. Format: `move path <source> :: <destination>`.";

    public bool CanHandle(string input) =>
        ToolInputHelper.StartsWithCommand(input, Name) ||
        ToolInputHelper.StartsWithCommand(input, "move file") ||
        ToolInputHelper.StartsWithCommand(input, "move folder");

    public async Task<ToolResult> ExecuteAsync(string input, ToolContext context, CancellationToken cancellationToken)
    {
        if (!AutomationWorkflowParsing.TryParseSourceAndDestination(input, out var source, out var destination, "move path", "move file", "move folder"))
        {
            return new ToolResult("Usage: move path <source> :: <destination>", Succeeded: false, SummaryText: "Missing move source or destination.");
        }

        var result = await context.DesktopAutomation.MovePathAsync(source, destination, cancellationToken);
        var success = !UiCommandParsing.LooksLikeFailure(result);
        return new ToolResult(result, Succeeded: success, VerificationText: result, SummaryText: success ? "Moved path." : "Move path failed.");
    }
}

// Copies a file or folder.
public sealed class CopyPathTool : IAssistantTool
{
    public string Name => "copy path";

    public string Description => "Copy a file or folder. Format: `copy path <source> :: <destination>`.";

    public bool CanHandle(string input) =>
        ToolInputHelper.StartsWithCommand(input, Name) ||
        ToolInputHelper.StartsWithCommand(input, "copy file") ||
        ToolInputHelper.StartsWithCommand(input, "copy folder");

    public async Task<ToolResult> ExecuteAsync(string input, ToolContext context, CancellationToken cancellationToken)
    {
        if (!AutomationWorkflowParsing.TryParseSourceAndDestination(input, out var source, out var destination, "copy path", "copy file", "copy folder"))
        {
            return new ToolResult("Usage: copy path <source> :: <destination>", Succeeded: false, SummaryText: "Missing copy source or destination.");
        }

        var result = await context.DesktopAutomation.CopyPathAsync(source, destination, cancellationToken);
        var success = !UiCommandParsing.LooksLikeFailure(result);
        return new ToolResult(result, Succeeded: success, VerificationText: result, SummaryText: success ? "Copied path." : "Copy path failed.");
    }
}

// Deletes a file or folder. The desktop service can refuse (for example a protected path), which is reported as a failure.
public sealed class DeletePathTool : IAssistantTool
{
    public string Name => "delete path";

    public string Description => "Delete a file or folder. Format: `delete path <target>`.";

    public bool CanHandle(string input) =>
        ToolInputHelper.StartsWithCommand(input, Name) ||
        ToolInputHelper.StartsWithCommand(input, "delete file") ||
        ToolInputHelper.StartsWithCommand(input, "delete folder");

    public async Task<ToolResult> ExecuteAsync(string input, ToolContext context, CancellationToken cancellationToken)
    {
        var target = ToolInputHelper.GetCommandTail(input, "delete path", "delete file", "delete folder").Trim();

        if (string.IsNullOrWhiteSpace(target))
        {
            return new ToolResult("Usage: delete path <target>", Succeeded: false, SummaryText: "Missing delete target.");
        }

        var result = await context.DesktopAutomation.DeletePathAsync(target, cancellationToken);
        // A reply starting with "Refusing " means the service declined to delete, so it counts as failed.
        var success = !UiCommandParsing.LooksLikeFailure(result) && !result.StartsWith("Refusing ", StringComparison.OrdinalIgnoreCase);
        return new ToolResult(result, Succeeded: success, VerificationText: result, SummaryText: success ? "Deleted path." : "Delete path failed.");
    }
}

// Reports detailed state of one app (window, whether it is running and similar). A "No matching" reply counts as a failure.
public sealed class AppStateTool : IAssistantTool
{
    public string Name => "app state";

    public string Description => "Inspect richer app state. Format: `app state <app>`.";

    public bool CanHandle(string input) =>
        ToolInputHelper.StartsWithCommand(input, Name) ||
        ToolInputHelper.StartsWithCommand(input, "application state");

    public async Task<ToolResult> ExecuteAsync(string input, ToolContext context, CancellationToken cancellationToken)
    {
        var target = ToolInputHelper.GetCommandTail(input, "app state", "application state");

        if (string.IsNullOrWhiteSpace(target))
        {
            return new ToolResult("Usage: app state <app>", Succeeded: false, SummaryText: "Missing app state target.");
        }

        var result = await context.DesktopAutomation.GetApplicationStateAsync(target, cancellationToken);
        var success = !result.StartsWith("No matching", StringComparison.OrdinalIgnoreCase)
            && !result.StartsWith("Usage:", StringComparison.OrdinalIgnoreCase);
        return new ToolResult(result, Succeeded: success, VerificationText: result, SummaryText: success ? "Read app state." : "App state lookup failed.");
    }
}

// Sends a Jarvis notification (toast).
public sealed class NotifyTool : IAssistantTool
{
    public string Name => "notify";

    public string Description => "Send a Jarvis notification. Use `notify <title> :: <message>`.";

    public bool CanHandle(string input) => AutomationWorkflowParsing.TryParseNotification(input, out _);

    public async Task<ToolResult> ExecuteAsync(string input, ToolContext context, CancellationToken cancellationToken)
    {
        if (!AutomationWorkflowParsing.TryParseNotification(input, out var command))
        {
            return new ToolResult("Usage: notify <title> :: <message>", Succeeded: false, SummaryText: "Missing notification command.");
        }

        if (string.IsNullOrWhiteSpace(command.Title)
            && string.IsNullOrWhiteSpace(command.Message))
        {
            return new ToolResult("Usage: notify <title> :: <message> | notify <message>", Succeeded: false, SummaryText: "Missing notification content.");
        }

        var result = await context.DesktopAutomation.SendNotificationAsync(command.Title, command.Message, cancellationToken);

        var success = result.StartsWith("Sent notification", StringComparison.OrdinalIgnoreCase);
        return new ToolResult(result, Succeeded: success, VerificationText: result, SummaryText: success ? "Sent notification." : "Notification failed.");
    }
}

// A parsed notification command: the toast title and message.
internal sealed record NotificationCommand(string Title = "", string Message = "");

// Parsers for the "::"-separated command formats above. Shared by the tools and by ActionSafety.cs.
internal static class AutomationWorkflowParsing
{
    // Reads "<prefix> <path> :: <content>". Only the first "::" splits, so the content may contain "::". False if the path or separator is missing.
    public static bool TryParsePathAndContent(string input, out string path, out string content, params string[] prefixes)
    {
        path = string.Empty;
        content = string.Empty;

        var remainder = ToolInputHelper.GetCommandTail(input, prefixes);

        if (string.IsNullOrWhiteSpace(remainder))
        {
            return false;
        }

        var parts = remainder.Split(["::"], 2, StringSplitOptions.None | StringSplitOptions.TrimEntries);

        if (parts.Length < 2)
        {
            return false;
        }

        path = parts[0].Trim().Trim('"');
        content = parts[1];
        return !string.IsNullOrWhiteSpace(path);
    }

    // Reads "<path> :: <find> :: <replace>". Only the first two "::" split, so the replacement may contain "::" but the find text may not.
    public static bool TryParseReplaceInFile(string input, out string path, out string findText, out string replaceText)
    {
        path = string.Empty;
        findText = string.Empty;
        replaceText = string.Empty;

        var remainder = ToolInputHelper.GetCommandTail(input, "replace in file", "edit file");

        if (string.IsNullOrWhiteSpace(remainder))
        {
            return false;
        }

        var parts = remainder.Split(["::"], 3, StringSplitOptions.None | StringSplitOptions.TrimEntries);

        if (parts.Length < 3)
        {
            return false;
        }

        path = parts[0].Trim().Trim('"');
        findText = parts[1];
        replaceText = parts[2];
        return !string.IsNullOrWhiteSpace(path);
    }

    // Reads "<prefix> <source> :: <destination>", for both move and copy. Both parts must be non-empty.
    public static bool TryParseSourceAndDestination(string input, out string source, out string destination, params string[] prefixes)
    {
        source = string.Empty;
        destination = string.Empty;

        var remainder = ToolInputHelper.GetCommandTail(input, prefixes);

        if (string.IsNullOrWhiteSpace(remainder))
        {
            return false;
        }

        var parts = remainder.Split(["::"], 2, StringSplitOptions.None | StringSplitOptions.TrimEntries);

        if (parts.Length < 2)
        {
            return false;
        }

        source = parts[0].Trim().Trim('"');
        destination = parts[1].Trim().Trim('"');
        return !string.IsNullOrWhiteSpace(source) && !string.IsNullOrWhiteSpace(destination);
    }

    // Recognises "notify ..." and "notification ...". With a single part, e.g. "notify dinner is ready",
    // the title defaults to "Jarvis". Returns false for anything else, so CanHandle can use it directly.
    public static bool TryParseNotification(string input, out NotificationCommand command)
    {
        if (ToolInputHelper.StartsWithCommand(input, "notify")
            || ToolInputHelper.StartsWithCommand(input, "notification"))
        {
            var payload = ToolInputHelper.GetCommandTail(input, "notify", "notification");

            if (string.IsNullOrWhiteSpace(payload))
            {
                command = new NotificationCommand();
                return true;
            }

            var parts = payload.Split(["::"], 2, StringSplitOptions.None | StringSplitOptions.TrimEntries);
            command = parts.Length == 1
                ? new NotificationCommand("Jarvis", parts[0])
                : new NotificationCommand(string.IsNullOrWhiteSpace(parts[0]) ? "Jarvis" : parts[0], parts[1]);
            return true;
        }

        command = new NotificationCommand();
        return false;
    }
}
