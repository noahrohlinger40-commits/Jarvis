// JarvisOptions.cs: every user-changeable Jarvis setting in one record. The app reads jarvis.settings.json
// (next to the project) into this type in JarvisSettingsLoader.cs; each property here matches a camelCase JSON key
// (PlannerBaseUrl is "plannerBaseUrl"). Any key missing from the file keeps the default written below.
// The defaults are generic (for example the OpenAI URL); the shipped jarvis.settings.json overrides the Planner*
// settings to use Jarvis's own local llama-server. The small records after JarvisOptions are list items stored inside it.
namespace Jarvis.Core;

// All settings. "init" means a value is set once when loaded; changing a setting creates a new copy (use "with").
public sealed record JarvisOptions
{
    // Identity and personality: name, persona text, presence preset, response style, what to call the user, and small talk.
    public string AssistantName { get; init; } = "Jarvis";

    public string AssistantPersona { get; init; } = string.Empty;

    public string AssistantPresencePreset { get; init; } = "baseline";

    public string AssistantResponseStyle { get; init; } = "adaptive";

    public string AssistantUserNickname { get; init; } = string.Empty;

    public bool AssistantSmallTalkEnabled { get; init; } = true;

    // Spoken-word hints so the voice says unusual names correctly.
    public string[] AssistantPronunciationHints { get; init; } = Array.Empty<string>();

    // The word that wakes Jarvis up when listening for a command.
    public string WakePhrase { get; init; } = "jarvis";

    // Text-to-speech (Jarvis talking). The Voice* settings pick the provider, server URL, model, voice name and audio format.
    public bool VoiceEnabled { get; init; }

    public string VoiceProvider { get; init; } = "auto";

    public string VoiceBaseUrl { get; init; } = string.Empty;

    public string VoiceModel { get; init; } = "gpt-4o-mini-tts";

    public string VoiceName { get; init; } = "coral";

    // Speech recognition (Jarvis listening). Settings starting with SpeechRecognition cover the engine, language, wake word,
    // microphone tuning and the hotkey below.
    public bool SpeechRecognitionEnabled { get; init; } = true;

    public string SpeechRecognitionProvider { get; init; } = "auto";

    public bool WakeWordEnabled { get; init; } = true;

    public string SpeechRecognitionBaseUrl { get; init; } = "https://api.openai.com/v1";

    public string SpeechRecognitionModel { get; init; } = "gpt-4o-mini-transcribe";

    public string SpeechRecognitionLanguage { get; init; } = "en";

    public string SpeechRecognitionPrompt { get; init; } = string.Empty;

    // Spoken nicknames mapped to what to launch: apps, websites, and the folders searched when looking for files.
    public Dictionary<string, string> ApplicationAliases { get; init; } = new();

    public Dictionary<string, string> SiteAliases { get; init; } = new();

    public string[] FileSearchRoots { get; init; } = Array.Empty<string>();

    // Learned fixes for words the recognizer keeps mishearing, and phrases to ignore completely.
    public SpeechRecognitionCorrectionEntry[] SpeechRecognitionCorrections { get; init; } = Array.Empty<SpeechRecognitionCorrectionEntry>();

    public SpeechRecognitionRejectedPhrase[] SpeechRecognitionRejectedPhrases { get; init; } = Array.Empty<SpeechRecognitionRejectedPhrase>();

    public string[] SpeechRecognitionWakeAliases { get; init; } = Array.Empty<string>();

    // Wake-word detection files and sensitivity. "auto" means Jarvis finds the shipped files itself.
    public string SpeechRecognitionWakeWordAssetPath { get; init; } = "auto";

    public string SpeechRecognitionWakeEnginePath { get; init; } = "auto";

    public bool SpeechRecognitionWakeDetectionEnabled { get; init; } = true;

    public double SpeechRecognitionWakeDetectionSensitivity { get; init; } = 0.62;

    // How long to wait for a command after the wake word before giving up.
    public int VoiceCommandTimeoutSeconds { get; init; } = 8;

    // Recognized text scoring below this (0 to 1) is rejected as probably wrong.
    public double SpeechRecognitionConfidenceThreshold { get; init; } = 0.58;

    // Microphone audio settings: sample rate, how loud counts as speech, how long a pause ends a phrase, audio kept from
    // just before speech starts (pre-roll) and the shortest phrase worth sending.
    public int SpeechRecognitionSampleRateHz { get; init; } = 16000;

    public double SpeechRecognitionVoiceActivityThreshold { get; init; } = 0.008;

    public int SpeechRecognitionSilenceDurationMilliseconds { get; init; } = 850;

    public int SpeechRecognitionPreRollMilliseconds { get; init; } = 450;

    public int SpeechRecognitionMinimumUtteranceMilliseconds { get; init; } = 180;

    public string SpeechRecognitionDeviceName { get; init; } = string.Empty;

    // The global hotkey (default Control+Space) as an alternative to the wake word.
    public bool SpeechRecognitionHotkeyEnabled { get; init; } = true;

    public string SpeechRecognitionHotkey { get; init; } = "Control+Space";

    // Optional audio cleanup: echo cancellation, beamforming and hardware DSP profiles.
    public bool SpeechRecognitionEchoCancellationEnabled { get; init; } = false;

    /// <summary>
    /// Stop talking whenever the microphone gets loud during a reply. Only safe with headphones: through a
    /// speaker Jarvis hears its own voice and cuts itself off. Saying "Jarvis ..." interrupts either way.
    /// </summary>
    public bool SpeechRecognitionBargeInEnabled { get; init; } = false;

    public string SpeechRecognitionBeamformingProfile { get; init; } = "auto";

    public string SpeechRecognitionHardwareDspProfile { get; init; } = "auto";

    // Per-microphone measurements and test results, saved by the calibration and validation tools.
    public SpeechRecognitionCalibrationProfile[] SpeechRecognitionCalibrationProfiles { get; init; } = Array.Empty<SpeechRecognitionCalibrationProfile>();

    public SpeechRecognitionValidationProfile[] SpeechRecognitionValidationProfiles { get; init; } = Array.Empty<SpeechRecognitionValidationProfile>();

    // "half-duplex" means Jarvis does not listen while it is speaking; "full-duplex" listens during replies.
    public string SpeechRecognitionMode { get; init; } = "half-duplex";

    public bool SpeechRecognitionPunctuationRestorationEnabled { get; init; } = true;

    // Closing the window hides Jarvis in the system tray instead of quitting.
    public bool KeepRunningInTrayOnClose { get; init; } = true;

    // Safety switches: shell commands are off unless enabled, major actions ask for approval, and protected system folders are blocked.
    public bool ShellExecutionEnabled { get; init; }

    public bool SafetyRequireApprovalForMajorActions { get; init; } = true;

    public bool SafetyBlockProtectedSystemPaths { get; init; } = true;

    // Optional link to the Brain notes workspace; the context is capped at BrainContextMaxCharacters.
    public bool BrainContextEnabled { get; init; } = false;

    public string BrainWorkspacePath { get; init; } = string.Empty;

    public int BrainContextMaxCharacters { get; init; } = 2400;

    // When true, models may only run on this computer (enforced by LocalAiPolicy), so nothing is sent to the internet.
    public bool LocalAiOnly { get; init; } = true;

    // How many model calls may run at once (1 to 4, see ModelWorkScheduler). Applied at startup only.
    public int ModelMaxConcurrentRequests { get; init; } = 1;

    // Limits and tuning for local model runs: background task count, CPU threads, and whether to log each call to data/planner-log.jsonl.
    public int PlannerMaxConcurrentBackgroundTasks { get; init; } = 2;

    public int PlannerLocalCpuThreads { get; init; } = 2;

    public bool PlannerRecordLocalCalls { get; init; } = true;

    // The "planner" is the language model that decides what Jarvis does. The settings below choose which server and models it uses (see ModelGateway.cs).
    public bool PlannerEnabled { get; init; } = true;

    // "local" is a server on this PC (Jarvis's llama-server); "openai" or "openai-compatible" is a hosted or other OpenAI-style server.
    public string PlannerProvider { get; init; } = "openai-compatible";

    // Server address. For the local engine use http://127.0.0.1:8179 ("/v1" is added automatically).
    public string PlannerBaseUrl { get; init; } = "https://api.openai.com/v1";

    // Model names per route: general, fast, reasoning and vision. Empty ones fall back to another route's model.
    public string PlannerModel { get; init; } = "gpt-4o-mini";

    public string PlannerFastModel { get; init; } = "gpt-4o-mini";

    public string PlannerReasoningModel { get; init; } = string.Empty;

    public string PlannerVisionModel { get; init; } = string.Empty;

    /// <summary>Model for slow, off-turn thinking (brain think). Empty keeps it on the reasoning model.</summary>
    public string PlannerDeepModel { get; init; } = string.Empty;

    /// <summary>Address of the bigger local model server used only when Jarvis's own model gets stuck (LM Studio's default port).</summary>
    public string PlannerEscalationBaseUrl { get; init; } = "http://127.0.0.1:1234";

    /// <summary>Model asked once when the own model is stuck on a request that changed nothing. Empty turns escalation off.</summary>
    public string PlannerEscalationModel { get; init; } = string.Empty;

    // API key lookup: typed value, then Credential Manager entry, then environment variable. Not needed for local servers.
    public string PlannerApiKey { get; init; } = string.Empty;

    public string PlannerApiKeyCredentialTarget { get; init; } = string.Empty;

    public string PlannerApiKeyEnvironmentVariable { get; init; } = "OPENAI_API_KEY";

    // Context window (how much text the model can see at once) assumed for local models; the shipped settings use a smaller one.
    public int PlannerLocalContextLength { get; init; } = 16384;

    // Extra standing instructions added to the model's prompt on every turn.
    public string PlannerInstructions { get; init; } =
        "Ground replies in the live computer context. When the user refers to apps, windows, or what is happening on the computer, prefer the computer, apps, and open app tools.";

    // How much history goes into each prompt: recent conversation turns, saved memories and tool results.
    public int PlannerRecentTurnCount { get; init; } = 6;

    public int PlannerRecentMemoryCount { get; init; } = 8;

    public int PlannerRecentToolResultCount { get; init; } = 6;

    // Maximum model-then-tool rounds in one turn, and how many times a failed model call is retried.
    public int PlannerMaxIterations { get; init; } = 5;

    public int PlannerRetryCount { get; init; } = 2;

    // Allows long jobs to run in the background; web research can be switched off and capped at a number of results.
    public bool PlannerBackgroundTasksEnabled { get; init; } = true;

    public bool PlannerWebResearchEnabled { get; init; } = true;

    public int PlannerWebResearchMaxResults { get; init; } = 5;

    /// <summary>The OAuth client JSON downloaded from Google Cloud Console (Desktop app type).</summary>
    public string GoogleCalendarClientSecretPath { get; init; } = "data/google-client-secret.json";

    /// <summary>
    /// The Obsidian vault lecture and meeting notes go to. Outside OneDrive by default: recordings are large,
    /// and the notes sync to the phone through Obsidian instead.
    /// </summary>
    public string LectureNotesFolder { get; init; } = @"%USERPROFILE%\Lecture Notes";

    // Where screenshots are saved and how many old ones are kept.
    public string VisualCaptureDirectory { get; init; } = Path.Combine("data", "captures");

    public int VisualCaptureRetentionCount { get; init; } = 24;

    // Place used when the user asks for the weather without naming one.
    public string DefaultWeatherLocation { get; init; } = string.Empty;

    // Where runtime data lives: memory, conversation log, agent state and pending approvals. Relative paths resolve under the Jarvis data folder (JarvisDataPaths).
    public string MemoryFilePath { get; init; } = Path.Combine("data", "memory.json");

    public string TranscriptFilePath { get; init; } = Path.Combine("data", "transcript.jsonl");

    public string AgentStateFilePath { get; init; } = Path.Combine("data", "agent-state.json");

    public string PendingApprovalFilePath { get; init; } = Path.Combine("data", "pending-approval.json");


    // Speaker volume settings.
    public bool WhisperModeEnabled { get; init; } = false;
    public double SpeakerVolumeMultiplier { get; init; } = 1.0;
}

// Microphone measurements for one device (background noise level, speech level, recommended thresholds).
public sealed record SpeechRecognitionCalibrationProfile
{
    public string DeviceName { get; init; } = string.Empty;

    public DateTimeOffset CapturedUtc { get; init; } = DateTimeOffset.UtcNow;

    public double AmbientRmsLevel { get; init; } = 0.004;

    public double SpeechRmsLevel { get; init; } = 0.040;

    public double SpeechPeakLevel { get; init; } = 0.180;

    public double RecommendedVoiceActivityThreshold { get; init; } = 0.008;

    public double RecommendedInputGainCap { get; init; } = 4.0;

    public double RecommendedTargetPeakLevel { get; init; } = 0.36;

    public string PreferredBeamformingProfile { get; init; } = "auto";

    public string HardwareDspProfileName { get; init; } = "auto";

    public string Notes { get; init; } = string.Empty;
}

// Results of a wake-word and full-duplex readiness test for one device, with a recommended conversation mode.
public sealed record SpeechRecognitionValidationProfile
{
    public string DeviceName { get; init; } = string.Empty;

    public DateTimeOffset ValidatedUtc { get; init; } = DateTimeOffset.UtcNow;

    public string WakeWordAssetName { get; init; } = string.Empty;

    public string WakeWordAssetPath { get; init; } = string.Empty;

    public string WakeEngineName { get; init; } = string.Empty;

    public string WakeEnginePath { get; init; } = string.Empty;

    public string BeamformingProfileName { get; init; } = "auto";

    public string HardwareDspProfileName { get; init; } = "auto";

    public double AmbientRmsLevel { get; init; } = 0.004;

    public double WakePhraseRmsLevel { get; init; } = 0.040;

    public double CommandSpeechRmsLevel { get; init; } = 0.050;

    public double PlaybackLeakageRmsLevel { get; init; } = 0.012;

    public double WakeWordReadinessScore { get; init; } = 0.50;

    public double FullDuplexReadinessScore { get; init; } = 0.50;

    public string RecommendedConversationMode { get; init; } = "full-duplex";

    public string Notes { get; init; } = string.Empty;
}

// One learned fix: when the recognizer hears HeardText, use CorrectedText (with a usage count).
public sealed record SpeechRecognitionCorrectionEntry
{
    public string HeardText { get; init; } = string.Empty;

    public string CorrectedText { get; init; } = string.Empty;

    public int CorrectionCount { get; init; } = 1;

    public DateTimeOffset LastUpdatedUtc { get; init; } = DateTimeOffset.UtcNow;

    public double? LastConfidence { get; init; }
}

// A phrase the user rejected; it is ignored in future (with a rejection count).
public sealed record SpeechRecognitionRejectedPhrase
{
    public string HeardText { get; init; } = string.Empty;

    public int RejectionCount { get; init; } = 1;

    public DateTimeOffset LastRejectedUtc { get; init; } = DateTimeOffset.UtcNow;

    public double? LastConfidence { get; init; }
}
