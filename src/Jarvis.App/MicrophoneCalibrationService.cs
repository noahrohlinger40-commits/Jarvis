// MicrophoneCalibrationService.cs: measures how loud the room and Noah's voice are on the chosen microphone, and turns the
// numbers into recommended settings (speech threshold, gain limit, target volume, wake-word readiness, half or full duplex).
// Used by the microphone setup / validation flow in the settings UI, not by the live listening loop.
// It records short samples (about 40 ms chunks), measures each chunk's loudness (RMS and peak) and summarises them.
// All levels are 0..1 where 1 is the loudest the 16-bit audio can be.
using Jarvis.Core;
using NAudio.Wave;

namespace Jarvis.App;

// Summary of one recording: average RMS, the 90th-percentile RMS (louder than 90% of chunks), the 95th-percentile peak, the
// loudest peak, and the number of chunks. Percentiles ignore a single odd click or cough.
internal sealed record AudioLevelSampleSnapshot(
    double AverageRms,
    double Rms90,
    double Peak95,
    double MaxPeak,
    int ChunkCount);

// Records short microphone samples and computes recommended audio settings from them.
internal sealed class MicrophoneCalibrationService
{
    // Records the microphone for the given time and summarises it (use it for "stay quiet" and "say something" samples).
    public Task<AudioLevelSampleSnapshot> CaptureSampleAsync(
        JarvisOptions options,
        int durationMilliseconds,
        CancellationToken cancellationToken)
    {
        return CaptureSampleCoreAsync(options, durationMilliseconds, stimulus: null, cancellationToken);
    }

    // Records while a test tone plays through the speakers, to measure how much of Jarvis's own sound leaks back into the microphone.
    public Task<AudioLevelSampleSnapshot> CapturePlaybackLeakageAsync(
        JarvisOptions options,
        int durationMilliseconds,
        CancellationToken cancellationToken)
    {
        return CaptureSampleCoreAsync(
            options,
            durationMilliseconds,
            token => PlayValidationToneAsync(durationMilliseconds, token),
            cancellationToken);
    }

    // Why a calibration must not be saved, or null when it looks usable: the speech sample is near silent (the clamp floor in
    // CreateCalibrationProfile is 0.8%), or the voice is not clearly louder (2x) than the room.
    public static string? FindCalibrationProblem(AudioLevelSampleSnapshot ambient, AudioLevelSampleSnapshot speech)
    {
        var ambientFloor = Math.Max(ambient.AverageRms, ambient.Rms90);
        var speechRms = Math.Max(speech.AverageRms, speech.Rms90 * 0.90);

        if (speechRms < 0.008)
        {
            return $"I could barely hear you (speech {speechRms:P1}). Check the microphone is not muted and speak up.";
        }

        return speechRms < ambientFloor * 2.0
            ? $"The room ({ambientFloor:P1}) was nearly as loud as your voice ({speechRms:P1}). Find a quieter spot."
            : null;
    }

    // Turns an ambient (quiet room) sample and a speech sample into a profile. Key numbers: the speech threshold sits between
    // the room level and the speech level (at least 2.6x the room noise, within 0.003 - 0.040); the target peak is a bit lower for
    // loud speakers and higher for quiet ones (0.28 - 0.44); the maximum gain is target / speech peak, but never under the uncalibrated 4x (4x - 4.8x).
    public SpeechRecognitionCalibrationProfile CreateCalibrationProfile(
        JarvisOptions options,
        AudioLevelSampleSnapshot ambient,
        AudioLevelSampleSnapshot speech,
        string beamformingProfileName,
        string hardwareDspProfileName)
    {
        // Room level uses the louder of average and 90th-percentile; speech uses slightly discounted values (x0.90 / x0.92) so brief spikes do not dominate.
        var ambientFloor = Math.Max(ambient.AverageRms, ambient.Rms90);
        var speechRms = Math.Max(speech.AverageRms, speech.Rms90 * 0.90);
        var speechPeak = Math.Max(speech.Peak95, speech.MaxPeak * 0.92);
        // Gap between speech and room level, at least 0.010 so a silent test does not give zero.
        var speechSeparation = Math.Max(0.010, speechRms - ambientFloor);
        var threshold = Math.Clamp(
            Math.Max(ambientFloor * 2.60, Math.Min(speechRms * 0.30, ambientFloor + (speechSeparation * 0.32))),
            0.0030,
            0.040);
        // Louder speakers get a lower target volume; quieter ones a higher target.
        var targetPeak = Math.Clamp(
            speechPeak switch
            {
                < 0.12 => 0.42,
                < 0.18 => 0.38,
                > 0.34 => 0.30,
                _ => 0.35
            },
            0.28,
            0.44);
        // The cap is only a ceiling for the automatic gain, which already boosts nothing that is loud enough. Never go below the
        // uncalibrated 4x: the calibration sentence is often louder than everyday talk, and on 2026-11-19 target / speech peak cut
        // the boost to 1.46x, so soft speech reached the speech server too quiet to be heard.
        var gainCap = Math.Clamp(targetPeak / Math.Max(speechPeak, 0.08), 4.00, 4.80);

        return new SpeechRecognitionCalibrationProfile
        {
            DeviceName = options.SpeechRecognitionDeviceName?.Trim() ?? string.Empty,
            CapturedUtc = DateTimeOffset.UtcNow,
            AmbientRmsLevel = Math.Clamp(ambientFloor, 0.0015, 0.10),
            SpeechRmsLevel = Math.Clamp(speechRms, 0.008, 0.40),
            SpeechPeakLevel = Math.Clamp(speechPeak, 0.030, 1.0),
            RecommendedVoiceActivityThreshold = threshold,
            RecommendedInputGainCap = gainCap,
            RecommendedTargetPeakLevel = targetPeak,
            PreferredBeamformingProfile = string.IsNullOrWhiteSpace(beamformingProfileName) ? "auto" : beamformingProfileName.Trim(),
            HardwareDspProfileName = string.IsNullOrWhiteSpace(hardwareDspProfileName) ? "auto" : hardwareDspProfileName.Trim(),
            Notes = $"ambient {ambientFloor:P1}, speech {speechRms:P1}, peak {speechPeak:P1}"
        };
    }

    // Scores how ready the microphone is. Wake margin = wake phrase loudness / room loudness; speech margin likewise; duplex
    // margin = speech loudness / loudness of Jarvis's own leaked sound. The scores scale those ratios onto 0..1. Full duplex (listening
    // while Jarvis speaks) is recommended only if the duplex score is at least 0.58 and speech is at least 3x the room noise.
    public SpeechRecognitionValidationProfile CreateValidationProfile(
        JarvisOptions options,
        AudioLevelSampleSnapshot ambient,
        AudioLevelSampleSnapshot wakePhrase,
        AudioLevelSampleSnapshot speech,
        AudioLevelSampleSnapshot playbackLeakage,
        string wakeWordAssetName,
        string wakeWordAssetPath,
        string wakeEngineName,
        string wakeEnginePath,
        string beamformingProfileName,
        string hardwareDspProfileName)
    {
        var ambientFloor = Math.Max(ambient.AverageRms, ambient.Rms90);
        var wakeRms = Math.Max(wakePhrase.AverageRms, wakePhrase.Rms90 * 0.95);
        var speechRms = Math.Max(speech.AverageRms, speech.Rms90 * 0.90);
        var leakageRms = Math.Max(playbackLeakage.AverageRms, playbackLeakage.Rms90);
        // Ratios are measured against the room level but never below 0.0015, to avoid dividing by almost zero.
        var wakeMargin = wakeRms / Math.Max(ambientFloor, 0.0015);
        var speechMargin = speechRms / Math.Max(ambientFloor, 0.0015);
        var duplexMargin = speechRms / Math.Max(leakageRms, 0.0015);
        var wakeScore = Math.Clamp((wakeMargin - 1.50) / 4.50, 0.0, 1.0);
        var fullDuplexScore = Math.Clamp((duplexMargin - 1.10) / 3.20, 0.0, 1.0);
        var recommendedMode = fullDuplexScore >= 0.58 && speechMargin >= 3.0
            ? "full-duplex"
            : "half-duplex";

        return new SpeechRecognitionValidationProfile
        {
            DeviceName = options.SpeechRecognitionDeviceName?.Trim() ?? string.Empty,
            ValidatedUtc = DateTimeOffset.UtcNow,
            WakeWordAssetName = string.IsNullOrWhiteSpace(wakeWordAssetName) ? "Recognizer Default" : wakeWordAssetName.Trim(),
            WakeWordAssetPath = WakeWordAssetCatalog.NormalizeStoredPath(wakeWordAssetPath),
            WakeEngineName = string.IsNullOrWhiteSpace(wakeEngineName) ? "Recognizer Wake" : wakeEngineName.Trim(),
            WakeEnginePath = WakeWordEngineCatalog.NormalizeStoredPath(wakeEnginePath),
            BeamformingProfileName = string.IsNullOrWhiteSpace(beamformingProfileName) ? "auto" : beamformingProfileName.Trim(),
            HardwareDspProfileName = string.IsNullOrWhiteSpace(hardwareDspProfileName) ? "auto" : hardwareDspProfileName.Trim(),
            AmbientRmsLevel = Math.Clamp(ambientFloor, 0.0015, 0.10),
            WakePhraseRmsLevel = Math.Clamp(wakeRms, 0.0030, 0.50),
            CommandSpeechRmsLevel = Math.Clamp(speechRms, 0.0060, 0.50),
            PlaybackLeakageRmsLevel = Math.Clamp(leakageRms, 0.0010, 0.30),
            WakeWordReadinessScore = wakeScore,
            FullDuplexReadinessScore = fullDuplexScore,
            RecommendedConversationMode = recommendedMode,
            Notes = $"wake {wakeMargin:F1}x ambient, speech {speechMargin:F1}x ambient, leakage {leakageRms:P1}"
        };
    }

    // Does the recording: opens the microphone, optionally starts a "stimulus" (the test tone) at the same time, stops after the
    // requested time, and summarises the collected chunks. Throws if there is no microphone.
    private async Task<AudioLevelSampleSnapshot> CaptureSampleCoreAsync(
        JarvisOptions options,
        int durationMilliseconds,
        Func<CancellationToken, Task>? stimulus,
        CancellationToken cancellationToken)
    {
        if (durationMilliseconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(durationMilliseconds));
        }

        var deviceIndex = AudioDeviceResolver.GetPreferredInputDeviceIndex(options.SpeechRecognitionDeviceName);

        if (deviceIndex < 0)
        {
            throw new InvalidOperationException("No microphone is available for calibration.");
        }

        var rmsLevels = new List<double>();
        var peakLevels = new List<double>();
        var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var stimulusCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var capture = new WaveInEvent
        {
            DeviceNumber = deviceIndex,
            BufferMilliseconds = 40,
            WaveFormat = new WaveFormat(options.SpeechRecognitionSampleRateHz, 16, 1)
        };

        // Per chunk: copy NAudio's buffer and record its RMS and peak.
        void OnDataAvailable(object? sender, WaveInEventArgs eventArgs)
        {
            if (eventArgs.BytesRecorded <= 0)
            {
                return;
            }

            var chunk = new byte[eventArgs.BytesRecorded];
            Buffer.BlockCopy(eventArgs.Buffer, 0, chunk, 0, eventArgs.BytesRecorded);
            rmsLevels.Add(AudioLevels.Rms(chunk));
            peakLevels.Add(AudioLevels.Peak(chunk));
        }

        void OnRecordingStopped(object? sender, StoppedEventArgs eventArgs)
        {
            if (eventArgs.Exception is not null)
            {
                completion.TrySetException(eventArgs.Exception);
                return;
            }

            completion.TrySetResult(null);
        }

        capture.DataAvailable += OnDataAvailable;
        capture.RecordingStopped += OnRecordingStopped;

        try
        {
            capture.StartRecording();

            var stimulusTask = stimulus is null
                ? Task.CompletedTask
                : stimulus(stimulusCts.Token);

            await Task.Delay(durationMilliseconds, cancellationToken);
            capture.StopRecording();
            await completion.Task.WaitAsync(cancellationToken);
            stimulusCts.Cancel();

            try
            {
                await stimulusTask.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }
        }
        finally
        {
            stimulusCts.Cancel();
            capture.DataAvailable -= OnDataAvailable;
            capture.RecordingStopped -= OnRecordingStopped;

            try
            {
                capture.StopRecording();
            }
            catch
            {
            }
        }

        return new AudioLevelSampleSnapshot(
            AverageRms: rmsLevels.Count == 0 ? 0 : rmsLevels.Average(),
            Rms90: Percentile(rmsLevels, 0.90),
            Peak95: Percentile(peakLevels, 0.95),
            MaxPeak: peakLevels.Count == 0 ? 0 : peakLevels.Max(),
            ChunkCount: rmsLevels.Count);
    }

    // Plays a short rising tone (about 0.5-0.9 s) through the speakers while the microphone is recording, with a short pause first.
    private static async Task PlayValidationToneAsync(int captureDurationMilliseconds, CancellationToken cancellationToken)
    {
        const int sampleRate = 48000;
        var toneDurationMilliseconds = Math.Clamp(captureDurationMilliseconds - 280, 520, 920);
        var toneBytes = BuildValidationTonePcm(sampleRate, toneDurationMilliseconds);

        await Task.Delay(160, cancellationToken);

        using var output = new WaveOutEvent
        {
            DesiredLatency = 60,
            NumberOfBuffers = 2
        };
        using var stream = new RawSourceWaveStream(new MemoryStream(toneBytes, writable: false), new WaveFormat(sampleRate, 16, 1));

        output.Init(stream);
        output.Play();

        try
        {
            while (output.PlaybackState == PlaybackState.Playing)
            {
                await Task.Delay(30, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            try
            {
                output.Stop();
            }
            catch
            {
            }

            throw;
        }
    }

    // Generates the test tone as 16-bit audio: a sine wave whose pitch glides from 420 Hz to 840 Hz, faded in over 50 ms and out
    // over 80 ms (so there is no click), at 24% of full volume.
    private static byte[] BuildValidationTonePcm(int sampleRate, int durationMilliseconds)
    {
        var sampleCount = (int)Math.Ceiling(sampleRate * (durationMilliseconds / 1000d));
        var bytes = new byte[sampleCount * 2];
        var durationSeconds = durationMilliseconds / 1000d;

        for (var index = 0; index < sampleCount; index++)
        {
            var timeSeconds = index / (double)sampleRate;
            var sweepProgress = durationSeconds <= 0 ? 0 : Math.Clamp(timeSeconds / durationSeconds, 0, 1);
            var frequency = 420 + (sweepProgress * 420);
            var fadeIn = Math.Min(1.0, timeSeconds / 0.05);
            var fadeOut = Math.Min(1.0, Math.Max(0, durationSeconds - timeSeconds) / 0.08);
            var envelope = Math.Min(fadeIn, fadeOut);
            var sample = Math.Sin(2 * Math.PI * frequency * timeSeconds) * 0.24 * envelope;
            var pcmSample = (short)Math.Round(sample * short.MaxValue);
            var offset = index * 2;
            var encoded = BitConverter.GetBytes(pcmSample);
            bytes[offset] = encoded[0];
            bytes[offset + 1] = encoded[1];
        }

        return bytes;
    }

    // Returns the value below which the given fraction of the list falls (0.90 = louder than 90% of the chunks).
    private static double Percentile(List<double> values, double percentile)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        var ordered = values.OrderBy(value => value).ToArray();
        var index = Math.Clamp((int)Math.Round((ordered.Length - 1) * percentile), 0, ordered.Length - 1);
        return ordered[index];
    }
}
