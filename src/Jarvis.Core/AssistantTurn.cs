// Small data types for one finished conversation turn and one saved memory note, plus SpokenReply,
// which decides what part of a reply is read aloud. The turn is built at the end of
// JarvisAssistant.cs and stored in memory; the UI and voice code read these records.
using System.Text;
using System.Text.RegularExpressions;

namespace Jarvis.Core;

// Turns a reply into the shorter text that the voice actually says (the full reply stays on screen).
public static class SpokenReply
{
    // Maximum characters of a reply to speak before cutting it off.
    private const int Budget = 300; // about 20 seconds of speech

    /// <summary>
    /// The part of a reply to say aloud. Every reply is spoken; a long one is cut to its opening
    /// sentences, since the full text is on screen. <paramref name="speakInFull"/> replies (page
    /// reading, calendar) are read whole.
    /// </summary>
    public static string Build(string reply, bool speakInFull)
    {
        if (speakInFull)
        {
            return reply.Trim();
        }

        var spoken = new StringBuilder();

        foreach (var line in reply.Split('\n'))
        {
            // Emoji and markdown bullets are for the eye. The Windows fallback voice reads emoji aloud
            // ("smiling face"), and "**Sources:**" or "- milk" should sound like plain words.
            // Strips symbols (emoji, surrogate halves, invisible format characters, asterisks, backticks) and then leading
            // bullet, dash and heading marks. Example: "- **Milk** :)" becomes "Milk".
            var text = Regex.Replace(line, @"[\p{So}\p{Cs}\p{Cf}️*`]", "").Trim().Trim('-', '•', '#').Trim();

            if (text.Equals("Sources:", StringComparison.OrdinalIgnoreCase))
            {
                break; // a citation list is for reading, not listening
            }

            // Splits after sentence-ending punctuation followed by whitespace, so each sentence can be counted against the budget.
            foreach (var sentence in Regex.Split(text, @"(?<=[.!?])\s+"))
            {
                if (sentence.Length == 0)
                {
                    continue;
                }

                // Over budget: stop here, but always say at least one sentence (spoken.Length > 0 is checked first).
                if (spoken.Length > 0 && spoken.Length + sentence.Length > Budget)
                {
                    return spoken.Append(" The rest is on screen.").ToString();
                }

                spoken.Append(spoken.Length > 0 ? " " : "").Append(sentence);

                // A line break is a pause. Without end punctuation the voice runs the lines together.
                if (!".!?:;,".Contains(sentence[^1]))
                {
                    spoken.Append('.');
                }
            }
        }

        return spoken.ToString();
    }

    /// <summary>
    /// Whether what the microphone heard is Jarvis's own voice coming back through the speaker. It can only
    /// be when the reply itself says "Jarvis"; otherwise a heard "Jarvis" came from someone else.
    /// </summary>
    // Guards against the microphone hearing Jarvis's own speech. Compares the words heard with the words just spoken.
    public static bool IsEcho(string heard, string spokenReply)
    {
        // Lower-case words (letters, digits, apostrophes) of the reply that was spoken.
        var spokenWords = Regex.Matches(spokenReply.ToLowerInvariant(), @"[a-z0-9']+").Select(match => match.Value).ToHashSet();

        if (!spokenWords.Contains("jarvis") && !spokenWords.Contains("jarvis's"))
        {
            return false;
        }

        // Same word split for what the microphone heard.
        var heardWords = Regex.Matches(heard.ToLowerInvariant(), @"[a-z0-9']+").Select(match => match.Value).ToList();
        // Needs at least 3 heard words, and 60% or more of them must appear in the spoken reply to count as an echo.
        return heardWords.Count >= 3 && heardWords.Count(spokenWords.Contains) >= heardWords.Count * 0.6;
    }
}

// One finished exchange: what the user said, what Jarvis answered, and the bookkeeping around it
// (intent label, session, tool results, sources, privacy level). SpeakInFull means read the whole reply aloud.
public sealed record AssistantTurn(
    DateTimeOffset TimestampUtc,
    string UserInput,
    string ResponseText,
    bool ShouldExit = false,
    string Intent = "",
    string SessionId = "",
    bool IsFollowUpQuestion = false,
    AssistantToolExecution[]? ToolResults = null,
    AssistantCitation[]? Citations = null,
    string Privacy = "persistent",
    InteractionContextSnapshot? Context = null,
    bool SpeakInFull = false);

// One saved long-term memory item. Beyond the text it records where it came from, how important it is,
// how long to keep it, and optional reminder and "forgotten" state (forgotten notes are kept but hidden).
public sealed record MemoryNote(
    string Id,
    string Content,
    DateTimeOffset CreatedAtUtc,
    string Kind = "note",
    string Category = "",
    string Source = "manual",
    string Privacy = "persistent",
    string[]? Tags = null,
    InteractionContextSnapshot? Context = null,
    int ObservationCount = 1,
    DateTimeOffset? LastObservedAtUtc = null,
    DateTimeOffset? SummaryWindowStartUtc = null,
    DateTimeOffset? SummaryWindowEndUtc = null,
    double ImportanceScore = 0.35d,
    string RetentionPolicy = "",
    DateTimeOffset? ExpiresAtUtc = null,
    bool UserApprovedRetention = false,
    StructuredMemoryReference[]? Entities = null,
    DateTimeOffset? ReminderAtUtc = null,
    string ReminderText = "",
    string ReminderStatus = "",
    bool IsForgotten = false,
    DateTimeOffset? ForgottenAtUtc = null,
    string ForgottenReason = "");
