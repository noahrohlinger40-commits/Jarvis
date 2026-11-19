// One slice of the StructuredAssistantPlanner class (see AssistantPlanner.cs): helpers that turn the model's tool call
// into a PlannerToolCall, and that describe how a tool run went (verified, failed, waiting for approval) for the
// plan-step list shown in the UI. Static helpers only; the real tool runs elsewhere.
using System.Text.Json;

namespace Jarvis.Core;

internal sealed partial class StructuredAssistantPlanner
{
    // Reads the arguments the model sent for a tool call. The expected shape is {"input": "..."}, but a bare JSON string
    // or even invalid JSON is accepted and used as the input text, because small local models are sloppy about format.
    private static PlannerToolCall ParsePlannerToolCall(ChatToolCall toolCall)
    {
        if (string.IsNullOrWhiteSpace(toolCall.ArgumentsJson))
        {
            return new PlannerToolCall(toolCall.Name, string.Empty);
        }

        try
        {
            using var document = JsonDocument.Parse(toolCall.ArgumentsJson);
            var root = document.RootElement;

            if (root.ValueKind == JsonValueKind.Object)
            {
                return new PlannerToolCall(toolCall.Name, ReadToolArgument(root, "input"));
            }

            if (root.ValueKind == JsonValueKind.String)
            {
                return new PlannerToolCall(toolCall.Name, root.GetString()?.Trim() ?? string.Empty);
            }
        }
        // Malformed JSON is not an error here; fall through to using the raw text as the input.
        catch (JsonException)
        {
        }

        return new PlannerToolCall(toolCall.Name, toolCall.ArgumentsJson.Trim());
    }

    // Reads one property as trimmed text; a missing or null property gives an empty string, and numbers/objects are shown as their JSON text.
    private static string ReadToolArgument(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property))
        {
            return string.Empty;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString()?.Trim() ?? string.Empty,
            JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
            _ => property.ToString().Trim()
        };
    }

    // Text shown next to a plan step in the UI.
    private static string BuildToolStepDetail(PlannerToolCall toolCall)
    {
        return string.IsNullOrWhiteSpace(toolCall.Input)
            ? "tool selected without additional planner metadata."
            : $"input: {TrimForFeed(toolCall.Input)}";
    }

    // Classifies a tool result for the UI: waiting for approval, failed, "observed" (ran, but the tool gave only a generic
    // success message) or "verified" (the tool gave a specific check of its own).
    private static ToolVerificationReport BuildToolVerificationReport(PlannerToolResult result)
    {
        if (LooksLikeApprovalGate(result))
        {
            return new ToolVerificationReport(
                "awaiting_approval",
                "needs_review",
                "The tool reached an approval gate.",
                BuildVerificationDetail(result, "The tool reached an approval gate."));
        }

        if (result.IsError)
        {
            return new ToolVerificationReport(
                "failed",
                "failed",
                "The tool reported a failure.",
                BuildVerificationDetail(result, "The tool reported a failure."));
        }

        return new ToolVerificationReport(
            IsGenericVerificationText(result.Verification.Trim()) ? "observed" : "verified",
            "completed",
            result.Summary,
            BuildVerificationDetail(result, result.Summary));
    }

    // Only the safety gate writes this summary on a result that did not succeed. Checking the verification text for "approval" would also match a tool's
    // input (a search for "approval form" would look like a gate).
    private static bool LooksLikeApprovalGate(PlannerToolResult result) =>
        result.IsError && result.Summary.StartsWith("Approval required", StringComparison.OrdinalIgnoreCase);

    // Some tools just say "returned a success signal"; that proves little, so it is treated as no real verification.
    private static bool IsGenericVerificationText(string verificationText)
    {
        if (string.IsNullOrWhiteSpace(verificationText))
        {
            return true;
        }

        var genericPhrases = new[]
        {
            "returned a success signal",
            "reported a success signal",
            "success-shaped response",
            "reported a failure signal"
        };

        return genericPhrases.Any(phrase => verificationText.Contains(phrase, StringComparison.OrdinalIgnoreCase));
    }

    // Builds the one-line detail text from the best available source: verification, then summary, then raw output.
    private static string BuildVerificationDetail(PlannerToolResult result, string headline)
    {
        var parts = new List<string> { headline };

        if (!string.IsNullOrWhiteSpace(result.Verification))
        {
            parts.Add($"Tool verification: {result.Verification.Trim()}");
        }
        else if (!string.IsNullOrWhiteSpace(result.Summary))
        {
            parts.Add($"Observed summary: {result.Summary.Trim()}");
        }
        else if (!string.IsNullOrWhiteSpace(result.Output))
        {
            parts.Add($"Observed output: {TrimForFeed(result.Output)}");
        }

        return string.Join(" ", parts);
    }

    // Status text for one tool result, in the shape the plan-step list needs.
    private sealed record ToolVerificationReport(
        string Status,
        string PlanStepStatus,
        string Summary,
        string Detail);
}
