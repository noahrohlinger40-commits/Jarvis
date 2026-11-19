// Plain data shapes for Jarvis's memory system. Nothing here touches disk.
// JsonMemoryStore.cs stores them, MemoryPolicies.cs and MemorySemanticSearch.cs read them, and the prompt builders call the
// ToPromptText/ToInlineText methods to turn them into text for the language model. MemoryNote itself lives in AssistantTurn.cs.
// The 'Safe...' properties exist because records read from an older memory.json can have null arrays.
using System.Text;

namespace Jarvis.Core;

// A snapshot of Noah's situation when something happened: time of day, location, the active app and window, and recent activity.
public sealed record InteractionContextSnapshot(
    DateTimeOffset ObservedAtUtc,
    string TimeOfDay = "",
    string Location = "",
    string ActiveApp = "",
    string ActiveWindow = "",
    string[]? VisibleApps = null,
    string[]? RecentActivity = null)
{
    // A shared 'no context' value (its time is when the type was first used).
    public static InteractionContextSnapshot Empty { get; } = new(DateTimeOffset.UtcNow);

    // RecentActivity, or an empty array if it is null.
    public string[] SafeRecentActivity => RecentActivity ?? Array.Empty<string>();

    // One-line version of the context for logs and tool output; shows at most 3 recent activities.
    public string ToInlineText()
    {
        var parts = new List<string>();

        AddIfPresent(parts, TimeOfDay, "time");
        AddIfPresent(parts, Location, "location");
        AddIfPresent(parts, ActiveApp, "app");
        AddIfPresent(parts, ActiveWindow, "window");

        if (SafeRecentActivity.Length > 0)
        {
            parts.Add("recent activity " + string.Join(", ", SafeRecentActivity.Take(3)));
        }

        return parts.Count == 0 ? "No contextual cues." : string.Join(" | ", parts);
    }

    // Adds 'label value' to the list unless the value is blank.
    private static void AddIfPresent(ICollection<string> parts, string value, string label)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            parts.Add($"{label} {value.Trim()}");
        }
    }
}

// One key/value fact about an entity, for example label=gym or deviceType=laptop.
public sealed record MemoryAttribute(
    string Key,
    string Value);

// An 'entity' mentioned in a note (a person, device, project, place...) with alternative names and attributes.
public sealed record StructuredMemoryReference(
    string Kind,
    string Name,
    string Summary = "",
    string[]? Aliases = null,
    MemoryAttribute[]? Attributes = null)
{
    // Aliases, or an empty array if null.
    public string[] SafeAliases => Aliases ?? Array.Empty<string>();

    // Attributes, or an empty array if null.
    public MemoryAttribute[] SafeAttributes => Attributes ?? Array.Empty<MemoryAttribute>();

    // One-line description such as '[Device] Atlas | aliases my laptop | deviceType: laptop'.
    public string ToInlineText()
    {
        var parts = new List<string>
        {
            $"[{NormalizeKind(Kind)}] {Name.Trim()}"
        };

        if (!string.IsNullOrWhiteSpace(Summary))
        {
            parts.Add(Summary.Trim());
        }

        if (SafeAliases.Length > 0)
        {
            parts.Add("aliases " + string.Join(", ", SafeAliases));
        }

        if (SafeAttributes.Length > 0)
        {
            parts.Add(string.Join(", ", SafeAttributes.Select(attribute => $"{attribute.Key}: {attribute.Value}")));
        }

        return string.Join(" | ", parts);
    }

    // Capitalizes the kind for display, or 'Entity' if it is blank.
    private static string NormalizeKind(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "entity";
        }

        var trimmed = value.Trim().ToLowerInvariant();
        return char.ToUpperInvariant(trimmed[0]) + trimmed[1..];
    }
}

// A reminder as shown in the profile: id, text, due time and status.
public sealed record ReminderSnapshot(
    string Id,
    string Text,
    DateTimeOffset ReminderAtUtc,
    string Status);

// One entity as shown in the profile, with how many times it was seen.
public sealed record StructuredMemorySummary(
    string Kind,
    string Name,
    string Summary,
    int ObservationCount,
    DateTimeOffset LastObservedAtUtc);

// A request to remember something. Only Content is required; JsonMemoryStore and MemoryPolicies fill in the rest
// (importance, retention, expiry) when they are left empty.
public sealed record MemoryWriteRequest(
    string Content,
    string Kind = "note",
    string Category = "",
    string Source = "manual",
    string Privacy = "persistent",
    string[]? Tags = null,
    InteractionContextSnapshot? Context = null,
    DateTimeOffset? CreatedAtUtc = null,
    DateTimeOffset? SummaryWindowStartUtc = null,
    DateTimeOffset? SummaryWindowEndUtc = null,
    double? ImportanceScore = null,
    string RetentionPolicy = "",
    DateTimeOffset? ExpiresAtUtc = null,
    bool UserApprovedRetention = false,
    StructuredMemoryReference[]? Entities = null,
    DateTimeOffset? ReminderAtUtc = null,
    string ReminderText = "",
    string ReminderStatus = "");

// A request to change an existing note by id. Unlike a write request, every editable field is supplied (the caller copies the current values).
public sealed record MemoryUpdateRequest(
    string Id,
    string Content,
    string Kind,
    string Category,
    string Privacy,
    string[] Tags,
    double ImportanceScore,
    string RetentionPolicy,
    DateTimeOffset? ExpiresAtUtc,
    bool UserApprovedRetention,
    DateTimeOffset? ReminderAtUtc,
    string ReminderText,
    string ReminderStatus,
    StructuredMemoryReference[]? Entities = null);

// What Jarvis knows about the present moment when it searches memory, so notes learned in a similar situation rank higher.
public sealed record MemoryQueryContext(
    string Query,
    string TimeOfDay = "",
    string Location = "",
    string ActiveApp = "",
    string ActiveWindow = "",
    string[]? RecentActivity = null)
{
    public string[] SafeRecentActivity => RecentActivity ?? Array.Empty<string>();
}

// One preference Jarvis has learned, with how often it was seen.
public sealed record LearnedPreference(
    string Category,
    string Value,
    int ObservationCount,
    DateTimeOffset LastObservedAtUtc);

// The summary of everything Jarvis has learned about Noah: counts plus the top preferences, entities and reminders.
public sealed record MemoryProfileSnapshot(
    int TotalMemories,
    int PreferenceCount,
    int SummaryCount,
    int StructuredMemoryCount,
    int PendingReminderCount,
    LearnedPreference[] Preferences,
    StructuredMemorySummary[] StructuredMemories,
    ReminderSnapshot[] PendingReminders)
{
    // A profile with nothing in it.
    public static MemoryProfileSnapshot Empty { get; } = new(
        0,
        0,
        0,
        0,
        0,
        Array.Empty<LearnedPreference>(),
        Array.Empty<StructuredMemorySummary>(),
        Array.Empty<ReminderSnapshot>());

    // Compact profile for the language model: counts, up to 3 items per preference category, per entity kind, and up to 4 reminders.
    public string ToPromptText()
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Memories: {TotalMemories}");
        builder.AppendLine($"Preferences: {PreferenceCount}");
        builder.AppendLine($"Summaries: {SummaryCount}");
        builder.AppendLine($"Structured memories: {StructuredMemoryCount}");
        builder.AppendLine($"Pending reminders: {PendingReminderCount}");

        foreach (var category in OrderedPreferenceCategories)
        {
            var matches = Preferences
                .Where(preference => string.Equals(preference.Category, category, StringComparison.OrdinalIgnoreCase))
                .Take(3)
                .ToArray();

            builder.AppendLine(matches.Length == 0
                ? $"{TitleCase(category)}: none"
                : $"{TitleCase(category)}:{Environment.NewLine}{string.Join(Environment.NewLine, matches.Select(RenderPreference))}");
        }

        foreach (var kind in OrderedStructuredKinds)
        {
            var matches = StructuredMemories
                .Where(memory => string.Equals(memory.Kind, kind, StringComparison.OrdinalIgnoreCase))
                .Take(3)
                .ToArray();

            if (matches.Length == 0)
            {
                continue;
            }

            builder.AppendLine($"{TitleCase(kind)}:");
            builder.AppendLine(string.Join(Environment.NewLine, matches.Select(RenderStructuredMemory)));
        }

        if (PendingReminders.Length > 0)
        {
            builder.AppendLine("Upcoming reminders:");
            builder.AppendLine(string.Join(Environment.NewLine, PendingReminders.Take(4).Select(RenderReminder)));
        }

        return builder.ToString().TrimEnd();
    }

    // Longer profile for showing to the user: up to 4 items per category and 6 reminders; empty categories are left out.
    public string ToDisplayText()
    {
        var lines = new List<string>
        {
            $"Stored memories: {TotalMemories}",
            $"Learned preferences: {PreferenceCount}",
            $"Conversation summaries: {SummaryCount}",
            $"Structured memories: {StructuredMemoryCount}",
            $"Pending reminders: {PendingReminderCount}"
        };

        if (PreferenceCount == 0 && StructuredMemoryCount == 0 && PendingReminderCount == 0)
        {
            lines.Add("No learned personalization signals yet.");
            return string.Join(Environment.NewLine, lines);
        }

        foreach (var category in OrderedPreferenceCategories)
        {
            var matches = Preferences
                .Where(preference => string.Equals(preference.Category, category, StringComparison.OrdinalIgnoreCase))
                .Take(4)
                .ToArray();

            if (matches.Length == 0)
            {
                continue;
            }

            lines.Add($"{TitleCase(category)}:");
            lines.AddRange(matches.Select(RenderPreference));
        }

        foreach (var kind in OrderedStructuredKinds)
        {
            var matches = StructuredMemories
                .Where(memory => string.Equals(memory.Kind, kind, StringComparison.OrdinalIgnoreCase))
                .Take(4)
                .ToArray();

            if (matches.Length == 0)
            {
                continue;
            }

            lines.Add($"{TitleCase(kind)}:");
            lines.AddRange(matches.Select(RenderStructuredMemory));
        }

        if (PendingReminders.Length > 0)
        {
            lines.Add("Upcoming reminders:");
            lines.AddRange(PendingReminders.Take(6).Select(RenderReminder));
        }

        return string.Join(Environment.NewLine, lines);
    }

    // One bullet line for a preference.
    private static string RenderPreference(LearnedPreference preference)
    {
        return $"- {preference.Value} | seen {preference.ObservationCount} time(s) | last observed {preference.LastObservedAtUtc:yyyy-MM-dd HH:mm}Z";
    }

    // One bullet line for an entity.
    private static string RenderStructuredMemory(StructuredMemorySummary summary)
    {
        var content = string.IsNullOrWhiteSpace(summary.Summary)
            ? summary.Name
            : $"{summary.Name} | {summary.Summary}";
        return $"- {content} | seen {summary.ObservationCount} time(s) | last observed {summary.LastObservedAtUtc:yyyy-MM-dd HH:mm}Z";
    }

    // One bullet line for a reminder, with the due time in local time.
    private static string RenderReminder(ReminderSnapshot reminder)
    {
        return $"- {reminder.Text} | due {reminder.ReminderAtUtc.ToLocalTime():yyyy-MM-dd h:mm tt} | status {NormalizeStatus(reminder.Status)}";
    }

    // Lower-cases the reminder status, defaulting to 'pending'.
    private static string NormalizeStatus(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "pending" : value.Trim().ToLowerInvariant();
    }

    // Capitalizes the first letter and lower-cases the rest.
    private static string TitleCase(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Other";
        }

        var trimmed = value.Trim().ToLowerInvariant();
        return char.ToUpperInvariant(trimmed[0]) + trimmed[1..];
    }

    // The order in which preference categories appear in the profile text.
    private static readonly string[] OrderedPreferenceCategories =
    [
        "tone",
        "response style",
        "address",
        "conversation",
        "phrasing",
        "apps",
        "locations",
        "automations",
        "routines",
        "habits",
        "favorites",
        "other"
    ];

    // The order in which entity kinds appear in the profile text.
    private static readonly string[] OrderedStructuredKinds =
    [
        "relationship",
        "person",
        "project",
        "device",
        "place",
        "routine",
        "habit",
        "playlist",
        "other"
    ];
}
