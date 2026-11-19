// VoiceCorrectionLearning.cs: lets Noah teach Jarvis its speech mistakes by voice. Saying "no, I said open Spotify, not open
// spot of eye" is parsed here, and the pair is stored in the settings (SpeechRecognitionCorrections). VoiceRecognitionText
// then applies these replacements to every later transcript. Options are immutable records, so each method returns a new copy.
using System.Text.RegularExpressions;
using Jarvis.Core;

namespace Jarvis.App;

// A parsed correction: what the user meant (CorrectedText) and what the recogniser wrongly wrote (HeardText).
internal sealed record VoiceCorrectionDirective(string CorrectedText, string HeardText);

// Static helpers; "partial" because the regexes below are generated at compile time.
internal static partial class VoiceCorrectionLearning
{
    // At most 48 learned corrections and 48 remembered rejected phrases are kept (the most used / most recent win).
    private const int MaxCorrectionEntries = 48;
    private const int MaxRejectedPhraseEntries = 48;

    // Recognises a spoken correction sentence in either order and extracts the two phrases. Returns false if it is not a correction.
    public static bool TryParseCorrectionDirective(string input, out VoiceCorrectionDirective directive)
    {
        directive = default!;

        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var trimmed = CollapseWhitespace(input);
        // Tries "I said X, not Y" first, then "you heard Y, but I said X".
        var match = HeardSecondCorrectionRegex().Match(trimmed);

        if (!match.Success)
        {
            match = HeardFirstCorrectionRegex().Match(trimmed);
        }

        if (!match.Success)
        {
            return false;
        }

        var correctedText = NormalizePhrase(match.Groups["corrected"].Value);
        var heardText = NormalizePhrase(match.Groups["heard"].Value);

        if (!CanLearnCorrection(heardText, correctedText))
        {
            return false;
        }

        directive = new VoiceCorrectionDirective(correctedText, heardText);
        return true;
    }

    // True when the user asks to see what has been learned ("voice corrections", "speech correction log", ...).
    public static bool IsReviewCommand(string input)
    {
        var normalized = NormalizeKey(input);
        return normalized is "voice corrections"
            or "show voice corrections"
            or "speech corrections"
            or "voice correction log"
            or "speech correction log";
    }

    // A correction is worth saving only if both sides are non-empty, under 160 characters and actually different (ignoring case and punctuation).
    public static bool CanLearnCorrection(string heardText, string correctedText)
    {
        var heard = NormalizePhrase(heardText);
        var corrected = NormalizePhrase(correctedText);

        if (string.IsNullOrWhiteSpace(heard)
            || string.IsNullOrWhiteSpace(corrected)
            || heard.Length > 160
            || corrected.Length > 160)
        {
            return false;
        }

        return !string.Equals(NormalizeKey(heard), NormalizeKey(corrected), StringComparison.Ordinal);
    }

    // Adds or updates a correction in the settings. A repeat of the same heard text bumps its count and replaces the target.
    // Also logs the heard phrase as rejected.
    public static JarvisOptions RecordCorrection(
        JarvisOptions options,
        string heardText,
        string correctedText,
        double? confidence)
    {
        var heard = NormalizePhrase(heardText);
        var corrected = NormalizePhrase(correctedText);

        if (!CanLearnCorrection(heard, corrected))
        {
            return options;
        }

        var now = DateTimeOffset.UtcNow;
        var corrections = options.SpeechRecognitionCorrections
            .Where(entry => !string.IsNullOrWhiteSpace(entry.HeardText) && !string.IsNullOrWhiteSpace(entry.CorrectedText))
            .ToList();
        var existingIndex = corrections.FindIndex(entry =>
            string.Equals(NormalizeKey(entry.HeardText), NormalizeKey(heard), StringComparison.Ordinal));

        if (existingIndex >= 0)
        {
            var existing = corrections[existingIndex];
            corrections[existingIndex] = existing with
            {
                HeardText = heard,
                CorrectedText = corrected,
                CorrectionCount = Math.Max(1, existing.CorrectionCount) + 1,
                LastUpdatedUtc = now,
                LastConfidence = confidence
            };
        }
        else
        {
            corrections.Add(new SpeechRecognitionCorrectionEntry
            {
                HeardText = heard,
                CorrectedText = corrected,
                CorrectionCount = 1,
                LastUpdatedUtc = now,
                LastConfidence = confidence
            });
        }

        // Most-used corrections first, then newest; keep only the first 48.
        var updated = options with
        {
            SpeechRecognitionCorrections = corrections
                .OrderByDescending(entry => entry.CorrectionCount)
                .ThenByDescending(entry => entry.LastUpdatedUtc)
                .Take(MaxCorrectionEntries)
                .ToArray()
        };

        return RecordRejectedPhrase(updated, heard, confidence);
    }

    // Remembers a phrase the user said was wrong, with a count and time, so the review list can show it. Newest first, capped at 48.
    public static JarvisOptions RecordRejectedPhrase(
        JarvisOptions options,
        string heardText,
        double? confidence)
    {
        var heard = NormalizePhrase(heardText);

        if (string.IsNullOrWhiteSpace(heard) || heard.Length > 160)
        {
            return options;
        }

        var now = DateTimeOffset.UtcNow;
        var rejected = options.SpeechRecognitionRejectedPhrases
            .Where(entry => !string.IsNullOrWhiteSpace(entry.HeardText))
            .ToList();
        var existingIndex = rejected.FindIndex(entry =>
            string.Equals(NormalizeKey(entry.HeardText), NormalizeKey(heard), StringComparison.Ordinal));

        if (existingIndex >= 0)
        {
            var existing = rejected[existingIndex];
            rejected[existingIndex] = existing with
            {
                HeardText = heard,
                RejectionCount = Math.Max(1, existing.RejectionCount) + 1,
                LastRejectedUtc = now,
                LastConfidence = confidence
            };
        }
        else
        {
            rejected.Add(new SpeechRecognitionRejectedPhrase
            {
                HeardText = heard,
                RejectionCount = 1,
                LastRejectedUtc = now,
                LastConfidence = confidence
            });
        }

        return options with
        {
            SpeechRecognitionRejectedPhrases = rejected
                .OrderByDescending(entry => entry.LastRejectedUtc)
                .ThenByDescending(entry => entry.RejectionCount)
                .Take(MaxRejectedPhraseEntries)
                .ToArray()
        };
    }

    // Builds the plain-text list shown for "voice corrections": the top 8 learned corrections and the 8 most recent rejected phrases.
    public static string BuildReview(JarvisOptions options)
    {
        var corrections = options.SpeechRecognitionCorrections
            .Where(entry => !string.IsNullOrWhiteSpace(entry.HeardText) && !string.IsNullOrWhiteSpace(entry.CorrectedText))
            .OrderByDescending(entry => entry.CorrectionCount)
            .ThenByDescending(entry => entry.LastUpdatedUtc)
            .Take(8)
            .ToArray();
        var rejected = options.SpeechRecognitionRejectedPhrases
            .Where(entry => !string.IsNullOrWhiteSpace(entry.HeardText))
            .OrderByDescending(entry => entry.LastRejectedUtc)
            .ThenByDescending(entry => entry.RejectionCount)
            .Take(8)
            .ToArray();
        var lines = new List<string>();

        lines.Add("Voice correction learning:");

        if (corrections.Length == 0)
        {
            lines.Add("- No learned corrections yet.");
        }
        else
        {
            lines.Add("- Learned corrections:");
            lines.AddRange(corrections.Select(entry =>
                $"  \"{entry.HeardText}\" -> \"{entry.CorrectedText}\" | count {entry.CorrectionCount} | updated {entry.LastUpdatedUtc.LocalDateTime:g}"));
        }

        if (rejected.Length == 0)
        {
            lines.Add("- No rejected transcripts logged yet.");
        }
        else
        {
            lines.Add("- Recent rejected transcripts:");
            lines.AddRange(rejected.Select(entry =>
                $"  \"{entry.HeardText}\" | rejections {entry.RejectionCount} | last {entry.LastRejectedUtc.LocalDateTime:g}"));
        }

        return string.Join(Environment.NewLine, lines);
    }

    // Trims spaces, quotes and end punctuation around a phrase.
    private static string NormalizePhrase(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = CollapseWhitespace(value)
            .Trim()
            .Trim('"', '\'', '`', '“', '”', '‘', '’')
            .Trim();

        return trimmed.Trim(',', '.', '!', '?', ':', ';');
    }

    // Comparison form of a phrase: lower-case, punctuation turned into spaces, spaces collapsed, so "Open Spotify!" equals "open spotify".
    private static string NormalizeKey(string value)
    {
        var normalized = NormalizePhrase(value);

        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        var chars = normalized
            .ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) || char.IsWhiteSpace(character) ? character : ' ')
            .ToArray();

        return CollapseWhitespace(new string(chars));
    }

    // Trims text and collapses runs of whitespace to a single space.
    private static string CollapseWhitespace(string value) =>
        WhitespaceRegex().Replace(value.Trim(), " ");

    // Matches "I said <corrected>, not <heard>" or "... instead of <heard>", with an optional leading "no".
    // Example: "No, I said open Spotify not open spot a fly" gives corrected = "open Spotify", heard = "open spot a fly".
    [GeneratedRegex(@"^(?:no[, ]+)?i said (?<corrected>.+?)(?:,?\s+not|,?\s+instead of) (?<heard>.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HeardSecondCorrectionRegex();

    // Matches "you heard <heard>, but I said <corrected>" (also "Jarvis heard", "you wrote", "Jarvis wrote").
    // Example: "you heard open notebad but I said open notepad".
    [GeneratedRegex(@"^(?:you heard|jarvis heard|you wrote|jarvis wrote) (?<heard>.+?)(?:,?\s+(?:but\s+)?i said) (?<corrected>.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HeardFirstCorrectionRegex();

    // Matches one or more whitespace characters.
    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
