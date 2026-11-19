// DesktopAutomation.Workflows.cs: the "do things to files and notifications" slice of DesktopAutomationService.
// Holds write/replace/move/copy/delete file tools with their safety checks (no editing Windows or Program Files),
// app state reporting and the Jarvis toast notification. Called by the tool layer through IDesktopAutomationService.
using System.Diagnostics;
using System.Security;
using System.Text;
using static Jarvis.Core.Win32UiAutomation;

namespace Jarvis.Core;

public sealed partial class DesktopAutomationService
{
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
        var replacementCount = original.Split(findText).Length - 1;

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

    // Tool: show a Windows toast notification.
    public async Task<string> SendNotificationAsync(string title, string message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedTitle = string.IsNullOrWhiteSpace(title) ? "Jarvis" : title.Trim();
        var normalizedMessage = (message ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(normalizedTitle) && string.IsNullOrWhiteSpace(normalizedMessage))
        {
            return "Usage: notify <title> :: <message>";
        }

        var toastAttempt = await TryShowToastNotificationAsync(normalizedTitle, normalizedMessage, cancellationToken);

        if (toastAttempt.Succeeded)
        {
            return $"Sent notification: {normalizedTitle}";
        }

        return string.IsNullOrWhiteSpace(toastAttempt.Error)
            ? "Could not show the notification: toast display was unavailable on this machine."
            : $"Could not show the notification: toast display was unavailable. Detail: {toastAttempt.Error}";
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

            // A non-zero exit code means PowerShell could not show the toast; its error text (shortened) is returned.
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
}
