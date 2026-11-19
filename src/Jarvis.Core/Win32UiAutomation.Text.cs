// Win32UiAutomation.Text.cs: third slice of Win32UiAutomation, typing text into a window the way a keyboard would.
// SendTextAsync is called by DesktopAutomationService.SendTextAsync and by TypeIntoElementAsync when no field is named.
// It uses SendInput (declared in Win32UiAutomation.cs) with Unicode events, so any character can be typed, not only keys on the keyboard.
using System.Runtime.InteropServices;
using System.Text;

namespace Jarvis.Core;

internal static partial class Win32UiAutomation
{
    // Flag meaning "treat wScan as a Unicode character instead of a key position", so letters, accents and emoji can be typed without a matching key.
    private const uint KeyeventfUnicode = 0x0004;

    // Tool: focus the window, wait a moment, then type the text into whatever has focus (like a user typing).
    public static async Task<string> SendTextAsync(string targetApp, string text)
    {
        if (!TryResolveWindow(targetApp, out var window))
        {
            return $"Window matching '{targetApp}' was not found.";
        }

        if (!TryBringWindowToFront(window.Handle))
        {
            return $"Unable to focus '{DescribeWindow(window)}' before sending text.";
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return "Usage: send text <window> <text>";
        }

        // 120 ms pause so focus has really moved to the window before the characters are injected.
        await Task.Delay(120).ConfigureAwait(false);

        if (!TrySendUnicodeText(text, out var error))
        {
            return error;
        }

        return $"Sent {DescribeTextLength(text)} to '{DescribeWindow(window)}'.";
    }

    // Builds one key-down and one key-up event per character and sends them all in a single SendInput call.
    private static bool TrySendUnicodeText(string text, out string error)
    {
        var inputs = new List<INPUT>();

        // EnumerateRunes walks the text one full character at a time; characters outside the basic range (many emoji) are two UTF-16 units and are sent as two events, as Windows expects.
        foreach (var rune in text.EnumerateRunes())
        {
            var characters = rune.Utf16SequenceLength == 1
                ? [char.ConvertFromUtf32(rune.Value)[0]]
                : char.ConvertFromUtf32(rune.Value).ToCharArray();

            foreach (var character in characters)
            {
                // Carriage returns are skipped and a newline is sent as a real Enter key press (virtual key 0x0D), because a Unicode "\n" would not start a new line in most programs.
                if (character == '\r')
                {
                    continue;
                }

                if (character == '\n')
                {
                    inputs.Add(CreateKeyboardInput(0x0D, keyUp: false));
                    inputs.Add(CreateKeyboardInput(0x0D, keyUp: true));
                    continue;
                }

                inputs.Add(CreateUnicodeKeyboardInput(character, keyUp: false));
                inputs.Add(CreateUnicodeKeyboardInput(character, keyUp: true));
            }
        }

        if (inputs.Count == 0)
        {
            error = "No text was available to send.";
            return false;
        }

        // If Windows accepts fewer events than were sent (for example another program blocked the input) the typing is reported as failed with how far it got.
        var sent = SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());

        if (sent != inputs.Count)
        {
            error = $"Unable to finish typing: Windows only dispatched {sent} of {inputs.Count} text input events.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    // One Unicode keyboard event: no virtual key (wVk = 0), the character goes in wScan, with the Unicode flag and the key-up flag when it is a release.
    private static INPUT CreateUnicodeKeyboardInput(char character, bool keyUp)
    {
        return new INPUT
        {
            type = InputKeyboard,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = 0,
                    wScan = character,
                    dwFlags = KeyeventfUnicode | (keyUp ? KeyeventfKeyup : 0)
                }
            }
        };
    }
}
