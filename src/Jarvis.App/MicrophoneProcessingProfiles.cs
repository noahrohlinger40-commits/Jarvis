// MicrophoneProcessingProfiles.cs: turns the microphone type and saved calibration into the numbers the voice pipeline uses
// (speech threshold, gain limit, target volume, echo and barge-in sensitivity). Output is one ResolvedMicrophoneProcessingProfile,
// built by MicrophoneProcessingProfileResolver.Resolve and used by OpenAiCompatibleVoiceRuntime, VoiceAcousticEchoSuppressor,
// WakeWordDetector and LectureRecorder. Two small catalogues of presets feed it: "beamforming" profiles (what kind of
// microphone and how far away the speaker is) and "hardware DSP" profiles (what cleanup the microphone/Windows already does).
// Most numbers are multipliers: 1.0 = no change, below 1 = more sensitive, above 1 = less sensitive. Levels are 0..1 loudness.
using Jarvis.Core;

namespace Jarvis.App;

// A preset describing the microphone setup. "Beamforming" = a microphone array that focuses on the speaker's direction. Scales
// adjust how readily speech is detected (VoiceActivityScale), how far gain may go (InputGainCapScale), the volume aimed for,
// how strict the echo check is, and the minimum peak a sound needs to count as speech. AutoKeywords are words in the device name that select it.
internal sealed record BeamformingProfile(
    string Name,
    string DisplayName,
    string Description,
    bool UseFarFieldNoiseReduction,
    double VoiceActivityScale,
    double InputGainCapScale,
    double TargetPeakLevel,
    double EchoGateScale,
    double MinimumSpeechPeakLevel,
    string[] AutoKeywords);

// A preset describing cleanup the hardware/Windows already does (echo cancellation, noise suppression, beamforming) and how
// much the app should therefore trust or distrust the signal. The scales feed VoiceAcousticEchoSuppressor.
internal sealed record HardwareDspProfile(
    string Name,
    string DisplayName,
    string Description,
    bool UseHardwareEchoCancellation,
    bool UseHardwareNoiseSuppression,
    bool UseHardwareBeamforming,
    int RollingCaptureBufferMilliseconds,
    double VoiceActivityScale,
    double EchoGateScale,
    double PlaybackEchoFloorScale,
    double AmbientFloorScale,
    double BargeInThresholdScale,
    double ContinuingSpeechThresholdScale,
    string[] AutoKeywords);

// The final combined settings for the chosen microphone: which profiles are active (requested vs. actually used), the wake
// engine and asset, and the computed numbers used at runtime. Calibration and Validation hold the measured results, if any.
internal sealed record ResolvedMicrophoneProcessingProfile(
    string DeviceName,
    string RequestedBeamformingProfileName,
    string EffectiveBeamformingProfileName,
    string EffectiveBeamformingDisplayName,
    string EffectiveBeamformingDescription,
    string RequestedHardwareDspProfileName,
    string EffectiveHardwareDspProfileName,
    string EffectiveHardwareDspDisplayName,
    string EffectiveHardwareDspDescription,
    string RequestedWakeEnginePath,
    string WakeEngineDisplayName,
    string EffectiveWakeEnginePath,
    string EffectiveWakeEngineDescription,
    bool WakeEngineAutomaticallyResolved,
    string WakeWordAssetDisplayName,
    string EffectiveWakeWordAssetPath,
    bool WakeWordAssetAutomaticallyResolved,
    bool HardwareEchoCancellationEnabled,
    bool HardwareNoiseSuppressionEnabled,
    bool HardwareBeamformingEnabled,
    int RollingCaptureBufferMilliseconds,
    double VoiceActivityThreshold,
    double AmbientNoiseFloor,
    double InputGainCap,
    double TargetPeakLevel,
    double EchoGateScale,
    double MinimumSpeechPeakLevel,
    double PlaybackEchoFloorScale,
    double AmbientFloorScale,
    double BargeInThresholdScale,
    double ContinuingSpeechThresholdScale,
    SpeechRecognitionCalibrationProfile? Calibration,
    SpeechRecognitionValidationProfile? Validation)
{
    // One-line summary for the status bar.
    public string BuildStatusLabel()
    {
        var calibrationLabel = Calibration is null ? "uncalibrated" : "calibrated";
        var validationLabel = Validation is null
            ? "unvalidated"
            : Validation.RecommendedConversationMode;
        var parts = new List<string>
        {
            EffectiveBeamformingDisplayName,
            EffectiveHardwareDspDisplayName
        };

        parts.Add(WakeEngineDisplayName);
        parts.Add(calibrationLabel);
        parts.Add(validationLabel);
        return string.Join(", ", parts);
    }

    // Longer summary for the settings screen (calibration, wake and duplex scores, profile names).
    public string BuildUiSummary()
    {
        var parts = new List<string>();

        if (Calibration is null)
        {
            parts.Add("No calibration");
        }
        else
        {
            parts.Add($"Ambient {Calibration.AmbientRmsLevel:P1}");
        }

        if (Validation is not null)
        {
            parts.Add($"wake {Validation.WakeWordReadinessScore:P0}");
            parts.Add($"duplex {Validation.FullDuplexReadinessScore:P0}");
        }

        parts.Add(EffectiveBeamformingDisplayName);
        parts.Add(EffectiveHardwareDspDisplayName);

        parts.Add(WakeEngineDisplayName);

        if (!string.IsNullOrWhiteSpace(WakeWordAssetDisplayName))
        {
            parts.Add(WakeWordAssetDisplayName);
        }

        return string.Join(" | ", parts);
    }
}

// The list of beamforming presets and the rules for choosing one.
internal static class BeamformingProfileCatalog
{
    // Presets: auto, off, desktop (default), headset (close, so more sensitive), conference and far-field-array (further away, so less sensitive and more gain).
    public static IReadOnlyList<BeamformingProfile> All { get; } =
    [
        new(
            "auto",
            "Auto",
            "Choose a sensible profile from the microphone name, wake asset, and saved calibration.",
            UseFarFieldNoiseReduction: false,
            VoiceActivityScale: 1.0,
            InputGainCapScale: 1.0,
            TargetPeakLevel: 0.36,
            EchoGateScale: 1.0,
            MinimumSpeechPeakLevel: 0.055,
            AutoKeywords: Array.Empty<string>()),
        new(
            "off",
            "Off",
            "Disable beamforming hints and keep the speech path in a plain near-field mode.",
            UseFarFieldNoiseReduction: false,
            VoiceActivityScale: 1.0,
            InputGainCapScale: 1.0,
            TargetPeakLevel: 0.36,
            EchoGateScale: 1.0,
            MinimumSpeechPeakLevel: 0.055,
            AutoKeywords: Array.Empty<string>()),
        new(
            "desktop",
            "Desktop",
            "Tune for a nearby desk microphone or monitor-mounted mic.",
            UseFarFieldNoiseReduction: false,
            VoiceActivityScale: 0.92,
            InputGainCapScale: 1.0,
            TargetPeakLevel: 0.34,
            EchoGateScale: 0.95,
            MinimumSpeechPeakLevel: 0.050,
            AutoKeywords: ["usb", "desktop", "monitor", "webcam", "yeti", "seiren", "snowball"]),
        new(
            "headset",
            "Headset",
            "Tune for close-talk headsets and boom microphones.",
            UseFarFieldNoiseReduction: false,
            VoiceActivityScale: 0.78,
            InputGainCapScale: 0.85,
            TargetPeakLevel: 0.28,
            EchoGateScale: 0.82,
            MinimumSpeechPeakLevel: 0.040,
            AutoKeywords: ["headset", "headphone", "boom", "earbud", "airpods", "hands-free"]),
        new(
            "conference",
            "Conference",
            "Tune for room microphones and devices with built-in array processing.",
            UseFarFieldNoiseReduction: true,
            VoiceActivityScale: 1.15,
            InputGainCapScale: 1.10,
            TargetPeakLevel: 0.38,
            EchoGateScale: 1.18,
            MinimumSpeechPeakLevel: 0.065,
            AutoKeywords: ["conference", "speakerphone", "poly", "logi dock", "surface", "studio", "dock"]),
        new(
            "far-field-array",
            "Far-Field Array",
            "Tune for microphone arrays intended to hear speech across the room.",
            UseFarFieldNoiseReduction: true,
            VoiceActivityScale: 1.35,
            InputGainCapScale: 1.20,
            TargetPeakLevel: 0.42,
            EchoGateScale: 1.30,
            MinimumSpeechPeakLevel: 0.075,
            AutoKeywords: ["array", "beam", "far field", "far-field", "respeaker", "smart speaker", "studio mic"])
    ];

    // Returns the named preset; "auto" is chosen from the device name; an unknown name falls back to "desktop".
    public static BeamformingProfile Resolve(string? requestedName, string? deviceName)
    {
        var normalized = Normalize(requestedName);

        if (normalized == "auto")
        {
            return ResolveAuto(deviceName);
        }

        return All.FirstOrDefault(profile => Normalize(profile.Name) == normalized)
            ?? All.First(profile => profile.Name == "desktop");
    }

    // Chooses a preset by looking for keywords ("headset", "array", "usb"...) in the microphone name; "desktop" if nothing matches.
    public static BeamformingProfile ResolveAuto(string? deviceName)
    {
        var name = deviceName?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(name))
        {
            return All.First(profile => profile.Name == "desktop");
        }

        foreach (var profile in All.Where(profile => profile.Name is not "auto" and not "off"))
        {
            if (profile.AutoKeywords.Any(keyword => name.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
            {
                return profile;
            }
        }

        return All.First(profile => profile.Name == "desktop");
    }

    // Empty means "auto"; otherwise lower-case.
    private static string Normalize(string? name)
    {
        return string.IsNullOrWhiteSpace(name)
            ? "auto"
            : name.Trim().ToLowerInvariant();
    }
}

// The list of hardware-cleanup presets and the rules for choosing one.
internal static class HardwareDspProfileCatalog
{
    // Presets: auto, off ("software only"), system-aec (Windows echo cancellation), voice-processing, conference-array, vendor-beamforming.
    // RollingCaptureBufferMilliseconds is the length of each recorded chunk (shorter = quicker reaction).
    public static IReadOnlyList<HardwareDspProfile> All { get; } =
    [
        new(
            "auto",
            "Auto",
            "Choose a hardware DSP posture from the microphone type and beamforming profile.",
            UseHardwareEchoCancellation: false,
            UseHardwareNoiseSuppression: false,
            UseHardwareBeamforming: false,
            RollingCaptureBufferMilliseconds: 60,
            VoiceActivityScale: 1.0,
            EchoGateScale: 1.0,
            PlaybackEchoFloorScale: 1.0,
            AmbientFloorScale: 1.0,
            BargeInThresholdScale: 1.0,
            ContinuingSpeechThresholdScale: 1.0,
            AutoKeywords: Array.Empty<string>()),
        new(
            "off",
            "Software Only",
            "Disable hardware-DSP assumptions and keep the app in software-only mode.",
            UseHardwareEchoCancellation: false,
            UseHardwareNoiseSuppression: false,
            UseHardwareBeamforming: false,
            RollingCaptureBufferMilliseconds: 60,
            VoiceActivityScale: 1.0,
            EchoGateScale: 1.0,
            PlaybackEchoFloorScale: 1.0,
            AmbientFloorScale: 1.0,
            BargeInThresholdScale: 1.0,
            ContinuingSpeechThresholdScale: 1.0,
            AutoKeywords: Array.Empty<string>()),
        new(
            "system-aec",
            "System AEC",
            "Assume Windows or the device stack can provide acoustic echo cancellation and communication noise suppression.",
            UseHardwareEchoCancellation: true,
            UseHardwareNoiseSuppression: true,
            UseHardwareBeamforming: false,
            RollingCaptureBufferMilliseconds: 48,
            VoiceActivityScale: 0.94,
            EchoGateScale: 0.82,
            PlaybackEchoFloorScale: 0.82,
            AmbientFloorScale: 0.92,
            BargeInThresholdScale: 0.88,
            ContinuingSpeechThresholdScale: 0.84,
            AutoKeywords: ["headset", "hands-free", "bluetooth", "communications", "earbud"]),
        new(
            "voice-processing",
            "Voice Processing",
            "Assume the microphone exposes a near-field voice-processing chain for speech capture.",
            UseHardwareEchoCancellation: true,
            UseHardwareNoiseSuppression: true,
            UseHardwareBeamforming: false,
            RollingCaptureBufferMilliseconds: 46,
            VoiceActivityScale: 0.92,
            EchoGateScale: 0.78,
            PlaybackEchoFloorScale: 0.78,
            AmbientFloorScale: 0.90,
            BargeInThresholdScale: 0.82,
            ContinuingSpeechThresholdScale: 0.78,
            AutoKeywords: ["webcam", "usb", "microphone", "snowball", "yeti", "seiren"]),
        new(
            "conference-array",
            "Conference Array",
            "Assume the device stack exposes hardware beamforming with room-scale echo control.",
            UseHardwareEchoCancellation: true,
            UseHardwareNoiseSuppression: true,
            UseHardwareBeamforming: true,
            RollingCaptureBufferMilliseconds: 42,
            VoiceActivityScale: 1.08,
            EchoGateScale: 0.74,
            PlaybackEchoFloorScale: 0.70,
            AmbientFloorScale: 0.88,
            BargeInThresholdScale: 0.74,
            ContinuingSpeechThresholdScale: 0.70,
            AutoKeywords: ["conference", "speakerphone", "surface", "dock", "poly", "studio"]),
        new(
            "vendor-beamforming",
            "Vendor Beamforming",
            "Assume the microphone stack exposes vendor-managed far-field beamforming and noise cleanup.",
            UseHardwareEchoCancellation: false,
            UseHardwareNoiseSuppression: true,
            UseHardwareBeamforming: true,
            RollingCaptureBufferMilliseconds: 44,
            VoiceActivityScale: 1.02,
            EchoGateScale: 0.88,
            PlaybackEchoFloorScale: 0.80,
            AmbientFloorScale: 0.90,
            BargeInThresholdScale: 0.80,
            ContinuingSpeechThresholdScale: 0.76,
            AutoKeywords: ["array", "beam", "far field", "far-field", "respeaker", "smart speaker"])
    ];

    // Returns the named preset, or picks automatically if the name is unknown.
    public static HardwareDspProfile Resolve(
        string? requestedName,
        string? deviceName,
        BeamformingProfile beamformingProfile)
    {
        var normalized = Normalize(requestedName);

        if (normalized == "auto")
        {
            return ResolveAuto(deviceName, beamformingProfile);
        }

        return All.FirstOrDefault(profile => Normalize(profile.Name) == normalized)
            ?? ResolveAuto(deviceName, beamformingProfile);
    }

    // Chooses by the beamforming preset first (far-field array and conference microphones imply matching hardware), then by device-name keywords; otherwise "off".
    public static HardwareDspProfile ResolveAuto(string? deviceName, BeamformingProfile beamformingProfile)
    {
        var name = deviceName?.Trim() ?? string.Empty;

        if (beamformingProfile.Name == "far-field-array")
        {
            return All.First(profile => profile.Name == "vendor-beamforming");
        }

        if (beamformingProfile.Name == "conference")
        {
            return All.First(profile => profile.Name == "conference-array");
        }

        foreach (var profile in All.Where(profile => profile.Name is not "auto" and not "off"))
        {
            if (profile.AutoKeywords.Any(keyword => name.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
            {
                return profile;
            }
        }

        return All.First(profile => profile.Name == "off");
    }

    // Empty means "auto"; otherwise lower-case.
    private static string Normalize(string? name)
    {
        return string.IsNullOrWhiteSpace(name)
            ? "auto"
            : name.Trim().ToLowerInvariant();
    }
}

// Combines the user's settings, saved calibration, saved validation and wake asset/engine choice into one profile.
internal static class MicrophoneProcessingProfileResolver
{
    // The choices depend on each other (the saved calibration to use depends on the profile, and the profile can depend on the
    // calibration), so Resolve works in rounds: a first guess, find the calibration, a second guess, find the validation, a final pass.
    public static ResolvedMicrophoneProcessingProfile Resolve(JarvisOptions options)
    {
        var selectedWakeWordAsset = WakeWordAssetCatalog.ResolveSelectionDescriptor(options);
        var wakeWordAsset = WakeWordAssetCatalog.ResolveConfiguredAsset(options);
        var initialRequestedBeamformingProfileName = ResolveRequestedBeamformingProfile(options, calibration: null, wakeWordAsset);
        var initialBeamformingProfile = BeamformingProfileCatalog.Resolve(initialRequestedBeamformingProfileName, options.SpeechRecognitionDeviceName);
        var initialRequestedHardwareDspProfileName = ResolveRequestedHardwareDspProfile(options, validation: null, wakeWordAsset);
        var initialHardwareDspProfile = HardwareDspProfileCatalog.Resolve(
            initialRequestedHardwareDspProfileName,
            options.SpeechRecognitionDeviceName,
            initialBeamformingProfile);
        var initialRequestedWakeEnginePath = ResolveRequestedWakeEnginePath(options, validation: null, wakeWordAsset);
        var selectedWakeEngine = WakeWordEngineCatalog.ResolveSelectionDescriptor(options);
        var wakeEngine = ResolveWakeEngine(
            options,
            wakeWordAsset,
            initialRequestedWakeEnginePath,
            initialBeamformingProfile.Name,
            initialHardwareDspProfile.Name);
        var calibration = ResolveCalibration(
            options,
            initialBeamformingProfile.Name,
            initialHardwareDspProfile.Name);
        var requestedBeamformingProfileName = ResolveRequestedBeamformingProfile(options, calibration, wakeWordAsset);
        var beamformingProfile = BeamformingProfileCatalog.Resolve(requestedBeamformingProfileName, options.SpeechRecognitionDeviceName);
        var tentativeHardwareDspProfileName = ResolveRequestedHardwareDspProfile(options, validation: null, wakeWordAsset);
        var hardwareDspProfile = HardwareDspProfileCatalog.Resolve(
            tentativeHardwareDspProfileName,
            options.SpeechRecognitionDeviceName,
            beamformingProfile);
        var tentativeWakeEnginePath = ResolveRequestedWakeEnginePath(options, validation: null, wakeWordAsset);
        wakeEngine = ResolveWakeEngine(
            options,
            wakeWordAsset,
            tentativeWakeEnginePath,
            beamformingProfile.Name,
            hardwareDspProfile.Name);
        var validation = ResolveValidation(
            options,
            wakeWordAsset,
            wakeEngine,
            beamformingProfile.Name,
            hardwareDspProfile.Name);
        var requestedHardwareDspProfileName = ResolveRequestedHardwareDspProfile(options, validation, wakeWordAsset);
        hardwareDspProfile = HardwareDspProfileCatalog.Resolve(
            requestedHardwareDspProfileName,
            options.SpeechRecognitionDeviceName,
            beamformingProfile);
        var requestedWakeEnginePath = ResolveRequestedWakeEnginePath(options, validation, wakeWordAsset);
        wakeEngine = ResolveWakeEngine(
            options,
            wakeWordAsset,
            requestedWakeEnginePath,
            beamformingProfile.Name,
            hardwareDspProfile.Name);
        validation = ResolveValidation(
            options,
            wakeWordAsset,
            wakeEngine,
            beamformingProfile.Name,
            hardwareDspProfile.Name);

        // Speech threshold: the calibrated value if there is one, else the setting; scaled by the beamforming and hardware presets (and
        // 6% lower when the wake word was found unreliable in testing), kept between 0.0025 and 0.040.
        var baseThreshold = calibration?.RecommendedVoiceActivityThreshold > 0
            ? calibration.RecommendedVoiceActivityThreshold
            : options.SpeechRecognitionVoiceActivityThreshold;
        var wakeSensitivityScale = validation is not null && validation.WakeWordReadinessScore < 0.55
            ? 0.94
            : 1.0;
        var voiceActivityThreshold = Math.Clamp(
            baseThreshold
            * beamformingProfile.VoiceActivityScale
            * hardwareDspProfile.VoiceActivityScale
            * wakeSensitivityScale,
            0.0025,
            0.040);
        // Room noise level: from calibration, else 30% of the speech threshold; never under 22% of the threshold; kept between 0.0015 and 0.10.
        var ambientNoiseFloor = Math.Clamp(
            Math.Max(calibration?.AmbientRmsLevel ?? (voiceActivityThreshold * 0.30), voiceActivityThreshold * 0.22),
            0.0015,
            0.10);
        // Largest automatic volume boost: calibrated value (default 4x) times the preset, kept between 1x and 6x.
        var inputGainCap = Math.Clamp(
            (calibration?.RecommendedInputGainCap ?? 4.0)
            * beamformingProfile.InputGainCapScale,
            1.0,
            6.0);
        // Volume the automatic gain aims for (the loudest sample): the preset's value, or the average of preset and calibration, kept between 0.24 and 0.50.
        var targetPeak = Math.Clamp(
            (calibration is null
                ? beamformingProfile.TargetPeakLevel
                : (calibration.RecommendedTargetPeakLevel + beamformingProfile.TargetPeakLevel) / 2.0),
            0.24,
            0.50);
        // If testing showed Jarvis's own voice leaks into the microphone, the echo gate is made stricter (up to 1.18x); good full-duplex scores relax it to 0.96x.
        var validationEchoScale = validation is null
            ? 1.0
            : Math.Clamp(1.18 - (validation.FullDuplexReadinessScore * 0.18), 0.96, 1.18);
        // Weak full-duplex score raises the minimum peak a sound needs to count as the user interrupting (up to +0.018).
        var validationSpeechPeakOffset = validation is null
            ? 0
            : Math.Clamp((0.70 - validation.FullDuplexReadinessScore) * 0.028, 0, 0.018);
        var hardwareEchoCancellationEnabled = hardwareDspProfile.UseHardwareEchoCancellation;
        var hardwareNoiseSuppressionEnabled = hardwareDspProfile.UseHardwareNoiseSuppression;
        // Beamforming counts as on when enabled in settings or implied by the preset.
        var hardwareBeamformingEnabled = options.SpeechRecognitionBeamformingEnabled
            || beamformingProfile.UseFarFieldNoiseReduction
            || hardwareDspProfile.UseHardwareBeamforming;

        // Assemble the result; scales are clamped to safe ranges.
        return new ResolvedMicrophoneProcessingProfile(
            DeviceName: options.SpeechRecognitionDeviceName?.Trim() ?? string.Empty,
            RequestedBeamformingProfileName: requestedBeamformingProfileName,
            EffectiveBeamformingProfileName: beamformingProfile.Name,
            EffectiveBeamformingDisplayName: beamformingProfile.DisplayName,
            EffectiveBeamformingDescription: beamformingProfile.Description,
            RequestedHardwareDspProfileName: requestedHardwareDspProfileName,
            EffectiveHardwareDspProfileName: hardwareDspProfile.Name,
            EffectiveHardwareDspDisplayName: hardwareDspProfile.DisplayName,
            EffectiveHardwareDspDescription: hardwareDspProfile.Description,
            RequestedWakeEnginePath: requestedWakeEnginePath,
            WakeEngineDisplayName: BuildWakeEngineDisplayName(selectedWakeEngine, wakeEngine),
            EffectiveWakeEnginePath: wakeEngine.Path,
            EffectiveWakeEngineDescription: wakeEngine.Description,
            WakeEngineAutomaticallyResolved: selectedWakeEngine.IsAutomaticSelection,
            WakeWordAssetDisplayName: BuildWakeWordAssetDisplayName(selectedWakeWordAsset, wakeWordAsset),
            EffectiveWakeWordAssetPath: wakeWordAsset?.Path ?? string.Empty,
            WakeWordAssetAutomaticallyResolved: selectedWakeWordAsset.IsAutomaticSelection,
            HardwareEchoCancellationEnabled: hardwareEchoCancellationEnabled,
            HardwareNoiseSuppressionEnabled: hardwareNoiseSuppressionEnabled,
            HardwareBeamformingEnabled: hardwareBeamformingEnabled,
            RollingCaptureBufferMilliseconds: hardwareDspProfile.RollingCaptureBufferMilliseconds,
            VoiceActivityThreshold: voiceActivityThreshold,
            AmbientNoiseFloor: ambientNoiseFloor,
            InputGainCap: inputGainCap,
            TargetPeakLevel: targetPeak,
            EchoGateScale: Math.Clamp(
                beamformingProfile.EchoGateScale
                * hardwareDspProfile.EchoGateScale
                * validationEchoScale,
                0.70,
                1.80),
            MinimumSpeechPeakLevel: Math.Clamp(
                beamformingProfile.MinimumSpeechPeakLevel
                + validationSpeechPeakOffset,
                0.030,
                0.12),
            PlaybackEchoFloorScale: Math.Clamp(
                hardwareDspProfile.PlaybackEchoFloorScale,
                0.55,
                1.80),
            AmbientFloorScale: Math.Clamp(
                hardwareDspProfile.AmbientFloorScale,
                0.55,
                1.80),
            BargeInThresholdScale: Math.Clamp(
                hardwareDspProfile.BargeInThresholdScale,
                0.55,
                1.90),
            ContinuingSpeechThresholdScale: Math.Clamp(
                hardwareDspProfile.ContinuingSpeechThresholdScale,
                0.55,
                1.90),
            Calibration: calibration,
            Validation: validation);
    }

    // Finds the saved calibration for this microphone, best-matching beamforming/DSP profile first, newest as tie-breaker. Null if none.
    private static SpeechRecognitionCalibrationProfile? ResolveCalibration(
        JarvisOptions options,
        string beamformingProfileName,
        string hardwareDspProfileName)
    {
        if (options.SpeechRecognitionCalibrationProfiles.Length == 0
            || string.IsNullOrWhiteSpace(options.SpeechRecognitionDeviceName))
        {
            return null;
        }

        var normalizedBeamformingProfileName = NormalizeProfileName(beamformingProfileName);
        var normalizedHardwareDspProfileName = NormalizeProfileName(hardwareDspProfileName);

        return options.SpeechRecognitionCalibrationProfiles
            .Where(profile =>
                string.Equals(
                    profile.DeviceName?.Trim(),
                    options.SpeechRecognitionDeviceName.Trim(),
                    StringComparison.OrdinalIgnoreCase))
            .Select(profile => new
            {
                Profile = profile,
                Score = ScoreCalibrationProfile(
                    profile,
                    normalizedBeamformingProfileName,
                    normalizedHardwareDspProfileName)
            })
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.Profile.CapturedUtc)
            .Select(item => item.Profile)
            .FirstOrDefault();
    }

    // Finds the saved validation for this microphone, best matching the wake asset, wake engine and profiles, newest first. Null if none.
    private static SpeechRecognitionValidationProfile? ResolveValidation(
        JarvisOptions options,
        WakeWordAssetDescriptor? wakeWordAsset,
        WakeWordEngineDescriptor wakeEngine,
        string beamformingProfileName,
        string hardwareDspProfileName)
    {
        if (options.SpeechRecognitionValidationProfiles.Length == 0
            || string.IsNullOrWhiteSpace(options.SpeechRecognitionDeviceName))
        {
            return null;
        }

        var normalizedWakeWordAssetPath = WakeWordAssetCatalog.NormalizeStoredPath(wakeWordAsset?.Path);
        var normalizedWakeWordAssetName = wakeWordAsset?.BuildShortLabel() ?? "Recognizer Default";
        var normalizedWakeEnginePath = WakeWordEngineCatalog.NormalizeStoredPath(wakeEngine.Path);
        var normalizedWakeEngineName = wakeEngine.BuildShortLabel();
        var normalizedBeamformingProfileName = NormalizeProfileName(beamformingProfileName);
        var normalizedHardwareDspProfileName = NormalizeProfileName(hardwareDspProfileName);

        return options.SpeechRecognitionValidationProfiles
            .Where(profile =>
                string.Equals(
                    profile.DeviceName?.Trim(),
                    options.SpeechRecognitionDeviceName.Trim(),
                    StringComparison.OrdinalIgnoreCase))
            .Select(profile => new
            {
                Profile = profile,
                Score = ScoreValidationProfile(
                    profile,
                    normalizedWakeWordAssetPath,
                    normalizedWakeWordAssetName,
                    normalizedWakeEnginePath,
                    normalizedWakeEngineName,
                    normalizedBeamformingProfileName,
                    normalizedHardwareDspProfileName)
            })
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.Profile.ValidatedUtc)
            .Select(item => item.Profile)
            .FirstOrDefault();
    }

    // Which beamforming profile to request. Order of preference: explicit setting, the profile the calibration was made with, the wake asset's preference, else "auto".
    private static string ResolveRequestedBeamformingProfile(
        JarvisOptions options,
        SpeechRecognitionCalibrationProfile? calibration,
        WakeWordAssetDescriptor? wakeWordAsset)
    {
        if (!string.IsNullOrWhiteSpace(options.SpeechRecognitionBeamformingProfile)
            && !string.Equals(options.SpeechRecognitionBeamformingProfile, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return options.SpeechRecognitionBeamformingProfile.Trim();
        }

        if (!string.IsNullOrWhiteSpace(calibration?.PreferredBeamformingProfile))
        {
            return calibration.PreferredBeamformingProfile.Trim();
        }

        if (!string.IsNullOrWhiteSpace(wakeWordAsset?.PreferredBeamformingProfile))
        {
            return wakeWordAsset.PreferredBeamformingProfile.Trim();
        }

        return string.IsNullOrWhiteSpace(options.SpeechRecognitionBeamformingProfile)
            ? "auto"
            : options.SpeechRecognitionBeamformingProfile.Trim();
    }

    // Which hardware DSP profile to request. Order: explicit setting, the validation's profile, the wake asset's preference, else "auto".
    private static string ResolveRequestedHardwareDspProfile(
        JarvisOptions options,
        SpeechRecognitionValidationProfile? validation,
        WakeWordAssetDescriptor? wakeWordAsset)
    {
        if (!string.IsNullOrWhiteSpace(options.SpeechRecognitionHardwareDspProfile)
            && !string.Equals(options.SpeechRecognitionHardwareDspProfile, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return options.SpeechRecognitionHardwareDspProfile.Trim();
        }

        if (!string.IsNullOrWhiteSpace(validation?.HardwareDspProfileName)
            && !string.Equals(validation.HardwareDspProfileName, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return validation.HardwareDspProfileName.Trim();
        }

        if (!string.IsNullOrWhiteSpace(wakeWordAsset?.PreferredHardwareDspProfile))
        {
            return wakeWordAsset.PreferredHardwareDspProfile.Trim();
        }

        return string.IsNullOrWhiteSpace(options.SpeechRecognitionHardwareDspProfile)
            ? "auto"
            : options.SpeechRecognitionHardwareDspProfile.Trim();
    }

    // Which wake engine file to request. Order: explicit setting, the validation's choice, the wake asset's preference, else "auto".
    private static string ResolveRequestedWakeEnginePath(
        JarvisOptions options,
        SpeechRecognitionValidationProfile? validation,
        WakeWordAssetDescriptor? wakeWordAsset)
    {
        if (!string.IsNullOrWhiteSpace(options.SpeechRecognitionWakeEnginePath)
            && !WakeWordEngineCatalog.IsAutoSelectionPath(options.SpeechRecognitionWakeEnginePath))
        {
            return WakeWordEngineCatalog.NormalizeStoredPath(options.SpeechRecognitionWakeEnginePath);
        }

        if (validation is not null
            && (!string.IsNullOrWhiteSpace(validation.WakeEnginePath)
                || !string.IsNullOrWhiteSpace(validation.WakeEngineName)))
        {
            return WakeWordEngineCatalog.NormalizeStoredPath(validation.WakeEnginePath);
        }

        if (!string.IsNullOrWhiteSpace(wakeWordAsset?.PreferredWakeEnginePath))
        {
            return WakeWordEngineCatalog.NormalizeStoredPath(wakeWordAsset.PreferredWakeEnginePath);
        }

        return string.IsNullOrWhiteSpace(options.SpeechRecognitionWakeEnginePath)
            ? WakeWordEngineCatalog.AutoSelectionPath
            : WakeWordEngineCatalog.NormalizeStoredPath(options.SpeechRecognitionWakeEnginePath);
    }

    // Finds the wake engine: "auto" picks the best for the microphone, otherwise the named file.
    private static WakeWordEngineDescriptor ResolveWakeEngine(
        JarvisOptions options,
        WakeWordAssetDescriptor? wakeWordAsset,
        string requestedWakeEnginePath,
        string beamformingProfileName,
        string hardwareDspProfileName)
    {
        if (WakeWordEngineCatalog.IsAutoSelectionPath(requestedWakeEnginePath))
        {
            return WakeWordEngineCatalog.ResolveConfiguredEngine(
                options with
                {
                    SpeechRecognitionWakeEnginePath = WakeWordEngineCatalog.AutoSelectionPath
                },
                wakeWordAsset,
                beamformingProfileName,
                hardwareDspProfileName);
        }

        return WakeWordEngineCatalog.ResolveDescriptorByPath(
            Directory.GetCurrentDirectory(),
            options,
            requestedWakeEnginePath);
    }

    // Text like "Auto -> Hey Jarvis" so the UI shows both the setting and what it resolved to.
    private static string BuildWakeWordAssetDisplayName(
        WakeWordAssetDescriptor selectionDescriptor,
        WakeWordAssetDescriptor? effectiveDescriptor)
    {
        if (selectionDescriptor.IsAutomaticSelection)
        {
            return effectiveDescriptor is null
                ? "Auto -> Recognizer Default"
                : $"Auto -> {effectiveDescriptor.BuildShortLabel()}";
        }

        return effectiveDescriptor?.BuildShortLabel() ?? "Recognizer Default";
    }

    // Same idea for the wake engine name.
    private static string BuildWakeEngineDisplayName(
        WakeWordEngineDescriptor selectionDescriptor,
        WakeWordEngineDescriptor effectiveDescriptor)
    {
        if (selectionDescriptor.IsAutomaticSelection)
        {
            return $"Auto -> {effectiveDescriptor.BuildShortLabel()}";
        }

        return effectiveDescriptor.BuildShortLabel();
    }

    // Match score between a saved calibration and the current profiles: +24 beamforming match (+10 if either is "auto"), +18 hardware match (+8 for "auto").
    private static int ScoreCalibrationProfile(
        SpeechRecognitionCalibrationProfile profile,
        string normalizedBeamformingProfileName,
        string normalizedHardwareDspProfileName)
    {
        var score = 0;
        var profileBeamformingProfileName = NormalizeProfileName(profile.PreferredBeamformingProfile);
        var profileHardwareDspProfileName = NormalizeProfileName(profile.HardwareDspProfileName);

        if (string.Equals(profileBeamformingProfileName, normalizedBeamformingProfileName, StringComparison.OrdinalIgnoreCase))
        {
            score += 24;
        }
        else if (profileBeamformingProfileName == "auto" || normalizedBeamformingProfileName == "auto")
        {
            score += 10;
        }

        if (string.Equals(profileHardwareDspProfileName, normalizedHardwareDspProfileName, StringComparison.OrdinalIgnoreCase))
        {
            score += 18;
        }
        else if (profileHardwareDspProfileName == "auto" || normalizedHardwareDspProfileName == "auto")
        {
            score += 8;
        }

        return score;
    }

    // Match score between a saved validation and the current setup: wake asset (34 path / 28 default / 18 name), wake engine (30 / 26 / 16),
    // beamforming (24, or 10 for "auto") and hardware profile (24, or 10 for "auto").
    private static int ScoreValidationProfile(
        SpeechRecognitionValidationProfile profile,
        string normalizedWakeWordAssetPath,
        string normalizedWakeWordAssetName,
        string normalizedWakeEnginePath,
        string normalizedWakeEngineName,
        string normalizedBeamformingProfileName,
        string normalizedHardwareDspProfileName)
    {
        var score = 0;
        var profileWakeWordAssetPath = WakeWordAssetCatalog.NormalizeStoredPath(profile.WakeWordAssetPath);
        var profileWakeWordAssetName = string.IsNullOrWhiteSpace(profile.WakeWordAssetName)
            ? "Recognizer Default"
            : profile.WakeWordAssetName.Trim();
        var profileWakeEnginePath = WakeWordEngineCatalog.NormalizeStoredPath(profile.WakeEnginePath);
        var profileWakeEngineName = string.IsNullOrWhiteSpace(profile.WakeEngineName)
            ? "Recognizer Wake"
            : profile.WakeEngineName.Trim();
        var profileBeamformingProfileName = NormalizeProfileName(profile.BeamformingProfileName);
        var profileHardwareDspProfileName = NormalizeProfileName(profile.HardwareDspProfileName);

        if (string.IsNullOrWhiteSpace(normalizedWakeWordAssetPath))
        {
            if (string.IsNullOrWhiteSpace(profileWakeWordAssetPath)
                && string.Equals(profileWakeWordAssetName, "Recognizer Default", StringComparison.OrdinalIgnoreCase))
            {
                score += 28;
            }
        }
        else if (string.Equals(profileWakeWordAssetPath, normalizedWakeWordAssetPath, StringComparison.OrdinalIgnoreCase))
        {
            score += 34;
        }
        else if (string.Equals(profileWakeWordAssetName, normalizedWakeWordAssetName, StringComparison.OrdinalIgnoreCase))
        {
            score += 18;
        }

        if (string.IsNullOrWhiteSpace(normalizedWakeEnginePath))
        {
            if (string.IsNullOrWhiteSpace(profileWakeEnginePath)
                && string.Equals(profileWakeEngineName, "Recognizer Wake", StringComparison.OrdinalIgnoreCase))
            {
                score += 26;
            }
        }
        else if (WakeWordEngineCatalog.StoredPathsMatch(profileWakeEnginePath, normalizedWakeEnginePath))
        {
            score += 30;
        }
        else if (string.Equals(profileWakeEngineName, normalizedWakeEngineName, StringComparison.OrdinalIgnoreCase))
        {
            score += 16;
        }

        if (string.Equals(profileBeamformingProfileName, normalizedBeamformingProfileName, StringComparison.OrdinalIgnoreCase))
        {
            score += 24;
        }
        else if (profileBeamformingProfileName == "auto" || normalizedBeamformingProfileName == "auto")
        {
            score += 10;
        }

        if (string.Equals(profileHardwareDspProfileName, normalizedHardwareDspProfileName, StringComparison.OrdinalIgnoreCase))
        {
            score += 24;
        }
        else if (profileHardwareDspProfileName == "auto" || normalizedHardwareDspProfileName == "auto")
        {
            score += 10;
        }

        return score;
    }

    // Empty means "auto"; otherwise lower-case.
    private static string NormalizeProfileName(string? profileName)
    {
        return string.IsNullOrWhiteSpace(profileName)
            ? "auto"
            : profileName.Trim().ToLowerInvariant();
    }
}
