// WakeWordAssets.cs: "wake word assets" are small JSON files (default folder data\wake-word-assets) describing a tuned wake
// word setup: the phrases and spelling variants to accept, a confidence cutoff, a sensitivity tweak, which microphone types
// it suits, and an optional detector model path. This file finds, loads (with a cache) and ranks them.
// Used by WakeWordDetector, WakeWordEngines, VoiceRecognitionText and the settings UI. "Auto" picks the best asset for the active microphone.
using System.Text.Json;
using Jarvis.Core;

namespace Jarvis.App;

// One loaded wake word asset: everything from its JSON file plus the file path. Path "auto" means automatic choice; an empty path means "no asset".
internal sealed record WakeWordAssetDescriptor(
    string Path,
    string Name,
    string DisplayName,
    string Description,
    string[] WakePhrases,
    string[] Aliases,
    double? MinimumConfidence,
    double SensitivityBias,
    string PreferredBeamformingProfile,
    string PreferredHardwareDspProfile,
    string PreferredWakeEnginePath,
    string[] DeviceKeywords,
    string[] ExcludedDeviceKeywords)
{
    // True for the built-in "no dedicated asset" entry.
    public bool IsRecognizerDefault => string.IsNullOrWhiteSpace(Path);

    // True for the "auto" entry that chooses an asset by microphone.
    public bool IsAutomaticSelection =>
        string.Equals(
            WakeWordAssetCatalog.NormalizeStoredPath(Path),
            WakeWordAssetCatalog.AutoSelectionPath,
            StringComparison.OrdinalIgnoreCase);

    // Name to show in menus: display name, else name, else "Recognizer Default".
    public string BuildShortLabel()
    {
        if (!string.IsNullOrWhiteSpace(DisplayName))
        {
            return DisplayName;
        }

        if (!string.IsNullOrWhiteSpace(Name))
        {
            return Name;
        }

        return "Recognizer Default";
    }

    // Wake phrases plus aliases, trimmed and de-duplicated, sorted by name.
    public IReadOnlyList<string> BuildWakeAliases()
    {
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var phrase in WakePhrases)
        {
            AddAlias(phrase);
        }

        foreach (var alias in Aliases)
        {
            AddAlias(alias);
        }

        return aliases.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();

        void AddAlias(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            aliases.Add(value.Trim());
        }
    }

    // How well this asset suits a microphone: each device keyword found in the microphone name adds 18 plus the keyword's length (up to 18).
    // An excluded keyword disqualifies it (int.MinValue). The default and auto entries never match (int.MinValue).
    public int ScoreDeviceMatch(string? deviceName)
    {
        if (IsRecognizerDefault || IsAutomaticSelection)
        {
            return int.MinValue;
        }

        var normalizedDeviceName = deviceName?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(normalizedDeviceName))
        {
            return 0;
        }

        if (ExcludedDeviceKeywords.Any(keyword =>
                normalizedDeviceName.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
        {
            return int.MinValue;
        }

        var score = 0;

        foreach (var keyword in DeviceKeywords)
        {
            if (normalizedDeviceName.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                score += 18 + Math.Min(keyword.Length, 18);
            }
        }

        return score;
    }
}

// Finds, loads and caches wake word assets.
internal static class WakeWordAssetCatalog
{
    // JSON reading rules (case-insensitive property names).
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    // Lock for the cache below.
    private static readonly object Sync = new();
    // Parsed assets keyed by file path, reloaded when the file's last-write time changes.
    private static readonly Dictionary<string, CachedWakeWordAsset> Cache = new(StringComparer.OrdinalIgnoreCase);
    // Where assets live, relative to the app folder.
    private const string DefaultRelativeDirectory = "data\\wake-word-assets";
    public const string AutoSelectionPath = "auto";

    // The "choose automatically" menu entry.
    public static WakeWordAssetDescriptor AutoSelection { get; } =
        new(
            Path: AutoSelectionPath,
            Name: "auto",
            DisplayName: "Auto (Per Device)",
            Description: "Automatically choose the best dedicated wake asset for the active microphone when one matches.",
            WakePhrases: Array.Empty<string>(),
            Aliases: Array.Empty<string>(),
            MinimumConfidence: null,
            SensitivityBias: 0,
            PreferredBeamformingProfile: string.Empty,
            PreferredHardwareDspProfile: string.Empty,
            PreferredWakeEnginePath: string.Empty,
            DeviceKeywords: Array.Empty<string>(),
            ExcludedDeviceKeywords: Array.Empty<string>());

    // The "use the built-in recogniser wake grammar, no asset" entry.
    public static WakeWordAssetDescriptor RecognizerDefault { get; } =
        new(
            Path: string.Empty,
            Name: "recognizer-default",
            DisplayName: "Recognizer Default",
            Description: "Use the built-in recognizer wake grammars without a dedicated asset manifest.",
            WakePhrases: Array.Empty<string>(),
            Aliases: Array.Empty<string>(),
            MinimumConfidence: null,
            SensitivityBias: 0,
            PreferredBeamformingProfile: string.Empty,
            PreferredHardwareDspProfile: string.Empty,
            PreferredWakeEnginePath: string.Empty,
            DeviceKeywords: Array.Empty<string>(),
            ExcludedDeviceKeywords: Array.Empty<string>());

    // Lists everything available: Auto, Default, then every valid .json asset from the configured file's folder and the default folder, sorted by name.
    public static IReadOnlyList<WakeWordAssetDescriptor> Discover(string workspaceRoot, JarvisOptions options)
    {
        var discovered = new List<WakeWordAssetDescriptor>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            NormalizeStoredPath(string.Empty),
            AutoSelectionPath
        };
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var configuredPath = NormalizeStoredPath(options.SpeechRecognitionWakeWordAssetPath);

        if (!string.IsNullOrWhiteSpace(configuredPath) && !IsAutoSelectionPath(configuredPath))
        {
            var configuredAbsolutePath = WakeWordPaths.ResolveAbsolutePath(configuredPath, workspaceRoot);
            TryAdd(configuredPath, configuredAbsolutePath, discovered, seen);

            var configuredDirectory = Path.GetDirectoryName(configuredAbsolutePath);

            if (!string.IsNullOrWhiteSpace(configuredDirectory))
            {
                directories.Add(configuredDirectory);
            }
        }

        directories.Add(WakeWordPaths.ResolveAbsolutePath(DefaultRelativeDirectory, workspaceRoot));

        foreach (var directory in directories.Where(Directory.Exists))
        {
            foreach (var filePath in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
            {
                var storedPath = WakeWordPaths.ToStoredPath(workspaceRoot, filePath);
                TryAdd(storedPath, filePath, discovered, seen);
            }
        }

        return [AutoSelection, RecognizerDefault, .. discovered
            .Where(asset => !asset.IsRecognizerDefault)
            .OrderBy(asset => asset.BuildShortLabel(), StringComparer.OrdinalIgnoreCase)];
    }

    // Returns the asset the settings point to. Auto picks the best one for the current microphone; an empty setting means none (null).
    public static WakeWordAssetDescriptor? ResolveConfiguredAsset(JarvisOptions options)
    {
        var storedPath = NormalizeStoredPath(options.SpeechRecognitionWakeWordAssetPath);

        if (IsAutoSelectionPath(storedPath))
        {
            return ResolveAutoAsset(options);
        }

        if (string.IsNullOrWhiteSpace(storedPath))
        {
            return null;
        }

        var absolutePath = WakeWordPaths.ResolveAbsolutePath(storedPath, Directory.GetCurrentDirectory());
        return TryLoadDescriptor(storedPath, absolutePath);
    }

    // Like ResolveConfiguredAsset but for showing the selection in the UI: never null, falls back to the recogniser default.
    public static WakeWordAssetDescriptor ResolveSelectionDescriptor(JarvisOptions options)
    {
        var storedPath = NormalizeStoredPath(options.SpeechRecognitionWakeWordAssetPath);

        if (IsAutoSelectionPath(storedPath))
        {
            return AutoSelection;
        }

        if (string.IsNullOrWhiteSpace(storedPath))
        {
            return RecognizerDefault;
        }

        var absolutePath = WakeWordPaths.ResolveAbsolutePath(storedPath, Directory.GetCurrentDirectory());
        return TryLoadDescriptor(storedPath, absolutePath) ?? RecognizerDefault;
    }

    // True if the setting is the word "auto".
    public static bool IsAutoSelectionPath(string? path) =>
        string.Equals(NormalizeStoredPath(path), AutoSelectionPath, StringComparison.OrdinalIgnoreCase);

    // Trims and uses backslashes so the same path is always spelled the same way.
    public static string NormalizeStoredPath(string? path)
    {
        return string.IsNullOrWhiteSpace(path)
            ? string.Empty
            : path.Trim().Replace('/', '\\');
    }

    // Chooses the best asset for the current microphone by score. If none scores above zero, prefers one whose preferred
    // beamforming/DSP profile names match, then alphabetical.
    private static WakeWordAssetDescriptor? ResolveAutoAsset(JarvisOptions options)
    {
        var discovered = Discover(Directory.GetCurrentDirectory(), options)
            .Where(asset => !asset.IsRecognizerDefault && !asset.IsAutomaticSelection)
            .ToArray();

        if (discovered.Length == 0)
        {
            return null;
        }

        var beamformingProfile = BeamformingProfileCatalog.ResolveAuto(options.SpeechRecognitionDeviceName);
        var hardwareDspProfile = HardwareDspProfileCatalog.ResolveAuto(options.SpeechRecognitionDeviceName, beamformingProfile);

        var bestMatch = discovered
            .Select(asset => new
            {
                Asset = asset,
                Score = ScoreAutoSelection(asset, options.SpeechRecognitionDeviceName, beamformingProfile.Name, hardwareDspProfile.Name)
            })
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Asset.BuildShortLabel(), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        if (bestMatch is null || bestMatch.Score <= 0)
        {
            return discovered
                .OrderByDescending(asset =>
                    string.Equals(asset.PreferredBeamformingProfile, beamformingProfile.Name, StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                .ThenByDescending(asset =>
                    string.Equals(asset.PreferredHardwareDspProfile, hardwareDspProfile.Name, StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                .ThenBy(asset => asset.BuildShortLabel(), StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }

        return bestMatch.Asset;
    }

    // Adds an asset to the list unless that path was already seen or the file cannot be read.
    private static void TryAdd(
        string storedPath,
        string absolutePath,
        ICollection<WakeWordAssetDescriptor> discovered,
        ISet<string> seen)
    {
        var normalizedPath = NormalizeStoredPath(storedPath);

        if (!seen.Add(normalizedPath))
        {
            return;
        }

        var asset = TryLoadDescriptor(normalizedPath, absolutePath);

        if (asset is null)
        {
            return;
        }

        discovered.Add(asset);
    }

    // Reads one JSON manifest into an asset, using the cache while the file is unchanged. Any read/parse error gives null (asset ignored).
    private static WakeWordAssetDescriptor? TryLoadDescriptor(string storedPath, string absolutePath)
    {
        if (!File.Exists(absolutePath))
        {
            return null;
        }

        var lastWriteUtc = File.GetLastWriteTimeUtc(absolutePath);

        lock (Sync)
        {
            if (Cache.TryGetValue(absolutePath, out var cached)
                && cached.LastWriteUtc == lastWriteUtc)
            {
                return cached.Asset;
            }
        }

        try
        {
            var json = File.ReadAllText(absolutePath);
            var manifest = JsonSerializer.Deserialize<WakeWordAssetManifest>(json, JsonOptions);

            if (manifest is null)
            {
                return null;
            }

            var descriptor = new WakeWordAssetDescriptor(
                Path: storedPath,
                Name: string.IsNullOrWhiteSpace(manifest.Name)
                    ? System.IO.Path.GetFileNameWithoutExtension(absolutePath)
                    : manifest.Name.Trim(),
                DisplayName: string.IsNullOrWhiteSpace(manifest.DisplayName)
                    ? System.IO.Path.GetFileNameWithoutExtension(absolutePath)
                    : manifest.DisplayName.Trim(),
                Description: manifest.Description?.Trim() ?? string.Empty,
                WakePhrases: WakeWordPaths.NormalizeValues(manifest.WakePhrases),
                Aliases: WakeWordPaths.NormalizeValues(manifest.Aliases),
                MinimumConfidence: manifest.MinimumConfidence,
                // Sensitivity bias is limited to -0.20..+0.20.
                SensitivityBias: Math.Clamp(manifest.SensitivityBias ?? 0, -0.20, 0.20),
                PreferredBeamformingProfile: manifest.PreferredBeamformingProfile?.Trim() ?? string.Empty,
                PreferredHardwareDspProfile: manifest.PreferredHardwareDspProfile?.Trim() ?? string.Empty,
                PreferredWakeEnginePath: manifest.PreferredWakeEnginePath?.Trim() ?? string.Empty,
                DeviceKeywords: WakeWordPaths.NormalizeValues(manifest.DeviceKeywords),
                ExcludedDeviceKeywords: WakeWordPaths.NormalizeValues(manifest.ExcludedDeviceKeywords));

            lock (Sync)
            {
                Cache[absolutePath] = new CachedWakeWordAsset(lastWriteUtc, descriptor);
            }

            return descriptor;
        }
        catch
        {
            return null;
        }
    }

    // A cache entry: the asset and the file's modification time when it was read.
    private sealed record CachedWakeWordAsset(DateTime LastWriteUtc, WakeWordAssetDescriptor? Asset);

    // Auto-selection score: device keyword score, +22 if the asset's preferred beamforming profile matches, +18 for the DSP profile.
    // If still not positive, a name containing "desktop", "headset" or "conference/far field" earns +8 when the microphone profile is the same kind.
    private static int ScoreAutoSelection(
        WakeWordAssetDescriptor asset,
        string? deviceName,
        string beamformingProfileName,
        string hardwareDspProfileName)
    {
        var score = asset.ScoreDeviceMatch(deviceName);

        if (score == int.MinValue)
        {
            return score;
        }

        if (!string.IsNullOrWhiteSpace(asset.PreferredBeamformingProfile)
            && string.Equals(asset.PreferredBeamformingProfile, beamformingProfileName, StringComparison.OrdinalIgnoreCase))
        {
            score += 22;
        }

        if (!string.IsNullOrWhiteSpace(asset.PreferredHardwareDspProfile)
            && string.Equals(asset.PreferredHardwareDspProfile, hardwareDspProfileName, StringComparison.OrdinalIgnoreCase))
        {
            score += 18;
        }

        if (score <= 0 && !string.IsNullOrWhiteSpace(deviceName))
        {
            if (asset.BuildShortLabel().Contains("desktop", StringComparison.OrdinalIgnoreCase)
                && beamformingProfileName == "desktop")
            {
                score += 8;
            }
            else if (asset.BuildShortLabel().Contains("headset", StringComparison.OrdinalIgnoreCase)
                     && beamformingProfileName == "headset")
            {
                score += 8;
            }
            else if ((asset.BuildShortLabel().Contains("conference", StringComparison.OrdinalIgnoreCase)
                      || asset.BuildShortLabel().Contains("far field", StringComparison.OrdinalIgnoreCase))
                     && (beamformingProfileName == "conference" || beamformingProfileName == "far-field-array"))
            {
                score += 8;
            }
        }

        return score;
    }

    // The JSON shape of an asset file. Missing fields get the safe defaults below.
    private sealed record WakeWordAssetManifest
    {
        public string Name { get; init; } = string.Empty;

        public string DisplayName { get; init; } = string.Empty;

        public string Description { get; init; } = string.Empty;

        public string[] WakePhrases { get; init; } = Array.Empty<string>();

        public string[] Aliases { get; init; } = Array.Empty<string>();

        public double? MinimumConfidence { get; init; }

        public double? SensitivityBias { get; init; }

        public string PreferredBeamformingProfile { get; init; } = string.Empty;

        public string PreferredHardwareDspProfile { get; init; } = string.Empty;

        public string PreferredWakeEnginePath { get; init; } = string.Empty;

        public string[] DeviceKeywords { get; init; } = Array.Empty<string>();

        public string[] ExcludedDeviceKeywords { get; init; } = Array.Empty<string>();
    }
}

// Path helpers for asset files.
internal static class WakeWordPaths
{
    // Relative paths are resolved against baseDirectory; absolute ones are kept.
    public static string ResolveAbsolutePath(string path, string baseDirectory)
    {
        return System.IO.Path.IsPathRooted(path)
            ? System.IO.Path.GetFullPath(path)
            : System.IO.Path.GetFullPath(System.IO.Path.Combine(baseDirectory, path));
    }

    // Makes a path relative to the workspace for saving in settings, falling back to the absolute path.
    public static string ToStoredPath(string workspaceRoot, string absolutePath)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            return absolutePath;
        }

        try
        {
            return WakeWordAssetCatalog.NormalizeStoredPath(System.IO.Path.GetRelativePath(workspaceRoot, absolutePath));
        }
        catch
        {
            return absolutePath;
        }
    }

    // Removes blanks and duplicates (ignoring case) from a list of strings from JSON.
    public static string[] NormalizeValues(IEnumerable<string>? values)
    {
        if (values is null)
        {
            return Array.Empty<string>();
        }

        return values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
