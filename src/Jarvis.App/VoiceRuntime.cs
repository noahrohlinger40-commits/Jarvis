// VoiceRuntime.cs: the "ears" contract of Jarvis plus the Windows built-in recognizer fallback.
// Holds the data types the UI listens to, IVoiceRuntime, the factory that picks an implementation, and SpeechRecognitionVoiceRuntime
// (Windows System.Speech). The main implementation is in OpenAiCompatibleVoiceRuntime.cs; the main form subscribes to its events.
// Path of one spoken request (main path, OpenAiCompatibleVoiceRuntime):
// 1. Microphone: NAudio captures 16-bit mono PCM (16,000 samples per second) in chunks of tens of milliseconds.
// 2. Echo check: VoiceAcousticEchoSuppressor drops chunks that are just Jarvis's own voice coming back through the speakers;
//    real speech over the reply raises a "barge-in" that cuts the reply off.
// 3. Loudness gate: a chunk louder than a noise-aware threshold is speech; the last ~450 ms is kept ("pre-roll") so the first syllable is not clipped.
// 4. End of speech: audio is collected until about 850 ms of quiet (or a length cap); that piece is one "utterance".
// 5. Transcription: the utterance is posted as a WAV file to the local whisper server (127.0.0.1:8178), which returns text plus a confidence score.
// 6. Wake word: the text is checked for "Jarvis" (VoiceRecognitionText), and WakeWordDetector can open the listening window from the raw audio.
//    Without a wake word, text counts only while a listening window is open.
// 7. Hand-off: CommandRecognized fires with the cleaned text; the main form passes it to the assistant.
// 8. Reply: the speaker (Kokoro voice) says the answer and calls SetPlaybackState so step 2 knows when Jarvis is talking.
// The Windows-recognizer class below does steps 3-6 itself using the built-in engine and grammars instead of whisper.
using System.Drawing;
using System.Speech.AudioFormat;
using System.Speech.Recognition;
using Jarvis.Core;

namespace Jarvis.App;

// A point-in-time summary of the voice system shown in the UI: is it usable, is the mic open, is the wake window open.
internal sealed record VoiceStatusSnapshot(
    bool IsAvailable,
    bool IsListening,
    bool IsArmed,
    string StatusText);

// Event payload that carries a VoiceStatusSnapshot to subscribers.
internal sealed class VoiceStatusChangedEventArgs : EventArgs
{
    public VoiceStatusChangedEventArgs(VoiceStatusSnapshot status)
    {
        Status = status;
    }

    public VoiceStatusSnapshot Status { get; }
}

// One reading of the microphone for the level meter: RMS (average loudness) and peak (loudest single sample), both 0 to 1.
internal sealed record VoiceSignalSnapshot(
    double RmsLevel,
    double PeakLevel,
    bool IsSpeechDetected,
    bool IsWakeWindowOpen,
    DateTimeOffset TimestampUtc);

// Event payload that carries a VoiceSignalSnapshot to subscribers.
internal sealed class VoiceSignalChangedEventArgs : EventArgs
{
    public VoiceSignalChangedEventArgs(VoiceSignalSnapshot signal)
    {
        Signal = signal;
    }

    public VoiceSignalSnapshot Signal { get; }
}

// Event payload for a finished spoken command. Text is the cleaned command, RawText is the transcript before the user's
// personal corrections were applied. If RequiresClarification is set, the app asks the user to confirm instead of running it.
internal sealed class VoiceCommandRecognizedEventArgs : EventArgs
{
    public VoiceCommandRecognizedEventArgs(
        string text,
        double? confidence = null,
        bool requiresClarification = false,
        string clarificationPrompt = "",
        string rawText = "")
    {
        Text = text;
        RawText = string.IsNullOrWhiteSpace(rawText) ? text : rawText;
        Confidence = confidence;
        RequiresClarification = requiresClarification;
        ClarificationPrompt = clarificationPrompt;
    }

    public string Text { get; }

    public string RawText { get; }

    public double? Confidence { get; }

    public bool RequiresClarification { get; }

    public string ClarificationPrompt { get; }
}

// Whisper's answer for one utterance: the text and a 0 to 1 confidence (null when the server gave none).
internal sealed record VoiceTranscriptResult(string Text, double? Confidence = null);

// Event payload telling the app the user started talking over Jarvis, so it should stop speaking.
internal sealed class VoiceBargeInEventArgs : EventArgs
{
    public VoiceBargeInEventArgs(string reason)
    {
        Reason = reason;
    }

    public string Reason { get; }
}

// How the Jarvis window is presented (console, voice only, overlay, docked to an edge, or dashboard).
internal enum SurfaceMode
{
    OperatorConsole,
    VoiceOnly,
    OverlayHud,
    EdgeDock,
    Dashboard
}

// What Jarvis is doing right now, used to colour and label the interface.
internal enum SurfaceState
{
    Idle,
    Listening,
    Thinking,
    Acting,
    Speaking,
    AwaitingApproval,
    Error
}

// What the rest of the app needs from any voice input implementation: status and level events, recognised commands,
// barge-in requests, and start/stop. Implementations: OpenAiCompatibleVoiceRuntime, SpeechRecognitionVoiceRuntime, NullVoiceRuntime.
internal interface IVoiceRuntime : IDisposable
{
    event EventHandler<VoiceStatusChangedEventArgs>? StatusChanged;

    event EventHandler<VoiceSignalChangedEventArgs>? SignalLevelChanged;

    event EventHandler<VoiceCommandRecognizedEventArgs>? CommandRecognized;


    event EventHandler<VoiceBargeInEventArgs>? BargeInRequested;

    VoiceStatusSnapshot CurrentStatus { get; }

    VoiceSignalSnapshot CurrentSignal { get; }

    // The app tells the runtime whether Jarvis is currently speaking, so it can ignore or detect speech over its own voice.
    void SetPlaybackState(bool isSpeaking);

    /// <summary>Opens the listening window as if the wake word was just heard, for answering a question.</summary>
    void ListenForFollowUp()
    {
    }

    Task StartAsync(CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);
}

// Helpers that turn recogniser confidence scores into decisions: ignore the text, ask the user to confirm, or accept it.
internal static class VoiceRecognitionConfidence
{
    // When confidence is below the user's configured threshold plus 0.12 (kept between 0.55 and 0.88), the app asks "did you mean...?".
    public static double ClarificationThreshold(JarvisOptions options) =>
        Math.Clamp(options.SpeechRecognitionConfidenceThreshold + 0.12, 0.55, 0.88);

    // True when the recogniser was sure enough about nothing: confidence is known and under the minimum, so drop the result.
    public static bool ShouldIgnore(double? confidence, double minimumConfidence) =>
        confidence is double value && value < minimumConfidence;

    public static bool ShouldClarify(JarvisOptions options, double? confidence, double minimumConfidence)
    {
        if (confidence is not double value)
        {
            return false;
        }

        return value < Math.Max(minimumConfidence, ClarificationThreshold(options));
    }

    public static string BuildClarificationPrompt(string text, double? confidence)
    {
        var confidenceText = confidence is double value
            ? $"{value:P0}"
            : "low";

        return $"I heard \"{text}\", but recognition confidence was {confidenceText}. Say yes to continue, no to cancel, or tell me what you actually said.";
    }

    // Whisper reports an average log-probability per segment (a negative number, 0 = certain). exp() converts it into a
    // 0 to 1 probability. NaN or infinity (a bad value from the server) means "unknown", returned as null.
    public static double? FromAverageLogProbability(double averageLogProbability)
    {
        if (double.IsNaN(averageLogProbability) || double.IsInfinity(averageLogProbability))
        {
            return null;
        }

        return Math.Clamp(Math.Exp(averageLogProbability), 0.0, 1.0);
    }

    // Averages the log-probabilities of all segments, ignoring bad values, then converts to one 0 to 1 confidence.
    public static double? FromLogProbabilities(IEnumerable<double> logProbabilities)
    {
        var values = logProbabilities
            .Where(value => !double.IsNaN(value) && !double.IsInfinity(value))
            .ToArray();

        if (values.Length == 0)
        {
            return null;
        }

        return FromAverageLogProbability(values.Average());
    }
}

// Chooses the voice input implementation at startup based on the "speechRecognitionProvider" setting.
internal static class VoiceRuntimeFactory
{
    // Order: disabled -> silent null runtime; "auto"/"openai-compatible"/"streaming" -> try the whisper runtime first; if that cannot be
    // set up, "auto" falls back to the Windows recogniser while an explicit provider choice reports the problem and stays off.
    public static IVoiceRuntime Create(JarvisOptions options)
    {
        if (!options.SpeechRecognitionEnabled)
        {
            return new NullVoiceRuntime(
                new VoiceStatusSnapshot(
                    IsAvailable: true,
                    IsListening: false,
                    IsArmed: false,
                    StatusText: "Speech recognition is disabled in jarvis.settings.json."));
        }

        var provider = NormalizeProvider(options.SpeechRecognitionProvider);
        var lastStatus = string.Empty;

        if (provider is "auto" or "openai-compatible" or "openai" or "streaming")
        {
            if (OpenAiCompatibleVoiceRuntime.TryCreate(options, out var runtime, out lastStatus))
            {
                return runtime!;
            }

            if (provider is "openai-compatible" or "openai" or "streaming")
            {
                return new NullVoiceRuntime(
                    new VoiceStatusSnapshot(
                        IsAvailable: false,
                        IsListening: false,
                        IsArmed: false,
                        StatusText: lastStatus));
            }
        }

        return CreateWindowsVoiceRuntime(options, lastStatus);
    }

    // Builds the Windows System.Speech fallback, preferring a recogniser that matches the configured culture (e.g. en-US).
    private static IVoiceRuntime CreateWindowsVoiceRuntime(JarvisOptions options, string fallbackReason)
    {
        try
        {
            var installedRecognizers = SpeechRecognitionEngine.InstalledRecognizers().ToArray();

            if (installedRecognizers.Length == 0)
            {
                return new NullVoiceRuntime(
                    new VoiceStatusSnapshot(
                        IsAvailable: false,
                        IsListening: false,
                        IsArmed: false,
                        StatusText: $"No Windows speech recognizer is installed for {options.RecognizerCulture}."));
            }

            var recognizer = installedRecognizers.FirstOrDefault(
                    candidate => string.Equals(
                        candidate.Culture.Name,
                        options.RecognizerCulture,
                        StringComparison.OrdinalIgnoreCase))
                ?? installedRecognizers[0];

            return new SpeechRecognitionVoiceRuntime(options, recognizer, fallbackReason);
        }
        catch (Exception exception)
        {
            return new NullVoiceRuntime(
                new VoiceStatusSnapshot(
                    IsAvailable: false,
                    IsListening: false,
                    IsArmed: false,
                    StatusText: $"Speech recognition unavailable: {exception.Message}"));
        }
    }

    // Lower-cases the provider setting; empty means "auto".
    private static string NormalizeProvider(string? provider)
    {
        if (string.IsNullOrWhiteSpace(provider))
        {
            return "auto";
        }

        return provider.Trim().ToLowerInvariant();
    }
}

// A do-nothing voice runtime used when speech is disabled or unavailable; it only reports a fixed status message.
internal sealed class NullVoiceRuntime : IVoiceRuntime
{
    private EventHandler<VoiceStatusChangedEventArgs>? _statusChanged;
    private EventHandler<VoiceSignalChangedEventArgs>? _signalChanged;

    public NullVoiceRuntime(VoiceStatusSnapshot status)
    {
        CurrentStatus = status;
        CurrentSignal = new VoiceSignalSnapshot(0, 0, false, false, DateTimeOffset.UtcNow);
    }

    public event EventHandler<VoiceStatusChangedEventArgs>? StatusChanged
    {
        add => _statusChanged += value;
        remove => _statusChanged -= value;
    }

    public event EventHandler<VoiceSignalChangedEventArgs>? SignalLevelChanged
    {
        add => _signalChanged += value;
        remove => _signalChanged -= value;
    }

    public event EventHandler<VoiceCommandRecognizedEventArgs>? CommandRecognized
    {
        add { }
        remove { }
    }

    public event EventHandler<VoiceBargeInEventArgs>? BargeInRequested
    {
        add { }
        remove { }
    }

    public VoiceStatusSnapshot CurrentStatus { get; private set; }

    public VoiceSignalSnapshot CurrentSignal { get; private set; }

    public void SetPlaybackState(bool isSpeaking)
    {
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _statusChanged?.Invoke(this, new VoiceStatusChangedEventArgs(CurrentStatus));
        _signalChanged?.Invoke(this, new VoiceSignalChangedEventArgs(CurrentSignal));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _statusChanged = null;
        _signalChanged = null;
    }
}

// Offline fallback that uses the Windows built-in speech engine instead of whisper. It listens with three kinds of
// grammar: a wake-phrase grammar, short command grammars (optionally prefixed by the wake word), and free dictation.
// It opens a "wake window" after the wake word during which the next recognised phrase counts as a command.
internal sealed class SpeechRecognitionVoiceRuntime : IVoiceRuntime
{
    private const string WakePhraseGrammarName = "wake-phrase";
    private const string DictationGrammarName = "dictation";
    private const string CommandGrammarPrefix = "command:";
    private const string WakeCommandGrammarPrefix = "wake-command:";

    // Guards the state flags below; recogniser events arrive on other threads.
    private readonly object _sync = new();
    private readonly JarvisOptions _options;
    private readonly RecognizerInfo _recognizer;
    private readonly SpeechRecognitionEngine _engine;
    private readonly string _fallbackReason;
    // Our own microphone reader, so we can apply gain and pick a device; null means the Windows default device is used.
    private MicrophonePcmStream? _microphoneStream;

    // Fires when the wake window runs out of time.
    private System.Threading.Timer? _wakeWindowTimer;
    // Last time we asked Jarvis to stop talking, used to avoid repeated barge-in requests.
    private DateTimeOffset _lastBargeInUtc = DateTimeOffset.MinValue;
    // True while the microphone/recogniser is running.
    private bool _isListening;
    // True while the wake window is open (wake word heard, waiting for the command).
    private bool _isArmed;
    // True while Jarvis is speaking.
    private bool _isPlaybackActive;

    public SpeechRecognitionVoiceRuntime(JarvisOptions options, RecognizerInfo recognizer, string fallbackReason = "")
    {
        _options = options;
        _recognizer = recognizer;
        _fallbackReason = fallbackReason;
        _engine = new SpeechRecognitionEngine(recognizer);
        _engine.SpeechRecognized += OnSpeechRecognized;
        _engine.RecognizeCompleted += OnRecognizeCompleted;
        _engine.AudioLevelUpdated += OnAudioLevelUpdated;


        // Three kinds of grammar are loaded: the wake phrase, the fixed command phrases, then open dictation as the catch-all.
        var wakePhraseGrammarBuilder = new GrammarBuilder
        {
            Culture = recognizer.Culture
        };
        wakePhraseGrammarBuilder.Append(new Choices(VoiceRecognitionText.BuildWakeGrammarChoices(options).ToArray()));

        _engine.LoadGrammar(new Grammar(wakePhraseGrammarBuilder) { Name = WakePhraseGrammarName });
        LoadCommandGrammars();
        _engine.LoadGrammar(new DictationGrammar { Name = DictationGrammarName });

        CurrentStatus = new VoiceStatusSnapshot(
            IsAvailable: true,
            IsListening: false,
            IsArmed: false,
            StatusText: BuildIdleStatusText());
        CurrentSignal = new VoiceSignalSnapshot(0, 0, false, false, DateTimeOffset.UtcNow);
    }

    public event EventHandler<VoiceStatusChangedEventArgs>? StatusChanged;

    public event EventHandler<VoiceSignalChangedEventArgs>? SignalLevelChanged;

    public event EventHandler<VoiceCommandRecognizedEventArgs>? CommandRecognized;

    public event EventHandler<VoiceBargeInEventArgs>? BargeInRequested;

    public VoiceStatusSnapshot CurrentStatus { get; private set; }

    public VoiceSignalSnapshot CurrentSignal { get; private set; }

    public void SetPlaybackState(bool isSpeaking)
    {
        lock (_sync)
        {
            _isPlaybackActive = isSpeaking;
        }
    }

    // Starts the microphone and the recogniser. If it fails, reports the failure as an unavailable status instead of throwing.
    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            if (_isListening)
            {
                PublishStatus(_isArmed, CurrentStatus.StatusText);
                return Task.CompletedTask;
            }

            try
            {
                var inputDescription = ConfigureAudioInput();
                _engine.RecognizeAsync(RecognizeMode.Multiple);
                _isListening = true;
                PublishSignal(0, 0, false);

                var listeningText = _options.WakeWordEnabled
                    ? $"Listening for \"{_options.WakePhrase}\" with offline {_recognizer.Culture.Name} speech recognition on {inputDescription}."
                    : $"Listening for voice directives with offline {_recognizer.Culture.Name} speech recognition on {inputDescription}.";

                PublishStatus(
                    isArmed: false,
                    statusText: string.IsNullOrWhiteSpace(_fallbackReason)
                        ? listeningText
                        : $"{listeningText} Cloud recognition unavailable: {_fallbackReason}");
            }
            catch (Exception exception)
            {
                DisposeMicrophoneStream();
                _isListening = false;
                PublishStatus(
                    isArmed: false,
                    statusText: $"Microphone startup failed: {exception.Message}",
                    isAvailable: false);
            }
        }

        return Task.CompletedTask;
    }

    // Stops listening and releases the microphone.
    public Task StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            if (!_isListening)
            {
                return Task.CompletedTask;
            }

            ClearWakeWindow();

            // End the audio stream first so the recognizer's blocked read returns and it can shut down.
            _microphoneStream?.StopCapture();

            try
            {
                _engine.RecognizeAsyncCancel();
                _engine.RecognizeAsyncStop();
            }
            catch
            {
            }

            DisposeMicrophoneStream();
            _isListening = false;
            _isArmed = false;
            PublishSignal(0, 0, false);
            PublishStatus(isArmed: false, statusText: "Microphone listening paused.");
        }

        return Task.CompletedTask;
    }

    // Shuts everything down and unhooks the recogniser's events.
    public void Dispose()
    {
        lock (_sync)
        {
            ClearWakeWindow();
            _microphoneStream?.StopCapture();

            try
            {
                if (_isListening)
                {
                    _engine.RecognizeAsyncCancel();
                    _engine.RecognizeAsyncStop();
                }
            }
            catch
            {
            }

            _engine.SpeechRecognized -= OnSpeechRecognized;
            _engine.RecognizeCompleted -= OnRecognizeCompleted;
            _engine.AudioLevelUpdated -= OnAudioLevelUpdated;
            _engine.Dispose();
            DisposeMicrophoneStream();
            _isListening = false;
            _isArmed = false;
            PublishSignal(0, 0, false);
        }
    }

    /// <summary>
    /// Points the recognizer at the configured microphone with automatic gain. Falls back to the
    /// Windows default device if the configured one cannot be opened.
    /// </summary>
    private string ConfigureAudioInput()
    {
        DisposeMicrophoneStream();

        try
        {
            if (AudioDeviceResolver.TryResolveMicrophoneDevice(_options.SpeechRecognitionDeviceName, out var deviceNumber))
            {
                var profile = MicrophoneProcessingProfileResolver.Resolve(_options);
                var stream = new MicrophonePcmStream(
                    deviceNumber,
                    profile.RollingCaptureBufferMilliseconds,
                    profile.InputGainCap,
                    profile.TargetPeakLevel);

                try
                {
                    _engine.SetInputToAudioStream(
                        stream,
                        new SpeechAudioFormatInfo(MicrophonePcmStream.SampleRateHz, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
                    stream.LevelMeasured += HandleAudioLevel;
                    stream.StartCapture();
                    _microphoneStream = stream;
                    return $"\"{stream.DeviceName.Trim()}\"";
                }
                catch
                {
                    stream.Dispose();
                }
            }
        }
        catch
        {
            // Fall through to the Windows default device below.
        }

        _engine.SetInputToDefaultAudioDevice();
        return "the Windows default microphone";
    }

    // Closes our microphone reader, if any.
    private void DisposeMicrophoneStream()
    {
        var stream = _microphoneStream;
        _microphoneStream = null;
        stream?.Dispose();
    }

    // Called by the Windows recogniser whenever it decides it heard something: filters by confidence, cleans the text,
    // looks for the wake word, and publishes a command if the wake window is open or the wake word came with a command.
    private void OnSpeechRecognized(object? sender, SpeechRecognizedEventArgs eventArgs)
    {
        if (eventArgs.Result is null || string.IsNullOrWhiteSpace(eventArgs.Result.Text))
        {
            return;
        }

        // Command grammars are matched against a fixed phrase list so they get a lower bar than free dictation (see GetMinimumConfidence).
        var minimumConfidence = GetMinimumConfidence(eventArgs.Result);

        if (eventArgs.Result.Confidence < minimumConfidence)
        {
            return;
        }

        // Keep both the raw transcript and a version with the user's personal word fixes applied.
        var rawRecognizedText = VoiceRecognitionText.NormalizeTranscript(eventArgs.Result.Text, _options, applyPersonalCorrections: false);
        var recognizedText = VoiceRecognitionText.ApplyPersonalCorrections(rawRecognizedText, _options);

        // Wake word turned off: every recognised phrase is a command.
        if (!_options.WakeWordEnabled)
        {
            PublishRecognizedCommand(
                JarvisCommandCatalog.NormalizeVoiceDirective(recognizedText),
                rawRecognizedText,
                eventArgs.Result.Confidence,
                minimumConfidence);
            return;
        }

        // "Jarvis open notepad" in one go runs "open notepad"; just "Jarvis" opens the wake window and waits.
        if (VoiceRecognitionText.TryExtractWakeCommand(recognizedText, _options, out var inlineCommand))
        {
            if (string.IsNullOrWhiteSpace(inlineCommand))
            {
                ArmWakeWindow();
                return;
            }

            ResetToIdle();
            PublishRecognizedCommand(
                JarvisCommandCatalog.NormalizeVoiceDirective(inlineCommand),
                rawRecognizedText,
                eventArgs.Result.Confidence,
                minimumConfidence);
            return;
        }

        // No wake word in this phrase: only accept it if the wake window is already open, then close the window.
        lock (_sync)
        {
            if (!_isArmed)
            {
                return;
            }

            _isArmed = false;
            ClearWakeWindow();
        }

        PublishStatus(isArmed: false, statusText: BuildIdleStatusText());
        PublishRecognizedCommand(
            JarvisCommandCatalog.NormalizeVoiceDirective(recognizedText),
            rawRecognizedText,
            eventArgs.Result.Confidence,
            minimumConfidence);
    }

    // The recogniser reports its own level as 0-100; scale it to 0-1.
    private void OnAudioLevelUpdated(object? sender, AudioLevelUpdatedEventArgs eventArgs)
    {
        HandleAudioLevel(Math.Clamp(eventArgs.AudioLevel / 100d, 0, 1));
    }

    // Updates the level meter, and asks Jarvis to stop speaking if the user talks over it.
    private void HandleAudioLevel(double level)
    {
        bool isListening;
        bool isWakeWindowOpen;

        lock (_sync)
        {
            isListening = _isListening;
            isWakeWindowOpen = _isArmed;
        }

        if (!isListening)
        {
            return;
        }

        // Level above which we call it "speech" for the meter: four times the configured activity threshold, at least 0.08.
        var speechThreshold = Math.Max(0.08, _options.SpeechRecognitionVoiceActivityThreshold * 4.0);

        if (ShouldRequestBargeIn(level))
        {
            BargeInRequested?.Invoke(this, new VoiceBargeInEventArgs("speech-over-playback"));
        }

        PublishSignal(level, level, level >= speechThreshold, isWakeWindowOpen);
    }

    // The recogniser stopped by itself (error or cancel); reset the state so the UI shows it is no longer listening.
    private void OnRecognizeCompleted(object? sender, RecognizeCompletedEventArgs eventArgs)
    {
        if (eventArgs.Error is null && !eventArgs.Cancelled)
        {
            return;
        }

        lock (_sync)
        {
            _isListening = false;
            _isArmed = false;
            ClearWakeWindow();
        }

        var statusText = eventArgs.Error is null
            ? "Speech recognition stopped."
            : $"Speech recognition stopped: {eventArgs.Error.Message}";

        PublishSignal(0, 0, false);
        PublishStatus(isArmed: false, statusText: statusText, isAvailable: eventArgs.Error is null);
    }

    // Opens the wake window without a wake word, e.g. after Jarvis asked a question and expects an answer.
    public void ListenForFollowUp()
    {
        if (_isListening)
        {
            ArmWakeWindow();
        }
    }

    // Opens the wake window and starts a timer; when it runs out (at least 2 seconds) the window closes.
    private void ArmWakeWindow()
    {
        lock (_sync)
        {
            _isArmed = true;
            ClearWakeWindow();
            _wakeWindowTimer = new System.Threading.Timer(
                _ => OnWakeWindowExpired(),
                null,
                TimeSpan.FromSeconds(Math.Max(2, _options.VoiceCommandTimeoutSeconds)),
                Timeout.InfiniteTimeSpan);
        }

        PublishStatus(
            isArmed: true,
            statusText: $"Wake word detected. Listening for the next command for {_options.VoiceCommandTimeoutSeconds} seconds.");
    }

    // Timer callback: closes the wake window if nobody spoke a command in time.
    private void OnWakeWindowExpired()
    {
        lock (_sync)
        {
            if (!_isArmed)
            {
                return;
            }

            _isArmed = false;
            ClearWakeWindow();
        }

        PublishStatus(isArmed: false, statusText: BuildIdleStatusText());
    }

    // Closes the wake window and returns to waiting for the wake word.
    private void ResetToIdle()
    {
        lock (_sync)
        {
            _isArmed = false;
            ClearWakeWindow();
        }

        PublishStatus(isArmed: false, statusText: BuildIdleStatusText());
    }

    // Builds and broadcasts a new status, and refreshes the level event so the UI sees the wake-window flag.
    private void PublishStatus(bool isArmed, string statusText, bool? isAvailable = null)
    {
        var snapshot = new VoiceStatusSnapshot(
            IsAvailable: isAvailable ?? CurrentStatus.IsAvailable,
            IsListening: _isListening,
            IsArmed: isArmed,
            StatusText: statusText);

        CurrentStatus = snapshot;
        StatusChanged?.Invoke(this, new VoiceStatusChangedEventArgs(snapshot));
        PublishSignal(
            CurrentSignal.RmsLevel,
            CurrentSignal.PeakLevel,
            CurrentSignal.IsSpeechDetected,
            isArmed);
    }

    // Human-readable "what am I doing" line for the status bar.
    private string BuildIdleStatusText()
    {
        return _options.WakeWordEnabled
            ? $"Listening for \"{_options.WakePhrase}\" with {_recognizer.Culture.Name} speech recognition."
            : $"Listening for voice directives with {_recognizer.Culture.Name} speech recognition.";
    }

    // Stops and forgets the wake-window timer.
    private void ClearWakeWindow()
    {
        _wakeWindowTimer?.Dispose();
        _wakeWindowTimer = null;
    }

    // Broadcasts a new microphone level reading (values forced into 0..1).
    private void PublishSignal(
        double rmsLevel,
        double peakLevel,
        bool isSpeechDetected,
        bool? isWakeWindowOpen = null)
    {
        CurrentSignal = new VoiceSignalSnapshot(
            Math.Clamp(rmsLevel, 0, 1),
            Math.Clamp(peakLevel, 0, 1),
            isSpeechDetected,
            isWakeWindowOpen ?? _isArmed,
            DateTimeOffset.UtcNow);
        SignalLevelChanged?.Invoke(this, new VoiceSignalChangedEventArgs(CurrentSignal));
    }

    // Barge-in: user speech over Jarvis's reply. Needs level of at least 0.16 (loud, so it is not just the speaker's echo),
    // playback active, and at most one request per 1.2 seconds.
    private bool ShouldRequestBargeIn(double level)
    {
        if (!_options.SpeechRecognitionBargeInEnabled || level < 0.16)
        {
            return false;
        }

        lock (_sync)
        {
            if (!_isPlaybackActive
                || DateTimeOffset.UtcNow - _lastBargeInUtc <= TimeSpan.FromMilliseconds(1200))
            {
                return false;
            }

            _lastBargeInUtc = DateTimeOffset.UtcNow;
            return true;
        }
    }

    // Registers one grammar per known command so the recogniser can match exact phrases (help, status, time, notes, etc.).
    private void LoadCommandGrammars()
    {
        LoadPhraseGrammar("help", JarvisCommandCatalog.HelpPhrases);
        LoadPhraseGrammar("status", JarvisCommandCatalog.StatusPhrases);
        LoadPhraseGrammar("time", JarvisCommandCatalog.TimePhrases);
        LoadPhraseGrammar("notes", JarvisCommandCatalog.NotesPhrases);
        LoadPhraseGrammar("weather", JarvisCommandCatalog.WeatherPhrases);
        LoadPhraseGrammar("system", JarvisCommandCatalog.SystemPhrases);
        LoadPhraseGrammar("computer", JarvisCommandCatalog.ComputerPhrases);
        LoadPhraseGrammar("apps", JarvisCommandCatalog.AppsPhrases);
        LoadPhraseGrammar("exit", JarvisCommandCatalog.ExitPhrases);

        LoadPayloadGrammar("remember", JarvisCommandCatalog.RememberPrefixes);
        LoadPayloadGrammar("recall", JarvisCommandCatalog.RecallPrefixes);
        LoadPayloadGrammar("run", JarvisCommandCatalog.RunPrefixes);
        LoadPayloadGrammar("open-app", JarvisCommandCatalog.OpenAppPrefixes);
        LoadPayloadGrammar("open-path", JarvisCommandCatalog.OpenPathPrefixes);
        LoadPayloadGrammar("list-files", JarvisCommandCatalog.ListFilesPrefixes);
        LoadPayloadGrammar("read-file", JarvisCommandCatalog.ReadFilePrefixes);
        LoadPayloadGrammar("find-files", JarvisCommandCatalog.FindFilesPrefixes);
        LoadPayloadGrammar("weather", JarvisCommandCatalog.WeatherPrefixes);
        LoadPayloadGrammar("browse", JarvisCommandCatalog.BrowsePrefixes);
        LoadPayloadGrammar("search-web", JarvisCommandCatalog.SearchWebPrefixes);
        LoadPayloadGrammar("click-element", JarvisCommandCatalog.ClickPrefixes);
        LoadPayloadGrammar("type-into", JarvisCommandCatalog.TypePrefixes);
        LoadPayloadGrammar("get-element-text", JarvisCommandCatalog.GetTextPrefixes);
        LoadPayloadGrammar("scroll", JarvisCommandCatalog.ScrollPrefixes);
        LoadPayloadGrammar("wait", JarvisCommandCatalog.WaitPrefixes);
    }

    // Loads a fixed-phrase grammar twice: once as-is, once requiring the wake word in front ("Jarvis, what time is it").
    private void LoadPhraseGrammar(string suffix, IReadOnlyList<string> phrases)
    {
        _engine.LoadGrammar(BuildPhraseGrammar($"{CommandGrammarPrefix}{suffix}", phrases, includeWakePrefix: false));
        _engine.LoadGrammar(BuildPhraseGrammar($"{WakeCommandGrammarPrefix}{suffix}", phrases, includeWakePrefix: true));
    }

    // Loads a "prefix then free text" grammar (e.g. "open app" + whatever app name), with and without the wake word.
    private void LoadPayloadGrammar(string suffix, IReadOnlyList<string> prefixes)
    {
        _engine.LoadGrammar(BuildPayloadGrammar($"{CommandGrammarPrefix}{suffix}", prefixes, includeWakePrefix: false));
        _engine.LoadGrammar(BuildPayloadGrammar($"{WakeCommandGrammarPrefix}{suffix}", prefixes, includeWakePrefix: true));
    }

    // A grammar that matches exactly one of the listed phrases.
    private Grammar BuildPhraseGrammar(string grammarName, IReadOnlyList<string> phrases, bool includeWakePrefix)
    {
        var builder = CreateGrammarBuilder(includeWakePrefix);
        builder.Append(new Choices(phrases.ToArray()));
        return new Grammar(builder) { Name = grammarName };
    }

    // A grammar that matches one of the listed prefixes followed by anything the user says (dictation).
    private Grammar BuildPayloadGrammar(string grammarName, IReadOnlyList<string> prefixes, bool includeWakePrefix)
    {
        var builder = CreateGrammarBuilder(includeWakePrefix);
        builder.Append(new Choices(prefixes.ToArray()));
        builder.AppendDictation();
        return new Grammar(builder) { Name = grammarName };
    }

    // Starts a grammar, optionally beginning with the wake-word choices.
    private GrammarBuilder CreateGrammarBuilder(bool includeWakePrefix)
    {
        var builder = new GrammarBuilder
        {
            Culture = _recognizer.Culture
        };

        if (includeWakePrefix)
        {
            builder.Append(new Choices(VoiceRecognitionText.BuildWakeGrammarChoices(_options).ToArray()));
        }

        return builder;
    }

    // Per-result confidence bar: free dictation uses the configured threshold; the stricter fixed command grammars can be
    // trusted at 0.20 less (but never under 0.35).
    private double GetMinimumConfidence(RecognitionResult result)
    {
        var grammarName = result.Grammar?.Name ?? string.Empty;

        if (string.Equals(grammarName, DictationGrammarName, StringComparison.Ordinal))
        {
            return _options.SpeechRecognitionConfidenceThreshold;
        }

        return Math.Max(0.35, _options.SpeechRecognitionConfidenceThreshold - 0.20);
    }

    // Drops weak results, flags middling ones for confirmation, and raises CommandRecognized.
    private void PublishRecognizedCommand(string text, string rawText, double? confidence, double minimumConfidence)
    {
        if (string.IsNullOrWhiteSpace(text) || VoiceRecognitionConfidence.ShouldIgnore(confidence, minimumConfidence))
        {
            return;
        }

        var requiresClarification = VoiceRecognitionConfidence.ShouldClarify(_options, confidence, minimumConfidence);
        var prompt = requiresClarification
            ? VoiceRecognitionConfidence.BuildClarificationPrompt(rawText, confidence)
            : string.Empty;

        CommandRecognized?.Invoke(
            this,
            new VoiceCommandRecognizedEventArgs(text, confidence, requiresClarification, prompt, rawText));
    }
}
