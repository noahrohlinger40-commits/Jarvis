// Reads and writes jarvis.settings.json (the JarvisOptions object) in the workspace folder.
// Used by AppBootstrapper at startup and by the main form when the user changes settings.
using System.Text.Json;
using Jarvis.Core;

namespace Jarvis.App;

// Small static helper; no state besides two reusable JSON option sets.
internal static class JarvisSettingsLoader
{
    // Web defaults means camelCase property names in the file.
    private static readonly JsonSerializerOptions ReadOptions = new(JsonSerializerDefaults.Web);
    // Same defaults, but pretty-printed so the file stays easy for a person to edit.
    private static readonly JsonSerializerOptions WriteOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    // Returns the saved settings, or all defaults if the file is missing or empty.
    public static async Task<JarvisOptions> LoadAsync(string workspaceRoot, CancellationToken cancellationToken)
    {
        var filePath = Path.Combine(workspaceRoot, "jarvis.settings.json");

        if (!File.Exists(filePath))
        {
            return new JarvisOptions();
        }

        await using var stream = File.OpenRead(filePath);
        var options = await JsonSerializer.DeserializeAsync<JarvisOptions>(
            stream,
            ReadOptions,
            cancellationToken);

        return options ?? new JarvisOptions();
    }

    // Overwrites jarvis.settings.json with the given settings, creating the folder if needed.
    public static async Task SaveAsync(string workspaceRoot, JarvisOptions options, CancellationToken cancellationToken)
    {
        var filePath = Path.Combine(workspaceRoot, "jarvis.settings.json");
        var directory = Path.GetDirectoryName(filePath);

        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Write to a temporary file first and then swap it in, so a crash mid-write cannot leave a half-written settings file
        // that stops Jarvis from starting.
        var temp = filePath + ".tmp";

        await using (var stream = File.Create(temp))
        {
            await JsonSerializer.SerializeAsync(stream, options, WriteOptions, cancellationToken);
        }

        File.Move(temp, filePath, overwrite: true);
    }
}
