// DesktopAutomation.Workflows.cs: the "do things to files and notifications" slice of DesktopAutomationService.
// Holds write/replace/move/copy/delete file tools with their safety checks (no editing Windows or Program Files),
// app state reporting, clipboard history and the Jarvis notification history/toast. Called by the tool layer through IDesktopAutomationService.
// History is stored as small JSON files under data\automation.
using System.Diagnostics;
using System.Security;
using System.Text;
using System.Text.Json;

namespace Jarvis.Core;

public sealed partial class DesktopAutomationService
{
    // JSON settings for the history files: web-style camelCase names, human-readable indentation.
    private static readonly JsonSerializerOptions AutomationJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    // Tool: write (or append to) a text file, creating folders as needed. Refuses system folders and binary files.
    public async Task<string> WriteFileAsync(string target, string content, bool append, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedContent = content ?? string.Empty;

        if (string.IsNullOrWhiteSpace(target))
        {
            return append
                ? "Usage: append file <path> :: <content>"
                : "Usage: write file <path> :: <content>";
        }

        var resolvedPath = ResolveSafePath(target);

        if (File.Exists(resolvedPath) && await IsLikelyBinaryAsync(resolvedPath, cancellationToken))
        {
            return $"File appears to be binary and cannot be edited safely: {resolvedPath}";
        }

        var parentDirectory = Path.GetDirectoryName(resolvedPath);

        if (!string.IsNullOrWhiteSpace(parentDirectory))
        {
            Directory.CreateDirectory(parentDirectory);
        }

        if (append)
        {
            await File.AppendAllTextAsync(resolvedPath, normalizedContent, cancellationToken);
        }
        else
        {
            await File.WriteAllTextAsync(resolvedPath, normalizedContent, cancellationToken);
        }

        var fileInfo = new FileInfo(resolvedPath);
        var action = append ? "Appended" : "Wrote";
        return $"{action} {normalizedContent.Length} characters to {resolvedPath} ({FormatBytes(fileInfo.Length)}).";
    }

    // Tool: replace every occurrence of one piece of text in a text file. Matching is exact (case-sensitive); reports how many were changed.
    public async Task<string> ReplaceInFileAsync(string target, string findText, string replaceText, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(target))
        {
            return "Usage: replace in file <path> :: <find> :: <replace>";
        }

        if (string.IsNullOrEmpty(findText))
        {
            return "A find value is required for replace in file.";
        }

        var resolvedPath = ResolveSafePath(target);

        if (!File.Exists(resolvedPath))
        {
            return $"File not found: {resolvedPath}";
        }

        if (await IsLikelyBinaryAsync(resolvedPath, cancellationToken))
        {
            return $"File appears to be binary and cannot be edited safely: {resolvedPath}";
        }

        var original = await File.ReadAllTextAsync(resolvedPath, cancellationToken);
        var replacementCount = CountOccurrences(original, findText);

        if (replacementCount == 0)
        {
            return $"No matches for the requested text were found in {resolvedPath}.";
        }

        var updated = original.Replace(findText, replaceText ?? string.Empty, StringComparison.Ordinal);
        await File.WriteAllTextAsync(resolvedPath, updated, cancellationToken);

        return $"Replaced {replacementCount} occurrence(s) in {resolvedPath}.";
    }

    // Tool: move a file or folder. A file move overwrites an existing destination file; a folder move refuses if the destination folder exists.
    public Task<string> MovePathAsync(string source, string destination, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(destination))
        {
            return Task.FromResult("Usage: move path <source> :: <destination>");
        }

        var resolvedSource = ResolveSafePath(source);

        if (!File.Exists(resolvedSource) && !Directory.Exists(resolvedSource))
        {
            return Task.FromResult($"Path not found: {resolvedSource}");
        }

        var resolvedDestination = ResolveSafeDestinationPath(resolvedSource, destination);

        if (string.Equals(resolvedSource, resolvedDestination, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult("Source and destination resolve to the same path.");
        }

        if (File.Exists(resolvedSource))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(resolvedDestination)!);
            File.Move(resolvedSource, resolvedDestination, overwrite: true);
            return Task.FromResult($"Moved file to {resolvedDestination}.");
        }

        if (Directory.Exists(resolvedDestination))
        {
            return Task.FromResult($"Destination directory already exists: {resolvedDestination}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(resolvedDestination)!);
        Directory.Move(resolvedSource, resolvedDestination);
        return Task.FromResult($"Moved folder to {resolvedDestination}.");
    }

    // Tool: copy a file or folder (folders are copied with everything inside).
    public Task<string> CopyPathAsync(string source, string destination, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(destination))
        {
            return Task.FromResult("Usage: copy path <source> :: <destination>");
        }

        var resolvedSource = ResolveSafePath(source);

        if (!File.Exists(resolvedSource) && !Directory.Exists(resolvedSource))
        {
            return Task.FromResult($"Path not found: {resolvedSource}");
        }

        var resolvedDestination = ResolveSafeDestinationPath(resolvedSource, destination);

        if (string.Equals(resolvedSource, resolvedDestination, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult("Source and destination resolve to the same path.");
        }

        if (File.Exists(resolvedSource))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(resolvedDestination)!);
            File.Copy(resolvedSource, resolvedDestination, overwrite: true);
            return Task.FromResult($"Copied file to {resolvedDestination}.");
        }

        CopyDirectory(resolvedSource, resolvedDestination);
        return Task.FromResult($"Copied folder to {resolvedDestination}.");
    }

    // Tool: delete a file or folder (folders recursively). Protected system paths are rejected by ResolveSafePath, and the workspace root or a drive root is refused here.
    public Task<string> DeletePathAsync(string target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(target))
        {
            return Task.FromResult("Usage: delete path <target>");
        }

        var resolvedPath = ResolveSafePath(target);

        if (IsDangerousDeleteTarget(resolvedPath))
        {
            return Task.FromResult($"Refusing to delete an unsafe root-level target: {resolvedPath}");
        }

        if (File.Exists(resolvedPath))
        {
            File.Delete(resolvedPath);
            return Task.FromResult($"Deleted file: {resolvedPath}");
        }

        if (Directory.Exists(resolvedPath))
        {
            Directory.Delete(resolvedPath, recursive: true);
            return Task.FromResult($"Deleted folder: {resolvedPath}");
        }

        return Task.FromResult($"Path not found: {resolvedPath}");
    }

    // Tool: report whether an app is visible, running or just installed, with process id and memory use, for up to 5 matches.
    public Task<string> GetApplicationStateAsync(string target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(target))
        {
            return Task.FromResult("Usage: app state <app>");
        }

        var snapshot = BuildContextSnapshot();
        var candidates = FindApplicationCandidates(target.Trim())
            .Take(5)
            .ToArray();

        if (candidates.Length == 0)
        {
            return Task.FromResult($"No matching application candidates were found for \"{target.Trim()}\".");
        }

        var lines = new List<string>
        {
            $"Application state for \"{target.Trim()}\":",
            $"- Foreground app: {OrUnavailable(snapshot.ActiveApplication)}",
            $"- Foreground window: {OrUnavailable(snapshot.ActiveWindow)}"
        };

        foreach (var candidate in candidates)
        {
            var state = candidate.WindowHandle != IntPtr.Zero
                ? "visible"
                : candidate.IsRunning
                    ? "running"
                    : "available";
            var memory = candidate.ProcessId > 0 ? TryDescribeProcessMemory(candidate.ProcessId) : string.Empty;
            var launchTarget = TrimForAutomationDisplay(candidate.LaunchTarget, 96);
            var windowTitle = TrimForAutomationDisplay(candidate.WindowTitle, 100);
            var source = string.IsNullOrWhiteSpace(candidate.Source) ? "unknown source" : candidate.Source;

            lines.Add(
                $"- {candidate.DisplayName}: {state} | source {source} | process {candidate.ProcessName} | pid {(candidate.ProcessId <= 0 ? "n/a" : candidate.ProcessId)}{(string.IsNullOrWhiteSpace(memory) ? string.Empty : $" | memory {memory}")}");

            if (!string.IsNullOrWhiteSpace(windowTitle))
            {
                lines.Add($"  window: {windowTitle}");
            }

            if (!string.IsNullOrWhiteSpace(launchTarget))
            {
                lines.Add($"  launch: {launchTarget}");
            }
        }

        return Task.FromResult(string.Join(Environment.NewLine, lines));
    }

    // Tool: show the last 10 clipboard entries. It first records whatever is on the clipboard now so the list is current.
    public async Task<string> GetClipboardHistoryAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (Win32UiAutomation.TryGetClipboardText(out var text, out _) && !string.IsNullOrWhiteSpace(text))
        {
            await CaptureClipboardHistoryEntryAsync(text, cancellationToken);
        }

        var history = await LoadClipboardHistoryAsync(cancellationToken);

        if (history.Count == 0)
        {
            return "Clipboard history is empty.";
        }

        var lines = history
            .Take(10)
            .Select((entry, index) =>
                $"- {index + 1}. {entry.CapturedAtUtc.ToLocalTime():yyyy-MM-dd h:mm tt} | {TrimForAutomationDisplay(entry.Text, 100)}")
            .ToArray();

        return "Clipboard history:" + Environment.NewLine + string.Join(Environment.NewLine, lines);
    }

    // Tool: put an older clipboard entry (counted from 1, newest first) back on the clipboard.
    public async Task<string> RestoreClipboardHistoryAsync(int index, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var history = await LoadClipboardHistoryAsync(cancellationToken);

        if (history.Count == 0)
        {
            return "Clipboard history is empty.";
        }

        if (index < 1 || index > history.Count)
        {
            return $"Clipboard history index must be between 1 and {history.Count}.";
        }

        var entry = history[index - 1];
        var setResult = await Win32UiAutomation.SetClipboardTextAsync(entry.Text);

        if (!UiCommandParsing.LooksLikeFailure(setResult))
        {
            await CaptureClipboardHistoryEntryAsync(entry.Text, cancellationToken);
        }

        return UiCommandParsing.LooksLikeFailure(setResult)
            ? setResult
            : $"Restored clipboard history item {index}: {TrimForAutomationDisplay(entry.Text, 100)}";
    }

    // Tool: show a Windows toast notification and also store it in Jarvis's own history (kept to the newest 30), so there is a record even if the toast could not be shown.
    public async Task<string> SendNotificationAsync(string title, string message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedTitle = string.IsNullOrWhiteSpace(title) ? "Jarvis" : title.Trim();
        var normalizedMessage = (message ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(normalizedTitle) && string.IsNullOrWhiteSpace(normalizedMessage))
        {
            return "Usage: notify <title> :: <message>";
        }

        var notifications = await LoadNotificationsAsync(cancellationToken);
        var toastAttempt = await TryShowToastNotificationAsync(normalizedTitle, normalizedMessage, cancellationToken);
        notifications.Insert(0, new AutomationNotificationRecord(
            Guid.NewGuid().ToString("N"),
            normalizedTitle,
            normalizedMessage,
            DateTimeOffset.UtcNow,
            toastAttempt.Succeeded,
            toastAttempt.Error));
        notifications = notifications.Take(30).ToList();
        await SaveNotificationsAsync(notifications, cancellationToken);

        if (toastAttempt.Succeeded)
        {
            return $"Sent notification: {normalizedTitle}";
        }

        return string.Join(
            Environment.NewLine,
            $"Recorded notification: {normalizedTitle}",
            string.IsNullOrWhiteSpace(toastAttempt.Error)
                ? "Toast display was unavailable on this machine, so the notification was only stored in Jarvis history."
                : $"Toast display was unavailable, so the notification was only stored in Jarvis history. Detail: {toastAttempt.Error}");
    }

    // Tool: list the 12 most recent stored notifications.
    public async Task<string> ListNotificationsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var notifications = await LoadNotificationsAsync(cancellationToken);

        if (notifications.Count == 0)
        {
            return "No Jarvis notifications are recorded.";
        }

        var lines = notifications
            .Take(12)
            .Select(entry =>
            {
                var preview = string.IsNullOrWhiteSpace(entry.Message)
                    ? "[no message]"
                    : TrimForAutomationDisplay(entry.Message, 100);
                var delivery = entry.ToastDisplayed ? "toast shown" : "history only";
                return $"- {entry.CreatedAtUtc.ToLocalTime():yyyy-MM-dd h:mm tt} | {entry.Title} | {delivery} | {preview}";
            })
            .ToArray();

        return "Jarvis notifications:" + Environment.NewLine + string.Join(Environment.NewLine, lines);
    }

    // Tool: delete the notification history.
    public async Task<string> ClearNotificationsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await SaveNotificationsAsync([], cancellationToken);
        return "Cleared the Jarvis notification history.";
    }

    // Saves clipboard text to history: skips blanks and repeats of the newest entry, keeps the newest 20.
    private async Task CaptureClipboardHistoryEntryAsync(string text, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalized = NormalizeClipboardText(text);

        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        var history = await LoadClipboardHistoryAsync(cancellationToken);

        if (history.Count > 0 && string.Equals(history[0].Text, normalized, StringComparison.Ordinal))
        {
            return;
        }

        history.Insert(0, new ClipboardHistoryRecord(
            Guid.NewGuid().ToString("N"),
            normalized,
            DateTimeOffset.UtcNow));
        history = history
            .Take(20)
            .ToList();
        await SaveJsonAsync(GetClipboardHistoryPath(), history, cancellationToken);
    }

    // Small loaders/savers for the two history files.
    private async Task<List<ClipboardHistoryRecord>> LoadClipboardHistoryAsync(CancellationToken cancellationToken)
    {
        return await LoadJsonAsync(GetClipboardHistoryPath(), cancellationToken, static () => new List<ClipboardHistoryRecord>());
    }

    private async Task<List<AutomationNotificationRecord>> LoadNotificationsAsync(CancellationToken cancellationToken)
    {
        return await LoadJsonAsync(GetNotificationHistoryPath(), cancellationToken, static () => new List<AutomationNotificationRecord>());
    }

    private async Task SaveNotificationsAsync(IReadOnlyList<AutomationNotificationRecord> notifications, CancellationToken cancellationToken)
    {
        await SaveJsonAsync(GetNotificationHistoryPath(), notifications, cancellationToken);
    }

    // Reads a JSON file; a missing or damaged file quietly gives a fresh default value instead of an error.
    private async Task<T> LoadJsonAsync<T>(string path, CancellationToken cancellationToken, Func<T> createDefault)
    {
        try
        {
            if (!File.Exists(path))
            {
                return createDefault();
            }

            await using var stream = File.OpenRead(path);
            var document = await JsonSerializer.DeserializeAsync<T>(stream, AutomationJsonOptions, cancellationToken);
            return document ?? createDefault();
        }
        catch
        {
            return createDefault();
        }
    }

    // Writes a JSON file, creating its folder first.
    private async Task SaveJsonAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path);

        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, value, AutomationJsonOptions, cancellationToken);
    }

    // Shows a Windows toast by running a tiny PowerShell script (the toast API is a Windows Runtime API that is simple to reach from PowerShell). The title and message are XML-escaped first so characters like < or & cannot break the toast XML.
    private async Task<(bool Succeeded, string Error)> TryShowToastNotificationAsync(
        string title,
        string message,
        CancellationToken cancellationToken)
    {
        var escapedTitle = SecurityElement.Escape(title) ?? string.Empty;
        var escapedMessage = SecurityElement.Escape(message) ?? string.Empty;
        var xml = "<toast><visual><binding template='ToastGeneric'>" +
                  $"<text>{escapedTitle}</text><text>{escapedMessage}</text>" +
                  "</binding></visual></toast>";
        // The script is passed as -EncodedCommand (base64 of UTF-16 text), which avoids any quoting problems with the XML inside it. The XML itself goes in a PowerShell here-string.
        // The notifier name 'Jarvis' is the app name that Windows shows on the toast.
        var script = string.Join(
            Environment.NewLine,
            "Add-Type -AssemblyName System.Runtime.WindowsRuntime",
            "[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] > $null",
            "[Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime] > $null",
            "$xml = @'",
            xml,
            "'@",
            "$doc = New-Object Windows.Data.Xml.Dom.XmlDocument",
            "$doc.LoadXml($xml)",
            "$toast = [Windows.UI.Notifications.ToastNotification]::new($doc)",
            "[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('Jarvis').Show($toast)");
        var encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell",
            Arguments = $"-NoProfile -NonInteractive -EncodedCommand {encodedCommand}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            using var process = Process.Start(startInfo);

            if (process is null)
            {
                return (false, "PowerShell could not be started for toast delivery.");
            }

            // Both output streams are read while waiting so a full pipe cannot freeze the process.
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var stdout = (await stdoutTask).Trim();
            var stderr = (await stderrTask).Trim();

            if (process.ExitCode == 0)
            {
                return (true, string.Empty);
            }

            // A non-zero exit code means PowerShell could not show the toast; its error text (shortened) is returned so it can be stored.
            var error = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            return (false, TrimForAutomationDisplay(error, 180));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return (false, TrimForAutomationDisplay(exception.Message, 180));
        }
    }

    // The two ResolveSafe... methods turn the text from the model into a full path and reject protected system locations before any file is touched.
    private string ResolveSafePath(string target)
    {
        var resolvedPath = ResolvePath(target);
        EnsurePathIsAllowed(resolvedPath);
        return resolvedPath;
    }

    private string ResolveSafeDestinationPath(string sourcePath, string destination)
    {
        var resolvedDestination = ResolveSafePath(destination);

        if (Directory.Exists(resolvedDestination))
        {
            var sourceName = File.Exists(sourcePath)
                ? Path.GetFileName(sourcePath)
                : new DirectoryInfo(sourcePath).Name;
            resolvedDestination = Path.Combine(resolvedDestination, sourceName);
        }

        EnsurePathIsAllowed(resolvedDestination);
        return resolvedDestination;
    }

    // Throws if the path is empty or inside a protected system folder.
    private void EnsurePathIsAllowed(string resolvedPath)
    {
        if (string.IsNullOrWhiteSpace(resolvedPath))
        {
            throw new InvalidOperationException("Path resolution failed.");
        }

        if (IsProtectedSystemPath(resolvedPath))
        {
            throw new InvalidOperationException($"Protected system locations cannot be modified: {resolvedPath}");
        }
    }

    // Extra delete-only guard: never delete the workspace root itself or a whole drive.
    private bool IsDangerousDeleteTarget(string resolvedPath)
    {
        if (string.IsNullOrWhiteSpace(resolvedPath))
        {
            return true;
        }

        var fullPath = Path.GetFullPath(resolvedPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var workspacePath = Path.GetFullPath(_workspaceRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (string.Equals(fullPath, workspacePath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var root = Path.GetPathRoot(fullPath)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return !string.IsNullOrWhiteSpace(root)
            && string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase);
    }

    // True for the Windows folder, System32, SysWOW64, Program Files (both), and ProgramData, and anything inside them. The path comparison adds a trailing separator so "C:\Windowsfoo" is not mistaken for "C:\Windows".
    private static bool IsProtectedSystemPath(string path)
    {
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var protectedRoots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SysWOW64"),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
        }
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Select(value => Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        .ToArray();

        return protectedRoots.Any(root =>
            string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    // Copies a folder tree by recursion, overwriting files that already exist.
    private static void CopyDirectory(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);

        foreach (var file in Directory.GetFiles(sourceDirectory))
        {
            var destinationPath = Path.Combine(destinationDirectory, Path.GetFileName(file));
            File.Copy(file, destinationPath, overwrite: true);
        }

        foreach (var directory in Directory.GetDirectories(sourceDirectory))
        {
            var nestedDestination = Path.Combine(destinationDirectory, Path.GetFileName(directory));
            CopyDirectory(directory, nestedDestination);
        }
    }

    // Counts non-overlapping occurrences of the text (exact, case-sensitive), used to report how many replacements were made.
    private static int CountOccurrences(string content, string findText)
    {
        var count = 0;
        var index = 0;

        while (index < content.Length)
        {
            var matchIndex = content.IndexOf(findText, index, StringComparison.Ordinal);

            if (matchIndex < 0)
            {
                break;
            }

            count++;
            index = matchIndex + Math.Max(1, findText.Length);
        }

        return count;
    }

    // Cleans clipboard text for the history: Windows line endings, trimmed, at most 12,000 characters.
    private static string NormalizeClipboardText(string text)
    {
        const int maxChars = 12000;
        var normalized = (text ?? string.Empty).ReplaceLineEndings(Environment.NewLine).Trim();

        if (normalized.Length <= maxChars)
        {
            return normalized;
        }

        return normalized[..maxChars];
    }

    // Working-set memory (the RAM a process is using right now) in readable form, or empty if the process is gone.
    private static string TryDescribeProcessMemory(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return FormatBytes(process.WorkingSet64);
        }
        catch
        {
            return string.Empty;
        }
    }

    // Shortens text to maxLength using the shared Clip extension.
    private static string TrimForAutomationDisplay(string value, int maxLength) => value.Clip(maxLength);

    private string GetClipboardHistoryPath() => JarvisDataPaths.Resolve("data/automation/clipboard-history.json");

    private string GetNotificationHistoryPath() => JarvisDataPaths.Resolve("data/automation/notifications.json");
}

// One saved clipboard-history entry.
internal sealed record ClipboardHistoryRecord(
    string Id,
    string Text,
    DateTimeOffset CapturedAtUtc);

// One saved notification: whether a toast was shown, and the error if not.
internal sealed record AutomationNotificationRecord(
    string Id,
    string Title,
    string Message,
    DateTimeOffset CreatedAtUtc,
    bool ToastDisplayed,
    string ToastError);
