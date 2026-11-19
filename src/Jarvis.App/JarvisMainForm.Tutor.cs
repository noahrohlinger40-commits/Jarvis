// JarvisMainForm slice: the tutor hook. The tutor (TutorService in Jarvis.Core) runs lessons on a background task and calls back here.
namespace Jarvis.App;

internal sealed partial class JarvisMainForm
{
    // A lesson step (or its final message) arrives from the tutor's background task: show it in the console
    // and say it, the way a due reminder is announced.
    private void OnTutorSaid(string message)
    {
        if (!CanUseUi())
        {
            return;
        }

        BeginInvoke((MethodInvoker)(() =>
        {
            AppendMessage(_currentOptions.AssistantName.ToUpperInvariant(), message, Color.FromArgb(106, 220, 197));
            _ = TrySpeakAsync(message, _cancellationTokenSource.Token);
        }));
    }
}
