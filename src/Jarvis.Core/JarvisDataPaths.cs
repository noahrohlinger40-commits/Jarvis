namespace Jarvis.Core;

/// <summary>
/// Everything Jarvis writes at runtime (memory, transcripts, logs, captures, the Google client secret)
/// lives under %LOCALAPPDATA%\Jarvis, next to its model and speech engine. The project usually sits in
/// OneDrive, which would copy all of it to the cloud. The repo's own data folder keeps only shipped
/// assets such as the wake-word profiles.
/// </summary>
public static class JarvisDataPaths
{
    public static string Home { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Jarvis");

    /// <summary>"data/memory.json" becomes %LOCALAPPDATA%\Jarvis\data\memory.json; absolute paths are kept.</summary>
    public static string Resolve(string path) => Path.GetFullPath(Path.Combine(Home, path));
}
