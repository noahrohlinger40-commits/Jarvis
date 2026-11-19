// VoiceAcousticEchoSuppressor.cs: stops Jarvis from hearing its own voice. While Jarvis is speaking, its sound comes back
// into the microphone through the speakers ("acoustic echo"). Called once per microphone chunk by OpenAiCompatibleVoiceRuntime.
// It learns how loud the echo normally is and only lets through sound clearly louder than that (the user talking over Jarvis).
// All levels are 0..1 loudness values (see OpenAiCompatibleVoiceRuntime.CalculateRootMeanSquareLevel); thresholds come from the tuning profile.
using Jarvis.Core;

namespace Jarvis.App;

// Verdict for one chunk: is Jarvis talking, does it look like echo, should the chunk be ignored, and should Jarvis be
// told to stop talking because the user is interrupting ("barge-in").
internal readonly record struct VoiceEchoControlResult(
    bool IsPlaybackActive,
    bool IsLikelyEcho,
    bool ShouldSuppress,
    bool ShouldRequestBargeIn);

// Keeps two running estimates of the sound level: the room's background noise and the level of Jarvis's own echo.
internal sealed class VoiceAcousticEchoSuppressor
{
    // Protects the fields below: audio and speaker events come from different threads.
    private readonly object _sync = new();
    private readonly JarvisOptions _options;
    private readonly ResolvedMicrophoneProcessingProfile _profile;
    // Last barge-in request; requests are spaced at least 1.2 s apart.
    private DateTimeOffset _lastBargeInUtc = DateTimeOffset.MinValue;
    // When Jarvis started speaking, so the first moments can be handled separately.
    private DateTimeOffset _playbackStartedUtc = DateTimeOffset.MinValue;
    // Running estimate of the quiet room level, only updated while Jarvis is silent.
    private double _ambientNoiseFloor;
    // Running estimate of how loud Jarvis's echo is at the microphone, learned while Jarvis speaks.
    private double _playbackEchoFloor;
    private bool _isPlaybackActive;

    // Seeds both floors from the tuning profile. The echo floor starts higher (0.012, or 0.008 if the sound card already cancels
    // echo) because speaker sound is louder than room noise.
    public VoiceAcousticEchoSuppressor(JarvisOptions options, ResolvedMicrophoneProcessingProfile profile)
    {
        _options = options;
        _profile = profile;
        _ambientNoiseFloor = Math.Max(
            profile.AmbientNoiseFloor * profile.AmbientFloorScale,
            profile.VoiceActivityThreshold * (profile.HardwareNoiseSuppressionEnabled ? 0.42 : 0.50) * profile.AmbientFloorScale);
        _playbackEchoFloor = Math.Max(
            (profile.HardwareEchoCancellationEnabled ? 0.008 : 0.012) * profile.PlaybackEchoFloorScale,
            profile.VoiceActivityThreshold * 1.50 * profile.EchoGateScale * profile.PlaybackEchoFloorScale);
    }

    // True while Jarvis is speaking.
    public bool IsPlaybackActive
    {
        get
        {
            lock (_sync)
            {
                return _isPlaybackActive;
            }
        }
    }

    // Called when Jarvis starts or stops speaking. When it stops, the echo floor decays to 75% so later playback re-learns it.
    public void SetPlaybackState(bool isSpeaking)
    {
        lock (_sync)
        {
            if (_isPlaybackActive == isSpeaking)
            {
                return;
            }

            _isPlaybackActive = isSpeaking;
            _playbackStartedUtc = isSpeaking ? DateTimeOffset.UtcNow : DateTimeOffset.MinValue;

            if (!isSpeaking)
            {
                _playbackEchoFloor = Math.Max(_playbackEchoFloor * 0.75, _ambientNoiseFloor * 1.50);
            }
        }
    }

    // Judges one chunk of microphone audio. While Jarvis is silent everything passes (and the room noise estimate is updated).
    // While Jarvis speaks: the first 350 ms are always treated as echo (playback just began and the level is not yet known);
    // afterwards a chunk is "user speech" only when it is louder than a threshold built from the learned echo floor.
    public VoiceEchoControlResult Process(
        double rmsLevel,
        double peakLevel,
        bool isCapturingSpeech,
        DateTimeOffset timestampUtc)
    {
        // Judge loudness as the larger of RMS and 42% of the peak, so brief sharp speech sounds still count.
        var effectiveLevel = Math.Max(rmsLevel, peakLevel * 0.42);

        lock (_sync)
        {
            if (!_isPlaybackActive)
            {
                UpdateAmbientFloor(effectiveLevel);
                return new VoiceEchoControlResult(false, false, false, false);
            }

            var playbackAge = timestampUtc - _playbackStartedUtc;

            if (playbackAge.TotalMilliseconds < 350)
            {
                UpdatePlaybackEchoFloor(effectiveLevel);
                return new VoiceEchoControlResult(
                    true,
                    true,
                    _options.SpeechRecognitionEchoCancellationEnabled,
                    false);
            }

            // The learned echo level, but never lower than 2.2x the room noise.
            var floor = Math.Max(_playbackEchoFloor, _ambientNoiseFloor * 2.20);
            // Threshold to START counting as the user interrupting: a multiple of the voice-activity threshold or of the echo floor,
            // whichever is larger (multipliers are smaller if the hardware already cancels echo), kept between 0.045 and 0.18.
            var bargeInThreshold = Math.Clamp(
                Math.Max(
                    _profile.VoiceActivityThreshold * (_profile.HardwareEchoCancellationEnabled ? 6.1 : 7.0) * _profile.EchoGateScale,
                    floor * (_profile.HardwareEchoCancellationEnabled ? 2.50 : 2.85)) * _profile.BargeInThresholdScale,
                0.045,
                0.18);
            // Lower threshold once the user is already mid-sentence: they only need to stay above 0.030..0.14 so natural dips do not cut them off.
            var continuingSpeechThreshold = Math.Clamp(
                Math.Max(
                    _profile.VoiceActivityThreshold * (_profile.HardwareEchoCancellationEnabled ? 3.4 : 4.0) * _profile.EchoGateScale,
                    floor * (_profile.HardwareEchoCancellationEnabled ? 1.62 : 1.90)) * _profile.ContinuingSpeechThresholdScale,
                0.030,
                0.14);
            var speechThreshold = isCapturingSpeech ? continuingSpeechThreshold : bargeInThreshold;
            var likelyUserSpeech = effectiveLevel >= speechThreshold && peakLevel >= _profile.MinimumSpeechPeakLevel;

            // Not speech means this chunk is probably echo, so use it to keep the echo estimate current.
            if (!likelyUserSpeech)
            {
                UpdatePlaybackEchoFloor(effectiveLevel);
            }

            // Barge-in request: enabled in settings, the user is really speaking, and at least 1.2 s since the last one.
            var shouldRequestBargeIn = _options.SpeechRecognitionBargeInEnabled
                && likelyUserSpeech
                && timestampUtc - _lastBargeInUtc > TimeSpan.FromMilliseconds(1200);

            if (shouldRequestBargeIn)
            {
                _lastBargeInUtc = timestampUtc;
            }

            // Suppress (treat as silence) only when echo cancellation is on, the chunk is not user speech and is not essentially silent (>0.001).
            var shouldSuppress = _options.SpeechRecognitionEchoCancellationEnabled
                && !likelyUserSpeech
                && effectiveLevel > 0.001;

            return new VoiceEchoControlResult(
                IsPlaybackActive: true,
                IsLikelyEcho: !likelyUserSpeech,
                ShouldSuppress: shouldSuppress,
                ShouldRequestBargeIn: shouldRequestBargeIn);
        }
    }

    // Slow moving average (94% old, 6% new) of quiet-time level; ignores silence and very loud chunks (>0.08), clamped to 0.0015..0.08.
    private void UpdateAmbientFloor(double effectiveLevel)
    {
        if (effectiveLevel <= 0 || effectiveLevel > 0.08)
        {
            return;
        }

        var scaledLevel = effectiveLevel * _profile.AmbientFloorScale;
        _ambientNoiseFloor = (_ambientNoiseFloor * 0.94) + (scaledLevel * 0.06);
        _ambientNoiseFloor = Math.Clamp(_ambientNoiseFloor, 0.0015, 0.08);
    }

    // Faster moving average (82% old, 18% new) of the echo level while Jarvis speaks, clamped to 0.006..0.20.
    private void UpdatePlaybackEchoFloor(double effectiveLevel)
    {
        if (effectiveLevel <= 0)
        {
            return;
        }

        var sample = Math.Clamp(effectiveLevel * _profile.PlaybackEchoFloorScale, 0.002, 0.20);
        _playbackEchoFloor = (_playbackEchoFloor * 0.82) + (sample * 0.18);
        _playbackEchoFloor = Math.Clamp(_playbackEchoFloor, 0.006, 0.20);
    }
}
