// AudioDeviceResolver.cs: finds which microphone to record from. Settings store a device name, but NAudio's WaveIn API
// (the older Windows multimedia interface used for recording) wants a numeric index and reports names slightly differently
// from what the user sees, so the name is matched loosely with a score. Falls back to the Windows default microphones.
// Called by the voice runtimes (OpenAiCompatibleVoiceRuntime, VoiceRuntime.cs) and the calibration service.
using System.Text;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Jarvis.App;

// Static helpers for choosing the recording device.
internal static class AudioDeviceResolver
{
    // Returns the device number to record from: the matching device, else the first device (0), else -1 when no microphone exists.
    public static int GetPreferredInputDeviceIndex(string? preferredDeviceName)
    {
        if (TryResolveMicrophoneDevice(preferredDeviceName, out var deviceNumber))
        {
            return deviceNumber;
        }

        return WaveInEvent.DeviceCount > 0 ? 0 : -1;
    }

    // Tries the configured name first, then the names of the Windows default capture devices; false if nothing matches.
    public static bool TryResolveMicrophoneDevice(string? preferredDeviceName, out int deviceNumber)
    {
        deviceNumber = -1;

        if (WaveInEvent.DeviceCount <= 0)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(preferredDeviceName)
            && TryMatchWaveInDevice(preferredDeviceName, out deviceNumber))
        {
            return true;
        }

        foreach (var candidateName in GetDefaultCaptureEndpointNames())
        {
            if (TryMatchWaveInDevice(candidateName, out deviceNumber))
            {
                return true;
            }
        }

        return false;
    }

    // Picks the device whose name scores highest against the wanted name; false if nothing scores above zero.
    private static bool TryMatchWaveInDevice(string candidateName, out int deviceNumber)
    {
        deviceNumber = -1;
        var bestScore = int.MinValue;

        for (var index = 0; index < WaveInEvent.DeviceCount; index++)
        {
            var capabilities = WaveInEvent.GetCapabilities(index);
            var score = ComputeMatchScore(candidateName, capabilities.ProductName);

            if (score <= bestScore)
            {
                continue;
            }

            bestScore = score;
            deviceNumber = index;
        }

        return bestScore > 0 && deviceNumber >= 0;
    }

    // Similarity score between two names: exact 1000; same without spaces 900; one contains the other 700 / 600 (plus the matching
    // length); otherwise 100 per shared word plus word lengths; 0 for nothing in common.
    private static int ComputeMatchScore(string? candidateName, string? productName)
    {
        var normalizedCandidate = Normalize(candidateName);
        var normalizedProduct = Normalize(productName);

        if (string.IsNullOrWhiteSpace(normalizedCandidate)
            || string.IsNullOrWhiteSpace(normalizedProduct))
        {
            return 0;
        }

        if (string.Equals(normalizedCandidate, normalizedProduct, StringComparison.OrdinalIgnoreCase))
        {
            return 1000;
        }

        if (string.Equals(RemoveWhitespace(normalizedCandidate), RemoveWhitespace(normalizedProduct), StringComparison.OrdinalIgnoreCase))
        {
            return 900;
        }

        if (normalizedProduct.Contains(normalizedCandidate, StringComparison.OrdinalIgnoreCase))
        {
            return 700 + Math.Min(normalizedCandidate.Length, 120);
        }

        if (normalizedCandidate.Contains(normalizedProduct, StringComparison.OrdinalIgnoreCase))
        {
            return 600 + Math.Min(normalizedProduct.Length, 120);
        }

        var candidateTokens = Tokenize(normalizedCandidate);
        var productTokens = Tokenize(normalizedProduct);
        var sharedTokens = candidateTokens.Intersect(productTokens, StringComparer.OrdinalIgnoreCase).ToArray();

        if (sharedTokens.Length == 0)
        {
            return 0;
        }

        return (sharedTokens.Length * 100) + sharedTokens.Sum(token => Math.Min(token.Length, 18));
    }

    // Lower-cases and removes null characters and double spaces so names compare cleanly.
    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value
            .Trim()
            .Replace('\u0000', ' ')
            .Replace("  ", " ", StringComparison.Ordinal)
            .ToLowerInvariant();
    }

    // Removes all spaces so "Mic Array" equals "MicArray".
    private static string RemoveWhitespace(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);

        foreach (var character in value)
        {
            if (!char.IsWhiteSpace(character))
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    // Splits a name into unique words, breaking at spaces, dashes, brackets, slashes, commas and dots.
    private static string[] Tokenize(string value)
    {
        return value
            .Split([' ', '-', '(', ')', '[', ']', '/', '\\', ',', '.'], StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    // Asks Windows for the friendly names of the default recording device for each role (communications, console, multimedia).
    // Each role can point to a different device, and a role with no device is skipped.
    private static IEnumerable<string> GetDefaultCaptureEndpointNames()
    {
        using var enumerator = new MMDeviceEnumerator();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var role in new[] { Role.Communications, Role.Console, Role.Multimedia })
        {
            try
            {
                using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, role);

                if (!string.IsNullOrWhiteSpace(device.FriendlyName))
                {
                    names.Add(device.FriendlyName.Trim());
                }
            }
            catch
            {
            }
        }

        return names;
    }
}
