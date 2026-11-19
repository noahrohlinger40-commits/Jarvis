// VoiceRecognitionText.cs: text-side helpers for speech. Cleans up what whisper heard, spots the
// wake word ("Jarvis") in a transcript and strips it to leave the command, builds the vocabulary hint sent to whisper, and
// recognises yes/no answers. Used by OpenAiCompatibleVoiceRuntime, WakeWordDetector and the main form.
// Whisper often mis-spells the name ("Jervis", "Garbus"), so matching is deliberately forgiving here.
using System.Text.RegularExpressions;
using Jarvis.Core;

namespace Jarvis.App;

// Static helper class (no state). "partial" only because the whitespace regex is generated at compile time (see the end of the file).
internal static partial class VoiceRecognitionText
{
    // Greeting words that may come right before the wake word: "hey Jarvis", "okay Jarvis".
    private static readonly string[] WakeLeadIns =
    [
        "hey",
        "okay",
        "ok",
        "hello",
        "hi",
        "please",
        "yo"
    ];

    // Filler words that may appear before the wake word without stopping it counting as the start of the sentence ("um, hey Jarvis ...").
    private static readonly HashSet<string> WakeSkippableTokens = new(StringComparer.Ordinal)
    {
        "uh",
        "um",
        "erm",
        "ah",
        "well",
        "please",
        "hey",
        "okay",
        "ok",
        "hello",
        "hi",
        "yo"
    };

    // Misspellings of "Jarvis" that are treated as the real name.
    private static readonly HashSet<string> CanonicalWakeVariants = new(StringComparer.Ordinal)
    {
        "jarvis",
        "jervis",
        "jarviss",
        "jarvice",
        "jarvus",
        "jarvess",
        // Spellings Whisper produced for a real "Jarvis" heard through a laptop microphone.
        "garbus",
        "gargus"
    };

    // Short spoken answers that mean "yes" when Jarvis asks for confirmation.
    private static readonly HashSet<string> ConfirmationApprovals = new(StringComparer.Ordinal)
    {
        "yes",
        "yeah",
        "yep",
        "correct",
        "thats right",
        "that is right",
        "right",
        "do that",
        "continue",
        "go ahead"
    };

    // Short spoken answers that mean "no" or "say it again".
    private static readonly HashSet<string> ConfirmationDenials = new(StringComparer.Ordinal)
    {
        "no",
        "nope",
        "negative",
        "wrong",
        "cancel",
        "stop",
        "say that again",
        "repeat that",
        "try again"
    };

    // Tidies a transcript: fixes a wrong wake-word spelling in the first three words, collapses spaces, fixes spacing around
    // punctuation, optionally applies the user's saved corrections, and capitalises the first letter.
    public static string NormalizeTranscript(string text, JarvisOptions options, bool applyPersonalCorrections = true)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var tokens = Tokenize(text);

        // Only the first three words are checked, because the wake word is said at the start.
        for (var index = 0; index < Math.Min(tokens.Count, 3); index++)
        {
            var corrected = CorrectWakeToken(tokens[index].Match, options);

            if (!string.Equals(corrected, tokens[index].Match, StringComparison.Ordinal))
            {
                tokens[index] = tokens[index] with { Original = corrected, Match = corrected };
            }
        }

        var normalized = string.Join(' ', tokens.Select(token => token.Original));
        normalized = WhitespaceRegex().Replace(normalized.Trim(), " ");
        // Regex "\s+([.,!?;:])" removes spaces before punctuation: "open notepad ." becomes "open notepad.".
        normalized = Regex.Replace(normalized, "\\s+([.,!?;:])", "$1");
        // Regex "([.,!?;:])([^\s])" adds a space after punctuation stuck to the next word: "hi,there" becomes "hi, there".
        normalized = Regex.Replace(normalized, "([.,!?;:])([^\\s])", "$1 $2");

        if (applyPersonalCorrections)
        {
            normalized = ApplyPersonalCorrections(normalized, options);
        }

        if (options.SpeechRecognitionPunctuationRestorationEnabled && normalized.Length > 1)
        {
            normalized = char.ToUpperInvariant(normalized[0]) + normalized[1..];
        }

        return normalized;
    }

    // Replaces words the recogniser keeps getting wrong with what the user said they meant (saved in settings; see VoiceCorrectionLearning).
    // Longest phrases are applied first so a short correction cannot break a longer one.
    public static string ApplyPersonalCorrections(string text, JarvisOptions options)
    {
        if (string.IsNullOrWhiteSpace(text) || options.SpeechRecognitionCorrections.Length == 0)
        {
            return text.Trim();
        }

        var corrected = text;
        var entries = options.SpeechRecognitionCorrections
            .Where(entry => !string.IsNullOrWhiteSpace(entry.HeardText) && !string.IsNullOrWhiteSpace(entry.CorrectedText))
            .OrderByDescending(entry => entry.HeardText.Length)
            .ThenByDescending(entry => entry.CorrectionCount);

        foreach (var entry in entries)
        {
            var heard = WhitespaceRegex().Replace(entry.HeardText.Trim(), " ");
            var replacement = entry.CorrectedText.Trim();

            if (string.IsNullOrWhiteSpace(heard) || string.IsNullOrWhiteSpace(replacement))
            {
                continue;
            }

            // Regex built from the heard phrase: whole-word match only. (?<![\p{L}\p{N}]) means "not preceded by a letter or digit" and
            // (?![\p{L}\p{N}]) "not followed by one", so "art" does not match inside "party".
            var pattern = $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(heard)}(?![\p{{L}}\p{{N}}])";
            corrected = Regex.Replace(
                corrected,
                pattern,
                replacement,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        corrected = WhitespaceRegex().Replace(corrected.Trim(), " ");
        corrected = Regex.Replace(corrected, "\\s+([.,!?;:])", "$1");
        corrected = Regex.Replace(corrected, "([.,!?;:])([^\\s])", "$1 $2");
        return corrected;
    }

    // Looks for the wake word at the start of the transcript. Returns true with the command after it ("Jarvis open notepad" gives
    // "open notepad"; "Jarvis" alone gives an empty command, meaning "I'm listening"). Falls back to a mid-sentence search.
    public static bool TryExtractWakeCommand(string recognizedText, JarvisOptions options, out string commandText)
    {
        var tokens = Tokenize(recognizedText);

        // First pass: the name (or an alias with a greeting) within the first three words, allowing a little fuzziness.
        foreach (var prefix in BuildWakePrefixTokens(options))
        {
            for (var startIndex = 0; startIndex <= Math.Min(2, tokens.Count - prefix.Length); startIndex++)
            {
                // When it does not start at word 0, everything before it must be filler like "um" or "hey".
                if (startIndex > 0
                    && tokens.Take(startIndex).Any(token => !WakeSkippableTokens.Contains(token.Match)))
                {
                    continue;
                }

                var matches = true;

                for (var index = 0; index < prefix.Length; index++)
                {
                    if (!IsWakeTokenMatch(tokens[startIndex + index].Match, prefix[index]))
                    {
                        matches = false;
                        break;
                    }
                }

                if (!matches)
                {
                    continue;
                }

                commandText = NormalizeCommandText(
                    string.Join(' ', tokens.Skip(startIndex + prefix.Length).Select(token => token.Original)));
                return true;
            }
        }

        // Second pass: exact name anywhere in the sentence.
        // Pauses get merged into one utterance, so "so if I say Jarvis open Spotify" is common.
        // Mid-sentence only an exact wake word counts; fuzzy matching there would catch "Travis" or "Marvin".
        for (var startIndex = 0; startIndex < tokens.Count; startIndex++)
        {
            // Tokens are already normalised, so compare with the name after the same normalisation.
            if (tokens[startIndex].Match != CorrectWakeToken(CanonicalizeForMatching(options.WakePhrase), null))
            {
                continue;
            }

            commandText = NormalizeCommandText(
                string.Join(' ', tokens.Skip(startIndex + 1).Select(token => token.Original)));

            // "...my project Jarvis." only mentions the name; it is not a call.
            if (commandText.Length == 0)
            {
                return false;
            }

            return true;
        }

        commandText = string.Empty;
        return false;
    }

    // All spoken forms of the wake word (and with greetings) for the wake-word detector.
    public static IReadOnlyList<string> BuildWakeGrammarChoices(JarvisOptions options)
    {
        var phrases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var alias in BuildWakeAliases(options))
        {
            phrases.Add(alias);

            foreach (var leadIn in WakeLeadIns)
            {
                phrases.Add($"{leadIn} {alias}");
            }
        }

        return phrases.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    // Builds the "prompt" sent with every whisper request: a short list of context, the wake word spellings, common commands
    // and the user's own corrections, which nudges whisper to spell app names and commands correctly.
    public static string BuildTranscriptionPrompt(JarvisOptions options)
    {
        var wakeAsset = WakeWordAssetCatalog.ResolveConfiguredAsset(options);
        var wakeAliases = BuildWakeAliases(options).Take(8).ToArray();
        var appAliasHints = options.ApplicationAliases
            .Where(alias => !string.IsNullOrWhiteSpace(alias.Key) && !string.IsNullOrWhiteSpace(alias.Value))
            .Take(6)
            .Select(alias => $"{alias.Key.Trim()} means {DescribeAppAliasTarget(alias.Value)}")
            .ToArray();
        var instructions = new List<string>
        {
            "This is live speech for a Windows desktop assistant.",
            $"Assistant name: {options.AssistantName}.",
            $"Primary wake phrase: {options.WakePhrase}.",
            $"Treat these as equivalent wake words: {string.Join(", ", wakeAliases)}.",
            "Preserve command phrases and app names accurately.",
            "Common commands include: help, status, time, notes, weather, system, computer, apps, open app, focus app, close app, window state, send hotkey, open path, list files, read file, find files, browse, search web, screen, analyze document, analyze image, click element, type into, get element text, scroll, wait, run.",
            "If you hear Jervis, Jarviss, or Jarvice, transcribe it as Jarvis."
        };

        if (wakeAsset is not null && !wakeAsset.IsRecognizerDefault)
        {
            instructions.Add($"Loaded wake asset: {wakeAsset.BuildShortLabel()}.");
        }

        if (appAliasHints.Length > 0)
        {
            instructions.Add($"Known spoken app aliases: {string.Join(", ", appAliasHints)}.");
        }

        var correctionHints = options.SpeechRecognitionCorrections
            .Where(entry => !string.IsNullOrWhiteSpace(entry.HeardText) && !string.IsNullOrWhiteSpace(entry.CorrectedText))
            .OrderByDescending(entry => entry.CorrectionCount)
            .ThenByDescending(entry => entry.LastUpdatedUtc)
            .Take(6)
            .Select(entry => $"\"{entry.HeardText.Trim()}\" should be \"{entry.CorrectedText.Trim()}\"")
            .ToArray();

        if (correctionHints.Length > 0)
        {
            instructions.Add($"Personal correction hints: {string.Join("; ", correctionHints)}.");
        }

        if (!string.IsNullOrWhiteSpace(options.SpeechRecognitionPrompt))
        {
            instructions.Add(options.SpeechRecognitionPrompt.Trim());
        }

        return string.Join(' ', instructions);
    }

    // True when the answer is a yes ("yes", "go ahead", ...).
    public static bool LooksLikeConfirmationApproval(string input) =>
        ConfirmationApprovals.Contains(CanonicalizeForMatching(input));

    // True when the answer is a no or a request to repeat ("cancel", "try again", ...).
    public static bool LooksLikeConfirmationDenial(string input) =>
        ConfirmationDenials.Contains(CanonicalizeForMatching(input));

    // Every wake phrase as a list of words: each alias alone and with each greeting in front.
    private static IReadOnlyList<string[]> BuildWakePrefixTokens(JarvisOptions options)
    {
        var prefixes = new List<string[]>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var alias in BuildWakeAliases(options))
        {
            AddPrefix(alias);

            foreach (var leadIn in WakeLeadIns)
            {
                AddPrefix($"{leadIn} {alias}");
            }
        }

        return prefixes;

        void AddPrefix(string phrase)
        {
            var tokens = Tokenize(phrase).Select(token => token.Match).ToArray();

            if (tokens.Length == 0)
            {
                return;
            }

            var key = string.Join('|', tokens);

            if (!seen.Add(key))
            {
                return;
            }

            prefixes.Add(tokens);
        }
    }

    // Every spelling that should count as the wake word: wake phrase, assistant name, configured aliases, the known
    // whisper misspellings (only when the phrase is "jarvis"), and aliases of the loaded wake-word asset.
    public static IReadOnlyList<string> BuildWakeAliases(JarvisOptions options)
    {
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var wakeAsset = WakeWordAssetCatalog.ResolveConfiguredAsset(options);

        AddAlias(options.WakePhrase);
        AddAlias(options.AssistantName);

        foreach (var alias in options.SpeechRecognitionWakeAliases)
        {
            AddAlias(alias);
        }

        if (CanonicalizeForMatching(options.WakePhrase) == "jarvis")
        {
            AddAlias("jervis");
            AddAlias("jarviss");
            AddAlias("jarvice");
            AddAlias("jarvus");
            AddAlias("jarvess");
            AddAlias("garbus");
            AddAlias("gargus");
        }

        if (wakeAsset is not null)
        {
            foreach (var alias in wakeAsset.BuildWakeAliases())
            {
                AddAlias(alias);
            }
        }

        return aliases.ToArray();

        void AddAlias(string? alias)
        {
            if (string.IsNullOrWhiteSpace(alias))
            {
                return;
            }

            var cleaned = WhitespaceRegex().Replace(alias.Trim(), " ");

            if (string.IsNullOrWhiteSpace(cleaned))
            {
                return;
            }

            aliases.Add(cleaned);
        }
    }

    // Turns an app alias target into something readable for the whisper prompt: "spotify:" -> "spotify", "C:\...\Code.exe" -> "Code".
    private static string DescribeAppAliasTarget(string target)
    {
        var trimmed = target.Trim();

        if (trimmed.EndsWith(':'))
        {
            return trimmed[..^1];
        }

        var leaf = Path.GetFileNameWithoutExtension(trimmed);
        return string.IsNullOrWhiteSpace(leaf) ? trimmed : leaf;
    }

    // Splits text into words, keeping each word as spoken (Original) and a cleaned lower-case form used for comparison (Match).
    private static List<SpokenToken> Tokenize(string text)
    {
        return text
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token =>
            {
                var original = token.Trim();
                var match = NormalizeTokenForMatching(original);
                return string.IsNullOrWhiteSpace(match)
                    ? null
                    : new SpokenToken(original, match);
            })
            .Where(token => token is not null)
            .Cast<SpokenToken>()
            .ToList();
    }

    // Lower-cases a word, trims punctuation and curly quotes, and swaps known wake-word misspellings for "jarvis".
    private static string NormalizeTokenForMatching(string token)
    {
        var trimmed = token
            .Trim()
            .Trim('"', '\'', ',', '.', '!', '?', ';', ':', '(', ')', '[', ']', '{', '}')
            .Replace("’", "'", StringComparison.Ordinal)
            .Replace("“", "\"", StringComparison.Ordinal)
            .Replace("”", "\"", StringComparison.Ordinal);

        return CorrectWakeToken(trimmed.ToLowerInvariant(), null);
    }

    // If the word is a known misspelling of Jarvis, return the proper wake word; otherwise unchanged.
    private static string CorrectWakeToken(string token, JarvisOptions? options)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return string.Empty;
        }

        var canonical = CanonicalizeForMatching(token);

        if (CanonicalWakeVariants.Contains(canonical))
        {
            return options is null
                ? "jarvis"
                : options.WakePhrase.Trim().ToLowerInvariant();
        }

        return token;
    }

    // Removes stray punctuation around the command text.
    private static string NormalizeCommandText(string text)
    {
        return text
            .Trim()
            .Trim(',', '.', '!', '?', ':', ';', '-', '"', '\'')
            .Trim();
    }

    // Does a heard word match the expected wake word? Exact, or after fixing misspellings, or close enough by edit distance:
    // one letter off for short words, two for words of six or more letters.
    private static bool IsWakeTokenMatch(string actual, string expected)
    {
        if (string.Equals(actual, expected, StringComparison.Ordinal))
        {
            return true;
        }

        var normalizedActual = CorrectWakeToken(actual, null);
        var normalizedExpected = CorrectWakeToken(expected, null);

        if (string.Equals(normalizedActual, normalizedExpected, StringComparison.Ordinal))
        {
            return true;
        }

        var actualCanonical = CanonicalizeForMatching(actual);
        var expectedCanonical = CanonicalizeForMatching(expected);

        if (string.IsNullOrWhiteSpace(actualCanonical) || string.IsNullOrWhiteSpace(expectedCanonical))
        {
            return false;
        }

        var allowedDistance = expectedCanonical.Length >= 6 ? 2 : 1;
        return ComputeLevenshteinDistance(actualCanonical, expectedCanonical) <= allowedDistance;
    }

    // Levenshtein distance: the smallest number of single-letter insertions, deletions or changes that turn one word into the other
    // ("jervis" to "jarvis" is 1). Uses one row of a table that is updated in place.
    private static int ComputeLevenshteinDistance(string left, string right)
    {
        var costs = new int[right.Length + 1];

        for (var index = 0; index <= right.Length; index++)
        {
            costs[index] = index;
        }

        for (var leftIndex = 1; leftIndex <= left.Length; leftIndex++)
        {
            var previousDiagonal = costs[0];
            costs[0] = leftIndex;

            for (var rightIndex = 1; rightIndex <= right.Length; rightIndex++)
            {
                var previous = costs[rightIndex];
                var substitutionCost = left[leftIndex - 1] == right[rightIndex - 1] ? 0 : 1;
                costs[rightIndex] = Math.Min(
                    Math.Min(costs[rightIndex] + 1, costs[rightIndex - 1] + 1),
                    previousDiagonal + substitutionCost);
                previousDiagonal = previous;
            }
        }

        return costs[right.Length];
    }

    // Lower-cases and replaces everything except letters, digits, '.' and spaces with spaces, then collapses repeated spaces.
    private static string CanonicalizeForMatching(string value)
    {
        var chars = value
            .ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) || character == '.' || char.IsWhiteSpace(character) ? character : ' ')
            .ToArray();

        return string.Join(' ', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    // Matches one or more whitespace characters.
    [GeneratedRegex("\\s+")]
    private static partial Regex WhitespaceRegex();

    // One word of a transcript: as heard, and in cleaned form for matching.
    private sealed record SpokenToken(string Original, string Match);
}
