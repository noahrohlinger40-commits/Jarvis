// DesktopAutomation.cs: the main slice of DesktopAutomationService, the class behind every "computer" tool Jarvis can call
// (open/launch apps, files, windows, clipboard, notifications, system status). The model's tool calls arrive here through
// IDesktopAutomationService. Window/keyboard/clipboard work is handed to Win32UiAutomation; file and app lookup lives here.
// Other slices of the same class: DesktopAutomation.AppIndex.cs (installed-app catalog) and DesktopAutomation.Workflows.cs.
using System.Text.RegularExpressions;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;

namespace Jarvis.Core;

// The list of everything Jarvis can do to the desktop. Tools call this interface instead of the concrete class, so tests can swap in a fake.
public interface IDesktopAutomationService
{
    Task<string> GetSystemStatusAsync(CancellationToken cancellationToken);

    Task<string> GetComputerContextAsync(CancellationToken cancellationToken);

    Task<DesktopContextSnapshot> GetContextSnapshotAsync(CancellationToken cancellationToken);

    Task<string> GetLocalNetworkStatusAsync(string target, CancellationToken cancellationToken);

    Task<string> ListApplicationsAsync(string target, CancellationToken cancellationToken);

    Task<string> OpenApplicationAsync(string target, CancellationToken cancellationToken);

    Task<string> LaunchApplicationAsync(string target, string arguments, CancellationToken cancellationToken);

    Task<string> OpenPathAsync(string target, CancellationToken cancellationToken);

    Task<string> ListFilesAsync(string target, CancellationToken cancellationToken);

    Task<string> ReadFilePreviewAsync(string target, CancellationToken cancellationToken);

    Task<string> FindFilesAsync(string target, CancellationToken cancellationToken);

    Task<string> WriteFileAsync(string target, string content, bool append, CancellationToken cancellationToken);

    Task<string> ReplaceInFileAsync(string target, string findText, string replaceText, CancellationToken cancellationToken);

    Task<string> MovePathAsync(string source, string destination, CancellationToken cancellationToken);

    Task<string> CopyPathAsync(string source, string destination, CancellationToken cancellationToken);

    Task<string> DeletePathAsync(string target, CancellationToken cancellationToken);

    Task<string> BrowseAsync(string target, CancellationToken cancellationToken);

    Task<string> SearchWebAsync(string query, CancellationToken cancellationToken);

    Task<string> GetApplicationStateAsync(string target, CancellationToken cancellationToken);

    Task<string> ListOpenWindowsAsync(string target, CancellationToken cancellationToken);

    Task<string> FocusApplicationAsync(string targetApp, CancellationToken cancellationToken);

    Task<string> CloseApplicationAsync(string targetApp, CancellationToken cancellationToken);

    Task<string> SetWindowStateAsync(string targetApp, string state, CancellationToken cancellationToken);

    Task<string> MoveWindowAsync(string targetApp, int x, int y, CancellationToken cancellationToken);

    Task<string> ResizeWindowAsync(string targetApp, int width, int height, CancellationToken cancellationToken);

    Task<string> SnapWindowAsync(string targetApp, string position, CancellationToken cancellationToken);

    Task<string> SendHotkeyAsync(string targetApp, string hotkey, CancellationToken cancellationToken);

    Task<string> ClickElementAsync(string targetApp, string elementName, CancellationToken cancellationToken);

    Task<string> TypeIntoElementAsync(string targetApp, string elementName, string text, CancellationToken cancellationToken);

    Task<string> SendTextAsync(string targetApp, string text, CancellationToken cancellationToken);

    Task<string> GetElementTextAsync(string targetApp, string elementName, CancellationToken cancellationToken);

    Task<string> ScrollWindowAsync(string targetApp, string direction, CancellationToken cancellationToken);

    Task<string> ControlMediaAsync(string command, CancellationToken cancellationToken);

    Task<string> GetClipboardAsync(CancellationToken cancellationToken);

    Task<string> SetClipboardAsync(string text, CancellationToken cancellationToken);

    Task<string> ClearClipboardAsync(CancellationToken cancellationToken);

    Task<string> PasteClipboardAsync(string targetApp, CancellationToken cancellationToken);

    Task<string> SendNotificationAsync(string title, string message, CancellationToken cancellationToken);

    Task<string> WaitAsync(double seconds, CancellationToken cancellationToken);
}

// The real implementation of IDesktopAutomationService. Declared partial: this file holds the public tool methods and file search.
[SupportedOSPlatform("windows")]
public sealed partial class DesktopAutomationService : IDesktopAutomationService
{
    // Win32 ShowWindow command number 9 = SW_RESTORE: bring a minimised window back to its normal size.
    private const int SwRestore = 9;
    // File types the shell can run when the user asks to open a launcher: shortcuts, internet shortcuts, ClickOnce links, programs, scripts and Microsoft Management Console snap-ins.
    private static readonly string[] DesktopLaunchableExtensions =
    [
        ".lnk",
        ".url",
        ".appref-ms",
        ".exe",
        ".bat",
        ".cmd",
        ".msc"
    ];

    // Spoken app names mapped to what Windows should run. Values that end in ":" are URI schemes (the app registers itself with Windows); bare names like "chrome" are resolved by the shell.
    // "spots" is here because speech recognition tends to hear "Spotify" that way.
    private static readonly IReadOnlyDictionary<string, string> BuiltInAppAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["notepad"] = "notepad.exe",
            ["calculator"] = "calc.exe",
            ["calc"] = "calc.exe",
            ["paint"] = "mspaint.exe",
            ["file explorer"] = "explorer.exe",
            ["explorer"] = "explorer.exe",
            ["task manager"] = "taskmgr.exe",
            ["powershell"] = "powershell.exe",
            ["command prompt"] = "cmd.exe",
            ["cmd"] = "cmd.exe",
            ["terminal"] = "wt.exe",
            ["windows terminal"] = "wt.exe",
            ["settings"] = "ms-settings:",
            ["spots"] = "spotify:",
            ["spotify"] = "spotify:",
            ["spotify app"] = "spotify:",
            ["music"] = "spotify:",
            ["music app"] = "spotify:",
            ["visual studio code"] = "code",
            ["vs code"] = "code",
            ["vscode"] = "code",
            ["code"] = "code",
            ["chrome"] = "chrome",
            ["google chrome"] = "chrome",
            ["edge"] = "msedge",
            ["microsoft edge"] = "msedge",
            ["firefox"] = "firefox",
            ["brave"] = "brave",
            ["discord"] = "discord",
            ["slack"] = "slack",
            ["steam"] = "steam",
            ["obsidian"] = "obsidian",
            ["word"] = "winword",
            ["excel"] = "excel",
            ["powerpoint"] = "powerpnt",
            ["outlook"] = "outlook",
            ["teams"] = "ms-teams",
            ["pycharm"] = "pycharm64.exe",
            ["rider"] = "rider64.exe"
        };

    // Site names the browse tool opens in the user's default browser (Jarvis's own Chrome profile is signed in to nothing),
    // so "open Discord in the browser" / "pull up my LinkedIn messages" go to the site, not a Bing search. Outlook is left out on purpose.
    // Settings can add or override entries under "siteAliases", same shape as "applicationAliases".
    private static readonly IReadOnlyDictionary<string, string> BuiltInSiteAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["teams"] = "https://teams.microsoft.com/",
            ["discord"] = "https://discord.com/channels/@me",
            ["linkedin"] = "https://www.linkedin.com/",
            ["linkedin messages"] = "https://www.linkedin.com/messaging/",
            ["notion"] = "https://www.notion.so/",
            ["canvas"] = "https://canvas.fsu.edu/",
            ["github"] = "https://github.com/",
            ["figma"] = "https://www.figma.com/"
        };

    // App and site name maps after merging the built-ins above with any extra entries from the user's settings file.
    private readonly IReadOnlyDictionary<string, string> _appAliases;
    private readonly IReadOnlyDictionary<string, string> _siteAliases;
    // Folder the Jarvis project is running from; shown in status and always included in file searches.
    private readonly string _workspaceRoot;
    // Folders that find-files is allowed to walk (see DefaultFileSearchRoots).
    private readonly string[] _fileSearchRoots;

    // Folder names never walked by find files: build output and package caches hide the real match and cost seconds.
    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase) { ".git", "node_modules", ".venv", "__pycache__" };

    // The methods below are thin wrappers: check the cancellation token, then pass the request to Win32UiAutomation, which does the actual window/keyboard work.
    public Task<string> ClickElementAsync(string targetApp, string elementName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Win32UiAutomation.ClickElementAsync(targetApp, elementName);
    }

    public Task<string> FocusApplicationAsync(string targetApp, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Win32UiAutomation.FocusWindowAsync(targetApp);
    }

    public Task<string> CloseApplicationAsync(string targetApp, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Win32UiAutomation.CloseWindowAsync(targetApp);
    }

    public Task<string> SetWindowStateAsync(string targetApp, string state, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Win32UiAutomation.SetWindowStateAsync(targetApp, state);
    }

    public Task<string> ListOpenWindowsAsync(string target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Win32UiAutomation.ListWindowsAsync(target);
    }

    public Task<string> MoveWindowAsync(string targetApp, int x, int y, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Win32UiAutomation.MoveWindowAsync(targetApp, x, y);
    }

    public Task<string> ResizeWindowAsync(string targetApp, int width, int height, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Win32UiAutomation.ResizeWindowAsync(targetApp, width, height);
    }

    public Task<string> SnapWindowAsync(string targetApp, string position, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Win32UiAutomation.SnapWindowAsync(targetApp, position);
    }

    public Task<string> SendHotkeyAsync(string targetApp, string hotkey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Win32UiAutomation.SendHotkeyAsync(targetApp, hotkey);
    }

    public Task<string> TypeIntoElementAsync(string targetApp, string elementName, string text, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Win32UiAutomation.TypeIntoElementAsync(targetApp, elementName, text);
    }

    public Task<string> SendTextAsync(string targetApp, string text, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Win32UiAutomation.SendTextAsync(targetApp, text);
    }

    public Task<string> GetElementTextAsync(string targetApp, string elementName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Win32UiAutomation.GetElementTextAsync(targetApp, elementName);
    }

    public Task<string> ScrollWindowAsync(string targetApp, string direction, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Win32UiAutomation.ScrollWindowAsync(targetApp, direction);
    }

    public Task<string> ControlMediaAsync(string command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Win32UiAutomation.ControlMediaAsync(command);
    }

    public Task<string> GetClipboardAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Win32UiAutomation.GetClipboardTextAsync();
    }

    public Task<string> SetClipboardAsync(string text, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Win32UiAutomation.SetClipboardTextAsync(text);
    }

    public Task<string> ClearClipboardAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Win32UiAutomation.ClearClipboardAsync();
    }

    public Task<string> PasteClipboardAsync(string targetApp, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Win32UiAutomation.PasteClipboardAsync(targetApp);
    }

    // Waiting does not need the cancellation check up front; Win32UiAutomation.WaitAsync observes the token itself while it sleeps.
    public Task<string> WaitAsync(double seconds, CancellationToken cancellationToken)
    {
        return Win32UiAutomation.WaitAsync(seconds, cancellationToken);
    }

    // Builds the service: merges the built-in alias tables with the user's settings and works out which folders file search may walk.
    public DesktopAutomationService(
        string workspaceRoot,
        IReadOnlyDictionary<string, string>? applicationAliases = null,
        IReadOnlyDictionary<string, string>? siteAliases = null,
        IEnumerable<string>? fileSearchRoots = null)
    {
        _workspaceRoot = workspaceRoot;
        _appAliases = MergeAliases(BuiltInAppAliases, applicationAliases);
        _siteAliases = MergeAliases(BuiltInSiteAliases, siteAliases);
        _fileSearchRoots = DefaultFileSearchRoots(fileSearchRoots, workspaceRoot);
    }

    // Folders the file search walks, so "open the report 4" finds a report on the Desktop: the settings list "fileSearchRoots", or by default
    // Desktop, OneDrive Desktop, Documents and Downloads, plus the workspace. Only folders that exist, no duplicates.
    private static string[] DefaultFileSearchRoots(IEnumerable<string>? configured, string workspaceRoot)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = configured?.Where(root => !string.IsNullOrWhiteSpace(root)).Select(root => root.Trim()).ToArray() ?? [];
        if (roots.Length == 0)
        {
            roots =
            [
                Path.Combine(profile, "Desktop"),
                Path.Combine(profile, "OneDrive", "Desktop"),
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                Path.Combine(profile, "Downloads")
            ];
        }

        // Environment variables like %USERPROFILE% in configured paths are expanded before checking the folder exists.
        return roots.Append(workspaceRoot)
            .Select(root => Environment.ExpandEnvironmentVariables(root))
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    // Maps number words (and the words speech-to-text mistakes them for, like "for" and "to") to digits, used by FindFiles.
    // Speech hears "report 4" as "report for", so a spoken number also matches its digit.
    private static readonly Dictionary<string, string> SpokenDigits = new(StringComparer.OrdinalIgnoreCase)
    {
        ["one"] = "1", ["won"] = "1", ["two"] = "2", ["to"] = "2", ["too"] = "2", ["three"] = "3", ["four"] = "4", ["for"] = "4",
        ["five"] = "5", ["six"] = "6", ["seven"] = "7", ["eight"] = "8", ["ate"] = "8", ["nine"] = "9"
    };

    /// <summary>Files and folders whose name contains every word of <paramref name="target"/>, best match tier only, newest first, at most 12.</summary>
    public IReadOnlyList<string> FindFiles(string target)
    {
        // Split the spoken request into words.
        var words = target.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0) return [];

        // Step 1: if the last word is "folder" or "directory" it is a hint about the kind of thing, so remove it from the name.
        // "the Open Tablet Driver folder": the last word says what kind of thing it is, it is not part of the name.
        var kind = words.Length > 1 ? words[^1].ToLowerInvariant() : "";
        var foldersOnly = kind is "folder" or "directory";
        if (foldersOnly) words = words[..^1];

        // Step 2: turn each word into a regex piece. Number words become "(?:four|4)" so either form matches; other words are escaped so they match literally.
        var patterns = words.Select(word => words.Length > 1 && SpokenDigits.TryGetValue(word, out var digit) ? $"(?:{word}|{digit})" : Regex.Escape(word)).ToArray();

        // Step 3: wordPatterns requires every word as a whole token; inOrder allows the words to run together, in order, separated by
        // anything non-alphanumeric. The lookbehind (?<![a-z0-9]) and lookahead (?![a-z0-9]) stop a match inside a longer word.
        // Each word must be a whole token of the name, or the words must run together ("report 4" matches "Homework_4_Report.pdf"
        // and "report4.txt", not "Intuos4ReportParser.cs" or a dated screenshot; "report for" must not match "ReportFormatter.cs").
        var wordPatterns = patterns.Select(pattern => new Regex($@"(?<![a-z0-9]){pattern}(?![a-z0-9])", RegexOptions.IgnoreCase)).ToArray();
        var inOrder = new Regex($@"(?<![a-z0-9]){string.Join("[^a-z0-9]*", patterns)}(?![a-z0-9])", RegexOptions.IgnoreCase);
        bool Matches(string name) => wordPatterns.All(pattern => pattern.IsMatch(name)) || inOrder.IsMatch(name);

        // Step 4: walk the folders. Breadth-first with a 4 second deadline and a depth limit of 8, so a huge folder cannot starve the other roots.
        // ponytail: a fresh walk every call with a 4 s cap, no index; an index (or Windows Search) if the walk is ever too slow.
        // Breadth first, so one huge folder cannot use up the cap before another root is looked at.
        var deadline = DateTime.UtcNow.AddSeconds(4);
        var matches = new List<(string Path, DateTime Written)>();
        var pending = new Queue<(string Dir, int Depth)>(_fileSearchRoots.Select(root => (root, 0)));

        while (pending.Count > 0 && DateTime.UtcNow < deadline)
        {
            var (dir, depth) = pending.Dequeue();
            try
            {
                foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos())
                {
                    var found = Matches(entry.Name) && (!foldersOnly || entry is DirectoryInfo);
                    if (found) matches.Add((entry.FullName, entry.LastWriteTimeUtc));
                    // Folders and files are both collected as matches; only real directories are queued for the next level.
                    // A matching folder is the answer to a folder ask, not the copies and projects of the same name inside it.
                    if (entry is DirectoryInfo && depth < 8 && !SkippedFolders.Contains(entry.Name) && !(found && foldersOnly)) pending.Enqueue((entry.FullName, depth + 1));
                }
            }
            // Folders we cannot read (permissions) or that disappear mid-walk are skipped silently.
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException) { }
        }

        // Step 5: rank. Prefer names that contain the words in order, and among those, names that start with them; then newest first, at most 12.
        // "report 4" means Report 4 NZR.docx, not the newer Homework_4_Report_NZR.pdf or itl-report4.md, so only the best tier
        // is returned and a single best name opens straight away: starts with the words, then has them in order, then any order.
        var exact = matches.Where(match => inOrder.IsMatch(Path.GetFileName(match.Path))).ToList();
        var leading = exact.Where(match => inOrder.Match(Path.GetFileName(match.Path)).Index == 0).ToList();
        // Roots can nest (the workspace sits under OneDrive Desktop), so the same file can be met twice.
        // Roots can nest (the workspace sits under OneDrive Desktop), so the same file can be met twice.
        var best = (leading.Count > 0 ? leading : exact.Count > 0 ? exact : matches).DistinctBy(match => match.Path, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(match => match.Written).Take(12).Select(match => match.Path).ToArray();
        // Step 6: if nothing matched and the request ended in "file" or "document", retry without that last word.
        // "design document" should find Design Document.docx, so "the budget file" drops its last word only when no name has it.
        return best.Length == 0 && kind is "file" or "document" ? FindFiles(string.Join(' ', words[..^1])) : best;
    }

    // Tool: a short text summary of the machine (OS, uptime, drives and so on) for the model to read.
    public Task<string> GetSystemStatusAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Environment.TickCount64 is milliseconds since Windows started.
        var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        var drives = GetDriveSummary();

        var response = string.Join(
            Environment.NewLine,
            "System state:",
            $"- OS: {RuntimeInformation.OSDescription}",
            $"- Machine: {Environment.MachineName}",
            $"- User: {Environment.UserName}",
            $"- Architecture: {RuntimeInformation.OSArchitecture}",
            $"- Local time: {DateTimeOffset.Now:dddd, MMM d yyyy h:mm tt}",
            $"- Uptime: {uptime.Days}d {uptime.Hours}h {uptime.Minutes}m",
            $"- Running processes: {Process.GetProcesses().Length}",
            $"- Workspace: {_workspaceRoot}",
            $"- Drives: {drives}");

        return Task.FromResult(response);
    }

    // Tool: a richer snapshot of what the user is doing now (active window, visible apps, workspace files), formatted as text.
    public Task<string> GetComputerContextAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = BuildContextSnapshot();

        var lines = new List<string>
        {
            "Computer context:",
            $"- Local time: {snapshot.LocalTime:dddd, MMMM d, yyyy h:mm tt zzz}",
            $"- Machine: {Environment.MachineName}",
            $"- User: {Environment.UserName}",
            $"- Workspace: {_workspaceRoot}",
            $"- Active application: {DescribeActiveApplication(snapshot)}",
            $"- Active window: {OrUnavailable(snapshot.ActiveWindow)}",
            $"- Visible desktop apps: {(snapshot.VisibleApplications.Length == 0 ? "None detected." : string.Join(" | ", snapshot.VisibleApplications))}",
            $"- Workspace root entries: {(snapshot.WorkspaceEntries.Length == 0 ? "None detected." : string.Join(" | ", snapshot.WorkspaceEntries))}",
            $"- Running processes: {snapshot.RunningProcesses}",
            $"- Drives: {snapshot.DriveSummary}"
        };

        return Task.FromResult(string.Join(Environment.NewLine, lines));
    }

    // Same snapshot as structured data instead of text, for callers that want the fields.
    public Task<DesktopContextSnapshot> GetContextSnapshotAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(BuildContextSnapshot());
    }

    // Tool: network health summary; the work is done by LocalNetworkMonitoring.
    public Task<string> GetLocalNetworkStatusAsync(string target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return LocalNetworkMonitoring.GetStatusAsync(target, cancellationToken);
    }

    // Tool: list installed or running apps, optionally filtered by a name. Candidates are scored against the name and the best 15 are shown.
    public Task<string> ListApplicationsAsync(string target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var candidates = BuildApplicationCatalog();
        var filtered = string.IsNullOrWhiteSpace(target)
            ? candidates
                .OrderByDescending(candidate => candidate.IsRunning)
                .ThenByDescending(GetCandidatePriority)
                .ThenBy(candidate => candidate.DisplayName, StringComparer.OrdinalIgnoreCase)
            : candidates
                .Select(candidate => new { Candidate = candidate, Score = ScoreApplicationCandidate(target, candidate) })
                .Where(item => item.Score > 0)
                .OrderByDescending(item => item.Score)
                .ThenByDescending(item => item.Candidate.IsRunning)
                .ThenByDescending(item => GetCandidatePriority(item.Candidate))
                .ThenBy(item => item.Candidate.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Select(item => item.Candidate);

        var matches = filtered
            .Take(15)
            .Select(candidate =>
            {
                var running = candidate.IsRunning ? "running" : "available";
                return $"- {candidate.DisplayName} ({candidate.Source}, {running})";
            })
            .ToArray();

        if (matches.Length == 0)
        {
            return Task.FromResult(
                string.IsNullOrWhiteSpace(target)
                    ? "No application entries were discovered."
                    : $"No matching application candidates were found for \"{target.Trim()}\".");
        }

        var heading = string.IsNullOrWhiteSpace(target)
            ? "Application catalog:"
            : $"Application matches for \"{target.Trim()}\":";

        return Task.FromResult(heading + Environment.NewLine + string.Join(Environment.NewLine, matches));
    }

    // Matches generic browser wording: optional "my"/"the", optional "web"/"internet"/"default", then "browser"
    // (e.g. "my default browser", "the web browser", "browser"). "open my browser" arrives here as just "browser"; without this check the fuzzy app match opens DB Browser instead of
    // Chrome. The default browser is read from Windows (never hard-coded); a settings alias for the word still wins,
    // and every other name is returned untouched.
    private static readonly Regex GenericBrowserName = new(@"^(?:(?:my|the)\s+)?(?:(?:web|internet|default)\s+)?browser$", RegexOptions.IgnoreCase);

    // Turns generic browser wording into the default browser's name; any other target is returned unchanged.
    public string ResolveAppTarget(string target)
    {
        var name = target.Trim();
        if (!GenericBrowserName.IsMatch(name)) return name;
        // The alias's value, not the word: the fuzzy app match runs before aliases are read, so "browser" would still find DB Browser.
        if (_appAliases.TryGetValue(name, out var alias) || _appAliases.TryGetValue("browser", out alias)) return alias;
        // Lookup failed: a web address opens in the default browser through the shell, so never fall back to the fuzzy "browser" match.
        return DefaultBrowserName() ?? "https://www.bing.com/";
    }

    // Reads the default browser from the registry: first the program ID the user chose for https links, then the command registered for that ID.
    // The https handler's program name ("chrome"): UserChoice ProgId, then that ProgId's open command, then the exe without extension. Read-only.
    public static string? DefaultBrowserName()
    {
        try
        {
            using var choice = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice");
            if (choice?.GetValue("ProgId") is not string progId) return null;
            using var open = Registry.ClassesRoot.OpenSubKey(progId + @"\shell\open\command");
            if (open?.GetValue(null) is not string command) return null;
            // The regex pulls the .exe path out of that command, with or without quotes, for example "C:\Program Files\Google\Chrome\chrome.exe" --single-argument %1 gives chrome.
            var exe = Regex.Match(command, "^\\s*\"?([^\"]+?\\.exe)", RegexOptions.IgnoreCase);
            return exe.Success ? Path.GetFileNameWithoutExtension(exe.Groups[1].Value) : null;
        }
        // Any registry problem just means "unknown browser"; the caller has a fallback.
        catch (Exception)
        {
            return null;
        }
    }

    // Tool: open an app. Order of attempts: focus an already-running window, launch a matching catalog entry, launch by alias or raw name, and finally treat the words as a file search.
    public Task<string> OpenApplicationAsync(string target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(target))
        {
            return Task.FromResult("Usage: open app <application>");
        }

        var normalizedTarget = ResolveAppTarget(target);
        var matchingCandidates = FindApplicationCandidates(normalizedTarget).ToArray();

        foreach (var candidate in matchingCandidates)
        {
            if (candidate.IsRunning
                && candidate.WindowHandle != IntPtr.Zero
                && TryFocusWindow(candidate.WindowHandle))
            {
                return Task.FromResult($"Focused application: {candidate.DisplayName} ({candidate.Source})");
            }

            if (TryLaunchShellTarget(candidate.LaunchTarget, candidate.LaunchArguments, out var launchedTarget, out var launchError))
            {
                return Task.FromResult($"Opened application target: {candidate.DisplayName} ({candidate.Source}) -> {launchedTarget}");
            }
        }

        var fallbackTarget = _appAliases.TryGetValue(normalizedTarget, out var aliasTarget)
            ? aliasTarget
            : normalizedTarget;

        if (TryLaunchShellTarget(fallbackTarget, out var launchedFallback, out var error))
        {
            return Task.FromResult($"Opened application target: {launchedFallback}");
        }

        // No app by that name: "open the report 4" means a file. The planner retries the bad app name and never searches,
        // so the search runs here: one match opens, several are listed.
        var files = FindFiles(normalizedTarget);
        return files.Count switch
        {
            1 when ToolSafetyService.OpensWithoutAsking(files[0]) => OpenPathAsync(files[0], cancellationToken),
            0 => Task.FromResult($"Unable to open \"{normalizedTarget}\": no app or file by that name ({error})."),
            _ => Task.FromResult($"No app called \"{normalizedTarget}\". Files that match, newest first (use open path <path> to open one):{Environment.NewLine}{string.Join(Environment.NewLine, files.Select(path => $"- {path}"))}")
        };
    }

    // Tool: launch an app with command-line arguments. With no arguments it is the same as OpenApplicationAsync. Candidates that run via a direct program path are preferred, since they can take arguments.
    public Task<string> LaunchApplicationAsync(string target, string arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(target))
        {
            return Task.FromResult("Usage: launch application <application> <arguments>");
        }

        if (string.IsNullOrWhiteSpace(arguments))
        {
            return OpenApplicationAsync(target, cancellationToken);
        }

        var normalizedTarget = ResolveAppTarget(target);
        var matchingCandidates = FindApplicationCandidates(normalizedTarget)
            .OrderByDescending(GetArgumentLaunchPriority)
            .ThenBy(candidate => candidate.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var candidate in matchingCandidates)
        {
            if (TryLaunchShellTarget(candidate.LaunchTarget, arguments.Trim(), out var launchedTarget, out var launchError))
            {
                return Task.FromResult($"Opened application target: {candidate.DisplayName} ({candidate.Source}) -> {launchedTarget}");
            }
        }

        var fallbackTarget = _appAliases.TryGetValue(normalizedTarget, out var aliasTarget)
            ? aliasTarget
            : normalizedTarget;

        return Task.FromResult(
            TryLaunchShellTarget(fallbackTarget, arguments.Trim(), out var launchedFallback, out var error)
                ? $"Opened application target: {launchedFallback}"
                : $"Unable to launch application target \"{normalizedTarget}\" with arguments: {error}");
    }

    // Tool: open a file or folder with whatever program Windows has registered for it.
    public Task<string> OpenPathAsync(string target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(target))
        {
            return Task.FromResult("Usage: open path <file-or-folder>");
        }

        var resolvedPath = ResolvePath(target);

        if (!File.Exists(resolvedPath) && !Directory.Exists(resolvedPath))
        {
            return Task.FromResult($"Path not found: {resolvedPath}");
        }

        return Task.FromResult(
            TryLaunchShellTarget(resolvedPath, out _, out var error)
                ? $"Opened path: {resolvedPath}"
                : $"Unable to open path \"{resolvedPath}\": {error}");
    }

    // Tool: list up to 20 entries in a folder (folders first, then files with sizes).
    public Task<string> ListFilesAsync(string target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var resolvedPath = ResolvePath(string.IsNullOrWhiteSpace(target) ? _workspaceRoot : target);

        if (!Directory.Exists(resolvedPath))
        {
            return Task.FromResult($"Directory not found: {resolvedPath}");
        }

        var entries = Directory.GetDirectories(resolvedPath)
            .Select(path => ("DIR", Path.GetFileName(path), string.Empty))
            .Concat(
                Directory.GetFiles(resolvedPath)
                    .Select(path =>
                    {
                        var fileInfo = new FileInfo(path);
                        return ("FILE", fileInfo.Name, FormatBytes(fileInfo.Length));
                    }))
            .OrderBy(entry => entry.Item1)
            .ThenBy(entry => entry.Item2, StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .Select(entry => entry.Item1 == "DIR"
                ? $"- [DIR] {entry.Item2}"
                : $"- [FILE] {entry.Item2} ({entry.Item3})")
            .ToArray();

        if (entries.Length == 0)
        {
            return Task.FromResult($"No files or folders found in {resolvedPath}");
        }

        return Task.FromResult(
            $"Items in {resolvedPath}:{Environment.NewLine}{string.Join(Environment.NewLine, entries)}");
    }

    // Tool: show the start of a text file. Capped at 40 lines and 4000 characters so a big file cannot flood the model; binary files are refused.
    public async Task<string> ReadFilePreviewAsync(string target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(target))
        {
            return "Usage: read file <path>";
        }

        var resolvedPath = ResolvePath(target);

        if (!File.Exists(resolvedPath))
        {
            return $"File not found: {resolvedPath}";
        }

        if (await IsLikelyBinaryAsync(resolvedPath, cancellationToken))
        {
            return $"File appears to be binary and cannot be previewed safely: {resolvedPath}";
        }

        var lines = new List<string>();

        using var stream = File.OpenRead(resolvedPath);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream && lines.Count < 40)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lines.Add((await reader.ReadLineAsync(cancellationToken)) ?? string.Empty);
        }

        var content = string.Join(Environment.NewLine, lines);

        if (content.Length > 4000)
        {
            content = content[..4000] + Environment.NewLine + "[preview truncated]";
        }

        return string.Join(
            Environment.NewLine,
            $"Preview of {resolvedPath}:",
            string.IsNullOrWhiteSpace(content) ? "[empty file]" : content);
    }

    // Tool: search for files and folders by spoken name (see FindFiles above).
    public Task<string> FindFilesAsync(string target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(target))
        {
            return Task.FromResult("Usage: find files <name-fragment>");
        }

        var matches = FindFiles(target.Trim()).Select(path => $"- {path}").ToArray();

        if (matches.Length == 0)
        {
            return Task.FromResult($"No files or folders named like \"{target}\" in {string.Join(", ", _fileSearchRoots)}.");
        }

        return Task.FromResult(
            $"Newest first, matches for \"{target}\" (use open path <path> to open one):{Environment.NewLine}{string.Join(Environment.NewLine, matches)}");
    }

    // Tool: open a web address, a known site name (see BuiltInSiteAliases) or a search in the user's default browser.
    public Task<string> BrowseAsync(string target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(target))
        {
            return Task.FromResult("Usage: browse <url>");
        }

        var launchTarget = ResolveBrowseTarget(target);

        return Task.FromResult(
            TryLaunchShellTarget(launchTarget, out _, out var error)
                ? $"Opened browser target: {launchTarget}."
                : $"Unable to open browser target \"{launchTarget}\": {error}");
    }

    /// <summary>The URL `browse` would open: a known site name ("discord", "my linkedin messages"), a URL, or a search.</summary>
    public string ResolveBrowseTarget(string target)
    {
        var raw = target.Trim();
        // Strips filler so "my linkedin messages" or "discord in the browser" can match a site alias. Removes a leading "my "/"the " or a trailing "in the browser", "site", "page" or "website".
        var name = Regex.Replace(raw, @"^(?:my|the)\s+|\s+(?:in (?:the |my )?browser|site|page|website)$", "", RegexOptions.IgnoreCase).Trim();
        return _siteAliases.TryGetValue(raw, out var url) || _siteAliases.TryGetValue(name, out url) ? url : NormalizeBrowseTarget(raw);
    }

    // Tool: run a Bing search in the default browser.
    public Task<string> SearchWebAsync(string query, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(query))
        {
            return Task.FromResult("Usage: search web <query>");
        }

        var searchUrl = $"https://www.bing.com/search?q={Uri.EscapeDataString(query.Trim())}";

        return Task.FromResult(
            TryLaunchShellTarget(searchUrl, out _, out var error)
                ? $"Opened search results for: {query.Trim()}"
                : $"Unable to open search results: {error}");
    }

    // Convenience overload for launching with no arguments.
    private static bool TryLaunchShellTarget(string target, out string launchedTarget, out string error)
    {
        return TryLaunchShellTarget(target, null, out launchedTarget, out error);
    }

    // Starts the target through the Windows shell. Returns false plus the error text instead of throwing, so the tool can report the failure to the model.
    private static bool TryLaunchShellTarget(string target, string? arguments, out string launchedTarget, out string error)
    {
        try
        {
            var startInfo = CreateLaunchStartInfo(target, arguments);

            Process.Start(startInfo);

            launchedTarget = string.IsNullOrWhiteSpace(arguments)
                ? target
                : $"{target} {arguments}";
            error = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            launchedTarget = string.IsNullOrWhiteSpace(arguments)
                ? target
                : $"{target} {arguments}";
            error = exception.Message;
            return false;
        }
    }

    // Chooses how to start a target: Store/Start-menu apps and web addresses go through explorer.exe; everything else is handed straight to the shell.
    private static ProcessStartInfo CreateLaunchStartInfo(string target, string? arguments)
    {
        // Store apps and other Start-menu entries are launched through the Apps folder. The raw text is
        // handed to Explorer as-is, because parsing it as a URI would rewrite its backslash.
        if (target.StartsWith(ShellAppsFolderPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{target}\"",
                UseShellExecute = true
            };
        }

        // A non-file URI (https:, spotify:, ms-settings: and so on) is passed to explorer.exe so Windows picks the right handler.
        if (string.IsNullOrWhiteSpace(arguments)
            && Uri.TryCreate(target, UriKind.Absolute, out var absoluteUri)
            && !absoluteUri.IsFile)
        {
            return new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{absoluteUri}\"",
                UseShellExecute = true
            };
        }

        return new ProcessStartInfo
        {
            FileName = target,
            Arguments = arguments ?? string.Empty,
            UseShellExecute = true
        };
    }

    // Brings an already-running window to the front: restore it if minimised, then make it the foreground window (both are Windows API calls declared elsewhere in this class).
    private static bool TryFocusWindow(IntPtr windowHandle)
    {
        try
        {
            ShowWindowAsync(windowHandle, SwRestore);
            return SetForegroundWindow(windowHandle);
        }
        catch
        {
            return false;
        }
    }

    // Turns what the user said into a real path: handles words like "desktop", environment variables, "~" for the home folder, and relative paths (taken from the workspace).
    private string ResolvePath(string target)
    {
        var trimmed = target.Trim().Trim('"');

        if (TryResolveSpecialFolder(trimmed, out var specialFolderPath))
        {
            return specialFolderPath;
        }

        var expanded = Environment.ExpandEnvironmentVariables(trimmed);

        if (expanded.StartsWith('~'))
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            expanded = Path.Combine(userProfile, expanded[1..].TrimStart('\\', '/'));
        }

        if (Path.IsPathRooted(expanded))
        {
            return Path.GetFullPath(expanded);
        }

        return Path.GetFullPath(Path.Combine(_workspaceRoot, expanded));
    }

    // Turns loose text into a URL: real URLs stay as they are, "www." or a dotted word with no spaces gets https://, anything else becomes a Bing search.
    private static string NormalizeBrowseTarget(string target)
    {
        if (Uri.TryCreate(target, UriKind.Absolute, out var absoluteUri))
        {
            return absoluteUri.ToString();
        }

        if (target.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
        {
            return $"https://{target}";
        }

        if (target.Contains('.') && !target.Contains(' '))
        {
            return $"https://{target}";
        }

        return $"https://www.bing.com/search?q={Uri.EscapeDataString(target)}";
    }

    // Cheap binary check: reads the first 512 bytes and treats any zero byte as binary (text files do not contain them).
    private static async Task<bool> IsLikelyBinaryAsync(string path, CancellationToken cancellationToken)
    {
        var buffer = new byte[512];

        await using var stream = File.OpenRead(path);
        var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);

        return buffer.Take(bytesRead).Any(value => value == 0);
    }

    // Shows a byte count as KB/MB/GB with one decimal.
    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unitIndex = 0;

        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return $"{value:0.#} {units[unitIndex]}";
    }

    // Maps friendly folder words ("desktop", "downloads", "music" and so on) to real folders.
    private bool TryResolveSpecialFolder(string target, out string path)
    {
        if (target.Equals("workspace", StringComparison.OrdinalIgnoreCase))
        {
            path = _workspaceRoot;
            return true;
        }

        if (target.Equals("desktop", StringComparison.OrdinalIgnoreCase))
        {
            path = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            return true;
        }

        if (target.Equals("documents", StringComparison.OrdinalIgnoreCase))
        {
            path = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            return true;
        }

        if (target.Equals("downloads", StringComparison.OrdinalIgnoreCase))
        {
            path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            return true;
        }

        if (target.Equals("pictures", StringComparison.OrdinalIgnoreCase))
        {
            path = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            return true;
        }

        if (target.Equals("music", StringComparison.OrdinalIgnoreCase))
        {
            path = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
            return true;
        }

        if (target.Equals("videos", StringComparison.OrdinalIgnoreCase))
        {
            path = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            return true;
        }

        path = string.Empty;
        return false;
    }

    // Builds the full list of apps Jarvis can open: running windows first, then the cached list of installed apps.
    private IReadOnlyList<ApplicationCandidate> BuildApplicationCatalog(bool forceRefresh = false)
    {
        var candidates = new List<ApplicationCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Windows that are open right now are always read fresh, so a running app wins over its shortcut.
        foreach (var candidate in GetVisibleApplications())
        {
            AddCandidate(candidates, seen, candidate);
        }

        // Everything installed comes from the cached index (see DesktopAutomation.AppIndex.cs).
        foreach (var candidate in GetStaticCatalog(forceRefresh))
        {
            AddCandidate(candidates, seen, candidate);
        }

        return PruneAliasCandidates(candidates);
    }

    // Combines the built-in alias table with extra entries from settings; settings win on a clash, blank entries are ignored.
    private static IReadOnlyDictionary<string, string> MergeAliases(
        IReadOnlyDictionary<string, string> builtIn,
        IReadOnlyDictionary<string, string>? applicationAliases)
    {
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var alias in builtIn)
        {
            merged[alias.Key] = alias.Value;
        }

        if (applicationAliases is null)
        {
            return merged;
        }

        foreach (var alias in applicationAliases)
        {
            var key = alias.Key?.Trim();
            var value = alias.Value?.Trim();

            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            merged[key] = value;
        }

        return merged;
    }

    // Adds an app to the list unless one with the same name, target and arguments is already there.
    private static void AddCandidate(
        ICollection<ApplicationCandidate> candidates,
        ISet<string> seen,
        ApplicationCandidate candidate)
    {
        var key =
            $"{NormalizeForMatching(candidate.DisplayName)}|{NormalizeForMatching(candidate.LaunchTarget)}|{NormalizeForMatching(candidate.LaunchArguments)}";

        if (!seen.Add(key))
        {
            return;
        }

        candidates.Add(candidate);
    }

    // Drops weak "alias" entries when a real entry (start menu, running window...) already has the same display name.
    private static IReadOnlyList<ApplicationCandidate> PruneAliasCandidates(IReadOnlyList<ApplicationCandidate> candidates)
    {
        var strongerDisplays = candidates
            .Where(candidate => !string.Equals(candidate.Source, "alias", StringComparison.OrdinalIgnoreCase))
            .Select(candidate => NormalizeForMatching(candidate.DisplayName))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return candidates
            .Where(candidate =>
                !string.Equals(candidate.Source, "alias", StringComparison.OrdinalIgnoreCase)
                || !strongerDisplays.Contains(NormalizeForMatching(candidate.DisplayName)))
            .ToArray();
    }

    // Finds apps matching a spoken name. If nothing matches, rescans installed apps once, at most once per MissRefreshInterval, in case the app was just installed.
    private IEnumerable<ApplicationCandidate> FindApplicationCandidates(string target)
    {
        var matches = RankApplicationCandidates(target, BuildApplicationCatalog());

        if (matches.Length > 0)
        {
            return matches;
        }

        // Nothing matched. The app may have been installed since the index was built, so rescan once
        // (rate limited) before giving up.
        lock (_catalogSync)
        {
            if (DateTime.UtcNow - _lastMissRefreshUtc < MissRefreshInterval)
            {
                return matches;
            }

            _lastMissRefreshUtc = DateTime.UtcNow;
        }

        return RankApplicationCandidates(target, BuildApplicationCatalog(forceRefresh: true));
    }

    // Scores every app against the query and keeps those scoring 60 or more, best first (running apps and higher-priority sources win ties).
    private static ApplicationCandidate[] RankApplicationCandidates(
        string target,
        IReadOnlyList<ApplicationCandidate> catalog)
    {
        return catalog
            .Select(candidate => new { Candidate = candidate, Score = ScoreApplicationCandidate(target, candidate) })
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.Candidate.IsRunning)
            .ThenByDescending(item => GetCandidatePriority(item.Candidate))
            .ThenBy(item => item.Candidate.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Where(item => item.Score >= 60)
            .Select(item => item.Candidate)
            .ToArray();
    }

    // Scores how well an app matches a spoken name. Exact name 120, starts-with 80, contains 45, plus bonuses for matching the display, process or launch name,
    // and 18 per matching word. Names that score under 60 get a fuzzy (typo-tolerant) score; running apps get +12. The 60 cut-off is applied in RankApplicationCandidates.
    private static int ScoreApplicationCandidate(string query, ApplicationCandidate candidate)
    {
        var normalizedQuery = StripQueryNoise(NormalizeForMatching(query));

        if (string.IsNullOrWhiteSpace(normalizedQuery))
        {
            return 0;
        }

        var display = NormalizeForMatching(candidate.DisplayName);
        var process = NormalizeForMatching(candidate.ProcessName);
        var window = NormalizeForMatching(candidate.WindowTitle);
        var launch = NormalizeForMatching(Path.GetFileNameWithoutExtension(candidate.LaunchTarget));
        var haystacks = new[] { display, process, window, launch }
            .Concat(GetSpokenNames(candidate.DisplayName))
            .ToArray();
        var score = 0;

        if (haystacks.Any(haystack => haystack == normalizedQuery))
        {
            score += 120;
        }

        if (haystacks.Any(haystack => haystack.StartsWith(normalizedQuery, StringComparison.OrdinalIgnoreCase)))
        {
            score += 80;
        }

        if (haystacks.Any(haystack => haystack.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase)))
        {
            score += 45;
        }

        if (display == normalizedQuery)
        {
            score += 50;
        }

        if (process == normalizedQuery || window == normalizedQuery)
        {
            score += 35;
        }

        if (launch == normalizedQuery)
        {
            score += 20;
        }

        foreach (var token in normalizedQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (haystacks.Any(haystack => haystack.Contains(token, StringComparison.OrdinalIgnoreCase)))
            {
                score += 18;
            }
        }

        // Speech recognition often gets one or two letters of a name wrong ("Discourd", "Spotifi").
        // Speech recognition often gets one or two letters of a name wrong ("Discourd", "Spotifi").
        if (score < 60)
        {
            score = Math.Max(score, ScoreFuzzyMatch(normalizedQuery, candidate.DisplayName));
        }

        if (candidate.IsRunning)
        {
            score += 12;
        }

        return score;
    }

    // Tie-break order for apps with equal scores: where the entry came from decides, higher number wins (running window best, plain aliases worst).
    private static int GetCandidatePriority(ApplicationCandidate candidate)
    {
        return candidate.Source switch
        {
            "running app" => 7,
            "start menu" => 6,
            "desktop shortcut" => 6,
            "desktop executable" => 6,
            "app paths" => 6,
            "installed app" => 5,
            "store app" => 5,
            "start app" => 5,
            "local app data" => 5,
            "alias" => 1,
            _ => 2
        };
    }

    // Same idea for launching WITH arguments: sources that point at a real program file rank above shortcuts and running windows.
    private static int GetArgumentLaunchPriority(ApplicationCandidate candidate)
    {
        return candidate.Source switch
        {
            "app paths" => 8,
            "local app data" => 8,
            "installed app" => 7,
            "start menu" => 7,
            "desktop executable" => 7,
            "desktop shortcut" => 6,
            "running app" => 5,
            "alias" => 4,
            _ => GetCandidatePriority(candidate)
        };
    }

    // Lists apps that have a visible top-level window right now by asking each process for its main window handle and title.
    private IEnumerable<ApplicationCandidate> GetVisibleApplications()
    {
        // Windows API GetForegroundWindow: the handle (id) of the window the user is currently looking at, so the active app can be flagged.
        var foregroundHandle = GetForegroundWindow();
        var visibleApps = new List<ApplicationCandidate>();

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (process.HasExited)
                {
                    continue;
                }

                var handle = process.MainWindowHandle;
                var title = process.MainWindowTitle;

                if (handle == IntPtr.Zero || string.IsNullOrWhiteSpace(title))
                {
                    continue;
                }

                visibleApps.Add(new ApplicationCandidate(
                    process.ProcessName,
                    process.ProcessName,
                    "running app",
                    true,
                    handle,
                    process.Id,
                    process.ProcessName,
                    title));
            }
            // Some processes refuse to give up their window info (system or elevated programs); those are skipped.
            catch
            {
            }
            // Process objects hold OS handles, so each one is disposed.
            finally
            {
                process.Dispose();
            }
        }

        // The window the user is looking at is listed first.
        return visibleApps
            .OrderByDescending(candidate => candidate.WindowHandle == foregroundHandle)
            .ThenBy(candidate => candidate.DisplayName, StringComparer.OrdinalIgnoreCase);
    }

    // Finds apps from the Start menu folders (this user's and all users'): shortcuts (.lnk), internet shortcuts (.url) and ClickOnce links (.appref-ms).
    private IEnumerable<ApplicationCandidate> GetStartMenuApplications()
    {
        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                     Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms)
                 })
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                continue;
            }

            IEnumerable<string> files;

            try
            {
                files = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
                    .Where(path =>
                        path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)
                        || path.EndsWith(".url", StringComparison.OrdinalIgnoreCase)
                        || path.EndsWith(".appref-ms", StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                continue;
            }

            foreach (var path in files)
            {
                var name = Path.GetFileNameWithoutExtension(path);

                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                yield return new ApplicationCandidate(name, path, "start menu", false, IntPtr.Zero, 0, name, string.Empty);
            }
        }
    }

    // Finds launchable items placed on the Desktop (this user's and the shared one), top level only.
    private IEnumerable<ApplicationCandidate> GetDesktopApplications()
    {
        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                     Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
                 })
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                continue;
            }

            IEnumerable<string> files;

            try
            {
                files = Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly)
                    .Where(IsLaunchableDesktopEntry);
            }
            catch
            {
                continue;
            }

            foreach (var path in files)
            {
                var name = Path.GetFileNameWithoutExtension(path);

                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var source = Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase)
                    ? "desktop executable"
                    : "desktop shortcut";

                yield return new ApplicationCandidate(name, path, source, false, IntPtr.Zero, 0, name, string.Empty);
            }
        }
    }

    // True when the file has one of the launchable extensions listed in DesktopLaunchableExtensions.
    private static bool IsLaunchableDesktopEntry(string path)
    {
        var extension = Path.GetExtension(path);

        if (string.IsNullOrWhiteSpace(extension))
        {
            return false;
        }

        return DesktopLaunchableExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    // Reads the "App Paths" registry entries, where installers register program names so Windows can run them by short name (like "chrome").
    private static IEnumerable<ApplicationCandidate> GetAppPathApplications()
    {
        foreach (var candidate in ReadAppPathKey(Registry.CurrentUser))
        {
            yield return candidate;
        }

        foreach (var candidate in ReadAppPathKey(Registry.LocalMachine))
        {
            yield return candidate;
        }
    }

    // Reads the "Uninstall" registry entries (the list behind Settings > Installed apps), in both the 64-bit and 32-bit views for this user and for all users.
    private static IEnumerable<ApplicationCandidate> GetInstalledApplications()
    {
        foreach (var candidate in ReadInstalledApplications(RegistryHive.CurrentUser, RegistryView.Registry64))
        {
            yield return candidate;
        }

        foreach (var candidate in ReadInstalledApplications(RegistryHive.CurrentUser, RegistryView.Registry32))
        {
            yield return candidate;
        }

        foreach (var candidate in ReadInstalledApplications(RegistryHive.LocalMachine, RegistryView.Registry64))
        {
            yield return candidate;
        }

        foreach (var candidate in ReadInstalledApplications(RegistryHive.LocalMachine, RegistryView.Registry32))
        {
            yield return candidate;
        }
    }

    // Finds per-user apps installed under AppData\Local (Discord, Slack style installers), which often do not appear in the Start menu list.
    private IEnumerable<ApplicationCandidate> GetLocalUserApplications()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        if (string.IsNullOrWhiteSpace(localAppData) || !Directory.Exists(localAppData))
        {
            yield break;
        }

        IEnumerable<string> directories;

        try
        {
            directories = Directory.EnumerateDirectories(localAppData, "*", SearchOption.TopDirectoryOnly);
        }
        catch
        {
            yield break;
        }

        foreach (var directory in directories)
        {
            if (ShouldSkipLocalApplicationDirectory(directory))
            {
                continue;
            }

            // Those installers keep each version in an "app-1.2.3" subfolder (Squirrel); this picks the newest one.
            var launchDirectory = ResolveLatestSquirrelApplicationDirectory(directory);

            if (string.IsNullOrWhiteSpace(launchDirectory))
            {
                continue;
            }

            var displayName = Path.GetFileName(directory);
            var launchTarget = FindBestExecutableInInstallLocation(launchDirectory, displayName, 1);

            if (string.IsNullOrWhiteSpace(launchTarget))
            {
                continue;
            }

            yield return new ApplicationCandidate(
                displayName,
                launchTarget,
                "local app data",
                false,
                IntPtr.Zero,
                0,
                displayName,
                string.Empty);
        }
    }

    // Reads one registry view of the Uninstall list and turns each named entry into an app candidate. Any registry error ends that view quietly.
    private static IReadOnlyList<ApplicationCandidate> ReadInstalledApplications(RegistryHive hive, RegistryView view)
    {
        var candidates = new List<ApplicationCandidate>();
        RegistryKey? baseKey = null;
        RegistryKey? uninstallKey = null;

        try
        {
            baseKey = RegistryKey.OpenBaseKey(hive, view);
            uninstallKey = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");

            if (uninstallKey is null)
            {
                return candidates;
            }

            foreach (var subKeyName in uninstallKey.GetSubKeyNames())
            {
                using var subKey = uninstallKey.OpenSubKey(subKeyName);

                if (subKey is null)
                {
                    continue;
                }

                var displayName = (subKey.GetValue("DisplayName") as string)?.Trim();

                if (string.IsNullOrWhiteSpace(displayName) || ShouldSkipInstalledApplication(displayName))
                {
                    continue;
                }

                var launchTarget = ResolveInstalledApplicationLaunchTarget(subKey, displayName);

                if (string.IsNullOrWhiteSpace(launchTarget))
                {
                    continue;
                }

                candidates.Add(new ApplicationCandidate(
                    displayName,
                    launchTarget,
                    "installed app",
                    false,
                    IntPtr.Zero,
                    0,
                    displayName,
                    string.Empty));
            }
        }
        catch
        {
        }
        finally
        {
            uninstallKey?.Dispose();
            baseKey?.Dispose();
        }

        return candidates;
    }

    // Reads one App Paths registry hive: each sub-key name (like chrome.exe) maps to the full program path stored as its default value.
    private static IEnumerable<ApplicationCandidate> ReadAppPathKey(RegistryKey root)
    {
        using var appPaths = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths");

        if (appPaths is null)
        {
            yield break;
        }

        foreach (var subKeyName in appPaths.GetSubKeyNames())
        {
            using var subKey = appPaths.OpenSubKey(subKeyName);
            var target = subKey?.GetValue(null) as string;

            if (string.IsNullOrWhiteSpace(target))
            {
                continue;
            }

            var name = Path.GetFileNameWithoutExtension(subKeyName);

            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            yield return new ApplicationCandidate(name, target, "app paths", false, IntPtr.Zero, 0, name, string.Empty);
        }
    }

    // Hides uninstall-list entries that are not apps a person would ask to open (runtime libraries, Windows updates, OneDrive/Edge updaters).
    private static bool ShouldSkipInstalledApplication(string displayName)
    {
        var normalized = displayName.Trim();

        if (string.IsNullOrWhiteSpace(normalized))
        {
            return true;
        }

        var noisyPrefixes = new[]
        {
            "Microsoft Visual C++",
            "Security Update for",
            "Update for",
            "Hotfix for",
            "NVIDIA PhysX",
            "Microsoft Edge Update",
            "Microsoft OneDrive"
        };

        return noisyPrefixes.Any(prefix => normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    // Works out which program to run for an Uninstall-list entry: first the program named by its icon path, else the best-looking .exe in its install folder.
    private static string ResolveInstalledApplicationLaunchTarget(RegistryKey subKey, string displayName)
    {
        var displayIcon = (subKey.GetValue("DisplayIcon") as string)?.Trim();
        var installLocation = (subKey.GetValue("InstallLocation") as string)?.Trim();

        if (TryExtractLaunchablePath(displayIcon, out var launchTarget))
        {
            return launchTarget;
        }

        if (!string.IsNullOrWhiteSpace(installLocation))
        {
            var candidate = FindBestExecutableInInstallLocation(installLocation, displayName);

            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate;
            }
        }

        return string.Empty;
    }

    // Pulls a real file path out of a registry value such as "C:\Program Files\App\app.exe",0 (quoted, or with trailing icon index/arguments).
    // Returns false unless the result is an absolute path to a file that exists.
    private static bool TryExtractLaunchablePath(string? rawValue, out string launchTarget)
    {
        launchTarget = string.Empty;

        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return false;
        }

        var trimmed = Environment.ExpandEnvironmentVariables(rawValue.Trim());
        string candidate;

        if (trimmed.StartsWith('"'))
        {
            var closingQuoteIndex = trimmed.IndexOf('"', 1);

            if (closingQuoteIndex <= 1)
            {
                return false;
            }

            candidate = trimmed[1..closingQuoteIndex];
        }
        else
        {
            var matchedPath = TrySlicePathToKnownExtension(trimmed);

            if (string.IsNullOrWhiteSpace(matchedPath))
            {
                return false;
            }

            candidate = matchedPath;
        }

        if (!Path.IsPathRooted(candidate))
        {
            return false;
        }

        candidate = Path.GetFullPath(candidate);

        if (!File.Exists(candidate))
        {
            return false;
        }

        launchTarget = candidate;
        return true;
    }

    // For an unquoted value, cuts the text off right after the last known program extension, dropping any trailing arguments or icon index.
    private static string TrySlicePathToKnownExtension(string value)
    {
        var extensions = new[] { ".exe", ".bat", ".cmd", ".lnk", ".appref-ms", ".msc", ".url" };
        var bestEndIndex = -1;

        foreach (var extension in extensions)
        {
            var index = value.IndexOf(extension, StringComparison.OrdinalIgnoreCase);

            if (index < 0)
            {
                continue;
            }

            var candidateEnd = index + extension.Length;

            if (candidateEnd > bestEndIndex)
            {
                bestEndIndex = candidateEnd;
            }
        }

        return bestEndIndex <= 0 ? string.Empty : value[..bestEndIndex];
    }

    // Looks through an install folder (a few levels deep, breadth-first) and picks the .exe whose name best matches the app's name; the one at the top level gets a small bonus.
    private static string FindBestExecutableInInstallLocation(string installLocation, string displayName, int maxDepth = 2)
    {
        if (string.IsNullOrWhiteSpace(installLocation))
        {
            return string.Empty;
        }

        var expandedLocation = Environment.ExpandEnvironmentVariables(installLocation);

        if (!Directory.Exists(expandedLocation))
        {
            return string.Empty;
        }

        var displayTokens = NormalizeForMatching(displayName)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var queue = new Queue<(string Directory, int Depth)>();
        var scoredCandidates = new List<(string Path, int Score)>();
        queue.Enqueue((expandedLocation, 0));

        while (queue.Count > 0)
        {
            var (directory, depth) = queue.Dequeue();

            try
            {
                foreach (var filePath in Directory.EnumerateFiles(directory, "*.exe", SearchOption.TopDirectoryOnly))
                {
                    var fileName = Path.GetFileNameWithoutExtension(filePath);

                    if (ShouldSkipExecutableCandidate(fileName))
                    {
                        continue;
                    }

                    var normalizedFileName = NormalizeForMatching(fileName);
                    var score = 0;

                    if (normalizedFileName == NormalizeForMatching(displayName))
                    {
                        score += 100;
                    }

                    if (normalizedFileName.Contains(NormalizeForMatching(displayName), StringComparison.OrdinalIgnoreCase))
                    {
                        score += 45;
                    }

                    foreach (var token in displayTokens)
                    {
                        if (normalizedFileName.Contains(token, StringComparison.OrdinalIgnoreCase))
                        {
                            score += 18;
                        }
                    }

                    if (directory.Equals(expandedLocation, StringComparison.OrdinalIgnoreCase))
                    {
                        score += 10;
                    }

                    scoredCandidates.Add((filePath, score));
                }

                if (depth >= maxDepth)
                {
                    continue;
                }

                foreach (var childDirectory in Directory.EnumerateDirectories(directory))
                {
                    queue.Enqueue((childDirectory, depth + 1));
                }
            }
            catch
            {
            }
        }

        return scoredCandidates
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
            .Select(candidate => candidate.Path)
            .FirstOrDefault() ?? string.Empty;
    }

    // Skips helper programs that would be the wrong thing to launch: uninstallers, setup/update programs and crash handlers.
    private static bool ShouldSkipExecutableCandidate(string fileName)
    {
        var normalized = fileName.Trim();

        if (string.IsNullOrWhiteSpace(normalized))
        {
            return true;
        }

        var noisyNames = new[]
        {
            "uninstall",
            "unins000",
            "unins001",
            "gamelaunchhelper",
            "setup",
            "update",
            "updater",
            "crashpad_handler"
        };

        return noisyNames.Any(noisy =>
            normalized.Contains(noisy, StringComparison.OrdinalIgnoreCase));
    }

    // Skips AppData\Local folders that hold caches, system data or vendor files rather than a user-launchable app.
    private static bool ShouldSkipLocalApplicationDirectory(string directory)
    {
        var name = Path.GetFileName(directory);

        if (string.IsNullOrWhiteSpace(name))
        {
            return true;
        }

        var noisyNames = new[]
        {
            "Temp",
            "Packages",
            "Package Cache",
            "CrashDumps",
            "ConnectedDevicesPlatform",
            "D3DSCache",
            "Microsoft",
            "NVIDIA",
            "PlaceholderTileLogoFolder",
            "Publishers",
            "SquirrelTemp"
        };

        return noisyNames.Any(noisy => string.Equals(name, noisy, StringComparison.OrdinalIgnoreCase));
    }

    // Discord and Slack style installers keep each release in a folder named "app-1.2.3"; this returns the folder with the highest version number.
    private static string ResolveLatestSquirrelApplicationDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return string.Empty;
        }

        IEnumerable<string> appDirectories;

        try
        {
            appDirectories = Directory.EnumerateDirectories(directory, "app-*", SearchOption.TopDirectoryOnly);
        }
        catch
        {
            return string.Empty;
        }

        return appDirectories
            .Select(path => new
            {
                Path = path,
                Version = ParseSquirrelVersion(Path.GetFileName(path))
            })
            .OrderByDescending(item => item.Version is not null)
            .ThenByDescending(item => item.Version)
            .ThenByDescending(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .Select(item => item.Path)
            .FirstOrDefault() ?? string.Empty;
    }

    // Turns a folder name like "app-1.0.9" into a Version object; null when it does not look like that.
    private static Version? ParseSquirrelVersion(string? directoryName)
    {
        if (string.IsNullOrWhiteSpace(directoryName)
            || !directoryName.StartsWith("app-", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Version.TryParse(directoryName[4..], out var version)
            ? version
            : null;
    }

    // Asks Windows which window is in front and which program owns it (name, process id, title). Returns null when that is not available.
    private ForegroundWindowInfo? TryGetForegroundWindowInfo()
    {
        try
        {
            var handle = GetForegroundWindow();

            if (handle == IntPtr.Zero)
            {
                return null;
            }

            // Windows API GetWindowThreadProcessId: given a window handle, tells us the id of the process that owns it.
            _ = GetWindowThreadProcessId(handle, out var processId);

            if (processId == 0)
            {
                return null;
            }

            using var process = Process.GetProcessById((int)processId);
            return new ForegroundWindowInfo(process.ProcessName, (int)processId, ReadWindowTitle(handle));
        }
        catch
        {
            return null;
        }
    }

    // Reads a window's title bar text. GetWindowTextLength says how long it is, GetWindowText copies it into a buffer (+1 for the end-of-string character).
    private static string ReadWindowTitle(IntPtr handle)
    {
        var length = GetWindowTextLength(handle);

        if (length <= 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(length + 1);
        _ = GetWindowText(handle, builder, builder.Capacity);
        return builder.ToString();
    }

    // Top-level entries (folders and files) of the workspace root, for the context snapshot.
    private string[] GetWorkspaceEntries()
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(_workspaceRoot)
                .Select(path =>
                {
                    var name = Path.GetFileName(path);
                    return Directory.Exists(path) ? $"[DIR] {name}" : $"[FILE] {name}";
                })
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    // Free and total space for up to the first three ready drives.
    private static string GetDriveSummary()
    {
        var drives = DriveInfo.GetDrives()
            .Where(drive => drive.IsReady)
            .Take(3)
            .Select(drive => $"{drive.Name} {FormatBytes(drive.AvailableFreeSpace)} free of {FormatBytes(drive.TotalSize)}")
            .ToArray();

        return drives.Length == 0 ? "No ready drives found." : string.Join(" | ", drives);
    }

    // Makes names comparable: lower-case, every character that is not a letter, digit or space becomes a space, and runs of spaces collapse to one. "Visual-Studio  Code!" becomes "visual studio code".
    private static string NormalizeForMatching(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var chars = value
            .ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) || char.IsWhiteSpace(character) ? character : ' ')
            .ToArray();

        return string.Join(' ', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    // P/Invoke declarations: these let C# call functions in Windows' own user32.dll. SetLastError = true lets .NET record the Windows error code if a call fails.
    // GetForegroundWindow returns the handle (a numeric id) of the window the user is currently typing into.
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetForegroundWindow();

    // GetWindowText copies a window's title bar text into a StringBuilder buffer. CharSet.Unicode = use the wide-character version so non-English titles survive.
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    // GetWindowTextLength returns how many characters the window's title has.
    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    // GetWindowThreadProcessId returns the thread that owns a window and, through the out parameter, the process id.
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    // SetForegroundWindow asks Windows to bring a window to the front and give it keyboard focus. Windows may refuse if another program is currently in front.
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    // ShowWindowAsync changes a window's show state without waiting for the owning program to respond; used with SW_RESTORE (9) to un-minimise before focusing.
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    // Collects the current state of the desktop (front window, up to 8 visible apps, up to 8 workspace entries, process count, drives) into one snapshot object.
    private DesktopContextSnapshot BuildContextSnapshot()
    {
        var foregroundWindow = TryGetForegroundWindowInfo();
        var visibleApps = GetVisibleApplications()
            .Take(8)
            .Select(app => $"{app.DisplayName} [{app.ProcessName}]")
            .ToArray();
        var workspaceEntries = GetWorkspaceEntries()
            .Take(8)
            .ToArray();

        return new DesktopContextSnapshot(
            DateTimeOffset.Now,
            foregroundWindow?.ProcessName ?? string.Empty,
            foregroundWindow?.WindowTitle ?? string.Empty,
            visibleApps,
            workspaceEntries,
            Process.GetProcesses().Length,
            GetDriveSummary());
    }

    // Active app name for display, or "Unavailable".
    private static string DescribeActiveApplication(DesktopContextSnapshot snapshot)
    {
        return string.IsNullOrWhiteSpace(snapshot.ActiveApplication)
            ? "Unavailable"
            : snapshot.ActiveApplication.Trim();
    }

    // Returns "Unavailable" for blank text, otherwise the trimmed text.
    private static string OrUnavailable(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "Unavailable" : value.Trim();
    }

    // One launchable thing in the app catalog: its name, what to run, where the entry came from (see GetCandidatePriority), whether a window is open now (with its handle and process), and optional command-line arguments.
    private sealed record ApplicationCandidate(
        string DisplayName,
        string LaunchTarget,
        string Source,
        bool IsRunning,
        IntPtr WindowHandle,
        int ProcessId,
        string ProcessName,
        string WindowTitle,
        string LaunchArguments = "");

    // The window currently in front: its program name, process id and title bar text.
    private sealed record ForegroundWindowInfo(string ProcessName, int ProcessId, string WindowTitle);

}

// Snapshot of what the desktop looks like right now; returned by GetContextSnapshotAsync.
public sealed record DesktopContextSnapshot(
    DateTimeOffset LocalTime,
    string ActiveApplication,
    string ActiveWindow,
    string[] VisibleApplications,
    string[] WorkspaceEntries,
    int RunningProcesses,
    string DriveSummary);
