// VoiceRuntime.cs: the "ears" contract of Jarvis.
// Holds the data types the UI listens to, IVoiceRuntime, the factory that picks an implementation, and NullVoiceRuntime.
// The main implementation is in OpenAiCompatibleVoiceRuntime.cs; the main form subscribes to its events.
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
// barge-in requests, and start/stop. Implementations: OpenAiCompatibleVoiceRuntime, NullVoiceRuntime.
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

// Helpers around recogniser confidence scores: the "did you mean...?" prompt and whisper's log-probability conversion.
internal static class VoiceRecognitionConfidence
{
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
    // Order: disabled -> silent null runtime; "auto"/"openai-compatible"/"streaming" -> the whisper runtime; if that cannot be
    // set up, or the provider is anything else, the silent null runtime reports why.
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
        var status = $"Speech recognition provider '{options.SpeechRecognitionProvider}' is not supported; use \"openai-compatible\".";

        if (provider is "auto" or "openai-compatible" or "openai" or "streaming"
            && OpenAiCompatibleVoiceRuntime.TryCreate(options, out var runtime, out status))
        {
            return runtime!;
        }

        return new NullVoiceRuntime(
            new VoiceStatusSnapshot(
                IsAvailable: false,
                IsListening: false,
                IsArmed: false,
                StatusText: status));
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
