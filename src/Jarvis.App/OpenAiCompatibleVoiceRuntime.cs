// OpenAiCompatibleVoiceRuntime.cs: Jarvis's main microphone pipeline. See the step-by-step path in VoiceRuntime.cs.
// It records the microphone, cuts the audio into utterances, sends each one to a local whisper server (an "OpenAI-compatible"
// /audio/transcriptions endpoint, normally 127.0.0.1:8178), and raises CommandRecognized with the cleaned text.
// Also contains OpenAiCompatibleTranscriptionClient (the HTTP call).
// Audio basics used throughout: samples are 16-bit whole numbers (-32768..32767), two bytes each; "level" is a sample divided
// by 32768 so it falls between -1 and 1; sample rate 16,000 means 16,000 samples (32,000 bytes) per second.
using System.Net.Http.Headers;
using System.Text.Json;
using Jarvis.Core;
using NAudio.Wave;

namespace Jarvis.App;

// Listens continuously, detects when someone is speaking, and turns each spoken piece into a command via whisper.
internal sealed class OpenAiCompatibleVoiceRuntime : IVoiceRuntime
{
    // Upper limit on one utterance (15 s) so the buffer cannot grow forever.
    // A single spoken command is far shorter than this; the cap only matters for steady background sound.
    private const int MaximumUtteranceMilliseconds = 15_000;

    // While Jarvis talks through a speaker its own voice never leaves the 850 ms of quiet that ends an
    // utterance, so "Jarvis, stop" would wait inside a 15 s piece of echo. Short pieces get it heard quickly;
    // the pre-roll overlap keeps a wake word cut at a boundary whole.
    private const int PlaybackUtteranceMilliseconds = 3_000;

    // Protects the fields below: audio callbacks, timers and transcription tasks all run on different threads.
    private readonly object _sync = new();
    private readonly JarvisOptions _options;
    private readonly OpenAiCompatibleTranscriptionClient _client;
    // Only one transcription request at a time, so the local whisper server is not flooded and results arrive in order.
    private readonly SemaphoreSlim _transcriptionGate = new(1, 1);
    // The openWakeWord "hey jarvis" detector, or null when no such engine is configured.
    private readonly WakeWordDetector? _wakeWordDetector;
    private readonly ResolvedMicrophoneProcessingProfile _audioProcessing;
    // Decides whether quiet or Jarvis-like sound is Jarvis's own reply leaking from the speakers.
    private readonly VoiceAcousticEchoSuppressor _echoSuppressor;
    // Rolling window of the most recent audio chunks (pre-roll), so the start of a sentence is not cut off.
    private readonly Queue<byte[]> _preRollChunks = new();
    // Size limits in bytes, converted from milliseconds in the constructor: pre-roll, shortest utterance worth sending,
    // longest utterance, and longest utterance while Jarvis is speaking.
    private readonly int _preRollByteLimit;
    private readonly int _minimumUtteranceBytes;
    private readonly int _maximumUtteranceBytes;
    private readonly int _playbackUtteranceBytes;
    // The live microphone recording (NAudio).
    private WaveInEvent? _capture;
    // Cancelled on stop so in-flight transcriptions give up.
    private CancellationTokenSource? _lifetimeCts;
    // Audio of the sentence being recorded right now; null while nobody is speaking.
    private MemoryStream? _activeSpeechBuffer;
    // Closes the listening window if no command arrives in time.
    private System.Threading.Timer? _wakeWindowTimer;
    // When speech was last heard; used to detect the end of an utterance.
    private DateTimeOffset _lastVoiceDetectedUtc;
    private int _preRollBytes;
    // Number of utterances sent for transcription but not finished yet.
    private int _pendingUtterances;
    private bool _isListening;
    // True while the listening window is open (wake word heard, next sentence is a command).
    private bool _isArmed;
    // Running estimate of background noise level (RMS, 0..1). The speech threshold is set relative to it.
    private double _noiseFloor;
    // Smoothed automatic gain (volume multiplier) applied to quiet microphones.
    private double _smoothedInputGain = 1.0;

    // Converts the millisecond settings into byte sizes and builds the wake word detector and echo suppressor.
    public OpenAiCompatibleVoiceRuntime(JarvisOptions options, OpenAiCompatibleTranscriptionClient client)
    {
        _options = options;
        _client = client;
        _audioProcessing = MicrophoneProcessingProfileResolver.Resolve(options);
        _wakeWordDetector = WakeWordDetector.Create(options, _audioProcessing);
        _echoSuppressor = new VoiceAcousticEchoSuppressor(options, _audioProcessing);
        _preRollByteLimit = BytesForMilliseconds(Math.Max(0, options.SpeechRecognitionPreRollMilliseconds), options.SpeechRecognitionSampleRateHz);
        _minimumUtteranceBytes = BytesForMilliseconds(Math.Max(0, options.SpeechRecognitionMinimumUtteranceMilliseconds), options.SpeechRecognitionSampleRateHz);
        _maximumUtteranceBytes = BytesForMilliseconds(MaximumUtteranceMilliseconds, options.SpeechRecognitionSampleRateHz);
        _playbackUtteranceBytes = BytesForMilliseconds(PlaybackUtteranceMilliseconds, options.SpeechRecognitionSampleRateHz);
        _noiseFloor = _audioProcessing.AmbientNoiseFloor;

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

    // Forwarded to the echo suppressor; it needs to know when Jarvis is talking.
    public void SetPlaybackState(bool isSpeaking)
    {
        _echoSuppressor.SetPlaybackState(isSpeaking);
    }

    // Checks settings and builds the runtime; returns false with a plain-language reason when it cannot run
    // (no model set, bad URL, or a remote endpoint blocked by local-only mode).
    public static bool TryCreate(JarvisOptions options, out IVoiceRuntime? runtime, out string status)
    {
        runtime = null;

        if (string.IsNullOrWhiteSpace(options.SpeechRecognitionModel))
        {
            status = "Rolling transcription fallback is enabled, but no transcription model is configured.";
            return false;
        }

        if (!Uri.TryCreate(options.SpeechRecognitionBaseUrl, UriKind.Absolute, out _))
        {
            status = "Rolling transcription fallback base URL is invalid.";
            return false;
        }

        // Local-only mode refuses to send voice to anything but a loopback (this computer) address.
        if (options.LocalAiOnly && !LocalAiPolicy.IsLoopbackHttpEndpoint(options.SpeechRecognitionBaseUrl))
        {
            status = "Local-only AI mode blocked remote transcription. Configure a local speech endpoint.";
            return false;
        }

        runtime = new OpenAiCompatibleVoiceRuntime(
            options,
            new OpenAiCompatibleTranscriptionClient(
                options.SpeechRecognitionBaseUrl,
                options.SpeechRecognitionModel,
                options.SpeechRecognitionLanguage,
                VoiceRecognitionText.BuildTranscriptionPrompt(options), options.LocalAiOnly));

        status = $"Rolling transcription fallback ready with model {options.SpeechRecognitionModel}.";
        return true;
    }

    // Starts recording from the microphone. Failures are reported as status text rather than thrown.
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
                _lifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _capture = BuildWaveIn();
                _capture.DataAvailable += OnDataAvailable;
                _capture.RecordingStopped += OnRecordingStopped;
                _capture.StartRecording();

                _isListening = true;
                PublishSignal(0, 0, false);
                PublishStatus(
                    isArmed: false,
                    statusText: BuildIdleStatusText());
            }
            catch (Exception exception)
            {
                DisposeCapture();
                _isListening = false;
                PublishStatus(
                    isArmed: false,
                    statusText: $"Rolling microphone startup failed: {TrimStatus(exception.Message)}",
                    isAvailable: false);
            }
        }

        return Task.CompletedTask;
    }

    // Stops recording and discards any half-finished audio.
    public Task StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            if (!_isListening)
            {
                return Task.CompletedTask;
            }

            _isListening = false;
            _isArmed = false;
            ClearWakeWindow();
            ClearSpeechBuffers();
            DisposeCapture();
            _lifetimeCts?.Cancel();
            _lifetimeCts?.Dispose();
            _lifetimeCts = null;
            PublishSignal(0, 0, false);
            PublishStatus(isArmed: false, statusText: "Microphone listening paused.");
        }

        return Task.CompletedTask;
    }

    // Stops the runtime and releases the transcription lock and wake word detector.
    public void Dispose()
    {
        try
        {
            // Off the UI thread and bounded, so a capture that won't stop can't hang shutdown.
            Task.Run(() => StopAsync(CancellationToken.None)).Wait(TimeSpan.FromSeconds(5));
        }
        catch
        {
        }

        _transcriptionGate.Dispose();
        _wakeWordDetector?.Dispose();
    }

    // Called by NAudio for every small chunk of microphone audio. Per chunk: measure loudness, ask the echo suppressor if this is
    // Jarvis's own voice, apply gain, decide speech or silence, grow or finish the current utterance, and hand finished utterances
    // to a background task so this audio thread never blocks.
    private void OnDataAvailable(object? sender, WaveInEventArgs eventArgs)
    {
        if (eventArgs.BytesRecorded <= 0)
        {
            return;
        }

        // Copy the chunk; NAudio reuses its buffer for the next callback.
        var chunk = new byte[eventArgs.BytesRecorded];
        Buffer.BlockCopy(eventArgs.Buffer, 0, chunk, 0, eventArgs.BytesRecorded);

        // Measure the raw level before any gain, so the echo check sees the real input.
        byte[]? utterance = null;
        var originalLevel = AudioLevels.Rms(chunk);
        var originalPeak = AudioLevels.Peak(chunk);
        double level = 0;
        double peakLevel = 0;
        bool detectedSpeech = false;
        bool wakeWindowOpen = false;
        bool isCapturingSpeech;

        lock (_sync)
        {
            if (!_isListening)
            {
                return;
            }

            wakeWindowOpen = _isArmed;
            isCapturingSpeech = _activeSpeechBuffer is not null;
        }

        // Ask the echo suppressor whether this chunk should be ignored as Jarvis's own voice, or counts as the user interrupting.
        var echoControl = _echoSuppressor.Process(originalLevel, originalPeak, isCapturingSpeech, DateTimeOffset.UtcNow);

        if (echoControl.ShouldRequestBargeIn)
        {
            BargeInRequested?.Invoke(this, new VoiceBargeInEventArgs("speech-over-playback"));
        }

        // Echo: pretend it is silence. An utterance already in progress is still closed after the usual quiet period.
        if (echoControl.ShouldSuppress)
        {
            if (isCapturingSpeech)
            {
                lock (_sync)
                {
                    if (_activeSpeechBuffer is not null
                        && DateTimeOffset.UtcNow - _lastVoiceDetectedUtc >= TimeSpan.FromMilliseconds(_options.SpeechRecognitionSilenceDurationMilliseconds))
                    {
                        utterance = FinalizeSpeechBuffer();
                    }
                }
            }

            PublishSignal(0, 0, false, wakeWindowOpen);

            if (utterance is not null)
            {
                _ = Task.Run(() => ProcessUtteranceAsync(utterance));
            }

            return;
        }

        lock (_sync)
        {
            if (!_isListening)
            {
                return;
            }

            // Boost quiet microphones up to a target volume (modifies the chunk in place), then store the chunk in the pre-roll history.
            ApplyInputGain(chunk, originalLevel, originalPeak);

            AppendToPreRoll(chunk);

            level = AudioLevels.Rms(chunk);
            peakLevel = AudioLevels.Peak(chunk);
            detectedSpeech = IsSpeechChunk(level, peakLevel);
            wakeWindowOpen = _isArmed;

            // Speech: remember the time. When a new utterance starts, begin it with the pre-roll audio so the first word is not lost.
            if (detectedSpeech)
            {
                _lastVoiceDetectedUtc = DateTimeOffset.UtcNow;

                if (_activeSpeechBuffer is null)
                {
                    _activeSpeechBuffer = new MemoryStream();
                    foreach (var bufferedChunk in _preRollChunks)
                    {
                        _activeSpeechBuffer.Write(bufferedChunk, 0, bufferedChunk.Length);
                    }
                }

                _activeSpeechBuffer.Write(chunk, 0, chunk.Length);

                // Steady background sound never produces the silence that normally ends an utterance,
                // so send what we have and start a fresh one instead of recording without limit.
                if (_activeSpeechBuffer.Length >= (echoControl.IsPlaybackActive ? _playbackUtteranceBytes : _maximumUtteranceBytes))
                {
                    utterance = FinalizeSpeechBuffer();
                }
            }
            // Silence after speech: keep the quiet audio (natural pauses) and finish the utterance once the silence lasts long enough
            // (850 ms by default).
            else if (_activeSpeechBuffer is not null)
            {
                _activeSpeechBuffer.Write(chunk, 0, chunk.Length);

                var silenceDuration = DateTimeOffset.UtcNow - _lastVoiceDetectedUtc;

                if (silenceDuration.TotalMilliseconds >= _options.SpeechRecognitionSilenceDurationMilliseconds)
                {
                    utterance = FinalizeSpeechBuffer();
                }
            }
        }

        PublishSignal(level, peakLevel, detectedSpeech, wakeWindowOpen);

        if (utterance is not null)
        {
            _ = Task.Run(() => ProcessUtteranceAsync(utterance));
        }
    }

    // The microphone stopped by itself (e.g. device unplugged); reset everything and tell the UI.
    private void OnRecordingStopped(object? sender, StoppedEventArgs eventArgs)
    {
        lock (_sync)
        {
            if (!_isListening)
            {
                return;
            }

            _isListening = false;
            _isArmed = false;
            ClearWakeWindow();
            ClearSpeechBuffers();
            DisposeCapture();
            _lifetimeCts?.Dispose();
            _lifetimeCts = null;
        }

        var statusText = eventArgs.Exception is null
            ? "Rolling speech recognition stopped."
            : $"Rolling speech recognition stopped: {TrimStatus(eventArgs.Exception.Message)}";

        PublishSignal(0, 0, false);
        PublishStatus(isArmed: false, statusText: statusText, isAvailable: eventArgs.Exception is null);
    }

    // Runs one utterance's transcription on a background thread and always decrements the pending counter afterward.
    private async Task ProcessUtteranceAsync(byte[] utteranceBytes)
    {
        try
        {
            await TranscribeUtteranceAsync(utteranceBytes);
        }
        finally
        {
            Interlocked.Decrement(ref _pendingUtterances);
        }
    }

    // Waits for its turn at the transcription lock, sends the audio to whisper, and passes the text on. Errors become status text.
    private async Task TranscribeUtteranceAsync(byte[] utteranceBytes)
    {
        var lifetimeToken = _lifetimeCts?.Token ?? CancellationToken.None;

        try
        {
            await _transcriptionGate.WaitAsync(lifetimeToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            var transcript = await _client.TranscribeAsync(utteranceBytes, _options.SpeechRecognitionSampleRateHz, lifetimeToken);
            HandleRecognizedText(transcript.Text.Trim(), transcript.Confidence, utteranceBytes);
        }
        catch (OperationCanceledException)
        {
        }
        catch (HttpRequestException) when (IsLocalSpeechEndpoint())
        {
            // A refused connection to localhost means the bundled speech server is not running; the message says where to look.
            PublishStatus(
                isArmed: _isArmed,
                statusText: "The local speech server is not running, so Jarvis cannot turn your voice into text. "
                    + "See %LOCALAPPDATA%\\Jarvis\\data\\whisper-server.log, or run scripts\\Setup-LocalSpeech.ps1.");
        }
        catch (Exception exception)
        {
            PublishStatus(
                isArmed: _isArmed,
                statusText: $"Rolling transcription error: {TrimStatus(exception.Message)}");
        }
        finally
        {
            _transcriptionGate.Release();
        }
    }

    // True when the transcription URL points at this computer.
    private bool IsLocalSpeechEndpoint() =>
        Uri.TryCreate(_options.SpeechRecognitionBaseUrl, UriKind.Absolute, out var uri) && uri.IsLoopback;

    // Decides what a transcript means: a command with the wake name inline, a wake word alone (open the window), the wake
    // engine hearing the name that whisper misheard, or a plain sentence that counts only while the window is open.
    private void HandleRecognizedText(string recognizedText, double? recognitionConfidence, byte[]? utteranceBytes = null)
    {
        // Clean the transcript; keep the raw version separate from one with the user's personal corrections applied.
        var rawRecognizedText = VoiceRecognitionText.NormalizeTranscript(recognizedText, _options, applyPersonalCorrections: false);
        recognizedText = VoiceRecognitionText.ApplyPersonalCorrections(rawRecognizedText, _options);

        // Wake word turned off: every non-empty transcript is a command.
        if (!_options.WakeWordEnabled)
        {
            if (string.IsNullOrWhiteSpace(recognizedText))
            {
                return;
            }

            PublishRecognizedCommand(
                JarvisCommandCatalog.NormalizeVoiceDirective(recognizedText),
                rawRecognizedText,
                recognitionConfidence);
            return;
        }

        // "Jarvis open notepad" runs "open notepad"; "Jarvis" alone just opens the listening window.
        if (!string.IsNullOrWhiteSpace(recognizedText)
            && VoiceRecognitionText.TryExtractWakeCommand(recognizedText, _options, out var inlineCommand))
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
                recognitionConfidence);
            return;
        }

        // No name in the transcript. The wake engine (openWakeWord) may still hear the name whisper misheard;
        // it only opens the follow-up window and never runs this text.
        if (utteranceBytes is not null && !_isArmed && _wakeWordDetector is not null
            && _wakeWordDetector.Detect(utteranceBytes, _options.SpeechRecognitionSampleRateHz).IsDetected)
        {
            ArmWakeWindow();
            return;
        }

        // No name in the transcript: only run it if a wake window is already open
        // (name said alone, hotkey, or a follow-up after Jarvis asked something).
        if (string.IsNullOrWhiteSpace(recognizedText))
        {
            return;
        }

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
            recognitionConfidence);
    }

    // Opens the listening window without a wake word, e.g. after Jarvis asked a question.
    public void ListenForFollowUp()
    {
        if (_isListening)
        {
            ArmWakeWindow();
        }
    }

    // Opens the listening window and starts a timer to close it (VoiceCommandTimeoutSeconds, at least 2 seconds).
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

    // Timer callback. Does not close the window while speech is still being recorded or transcribed, so a slow transcription does not lose the command.
    private void OnWakeWindowExpired()
    {
        lock (_sync)
        {
            if (!_isArmed)
            {
                return;
            }

            // The command may already be spoken and waiting on transcription, which takes seconds on a
            // CPU. Closing the window now would drop it, so wait until that speech has been handled.
            if (_activeSpeechBuffer is not null || Volatile.Read(ref _pendingUtterances) > 0)
            {
                _wakeWindowTimer?.Change(TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
                return;
            }

            _isArmed = false;
            ClearWakeWindow();
        }

        PublishStatus(isArmed: false, statusText: BuildIdleStatusText());
    }

    // Closes the listening window and goes back to waiting for the wake word.
    private void ResetToIdle()
    {
        lock (_sync)
        {
            _isArmed = false;
            ClearWakeWindow();
        }

        PublishStatus(isArmed: false, statusText: BuildIdleStatusText());
    }

    // Builds and broadcasts the current status, and re-sends the signal so the UI sees the listening-window flag.
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

    // Plain-language status line describing what is being listened for and with which microphone/tuning.
    private string BuildIdleStatusText()
    {
        var providerLabel = $"rolling transcription fallback ({_options.SpeechRecognitionModel})";
        var deviceLabel = string.IsNullOrWhiteSpace(_options.SpeechRecognitionDeviceName)
            ? "the default microphone"
            : $"microphone \"{_options.SpeechRecognitionDeviceName}\"";
        var modeLabel = _options.SpeechRecognitionMode?.Trim() ?? "half-duplex";
        var tuningLabel = _audioProcessing.BuildStatusLabel();

        return _options.WakeWordEnabled
            ? _wakeWordDetector is null
                ? $"Listening for \"{_options.WakePhrase}\" with {providerLabel} on {deviceLabel} ({modeLabel}, {tuningLabel})."
                : $"Listening for \"{_options.WakePhrase}\" with {_wakeWordDetector.DescriptionLabel} and {providerLabel} on {deviceLabel} ({modeLabel}, {tuningLabel})."
            : $"Listening for voice directives with {providerLabel} on {deviceLabel} ({modeLabel}, {tuningLabel}).";
    }

    // Decides whether this chunk is speech. Two thresholds with hysteresis (hard to start, easier to continue, so natural
    // dips between words do not end the sentence). Both follow the measured noise floor: about 2.65x the floor to start
    // speaking, about 1.95x to keep going, clamped around the configured voice-activity threshold. The level used is the
    // larger of RMS and 42% of the peak, so short sharp sounds count. Quiet chunks update the noise floor estimate.
    private bool IsSpeechChunk(double level, double peakLevel)
    {
        var configuredThreshold = Math.Max(0.0035, _audioProcessing.VoiceActivityThreshold);
        var effectiveLevel = Math.Max(level, peakLevel * 0.42);
        var isContinuingSpeech = _activeSpeechBuffer is not null;
        var adaptiveThreshold = isContinuingSpeech
            ? Math.Clamp(Math.Max(configuredThreshold * 0.34, _noiseFloor * 1.95), 0.0026, configuredThreshold * 1.8)
            : Math.Clamp(Math.Max(configuredThreshold * 0.46, _noiseFloor * 2.65), 0.0030, configuredThreshold * 2.2);

        if (effectiveLevel < adaptiveThreshold)
        {
            UpdateNoiseFloor(level);
            return false;
        }

        // Starting speech nudges the noise floor down slightly (x0.94, minimum 0.0022) so one loud burst cannot raise it for good.
        if (!isContinuingSpeech)
        {
            _noiseFloor = Math.Max(0.0022, _noiseFloor * 0.94);
        }

        return true;
    }

    // Slow moving average of background noise: 92% old value, 8% new level (the level is limited to 0.0005..0.10 so a clap or dead silence does not skew it).
    private void UpdateNoiseFloor(double level)
    {
        var sample = Math.Clamp(level, 0.0005, 0.10);
        _noiseFloor = (_noiseFloor * 0.92) + (sample * 0.08);
    }

    // Keeps the last few hundred milliseconds of audio in a queue, dropping the oldest chunks past the size limit.
    private void AppendToPreRoll(byte[] chunk)
    {
        if (_preRollByteLimit <= 0)
        {
            return;
        }

        _preRollChunks.Enqueue(chunk);
        _preRollBytes += chunk.Length;

        while (_preRollBytes > _preRollByteLimit && _preRollChunks.Count > 0)
        {
            _preRollBytes -= _preRollChunks.Dequeue().Length;
        }
    }

    // Closes the current utterance and returns its bytes, or null if it is shorter than the minimum (a click or cough).
    private byte[]? FinalizeSpeechBuffer()
    {
        if (_activeSpeechBuffer is null)
        {
            return null;
        }

        var utterance = _activeSpeechBuffer.ToArray();
        _activeSpeechBuffer.Dispose();
        _activeSpeechBuffer = null;

        if (utterance.Length < _minimumUtteranceBytes)
        {
            return null;
        }

        // Every returned utterance is handed to ProcessUtteranceAsync, which decrements this.
        Interlocked.Increment(ref _pendingUtterances);
        return utterance;
    }

    // Stops and forgets the listening-window timer.
    private void ClearWakeWindow()
    {
        _wakeWindowTimer?.Dispose();
        _wakeWindowTimer = null;
    }

    // Throws away the current utterance and pre-roll audio.
    private void ClearSpeechBuffers()
    {
        _activeSpeechBuffer?.Dispose();
        _activeSpeechBuffer = null;
        _preRollChunks.Clear();
        _preRollBytes = 0;
    }

    // Opens the microphone for 16-bit mono recording at the configured sample rate; uses the configured device if found.
    private WaveInEvent BuildWaveIn()
    {
        var waveIn = new WaveInEvent
        {
            BufferMilliseconds = _audioProcessing.RollingCaptureBufferMilliseconds,
            WaveFormat = new WaveFormat(_options.SpeechRecognitionSampleRateHz, 16, 1)
        };

        var deviceNumber = AudioDeviceResolver.GetPreferredInputDeviceIndex(_options.SpeechRecognitionDeviceName);

        if (deviceNumber >= 0)
        {
            waveIn.DeviceNumber = deviceNumber;
        }

        return waveIn;
    }

    // Automatic gain: scales samples so the peak reaches TargetPeakLevel, never above InputGainCap and never below x1.
    // Near-silence gets no gain, so room hiss is not boosted. The gain is smoothed (76% old, 24% new) to avoid sudden jumps.
    private void ApplyInputGain(byte[] pcm16MonoChunk, double rmsLevel, double peakLevel)
    {
        if (pcm16MonoChunk.Length < 2)
        {
            return;
        }

        double targetGain;

        if (peakLevel < 0.004 && rmsLevel < 0.0008)
        {
            targetGain = 1.0;
        }
        else
        {
            targetGain = Math.Clamp(_audioProcessing.TargetPeakLevel / Math.Max(peakLevel, 0.025), 1.0, _audioProcessing.InputGainCap);
        }

        _smoothedInputGain = (_smoothedInputGain * 0.76) + (targetGain * 0.24);

        if (_smoothedInputGain <= 1.02)
        {
            return;
        }

        // Multiply each 16-bit sample and clamp so it cannot wrap around (distortion).
        for (var index = 0; index < pcm16MonoChunk.Length - 1; index += 2)
        {
            var sample = BitConverter.ToInt16(pcm16MonoChunk, index);
            var amplified = (int)Math.Round(sample * _smoothedInputGain);
            amplified = Math.Clamp(amplified, short.MinValue, short.MaxValue);

            var bytes = BitConverter.GetBytes((short)amplified);
            pcm16MonoChunk[index] = bytes[0];
            pcm16MonoChunk[index + 1] = bytes[1];
        }
    }

    // Stops and releases the microphone recording.
    private void DisposeCapture()
    {
        if (_capture is null)
        {
            return;
        }

        try
        {
            _capture.DataAvailable -= OnDataAvailable;
            _capture.RecordingStopped -= OnRecordingStopped;
            _capture.StopRecording();
        }
        catch
        {
        }
        finally
        {
            _capture.Dispose();
            _capture = null;
        }
    }

    // Broadcasts a new level reading to the UI meter, limited to 0..1.
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

    // Milliseconds to byte count: samples = rate x seconds, and each 16-bit sample is 2 bytes.
    private static int BytesForMilliseconds(int milliseconds, int sampleRateHz)
    {
        var samples = (int)Math.Ceiling(sampleRateHz * (milliseconds / 1000d));
        return Math.Max(0, samples * 2);
    }

    // Shortens long error text for the status bar.
    private static string TrimStatus(string message)
    {
        const int maxLength = 220;
        var value = message.Trim();
        return value.Length <= maxLength ? value : value[..maxLength] + "...";
    }

    // Whisper scores short commands low even when right (measured on small.en: correct 0.52-0.80, misheard
    // 0.42-0.55), so the shared 0.70 cutoff would ask on most commands. Text is never dropped here, only asked about.
    internal const double ClarifyBelowConfidence = 0.50;

    // Raises CommandRecognized; confidence under ClarifyBelowConfidence makes the app ask the user to confirm first.
    private void PublishRecognizedCommand(string text, string rawText, double? confidence)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var requiresClarification = confidence < ClarifyBelowConfidence;
        var prompt = requiresClarification
            ? VoiceRecognitionConfidence.BuildClarificationPrompt(rawText, confidence)
            : string.Empty;

        CommandRecognized?.Invoke(
            this,
            new VoiceCommandRecognizedEventArgs(text, confidence, requiresClarification, prompt, rawText));
    }
}

// Sends audio to a Whisper-style /audio/transcriptions endpoint and parses the JSON reply.
internal sealed class OpenAiCompatibleTranscriptionClient
{
    private readonly HttpClient _httpClient;
    private readonly string _endpoint;
    private readonly string _model;
    private readonly string _language;
    private readonly string _prompt;

    public OpenAiCompatibleTranscriptionClient(
        string baseUrl,
        string model,
        string language,
        string prompt,
        bool localAiOnly = true)
    {
        _endpoint = BuildEndpoint(baseUrl);
        _model = model;
        _language = language;
        _prompt = prompt;
        _httpClient = LocalAiPolicy.CreateHttpClient(localAiOnly, TimeSpan.FromSeconds(45));
    }

    // Plain text plus confidence for one utterance.
    public async Task<VoiceTranscriptResult> TranscribeAsync(byte[] pcm16MonoAudio, int sampleRateHz, CancellationToken cancellationToken)
    {
        var body = await SendAsync(pcm16MonoAudio, sampleRateHz, "verbose_json", cancellationToken);
        return ParseTranscript(body);
    }

    /// <summary>Text plus a confidence from the segments' avg_logprob; confidence stays null when a server omits it.</summary>
    internal static VoiceTranscriptResult ParseTranscript(string verboseJson)
    {
        using var json = JsonDocument.Parse(verboseJson);
        var text = json.RootElement.GetProperty("text").GetString()?.Trim() ?? string.Empty;
        var logProbabilities = json.RootElement.TryGetProperty("segments", out var segments)
            ? segments.EnumerateArray()
                .Where(segment => segment.TryGetProperty("avg_logprob", out var value) && value.ValueKind == JsonValueKind.Number)
                .Select(segment => segment.GetProperty("avg_logprob").GetDouble())
                .ToArray()
            : Array.Empty<double>();

        return new VoiceTranscriptResult(text, VoiceRecognitionConfidence.FromLogProbabilities(logProbabilities));
    }

    // Returns every segment with its start time; used for longer recordings such as lectures.
    /// <summary>The transcript as timed lines, each at its offset into the audio.</summary>
    public async Task<IReadOnlyList<TranscriptLine>> TranscribeLinesAsync(byte[] pcm16MonoAudio, int sampleRateHz, CancellationToken cancellationToken)
    {
        using var json = JsonDocument.Parse(await SendAsync(pcm16MonoAudio, sampleRateHz, "verbose_json", cancellationToken));

        return json.RootElement.GetProperty("segments").EnumerateArray()
            .Select(segment => new TranscriptLine(
                TimeSpan.FromSeconds(segment.GetProperty("start").GetDouble()),
                segment.GetProperty("text").GetString()?.Trim() ?? string.Empty))
            .Where(line => line.Text.Length > 0)
            .ToList();
    }

    // Builds the multipart form (WAV file, model, language, optional vocabulary prompt) and POSTs it; non-200 replies become errors.
    private async Task<string> SendAsync(byte[] pcm16MonoAudio, int sampleRateHz, string responseFormat, CancellationToken cancellationToken)
    {
        using var form = new MultipartFormDataContent();

        // The server expects a WAV file, not raw samples: wrap the audio as 16-bit mono PCM. The writer closes the stream it wraps.
        var wavStream = new MemoryStream();
        using (var wavWriter = new WaveFileWriter(wavStream, new WaveFormat(sampleRateHz, 16, 1)))
        {
            wavWriter.Write(pcm16MonoAudio, 0, pcm16MonoAudio.Length);
        }

        var audioContent = new ByteArrayContent(wavStream.ToArray());
        audioContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");

        form.Add(audioContent, "file", "speech.wav");
        form.Add(new StringContent(_model), "model");
        form.Add(new StringContent(responseFormat), "response_format");

        if (!string.IsNullOrWhiteSpace(_language))
        {
            form.Add(new StringContent(_language), "language");
        }

        if (!string.IsNullOrWhiteSpace(_prompt))
        {
            form.Add(new StringContent(_prompt), "prompt");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = form
        };

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"speech API HTTP {(int)response.StatusCode}: {TrimForError(body)}");
        }

        return body;
    }

    // Accepts either a base URL or a full endpoint URL.
    private static string BuildEndpoint(string baseUrl)
    {
        var trimmed = baseUrl.Trim().TrimEnd('/');

        if (trimmed.EndsWith("/audio/transcriptions", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        return $"{trimmed}/audio/transcriptions";
    }

    // Shortens error bodies to 500 characters.
    private static string TrimForError(string text)
    {
        const int maxLength = 500;
        var value = text.Trim();
        return value.Length <= maxLength ? value : value[..maxLength] + "...";
    }
}

// Loudness of a chunk of 16-bit mono audio, shared by the voice pipeline, the microphone gain stage and the calibration tool.
internal static class AudioLevels
{
    // RMS (root mean square): square every sample, average, take the square root. It is the "average loudness" of the chunk,
    // from 0 (silence) to 1 (full scale). 0.008 is about -42 dB relative to full scale.
    public static double Rms(byte[] pcm16MonoChunk)
    {
        if (pcm16MonoChunk.Length < 2)
        {
            return 0;
        }

        double sumSquares = 0;
        var sampleCount = pcm16MonoChunk.Length / 2;

        for (var index = 0; index < pcm16MonoChunk.Length - 1; index += 2)
        {
            var sample = BitConverter.ToInt16(pcm16MonoChunk, index);
            var normalized = sample / 32768d;
            sumSquares += normalized * normalized;
        }

        return Math.Sqrt(sumSquares / sampleCount);
    }

    // Peak: the single loudest sample in the chunk, 0..1.
    public static double Peak(byte[] pcm16MonoChunk)
    {
        if (pcm16MonoChunk.Length < 2)
        {
            return 0;
        }

        double peak = 0;

        for (var index = 0; index < pcm16MonoChunk.Length - 1; index += 2)
        {
            var sample = BitConverter.ToInt16(pcm16MonoChunk, index);
            // Widen to int first: Math.Abs(short.MinValue) throws OverflowException on clipped audio.
            var normalized = Math.Abs((int)sample) / 32768d;

            if (normalized > peak)
            {
                peak = normalized;
            }
        }

        return peak;
    }
}
