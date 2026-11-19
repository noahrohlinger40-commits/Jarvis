// WakeWordDetector.cs: decides whether a piece of recorded audio contains the wake word ("Jarvis" / "hey Jarvis").
// OpenAiCompatibleVoiceRuntime calls Detect(audio) on each utterance when whisper's transcript had no name in it.
// Two interchangeable back ends sit behind one wrapper: an external program such as openWakeWord (run as a child process on a
// temporary WAV file, see WakeWordEngines.cs) or the Windows speech recogniser with a tiny grammar of just the wake phrases.
using System.Speech.Recognition;
using System.Text.Json;
using Jarvis.Core;

namespace Jarvis.App;

// Outcome of one check: whether the wake word was found, which spelling matched, confidence (0..1), whether it was the
// name alone (ShouldArmOnly = just open the listening window), and any command spoken right after it.
internal sealed record WakeWordDetectionResult(
    bool IsDetected,
    string Alias,
    double Confidence,
    bool ShouldArmOnly,
    TimeSpan MatchOffset,
    TimeSpan MatchDuration,
    string RecognizedText = "",
    string InlineCommand = "")
{
    // Shared "nothing found" result.
    public static WakeWordDetectionResult None { get; } =
        new(false, string.Empty, 0, false, TimeSpan.Zero, TimeSpan.Zero);
}

// Public face of the detector; hides which back end is in use.
internal sealed class WakeWordDetector : IDisposable
{
    // The actual detection method (recogniser or external program).
    private readonly IWakeWordDetectionBackend _backend;

    private WakeWordDetector(IWakeWordDetectionBackend backend)
    {
        _backend = backend;
    }

    public string DescriptionLabel => _backend.DescriptionLabel;

    // True for a manifest-backed engine such as openWakeWord; the recognizer-grammar default is status-only.
    /// <summary>True for a manifest-backed engine such as openWakeWord; the recognizer-grammar default is status-only.</summary>
    public bool UsesExternalEngine => _backend is ExternalProcessWakeWordDetectionBackend;

    // Builds a detector using the default microphone processing profile.
    public static WakeWordDetector? Create(JarvisOptions options)
    {
        return Create(options, MicrophoneProcessingProfileResolver.Resolve(options));
    }

    // Builds the detector from settings, or returns null when wake detection is off or impossible (no phrases, no recogniser).
    // Picks the external engine if its executable is configured, otherwise the Windows recogniser. Any failure means "no detector".
    public static WakeWordDetector? Create(JarvisOptions options, ResolvedMicrophoneProcessingProfile processingProfile)
    {
        if (!options.WakeWordEnabled || !options.SpeechRecognitionWakeDetectionEnabled)
        {
            return null;
        }

        try
        {
            var asset = WakeWordAssetCatalog.ResolveConfiguredAsset(options);
            var wakeChoices = BuildWakeChoices(options, asset)
                .Where(choice => !string.IsNullOrWhiteSpace(choice))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (wakeChoices.Length == 0)
            {
                return null;
            }

            var wakeEngine = WakeWordEngineCatalog.ResolveDescriptorByPath(
                Directory.GetCurrentDirectory(),
                options,
                processingProfile.EffectiveWakeEnginePath);
            var minimumConfidence = ResolveMinimumConfidence(options, asset, wakeEngine);

            if (wakeEngine.UsesExternalProcess
                && !string.IsNullOrWhiteSpace(wakeEngine.ExecutablePath))
            {
                return new WakeWordDetector(
                    new ExternalProcessWakeWordDetectionBackend(
                        options,
                        processingProfile,
                        wakeChoices,
                        asset,
                        wakeEngine,
                        minimumConfidence));
            }

            var recognizer = ResolveRecognizer(options);

            if (recognizer is null)
            {
                return null;
            }

            return new WakeWordDetector(
                new RecognizerWakeWordDetectionBackend(
                    options,
                    recognizer,
                    wakeChoices,
                    asset,
                    minimumConfidence));
        }
        catch
        {
            return null;
        }
    }

    // Checks one utterance of 16-bit mono audio for the wake word.
    public WakeWordDetectionResult Detect(byte[] pcm16MonoAudio, int sampleRateHz)
    {
        return _backend.Detect(pcm16MonoAudio, sampleRateHz);
    }

    public void Dispose()
    {
        _backend.Dispose();
    }

    // Collects every phrase that should count as the wake word: the normal aliases with greetings, plus those from the wake asset.
    internal static IReadOnlyList<string> BuildWakeChoices(JarvisOptions options, WakeWordAssetDescriptor? asset)
    {
        var choices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var choice in VoiceRecognitionText.BuildWakeGrammarChoices(options))
        {
            AddChoice(choice);
        }

        if (asset is not null)
        {
            foreach (var alias in asset.BuildWakeAliases())
            {
                AddChoice(alias);
                AddChoice($"hey {alias}");
                AddChoice($"okay {alias}");
                AddChoice($"please {alias}");
            }
        }

        return choices.ToArray();

        void AddChoice(string? choice)
        {
            if (string.IsNullOrWhiteSpace(choice))
            {
                return;
            }

            choices.Add(choice.Trim());
        }
    }

    // Checks whether a recognised text starts with one of the wake phrases and, if so, returns the matched phrase and the rest as the inline command.
    // A phrase only matches as a whole word: "jarvisford" does not count, but "jarvis, open notepad" does (a space or , . ! ? : ; - may follow).
    internal static bool TryExtractDetectedWakeCommand(
        string recognizedText,
        IReadOnlyList<NormalizedWakeChoice> normalizedWakeChoices,
        out string matchedAlias,
        out string inlineCommand)
    {
        foreach (var wakeChoice in normalizedWakeChoices)
        {
            if (!recognizedText.StartsWith(wakeChoice.Normalized, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var remainder = recognizedText[wakeChoice.Normalized.Length..];

            if (!string.IsNullOrWhiteSpace(remainder))
            {
                var nextCharacter = remainder[0];

                if (!char.IsWhiteSpace(nextCharacter)
                    && nextCharacter is not ',' and not '.' and not '!' and not '?' and not ':' and not ';' and not '-')
                {
                    continue;
                }
            }

            matchedAlias = wakeChoice.Original;
            inlineCommand = NormalizeDetectedCommand(remainder);
            return true;
        }

        matchedAlias = string.Empty;
        inlineCommand = string.Empty;
        return false;
    }

    // Removes stray punctuation around the command.
    internal static string NormalizeDetectedCommand(string text)
    {
        return text
            .Trim()
            .Trim(',', '.', '!', '?', ':', ';', '-', '"', '\'')
            .Trim();
    }

    // Picks the installed Windows speech recogniser for the configured language, else the first one.
    private static RecognizerInfo? ResolveRecognizer(JarvisOptions options)
    {
        var installedRecognizers = SpeechRecognitionEngine.InstalledRecognizers().ToArray();

        if (installedRecognizers.Length == 0)
        {
            return null;
        }

        return installedRecognizers.FirstOrDefault(
                candidate => string.Equals(
                    candidate.Culture.Name,
                    options.RecognizerCulture,
                    StringComparison.OrdinalIgnoreCase))
            ?? installedRecognizers[0];
    }

    // Works out how sure a detection must be before it counts (0..1, lower = more sensitive). Starts from the lower (more lenient) of the
    // recogniser threshold (minus 0.10) and the sensitivity slider (0.70 down to 0.40), then an asset or engine can override it
    // and each subtracts its own sensitivity bias. Final range is 0.22 to 0.82.
    private static double ResolveMinimumConfidence(
        JarvisOptions options,
        WakeWordAssetDescriptor? asset,
        WakeWordEngineDescriptor wakeEngine)
    {
        var sensitivity = Math.Clamp(options.SpeechRecognitionWakeDetectionSensitivity, 0.0, 1.0);
        var recognitionThreshold = Math.Clamp(options.SpeechRecognitionConfidenceThreshold - 0.10, 0.30, 0.70);
        var wakeThreshold = Math.Clamp(0.70 - (sensitivity * 0.30), 0.34, 0.70);
        var threshold = Math.Min(recognitionThreshold, wakeThreshold);

        if (asset?.MinimumConfidence is double assetThreshold)
        {
            threshold = Math.Clamp(assetThreshold, 0.28, 0.82);
        }

        if (wakeEngine.MinimumConfidence is double engineThreshold)
        {
            threshold = Math.Clamp(engineThreshold, 0.24, 0.82);
        }

        if (asset is not null)
        {
            threshold = Math.Clamp(threshold - asset.SensitivityBias, 0.24, 0.82);
        }

        threshold = Math.Clamp(threshold - wakeEngine.SensitivityBias, 0.22, 0.82);
        return threshold;
    }

    // What every back end must offer: a label and a Detect method.
    private interface IWakeWordDetectionBackend : IDisposable
    {
        string DescriptionLabel { get; }

        WakeWordDetectionResult Detect(byte[] pcm16MonoAudio, int sampleRateHz);
    }

    // Uses the Windows recogniser with a small grammar (only wake phrases, or wake phrase + free speech) and feeds it the audio from memory.
    private sealed class RecognizerWakeWordDetectionBackend : IWakeWordDetectionBackend
    {
        // Names of the two grammars loaded into the recogniser.
        private const string WakeGrammarName = "trained-wake-word";
        private const string WakeCommandGrammarName = "trained-wake-command";

        // Only one recognition at a time; the engine is not thread safe.
        private readonly object _sync = new();
        private readonly JarvisOptions _options;
        private readonly SpeechRecognitionEngine _engine;
        // Wake phrases pre-cleaned and sorted longest first, so "hey jarvis" is tried before "jarvis".
        private readonly IReadOnlyList<NormalizedWakeChoice> _normalizedWakeChoices;
        private readonly double _minimumConfidence;
        private bool _isDisposed;

        public RecognizerWakeWordDetectionBackend(
            JarvisOptions options,
            RecognizerInfo recognizer,
            IReadOnlyList<string> wakeChoices,
            WakeWordAssetDescriptor? asset,
            double minimumConfidence)
        {
            _options = options;
            _normalizedWakeChoices = wakeChoices
                .Select(choice => new NormalizedWakeChoice(choice, VoiceRecognitionText.NormalizeTranscript(choice, options)))
                .Where(choice => !string.IsNullOrWhiteSpace(choice.Normalized))
                .OrderByDescending(choice => choice.Normalized.Length)
                .ToArray();
            _minimumConfidence = minimumConfidence;
            DescriptionLabel = asset is null || asset.IsRecognizerDefault
                ? "the recognizer wake model"
                : $"wake asset \"{asset.BuildShortLabel()}\"";
            _engine = new SpeechRecognitionEngine(recognizer);
            LoadWakeGrammars(recognizer, wakeChoices);
        }

        public string DescriptionLabel { get; }

        // Runs recognition on the audio (rejects audio under 125 ms, which is sample rate / 8 samples) and returns the first wake match
        // that is confident enough. 2.5 seconds is the most it will wait for the recogniser.
        public WakeWordDetectionResult Detect(byte[] pcm16MonoAudio, int sampleRateHz)
        {
            if (sampleRateHz <= 0 || pcm16MonoAudio.Length < sampleRateHz / 8)
            {
                return WakeWordDetectionResult.None;
            }

            RecognitionResult? result;

            try
            {
                // Wrap the raw audio as a WAV file in memory, which the recogniser can read.
                using var stream = new MemoryStream(Pcm16WavWriter.WrapPcm16Mono(pcm16MonoAudio, sampleRateHz));

                lock (_sync)
                {
                    if (_isDisposed)
                    {
                        return WakeWordDetectionResult.None;
                    }

                    try
                    {
                        _engine.SetInputToWaveStream(stream);
                        result = _engine.Recognize(TimeSpan.FromSeconds(2.5));
                    }
                    finally
                    {
                        try
                        {
                            _engine.SetInputToNull();
                        }
                        catch
                        {
                        }
                    }
                }
            }
            catch
            {
                return WakeWordDetectionResult.None;
            }

            if (result is null || result.Confidence < _minimumConfidence)
            {
                return WakeWordDetectionResult.None;
            }

            var recognizedText = VoiceRecognitionText.NormalizeTranscript(result.Text, _options);

            if (string.IsNullOrWhiteSpace(recognizedText)
                || !TryExtractDetectedWakeCommand(recognizedText, _normalizedWakeChoices, out var matchedAlias, out var inlineCommand))
            {
                return WakeWordDetectionResult.None;
            }

            var duration = TimeSpan.FromSeconds((pcm16MonoAudio.Length / 2d) / sampleRateHz);

            return new WakeWordDetectionResult(
                IsDetected: true,
                Alias: matchedAlias,
                Confidence: result.Confidence,
                ShouldArmOnly: string.IsNullOrWhiteSpace(inlineCommand),
                MatchOffset: TimeSpan.Zero,
                MatchDuration: duration,
                RecognizedText: recognizedText,
                InlineCommand: inlineCommand);
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_isDisposed)
                {
                    return;
                }

                _isDisposed = true;

                try
                {
                    _engine.SetInputToNull();
                }
                catch
                {
                }

                _engine.Dispose();
            }
        }

        // Loads two grammars: the wake phrase alone, and the wake phrase followed by free dictation.
        private void LoadWakeGrammars(RecognizerInfo recognizer, IReadOnlyList<string> wakeChoices)
        {
            var choices = new Choices(wakeChoices.ToArray());

            var wakeBuilder = new GrammarBuilder
            {
                Culture = recognizer.Culture
            };
            wakeBuilder.Append(choices);
            _engine.LoadGrammar(new Grammar(wakeBuilder) { Name = WakeGrammarName });

            var wakeCommandBuilder = new GrammarBuilder
            {
                Culture = recognizer.Culture
            };
            wakeCommandBuilder.Append(choices);
            wakeCommandBuilder.AppendDictation();
            _engine.LoadGrammar(new Grammar(wakeCommandBuilder) { Name = WakeCommandGrammarName });
        }
    }

    // Runs an external wake word program (for example openWakeWord) as a separate process for each utterance.
    private sealed class ExternalProcessWakeWordDetectionBackend : IWakeWordDetectionBackend
    {
        private readonly JarvisOptions _options;
        private readonly ResolvedMicrophoneProcessingProfile _processingProfile;
        private readonly IReadOnlyList<string> _wakeChoices;
        private readonly IReadOnlyList<NormalizedWakeChoice> _normalizedWakeChoices;
        private readonly WakeWordAssetDescriptor? _asset;
        private readonly WakeWordEngineDescriptor _wakeEngine;
        private readonly double _minimumConfidence;

        public ExternalProcessWakeWordDetectionBackend(
            JarvisOptions options,
            ResolvedMicrophoneProcessingProfile processingProfile,
            IReadOnlyList<string> wakeChoices,
            WakeWordAssetDescriptor? asset,
            WakeWordEngineDescriptor wakeEngine,
            double minimumConfidence)
        {
            _options = options;
            _processingProfile = processingProfile;
            _wakeChoices = wakeChoices;
            _normalizedWakeChoices = wakeChoices
                .Select(choice => new NormalizedWakeChoice(choice, VoiceRecognitionText.NormalizeTranscript(choice, options)))
                .Where(choice => !string.IsNullOrWhiteSpace(choice.Normalized))
                .OrderByDescending(choice => choice.Normalized.Length)
                .ToArray();
            _asset = asset;
            _wakeEngine = wakeEngine;
            _minimumConfidence = minimumConfidence;
        }

        public string DescriptionLabel => $"external wake engine \"{_wakeEngine.BuildShortLabel()}\"";

        // Writes the audio to a temporary WAV file, runs the configured program with that file, parses its JSON output and deletes the file.
        // Blocks the calling thread until the program ends or times out. Returns None on any failure.
        public WakeWordDetectionResult Detect(byte[] pcm16MonoAudio, int sampleRateHz)
        {
            if (sampleRateHz <= 0 || pcm16MonoAudio.Length < sampleRateHz / 8)
            {
                return WakeWordDetectionResult.None;
            }

            // Unique temp file name so two checks never collide.
            var tempFilePath = Path.Combine(Path.GetTempPath(), $"jarvis-wake-{Guid.NewGuid():N}.wav");

            try
            {
                File.WriteAllBytes(tempFilePath, Pcm16WavWriter.WrapPcm16Mono(pcm16MonoAudio, sampleRateHz));
                // Values substituted into the program's command-line template (audio file, sample rate, aliases, model path...).
                var placeholders = BuildPlaceholders(tempFilePath, sampleRateHz);
                var result = ConfiguredExternalProcessRunner.RunAsync(
                        _wakeEngine.ExecutablePath,
                        _wakeEngine.Arguments,
                        _wakeEngine.WorkingDirectory,
                        _wakeEngine.DefinitionDirectory,
                        _wakeEngine.EnvironmentVariables,
                        placeholders,
                        _wakeEngine.CommandTimeoutMilliseconds,
                        CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();

                if (!result.Succeeded || string.IsNullOrWhiteSpace(result.StandardOutput))
                {
                    return WakeWordDetectionResult.None;
                }

                // Parse the program's output into a result; its text is run through the same wake phrase matcher.
                var detection = ExternalWakeEngineResponseParser.Parse(
                    result.StandardOutput,
                    _options,
                    _wakeChoices,
                    recognizedText =>
                    {
                        var matched = TryExtractDetectedWakeCommand(
                            recognizedText,
                            _normalizedWakeChoices,
                            out var alias,
                            out var inlineCommand);
                        return (matched, alias, inlineCommand);
                    },
                    _minimumConfidence);

                return detection.IsDetected && detection.Confidence >= _minimumConfidence
                    ? detection
                    : WakeWordDetectionResult.None;
            }
            catch
            {
                return WakeWordDetectionResult.None;
            }
            finally
            {
                try
                {
                    // Always remove the temporary audio file.
                    if (File.Exists(tempFilePath))
                    {
                        File.Delete(tempFilePath);
                    }
                }
                catch
                {
                }
            }
        }

        public void Dispose()
        {
        }

        // Fills in the placeholder values the engine's command line can refer to.
        private IReadOnlyDictionary<string, string> BuildPlaceholders(string audioFilePath, int sampleRateHz)
        {
            var placeholders = ConfiguredExternalProcessRunner.BuildCommonPlaceholders(_options, _processingProfile);
            placeholders["audioPath"] = audioFilePath;
            placeholders["sampleRateHz"] = sampleRateHz.ToString(System.Globalization.CultureInfo.InvariantCulture);
            placeholders["wakeAliasesJson"] = JsonSerializer.Serialize(_wakeChoices);
            placeholders["wakeAliases"] = string.Join("|", _wakeChoices);
            placeholders["wakeAssetLabel"] = _asset?.BuildShortLabel() ?? "Recognizer Default";
            placeholders["wakeEngineName"] = _wakeEngine.BuildShortLabel();
            placeholders["wakeEngineModelPath"] = ConfiguredExternalProcessRunner.ResolveOptionalPath(
                _wakeEngine.ModelPath,
                _wakeEngine.DefinitionDirectory);
            placeholders["wakeEngineAccessKey"] = _wakeEngine.AccessKey ?? string.Empty;
            return placeholders;
        }
    }

    // A wake phrase as written in settings and its cleaned form used for comparison.
    internal sealed record NormalizedWakeChoice(string Original, string Normalized);
}
