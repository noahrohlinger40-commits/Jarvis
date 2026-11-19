// WakeWordEngines.cs: "wake engines" are JSON files (default folder data\wake-engines) that describe an external program able to
// spot the wake word in audio, such as the openWakeWord "hey jarvis" detector: its executable, command-line template, model file,
// timeout and confidence tweaks. This file finds and loads (with a cache) them, in the same way WakeWordAssets.cs does for assets.
// WakeWordDetector uses the chosen engine to build its external-process back end; if none fits, there is no wake detector.
using System.Text.Json;
using Jarvis.Core;

namespace Jarvis.App;

// One loaded wake engine: all fields from its JSON file, plus where the file is. Empty path = no engine.
internal sealed record WakeWordEngineDescriptor(
    string Path,
    string DefinitionDirectory,
    string Name,
    string DisplayName,
    string Description,
    string EngineKind,
    string ExecutablePath,
    string Arguments,
    string WorkingDirectory,
    string ModelPath,
    string AccessKey,
    int CommandTimeoutMilliseconds,
    double? MinimumConfidence,
    double SensitivityBias,
    string PreferredBeamformingProfile,
    string PreferredHardwareDspProfile,
    string[] PreferredWakeAssetPaths,
    IReadOnlyDictionary<string, string> EnvironmentVariables)
{
    // True for the built-in "no external engine" entry.
    public bool IsRecognizerDefault => string.IsNullOrWhiteSpace(Path);

    // True when a real external program should be run (not the default, and the kind is not "recognizer").
    public bool UsesExternalProcess =>
        !IsRecognizerDefault
        && !string.Equals(NormalizeEngineKind(EngineKind), "recognizer", StringComparison.Ordinal);

    // Name to show in menus.
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

        return "Recognizer Wake";
    }

    // No kind in the file means an external program.
    private static string NormalizeEngineKind(string? engineKind) =>
        string.IsNullOrWhiteSpace(engineKind)
            ? "external-process"
            : engineKind.Trim().ToLowerInvariant();
}

// Finds, loads and caches wake engine descriptions.
internal static class WakeWordEngineCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    // Lock for the cache below.
    private static readonly object Sync = new();
    // Parsed engines keyed by file path, reloaded when the file changes on disk.
    private static readonly Dictionary<string, CachedWakeWordEngine> Cache = new(StringComparer.OrdinalIgnoreCase);
    // Where engine files live by default, relative to the app folder.
    private const string DefaultRelativeDirectory = "data\\wake-engines";
    // Old settings files may still say "auto"; it now means the first engine file found.
    public const string AutoSelectionPath = "auto";

    // The "no engine" entry. Command timeout default 1800 ms (1.8 s).
    public static WakeWordEngineDescriptor RecognizerDefault { get; } =
        new(
            Path: string.Empty,
            DefinitionDirectory: string.Empty,
            Name: "recognizer-default",
            DisplayName: "Recognizer Wake",
            Description: "Run no wake engine; whisper's transcript decides.",
            EngineKind: "recognizer",
            ExecutablePath: string.Empty,
            Arguments: string.Empty,
            WorkingDirectory: string.Empty,
            ModelPath: string.Empty,
            AccessKey: string.Empty,
            CommandTimeoutMilliseconds: 1800,
            MinimumConfidence: null,
            SensitivityBias: 0,
            PreferredBeamformingProfile: string.Empty,
            PreferredHardwareDspProfile: string.Empty,
            PreferredWakeAssetPaths: Array.Empty<string>(),
            EnvironmentVariables: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    // Lists everything available: Default, then each valid .json engine from the configured file's folder and the default folder.
    public static IReadOnlyList<WakeWordEngineDescriptor> Discover(string workspaceRoot, JarvisOptions options)
    {
        var discovered = new List<WakeWordEngineDescriptor>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            NormalizeStoredPath(string.Empty)
        };
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var configuredPath = NormalizeStoredPath(options.SpeechRecognitionWakeEnginePath);

        if (!string.IsNullOrWhiteSpace(configuredPath) && !IsAutoSelectionPath(configuredPath))
        {
            var configuredAbsolutePath = WakeWordPaths.ResolveAbsolutePath(configuredPath, workspaceRoot);
            TryAdd(configuredPath, configuredAbsolutePath, workspaceRoot, discovered, seen);

            var configuredDirectory = System.IO.Path.GetDirectoryName(configuredAbsolutePath);

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
                TryAdd(storedPath, filePath, workspaceRoot, discovered, seen);
            }
        }

        return [RecognizerDefault, .. discovered
            .Where(engine => !engine.IsRecognizerDefault)
            .OrderBy(engine => engine.BuildShortLabel(), StringComparer.OrdinalIgnoreCase)];
    }

    // Loads the engine file at a path, or if that fails looks for one with a matching file name among the discovered engines;
    // no engine is the last fallback.
    public static WakeWordEngineDescriptor ResolveDescriptorByPath(string workspaceRoot, JarvisOptions options, string? storedPath)
    {
        var normalizedStoredPath = NormalizeStoredPath(storedPath);

        if (string.IsNullOrWhiteSpace(normalizedStoredPath))
        {
            return RecognizerDefault;
        }

        var absolutePath = WakeWordPaths.ResolveAbsolutePath(normalizedStoredPath, workspaceRoot);
        var direct = TryLoadDescriptor(normalizedStoredPath, absolutePath, workspaceRoot);

        if (direct is not null)
        {
            return direct;
        }

        return Discover(workspaceRoot, options)
            .FirstOrDefault(engine => StoredPathsMatch(engine.Path, normalizedStoredPath))
            ?? RecognizerDefault;
    }

    // True if the setting is the word "auto".
    public static bool IsAutoSelectionPath(string? path) =>
        string.Equals(NormalizeStoredPath(path), AutoSelectionPath, StringComparison.OrdinalIgnoreCase);

    // Trims and uses backslashes so one path always has one spelling.
    public static string NormalizeStoredPath(string? path)
    {
        return string.IsNullOrWhiteSpace(path)
            ? string.Empty
            : path.Trim().Replace('/', '\\');
    }

    // Two stored paths are the same if they are equal, or end in the same file name (the folder may have moved).
    public static bool StoredPathsMatch(string? left, string? right)
    {
        var normalizedLeft = NormalizeStoredPath(left);
        var normalizedRight = NormalizeStoredPath(right);

        if (string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(normalizedLeft) || string.IsNullOrWhiteSpace(normalizedRight))
        {
            return false;
        }

        var leftFileName = System.IO.Path.GetFileName(normalizedLeft);
        var rightFileName = System.IO.Path.GetFileName(normalizedRight);
        return string.Equals(leftFileName, rightFileName, StringComparison.OrdinalIgnoreCase);
    }

    // Adds an engine to the list unless the path was already seen or the file cannot be read.
    private static void TryAdd(
        string storedPath,
        string absolutePath,
        string workspaceRoot,
        ICollection<WakeWordEngineDescriptor> discovered,
        ISet<string> seen)
    {
        var normalizedPath = NormalizeStoredPath(storedPath);

        if (!seen.Add(normalizedPath))
        {
            return;
        }

        var descriptor = TryLoadDescriptor(normalizedPath, absolutePath, workspaceRoot);

        if (descriptor is null)
        {
            return;
        }

        discovered.Add(descriptor);
    }

    // Reads one JSON manifest into a descriptor, reusing the cache while the file is unchanged. Errors give null (engine ignored).
    private static WakeWordEngineDescriptor? TryLoadDescriptor(string storedPath, string absolutePath, string workspaceRoot)
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
                return cached.Descriptor;
            }
        }

        try
        {
            var json = File.ReadAllText(absolutePath);
            var manifest = JsonSerializer.Deserialize<WakeWordEngineManifest>(json, JsonOptions);

            if (manifest is null)
            {
                return null;
            }

            var descriptor = new WakeWordEngineDescriptor(
                Path: storedPath,
                DefinitionDirectory: System.IO.Path.GetDirectoryName(absolutePath) ?? workspaceRoot,
                Name: string.IsNullOrWhiteSpace(manifest.Name)
                    ? System.IO.Path.GetFileNameWithoutExtension(absolutePath)
                    : manifest.Name.Trim(),
                DisplayName: string.IsNullOrWhiteSpace(manifest.DisplayName)
                    ? System.IO.Path.GetFileNameWithoutExtension(absolutePath)
                    : manifest.DisplayName.Trim(),
                Description: manifest.Description?.Trim() ?? string.Empty,
                EngineKind: manifest.EngineKind?.Trim() ?? "external-process",
                ExecutablePath: manifest.ExecutablePath?.Trim() ?? string.Empty,
                Arguments: manifest.Arguments?.Trim() ?? string.Empty,
                WorkingDirectory: manifest.WorkingDirectory?.Trim() ?? string.Empty,
                ModelPath: manifest.ModelPath?.Trim() ?? string.Empty,
                AccessKey: manifest.AccessKey?.Trim() ?? string.Empty,
                // Timeout clamped to 250 ms - 15 s; sensitivity bias to -0.20..+0.20.
                CommandTimeoutMilliseconds: Math.Clamp(manifest.CommandTimeoutMilliseconds ?? 1800, 250, 15000),
                MinimumConfidence: manifest.MinimumConfidence,
                SensitivityBias: Math.Clamp(manifest.SensitivityBias ?? 0, -0.20, 0.20),
                PreferredBeamformingProfile: manifest.PreferredBeamformingProfile?.Trim() ?? string.Empty,
                PreferredHardwareDspProfile: manifest.PreferredHardwareDspProfile?.Trim() ?? string.Empty,
                PreferredWakeAssetPaths: NormalizeStoredPaths(
                    manifest.PreferredWakeAssetPaths,
                    absolutePath,
                    workspaceRoot),
                EnvironmentVariables: NormalizeEnvironmentVariables(manifest.EnvironmentVariables));

            lock (Sync)
            {
                Cache[absolutePath] = new CachedWakeWordEngine(lastWriteUtc, descriptor);
            }

            return descriptor;
        }
        catch
        {
            return null;
        }
    }

    // Trims names and values of the environment variables passed to the external program.
    private static IReadOnlyDictionary<string, string> NormalizeEnvironmentVariables(
        IReadOnlyDictionary<string, string>? values)
    {
        if (values is null || values.Count == 0)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in values)
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
            {
                continue;
            }

            normalized[pair.Key.Trim()] = pair.Value?.Trim() ?? string.Empty;
        }

        return normalized;
    }

    // Turns asset paths listed in the engine file into the same stored form used elsewhere (relative to the workspace when the file exists).
    private static string[] NormalizeStoredPaths(
        IEnumerable<string>? values,
        string manifestAbsolutePath,
        string workspaceRoot)
    {
        if (values is null)
        {
            return Array.Empty<string>();
        }

        var manifestDirectory = System.IO.Path.GetDirectoryName(manifestAbsolutePath) ?? workspaceRoot;
        var normalized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var trimmed = NormalizeStoredPath(value);

            if (System.IO.Path.IsPathRooted(trimmed))
            {
                normalized.Add(WakeWordPaths.ToStoredPath(workspaceRoot, System.IO.Path.GetFullPath(trimmed)));
                continue;
            }

            var manifestRelativePath = System.IO.Path.GetFullPath(System.IO.Path.Combine(manifestDirectory, trimmed));

            if (File.Exists(manifestRelativePath))
            {
                normalized.Add(WakeWordPaths.ToStoredPath(workspaceRoot, manifestRelativePath));
                continue;
            }

            normalized.Add(trimmed);
        }

        return normalized.ToArray();
    }

    // Cache entry: the descriptor and the file's modification time when it was read.
    private sealed record CachedWakeWordEngine(DateTime LastWriteUtc, WakeWordEngineDescriptor? Descriptor);

    // The JSON shape of an engine file; missing fields get these safe defaults.
    private sealed record WakeWordEngineManifest
    {
        public string Name { get; init; } = string.Empty;

        public string DisplayName { get; init; } = string.Empty;

        public string Description { get; init; } = string.Empty;

        public string EngineKind { get; init; } = "external-process";

        public string ExecutablePath { get; init; } = string.Empty;

        public string Arguments { get; init; } = string.Empty;

        public string WorkingDirectory { get; init; } = string.Empty;

        public string ModelPath { get; init; } = string.Empty;

        public string AccessKey { get; init; } = string.Empty;

        public int? CommandTimeoutMilliseconds { get; init; }

        public double? MinimumConfidence { get; init; }

        public double? SensitivityBias { get; init; }

        public string PreferredBeamformingProfile { get; init; } = string.Empty;

        public string PreferredHardwareDspProfile { get; init; } = string.Empty;

        public string[] PreferredWakeAssetPaths { get; init; } = Array.Empty<string>();

        public Dictionary<string, string> EnvironmentVariables { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
