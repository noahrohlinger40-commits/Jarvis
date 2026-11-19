// VisualContext.cs: the interface for "look at the screen" abilities (take a screenshot, read text on screen, find
// buttons, list monitors, describe an image or document) plus a do-nothing version. The real Windows implementation
// lives in the app project; Jarvis.Core uses NullVisualContextService when no vision runtime is available, so the
// tools still answer politely instead of crashing.
namespace Jarvis.Core;

// Everything Jarvis can do with images and the screen. Each method takes the user's text input and returns a ToolResult.
public interface IVisualContextService
{
    Task<ToolResult> AnalyzeCurrentScreenAsync(string input, CancellationToken cancellationToken);

    Task<ToolResult> CaptureScreenAsync(string input, CancellationToken cancellationToken);

    Task<ToolResult> ExtractScreenTextAsync(string input, CancellationToken cancellationToken);

    Task<ToolResult> DetectScreenUiAsync(string input, CancellationToken cancellationToken);

    Task<ToolResult> ListDisplaysAsync(CancellationToken cancellationToken);

    Task<ToolResult> AnalyzeImageAsync(string input, CancellationToken cancellationToken);

    Task<ToolResult> AnalyzeDocumentAsync(string input, CancellationToken cancellationToken);
}

// Placeholder that answers every request with a failed ToolResult explaining that visual features are not available.
public sealed class NullVisualContextService : IVisualContextService
{
    public Task<ToolResult> AnalyzeCurrentScreenAsync(string input, CancellationToken cancellationToken)
    {
        return Task.FromResult(new ToolResult(
            "Visual understanding is unavailable because no vision-capable runtime is configured.",
            Succeeded: false,
            VerificationText: "No visual service is active.",
            SummaryText: "Visual understanding is unavailable."));
    }

    public Task<ToolResult> CaptureScreenAsync(string input, CancellationToken cancellationToken)
    {
        return Task.FromResult(new ToolResult(
            "Screen capture is unavailable because no Windows visual service is active.",
            Succeeded: false,
            VerificationText: "No visual service is active.",
            SummaryText: "Screen capture is unavailable."));
    }

    public Task<ToolResult> ExtractScreenTextAsync(string input, CancellationToken cancellationToken)
    {
        return Task.FromResult(new ToolResult(
            "Screen OCR is unavailable because no vision-capable runtime is configured.",
            Succeeded: false,
            VerificationText: "No visual service is active.",
            SummaryText: "Screen OCR is unavailable."));
    }

    public Task<ToolResult> DetectScreenUiAsync(string input, CancellationToken cancellationToken)
    {
        return Task.FromResult(new ToolResult(
            "Structured UI detection is unavailable because no vision-capable runtime is configured.",
            Succeeded: false,
            VerificationText: "No visual service is active.",
            SummaryText: "UI detection is unavailable."));
    }

    public Task<ToolResult> ListDisplaysAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult(new ToolResult(
            "Display inventory is unavailable because no Windows visual service is active.",
            Succeeded: false,
            VerificationText: "No visual service is active.",
            SummaryText: "Display inventory is unavailable."));
    }

    // Image analysis simply reuses the screen-analysis answer, which is also "unavailable" here.
    public Task<ToolResult> AnalyzeImageAsync(string input, CancellationToken cancellationToken)
    {
        return AnalyzeCurrentScreenAsync(input, cancellationToken);
    }

    public Task<ToolResult> AnalyzeDocumentAsync(string input, CancellationToken cancellationToken)
    {
        return Task.FromResult(new ToolResult(
            "Document understanding is unavailable because no analysis service is configured.",
            Succeeded: false,
            VerificationText: "No document analysis service is active.",
            SummaryText: "Document understanding is unavailable."));
    }
}
