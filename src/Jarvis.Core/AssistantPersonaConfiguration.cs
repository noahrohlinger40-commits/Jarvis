// AssistantPersonaConfiguration.cs: turns the personality settings in JarvisOptions (persona text, presence preset,
// response style, nickname, pronunciation hints) into the instruction text that is put into the model's prompt, and picks the
// speaking speed of the built-in Windows voice. Everything here only builds strings or picks valid values; nothing is
// sent anywhere.
namespace Jarvis.Core;

// Static helpers; there is no state, each method reads the options it is given.
public static class AssistantPersonaConfiguration
{
    // The allowed values for the AssistantPresencePreset setting. Anything else falls back to "baseline".
    private static readonly string[] SupportedPresencePresets =
    [
        "baseline",
        "operator",
        "warm",
        "cinematic"
    ];

    // The allowed values for the AssistantResponseStyle setting. Anything else falls back to "adaptive".
    private static readonly string[] SupportedResponseStyles =
    [
        "adaptive",
        "concise",
        "normal",
        "detailed",
        "cinematic"
    ];

    // The personality used when the user has not written one (the baseline preset).
    private const string DefaultPersona =
        "Calm, precise, restrained, technically grounded, and proactively helpful. Keep replies concise, natural, and quietly confident.";

    // The final persona text. Baseline preset: the user's own persona replaces the default. Other presets: the
    // preset's persona comes first and the user's text is added as extra guidance.
    public static string Resolve(JarvisOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var configured = options.AssistantPersona?.Trim() ?? string.Empty;
        var presetPersona = BuildPresetPersona(ResolvePresencePreset(options));

        if (string.IsNullOrWhiteSpace(configured))
        {
            return presetPersona;
        }

        return string.Equals(ResolvePresencePreset(options), "baseline", StringComparison.OrdinalIgnoreCase)
            ? configured
            : $"{presetPersona} Additional persona guidance: {configured}";
    }

    // Returns the configured preset name in lower case, or "baseline" when it is missing or not one of the supported names.
    public static string ResolvePresencePreset(JarvisOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var configured = options.AssistantPresencePreset?.Trim().ToLowerInvariant() ?? string.Empty;
        return SupportedPresencePresets.Contains(configured, StringComparer.OrdinalIgnoreCase)
            ? configured
            : "baseline";
    }

    // Cleans the pronunciation hint list: removes blanks and duplicates (ignoring case).
    public static IReadOnlyList<string> ResolvePronunciationHints(JarvisOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return (options.AssistantPronunciationHints ?? Array.Empty<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    // A prompt line telling the model to keep the persona steady and not drift with recent conversation style.
    public static string BuildStabilityInstruction()
    {
        return "Treat the configured persona as the assistant's persistent baseline identity. Keep it stable across turns and sessions. If the user requests a temporary tone or formatting change, apply it for that reply or task and then return to the configured persona. Do not let recent conversation style or learned preferences rewrite the core persona.";
    }

    // A one-line, shortened persona for showing in the UI.
    public static string BuildStatusSummary(JarvisOptions options, int maxLength = 120)
    {
        if (maxLength <= 0)
        {
            return string.Empty;
        }

        var persona = Resolve(options).ReplaceLineEndings(" ").Trim();
        return persona.Length <= maxLength ? persona : persona[..maxLength] + "...";
    }

    // Returns the configured response style, or "adaptive" when it is missing or unsupported.
    public static string ResolveResponseStyle(JarvisOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var configured = options.AssistantResponseStyle?.Trim().ToLowerInvariant() ?? string.Empty;
        return SupportedResponseStyles.Contains(configured, StringComparer.OrdinalIgnoreCase)
            ? configured
            : "adaptive";
    }

    // What to call the user: the nickname from settings if set, otherwise one learned from memory (a saved preference
    // like "call the user Sir"), otherwise nothing.
    public static string ResolvePreferredUserAddress(JarvisOptions options, MemoryProfileSnapshot? memoryProfile = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        var configured = options.AssistantUserNickname?.Trim() ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        if (memoryProfile is null)
        {
            return string.Empty;
        }

        var addressPreference = memoryProfile.Preferences
            .Where(preference =>
                string.Equals(preference.Category, "address", StringComparison.OrdinalIgnoreCase)
                || string.Equals(preference.Category, "phrasing", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(preference => preference.ObservationCount)
            .ThenByDescending(preference => preference.LastObservedAtUtc)
            .Select(preference => TryExtractPreferredAddress(preference.Value))
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

        return addressPreference ?? string.Empty;
    }

    // Builds the block of "how to behave" lines added to the model prompt: preset, style, small talk, nickname,
    // pronunciation hints.
    public static string BuildPresenceGuidance(JarvisOptions options, MemoryProfileSnapshot? memoryProfile = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        var preset = ResolvePresencePreset(options);
        var lines = new List<string>
        {
            $"Presence preset: {DescribePresencePreset(preset)}",
            $"Preferred response style: {DescribeResponseStyle(ResolveResponseStyle(options))}",
            options.AssistantSmallTalkEnabled
                ? "If the user is clearly being social, brief natural small talk is allowed, but keep it disciplined and do not become chatty by default."
                : "Keep small talk minimal unless the user explicitly invites it."
        };

        var preferredAddress = ResolvePreferredUserAddress(options, memoryProfile);

        if (!string.IsNullOrWhiteSpace(preferredAddress))
        {
            lines.Add($"Preferred user address: {preferredAddress}. Use it sparingly and naturally, mainly in greetings, confirmations, or when warmth helps.");
        }

        var pronunciationHints = ResolvePronunciationHints(options);

        if (pronunciationHints.Count > 0)
        {
            lines.Add("Pronunciation hints:");
            lines.AddRange(pronunciationHints.Take(4).Select(value => $"- {value}"));
        }

        lines.Add("Warmth and emotional tone may flex slightly with context, but stay technically grounded and avoid theatrical overreaction.");
        return string.Join(Environment.NewLine, lines);
    }

    // Speaking speed for the built-in Windows voice, from -10 (slow) to 10 (fast): whisper mode is slightly slow,
    // operator and concise styles are slightly fast, warm, cinematic and detailed are slightly slow, everything else normal.
    public static int ResolveWindowsSpeechRate(JarvisOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.WhisperModeEnabled)
        {
            return -1;
        }

        return (ResolvePresencePreset(options), ResolveResponseStyle(options)) switch
        {
            ("operator", _) => 1,
            ("warm", _) => -1,
            ("cinematic", _) => -1,
            (_, "concise") => 1,
            (_, "detailed") => -1,
            _ => 0
        };
    }

    // Plain-English description of a response style, inserted into prompts.
    private static string DescribeResponseStyle(string style)
    {
        return style switch
        {
            "concise" => "keep replies tight, direct, and compact by default.",
            "normal" => "use balanced, natural replies with moderate detail.",
            "detailed" => "give fuller explanations and more step-by-step detail when useful.",
            "cinematic" => "allow a slightly more polished, high-presence voice while staying precise and useful.",
            _ => "adapt to the task, but favor concise high-signal replies unless depth is clearly needed."
        };
    }

    // Plain-English description of a presence preset, inserted into prompts.
    private static string DescribePresencePreset(string preset)
    {
        return preset switch
        {
            "operator" => "operator mode: crisp, mission-oriented, restrained, and highly task-focused.",
            "warm" => "warm mode: calm, personable, and supportive without becoming chatty.",
            "cinematic" => "cinematic mode: polished, high-presence, and slightly dramatic while staying precise.",
            _ => "baseline mode: calm, precise, restrained, technically grounded, and quietly confident."
        };
    }

    // The default persona text for each preset.
    private static string BuildPresetPersona(string preset)
    {
        return preset switch
        {
            "operator" => "Crisp, mission-oriented, restrained, technically grounded, and direct. Favor rapid clarity, firm structure, and disciplined execution language.",
            "warm" => "Calm, warm, technically grounded, and reassuring without losing precision. Be personable and human, but stay concise and useful.",
            "cinematic" => "Polished, high-presence, technically grounded, and composed. Allow a cinematic edge in phrasing while staying restrained, precise, and practical.",
            _ => DefaultPersona
        };
    }

    // Pulls the name out of a saved preference such as "Call the user Boss." (text after "call the user ", without end punctuation).
    private static string? TryExtractPreferredAddress(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        const string marker = "call the user ";
        var index = value.IndexOf(marker, StringComparison.OrdinalIgnoreCase);

        if (index < 0)
        {
            return null;
        }

        var remainder = value[(index + marker.Length)..].Trim().TrimEnd('.', '!', '?');
        return string.IsNullOrWhiteSpace(remainder) ? null : remainder;
    }
}
