// Two things. Checklist.LooksMultiPart is a cheap check for "this request asks for several things". The second part is a
// slice of StructuredAssistantPlanner (see AssistantPlanner.cs) that splits such a request into steps with one fast model
// call and runs each step as its own planner turn. Called from the planner's main entry point.
using System.Text.RegularExpressions;

namespace Jarvis.Core;

/// <summary>Spots a request that asks for more than one thing, so the planner can run it as a checklist.</summary>
public static class Checklist
{
    // Loose on purpose: a false hit costs one short model call that answers with a single step, and the request then runs as usual.
    // Matches words or punctuation that join separate tasks: a sentence break followed by more text ("Open Chrome. Then ...")
    // or and/then/also/plus/after that/as well as.
    private static readonly Regex Joiner = new(@"[.;!?]\s+\S|\b(?:and|then|also|plus|after that|as well as)\b", RegexOptions.IgnoreCase);

    // Needs at least 8 words and one joiner, so short requests like "play music and pause" are not split.
    public static bool LooksMultiPart(string input) =>
        !string.IsNullOrWhiteSpace(input) && input.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 8 && Joiner.IsMatch(input.Trim());
}

internal sealed partial class StructuredAssistantPlanner
{
    // Asks the fast model to split a multi-part request into short standalone steps (at most 6). Returns an empty array to mean "do not use
    // a checklist", so the request runs as one piece.
    private async Task<string[]> SplitChecklistAsync(string input, string sessionId, IProgress<AssistantActivityEvent> activity, CancellationToken cancellationToken)
    {
        var messages = new[]
        {
            new ChatMessage("system", [ChatMessageContentPart.FromText(
                "Split the user's request into the separate things to do, in order. Return only JSON: {\"steps\":[\"...\"]}. " +
                "Each step is one short imperative sentence that stands on its own (repeat names, never say 'it' or 'that'). " +
                "If the request is a single thing, return exactly one step. Never add a step the user did not ask for: " +
                "a remark about what might be needed later (\"I think some messages need to be sent\") is not a step, only an instruction is. " +
                "If the request is a question or a complaint, return exactly one step: the request as written. " +
                "Copy every link, file path and site, app or file name into the step exactly as written. " +
                "Never write a step that only says to acknowledge, explain, ensure or confirm something.")]),
            new ChatMessage("user", [ChatMessageContentPart.FromText(input)])
        };

        try
        {
            var response = await ExecuteModelCallAsync(
                new ChatCompletionRequest(messages, Temperature: 0, User: BuildModelSessionKey(sessionId, "split")),
                "checklist split",
                sessionId,
                activity,
                ModelRoute.Fast,
                cancellationToken);
            var steps = ParseJson<ChecklistContract>(response.Content)?.Steps?
                // Needs two or more steps to count; a single step means the request was not really multi-part.
                .Where(step => !string.IsNullOrWhiteSpace(step)).Select(step => step.Trim()).Take(6).ToArray() ?? [];
            return steps.Length >= 2 ? steps : [];
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return []; // a failed split just means the request runs whole
        }
    }

    // Runs the steps one after another through the normal planner, collects every tool result and citation, and builds
    // one reply: the numbered list, then one line per step. A failed step does not stop later ones. The list also goes to the screen, not aloud.
    private async Task<AssistantPlanningResult> RunChecklistAsync(
        string[] steps,
        AssistantPlanningRequest request,
        string sessionId,
        IntentDecision decision,
        Func<PlannerToolCall, CancellationToken, Task<PlannerToolResult>> executeToolAsync,
        IProgress<AssistantActivityEvent> activity,
        CancellationToken cancellationToken)
    {
        var header = $"{steps.Length} steps: {string.Join("; ", steps)}";
        activity.Report(new AssistantActivityEvent(AssistantActivityKind.Planning, header, sessionId));

        var lines = new List<string> { header };
        var toolResults = new List<PlannerToolResult>();
        var citations = new List<AssistantCitation>();
        var shouldExit = false;

        // Each step gets its own session id (parent id plus step number) so their saved state does not collide.
        for (var i = 0; i < steps.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stepSessionId = $"{sessionId}-{i + 1}";
            AssistantPlanningResult result;

            try
            {
                result = await ExecutePlannedSessionAsync(
                    request with { UserInput = steps[i] },
                    stepSessionId,
                    decision with { Intent = AgentIntentKind.Planner, Reason = "checklist step" },
                    executeToolAsync,
                    activity,
                    null,
                    false,
                    cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                result = new AssistantPlanningResult($"Failed: {TrimError(exception.Message)}", [], [], AgentIntentKind.Planner);
            }

            if (result.RequiresClarification)
            {
                // A question in the middle of a list cannot wait for an answer; the step is skipped and the question is shown.
                await _stateStore.RemoveSessionAsync(stepSessionId, cancellationToken);
            }

            lines.Add($"{i + 1}. {steps[i]}: {result.Response}");
            toolResults.AddRange(result.ToolResults);
            citations.AddRange(result.Citations);
            shouldExit |= result.ShouldExit;

            // A gate waits for the user's answer, and the saved approval holds one action only, so later steps must not run past it.
            // Resume after the answer is not built: the pending approval cannot carry the rest of the list.
            if (result.ToolResults.Any(LooksLikeApprovalGate) && i < steps.Length - 1)
            {
                lines.Add(BuildGateStopLine(steps, i));
                break;
            }
        }

        return new AssistantPlanningResult(
            string.Join(Environment.NewLine, lines),
            toolResults.ToArray(),
            citations.ToArray(),
            AgentIntentKind.Planner,
            shouldExit,
            SessionId: sessionId);
    }

    // Explains to the user which steps were not started because an earlier step is waiting for their approval.
    private static string BuildGateStopLine(string[] steps, int stoppedAt) =>
        $"Stopped at step {stoppedAt + 1} until you answer. Not started: " +
        $"{string.Join("; ", steps.Skip(stoppedAt + 1).Select((step, n) => $"{stoppedAt + n + 2}. {step.TrimEnd('.')}"))}. Ask for these again after you answer.";

    // Shape of the model's JSON answer for the split call.
    private sealed record ChecklistContract(string[]? Steps);
}
