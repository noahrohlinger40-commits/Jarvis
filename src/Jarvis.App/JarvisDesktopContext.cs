// Holds the long-lived pieces of Jarvis built at startup by AppBootstrapper and handed to JarvisMainForm.
// The setters exist because settings changes in the UI rebuild the assistant, speaker and voice runtime.
using Jarvis.Core;

namespace Jarvis.App;

// Bundle of everything the main window needs: settings, desktop control, the assistant brain, speech output and voice input.
internal sealed class JarvisDesktopContext
{
    public JarvisDesktopContext(
        string workspaceRoot,
        JarvisOptions options,
        IDesktopAutomationService desktopAutomation,
        JarvisAssistant assistant,
        ISpeaker speaker,
        IVoiceRuntime voiceRuntime)
    {
        WorkspaceRoot = workspaceRoot;
        Options = options;
        DesktopAutomation = desktopAutomation;
        Assistant = assistant;
        Speaker = speaker;
        VoiceRuntime = voiceRuntime;
    }

    // Folder that contains jarvis.settings.json.
    public string WorkspaceRoot { get; }

    // Current settings; replaced when the user saves new ones.
    public JarvisOptions Options { get; set; }

    // Opens apps, files and sites, clicks and types for the user.
    public IDesktopAutomationService DesktopAutomation { get; }

    // The brain that plans and runs each command; recreated when settings change.
    public JarvisAssistant Assistant { get; set; }

    // Text-to-speech output.
    public ISpeaker Speaker { get; set; }

    // Microphone listening and speech recognition.
    public IVoiceRuntime VoiceRuntime { get; set; }
}
