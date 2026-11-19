// WakeWordAssets.cs: "wake word assets" are small JSON files (default folder data\wake-word-assets) describing a tuned wake
// word setup: the phrases and spelling variants to accept, a confidence cutoff, a sensitivity tweak, which microphone types
// it suits, and an optional detector model path. This file finds and loads them (with a cache).
// Used by WakeWordDetector, WakeWordEngines, VoiceRecognitionText and the settings UI. The asset is always the one named in the settings.
using System.Text.Json;
using Jarvis.Core;

namespace Jarvis.App;

// One loaded wake word asset: everything from its JSON file plus the file path. An empty path means "no asset".
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
    string PreferredWakeEnginePath)
{
    // True for the built-in "no dedicated asset" entry.
    public bool IsRecognizerDefault => string.IsNullOrWhiteSpace(Path);

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
    // Old settings files may still say "auto"; it now means the first asset file found.
    public const string AutoSelectionPath = "auto";

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
            PreferredWakeEnginePath: string.Empty);

    // Lists everything available: Default, then every valid .json asset from the configured file's folder and the default folder, sorted by name.
    public static IReadOnlyList<WakeWordAssetDescriptor> Discover(string workspaceRoot, JarvisOptions options)
    {
        var discovered = new List<WakeWordAssetDescriptor>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            NormalizeStoredPath(string.Empty)
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

        return [RecognizerDefault, .. discovered
            .Where(asset => !asset.IsRecognizerDefault)
            .OrderBy(asset => asset.BuildShortLabel(), StringComparer.OrdinalIgnoreCase)];
    }

    // Returns the asset the settings point to. An empty setting means none (null); an old "auto" setting means the first asset file found.
    public static WakeWordAssetDescriptor? ResolveConfiguredAsset(JarvisOptions options)
    {
        var storedPath = NormalizeStoredPath(options.SpeechRecognitionWakeWordAssetPath);

        if (IsAutoSelectionPath(storedPath))
        {
            return Discover(Directory.GetCurrentDirectory(), options).FirstOrDefault(asset => !asset.IsRecognizerDefault);
        }

        if (string.IsNullOrWhiteSpace(storedPath))
        {
            return null;
        }

        var absolutePath = WakeWordPaths.ResolveAbsolutePath(storedPath, Directory.GetCurrentDirectory());
        return TryLoadDescriptor(storedPath, absolutePath);
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
                PreferredWakeEnginePath: manifest.PreferredWakeEnginePath?.Trim() ?? string.Empty);

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
