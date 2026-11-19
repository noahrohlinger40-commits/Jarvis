// ExternalToolIntegration.cs: helpers for running an outside program as a plug-in and reading what it prints.
// The only caller is WakeWordDetector.cs, which runs a configured "wake engine" program (described by a
// manifest such as data/wake-engines/*.json) to decide whether the user said the wake word. Two classes:
// ConfiguredExternalProcessRunner starts the program safely, ExternalWakeEngineResponseParser interprets its output.
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Jarvis.Core;

namespace Jarvis.App;

// The outcome of running an external program: success flag, exit code, captured output and error text, whether it was killed for taking too long, and the full path actually run.
internal sealed record ExternalProcessExecutionResult(
    bool Succeeded,
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut,
    string ExecutablePath);

// Starts a configured program, waits for it within a time limit, and returns its output. Ordinary failures come back as Succeeded = false, never as exceptions.
internal static class ConfiguredExternalProcessRunner
{
    // Manifest settings may contain placeholders like {wakePhrase}; "definitionDirectory" is the manifest's folder, used to resolve relative paths.
    public static async Task<ExternalProcessExecutionResult> RunAsync(
        string executablePath,
        string arguments,
        string workingDirectory,
        string definitionDirectory,
        IReadOnlyDictionary<string, string> environmentVariables,
        IReadOnlyDictionary<string, string> placeholders,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        // Fill in {placeholder} markers in the program path, arguments and working directory.
        var expandedExecutablePath = ExpandTemplate(executablePath, placeholders);
        var expandedArguments = ExpandTemplate(arguments, placeholders);
        var expandedWorkingDirectory = ExpandTemplate(workingDirectory, placeholders);
        var resolvedExecutablePath = ResolveExecutablePath(expandedExecutablePath, definitionDirectory);

        // Nothing to run.
        if (string.IsNullOrWhiteSpace(resolvedExecutablePath))
        {
            return new ExternalProcessExecutionResult(
                Succeeded: false,
                ExitCode: -1,
                StandardOutput: string.Empty,
                StandardError: "No executable was configured.",
                TimedOut: false,
                ExecutablePath: string.Empty);
        }

        var resolvedWorkingDirectory = ResolveWorkingDirectory(expandedWorkingDirectory, definitionDirectory);

        // Run without a window, with output captured instead of shown, and no shell (so no cmd.exe quoting surprises).
        var startInfo = new ProcessStartInfo
        {
            FileName = resolvedExecutablePath,
            Arguments = expandedArguments,
            WorkingDirectory = resolvedWorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // Pass the placeholders and manifest settings to the program as environment variables too.
        foreach (var pair in BuildEnvironmentVariables(environmentVariables, placeholders))
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }

        using var process = new Process
        {
            StartInfo = startInfo
        };

        // Start can fail (file not found, no permission); report that as a normal failed result.
        try
        {
            if (!process.Start())
            {
                return new ExternalProcessExecutionResult(
                    Succeeded: false,
                    ExitCode: -1,
                    StandardOutput: string.Empty,
                    StandardError: "The configured process could not be started.",
                    TimedOut: false,
                    ExecutablePath: resolvedExecutablePath);
            }
        }
        catch (Exception exception)
        {
            return new ExternalProcessExecutionResult(
                Succeeded: false,
                ExitCode: -1,
                StandardOutput: string.Empty,
                StandardError: exception.Message,
                TimedOut: false,
                ExecutablePath: resolvedExecutablePath);
        }

        // Keep the time limit between 0.25 s and 60 s whatever the manifest asks for.
        var timeout = Math.Clamp(timeoutMilliseconds, 250, 60000);
        // Read output right away: a program that fills its output buffer while nobody reads would freeze.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var waitForExitTask = process.WaitForExitAsync(cancellationToken);
        // Timeout: whichever finishes first, the process exiting or the delay, wins.
        var timeoutTask = Task.Delay(timeout, cancellationToken);
        var completedTask = await Task.WhenAny(waitForExitTask, timeoutTask).ConfigureAwait(false);

        // Timed out: kill the program (and anything it started) and return whatever it printed so far.
        if (completedTask == timeoutTask)
        {
            TryTerminate(process);
            var timeoutOutput = await SafeReadAsync(stdoutTask).ConfigureAwait(false);
            var timeoutError = await SafeReadAsync(stderrTask).ConfigureAwait(false);
            return new ExternalProcessExecutionResult(
                Succeeded: false,
                ExitCode: process.HasExited ? process.ExitCode : -1,
                StandardOutput: timeoutOutput,
                StandardError: string.IsNullOrWhiteSpace(timeoutError)
                    ? $"The process did not finish within {timeout} ms."
                    : timeoutError,
                TimedOut: true,
                ExecutablePath: resolvedExecutablePath);
        }

        // Finished in time: now it is safe to collect the output.
        await waitForExitTask.ConfigureAwait(false);
        var standardOutput = await SafeReadAsync(stdoutTask).ConfigureAwait(false);
        var standardError = await SafeReadAsync(stderrTask).ConfigureAwait(false);

        return new ExternalProcessExecutionResult(
            Succeeded: process.ExitCode == 0,
            ExitCode: process.ExitCode,
            StandardOutput: standardOutput,
            StandardError: standardError,
            TimedOut: false,
            ExecutablePath: resolvedExecutablePath);
    }

    // Replace every {name} in a template with its value, ignoring letter case. Example: "--phrase {wakePhrase}" becomes "--phrase hey jarvis".
    public static string ExpandTemplate(string? template, IReadOnlyDictionary<string, string> placeholders)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return string.Empty;
        }

        var expanded = template;

        foreach (var pair in placeholders)
        {
            expanded = expanded.Replace("{" + pair.Key + "}", pair.Value ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        return expanded;
    }

    // Turn a path from the manifest into an absolute one: absolute paths stay, relative ones are taken from the manifest's folder.
    public static string ResolveOptionalPath(string? path, string definitionDirectory, IReadOnlyDictionary<string, string>? placeholders = null)
    {
        var expanded = placeholders is null
            ? path?.Trim() ?? string.Empty
            : ExpandTemplate(path, placeholders);

        if (string.IsNullOrWhiteSpace(expanded))
        {
            return string.Empty;
        }

        if (Path.IsPathRooted(expanded))
        {
            return Path.GetFullPath(expanded);
        }

        return Path.GetFullPath(Path.Combine(definitionDirectory, expanded));
    }

    // The standard set of {placeholders} offered to wake-engine manifests: app folder, assistant name, microphone device, wake phrase, audio-processing profile names, and model/asset paths.
    public static Dictionary<string, string> BuildCommonPlaceholders(
        JarvisOptions options,
        ResolvedMicrophoneProcessingProfile profile)
    {
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["workspaceRoot"] = Directory.GetCurrentDirectory(),
            ["assistantName"] = options.AssistantName ?? string.Empty,
            ["deviceName"] = options.SpeechRecognitionDeviceName?.Trim() ?? string.Empty,
            ["wakePhrase"] = options.WakePhrase?.Trim() ?? string.Empty,
            ["beamformingProfile"] = profile.EffectiveBeamformingProfileName,
            ["hardwareDspProfile"] = profile.EffectiveHardwareDspProfileName,
            ["wakeWordAssetPath"] = profile.EffectiveWakeWordAssetPath,
            ["wakeEnginePath"] = profile.EffectiveWakeEnginePath,
            ["speechRecognitionModel"] = options.SpeechRecognitionModel?.Trim() ?? string.Empty
        };
    }

    // A bare name like "python" is left alone so Windows searches PATH; anything with a slash or ending in .exe/.cmd/.bat is treated as a file relative to the manifest.
    private static string ResolveExecutablePath(string path, string definitionDirectory)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        if (Path.IsPathRooted(path))
        {
            return Path.GetFullPath(path);
        }

        if (path.Contains(Path.DirectorySeparatorChar)
            || path.Contains(Path.AltDirectorySeparatorChar)
            || path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFullPath(Path.Combine(definitionDirectory, path));
        }

        return path;
    }

    // Use the manifest's folder when no working directory is given.
    private static string ResolveWorkingDirectory(string path, string definitionDirectory)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return definitionDirectory;
        }

        return Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(definitionDirectory, path));
    }

    // Environment for the child program: JARVIS_WORKSPACE_ROOT, one JARVIS_* variable per placeholder, then the manifest's own variables (which win on conflicts).
    private static IReadOnlyDictionary<string, string> BuildEnvironmentVariables(
        IReadOnlyDictionary<string, string> manifestVariables,
        IReadOnlyDictionary<string, string> placeholders)
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["JARVIS_WORKSPACE_ROOT"] = Directory.GetCurrentDirectory()
        };

        foreach (var pair in placeholders)
        {
            var envName = BuildEnvironmentVariableName(pair.Key);

            if (!string.IsNullOrWhiteSpace(envName))
            {
                environment[envName] = pair.Value ?? string.Empty;
            }
        }

        foreach (var pair in manifestVariables)
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
            {
                continue;
            }

            environment[pair.Key.Trim()] = ExpandTemplate(pair.Value, placeholders);
        }

        return environment;
    }

    // Name for a placeholder's environment variable, e.g. "wakePhrase" becomes JARVIS_WAKEPHRASE and "wake.path" becomes JARVIS_WAKE_PATH.
    private static string BuildEnvironmentVariableName(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return string.Empty;
        }

        var builder = new StringBuilder("JARVIS_");

        foreach (var character in key)
        {
            builder.Append(char.IsLetterOrDigit(character)
                ? char.ToUpperInvariant(character)
                : '_');
        }

        return builder.ToString();
    }

    // Read a finished task's text, or an empty string if reading failed (for example after the process was killed).
    private static async Task<string> SafeReadAsync(Task<string> task)
    {
        try
        {
            return await task.ConfigureAwait(false);
        }
        catch
        {
            return string.Empty;
        }
    }

    // Best-effort kill of the whole process tree; errors are ignored because the process may already have exited.
    private static void TryTerminate(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }
}

// Turns whatever a wake-engine program printed into a WakeWordDetectionResult.
// Accepted shapes, in order: nothing (not detected); the word true, 1 or detected; plain text containing the wake phrase; or a JSON object such as {"detected": true, "confidence": 0.9, "shouldArmOnly": true}.
internal static class ExternalWakeEngineResponseParser
{
    public static WakeWordDetectionResult Parse(
        string output,
        JarvisOptions options,
        IReadOnlyList<string> wakeChoices,
        Func<string, (bool Matched, string Alias, string InlineCommand)> tryExtractWakeCommand,
        double minimumConfidence)
    {
        var trimmedOutput = output?.Trim() ?? string.Empty;

        // Empty output means no detection.
        if (string.IsNullOrWhiteSpace(trimmedOutput))
        {
            return WakeWordDetectionResult.None;
        }

        // A bare "true" / "1" / "detected" counts as a detection with no command attached.
        if (string.Equals(trimmedOutput, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmedOutput, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmedOutput, "detected", StringComparison.OrdinalIgnoreCase))
        {
            return new WakeWordDetectionResult(
                IsDetected: true,
                Alias: wakeChoices.FirstOrDefault() ?? options.WakePhrase,
                Confidence: minimumConfidence,
                ShouldArmOnly: true,
                MatchOffset: TimeSpan.Zero,
                MatchDuration: TimeSpan.Zero);
        }

        // Maybe the engine printed a transcript: clean it up and look for the wake phrase (and a command after it).
        var normalizedText = VoiceRecognitionText.NormalizeTranscript(trimmedOutput, options);
        var extractedText = tryExtractWakeCommand(normalizedText);

        if (extractedText.Matched)
        {
            return new WakeWordDetectionResult(
                IsDetected: true,
                Alias: extractedText.Alias,
                Confidence: minimumConfidence,
                ShouldArmOnly: string.IsNullOrWhiteSpace(extractedText.InlineCommand),
                MatchOffset: TimeSpan.Zero,
                MatchDuration: TimeSpan.Zero,
                RecognizedText: normalizedText,
                InlineCommand: extractedText.InlineCommand);
        }

        // Otherwise expect JSON. Several property names are accepted for the same idea because engines differ.
        try
        {
            using var document = JsonDocument.Parse(trimmedOutput);
            var root = document.RootElement;
            var isDetected = TryGetBoolean(root, "detected")
                || TryGetBoolean(root, "isDetected")
                || TryGetBoolean(root, "wakeDetected");

            if (!isDetected)
            {
                return WakeWordDetectionResult.None;
            }

            var confidence = TryGetDouble(root, "confidence") ?? minimumConfidence;
            var alias = TryGetString(root, "alias")
                ?? wakeChoices.FirstOrDefault()
                ?? options.WakePhrase;
            var recognizedText = TryGetString(root, "recognizedText")
                ?? TryGetString(root, "text")
                ?? string.Empty;
            var inlineCommand = TryGetString(root, "inlineCommand")
                ?? TryGetString(root, "command")
                ?? string.Empty;
            // "Arm only" means: wake word heard, now listen for the command, rather than treating this utterance as the command.
            var shouldArmOnly = TryGetBoolean(root, "shouldArmOnly");

            if (!string.IsNullOrWhiteSpace(recognizedText))
            {
                var normalized = VoiceRecognitionText.NormalizeTranscript(recognizedText, options);
                var extracted = tryExtractWakeCommand(normalized);

                if (extracted.Matched)
                {
                    alias = string.IsNullOrWhiteSpace(alias) ? extracted.Alias : alias;

                    if (string.IsNullOrWhiteSpace(inlineCommand))
                    {
                        inlineCommand = extracted.InlineCommand;
                    }
                }

                recognizedText = normalized;
            }

            // With no command included, the engine can only have woken Jarvis up.
            if (string.IsNullOrWhiteSpace(inlineCommand))
            {
                shouldArmOnly = true;
            }

            return new WakeWordDetectionResult(
                IsDetected: true,
                Alias: alias,
                Confidence: confidence,
                ShouldArmOnly: shouldArmOnly,
                MatchOffset: TimeSpan.FromMilliseconds(TryGetDouble(root, "matchOffsetMs") ?? 0),
                MatchDuration: TimeSpan.FromMilliseconds(TryGetDouble(root, "matchDurationMs") ?? 0),
                RecognizedText: recognizedText,
                InlineCommand: inlineCommand);
        }
        // Not valid JSON or unexpected shape: treat as not detected.
        catch
        {
            return WakeWordDetectionResult.None;
        }
    }

    // Small JSON readers that return a default instead of throwing when a property is missing or has the wrong type. TryGetBoolean also accepts "true" strings and non-zero numbers.
    private static string? TryGetString(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
    }

    private static bool TryGetBoolean(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var element))
        {
            return false;
        }

        return element.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(element.GetString(), out var value) && value,
            JsonValueKind.Number => element.TryGetInt32(out var numericValue) && numericValue != 0,
            _ => false
        };
    }

    private static double? TryGetDouble(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetDouble(out var value)
                ? value
                : null;
    }
}
