// Speech.cs: the "mouth" contract. ISpeaker is what the app calls to say a reply aloud; SpeakerFactory picks the real voice.
// The preferred voice is OpenAiCompatibleStreamingSpeaker (local Kokoro server); WindowsSpeechSpeaker below is the built-in
// Windows voice, used when the Kokoro voice is not configured or fails. NullSpeaker is used when voice is turned off.
// Speaking state is reported through StatusChanged so VoiceRuntime knows when Jarvis is talking (echo handling).
using System.Speech.Synthesis;
using Jarvis.Core;

namespace Jarvis.App;

// Whether Jarvis is speaking right now and the text being spoken.
internal sealed record SpeakerStatusSnapshot(
    bool IsSpeaking,
    string Message,
    DateTimeOffset TimestampUtc);

// Event payload carrying a SpeakerStatusSnapshot.
internal sealed class SpeakerStatusChangedEventArgs : EventArgs
{
    public SpeakerStatusChangedEventArgs(SpeakerStatusSnapshot status)
    {
        Status = status;
    }

    public SpeakerStatusSnapshot Status { get; }
}

// What the app needs from any voice: say something, stop talking, and report speaking status.
internal interface ISpeaker
{
    event EventHandler<SpeakerStatusChangedEventArgs>? StatusChanged;

    SpeakerStatusSnapshot CurrentStatus { get; }

    void Interrupt();

    Task SpeakAsync(string message, CancellationToken cancellationToken);
}

// Chooses the speaker from settings: disabled -> silent; auto/openai-compatible/streaming -> try the Kokoro-style speech server;
// anything else (or if that cannot be set up) -> the Windows voice.
internal static class SpeakerFactory
{
    public static ISpeaker Create(JarvisOptions options)
    {
        if (!options.VoiceEnabled)
        {
            return new NullSpeaker();
        }

        var provider = NormalizeProvider(options.VoiceProvider);

        if (provider is "auto" or "openai-compatible" or "openai" or "streaming")
        {
            if (OpenAiCompatibleStreamingSpeaker.TryCreate(options, out var speaker))
            {
                return speaker!;
            }
        }

        return new WindowsSpeechSpeaker(options);
    }

    // Lower-cases the voice provider setting; empty means "auto".
    private static string NormalizeProvider(string? provider)
    {
        if (string.IsNullOrWhiteSpace(provider))
        {
            return "auto";
        }

        return provider.Trim().ToLowerInvariant();
    }
}

// A speaker that says nothing, used when voice output is switched off.
internal sealed class NullSpeaker : ISpeaker
{
    public event EventHandler<SpeakerStatusChangedEventArgs>? StatusChanged
    {
        add { }
        remove { }
    }

    public SpeakerStatusSnapshot CurrentStatus { get; } = new(false, string.Empty, DateTimeOffset.UtcNow);

    public void Interrupt()
    {
    }

    public Task SpeakAsync(string message, CancellationToken cancellationToken) => Task.CompletedTask;
}

// Speaks with the built-in Windows text-to-speech (System.Speech). Replies one at a time and can be interrupted.
internal sealed class WindowsSpeechSpeaker : ISpeaker
{
    // Protects the active-speech fields from being changed by two threads at once.
    private readonly object _sync = new();
    // One reply at a time: a second SpeakAsync waits here until the first finishes.
    private readonly SemaphoreSlim _gate = new(1, 1);
    // Cancel handle and synthesizer of the reply being spoken, so Interrupt can stop it.
    private CancellationTokenSource? _activeSpeechCts;
    private SpeechSynthesizer? _activeSynthesizer;
    private readonly JarvisOptions _options;

    public WindowsSpeechSpeaker(JarvisOptions options)
    {
        _options = options;
    }

    public event EventHandler<SpeakerStatusChangedEventArgs>? StatusChanged;

    public SpeakerStatusSnapshot CurrentStatus { get; private set; } = new(false, string.Empty, DateTimeOffset.UtcNow);

    // Waits for its turn, speaks the message, and always reports "not speaking" afterwards. A stop request is swallowed;
    // cancellation by the caller itself still propagates.
    public async Task SpeakAsync(string message, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken);

        using var interruptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            var normalized = message.Trim();
            PublishStatus(true, normalized);

            await SpeakWithSynthesizerAsync(normalized, interruptCts, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_activeSpeechCts, interruptCts))
                {
                    _activeSpeechCts = null;
                    _activeSynthesizer = null;
                }
            }

            PublishStatus(false, string.Empty);
            _gate.Release();
        }
    }

    // Stops the current reply immediately (used by "Jarvis, stop" and when the user talks over it).
    public void Interrupt()
    {
        CancellationTokenSource? cts;
        SpeechSynthesizer? synthesizer;

        lock (_sync)
        {
            cts = _activeSpeechCts;
            synthesizer = _activeSynthesizer;
        }

        try
        {
            cts?.Cancel();
            synthesizer?.SpeakAsyncCancelAll();
        }
        catch
        {
        }
    }

    // Runs one System.Speech synthesis and waits for it to finish or be cancelled.
    private async Task SpeakWithSynthesizerAsync(
        string message,
        CancellationTokenSource interruptCts,
        CancellationToken callerToken)
    {
        using var speaker = new SpeechSynthesizer();
        // Turns the "speech finished" event into something we can await.
        var completed = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnSpeakCompleted(object? sender, SpeakCompletedEventArgs eventArgs)
        {
            if (eventArgs.Error is not null)
            {
                completed.TrySetException(eventArgs.Error);
                return;
            }

            completed.TrySetResult(null);
        }

        speaker.SetOutputToDefaultAudioDevice();
        
        // Volume setting (0..1) becomes 0..100; whisper mode is 60% as loud.
        var volume = Math.Clamp(_options.SpeakerVolumeMultiplier * 100, 0, 100);
        if (_options.WhisperModeEnabled)
        {
            volume *= 0.6;
        }
        speaker.Rate = AssistantPersonaConfiguration.ResolveWindowsSpeechRate(_options);
        speaker.Volume = (int)volume;
        speaker.SpeakCompleted += OnSpeakCompleted;

        lock (_sync)
        {
            _activeSpeechCts = interruptCts;
            _activeSynthesizer = speaker;
        }

        // If the reply is interrupted, tell the synthesizer to stop talking.
        using var registration = interruptCts.Token.Register(() =>
        {
            try
            {
                speaker.SpeakAsyncCancelAll();
            }
            catch
            {
            }
        });

        try
        {
            speaker.SpeakAsync(message);
            await completed.Task.WaitAsync(interruptCts.Token);
        }
        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
        {
        }
        finally
        {
            speaker.SpeakCompleted -= OnSpeakCompleted;

            lock (_sync)
            {
                if (ReferenceEquals(_activeSynthesizer, speaker))
                {
                    _activeSynthesizer = null;
                    _activeSpeechCts = null;
                }
            }
        }
    }

    // Records and broadcasts the new speaking state.
    private void PublishStatus(bool isSpeaking, string message)
    {
        CurrentStatus = new SpeakerStatusSnapshot(isSpeaking, message, DateTimeOffset.UtcNow);
        StatusChanged?.Invoke(this, new SpeakerStatusChangedEventArgs(CurrentStatus));
    }
}
