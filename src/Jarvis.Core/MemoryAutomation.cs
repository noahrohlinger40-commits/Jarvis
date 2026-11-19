// Decides what Jarvis should remember (and what it must NOT save) from each conversation turn. It is rule-based: plain phrases
// and regular expressions, no language model. After a turn the assistant (JarvisAssistant.cs, with help from Tools.cs and ActionSafety.cs) asks this class:
// (1) PrepareTurn: may this turn be logged at all? (2) ExtractLearnedPreferences: did the user say what to call them?
// (3) BuildEpisodicMemory / BuildConversationSummary: what is worth recording about what just happened?
// It also parses spoken reminders ('remind me to call mom at 5'). The results are MemoryWriteRequest objects that JsonMemoryStore saves.
// Caution: matching is by substring and loose patterns, so it can occasionally learn something from a sentence that merely contains the phrase.
using System.Globalization;
using System.Text.RegularExpressions;

namespace Jarvis.Core;

// The verdict from PrepareTurn: the text to act on, the privacy mode ('persistent', 'off-the-record' or 'sensitive'),
// whether to write the transcript and automatic memory, and a reason shown to the user.
internal sealed record TurnPersistencePlan(
    string EffectiveInput,
    string PrivacyMode,
    bool PersistTranscript,
    bool PersistAutomaticMemory,
    string Reason);

// Static helpers that turn conversation text into memory-write requests. Holds no state.
internal static class MemoryAutomation
{
    // Phrases that, at the start of a message, mean 'do not record this turn at all'.
    private static readonly string[] WholeTurnPrivacyPrefixes =
    [
        "off the record",
        "dont log this",
        "don't log this",
        "do not log this",
        "dont save this",
        "don't save this",
        "do not save this",
        "private request"
    ];

    // Words that suggest secrets (passwords, keys, codes). If any appears anywhere in a message the turn is not saved.
    // Matching is a plain substring test, so harmless words that contain one of these (for example 'token' inside 'tokenizer') are blocked too: it errs on the side of not saving.
    private static readonly string[] SensitiveSignals =
    [
        "password",
        "passcode",
        "pin code",
        "api key",
        "token",
        "secret",
        "social security",
        "ssn",
        "one-time code",
        "one time code",
        "otp"
    ];

    // Matches 13 to 19 digits, optionally separated by single spaces or dashes: a payment-card-looking number such as '4111 1111 1111 1111'.
    private static readonly Regex CardLikeNumberRegex = new(@"\b(?:\d[ -]?){13,19}\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    // Matches how the user wants to be addressed: 'call me Noah', 'my nickname is X', 'my name is X'.
    private static readonly Regex AddressPreferenceRegex = new(@"\b(?:call me|you can call me|my nickname is|my preferred name is|refer to me as|address me as|my name is)\s+(?<name>.+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    // Matches the start of a reminder request: 'remind me (to/about/that)' or 'set/add/create/make a (new) reminder'.
    private static readonly Regex ReminderPrefixRegex = new(@"^(?:remind\s+me|(?:set|add|create|make)\s+(?:a\s+)?(?:new\s+)?reminder)(?:\s+(?:(?:to|about|that)\b)?|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    // The next two patterns read relative reminder times. ReminderSpan is the reusable part: either 'half an' or a number word/digits,
    // then a unit (seconds, minutes, hours, days). Named groups: half, n (the amount), u (the unit).
    // "in 10 minutes", "after an hour", "30 seconds from now", "half an hour from now"
    private const string ReminderSpan = @"(?:(?<half>half\s+an)|(?<n>an?|one|two|three|four|five|six|seven|eight|nine|ten|fifteen|twenty|thirty|\d+(?:\.\d+)?))\s*(?<u>seconds?|secs?|minutes?|mins?|hours?|hrs?|days?)\b";
    // Matches 'in <span>' / 'after <span>' or '<span> from now'. The pattern is written once and reused through ReminderSpan.
    private static readonly Regex RelativeReminderRegex = new($@"\b(?:in|after)\s+{ReminderSpan}|\b{ReminderSpan}\s+from\s+now\b", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // First gate for every user message: decides whether the turn may be written to the transcript and to automatic memory.
    public static TurnPersistencePlan PrepareTurn(string input)
    {
        var trimmed = input.Trim();

        // Check 1: the user explicitly said 'off the record' etc. Strip that phrase and process the rest, but save nothing.
        foreach (var prefix in WholeTurnPrivacyPrefixes)
        {
            if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var remainder = trimmed[prefix.Length..].TrimStart(' ', ',', ':', ';', '-');
            return new TurnPersistencePlan(
                string.IsNullOrWhiteSpace(remainder) ? trimmed : remainder,
                "off-the-record",
                PersistTranscript: false,
                PersistAutomaticMemory: false,
                "The user explicitly marked this turn as off the record.");
        }

        // Check 2: it looks like it contains a secret, so process it but save nothing.
        if (LooksSensitive(trimmed))
        {
            return new TurnPersistencePlan(
                trimmed,
                "sensitive",
                PersistTranscript: false,
                PersistAutomaticMemory: false,
                "Sensitive content was detected, so Jarvis will not persist this turn.");
        }

        // Otherwise the turn may be saved normally.
        return new TurnPersistencePlan(trimmed, "persistent", PersistTranscript: true, PersistAutomaticMemory: true, string.Empty);
    }

    // Packages what Jarvis can see right now (clock, active app, window, recent activity, default location) into a context snapshot attached to new memories. Keeps at most 5 distinct recent activities.
    public static InteractionContextSnapshot BuildInteractionContext(
        DesktopContextSnapshot desktopContext,
        string defaultLocation,
        IEnumerable<string> recentActivity)
    {
        var location = string.IsNullOrWhiteSpace(defaultLocation) ? string.Empty : defaultLocation.Trim();
        var activities = recentActivity
            .Where(activity => !string.IsNullOrWhiteSpace(activity))
            .Select(activity => activity.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToArray();

        return new InteractionContextSnapshot(
            desktopContext.LocalTime.ToUniversalTime(),
            GetTimeOfDay(desktopContext.LocalTime),
            location,
            desktopContext.ActiveApplication,
            desktopContext.ActiveWindow,
            desktopContext.VisibleApplications,
            activities);
    }

    // Checks the user's message for how they want to be addressed ('call me Noah'), the only preference Jarvis still learns by rule.
    // Returns zero or one item.
    public static IReadOnlyList<MemoryWriteRequest> ExtractLearnedPreferences(
        string userInput,
        InteractionContextSnapshot context)
    {
        return TryExtractAddressPreference(userInput.Trim(), context, out var addressPreference)
            ? new[] { addressPreference }
            : Array.Empty<MemoryWriteRequest>();
    }

    // After a turn, writes a short 'Task outcome' note if a useful tool (open app, read file, search web...) succeeded.
    // Uses at most the first two successful tools. Returns null when nothing worth recording happened.
    public static MemoryWriteRequest? BuildEpisodicMemory(
        string userInput,
        IReadOnlyList<AssistantToolExecution> toolResults,
        InteractionContextSnapshot context)
    {
        if (string.IsNullOrWhiteSpace(userInput))
        {
            return null;
        }

        var successful = toolResults
            .Where(result => result.Succeeded && ShouldCaptureToolOutcome(result.ToolName))
            .Take(2)
            .ToArray();

        if (successful.Length == 0)
        {
            return null;
        }

        var outcomeText = string.Join("; ", successful.Select(result => $"{result.ToolName}: {TrimSentence(result.Summary)}"));
        var content = $"Task outcome: {TrimSentence(userInput)} -> {outcomeText}";

        return new MemoryWriteRequest(
            content,
            Kind: "episodic",
            Category: "activity",
            Source: "turn",
            Tags: ["episodic", "activity"],
            Context: context,
            CreatedAtUtc: DateTimeOffset.UtcNow,
            ImportanceScore: 0.64d,
            RetentionPolicy: "episodic");
    }

    // True if the message starts like a reminder request.
    public static bool IsReminderRequest(string input) => ReminderPrefixRegex.IsMatch(input.Trim());

    // Builds the memory note for a reminder: marked pending and kept until completed, with a high fixed importance (0.82).
    public static MemoryWriteRequest BuildReminderMemory(
        string reminderText,
        DateTimeOffset reminderAtLocal,
        InteractionContextSnapshot context,
        DateTimeOffset nowLocal)
    {
        return new MemoryWriteRequest(
            $"Reminder: {TrimSentence(reminderText)}",
            Kind: "reminder",
            Category: "reminders",
            Source: "manual",
            Tags: ["reminder", "time-based"],
            Context: context,
            CreatedAtUtc: nowLocal.ToUniversalTime(),
            ImportanceScore: 0.82d,
            RetentionPolicy: "until-complete",
            ReminderAtUtc: reminderAtLocal.ToUniversalTime(),
            ReminderText: TrimSentence(reminderText),
            ReminderStatus: "pending");
    }

    // Condenses older conversation turns into one 'summary' note. It skips turns already covered by an earlier summary and turns marked
    // off the record, always leaves the newest 6 turns un-summarized, and only summarizes when at least 6 older turns are waiting (taking at most 8).
    public static MemoryWriteRequest? BuildConversationSummary(
        IReadOnlyList<AssistantTurn> turns,
        IReadOnlyList<MemoryNote> notes)
    {
        if (turns.Count == 0)
        {
            return null;
        }

        // End time of the latest existing summary, so the same turns are not summarized twice.
        var summarizedThroughUtc = notes
            .Where(note => string.Equals(note.Kind, "summary", StringComparison.OrdinalIgnoreCase))
            .Max(note => note.SummaryWindowEndUtc) ?? DateTimeOffset.MinValue;

        var eligibleTurns = turns
            .Where(turn => turn.TimestampUtc > summarizedThroughUtc
                && !string.Equals(turn.Privacy, "off-the-record", StringComparison.OrdinalIgnoreCase))
            .OrderBy(turn => turn.TimestampUtc)
            .ToArray();

        // Tuning numbers: keep the latest 6 turns as live context, need 6 older turns before summarizing, and fold at most 8 into one summary.
        const int reserveRecentTurnCount = 6;
        const int minimumSummaryTurnCount = 6;
        const int summaryWindowTurnCount = 8;

        if (eligibleTurns.Length <= reserveRecentTurnCount)
        {
            return null;
        }

        var olderTurns = eligibleTurns
            .Take(eligibleTurns.Length - reserveRecentTurnCount)
            .Take(summaryWindowTurnCount)
            .ToArray();

        if (olderTurns.Length < minimumSummaryTurnCount)
        {
            return null;
        }

        // The up-to-3 distinct things the user asked for.
        var requests = olderTurns
            .Select(turn => TrimSentence(turn.UserInput))
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToArray();
        // The up-to-3 distinct successful tool results.
        var outcomes = olderTurns
            .SelectMany(turn => turn.ToolResults ?? Array.Empty<AssistantToolExecution>())
            .Where(result => result.Succeeded && !string.IsNullOrWhiteSpace(result.Summary))
            .Select(result => TrimSentence(result.Summary))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToArray();

        if (requests.Length == 0 && outcomes.Length == 0)
        {
            return null;
        }

        // Up to 3 distinct apps that were active.
        var apps = olderTurns
            .Select(turn => turn.Context?.ActiveApp)
            .Where(app => !string.IsNullOrWhiteSpace(app))
            .Select(app => app!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToArray();
        var location = olderTurns
            .Select(turn => turn.Context?.Location)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
        // The most common time of day among the turns.
        var dominantTimeOfDay = olderTurns
            .Select(turn => turn.Context?.TimeOfDay)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .GroupBy(value => value!, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .Select(group => group.Key)
            .FirstOrDefault() ?? string.Empty;
        var recentActivity = outcomes.Length > 0 ? outcomes : requests;
        // Context attached to the summary, stamped with the last summarized turn's time.
        var summaryContext = new InteractionContextSnapshot(
            olderTurns[^1].TimestampUtc,
            dominantTimeOfDay,
            location,
            apps.FirstOrDefault() ?? string.Empty,
            string.Empty,
            apps,
            recentActivity);
        var content = BuildSummaryText(olderTurns, requests, outcomes, apps);

        return new MemoryWriteRequest(
            content,
            Kind: "summary",
            Category: "conversation",
            Source: "summary",
            Tags: ["summary", "conversation"],
            Context: summaryContext,
            CreatedAtUtc: olderTurns[^1].TimestampUtc,
            SummaryWindowStartUtc: olderTurns[0].TimestampUtc,
            SummaryWindowEndUtc: olderTurns[^1].TimestampUtc);
    }

    // True if the text contains a secret-looking word (see SensitiveSignals) or a card-like number.
    public static bool LooksSensitive(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        if (SensitiveSignals.Any(signal => input.Contains(signal, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return CardLikeNumberRegex.IsMatch(input);
    }

    // Detects how Noah wants to be addressed, including the negative form ('don't call me X').
    private static bool TryExtractAddressPreference(
        string input,
        InteractionContextSnapshot context,
        out MemoryWriteRequest preference)
    {
        var trimmed = input.Trim();

        if (trimmed.StartsWith("don't call me ", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("dont call me ", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("do not call me ", StringComparison.OrdinalIgnoreCase))
        {
            var prefix = trimmed.StartsWith("do not call me ", StringComparison.OrdinalIgnoreCase)
                ? "do not call me "
                : trimmed.StartsWith("don't call me ", StringComparison.OrdinalIgnoreCase)
                    ? "don't call me "
                    : "dont call me ";
            var nameToAvoid = TrimSentence(trimmed[prefix.Length..]);

            if (!string.IsNullOrWhiteSpace(nameToAvoid))
            {
                preference = BuildPreference(
                    $"Address preference: do not call the user {nameToAvoid}.",
                    "address",
                    context,
                    ["preference", "address", "avoid"]);
                return true;
            }
        }

        var match = AddressPreferenceRegex.Match(trimmed);

        if (!match.Success)
        {
            preference = default!;
            return false;
        }

        var name = TrimSentence(match.Groups["name"].Value);

        if (string.IsNullOrWhiteSpace(name))
        {
            preference = default!;
            return false;
        }

        preference = BuildPreference(
            $"Address preference: call the user {name}.",
            "address",
            context,
            ["preference", "address"]);
        return true;
    }

    // Wraps a learned preference as a write request (source 'learned').
    private static MemoryWriteRequest BuildPreference(
        string content,
        string category,
        InteractionContextSnapshot context,
        string[] tags)
    {
        return new MemoryWriteRequest(
            content,
            Kind: "preference",
            Category: category,
            Source: "learned",
            Tags: tags,
            Context: context,
            CreatedAtUtc: DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Reads "remind me to call mom at 5" or "set a reminder 30 seconds from now to take my medication".
    /// True only with both a task and a time still ahead. Either way the outputs hold what was understood
    /// (an empty task, or <c>default</c> for no time), so the caller can ask for just the missing part.
    /// </summary>
    // Details: the relative form ('in 10 minutes') is tried first; otherwise the calendar's own date/time reader (CalendarText) is used.
    // A time with no date means its next occurrence; a date with no time means 9:00 AM.
    internal static bool TryParseReminder(
        string input,
        DateTimeOffset nowLocal,
        out string reminderText,
        out DateTimeOffset reminderAtLocal)
    {
        reminderText = string.Empty;
        reminderAtLocal = default;

        // Step 1: the message must start like a reminder request.
        var prefix = ReminderPrefixRegex.Match(input.Trim());

        if (!prefix.Success)
        {
            return false;
        }

        var payload = input.Trim()[prefix.Length..];
        // Step 2: find the time, either relative ('in 10 minutes') or calendar-style, and cut it out of the text.
        var relative = RelativeReminderRegex.Match(payload);

        if (relative.Success)
        {
            reminderAtLocal = nowLocal.Add(ParseRelativeReminder(relative));
            payload = payload.Remove(relative.Index, relative.Length);
        }
        else
        {
            // The calendar's reader, so reminders understand the same times: "3.30", "2:29 a. m.", "noon".
            var (date, time) = CalendarText.ExtractWhen(ref payload, nowLocal.DateTime);
            var today = DateOnly.FromDateTime(nowLocal.DateTime);

            // A time with no date is the next one; a date with no time ("tomorrow") is 9 AM.
            var at = time is TimeOnly clock
                ? (date ?? (clock > TimeOnly.FromDateTime(nowLocal.DateTime) ? today : today.AddDays(1))).ToDateTime(clock)
                : date?.ToDateTime(new TimeOnly(9, 0));

            if (at is not null)
            {
                // An unspecified-kind DateTime takes the local offset for its own date, so DST is right.
                reminderAtLocal = new DateTimeOffset(at.Value);
            }
        }

        // Step 3: what is left is the task; the next regexes tidy it up. Valid only if the task is non-empty and the time is in the future.
        // Words stranded by the removed time: "to stretch" from "in 10 minutes to stretch", "call mom at".
        reminderText = Regex.Replace(payload, @"\s+", " ").Trim(' ', ',', '.', ';', ':', '-');
        // An answer to "What should I remind you about?" can restate the request: "(and I want you to) remind me about my medication".
        reminderText = Regex.Replace(reminderText, @"^(?:.*?\bremind\s+me\b|(?:set|add|create|make)\s+(?:a\s+)?(?:new\s+)?reminder\b)\s*", string.Empty, RegexOptions.IgnoreCase);
        reminderText = Regex.Replace(reminderText, @"^(?:(?:to|about|that)\b\s*)+|(?:\s+(?:at|on|in|for|by|and|to|,))+$", string.Empty, RegexOptions.IgnoreCase).Trim(' ', ',', '.');
        return reminderText.Length > 0 && reminderAtLocal > nowLocal;
    }

    // Converts a relative-time match into a duration: the amount (number word or digits; 'half an' = 0.5) times the unit taken from the first letter (s, m, h, otherwise days).
    private static TimeSpan ParseRelativeReminder(Match match)
    {
        var amount = match.Groups["half"].Success ? 0.5 : match.Groups["n"].Value.ToLowerInvariant() switch
        {
            "a" or "an" or "one" => 1,
            "two" => 2,
            "three" => 3,
            "four" => 4,
            "five" => 5,
            "six" => 6,
            "seven" => 7,
            "eight" => 8,
            "nine" => 9,
            "ten" => 10,
            "fifteen" => 15,
            "twenty" => 20,
            "thirty" => 30,
            var number => double.Parse(number, CultureInfo.InvariantCulture)
        };

        return char.ToLowerInvariant(match.Groups["u"].Value[0]) switch
        {
            's' => TimeSpan.FromSeconds(amount),
            'm' => TimeSpan.FromMinutes(amount),
            'h' => TimeSpan.FromHours(amount),
            _ => TimeSpan.FromDays(amount)
        };
    }

    // Writes the plain-text body of a conversation summary.
    private static string BuildSummaryText(
        IReadOnlyList<AssistantTurn> turns,
        IReadOnlyList<string> requests,
        IReadOnlyList<string> outcomes,
        IReadOnlyList<string> apps)
    {
        var start = turns[0].TimestampUtc;
        var end = turns[^1].TimestampUtc;
        var parts = new List<string>
        {
            $"Conversation summary from {start:yyyy-MM-dd HH:mm}Z to {end:yyyy-MM-dd HH:mm}Z."
        };

        if (requests.Count > 0)
        {
            parts.Add("Key requests: " + string.Join(" | ", requests));
        }

        if (outcomes.Count > 0)
        {
            parts.Add("Outcomes: " + string.Join(" | ", outcomes));
        }

        if (apps.Count > 0)
        {
            parts.Add("Active apps: " + string.Join(" | ", apps));
        }

        return string.Join(" ", parts);
    }

    // Buckets the hour: 5-11 morning, 12-16 afternoon, 17-21 evening, otherwise night.
    private static string GetTimeOfDay(DateTimeOffset localTime)
    {
        return localTime.Hour switch
        {
            >= 5 and < 12 => "morning",
            >= 12 and < 17 => "afternoon",
            >= 17 and < 22 => "evening",
            _ => "night"
        };
    }

    // Which tools are worth remembering the outcome of (opening, reading, searching, running, screen analysis).
    private static bool ShouldCaptureToolOutcome(string toolName)
    {
        var normalized = toolName.Trim().ToLowerInvariant();

        return normalized is "open app"
            or "open path"
            or "read file"
            or "find files"
            or "browse"
            or "search web"
            or "research"
            or "run"
            or "screen"
            or "analyze image"
            or "analyze document";
    }

    // Flattens line breaks and cuts text at 180 characters so long text does not bloat memory.
    private static string TrimSentence(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var flattened = value.ReplaceLineEndings(" ").Trim();
        return flattened.Length <= 180 ? flattened : flattened[..180] + "...";
    }
}
