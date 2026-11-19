// Intent classification and the simple routes of the planner. "Intent" means which route a request takes: direct tool,
// simple reply, research, full planner, background, visual or a clarifying question. First a cheap keyword guess
// (ClassifyHeuristically, with the word lists in AssistantPlanner.Formatting.cs); if that is unsure, a quick model call
// double-checks. Also holds the fallback and summary replies the main loop in AssistantPlanner.cs uses.
// Slice of the StructuredAssistantPlanner class; RequestSurface at the top is a separate public helper.
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Jarvis.Core;

/// <summary>Spots a request that names something to look at on this PC or online, which only the planner's tools can reach.</summary>
public static class RequestSurface
{
    // Matches a word that names something outside the chat to look at (email, Discord, a file, a page...) or a link ("://").
    // Such requests need the planner's tools, not a plain chat answer.
    private static readonly Regex Named = new(
        @"://|\b(?:e-?mails?|outlook|inbox|drafts?|messages?|dms?|discord|teams|canvas|notion|linkedin|sharepoint|calendar|files?|folders?|documents?|docs?|pdfs?|pages?|websites?|sites?|posts?|channels?|tabs?|sheets?|spreadsheets?|database|projects?|reports?|story|excel|figma|github|spotify)\b",
        RegexOptions.IgnoreCase);

    // True when the input mentions a named surface such as email, Discord or a file.
    public static bool IsNamed(string input) => !string.IsNullOrWhiteSpace(input) && Named.IsMatch(input);

    // A pasted link counts: nobody pastes one just to have it opened and left there.
    // Matches phrasing that asks what is ON a page rather than just opening it: "look through", "read", "summarize",
    // "what is", "catch me up", "any new", "unread", "find", or a link. Example: "check my Discord for anything new".
    private static readonly Regex AboutThePage = new(
        @"://|\b(?:look(?:ing)?\s+(?:through|at|over|for)|go(?:ing)?\s+through|read|check|see\s+(?:if|what|who)|make\s+sure|summar\w*|what(?:'?s|\s+is|\s+are)|catch\s+me\s+up|respond|reply|answer|any(?:thing)?\s+new|unread|missed|find|browse)\b",
        RegexOptions.IgnoreCase);

    /// <summary>True when the request wants to know what is on a page, not only to have it opened.</summary>
    public static bool AsksAboutThePage(string input) => !string.IsNullOrWhiteSpace(input) && AboutThePage.IsMatch(input);
}

internal sealed partial class StructuredAssistantPlanner
{
    // Picks the route. A resumed workflow always goes to the planner; otherwise the keyword guess is used, and a fast model call
    // may override it when the guess is shaky.
    private async Task<IntentDecision> ClassifyIntentAsync(
        string input,
        AssistantPlanningRequest request,
        PersistedAgentSession? pendingSession,
        PersistedAgentSession? resumedSession,
        string sessionId,
        IProgress<AssistantActivityEvent> activity,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IntentDecision decision;

        if (resumedSession is not null)
        {
            decision = new IntentDecision(
                ShouldRunInBackground(request.UserInput) ? AgentIntentKind.Background : AgentIntentKind.Planner,
                $"resuming persisted {resumedSession.Status} workflow",
                RunInBackground: ShouldRunInBackground(request.UserInput));
        }
        else
        {
            var heuristicDecision = ClassifyHeuristically(input, request, pendingSession);
            decision = heuristicDecision;

            if (ShouldUseModelIntentClassification(input, heuristicDecision, pendingSession))
            {
                var modelDecision = await TryClassifyIntentWithModelAsync(
                    input,
                    request,
                    sessionId,
                    activity,
                    heuristicDecision,
                    cancellationToken);

                if (modelDecision is not null)
                {
                    decision = modelDecision;
                }
            }
        }

        activity.Report(new AssistantActivityEvent(
            AssistantActivityKind.Classification,
            $"Intent classified as {decision.Intent.ToString().ToLowerInvariant()} ({decision.Reason}).",
            sessionId,
            Detail: input));

        return decision;
    }

    // The keyword-based guess, checked in priority order: pending question, ambiguous request, a tool name found in the
    // words (cheap direct route), small talk, research, an explicit action request, and finally a plain conversational reply.
    private IntentDecision ClassifyHeuristically(string input, AssistantPlanningRequest request, PersistedAgentSession? pendingSession)
    {
        if (pendingSession is not null)
        {
            return new IntentDecision(
                AgentIntentKind.Planner,
                "continuing pending clarification");
        }

        if (LooksAmbiguous(input))
        {
            return new IntentDecision(
                AgentIntentKind.Clarification,
                "request is ambiguous",
                AskFollowUp: true,
                FollowUpQuestion: BuildClarificationQuestion(input, request));
        }

        if (TryInferToolHint(input, out var toolHint))
        {
            var intent = string.Equals(toolHint, "screen", StringComparison.OrdinalIgnoreCase)
                || string.Equals(toolHint, "analyze image", StringComparison.OrdinalIgnoreCase)
                || string.Equals(toolHint, "analyze document", StringComparison.OrdinalIgnoreCase)
                ? AgentIntentKind.Visual
                : AgentIntentKind.DirectTool;

            return new IntentDecision(
                intent,
                "matched a cheap local tool route",
                toolHint,
                ExtractNaturalToolInput(input, toolHint));
        }

        if (LooksLikeSimpleReply(input))
        {
            return new IntentDecision(
                AgentIntentKind.SimpleReply,
                "small conversational request");
        }

        if (LooksLikeResearchRequest(input))
        {
            return new IntentDecision(
                AgentIntentKind.Research,
                "open-domain research request",
                RunInBackground: ShouldRunInBackground(input));
        }

        if (LooksLikePlannerRequest(input))
        {
            return new IntentDecision(
                ShouldRunInBackground(input) ? AgentIntentKind.Background : AgentIntentKind.Planner,
                "explicit action workflow",
                RunInBackground: ShouldRunInBackground(input));
        }

        return new IntentDecision(
            AgentIntentKind.SimpleReply,
            "assistant conversational reply");
    }

    // Decides whether the guess needs a second opinion from the model. Strong guesses (tool, research, planner) are trusted;
    // only the uncertain chat-like ones, or longer ones (over 24 characters), are checked. Skipped when the model is offline.
    private bool ShouldUseModelIntentClassification(
        string input,
        IntentDecision heuristicDecision,
        PersistedAgentSession? pendingSession)
    {
        if (pendingSession is not null || !_modelGateway.IsAvailable)
        {
            return false;
        }

        if (heuristicDecision.Intent is AgentIntentKind.DirectTool
            or AgentIntentKind.Visual
            or AgentIntentKind.Research
            or AgentIntentKind.Planner
            or AgentIntentKind.Background)
        {
            return false;
        }

        return heuristicDecision.Intent != AgentIntentKind.SimpleReply
            || !LooksLikeSimpleReply(input)
            || input.Trim().Length > 24
            || LooksLikeEmailContextQuestion(input)
            || LooksLikeTaskContextQuestion(input);
    }

    // Asks the fast model for a JSON classification. Any failure means "keep the keyword guess" (returns null).
    private async Task<IntentDecision?> TryClassifyIntentWithModelAsync(
        string input,
        AssistantPlanningRequest request,
        string sessionId,
        IProgress<AssistantActivityEvent> activity,
        IntentDecision heuristicDecision,
        CancellationToken cancellationToken)
    {
        try
        {
            activity.Report(new AssistantActivityEvent(
                AssistantActivityKind.Classification,
                "Checking a fast structured classifier before planning.",
                sessionId,
                IsTransient: true,
                Detail: input));

            var response = await ExecuteModelCallAsync(
                new ChatCompletionRequest(
                    BuildIntentClassificationMessages(request, input, heuristicDecision),
                    Temperature: 0,
                    User: BuildModelSessionKey(sessionId, "classify")),
                "intent classification",
                sessionId,
                activity,
                ModelRoute.Fast,
                cancellationToken);

            var classification = ParseJson<IntentClassificationContract>(response.Content);
            return classification is null
                ? null
                : BuildIntentDecisionFromModelClassification(input, request, heuristicDecision, classification);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            activity.Report(new AssistantActivityEvent(
                AssistantActivityKind.Warning,
                "Falling back to heuristic intent classification.",
                sessionId,
                Detail: TrimError(exception.Message)));

            return null;
        }
    }

    // Builds the prompt for the classifier: instructions, the keyword guess, the tools, and a little recent context
    // (trimmed to 900 characters each so the call stays fast).
    private ChatMessage[] BuildIntentClassificationMessages(
        AssistantPlanningRequest request,
        string input,
        IntentDecision heuristicDecision)
    {
        var toolHints = string.Join(", ", request.Tools.Select(tool => tool.ApiName));

        return
        [
            new ChatMessage("system", [ChatMessageContentPart.FromText(BuildPersonaScopedSystemPrompt(
                "Classify the user's request for routing. Return only JSON with keys intent, reason, toolHint, toolInput, askFollowUp, followUpQuestion, runInBackground. Allowed intent values: direct_tool, simple_reply, planner, research, visual. Use direct_tool only when one immediate tool call is enough. Use visual for screen, image, or document understanding requests. Use research only for open-domain web questions. Use planner for local computer actions or multi-step workflows. Use simple_reply for conversational answers that can be grounded in the supplied context without taking an action. Ask a follow-up only when a specific missing detail blocks the next step. Email and task snapshot questions are usually simple_reply unless the user explicitly wants to send, draft, create, edit, or update something. toolHint must be one of the allowed tool names or empty. toolInput must contain only the argument tail, not the command name."))]),
            new ChatMessage("user", [ChatMessageContentPart.FromText(string.Join(
                Environment.NewLine,
                $"User input: {input}",
                $"Heuristic guess: intent={MapIntentToClassifierValue(heuristicDecision.Intent)}; reason={heuristicDecision.Reason}; toolHint={heuristicDecision.ToolHint}; runInBackground={heuristicDecision.RunInBackground}",
                $"Allowed tool hints: {toolHints}",
                "Recent conversation:",
                TruncateForContext(BuildReplyRecentTurnBlock(request.RecentTurns), 900),
                "Recent tool evidence:",
                BuildRecentToolResultBlock(request.RecentToolResults, Math.Min(3, Math.Max(1, _options.PlannerRecentToolResultCount))),
                "Live computer context:",
                TruncateForContext(request.ComputerContext, 900)))])
        ];
    }

    // Turns the model's JSON answer into a decision, correcting inconsistent answers: a follow-up question always
    // means Clarification, and a direct-tool answer without a known tool name is discarded (returns null).
    private IntentDecision? BuildIntentDecisionFromModelClassification(
        string input,
        AssistantPlanningRequest request,
        IntentDecision heuristicDecision,
        IntentClassificationContract classification)
    {
        if (!TryMapClassifierIntent(classification.Intent, out var intent))
        {
            return null;
        }

        var askFollowUp = classification.AskFollowUp ?? false;
        var followUpQuestion = askFollowUp
            ? string.IsNullOrWhiteSpace(classification.FollowUpQuestion)
                ? BuildClarificationQuestion(input)
                : classification.FollowUpQuestion.Trim()
            : string.Empty;
        var toolHint = NormalizeClassifierToolHint(classification.ToolHint, request.Tools);
        var runInBackground = (classification.RunInBackground ?? false) || ShouldRunInBackground(input);

        if (intent == AgentIntentKind.Clarification)
        {
            askFollowUp = true;
            if (string.IsNullOrWhiteSpace(followUpQuestion))
            {
                followUpQuestion = BuildClarificationQuestion(input);
            }
        }

        if (askFollowUp)
        {
            intent = AgentIntentKind.Clarification;
            toolHint = string.Empty;
        }
        else if (intent is AgentIntentKind.DirectTool or AgentIntentKind.Visual)
        {
            if (string.IsNullOrWhiteSpace(toolHint) && TryInferToolHint(input, out var inferredToolHint))
            {
                toolHint = inferredToolHint;
            }

            if (string.IsNullOrWhiteSpace(toolHint))
            {
                return null;
            }

            intent = IsVisualTool(toolHint) ? AgentIntentKind.Visual : AgentIntentKind.DirectTool;
        }
        else
        {
            toolHint = string.Empty;
        }

        var toolInput = string.IsNullOrWhiteSpace(toolHint)
            ? string.Empty
            : string.IsNullOrWhiteSpace(classification.ToolInput)
                ? ExtractNaturalToolInput(input, toolHint)
                : classification.ToolInput.Trim();
        var reason = string.IsNullOrWhiteSpace(classification.Reason)
            ? heuristicDecision.Reason
            : $"model classifier: {classification.Reason.Trim()}";

        return new IntentDecision(
            intent,
            reason,
            toolHint,
            toolInput,
            askFollowUp,
            followUpQuestion,
            runInBackground);
    }

    // Direct route: runs the single tool the classifier chose and returns its output as the reply.
    private async Task<AssistantPlanningResult> ExecuteDirectToolIntentAsync(
        IntentDecision decision,
        Func<PlannerToolCall, CancellationToken, Task<PlannerToolResult>> executeToolAsync,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var result = await executeToolAsync(new PlannerToolCall(decision.ToolHint, decision.ToolInput), cancellationToken);

        return new AssistantPlanningResult(
            result.Output,
            [result],
            [],
            decision.Intent,
            result.ShouldExit,
            SessionId: sessionId);
    }

    // Simple reply route: one model call with recent context and no tools. If the model is offline or fails,
    // a canned offline reply is used instead.
    private async Task<AssistantPlanningResult> ExecuteSimpleReplyAsync(
        AssistantPlanningRequest request,
        string sessionId,
        PersistedAgentSession? resumedSession,
        CancellationToken cancellationToken)
    {
        var offlineFallback = BuildOfflineSimpleReply(request.UserInput);

        if (!_modelGateway.IsAvailable)
        {
            return new AssistantPlanningResult(
                offlineFallback,
                [],
                [],
                AgentIntentKind.SimpleReply,
                SessionId: sessionId);
        }

        try
        {
            var response = await ExecuteModelCallAsync(
                new ChatCompletionRequest(BuildContextualReplyMessages(request, resumedSession), Temperature: 0.25, User: BuildModelSessionKey(sessionId, "simple")),
                "simple reply",
                sessionId,
                null,
                ModelRoute.Fast,
                cancellationToken);

            return new AssistantPlanningResult(
                string.IsNullOrWhiteSpace(response.Content) ? offlineFallback : response.Content.Trim(),
                [],
                [],
                AgentIntentKind.SimpleReply,
                SessionId: sessionId);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new AssistantPlanningResult(
                offlineFallback,
                [],
                [],
                AgentIntentKind.SimpleReply,
                SessionId: sessionId);
        }
    }

    // Research route: web search for citations, then (if the model is up) a model call that answers from those sources only.
    // Without a model, or without results, it returns a plain list of the sources found.
    private async Task<AssistantPlanningResult> ExecuteResearchIntentAsync(
        string input,
        string sessionId,
        CancellationToken cancellationToken)
    {
        if (!_options.PlannerWebResearchEnabled)
        {
            return new AssistantPlanningResult(
                "Web research is disabled in settings.",
                [],
                [],
                AgentIntentKind.Research,
                SessionId: sessionId);
        }

        var research = await _webResearchService.ResearchAsync(
            input,
            _options.PlannerWebResearchMaxResults,
            cancellationToken);

        if (!_modelGateway.IsAvailable || research.Citations.Length == 0)
        {
            return new AssistantPlanningResult(
                BuildOfflineResearchSummary(research),
                [],
                research.Citations,
                AgentIntentKind.Research,
                SessionId: sessionId);
        }

        var sourceText = string.Join(
            Environment.NewLine,
            research.Citations.Select((citation, index) =>
                $"[{index + 1}] {citation.Title}{Environment.NewLine}URL: {citation.Url}{Environment.NewLine}Snippet: {citation.Snippet}"));

        var messages = new[]
        {
            new ChatMessage("system", [ChatMessageContentPart.FromText(
                BuildPersonaScopedSystemPrompt("Answer the question using only the supplied web citations. If the evidence is thin, say so plainly. End with a short Sources section that references the numbered citations."))]),
            new ChatMessage("user", [ChatMessageContentPart.FromText($"Question: {input}{Environment.NewLine}{Environment.NewLine}Sources:{Environment.NewLine}{sourceText}")])
        };

        try
        {
            var response = await ExecuteModelCallAsync(
                new ChatCompletionRequest(messages, Temperature: 0.2, User: BuildModelSessionKey(sessionId, "research")),
                "research summary",
                sessionId,
                null,
                ModelRoute.Reasoning,
                cancellationToken);

            var content = string.IsNullOrWhiteSpace(response.Content)
                ? BuildOfflineResearchSummary(research)
                : response.Content.Trim();

            return new AssistantPlanningResult(
                content,
                [],
                research.Citations,
                AgentIntentKind.Research,
                SessionId: sessionId);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new AssistantPlanningResult(
                BuildOfflineResearchSummary(research) + Environment.NewLine + $"Model fallback: {TrimError(exception.Message)}",
                [],
                research.Citations,
                AgentIntentKind.Research,
                SessionId: sessionId);
        }
    }

    // Used when the planner loop itself fails: tries a plain conversational answer if no tool has run yet, otherwise summarises
    // what the tools did, and ends the saved session.
    private async Task<AssistantPlanningResult> BuildFallbackPlannedResultAsync(
        AssistantPlanningRequest request,
        string sessionId,
        IReadOnlyList<PlannerToolResult> toolResults,
        IReadOnlyList<AssistantCitation> citations,
        string errorMessage,
        CancellationToken cancellationToken,
        StuckNotes? stuck = null)
    {
        var contextualFallback = toolResults.Count == 0
            ? await TryBuildContextualFallbackReplyAsync(request, sessionId, cancellationToken)
            : string.Empty;
        var response = toolResults.Count == 0
            ? (string.IsNullOrWhiteSpace(contextualFallback)
                ? string.Join(
                    Environment.NewLine,
                    $"The planner could not complete that request: {TrimError(errorMessage)}",
                    "You can try a more direct command, or rephrase the request once connectivity is available.")
                : contextualFallback)
            : await BuildPlannedSummaryAsync(request.UserInput, toolResults, citations.ToArray(), sessionId, cancellationToken, stuck);
        var intent = string.IsNullOrWhiteSpace(contextualFallback) ? AgentIntentKind.Planner : AgentIntentKind.SimpleReply;

        await _stateStore.RemoveSessionAsync(sessionId, cancellationToken);

        return new AssistantPlanningResult(
            response,
            toolResults.ToArray(),
            citations.ToArray(),
            intent,
            ShouldExit: toolResults.LastOrDefault()?.ShouldExit ?? false,
            SessionId: sessionId);
    }

    // Plain model reply used as a last resort when the planner model call failed.
    private async Task<string> TryBuildContextualFallbackReplyAsync(
        AssistantPlanningRequest request,
        string sessionId,
        CancellationToken cancellationToken)
    {
        if (!_modelGateway.IsAvailable)
        {
            return string.Empty;
        }

        try
        {
            var response = await ExecuteModelCallAsync(
                new ChatCompletionRequest(BuildContextualReplyMessages(request), Temperature: 0.2, User: BuildModelSessionKey(sessionId, "fallback")),
                "fallback reply",
                sessionId,
                null,
                ModelRoute.Fast,
                cancellationToken);

            return response.Content?.Trim() ?? string.Empty;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return string.Empty;
        }
    }

    // Final reply after tool work: a short model-written summary of what the tools did, or a plain-text summary if the model
    // is unavailable. A waiting approval prompt is returned as is.
    private async Task<string> BuildPlannedSummaryAsync(
        string userInput,
        IReadOnlyList<PlannerToolResult> toolResults,
        AssistantCitation[] citations,
        string sessionId,
        CancellationToken cancellationToken,
        StuckNotes? stuck = null)
    {
        if (toolResults.Count == 0)
        {
            return AppendCitations("I could not complete that request with the available tools.", citations);
        }

        // A gate's own text says what is waiting and how to answer; a model summary of a "finished workflow" could lose that.
        if (string.Equals(ResolveVerificationStatus(toolResults[^1]), "awaiting_approval", StringComparison.OrdinalIgnoreCase))
        {
            return toolResults[^1].Output;
        }

        if (!_modelGateway.IsAvailable)
        {
            return AppendCitations(BuildOfflinePlanSummary(toolResults), citations);
        }

        var toolText = string.Join(
            Environment.NewLine,
            toolResults.TakeLast(Math.Max(1, _options.PlannerRecentToolResultCount)).Select(result =>
                $"- {result.ApiName}({result.Input}): success={!result.IsError}; output={TrimToolOutput(result.ApiName, result.Output).ReplaceLineEndings(" ")}"));

        var messages = new[]
        {
            new ChatMessage("system", [ChatMessageContentPart.FromText(
                BuildPersonaScopedSystemPrompt("Summarize the finished workflow for the user. Do not dump raw tool output. Mention whether the action actually succeeded based on the tool results. If every tool only read something (screen, notes, status), say what was read and that nothing was done; never describe an action no tool performed."))]),
            new ChatMessage("user", [ChatMessageContentPart.FromText(
                $"User request: {userInput}{Environment.NewLine}Tool results:{Environment.NewLine}{toolText}")])
        };

        try
        {
            var response = await ExecuteModelCallAsync(
                new ChatCompletionRequest(messages, Temperature: 0.2, User: BuildModelSessionKey(sessionId, "summary")),
                "plan summary",
                sessionId,
                null,
                ModelRoute.Fast,
                cancellationToken);

            var content = string.IsNullOrWhiteSpace(response.Content)
                ? BuildOfflinePlanSummary(toolResults)
                : response.Content.Trim();

            // This summary is its own model call, so it can narrate an action just like
            // the planner reply; when nothing acted this turn (reads only), a narrated action is replaced by the honest line.
            if (!ActionClaimGuard.Acted(toolResults) && ActionClaimGuard.IsUnverifiedClaim(content))
            {
                content = ActionClaimGuard.HonestReplyAfter(toolResults);
                // Stuck signal (a) again, for the summary's reply.
                if (content == ActionClaimGuard.HonestReply) stuck?.Note("the honesty gate replaced a summary that claimed an action no tool performed");
            }

            return AppendCitations(content, citations);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            stuck?.Note($"the summary model call failed: {TrimError(exception.Message)}");
            return AppendCitations(
                BuildOfflinePlanSummary(toolResults) + Environment.NewLine + $"Model fallback: {TrimError(exception.Message)}",
                citations);
        }
    }

    // The route chosen for a request, with the reason (shown in the UI), the tool to run for direct routes, and any follow-up question.
    private sealed record IntentDecision(
        AgentIntentKind Intent,
        string Reason,
        string ToolHint = "",
        string ToolInput = "",
        bool AskFollowUp = false,
        string FollowUpQuestion = "",
        bool RunInBackground = false);

    // The JSON shape the classifier model is asked to return.
    private sealed record IntentClassificationContract(
        string? Intent,
        string? Reason,
        string? ToolHint,
        string? ToolInput,
        bool? AskFollowUp,
        string? FollowUpQuestion,
        bool? RunInBackground);

    // Status of a tool result: the recorded one, or "failed"/"observed" as a default.
    private static string ResolveVerificationStatus(PlannerToolResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.VerificationStatus))
        {
            return result.VerificationStatus.Trim();
        }

        return result.IsError ? "failed" : "observed";
    }

    // Adds the assistant's name and persona text in front of a task-specific instruction.
    private string BuildPersonaScopedSystemPrompt(string taskInstruction)
    {
        return string.Join(
            Environment.NewLine,
            $"You are {_options.AssistantName}, a Windows desktop assistant.",
            $"Configured persona: {AssistantPersonaConfiguration.Resolve(_options)}",
            AssistantPersonaConfiguration.BuildStabilityInstruction(),
            "Presence guidance:",
            AssistantPersonaConfiguration.BuildPresenceGuidance(_options),
            taskInstruction);
    }

    // Lower-cases and replaces underscores/hyphens with spaces so "direct_tool" and "Direct Tool" compare equal.
    private static string NormalizeClassifierLabel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return string.Join(
            ' ',
            value
                .Replace('_', ' ')
                .Replace('-', ' ')
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Trim()
            .ToLowerInvariant();
    }

    // Maps a route back to the label the classifier prompt uses: the enum name in snake case ("direct_tool"); Background is shown to the model as "planner".
    private static string MapIntentToClassifierValue(AgentIntentKind intent) =>
        intent == AgentIntentKind.Background
            ? "planner"
            : Regex.Replace(intent.ToString(), "(?<=[a-z])(?=[A-Z])", "_").ToLowerInvariant();

    // Maps the label returned by the model to a route by enum name (spaces dropped, case ignored); false for anything unrecognised.
    // Letters only, because Enum.TryParse would also accept numbers and comma lists. Background is a route Jarvis picks itself, never a label the model may answer with.
    private static bool TryMapClassifierIntent(string? value, out AgentIntentKind intent)
    {
        var label = NormalizeClassifierLabel(value).Replace(" ", string.Empty);
        intent = AgentIntentKind.SimpleReply;
        return label.Length > 0
            && label.All(char.IsLetter)
            && Enum.TryParse(label, ignoreCase: true, out intent)
            && intent != AgentIntentKind.Background;
    }

    // Matches the model's tool name against the real tool list (by API name or command name); unknown names give an empty string.
    private static string NormalizeClassifierToolHint(string? toolHint, IReadOnlyList<PlannerToolDefinition> tools)
    {
        var normalized = NormalizeClassifierLabel(toolHint);

        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        foreach (var tool in tools)
        {
            if (NormalizeClassifierLabel(tool.ApiName) == normalized
                || NormalizeClassifierLabel(tool.CommandName) == normalized)
            {
                return tool.ApiName;
            }
        }

        return string.Empty;
    }

    // Tools that look at the screen or a file rather than act on the computer.
    private static bool IsVisualTool(string toolHint) =>
        string.Equals(toolHint, "screen", StringComparison.OrdinalIgnoreCase)
        || string.Equals(toolHint, "analyze image", StringComparison.OrdinalIgnoreCase)
        || string.Equals(toolHint, "analyze document", StringComparison.OrdinalIgnoreCase);
}
