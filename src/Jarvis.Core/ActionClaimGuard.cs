// The "honesty gate": catches replies where the model says it did something ("I've opened Chrome") although no
// tool actually ran. AssistantPlanner.cs calls IsUnverifiedClaim and Acted after the model answers, then either
// nudges the model to try again or swaps in HonestReplyAfter; AssistantPlanner.Intents.cs runs the same check on its
// summary reply. Pure text checks, no I/O.
using System.Text.RegularExpressions;

namespace Jarvis.Core;

/// <summary>Catches a reply that narrates an action ("I've opened...", "Searching the inbox...") when no tool ran this turn.</summary>
public static class ActionClaimGuard
{
    // Start of every "I cannot do this" reply; also used to recognise such a reply so it is never flagged as a false claim.
    public const string CannotPrefix = "I can't do that yet";

    // Extra instruction sent back to the model when it claimed an action without running a tool.
    public const string Nudge =
        "No acting tool ran this turn (reading the screen or notes is not an action), so none of that happened on the computer. " +
        "If one of your tools can do it, call it now. " +
        "If none can, reply in one sentence that starts with \"" + CannotPrefix + ":\" and name what is missing. " +
        "If the request only needed an answer, give the answer without narrating actions. " +
        "Do not describe actions you did not take.";

    // Fallback reply shown when the model still claims an action after the nudge.
    public const string HonestReply = CannotPrefix + ": none of my tools cover this request, so I did nothing.";

    /// <summary>The line that replaces a caught claim. "None of my tools" would be false after a file search that ran and came up empty.</summary>
    public static string HonestReplyAfter(IEnumerable<PlannerToolResult> toolResults) =>
        toolResults.Any(result => result.IsError && string.Equals(result.ApiName, "find files", StringComparison.OrdinalIgnoreCase))
            ? "I searched your files and found nothing by that name."
            : HonestReply;

    // Verbs that, after "I" (or "I've", "I'm"...), make a sentence sound like a completed or ongoing action. Written as regex
    // alternatives, e.g. "open(?:ed|ing)?" matches open, opened, opening.
    private const string Verbs =
        "open(?:ed|ing)?|check(?:ed|ing)?|search(?:ed|ing)?|pull(?:ed|ing)?|found|find|updat(?:ed|ing)|sent|send(?:ing)?|" +
        "creat(?:ed|ing)|edit(?:ed|ing)|merg(?:ed|ing)|mov(?:ed|ing)|sav(?:ed|ing)|read(?:ing)?|scann(?:ed|ing)|clos(?:ed|ing)|" +
        "launch(?:ed|ing)|ran|running|start(?:ed|ing)|look(?:ed|ing)|locat(?:ed|ing)|review(?:ed|ing)|load(?:ed|ing)|" +
        "draft(?:ed|ing)|rewrit(?:ten|ing)|rewrote|combin(?:ed|ing)|tak(?:en|ing)|brows(?:ed|ing)|navigat(?:ed|ing)|appl(?:ied|ying)|" +
        "add(?:ed|ing)|fix(?:ed|ing)|went|switch(?:ed|ing)|got (?:full )?access";

    // ponytail: word-list heuristic of past/present claims only; promises like "I'll handle it" pass, because flagging the future forms
    // cost plain answers their pass. Upgrade path: a one-line judge call to the model if this falls short.
    // Matches a claimed action: "I" + optional helper + an action verb; Searching/Checking/Opening/Pulling or "Here's what I found";
    // a reply starting with "Done"; a check mark; and lines that imitate tool output ("Opened application target: ... is now running"),
    // which the model sometimes writes with no tool call. "is now running/open" counts only at the end of a sentence, so
    // "the pharmacy is now open until 9" and "now open source" are plain answers. Example hit: "I've opened the calendar".
    private static readonly Regex Claim = new(
        @"(?:\bI(?:'ve| have|'m| am| just)?\s+(?:just |now |already |currently |also )?(?:" + Verbs + @")\b)|\b(?:Searching|Checking|Opening|Pulling|Here's what I found)\b|^Done\b|✅|(?m:^)(?:Opened|Launched|Closed|Added|Created|Saved|Sent) (?:application|browser|file|a |an |the )|\bis now (?:running|open)\s*(?:[.!]|$)",
        RegexOptions.IgnoreCase);

    // Tools whose work counts as real action for honesty purposes even though they only read: without this, "browse linkedin messages"
    // opened the page and the gate replaced the reply with "I did nothing", and "I found it in Portfolio Website - Upload" after a
    // correct file search became "none of my tools cover this".
    // "computer" and "brain" stay out on purpose: the lies came from calling one of those and then claiming an action.
    // ponytail: after find files, a false "I opened it" now gets through; split the verb list by tool if a run shows that happening.
    private static readonly HashSet<string> RealWork = new(StringComparer.OrdinalIgnoreCase)
    {
        "browse", "search web", "find files", "list files", "read file", "page", "calendar"
    };

    /// <summary>True when any tool this turn actually did something: not read-only (screen, notes, status...) and not an error.</summary>
    public static bool Acted(IEnumerable<PlannerToolResult> toolResults) =>
        toolResults.Any(result => !result.IsError && (RealWork.Contains(result.ApiName) || ToolSafetyService.ActedSuccessfully(result)));

    // True when the reply reads like a claimed action. Normalises curly apostrophes first so "I’ve" matches "I've".
    public static bool IsUnverifiedClaim(string reply)
    {
        if (string.IsNullOrWhiteSpace(reply)) return false;
        var text = reply.Replace('’', '\'').Replace('‘', '\'').Trim();
        if (text.StartsWith(CannotPrefix, StringComparison.OrdinalIgnoreCase)) return false;
        return Claim.IsMatch(text);
    }
}
