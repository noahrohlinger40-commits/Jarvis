// The planner: the part of Jarvis that decides how to handle a typed or spoken request. JarvisAssistant.cs builds an
// AssistantPlanningRequest and calls IAssistantPlanner.ExecuteTurnAsync. The planner classifies the request
// (AssistantPlanner.Intents.cs), then either answers directly, runs one tool, does web research, runs a checklist
// (AssistantPlanner.Checklist.cs), queues a background task, or loops "ask the model, run the tools it picks, feed results back"
// (ExecutePlannedSessionAsync below). Model calls go through AssistantPlanner.ModelCalls.cs, tool-call parsing is in
// AssistantPlanner.ToolCalling.cs, and prompt and reply text helpers are in AssistantPlanner.Formatting.cs.
// This file holds the request/result records, the factory, and the main flow of the StructuredAssistantPlanner class.
using System.Collections.Concurrent;

namespace Jarvis.Core;

// A tool the model may call: ApiName is the identifier the model uses, CommandName is Jarvis's own name for it.
public sealed record PlannerToolDefinition(
    string ApiName,
    string CommandName,
    string Description,
    string InputDescription = "");

// A tool call the model asked for, with its text argument.
public sealed record PlannerToolCall(
    string ApiName,
    string Input);

// What a tool returned: its output, whether it failed, whether it should end the app, and a check of the result
// (Verification fields) that tells whether the effect was confirmed.
public sealed record PlannerToolResult(
    string ApiName,
    string Command,
    string Input,
    string Output,
    bool IsError,
    bool ShouldExit = false,
    string Verification = "",
    string Summary = "",
    string VerificationStatus = "",
    string VerificationSummary = "");

// Everything the planner needs for one turn: the user's words, the available tools, recent memory and conversation,
// and extra context text (computer state, Brain notes).
public sealed record AssistantPlanningRequest(
    string AssistantName,
    string UserInput,
    string WorkspaceRoot,
    string ComputerContext,
    bool ShellExecutionEnabled,
    IReadOnlyList<PlannerToolDefinition> Tools,
    IReadOnlyList<MemoryNote> RecentMemories,
    IReadOnlyList<AssistantTurn> RecentTurns,
    IReadOnlyList<AssistantToolExecution> RecentToolResults,
    ConversationStateSnapshot ConversationState,
    MemoryProfileSnapshot MemoryProfile,
    string BrainContext = "");

// What the planner hands back: the reply text, tools that ran, sources, which route was used, and flags for
// "ask the user a question", "exit the app" and "queued as a background task".
public sealed record AssistantPlanningResult(
    string Response,
    PlannerToolResult[] ToolResults,
    AssistantCitation[] Citations,
    AgentIntentKind Intent,
    bool ShouldExit = false,
    bool RequiresClarification = false,
    string ClarificationQuestion = "",
    bool StartedBackgroundTask = false,
    string SessionId = "");

// What JarvisAssistant needs from a planner, so a disabled planner can stand in for the real one.
public interface IAssistantPlanner
{
    bool IsAvailable { get; }

    string Status { get; }

    Task InitializeAsync(
        Func<PlannerToolCall, CancellationToken, Task<PlannerToolResult>> executeToolAsync,
        IProgress<AssistantActivityEvent> activity,
        CancellationToken cancellationToken);

    Task<AssistantPlanningResult> ExecuteTurnAsync(
        AssistantPlanningRequest request,
        Func<PlannerToolCall, CancellationToken, Task<PlannerToolResult>> executeToolAsync,
        IProgress<AssistantActivityEvent> activity,
        CancellationToken cancellationToken);
}

// Chooses the real planner or a do-nothing one depending on the PlannerEnabled setting.
public static class AssistantPlannerFactory
{
    public static IAssistantPlanner Create(
        JarvisOptions options,
        IModelGateway modelGateway,
        IAgentStateStore stateStore,
        IWebResearchService webResearchService,
        IModelGateway? escalationGateway = null)
    {
        if (!options.PlannerEnabled)
        {
            return new DisabledAssistantPlanner("disabled in settings");
        }

        return new StructuredAssistantPlanner(options, modelGateway, stateStore, webResearchService, escalationGateway);
    }
}

// Stand-in used when the planner is switched off in settings; it reports itself unavailable and refuses to run.
internal sealed class DisabledAssistantPlanner : IAssistantPlanner
{
    public DisabledAssistantPlanner(string reason)
    {
        Status = $"disabled ({reason})";
    }

    public bool IsAvailable => false;

    public string Status { get; }

    public Task InitializeAsync(
        Func<PlannerToolCall, CancellationToken, Task<PlannerToolResult>> executeToolAsync,
        IProgress<AssistantActivityEvent> activity,
        CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task<AssistantPlanningResult> ExecuteTurnAsync(
        AssistantPlanningRequest request,
        Func<PlannerToolCall, CancellationToken, Task<PlannerToolResult>> executeToolAsync,
        IProgress<AssistantActivityEvent> activity,
        CancellationToken cancellationToken)
    {
        throw new InvalidOperationException("Planner is not available.");
    }
}

// The real planner. This file is the main flow; other files hold more of this class (see the header).
internal sealed partial class StructuredAssistantPlanner : IAssistantPlanner
{
    private readonly JarvisOptions _options;
    private readonly IModelGateway _modelGateway;
    private readonly IAgentStateStore _stateStore;
    private readonly IWebResearchService _webResearchService;
    // Only set on the escalation planner: every model call it makes uses this route (Deep: its timeout and token cap).
    private readonly ModelRoute? _routeOverride;
    // The planner on the bigger model, or null when escalation is off (see AssistantPlanner.Escalation.cs).
    private readonly StructuredAssistantPlanner? _escalation;
    // One background budget and task map per process: the app rebuilds the assistant on many settings changes
    // while background jobs keep running. ponytail: capacity is fixed at first use, so changing
    // PlannerMaxConcurrentBackgroundTasks takes effect on restart.
    private static ModelWorkScheduler? _sharedBackgroundScheduler;
    private static readonly ConcurrentDictionary<string, Task> _backgroundTasks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ModelWorkScheduler _backgroundScheduler;

    // Reuses the shared background scheduler, creating it on first use (1 to 4 jobs at once, up to 16 waiting).
    public StructuredAssistantPlanner(
        JarvisOptions options,
        IModelGateway modelGateway,
        IAgentStateStore stateStore,
        IWebResearchService webResearchService,
        IModelGateway? escalationGateway = null,
        ModelRoute? routeOverride = null)
    {
        _options = options;
        _modelGateway = modelGateway;
        _stateStore = stateStore;
        _webResearchService = webResearchService;
        _routeOverride = routeOverride;
        // The second planner is the same class on the escalation gateway; it has no escalation of its own, so a request escalates at most once.
        _escalation = escalationGateway is null
            ? null
            : new StructuredAssistantPlanner(options, escalationGateway, stateStore, webResearchService, null, ModelRoute.Deep);
        _backgroundScheduler = LazyInitializer.EnsureInitialized(ref _sharedBackgroundScheduler,
            () => new ModelWorkScheduler(Math.Clamp(options.PlannerMaxConcurrentBackgroundTasks, 1, 4), maxWaiting: 16));
    }

    // Text shown in the UI about what the planner can currently do; mentions degraded mode when the model is not reachable.
    public bool IsAvailable => true;

    public string Status =>
        _modelGateway.IsAvailable
            ? $"agent runtime enabled ({_options.PlannerProvider}, routes fast/reasoning/vision, retries {_options.PlannerRetryCount}); {_modelGateway.Status}"
            : "agent runtime enabled in degraded mode (heuristics, direct tools, and optional web research)";

    // At startup, tells the user if there are unfinished workflows they can resume by saying "resume".
    public async Task InitializeAsync(
        Func<PlannerToolCall, CancellationToken, Task<PlannerToolResult>> executeToolAsync,
        IProgress<AssistantActivityEvent> activity,
        CancellationToken cancellationToken)
    {
        var queuedBackgroundSessions = await _stateStore.GetBackgroundSessionsAsync(cancellationToken);
        var latestResumableSession = await _stateStore.GetLatestResumableSessionAsync(cancellationToken);

        if (queuedBackgroundSessions.Count > 0)
        {
            activity.Report(new AssistantActivityEvent(
                AssistantActivityKind.Background,
                $"{queuedBackgroundSessions.Count} background workflow{(queuedBackgroundSessions.Count == 1 ? string.Empty : "s")} can be resumed. Say `resume` to continue the latest interrupted task.",
                Detail: string.Join(Environment.NewLine, queuedBackgroundSessions.Select(session => $"{session.Status}: {session.OriginalRequest}"))));
        }

        if (latestResumableSession is not null
            && queuedBackgroundSessions.All(session => !string.Equals(session.SessionId, latestResumableSession.SessionId, StringComparison.OrdinalIgnoreCase)))
        {
            activity.Report(new AssistantActivityEvent(
                AssistantActivityKind.Planning,
                "A persisted workflow can be resumed. Say `resume` to continue it.",
                latestResumableSession.SessionId,
                Detail: $"{latestResumableSession.Status}: {latestResumableSession.OriginalRequest}"));
        }
    }

    // The main entry point for one turn. Order of events: (1) work out whether this input continues a pending question or a
    // resumed workflow, (2) resolve short references like "open it", (3) classify the intent, (4) take the route for that
    // intent, falling back to the full planner when a cheaper route fails.
    public async Task<AssistantPlanningResult> ExecuteTurnAsync(
        AssistantPlanningRequest request,
        Func<PlannerToolCall, CancellationToken, Task<PlannerToolResult>> executeToolAsync,
        IProgress<AssistantActivityEvent> activity,
        CancellationToken cancellationToken)
    {
        // A saved session waiting for the user's answer to a clarification question, if any.
        var pendingSession = await _stateStore.GetPendingClarificationAsync(cancellationToken);
        PersistedAgentSession? resumedSession = null;
        var trimmedInput = request.UserInput.Trim();

        // "resume" only counts when no clarification is pending.
        if (pendingSession is null && LooksLikeResumeRequest(trimmedInput))
        {
            resumedSession = await _stateStore.GetLatestResumableSessionAsync(cancellationToken);

            if (resumedSession is null)
            {
                return new AssistantPlanningResult(
                    "There is no interrupted workflow to resume.",
                    [],
                    [],
                    AgentIntentKind.SimpleReply);
            }

            if (_backgroundTasks.ContainsKey(resumedSession.SessionId))
            {
                return new AssistantPlanningResult(
                    "That workflow is already running in the background.",
                    [],
                    resumedSession.Citations,
                    AgentIntentKind.Background,
                    SessionId: resumedSession.SessionId);
            }
        }

        // New turns get a fresh id; pending or resumed ones keep the id of the saved session so its state carries on.
        var sessionId = pendingSession?.SessionId ?? resumedSession?.SessionId ?? Guid.NewGuid().ToString("n");
        // The text the planner works on: plain input, resumed request plus any new instruction, or the original request plus the user's answer.
        var effectiveInput = pendingSession is null
            ? resumedSession is null
                ? trimmedInput
                : BuildResumedInput(trimmedInput, resumedSession)
            : $"{pendingSession.OriginalRequest}{Environment.NewLine}Clarification from the user: {trimmedInput}";
        var effectiveRequest = request with { UserInput = effectiveInput };

        if (pendingSession is not null)
        {
            activity.Report(new AssistantActivityEvent(
                AssistantActivityKind.Clarification,
                "Continuing the pending workflow with the user's follow-up.",
                sessionId,
                Detail: trimmedInput));

            if (TryResolveClarificationReference(pendingSession.OriginalRequest, trimmedInput, request, out var clarifiedInput, out var clarificationDetail))
            {
                effectiveInput = clarifiedInput;
                effectiveRequest = request with { UserInput = effectiveInput };

                activity.Report(new AssistantActivityEvent(
                    AssistantActivityKind.Classification,
                    "Resolved the user's clarification from recent context.",
                    sessionId,
                    IsTransient: true,
                    Detail: clarificationDetail));
            }
        }
        else if (resumedSession is not null)
        {
            activity.Report(new AssistantActivityEvent(
                AssistantActivityKind.Background,
                "Resuming the most recent interrupted workflow.",
                sessionId,
                Detail: resumedSession.OriginalRequest));
        }
        else if (TryResolveContextualReference(effectiveInput, effectiveRequest, out var resolvedInput, out var resolutionDetail))
        {
            effectiveInput = resolvedInput;
            effectiveRequest = request with { UserInput = effectiveInput };

            activity.Report(new AssistantActivityEvent(
                AssistantActivityKind.Classification,
                "Resolved a short follow-up reference from recent context.",
                sessionId,
                IsTransient: true,
                Detail: resolutionDetail));
        }

        // Decides which route the request takes (see AssistantPlanner.Intents.cs).
        var intentDecision = await ClassifyIntentAsync(effectiveInput, effectiveRequest, pendingSession, resumedSession, sessionId, activity, cancellationToken);

        // The classifier needs more information: save the question and return it as the reply.
        if (intentDecision.AskFollowUp)
        {
            await SaveClarificationSessionAsync(sessionId, effectiveInput, intentDecision, pendingSession ?? resumedSession, cancellationToken);

            return new AssistantPlanningResult(
                intentDecision.FollowUpQuestion,
                [],
                [],
                AgentIntentKind.Clarification,
                RequiresClarification: true,
                ClarificationQuestion: intentDecision.FollowUpQuestion,
                SessionId: sessionId);
        }

        if (pendingSession is not null)
        {
            await _stateStore.RemoveSessionAsync(pendingSession.SessionId, cancellationToken);
        }

        // Background tasks are only started when the setting allows it.
        if (intentDecision.RunInBackground && _options.PlannerBackgroundTasksEnabled)
        {
            return await QueueBackgroundTaskAsync(
                sessionId,
                effectiveRequest,
                intentDecision,
                executeToolAsync,
                activity,
                resumedSession,
                cancellationToken);
        }

        // Checklist: a planner ask that names more than one thing runs as ordered steps (see AssistantPlanner.Checklist.cs).
        // Research is included: "close the Figma tab. What is the grade" is two things, and the "what is" words alone would send
        // the whole ask to web research.
        if (intentDecision.Intent is AgentIntentKind.Planner or AgentIntentKind.Research && resumedSession is null && Checklist.LooksMultiPart(effectiveInput))
        {
            var steps = await SplitChecklistAsync(effectiveInput, sessionId, activity, cancellationToken);
            if (steps.Length > 1) return await RunChecklistAsync(steps, effectiveRequest, sessionId, intentDecision, executeToolAsync, activity, cancellationToken);
        }

        // Simple reply route: a single model call without tools.
        if (intentDecision.Intent == AgentIntentKind.SimpleReply)
        {
            var simple = await ExecuteSimpleReplyAsync(effectiveRequest, sessionId, resumedSession, cancellationToken);
            if (!ActionClaimGuard.IsUnverifiedClaim(simple.Response)) return simple;

            // Honesty gate: the simple-reply route has no tools, so a reply like "I've opened your inbox" is made up. The same request
            // goes through the planner below instead, which has the tools and the retry in the loop. Costs one extra model call, only when a claim was caught.
            intentDecision = intentDecision with { Intent = AgentIntentKind.Planner, Reason = "simple reply narrated an action" };
        }

        // Direct route: the request names one tool, so run that tool without asking the model to plan.
        if (intentDecision.Intent is AgentIntentKind.DirectTool or AgentIntentKind.Visual)
        {
            var direct = await ExecuteDirectToolIntentAsync(intentDecision, executeToolAsync, sessionId, cancellationToken);
            if (!ShouldReplanDirect(direct.ToolResults[0])) return direct;

            // The direct route guesses one tool from the words ("open the test site for me" -> open app "test site for me"; "wait why are you adding..." -> wait).
            // When that tool reports an error the guess was wrong, so the same request goes to the planner, which has every tool plus the honesty gate (one extra planner turn, only on a miss).
            intentDecision = intentDecision with { Intent = AgentIntentKind.Planner, Reason = $"direct route failed: {direct.ToolResults[0].Summary}" };
        }

        // Research route: web search with citations.
        if (intentDecision.Intent == AgentIntentKind.Research)
        {
            var research = await ExecuteResearchIntentAsync(effectiveInput, sessionId, cancellationToken);
            if (research.Citations.Length > 0) return research;

            // The "what is" word match sent this to web research and nothing came back (or research is off), so the planner
            // gets the same request instead of an empty reply.
            intentDecision = intentDecision with { Intent = AgentIntentKind.Planner, Reason = "research found nothing" };
        }

        // Default route: the full ask-model, run-tools, repeat loop. When it ends stuck, the bigger model may get one try (see AssistantPlanner.Escalation.cs).
        var stuck = new StuckNotes();
        var planned = await ExecutePlannedSessionAsync(
            effectiveRequest,
            sessionId,
            intentDecision,
            executeToolAsync,
            activity,
            resumedSession,
            false,
            cancellationToken,
            stuck);

        return stuck.Reason is null || resumedSession is not null
            ? planned
            : await EscalateIfStuckAsync(effectiveRequest, sessionId, intentDecision, executeToolAsync, activity, planned, stuck.Reason, cancellationToken);
    }

    // Replan after a failed direct tool call, except for "write down", whose error message should reach the user as it is.
    // A failed note's reply holds the user's words and the reason; the planner would only lose them.
    private static bool ShouldReplanDirect(PlannerToolResult result) =>
        result.IsError && !string.Equals(result.ApiName, "write down", StringComparison.OrdinalIgnoreCase);

    // Tools that are always offered to a local model (see SelectPlannerTools).
    private static readonly string[] CorePlannerTools =
    [
        "computer", "open app", "focus app", "close app", "type into", "send hotkey",
        "click element", "open path", "find files", "browse", "search web", "wait", "calendar", "write down", "page", "brain"
    ];

    // Limit local-model context to core tools and tools whose names match words in the request.
    // Small local models get confused by a long tool list, so a "local" provider only sees the core tools plus tools whose
    // name shares a word (4+ letters) with the request, at most 18. Other providers see every tool.
    private IEnumerable<PlannerToolDefinition> SelectPlannerTools(AssistantPlanningRequest request)
    {
        if (!string.Equals(_options.PlannerProvider?.Trim(), "local", StringComparison.OrdinalIgnoreCase))
        {
            return request.Tools;
        }

        var inputWords = request.UserInput
            .ToLowerInvariant()
            .Split([' ', ',', '.', '?', '!', ':', ';', '"', '\''], StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet();

        return request.Tools
            .Where(tool => CorePlannerTools.Contains(tool.CommandName, StringComparer.OrdinalIgnoreCase)
                || tool.CommandName.Split(' ').Any(word => word.Length >= 4 && (inputWords.Contains(word) || inputWords.Contains(word + "s"))))
            .OrderByDescending(tool => CorePlannerTools.Contains(tool.CommandName, StringComparer.OrdinalIgnoreCase))
            .Take(18);
    }

    // Starts the request as a background job: saves a "queued" session, then runs the normal planner loop on a worker thread
    // and updates the saved session as it goes. Returns immediately with a short confirmation.
    private async Task<AssistantPlanningResult> QueueBackgroundTaskAsync(
        string sessionId,
        AssistantPlanningRequest request,
        IntentDecision decision,
        Func<PlannerToolCall, CancellationToken, Task<PlannerToolResult>> executeToolAsync,
        IProgress<AssistantActivityEvent> activity,
        PersistedAgentSession? existingSession,
        CancellationToken cancellationToken)
    {
        // Reserve the background slot before promising anything: a full queue comes back as an already-faulted task.
        var slot = _backgroundScheduler.AcquireAsync(background: true, CancellationToken.None);
        if (slot.Exception is not null)
        {
            return new AssistantPlanningResult(
                "The background queue is full, so I did not start that. Try again after a background job finishes.",
                [],
                [],
                AgentIntentKind.Background);
        }

        var queuedStep = new PlanStepState("Queued", "queued", "Waiting for the background worker to start.");
        var session = new PersistedAgentSession(
            sessionId,
            "background-queued",
            AgentIntentKind.Background,
            existingSession?.OriginalRequest ?? request.UserInput,
            request.UserInput,
            string.Empty,
            existingSession?.LatestResponse ?? string.Empty,
            RunInBackground: true,
            existingSession?.CreatedAtUtc ?? DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            existingSession is { Steps.Length: > 0 }
                ? existingSession.Steps.Concat([queuedStep]).ToArray()
                : [queuedStep],
            existingSession?.ToolResults ?? [],
            existingSession?.Citations ?? []);

        try
        {
            await _stateStore.UpsertSessionAsync(session, cancellationToken);
            activity.Report(new AssistantActivityEvent(
                AssistantActivityKind.Background,
                "Queued a background workflow.",
                sessionId,
                Detail: request.UserInput));
        }
        catch
        {
            // Give the reserved slot back (now, or once it is granted) so a failed save cannot shrink the budget.
            _ = slot.ContinueWith(static reserved => reserved.Result.Dispose(), TaskContinuationOptions.OnlyOnRanToCompletion);
            throw;
        }

        // Register before starting: a fast completion must not leave a stale entry that blocks resume.
        _backgroundTasks[sessionId] = Task.CompletedTask;
        // Fire-and-forget worker. All its state changes use CancellationToken.None so the job keeps going after the turn that started it ends.
        _ = Task.Run(async () =>
        {
            try
            {
                using var backgroundScope = ModelWorkContext.EnterBackground();
                using var workLease = await slot;
                var backgroundRequest = request with
                {
                    UserInput = request.UserInput.Replace("in the background", string.Empty, StringComparison.OrdinalIgnoreCase).Trim()
                };

                await _stateStore.UpsertSessionAsync(
                    session with
                    {
                        Status = "background-running",
                        UpdatedAtUtc = DateTimeOffset.UtcNow,
                        Steps = session.Steps.Concat([new PlanStepState("Running", "in_progress", "Processing the background workflow.")]).ToArray()
                    },
                    CancellationToken.None);

                activity.Report(new AssistantActivityEvent(
                    AssistantActivityKind.Background,
                    "Background workflow started.",
                    sessionId,
                    Detail: backgroundRequest.UserInput));

                var result = await ExecutePlannedSessionAsync(
                    backgroundRequest,
                    sessionId,
                    decision with { RunInBackground = false, Intent = AgentIntentKind.Planner },
                    executeToolAsync,
                    activity,
                    session,
                    true,
                    CancellationToken.None);

                await _stateStore.UpsertSessionAsync(
                    session with
                    {
                        Status = "background-complete",
                        UpdatedAtUtc = DateTimeOffset.UtcNow,
                        LatestResponse = result.Response,
                        ToolResults = result.ToolResults.Select(ToToolExecution).ToArray(),
                        Citations = result.Citations,
                        Steps = session.Steps.Concat([new PlanStepState("Completed", "completed", "Background workflow finished.")]).ToArray()
                    },
                    CancellationToken.None);

                activity.Report(new AssistantActivityEvent(
                    AssistantActivityKind.Background,
                    $"Background workflow completed. {TrimForFeed(result.Response)}",
                    sessionId,
                    Detail: result.Response));
            }
            catch (Exception exception) // runs on CancellationToken.None, so even a cancellation is a failure
            {
                await _stateStore.UpsertSessionAsync(
                    session with
                    {
                        Status = "background-failed",
                        UpdatedAtUtc = DateTimeOffset.UtcNow,
                        LatestResponse = exception.Message,
                        Steps = session.Steps.Concat([new PlanStepState("Failed", "failed", TrimError(exception.Message))]).ToArray()
                    },
                    CancellationToken.None);

                activity.Report(new AssistantActivityEvent(
                    AssistantActivityKind.Error,
                    $"Background workflow failed: {TrimError(exception.Message)}",
                    sessionId,
                    Detail: exception.Message));
            }
            finally
            {
                _backgroundTasks.TryRemove(sessionId, out _);
            }
        });


        return new AssistantPlanningResult(
            "I queued that as a background task and will keep working on it while the app stays open.",
            [],
            [],
            AgentIntentKind.Background,
            StartedBackgroundTask: true,
            SessionId: sessionId);
    }

    // The core loop for the planner route. Each iteration: ask the model what to do; if it answers with plain text, that is the
    // final reply; otherwise run each tool it asked for, add the results to the conversation, and ask again. Stops after
    // PlannerMaxIterations rounds and then summarizes what happened. Progress is saved after every tool so a crash can be resumed.
    private async Task<AssistantPlanningResult> ExecutePlannedSessionAsync(
        AssistantPlanningRequest request,
        string sessionId,
        IntentDecision intentDecision,
        Func<PlannerToolCall, CancellationToken, Task<PlannerToolResult>> executeToolAsync,
        IProgress<AssistantActivityEvent> activity,
        PersistedAgentSession? existingSession,
        bool runInBackground,
        CancellationToken cancellationToken,
        StuckNotes? stuck = null)
    {
        // Start from any saved results if this is a resumed workflow.
        var toolResults = existingSession?.ToolResults.Select(ToPlannerToolResult).ToList() ?? new List<PlannerToolResult>();
        var citations = existingSession?.Citations.ToList() ?? new List<AssistantCitation>();
        var steps = existingSession?.Steps.ToList() ?? new List<PlanStepState>();

        if (existingSession is not null)
        {
            steps.Add(new PlanStepState(
                "Resume context",
                "completed",
                $"Resuming persisted workflow from `{existingSession.Status}` with {existingSession.ToolResults.Length} saved tool result{(existingSession.ToolResults.Length == 1 ? string.Empty : "s")} and {existingSession.Steps.Length} saved step{(existingSession.Steps.Length == 1 ? string.Empty : "s")}"));
        }

        // The conversation sent to the model: system prompt, memory, recent turns and the request (see AssistantPlanner.Formatting.cs).
        var messages = BuildConversationMessages(request, existingSession);
        // Describe the tools in the form the model's tool-calling API expects.
        var toolDefinitions = SelectPlannerTools(request)
            .Select(tool => new ChatToolDefinition(
                tool.ApiName,
                $"{tool.Description} Pass only the argument tail in `input`.",
                string.IsNullOrWhiteSpace(tool.InputDescription)
                    ? $"Optional argument tail for `{tool.CommandName}`."
                    : tool.InputDescription))
            .ToArray();

        await PersistActiveSessionAsync(
            sessionId,
            request.UserInput,
            intentDecision.Intent,
            steps,
            toolResults,
            citations,
            existingSession,
            runInBackground,
            cancellationToken);

        // At least one round, whatever the setting says.
        var maxIterations = Math.Max(1, _options.PlannerMaxIterations);
        // Whether the honesty gate has already nudged the model once this turn.
        var nudged = false;

        for (var iteration = 0; iteration < maxIterations; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stepTitle = $"Iteration {iteration + 1}";
            steps.Add(new PlanStepState(stepTitle, "in_progress", "Planning the next action."));

            activity.Report(new AssistantActivityEvent(
                AssistantActivityKind.Planning,
                $"Planning step {iteration + 1} of {maxIterations}.",
                sessionId,
                IsTransient: true,
                Detail: steps[^1].Detail));

            ChatCompletionResponse response;

            try
            {
                // If the model server is unreachable, an empty offline answer is used and the fallback summary path takes over.
                response = _modelGateway.IsAvailable
                    ? await ExecuteModelCallAsync(
                        new ChatCompletionRequest(messages.ToArray(), toolDefinitions, Temperature: 0.2, User: BuildModelSessionKey(sessionId, "planner")),
                        "planner",
                        sessionId,
                        activity,
                        ModelRoute.Reasoning,
                        cancellationToken)
                    : new ChatCompletionResponse(string.Empty, [], string.Empty);
            }
            // A model failure (other than the user cancelling) ends the loop with a fallback answer built from what is known so far.
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                steps[^1] = steps[^1] with { Status = "failed", Detail = TrimError(exception.Message) };
                // Stuck signal (c): the model call failed on every retry.
                stuck?.Note($"the model call failed ({exception.GetType().Name}): {TrimError(exception.Message)}");
                return await BuildFallbackPlannedResultAsync(request, sessionId, toolResults, citations, exception.Message, cancellationToken, stuck);
            }

            steps[^1] = steps[^1] with
            {
                Status = "completed",
                Detail = response.ToolCalls.Length == 0
                    ? "The model finalized the response without another tool call."
                    : $"Selected {response.ToolCalls.Length} tool step{(response.ToolCalls.Length == 1 ? string.Empty : "s")} for this iteration."
            };

            // No tool calls means the model gave its final answer as text.
            if (response.ToolCalls.Length == 0)
            {
                var content = (response.Content ?? string.Empty).Trim();

                // Honesty gate: no acting tool ran this turn (a screen read, a notes search or a failed call is not an action, per ToolSafetyService.IsReadOnlyCall),
                // yet the reply narrates an action, so it is made up. First time: push the reply back with a nudge (call the tool, or say "I can't do that yet") and loop once more.
                // Second time, or no iteration left: replace the reply with the fixed honest line.
                if (!ActionClaimGuard.Acted(toolResults) && ActionClaimGuard.IsUnverifiedClaim(content))
                {
                    steps[^1] = steps[^1] with { Status = "needs_correction", Detail = "The reply described actions but no tool ran." };
                    if (!nudged && iteration + 1 < maxIterations)
                    {
                        nudged = true;
                        messages.Add(BuildAssistantToolCallMessage(response));
                        messages.Add(new ChatMessage("user", [ChatMessageContentPart.FromText(ActionClaimGuard.Nudge)]));
                        continue;
                    }
                    content = ActionClaimGuard.HonestReplyAfter(toolResults);
                    // Stuck signal (a): the honesty gate replaced the reply. "I searched your files and found nothing" is a correct answer, so it does not count.
                    if (content == ActionClaimGuard.HonestReply) stuck?.Note("the honesty gate replaced a reply that claimed an action no tool performed");
                }
                // Stuck signal (c): the model answered with nothing usable. After tool work an empty reply is not stuck by itself:
                // the summary call below writes the answer and has its own checks.
                else if (string.IsNullOrWhiteSpace(content) && toolResults.Count == 0) stuck?.Note("the model returned an empty reply");
                // Signal (d): the model gave up, in the words the honesty nudge asks for. Logged as something Jarvis can't do yet, never handed off.
                else if (content.StartsWith(ActionClaimGuard.CannotPrefix, StringComparison.OrdinalIgnoreCase)) stuck?.Note(GaveUpReason);

                var finalResponse = string.IsNullOrWhiteSpace(content)
                    ? await BuildPlannedSummaryAsync(request.UserInput, toolResults, citations.ToArray(), sessionId, cancellationToken, stuck)
                    : AppendCitations(content, citations);

                await _stateStore.RemoveSessionAsync(sessionId, cancellationToken);

                return new AssistantPlanningResult(
                    finalResponse,
                    toolResults.ToArray(),
                    citations.ToArray(),
                    intentDecision.Intent,
                    ShouldExit: toolResults.LastOrDefault()?.ShouldExit ?? false,
                    SessionId: sessionId);
            }

            // The model wants tools run: keep its request in the conversation so the tool results can be matched to it.
            messages.Add(BuildAssistantToolCallMessage(response));

            foreach (var toolCall in response.ToolCalls)
            {
                var plannerToolCall = ParsePlannerToolCall(toolCall);
                var toolStepIndex = steps.Count;
                steps.Add(new PlanStepState(
                    $"Tool {plannerToolCall.ApiName}",
                    "in_progress",
                    BuildToolStepDetail(plannerToolCall)));

                activity.Report(new AssistantActivityEvent(
                    AssistantActivityKind.ToolStart,
                    $"Running {plannerToolCall.ApiName}.",
                    sessionId,
                    IsTransient: true,
                    Detail: BuildToolStepDetail(plannerToolCall)));

                // Actually run the tool (through the callback from JarvisAssistant, which applies the safety checks).
                var rawResult = await executeToolAsync(plannerToolCall, cancellationToken);
                // If the browser was opened for a request about the page's contents, also read the page now instead of trusting the model to.
                if (string.Equals(plannerToolCall.ApiName, "browse", StringComparison.OrdinalIgnoreCase) && !rawResult.IsError && RequestSurface.AsksAboutThePage(request.UserInput)
                    && !toolResults.Any(earlier => earlier.ApiName == plannerToolCall.ApiName && earlier.Input == plannerToolCall.Input))
                {
                    rawResult = await ReadOpenedPageAsync(rawResult, request.UserInput, executeToolAsync, cancellationToken);
                }

                // Judge whether the tool really did what was asked, and attach that to the result.
                var verificationReport = BuildToolVerificationReport(rawResult);
                var result = rawResult with
                {
                    VerificationStatus = verificationReport.Status,
                    VerificationSummary = verificationReport.Summary
                };
                toolResults.Add(result);

                var summarizedOutput = string.IsNullOrWhiteSpace(result.Summary)
                    ? TrimForFeed(result.Output)
                    : result.Summary;

                steps[toolStepIndex] = steps[toolStepIndex] with
                {
                    Status = result.IsError ? "needs_correction" : "completed",
                    Detail = $"{BuildToolStepDetail(plannerToolCall)} | observed: {summarizedOutput}"
                };

                steps.Add(new PlanStepState(
                    $"Verify {plannerToolCall.ApiName}",
                    verificationReport.PlanStepStatus,
                    verificationReport.Detail));

                activity.Report(new AssistantActivityEvent(
                    AssistantActivityKind.ToolComplete,
                    $"{plannerToolCall.ApiName}: {summarizedOutput}",
                    sessionId,
                    Detail: verificationReport.Detail));

                activity.Report(new AssistantActivityEvent(
                    ResolveVerificationActivityKind(verificationReport.Status),
                    $"Verification for {plannerToolCall.ApiName}: {verificationReport.Summary}",
                    sessionId,
                    IsTransient: true,
                    Detail: verificationReport.Detail));

                // Feed the tool's output back to the model, shortened to fit the context space still left.
                messages.Add(new ChatMessage(
                    "tool",
                    [ChatMessageContentPart.FromText(BuildToolMessageContent(result, ToolOutputRoom(toolResults.SkipLast(1))))],
                    ToolCallId: toolCall.Id));

                // Research tool output carries source links; collect them for the citation list.
                if (string.Equals(result.ApiName, "research", StringComparison.OrdinalIgnoreCase))
                {
                    citations.AddRange(ParseCitationsFromOutput(result.Output));
                }

                await PersistActiveSessionAsync(
                    sessionId,
                    request.UserInput,
                    intentDecision.Intent,
                    steps,
                    toolResults,
                    citations,
                    existingSession,
                    runInBackground,
                    cancellationToken);

                // A tool such as "exit" ends the turn at once with its output as the reply.
                if (result.ShouldExit)
                {
                    await _stateStore.RemoveSessionAsync(sessionId, cancellationToken);

                    return new AssistantPlanningResult(
                        result.Output,
                        toolResults.ToArray(),
                        citations.ToArray(),
                        intentDecision.Intent,
                        ShouldExit: true,
                        SessionId: sessionId);
                }
            }

            // Only an approval gate ends the turn here; after anything else the planner reads the result and decides for itself.
            if (toolResults.Count > 0 && string.Equals(ResolveVerificationStatus(toolResults[^1]), "awaiting_approval", StringComparison.OrdinalIgnoreCase))
            {
                activity.Report(new AssistantActivityEvent(
                    AssistantActivityKind.Summary,
                    "Reflection determined the current evidence is sufficient to finish.",
                    sessionId,
                    IsTransient: true,
                    Detail: "The workflow is paused pending approval."));

                var finalResponse = await BuildPlannedSummaryAsync(
                    request.UserInput,
                    toolResults,
                    citations.ToArray(),
                    sessionId,
                    cancellationToken);

                await _stateStore.RemoveSessionAsync(sessionId, cancellationToken);

                return new AssistantPlanningResult(
                    finalResponse,
                    toolResults.ToArray(),
                    citations.ToArray(),
                    intentDecision.Intent,
                    ShouldExit: toolResults.LastOrDefault()?.ShouldExit ?? false,
                    SessionId: sessionId);
            }
        }

        // Ran out of iterations: summarize what was done instead of looping forever.
        // Stuck signal (b): every iteration was used without a final answer.
        stuck?.Note($"the planner used all {maxIterations} iterations without a final answer");
        await _stateStore.RemoveSessionAsync(sessionId, cancellationToken);

        return new AssistantPlanningResult(
            await BuildPlannedSummaryAsync(request.UserInput, toolResults, citations.ToArray(), sessionId, cancellationToken, stuck),
            toolResults.ToArray(),
            citations.ToArray(),
            intentDecision.Intent,
            ShouldExit: toolResults.LastOrDefault()?.ShouldExit ?? false,
            SessionId: sessionId);
    }

    // Chains "wait 3 seconds, then summarize the page" after a browser open, because the small local model never called the page tool itself and made up a summary.
    // Waiting lets the page load before reading. Both calls go through the normal tool path, so the safety gate and the outside-content guard see them.
    // The page text comes back inside the browse result. A second browse of the same target in a turn does not read again.
    // ponytail: a fixed 3 s wait, plus the reader's own retries; a longer one if live Discord or Teams pages
    // read empty.
    private static async Task<PlannerToolResult> ReadOpenedPageAsync(
        PlannerToolResult opened,
        string request,
        Func<PlannerToolCall, CancellationToken, Task<PlannerToolResult>> executeToolAsync,
        CancellationToken cancellationToken)
    {
        await executeToolAsync(new PlannerToolCall("wait", "3"), cancellationToken);
        var page = await executeToolAsync(new PlannerToolCall("page", $"summarize {TruncateForContext(request, 160)}"), cancellationToken);
        var outcome = page.IsError ? $"Could not read the page: {page.Output}" : $"What the page shows: {page.Output}";
        return opened with { Output = $"{opened.Output}{Environment.NewLine}{outcome}" };
    }

    // Saves the current progress of a session to disk so an interrupted workflow can be resumed.
    private async Task PersistActiveSessionAsync(
        string sessionId,
        string latestInput,
        AgentIntentKind intent,
        IReadOnlyList<PlanStepState> steps,
        IReadOnlyList<PlannerToolResult> toolResults,
        IReadOnlyList<AssistantCitation> citations,
        PersistedAgentSession? existingSession,
        bool runInBackground,
        CancellationToken cancellationToken)
    {
        var latestToolResult = toolResults.LastOrDefault();
        var latestResponse = latestToolResult is null
            ? existingSession?.LatestResponse ?? string.Empty
            : string.IsNullOrWhiteSpace(latestToolResult.Summary)
                ? latestToolResult.Output
                : latestToolResult.Summary;

        await _stateStore.UpsertSessionAsync(
            new PersistedAgentSession(
                sessionId,
                runInBackground ? "background-running" : "active",
                runInBackground ? AgentIntentKind.Background : intent,
                existingSession?.OriginalRequest ?? latestInput,
                latestInput,
                string.Empty,
                latestResponse,
                RunInBackground: runInBackground,
                existingSession?.CreatedAtUtc ?? DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                steps.ToArray(),
                toolResults.Select(ToToolExecution).ToArray(),
                citations.ToArray()),
            cancellationToken);
    }

    // Saves a session that is waiting for the user to answer a question.
    private async Task SaveClarificationSessionAsync(
        string sessionId,
        string originalRequest,
        IntentDecision decision,
        PersistedAgentSession? existingSession,
        CancellationToken cancellationToken)
    {
        var createdAt = existingSession?.CreatedAtUtc ?? DateTimeOffset.UtcNow;

        await _stateStore.UpsertSessionAsync(
            new PersistedAgentSession(
                sessionId,
                "awaiting-clarification",
                AgentIntentKind.Clarification,
                existingSession?.OriginalRequest ?? originalRequest,
                originalRequest,
                decision.FollowUpQuestion,
                string.Empty,
                RunInBackground: existingSession?.RunInBackground ?? false,
                createdAt,
                DateTimeOffset.UtcNow,
                existingSession?.Steps ?? [],
                existingSession?.ToolResults ?? [],
                existingSession?.Citations ?? []),
            cancellationToken);
    }

    // Text for a resumed workflow: the original request, plus the user's new instruction if there is one.
    private static string BuildResumedInput(string latestUserInput, PersistedAgentSession session)
    {
        if (string.IsNullOrWhiteSpace(latestUserInput) || LooksLikeResumeRequest(latestUserInput))
        {
            return session.OriginalRequest;
        }

        return string.Join(
            Environment.NewLine,
            session.OriginalRequest,
            $"Additional instruction for the resumed workflow: {latestUserInput.Trim()}");
    }

    // Maps how well a tool result was confirmed to an activity kind: confirmed results are summaries, anything else a warning.
    private static AssistantActivityKind ResolveVerificationActivityKind(string verificationStatus) =>
        verificationStatus switch
        {
            "verified" or "observed" => AssistantActivityKind.Summary,
            _ => AssistantActivityKind.Warning
        };
}
