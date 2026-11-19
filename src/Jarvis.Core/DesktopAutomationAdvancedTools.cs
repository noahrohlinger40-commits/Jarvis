// DesktopAutomationAdvancedTools.cs: the assistant tools for listing windows and media keys (windows, media).
// Each tool recognises typed commands (CanHandle), parses them with UiCommandParsing (bottom of this file) and calls IDesktopAutomationService.
// The tool classes follow the IAssistantTool pattern: Name, Description (shown to the model), CanHandle (is this input mine?), ExecuteAsync (do it).
namespace Jarvis.Core;

// Tool that lists open desktop windows, optionally filtered by a word ("windows code").
public sealed class WindowsTool : IAssistantTool
{
    public string Name => "windows";

    public string Description => "List visible desktop windows. `windows` or `windows code`.";

    // Matches "windows", "list windows", "show windows" or "visible windows" at the start of the input.
    public bool CanHandle(string input) => UiCommandParsing.TryParseWindowsQuery(input, out _);

    public async Task<ToolResult> ExecuteAsync(string input, ToolContext context, CancellationToken cancellationToken)
    {
        if (!UiCommandParsing.TryParseWindowsQuery(input, out var target))
        {
            return new ToolResult("Usage: windows [filter]");
        }

        var result = await context.DesktopAutomation.ListOpenWindowsAsync(target, cancellationToken);
        var success = !UiCommandParsing.LooksLikeFailure(result);
        return new ToolResult(result, VerificationText: result, SummaryText: success ? "Listed windows." : "Window lookup failed.");
    }
}

// Tool that sends the keyboard media keys (play, pause, next, previous, volume, mute) to Windows.
public sealed class MediaTool : IAssistantTool
{
    public string Name => "media";

    public string Description => "Send system media keys. `media next`, `media pause`, `volume up 3`, or `mute`.";

    public bool CanHandle(string input) => UiCommandParsing.TryParseMediaCommand(input, out _);

    public async Task<ToolResult> ExecuteAsync(string input, ToolContext context, CancellationToken cancellationToken)
    {
        if (!UiCommandParsing.TryParseMediaCommand(input, out var command))
        {
            return new ToolResult("Usage: media <play|pause|toggle|stop|next|previous|volume up|volume down|mute> [count]");
        }

        var result = await context.DesktopAutomation.ControlMediaAsync(command, cancellationToken);
        var success = !UiCommandParsing.LooksLikeFailure(result);
        return new ToolResult(result, VerificationText: result, SummaryText: success ? "Sent media command." : "Media command failed.");
    }
}

// Text parsing for the commands in this file. The class is split over several files; this part handles window-list and media commands.
internal static partial class UiCommandParsing
{
    // "windows [filter]": whatever follows the command word becomes the filter.
    public static bool TryParseWindowsQuery(string input, out string target)
    {
        target = ToolInputHelper.GetCommandTail(input, "windows", "list windows", "show windows", "visible windows");
        return ToolInputHelper.StartsWithCommand(input, "windows")
            || ToolInputHelper.StartsWithCommand(input, "list windows")
            || ToolInputHelper.StartsWithCommand(input, "show windows")
            || ToolInputHelper.StartsWithCommand(input, "visible windows");
    }

    // Turns spoken media phrases into short commands: "media next", "next track", "skip track", "volume up 3", "mute" and so on.
    public static bool TryParseMediaCommand(string input, out string command)
    {
        command = string.Empty;

        if (ToolInputHelper.StartsWithCommand(input, "media"))
        {
            command = ToolInputHelper.GetCommandTail(input, "media");
            return !string.IsNullOrWhiteSpace(command);
        }

        foreach (var direct in new[]
                 {
                     ("play media", "play"),
                     ("pause media", "pause"),
                     ("resume media", "resume"),
                     ("toggle media", "toggle"),
                     ("stop media", "stop"),
                     ("next track", "next"),
                     ("previous track", "previous"),
                     ("prev track", "previous"),
                     ("skip track", "next"),
                     ("mute audio", "mute"),
                     ("toggle mute", "mute")
                 })
        {
            if (!ToolInputHelper.StartsWithCommand(input, direct.Item1))
            {
                continue;
            }

            command = direct.Item2;
            return true;
        }

        if (ToolInputHelper.StartsWithCommand(input, "volume up")
            || ToolInputHelper.StartsWithCommand(input, "volume down"))
        {
            var prefix = ToolInputHelper.StartsWithCommand(input, "volume up") ? "volume up" : "volume down";
            var tail = ToolInputHelper.GetCommandTail(input, prefix);
            command = string.IsNullOrWhiteSpace(tail)
                ? prefix
                : $"{prefix} {tail}";
            return true;
        }

        if (ToolInputHelper.MatchesExact(input, "mute"))
        {
            command = "mute";
            return true;
        }

        return false;
    }
}
