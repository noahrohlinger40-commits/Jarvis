// Short-term memory of the current session (last few turns), kept only in RAM. JarvisAssistant adds each finished turn
// and the planner asks for a snapshot to put into the model prompt so follow-ups like "open it" make sense.
// Long-term memory is separate (MemoryNote).
using System.Text;

namespace Jarvis.Core;

// One remembered turn of the current session.
public sealed record ConversationStateEntry(
    DateTimeOffset TimestampUtc,
    string UserInput,
    string ResponseText,
    string Intent,
    string Privacy,
    AssistantToolExecution[] ToolResults)
{
    // True for turns marked private, so callers can avoid saving or quoting them.
    public bool IsPrivate =>
        string.Equals(Privacy, "off-the-record", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Privacy, "sensitive", StringComparison.OrdinalIgnoreCase);
}

// Read-only view of recent turns, plus a guess at what the conversation is currently about.
public sealed record ConversationStateSnapshot(
    ConversationStateEntry[] Entries,
    string CurrentFocus,
    bool ContainsOffRecordTurns)
{
    public static ConversationStateSnapshot Empty { get; } = new(Array.Empty<ConversationStateEntry>(), string.Empty, false);

    // Renders the last 6 turns as compact text lines for the model prompt, with user text and tool summaries clipped short.
    public string ToPromptText()
    {
        if (Entries.Length == 0)
        {
            return "No short-term session state yet.";
        }

        var builder = new StringBuilder();
        builder.AppendLine($"Current focus: {OrNone(CurrentFocus)}");
        builder.AppendLine($"Off-the-record turns in session: {(ContainsOffRecordTurns ? "yes" : "no")}");
        builder.AppendLine("Recent session turns:");

        foreach (var entry in Entries.TakeLast(6))
        {
            builder.Append("- ");
            builder.Append(entry.TimestampUtc.ToLocalTime().ToString("MM-dd h:mm tt"));
            builder.Append(" | ");
            builder.Append(Trim(entry.UserInput, 110));
            builder.Append(" | intent ");
            builder.Append(string.IsNullOrWhiteSpace(entry.Intent) ? "unknown" : entry.Intent);

            if (entry.ToolResults.Length > 0)
            {
                var latestTool = entry.ToolResults.Last();
                builder.Append(" | tool ");
                builder.Append(latestTool.ToolName);
                builder.Append(": ");
                builder.Append(Trim(latestTool.Summary, 80));
            }

            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    private static string OrNone(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "none" : value.Trim();
    }

    private static string Trim(string value, int maxLength) => value.Clip(maxLength);
}

// Thread-safe rolling buffer of recent turns (oldest are dropped past the capacity).
public sealed class ShortTermConversationState
{
    private readonly int _capacity;
    private readonly object _gate = new();
    private readonly List<ConversationStateEntry> _entries = new();

    public ShortTermConversationState(int capacity = 18)
    {
        // Never smaller than 6, because the prompt text above shows the last 6.
        _capacity = Math.Max(6, capacity);
    }

    public void AddTurn(AssistantTurn turn)
    {
        ArgumentNullException.ThrowIfNull(turn);

        // The lock is needed because turns can be added from background work while the planner reads a snapshot.
        lock (_gate)
        {
            _entries.Add(new ConversationStateEntry(
                turn.TimestampUtc,
                turn.UserInput,
                turn.ResponseText,
                turn.Intent,
                turn.Privacy,
                turn.ToolResults ?? Array.Empty<AssistantToolExecution>()));

            if (_entries.Count > _capacity)
            {
                _entries.RemoveRange(0, _entries.Count - _capacity);
            }
        }
    }

    public ConversationStateSnapshot GetSnapshot(int maxResults)
    {
        lock (_gate)
        {
            if (_entries.Count == 0)
            {
                return ConversationStateSnapshot.Empty;
            }

            var entries = _entries
                .TakeLast(Math.Max(1, maxResults))
                .ToArray();
            // Focus is the newest tool result's summary if there is one, otherwise what the user last said.
            var latest = entries.Last();
            var currentFocus = latest.ToolResults.LastOrDefault()?.Summary;

            if (string.IsNullOrWhiteSpace(currentFocus))
            {
                currentFocus = latest.UserInput;
            }

            var containsOffRecordTurns = entries.Any(entry => entry.IsPrivate);

            return new ConversationStateSnapshot(entries, currentFocus ?? string.Empty, containsOffRecordTurns);
        }
    }
}
