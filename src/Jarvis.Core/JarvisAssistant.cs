// JarvisAssistant is the front door of Jarvis.Core: the app's UI or voice code hands it one line of text and gets back
// one AssistantTurn (the reply plus bookkeeping). This is where to start reading.
// Path of one request, in order:
// 1. HandleAsync (below) cleans the input: finishes a pending follow-up, removes "hey Jarvis", works out privacy
// (MemoryAutomation) and maps wording to a command name (JarvisCommandCatalog.NormalizeToCommand).
// 2. A pending approval ("yes" / "no" to a risky action asked about earlier) is handled first.
// 3. If a built-in tool recognises the text (tool.CanHandle, the list built in the constructor), that tool runs directly.
// Every tool call goes through ExecuteToolWithSafetyAsync, which asks ToolSafetyService to allow, block or require approval.
// 4. Otherwise a small social reply ("good morning") may be answered, or the text goes to the planner
// (HandleWithPlannerAsync -> AssistantPlanner.cs). The planner may classify the request (AssistantPlanner.Intents.cs),
// build prompts (AssistantPlanner.Formatting.cs), call the language model, a local server such as Jarvis's own llama-server (AssistantPlanner.ModelCalls.cs),
// and run the tools the model picks by calling back into ExecutePlannerToolAsync here (tool-call parsing is in
// AssistantPlanner.ToolCalling.cs, multi-part requests in AssistantPlanner.Checklist.cs). ActionClaimGuard.cs catches replies
// that claim an action no tool performed.
// 5. The reply is styled (AssistantPresenceStyle), saved to short-term state (ConversationState.cs), the transcript and
// long-term memory (unless the turn was private), and returned as an AssistantTurn (AssistantTurn.cs). SpokenReply decides
// what part is read aloud. Progress messages go out through the Activity event during all of this.
namespace Jarvis.Core;

// The assistant object: owns the tool list, the planner, memory and transcript stores, and the approval and safety services.
public sealed class JarvisAssistant
{
    private readonly ToolContext _toolContext;
    private readonly JsonMemoryStore _memoryStore;
    private readonly JsonLineTranscriptStore _transcriptStore;
    private readonly IAgentStateStore _agentStateStore;
    private readonly IReadOnlyList<IAssistantTool> _tools;
    private readonly IAssistantPlanner _planner;
    private readonly IPendingApprovalStore _pendingApprovalStore;
    private readonly ToolSafetyService _toolSafety;
    private readonly ShortTermConversationState _conversationState = new();
    // How long (30 seconds) a "what should I write down?" style question stays open; after that the next input is treated as a new request.
    private static readonly TimeSpan FollowUpCommandLifetime = TimeSpan.FromSeconds(30);
    // Answers that cancel a pending follow-up question instead of completing it.
    private static readonly HashSet<string> FollowUpCancelPhrases = new(StringComparer.OrdinalIgnoreCase)
    {
        "cancel", "never mind", "nevermind", "forget it", "no", "stop"
    };
    // The command waiting for the user's answer, and when it was asked.
    private (string Command, DateTimeOffset AskedUtc)? _pendingFollowUpCommand;

    // Prompt-injection guard: has this turn's planner read outside content (see ToolSafetyService.ReadsOutsideContent)?
    // Turns run concurrently, so HandleAsync gives each its own box. A box is needed because a value set in a child
    // async method does not flow back to its caller; tools set the flag on the shared box instead. Background
    // workflows queued by a turn run on a copy of its context, so they share its box and start with its mark.
    // Shared flag for one turn: set once any tool has read outside content (a web page, mail...). Afterwards actions that
    // change things ask for approval, so text from a page cannot silently make Jarvis act. volatile because tools and the turn may run on different threads.
    private sealed class OutsideReadMark { public volatile bool Read; }
    // Holds the current turn's mark; AsyncLocal gives each concurrent turn its own value.
    private readonly AsyncLocal<OutsideReadMark?> _outsideRead = new();
    private readonly ILectureRecorder? _lectureRecorder;

    // True while a lecture recording is in progress.
    public bool IsRecordingLecture => _lectureRecorder?.IsRecording == true;

    // Optional tutor service, exposed for the UI.
    public ITutorService? Tutor { get; }

    // Wires everything together and registers every tool. The order of the tool list matters: HandleAsync uses the first tool whose
    // CanHandle accepts the text, so earlier tools take priority over later ones.
    public JarvisAssistant(
        JarvisOptions options,
        JsonMemoryStore memoryStore,
        IDesktopAutomationService desktopAutomation,
        IWeatherService weatherService,
        IWebResearchService webResearchService,
        IVisualContextService visualContextService,
        JsonLineTranscriptStore transcriptStore,
        IAgentStateStore agentStateStore,
        IAssistantPlanner planner,
        IPendingApprovalStore pendingApprovalStore,
        ToolSafetyService toolSafety,
        string workspaceRoot,
        IModelGateway? modelGateway = null,
        IBrowserPageReader? browserPageReader = null,
        ILectureRecorder? lectureRecorder = null,
        ITutorService? tutor = null)
    {
        Tutor = tutor;
        Options = options;
        _lectureRecorder = lectureRecorder;
        _agentStateStore = agentStateStore;
        _planner = planner;
        _memoryStore = memoryStore;
        _pendingApprovalStore = pendingApprovalStore;
        _toolSafety = toolSafety;
        // The shared context object every tool receives when it runs.
        _toolContext = new ToolContext(
            options,
            memoryStore,
            transcriptStore,
            desktopAutomation,
            weatherService,
            webResearchService,
            visualContextService,
            workspaceRoot,
            planner.IsAvailable,
            planner.Status)
        {
            // A note is saved outside Jarvis, so off-the-record and sensitive turns stay out of it.
            RecentTurns = count => _conversationState.GetSnapshot(count).Entries.Where(entry => !entry.IsPrivate).ToArray()
        };
        _transcriptStore = transcriptStore;

        // Register the built-in tools. The help tool receives a function that returns the list so it can describe all of them.
        var tools = new List<IAssistantTool>();
        tools.Add(new HelpTool(() => tools));
        // Early on purpose: while a lesson runs, `next` is the tutor's, not the media tool's.
        tools.Add(new TeachTool(tutor));
        tools.Add(new BrainTool(modelGateway));
        tools.Add(new StatusTool());
        tools.Add(new TimeTool());
        tools.Add(new RememberTool());
        tools.Add(new RecallTool());
        tools.Add(new ListMemoryTool());
        tools.Add(new ReminderTool());
        tools.Add(new ForgetMemoryTool());
        tools.Add(new MemoryProfileTool());
        tools.Add(new WeatherTool());
        tools.Add(new WriteDownTool(modelGateway));
        tools.Add(new CalendarTool());
        tools.Add(new PageTool(browserPageReader, modelGateway));
        tools.Add(new LectureNotesTool(lectureRecorder, modelGateway));
        tools.Add(new SleepTool());
        tools.Add(new SystemStateTool());
        tools.Add(new ComputerContextTool());
        tools.Add(new NetworkTool());
        tools.Add(new VpnTool());
        tools.Add(new ApplicationsTool());
        tools.Add(new OpenApplicationTool());
        tools.Add(new OpenPathTool());
        tools.Add(new ListFilesTool());
        tools.Add(new ReadFileTool());
        tools.Add(new FindFilesTool());
        tools.Add(new WriteFileTool());
        tools.Add(new AppendFileTool());
        tools.Add(new ReplaceInFileTool());
        tools.Add(new MovePathTool());
        tools.Add(new CopyPathTool());
        tools.Add(new DeletePathTool());
        tools.Add(new BrowseTool());
        tools.Add(new SearchWebTool());
        tools.Add(new SpotifyTool());
        tools.Add(new CodeTool());
        tools.Add(new ChromeTool());
        tools.Add(new ExplorerAppTool());
        tools.Add(new OfficeTool());
        tools.Add(new DiscordTool());
        tools.Add(new SlackTool());
        tools.Add(new WindowsTool());
        tools.Add(new AppStateTool());
        tools.Add(new FocusAppTool());
        tools.Add(new CloseAppTool());
        tools.Add(new WindowStateTool());
        tools.Add(new MoveWindowTool());
        tools.Add(new ResizeWindowTool());
        tools.Add(new SnapWindowTool());
        tools.Add(new SendHotkeyTool());
        tools.Add(new ClickElementTool());
        tools.Add(new TypeIntoElementTool());
        tools.Add(new GetElementTextTool());
        tools.Add(new ScrollWindowTool());
        tools.Add(new MediaTool());
        tools.Add(new ClipboardTool());
        tools.Add(new NotifyTool());
        tools.Add(new WaitTool());
        tools.Add(new ResearchTool());
        tools.Add(new ScreenTool());
        tools.Add(new AnalyzeImageTool());
        tools.Add(new AnalyzeDocumentTool());
        tools.Add(new ShellTool());
        tools.Add(new ExitTool());
        _tools = tools;
    }

    // The settings this assistant was built with.
    public JarvisOptions Options { get; }

    // Raised with progress messages ("Running browse", warnings, errors) for the UI to show, while a request is being handled.
    public event EventHandler<AssistantActivityEventArgs>? Activity;

    // Thin pass-throughs that let the UI read memory, sessions and approvals without touching the stores directly.
    public Task<PendingApprovalAction?> GetPendingApprovalAsync(CancellationToken cancellationToken) =>
        _pendingApprovalStore.GetAsync(cancellationToken);

    public Task<PersistedAgentSession?> GetPendingClarificationSessionAsync(CancellationToken cancellationToken) =>
        _agentStateStore.GetPendingClarificationAsync(cancellationToken);

    public Task<PersistedAgentSession?> GetSessionAsync(string sessionId, CancellationToken cancellationToken) =>
        _agentStateStore.GetSessionAsync(sessionId, cancellationToken);

    public Task<IReadOnlyList<PersistedAgentSession>> GetBackgroundSessionsAsync(CancellationToken cancellationToken) =>
        _agentStateStore.GetBackgroundSessionsAsync(cancellationToken);

    public Task<IReadOnlyList<MemoryNote>> GetMemoriesAsync(CancellationToken cancellationToken) =>
        _memoryStore.GetRecentAsync(200, cancellationToken);

    public Task<IReadOnlyList<MemoryNote>> GetPendingRemindersAsync(CancellationToken cancellationToken) =>
        _memoryStore.GetPendingRemindersAsync(32, cancellationToken);

    public Task<MemoryNote?> UpdateMemoryAsync(MemoryUpdateRequest request, CancellationToken cancellationToken) =>
        _memoryStore.UpdateAsync(request, cancellationToken);

    public Task<bool> ForgetMemoryAsync(string id, string reason, CancellationToken cancellationToken) =>
        _memoryStore.ForgetAsync(id, reason, cancellationToken);

    // Called once at startup; lets the planner announce unfinished workflows. Does nothing when the planner is disabled.
    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (!_planner.IsAvailable)
        {
            return Task.CompletedTask;
        }

        return _planner.InitializeAsync(ExecutePlannerToolAsync, CreateActivityProgress(), cancellationToken);
    }

    // Handles one user input from start to finish and returns the finished turn. See the header for the overall route.
    public async Task<AssistantTurn> HandleAsync(string input, CancellationToken cancellationToken)
    {
        // Fresh prompt-injection flag for this turn.
        _outsideRead.Value = new OutsideReadMark();
        // If the previous reply asked for the rest of a command, this input is the answer.
        input = CompletePendingFollowUpCommand(input, out var cancelledFollowUp);
        // Works out what the turn should be saved as: the effective text and whether it is private ("off the record").
        var persistencePlan = MemoryAutomation.PrepareTurn(input);
        // Removes the wake phrase and maps loose wording to a command name.
        var normalizedInput = NormalizeInput(persistencePlan.EffectiveInput);
        ToolResult result;
        AgentIntentKind intent = AgentIntentKind.DirectTool;
        string sessionId = string.Empty;
        bool isFollowUpQuestion = false;
        AssistantToolExecution[] toolResults = [];
        AssistantCitation[] citations = [];
        MemoryProfileSnapshot? memoryProfile = null;

        // Choose who produces the reply: an empty input, a cancelled follow-up, a private "remember" command, a pending approval,
        // a directly matched tool, a social reply, or the planner.
        if (string.IsNullOrWhiteSpace(normalizedInput))
        {
            result = new ToolResult($"{Options.AssistantName} online. Use `help` to inspect the current command surface.");
        }
        else if (cancelledFollowUp)
        {
            result = new ToolResult("Okay, never mind.");
        }
        // A "remember ..." command in an off-the-record turn is acknowledged but not saved.
        else if (!persistencePlan.PersistAutomaticMemory && LooksLikeRememberCommand(normalizedInput))
        {
            result = new ToolResult(
                "Noted privately. I did not persist that memory.",
                VerificationText: "The user marked the turn as off the record, so the memory was not stored.",
                SummaryText: "Private memory was not persisted.");
        }
        else
        {
            // A waiting "may I do this?" question takes priority: the user's yes or no answers it.
            var pendingApprovalOutcome = await TryHandlePendingApprovalAsync(normalizedInput, cancellationToken);

            if (pendingApprovalOutcome is not null)
            {
                result = pendingApprovalOutcome.Result;
                intent = pendingApprovalOutcome.Intent;
                sessionId = pendingApprovalOutcome.SessionId;
                isFollowUpQuestion = pendingApprovalOutcome.IsFollowUpQuestion;
                toolResults = pendingApprovalOutcome.ToolResults;
                citations = pendingApprovalOutcome.Citations;
            }
            else
            {
                // First tool that claims the input handles it directly, with no model involved.
                var tool = _tools.FirstOrDefault(candidate => candidate.CanHandle(normalizedInput));
                if (tool is null)
                {
                    // No tool matched: try a built-in social reply, otherwise ask the planner.
                    memoryProfile = await _memoryStore.GetProfileAsync(cancellationToken);
                    var preferredUserAddress = AssistantPersonaConfiguration.ResolvePreferredUserAddress(Options, memoryProfile);

                    if (AssistantPresenceStyle.TryBuildSocialReply(normalizedInput, Options, preferredUserAddress, out var socialReply))
                    {
                        intent = AgentIntentKind.SimpleReply;
                        result = new ToolResult(
                            socialReply,
                            SummaryText: TrimForSummary(socialReply),
                            VerificationText: "The runtime handled the input as a direct social-presence reply without tool execution.");

                        EmitActivity(new AssistantActivityEvent(
                            AssistantActivityKind.Status,
                            "Handled as a direct presence reply.",
                            Detail: socialReply));
                    }
                    else
                    {
                        var planningOutcome = await HandleWithPlannerAsync(normalizedInput, memoryProfile, cancellationToken);
                        result = planningOutcome.Result;
                        intent = planningOutcome.Intent;
                        sessionId = planningOutcome.SessionId;
                        isFollowUpQuestion = planningOutcome.IsFollowUpQuestion;
                        toolResults = planningOutcome.ToolResults;
                        citations = planningOutcome.Citations;
                    }
                }
                else
                {
                    // A tool matched: run it through the safety check.
                    var toolInput = ToolInputHelper.GetCommandTail(normalizedInput, tool.Name);

                    EmitActivity(new AssistantActivityEvent(
                        AssistantActivityKind.Classification,
                        $"Matched the direct `{tool.Name}` tool route.",
                        Detail: string.IsNullOrWhiteSpace(toolInput) ? tool.Description : toolInput));

                    EmitActivity(new AssistantActivityEvent(
                        AssistantActivityKind.ToolStart,
                        $"Running {tool.Name}.",
                        Detail: toolInput));

                    result = await ExecuteToolWithSafetyAsync(tool, normalizedInput, input, cancellationToken);

                    // The tool asked a question and needs the next input as its answer; remember the command for CompletePendingFollowUpCommand.
                    if (!string.IsNullOrWhiteSpace(result.FollowUpCommand))
                    {
                        _pendingFollowUpCommand = (result.FollowUpCommand, DateTimeOffset.UtcNow);
                        isFollowUpQuestion = true;
                    }
                    toolResults = [BuildToolExecution(tool.Name, toolInput, result, result.ResponseText)];

                    if (!LooksLikeSafetyIntervention(result))
                    {
                        EmitActivity(new AssistantActivityEvent(
                            result.Succeeded && !InferToolError(result.ResponseText)
                                ? AssistantActivityKind.ToolComplete
                                : AssistantActivityKind.Error,
                            $"{tool.Name}: {toolResults[0].Summary}",
                            Detail: toolResults[0].VerificationText));
                    }

                    // Cheap-route fallback: a word match picked this tool ("open the test site for me" ->
                    // open app "test site for me"; "wait why are you adding..." -> wait) and the tool reported an error, so the
                    // guess was wrong: the user's own words (not the rewritten command) go to the planner, which has every tool plus
                    // the honesty gate. Voice input arrives already rewritten, so this only changes typed input. One planner turn, only on a miss.
                    // Safety stops and follow-up questions are not misses and stay as they are.
                    if ((!result.Succeeded || InferToolError(result.ResponseText))
                        && !LooksLikeSafetyIntervention(result)
                        && string.IsNullOrWhiteSpace(result.FollowUpCommand)
                        // SkipReplan: a failed sticky note keeps its own error; the planner's chat steps wrote no note and hid the failure.
                        && !result.SkipReplan)
                    {
                        memoryProfile ??= await _memoryStore.GetProfileAsync(cancellationToken);
                        var replanned = await HandleWithPlannerAsync(StripWakeAddressing(persistencePlan.EffectiveInput.Trim()), memoryProfile, cancellationToken);
                        result = replanned.Result;
                        intent = replanned.Intent;
                        sessionId = replanned.SessionId;
                        isFollowUpQuestion = replanned.IsFollowUpQuestion;
                        toolResults = replanned.ToolResults;
                        citations = replanned.Citations;
                    }
                }
            }
        }

        // Apply the configured persona to the reply text (tone, how the user is addressed).
        if (!string.IsNullOrWhiteSpace(result.ResponseText))
        {
            memoryProfile ??= await _memoryStore.GetProfileAsync(cancellationToken);
            var preferredUserAddress = AssistantPersonaConfiguration.ResolvePreferredUserAddress(Options, memoryProfile);
            var presentedResponse = AssistantPresenceStyle.ApplyResponsePresence(
                persistencePlan.EffectiveInput,
                result.ResponseText,
                Options,
                intent,
                isFollowUpQuestion,
                preferredUserAddress);

            if (!string.Equals(presentedResponse, result.ResponseText, StringComparison.Ordinal))
            {
                result = result with { ResponseText = presentedResponse };
            }
        }

        // Snapshot of the desktop at this moment, stored with the turn.
        var interactionContext = await CaptureInteractionContextAsync(
            persistencePlan.EffectiveInput,
            toolResults,
            cancellationToken);

        // Build the finished turn record.
        var turn = new AssistantTurn(
            DateTimeOffset.UtcNow,
            input,
            result.ResponseText,
            result.ShouldExit,
            intent.ToString(),
            sessionId,
            isFollowUpQuestion,
            toolResults,
            citations,
            persistencePlan.PrivacyMode,
            interactionContext,
            result.SpeakInFull);

        // Remember it for the next few follow-up questions in this session.
        _conversationState.AddTurn(turn);

        // Saved to disk unless the turn was private.
        if (persistencePlan.PersistTranscript)
        {
            await _transcriptStore.AppendAsync(turn, cancellationToken);
        }

        // Learn preferences and notes from the turn, and sometimes write a conversation summary note, unless private.
        if (persistencePlan.PersistAutomaticMemory)
        {
            await LearnFromTurnAsync(persistencePlan.EffectiveInput, toolResults, interactionContext, cancellationToken);

            if (persistencePlan.PersistTranscript)
            {
                await TryCreateConversationSummaryAsync(cancellationToken);
            }
        }

        return turn;
    }

    /// <summary>
    /// When the last reply asked for the rest of a command ("What should I write down?"), this input is
    /// the answer, so it is appended to that command. Expires so an answer never given cannot swallow a
    /// later, unrelated request.
    /// </summary>
    // Continues a command when the previous reply asked a question. Returns the original input if nothing is pending, the
    // question expired, or the user cancelled.
    private string CompletePendingFollowUpCommand(string input, out bool cancelled)
    {
        cancelled = false;

        if (_pendingFollowUpCommand is not { } pending || string.IsNullOrWhiteSpace(input))
        {
            return input;
        }

        _pendingFollowUpCommand = null;

        if (DateTimeOffset.UtcNow - pending.AskedUtc > FollowUpCommandLifetime)
        {
            return input;
        }

        var answer = StripWakeAddressing(input.Trim());
        cancelled = FollowUpCancelPhrases.Contains(answer.Trim(' ', '.', ',', '!'));
        return cancelled ? input : $"{pending.Command} {answer}";
    }

    // Strips wake words and maps the text to a command name.
    private string NormalizeInput(string input)
    {
        var value = StripWakeAddressing(input.Trim());
        return JarvisCommandCatalog.NormalizeToCommand(value);
    }

    // Removes the assistant's name or wake phrase from the start ("Jarvis, open Chrome" becomes "open Chrome").
    private string StripWakeAddressing(string value)
    {
        var current = value;
        current = StripGreetingAddressPrefix(current, Options.AssistantName);
        current = StripGreetingAddressPrefix(current, Options.WakePhrase);
        current = StripPrefix(current, Options.AssistantName);
        current = StripPrefix(current, Options.WakePhrase);
        return current;
    }

    // Removes "hey/hi/hello <name>" from the start. If nothing else is said, only the greeting is kept so it is still answered.
    private static string StripGreetingAddressPrefix(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return value;
        }

        var current = value.Trim();

        foreach (var greeting in new[] { "hey", "hi", "hello" })
        {
            foreach (var candidate in new[]
            {
                $"{greeting} {name}",
                $"{greeting}, {name}",
                $"{greeting} {name},",
                $"{greeting}, {name},"
            })
            {
                if (!current.StartsWith(candidate, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var remainder = current[candidate.Length..].TrimStart(' ', ',', ':', ';', '-');
                return string.IsNullOrWhiteSpace(remainder) ? greeting : remainder;
            }
        }

        return current;
    }

    // Packages the request for the planner (recent turns, relevant memories, desktop state, tool list), runs it, and converts its
    // result back into the shape HandleAsync uses. Errors become a visible message instead of an exception.
    private async Task<PlanningOutcome> HandleWithPlannerAsync(
        string originalInput,
        MemoryProfileSnapshot? memoryProfile,
        CancellationToken cancellationToken)
    {
        if (!_planner.IsAvailable)
        {
            return new PlanningOutcome(
                new ToolResult(BuildFallbackResponse()),
                AgentIntentKind.Planner,
                string.Empty,
                false,
                [],
                []);
        }

        // Context gathering: recent turns, memory profile, recent tool results, and the live desktop state.
        var recentTurns = await _transcriptStore.GetRecentAsync(
            Math.Max(0, Options.PlannerRecentTurnCount),
            cancellationToken);
        memoryProfile ??= await _memoryStore.GetProfileAsync(cancellationToken);
        var recentToolResults = recentTurns
            .SelectMany(turn => turn.ToolResults ?? Array.Empty<AssistantToolExecution>())
            .TakeLast(Math.Max(0, Options.PlannerRecentToolResultCount))
            .ToArray();

        string computerContext;
        DesktopContextSnapshot desktopContext;

        // Desktop information can fail (a window closing mid-read); the turn goes on without it.
        try
        {
            desktopContext = await _toolContext.DesktopAutomation.GetContextSnapshotAsync(cancellationToken);
            computerContext = await _toolContext.DesktopAutomation.GetComputerContextAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            desktopContext = new DesktopContextSnapshot(DateTimeOffset.Now, string.Empty, string.Empty, [], [], 0, string.Empty);
            computerContext = $"Computer context unavailable: {TrimErrorMessage(exception.Message)}";
        }

        // Pick memories related to this request, then pad with the newest ones if there are too few.
        var memoryQueryContext = BuildMemoryQueryContext(originalInput, desktopContext);
        var relevantMemories = await _memoryStore.SearchAsync(
            originalInput,
            memoryQueryContext,
            Math.Max(0, Options.PlannerRecentMemoryCount),
            cancellationToken);

        if (relevantMemories.Count < Math.Max(0, Options.PlannerRecentMemoryCount))
        {
            var recentFallback = await _memoryStore.GetRecentAsync(
                Math.Max(0, Options.PlannerRecentMemoryCount * 2),
                cancellationToken);
            relevantMemories = relevantMemories
                .Concat(recentFallback.Where(note => relevantMemories.All(existing => !string.Equals(existing.Id, note.Id, StringComparison.OrdinalIgnoreCase))))
                .Take(Math.Max(0, Options.PlannerRecentMemoryCount))
                .ToArray();
        }

        // ponytail: this automatic context is not tainted-tracked but can carry outside text into a turn that never read
        // any: window titles and app names (page titles), email/task titles and details, recent assistant replies and tool
        // summaries (which may quote a page), and stored memories. It is short and trimmed, not whole pages. Fence it
        // like BrainWorkspace.Fence, or start every turn marked, if that proves to be a way around the guard.
        var request = new AssistantPlanningRequest(
            Options.AssistantName,
            originalInput,
            _toolContext.WorkspaceRoot,
            computerContext,
            Options.ShellExecutionEnabled,
            _tools.Select(BuildPlannerToolDefinition).ToArray(),
            relevantMemories,
            recentTurns,
            recentToolResults,
            _conversationState.GetSnapshot(Math.Max(4, Options.PlannerRecentTurnCount)),
            memoryProfile,
            BrainWorkspace.BuildContext(Options, originalInput));

        AssistantPlanningResult plan;

        try
        {
            plan = await _planner.ExecuteTurnAsync(
                request,
                ExecutePlannerToolAsync,
                CreateActivityProgress(),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Console.Error.WriteLine(exception); // the reply only carries the message; the stack goes to stderr for whoever is watching
            return new PlanningOutcome(
                new ToolResult(BuildPlannerErrorResponse(exception), Succeeded: false),
                AgentIntentKind.Planner,
                string.Empty,
                false,
                [],
                []);
        }

        // Three outcomes: the planner wrote a reply; it only ran tools, so the last tool's output is the reply; it produced nothing, so a standard "could not complete" message is used.
        if (!string.IsNullOrWhiteSpace(plan.Response))
        {
            return new PlanningOutcome(
                new ToolResult(plan.Response.Trim(), plan.ShouldExit, SummaryText: TrimForSummary(plan.Response)),
                plan.Intent,
                plan.SessionId,
                plan.RequiresClarification,
                plan.ToolResults.Select(ToToolExecution).ToArray(),
                plan.Citations);
        }

        if (plan.ToolResults.Length > 0)
        {
            var lastToolResult = plan.ToolResults[^1];
            return new PlanningOutcome(
                new ToolResult(
                    lastToolResult.Output,
                    lastToolResult.ShouldExit,
                    Succeeded: !lastToolResult.IsError,
                    VerificationText: lastToolResult.Verification,
                    SummaryText: lastToolResult.Summary),
                plan.Intent,
                plan.SessionId,
                plan.RequiresClarification,
                plan.ToolResults.Select(ToToolExecution).ToArray(),
                plan.Citations);
        }

        return new PlanningOutcome(
            new ToolResult(BuildFallbackResponse()),
            plan.Intent,
            plan.SessionId,
            plan.RequiresClarification,
            [],
            plan.Citations);
    }

    // If the app is waiting for a yes/no about a risky action, applies the user's answer: "no" clears it, "yes" runs the saved
    // command with approval already given, anything else is ignored (and the request goes on normally). Returns null when nothing was pending.
    private async Task<PlanningOutcome?> TryHandlePendingApprovalAsync(
        string normalizedInput,
        CancellationToken cancellationToken)
    {
        var pendingApproval = await _pendingApprovalStore.GetAsync(cancellationToken);

        if (pendingApproval is null)
        {
            return null;
        }

        if (_toolSafety.LooksLikeDenialResponse(normalizedInput))
        {
            await _pendingApprovalStore.ClearAsync(cancellationToken);

            EmitActivity(new AssistantActivityEvent(
                AssistantActivityKind.Warning,
                "Canceled the pending major-action approval.",
                Detail: pendingApproval.Summary));

            return new PlanningOutcome(
                new ToolResult(
                    string.Join(
                        Environment.NewLine,
                        "Canceled the pending action.",
                        pendingApproval.Summary),
                    SummaryText: "Canceled the pending major-action approval.",
                    VerificationText: "The pending approved action was canceled before execution."),
                AgentIntentKind.Clarification,
                string.Empty,
                false,
                [],
                []);
        }

        if (!_toolSafety.LooksLikeApprovalResponse(normalizedInput))
        {
            return null;
        }

        await _pendingApprovalStore.ClearAsync(cancellationToken);

        var tool = _tools.FirstOrDefault(candidate => string.Equals(candidate.Name, pendingApproval.ToolName, StringComparison.OrdinalIgnoreCase));

        if (tool is null)
        {
            return new PlanningOutcome(
                new ToolResult(
                    $"The pending action could not be completed because the `{pendingApproval.ToolName}` tool is no longer available.",
                    Succeeded: false,
                    SummaryText: "Pending approval could not be executed.",
                    VerificationText: "The pending action referred to a missing tool."),
                AgentIntentKind.Clarification,
                string.Empty,
                false,
                [],
                []);
        }

        EmitActivity(new AssistantActivityEvent(
            AssistantActivityKind.Warning,
            $"Approval received. Running the pending `{tool.Name}` action.",
            Detail: pendingApproval.Summary));

        var result = await ExecuteToolWithSafetyAsync(
            tool,
            pendingApproval.NormalizedCommand,
            pendingApproval.OriginalInput,
            cancellationToken,
            bypassApproval: true);

        return new PlanningOutcome(
            result,
            AgentIntentKind.Clarification,
            string.Empty,
            false,
            [BuildToolExecution(tool.Name, ToolInputHelper.GetCommandTail(pendingApproval.NormalizedCommand, tool.Name), result, result.ResponseText)],
            []);
    }

    // Removes a leading name from the text, plus any separating punctuation.
    private static string StripPrefix(string value, string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix) || !value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        var stripped = value[prefix.Length..];
        return stripped.TrimStart(' ', ',', ':', ';', '-');
    }

    // Standard reply when no useful answer was produced; lists what the assistant can do.
    private string BuildFallbackResponse()
    {
        if (_planner.IsAvailable)
        {
            return string.Join(
                Environment.NewLine,
                "Agent runtime is active, but I could not confidently complete that request.",
                "Try rephrasing it naturally or use `help` to inspect the current command surface.",
                "I currently handle memory, live computer context, app discovery and launch, app adapters for Spotify, VS Code, Chrome, Explorer, Discord, and Slack, window management, clipboard and media controls, Win32 UI automation, file create/edit/move/copy/delete actions, browser and form workflows, notifications, web research, user-state context, visual analysis for screens and documents, system state, and optional shell execution.");
        }

        return string.Join(
            Environment.NewLine,
            "Bootstrap mode is active.",
            "I do not have a general-purpose reasoning model wired in yet.",
            "Use `help` to inspect commands for memory, live computer context, app discovery, desktop automation, browser and file workflows, and system state.",
            "The next major layers are planner access, stronger voice handling, deeper app-specific automation, and a richer HUD.");
    }

    // Reply used when the planner throws, such as a missing API key or unreachable model.
    private string BuildPlannerErrorResponse(Exception exception)
    {
        return string.Join(
            Environment.NewLine,
            $"Planner request failed: {TrimErrorMessage(exception.Message)}",
            "Check the planner settings, model, and API key, or use `help` to fall back to direct commands.");
    }

    // Callback the planner uses to run a tool the model picked. Looks the tool up by name, runs it with the safety check, remembers
    // whether outside content was read, and converts the result into the planner's format.
    private async Task<PlannerToolResult> ExecutePlannerToolAsync(PlannerToolCall toolCall, CancellationToken cancellationToken)
    {
        var tool = _tools.FirstOrDefault(candidate => string.Equals(candidate.Name, toolCall.ApiName, StringComparison.OrdinalIgnoreCase));

        if (tool is null)
        {
            return new PlannerToolResult(
                toolCall.ApiName,
                string.Empty,
                toolCall.Input,
                $"Unsupported internal tool: {toolCall.ApiName}",
                IsError: true,
                Verification: "The requested tool was not registered in the current runtime.",
                Summary: $"Unsupported tool `{toolCall.ApiName}`.");
        }

        var commandText = string.IsNullOrWhiteSpace(toolCall.Input)
            ? tool.Name
            : $"{tool.Name} {toolCall.Input}";

        var normalizedCommand = NormalizeInput(commandText);
        // While the turn has read outside content, any non-read-only action will ask for approval first.
        var mark = _outsideRead.Value;
        var toolResult = await ExecuteToolWithSafetyAsync(tool, normalizedCommand, commandText, cancellationToken, askBeforeActing: mark?.Read == true);

        if (mark is not null && toolResult.Succeeded && ToolSafetyService.ReadsOutsideContent(tool.Name, normalizedCommand))
        {
            mark.Read = true;
        }

        var isError = !toolResult.Succeeded || InferToolError(toolResult.ResponseText);
        var summary = string.IsNullOrWhiteSpace(toolResult.SummaryText)
            ? TrimForSummary(toolResult.ResponseText)
            : toolResult.SummaryText.Trim();
        var verification = string.IsNullOrWhiteSpace(toolResult.VerificationText)
            ? BuildVerificationText(tool.Name, toolCall.Input, toolResult.ResponseText, isError)
            : toolResult.VerificationText.Trim();

        return new PlannerToolResult(
            toolCall.ApiName,
            tool.Name,
            toolCall.Input,
            toolResult.ResponseText,
            isError,
            toolResult.ShouldExit,
            verification,
            summary);
    }

    // The one place where tools are actually run. Safety check first: Block returns a refusal, RequireApproval saves the action
    // and asks the user, Allow runs it. bypassApproval is true when the user has just said yes. Exceptions from a tool become a failure result.
    private async Task<ToolResult> ExecuteToolWithSafetyAsync(
        IAssistantTool tool,
        string normalizedCommand,
        string originalInput,
        CancellationToken cancellationToken,
        bool bypassApproval = false,
        bool askBeforeActing = false)
    {
        var safetyDecision = _toolSafety.Assess(tool.Name, normalizedCommand);

        // Block and RequireApproval stand. An Allow becomes an approval when the turn read outside content and this call acts.
        if (askBeforeActing
            && safetyDecision.Kind == SafetyDecisionKind.Allow
            && !ToolSafetyService.IsReadOnlyCall(tool.Name, normalizedCommand))
        {
            safetyDecision = ToolSafetyService.RequireApprovalAfterOutsideRead(tool.Name, normalizedCommand);
        }

        if (safetyDecision.Kind == SafetyDecisionKind.Block)
        {
            EmitActivity(new AssistantActivityEvent(
                AssistantActivityKind.Warning,
                safetyDecision.SummaryText,
                Detail: safetyDecision.ResponseText));

            return new ToolResult(
                safetyDecision.ResponseText,
                Succeeded: false,
                VerificationText: safetyDecision.VerificationText,
                SummaryText: safetyDecision.SummaryText);
        }

        if (!bypassApproval && safetyDecision.Kind == SafetyDecisionKind.RequireApproval)
        {
            await _pendingApprovalStore.SaveAsync(
                new PendingApprovalAction(
                    tool.Name,
                    normalizedCommand,
                    originalInput,
                    safetyDecision.SummaryText,
                    safetyDecision.ResponseText,
                    DateTimeOffset.UtcNow),
                cancellationToken);

            EmitActivity(new AssistantActivityEvent(
                AssistantActivityKind.Warning,
                safetyDecision.SummaryText,
                Detail: safetyDecision.ResponseText));

            return new ToolResult(
                safetyDecision.ResponseText,
                Succeeded: false,
                VerificationText: safetyDecision.VerificationText,
                SummaryText: safetyDecision.SummaryText);
        }

        try
        {
            return await tool.ExecuteAsync(normalizedCommand, _toolContext, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new ToolResult(
                $"{tool.Name} failed: {TrimErrorMessage(exception.Message)}",
                Succeeded: false,
                VerificationText: $"The `{tool.Name}` tool threw an unhandled exception that was converted into a failure result.",
                SummaryText: $"{tool.Name} failed.");
        }
    }

    // Raises the Activity event for the UI.
    private void EmitActivity(AssistantActivityEvent activity)
    {
        Activity?.Invoke(this, new AssistantActivityEventArgs(activity));
    }

    // Progress object the planner uses to report activity; it forwards to EmitActivity.
    private IProgress<AssistantActivityEvent> CreateActivityProgress()
    {
        return new Progress<AssistantActivityEvent>(EmitActivity);
    }

    // Converts a tool result to the record kept in the turn, with a summary and a verification note.
    private static AssistantToolExecution BuildToolExecution(string toolName, string input, ToolResult result, string fallbackText)
    {
        var failed = !result.Succeeded || InferToolError(result.ResponseText);
        var summary = string.IsNullOrWhiteSpace(result.SummaryText)
            ? TrimForSummary(fallbackText)
            : result.SummaryText.Trim();
        var verification = string.IsNullOrWhiteSpace(result.VerificationText)
            ? BuildVerificationText(toolName, input, result.ResponseText, failed)
            : result.VerificationText.Trim();
        var verificationStatus = BuildVerificationStatus(summary, failed);
        var verificationSummary = BuildVerificationSummary(summary, verification, verificationStatus);

        return new AssistantToolExecution(
            toolName,
            input,
            summary,
            !failed,
            verification,
            result.ResponseText,
            verificationStatus,
            verificationSummary);
    }

    // Same conversion, starting from the planner's result type.
    private static AssistantToolExecution ToToolExecution(PlannerToolResult result)
    {
        var verification = string.IsNullOrWhiteSpace(result.Verification)
            ? BuildVerificationText(result.Command, result.Input, result.Output, result.IsError)
            : result.Verification.Trim();
        var verificationStatus = string.IsNullOrWhiteSpace(result.VerificationStatus)
            ? BuildVerificationStatus(result.Summary, result.IsError)
            : result.VerificationStatus.Trim();
        var verificationSummary = string.IsNullOrWhiteSpace(result.VerificationSummary)
            ? BuildVerificationSummary(result.Summary, verification, verificationStatus)
            : result.VerificationSummary.Trim();

        return new AssistantToolExecution(
            result.Command,
            result.Input,
            string.IsNullOrWhiteSpace(result.Summary) ? TrimForSummary(result.Output) : result.Summary.Trim(),
            !result.IsError,
            verification,
            result.Output,
            verificationStatus,
            verificationSummary);
    }

    // Heuristic: does this tool reply look like a failure? Empty text, or starts with a known error phrase ("usage:", "path not found",
    // "window matching '...' was not found"...). Used because tools return plain text rather than an error flag.
    private static bool InferToolError(string responseText)
    {
        if (string.IsNullOrWhiteSpace(responseText))
        {
            return true;
        }

        var errorPrefixes = new[]
        {
            "usage:",
            "unable ",
            "no matching",
            "no files or folders",
            "shell execution is disabled",
            "command failed",
            "speech recognition unavailable",
            "microphone startup failed"
        };

        // Only the start counts: a reply that reads text back (a clipboard saying "command not found") is not a failure.
        return errorPrefixes.Concat(UiCommandParsing.NotFoundPrefixes).Any(prefix => responseText.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    // Default wording for the verification note when the tool gave none. It only says the tool reported success or failure;
    // it does not prove the effect happened.
    private static string BuildVerificationText(string toolName, string input, string responseText, bool isError)
    {
        if (isError)
        {
            return $"The `{toolName}` tool reported a failure signal for input `{input}`.";
        }

        return $"The `{toolName}` tool returned a success-shaped response for input `{input}`.";
    }

    // "awaiting_approval" if the tool stopped at the approval gate, otherwise "failed" or "observed".
    private static string BuildVerificationStatus(string summary, bool isError)
    {
        if (isError && summary.StartsWith("Approval required", StringComparison.OrdinalIgnoreCase))
        {
            return "awaiting_approval";
        }

        return isError ? "failed" : "observed";
    }

    // Chooses the one-line verification summary shown for a tool result.
    private static string BuildVerificationSummary(string summary, string verificationText, string verificationStatus)
    {
        if (string.Equals(verificationStatus, "awaiting_approval", StringComparison.OrdinalIgnoreCase))
        {
            return "Awaiting user approval before the requested evidence can be confirmed.";
        }

        if (!string.IsNullOrWhiteSpace(verificationText))
        {
            return verificationText.Trim();
        }

        return string.IsNullOrWhiteSpace(summary) ? string.Empty : summary.Trim();
    }

    // Short 160-character version for summaries.
    private static string TrimForSummary(string text) => text.Clip(160);

    // Shortens an error message to 320 characters.
    private static string TrimErrorMessage(string message)
    {
        const int maxLength = 320;
        var value = message.Trim();
        return value.Length <= maxLength ? value : value[..maxLength] + "...";
    }

    // True if the tool stopped for safety (blocked, or approval needed) rather than failing, so it is not retried through the planner.
    private static bool LooksLikeSafetyIntervention(ToolResult result)
    {
        return result.SummaryText.Contains("approval required", StringComparison.OrdinalIgnoreCase)
            || result.SummaryText.Contains("blocked", StringComparison.OrdinalIgnoreCase)
            || result.ResponseText.StartsWith("Approval required", StringComparison.OrdinalIgnoreCase)
            || result.ResponseText.StartsWith("Blocked for safety", StringComparison.OrdinalIgnoreCase);
    }

    // Snapshot of the active app, window, location and recent activity for this turn. Falls back to a smaller record if desktop info fails.
    private async Task<InteractionContextSnapshot> CaptureInteractionContextAsync(
        string effectiveInput,
        IReadOnlyList<AssistantToolExecution> toolResults,
        CancellationToken cancellationToken)
    {
        try
        {
            var desktopContext = await _toolContext.DesktopAutomation.GetContextSnapshotAsync(cancellationToken);
            return MemoryAutomation.BuildInteractionContext(
                desktopContext,
                ResolveLocationContext(),
                BuildRecentActivity(effectiveInput, toolResults));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new InteractionContextSnapshot(
                DateTimeOffset.UtcNow,
                string.Empty,
                ResolveLocationContext(),
                string.Empty,
                string.Empty,
                [],
                BuildRecentActivity(effectiveInput, toolResults).ToArray());
        }
    }

    // Saves what this turn teaches: learned preferences, and an "episodic" note about what happened.
    private async Task LearnFromTurnAsync(
        string effectiveInput,
        IReadOnlyList<AssistantToolExecution> toolResults,
        InteractionContextSnapshot interactionContext,
        CancellationToken cancellationToken)
    {
        foreach (var preference in MemoryAutomation.ExtractLearnedPreferences(effectiveInput, interactionContext))
        {
            await _memoryStore.AddAsync(preference, cancellationToken);
        }

        var episodicMemory = MemoryAutomation.BuildEpisodicMemory(effectiveInput, toolResults, interactionContext);

        if (episodicMemory is not null)
        {
            await _memoryStore.AddAsync(episodicMemory, cancellationToken);
        }
    }

    // Occasionally writes a summary note of the conversation (the memory automation decides when).
    private async Task TryCreateConversationSummaryAsync(CancellationToken cancellationToken)
    {
        var turns = await _transcriptStore.GetAllAsync(cancellationToken);
        var notes = await _memoryStore.GetAllAsync(cancellationToken);
        var summary = MemoryAutomation.BuildConversationSummary(turns, notes);

        if (summary is not null)
        {
            await _memoryStore.AddAsync(summary, cancellationToken);
        }
    }

    // Context used to rank memories: query text, time of day, location, active app and window.
    private MemoryQueryContext BuildMemoryQueryContext(string query, DesktopContextSnapshot desktopContext)
    {
        var interactionContext = MemoryAutomation.BuildInteractionContext(
            desktopContext,
            ResolveLocationContext(),
            [query]);

        return new MemoryQueryContext(
            query,
            interactionContext.TimeOfDay,
            interactionContext.Location,
            interactionContext.ActiveApp,
            interactionContext.ActiveWindow,
            interactionContext.SafeRecentActivity);
    }

    // Short list of what just happened (the request and up to 3 successful tool summaries).
    private IEnumerable<string> BuildRecentActivity(string userInput, IReadOnlyList<AssistantToolExecution> toolResults)
    {
        if (!string.IsNullOrWhiteSpace(userInput))
        {
            yield return TrimForSummary(userInput);
        }

        foreach (var toolResult in toolResults.Where(result => result.Succeeded).Take(3))
        {
            yield return $"{toolResult.ToolName}: {TrimForSummary(toolResult.Summary)}";
        }
    }

    // The default weather location doubles as the user's location context.
    private string ResolveLocationContext()
    {
        return string.IsNullOrWhiteSpace(Options.DefaultWeatherLocation)
            ? string.Empty
            : Options.DefaultWeatherLocation.Trim();
    }

    // "remember ..." or "note ..." commands.
    private static bool LooksLikeRememberCommand(string normalizedInput)
    {
        return normalizedInput.StartsWith("remember ", StringComparison.OrdinalIgnoreCase)
            || normalizedInput.StartsWith("note ", StringComparison.OrdinalIgnoreCase);
    }

    // Describes a tool to the model: its name, what it does and what to put in its input. Written by hand because the model sees
    // only these sentences when choosing a tool. Tools not listed fall back to their own description.
    private static PlannerToolDefinition BuildPlannerToolDefinition(IAssistantTool tool)
    {
        return tool.Name switch
        {
            "computer" => new PlannerToolDefinition(
                tool.Name,
                tool.Name,
                "Read live desktop context including the active window, visible apps, workspace entries, and system state. This is read-only.",
                "Leave empty."),
            "research" => new PlannerToolDefinition(
                tool.Name,
                tool.Name,
                "Collect cited public-web sources for open-domain questions. Prefer this over browser search when the user needs sourced information.",
                "Open-domain research query only."),
            "screen" => new PlannerToolDefinition(
                tool.Name,
                tool.Name,
                "Analyze the current screen, capture a screenshot, extract screen text, or detect UI elements.",
                "Optional target or question. Examples: `capture`, `ocr browser`, `ui settings`, or a question about the visible screen."),
            "analyze image" => new PlannerToolDefinition(
                tool.Name,
                tool.Name,
                "Analyze an image file. This is for existing local image files.",
                "Format: `<path>` or `<path> :: <question>`."),
            "analyze document" => new PlannerToolDefinition(
                tool.Name,
                tool.Name,
                "Analyze a text or image-based document file.",
                "Format: `<path>` or `<path> :: <question>`."),
            "open app" => new PlannerToolDefinition(
                tool.Name,
                tool.Name,
                "Focus a running app or launch an application. If nothing is installed by that name but a file is, the file opens (several matches are listed).",
                "Application name, or the file name the user asked to open."),
            "open path" => new PlannerToolDefinition(
                tool.Name,
                tool.Name,
                "Open a file or folder path.",
                "File or folder path only."),
            "list files" => new PlannerToolDefinition(
                tool.Name,
                tool.Name,
                "List files in a directory.",
                "Directory path only."),
            "read file" => new PlannerToolDefinition(
                tool.Name,
                tool.Name,
                "Preview a text file from disk.",
                "File path only."),
            "find files" => new PlannerToolDefinition(
                tool.Name,
                tool.Name,
                "Search the user's Desktop, OneDrive Desktop, Documents and Downloads by file or folder name, newest first.",
                "Words from the file or folder name only, for example `report 4`."),
            "write file" => new PlannerToolDefinition(
                tool.Name,
                tool.Name,
                "Create or overwrite a text file on disk. Approval may be required.",
                "Format: `<path> :: <content>`."),
            "append file" => new PlannerToolDefinition(
                tool.Name,
                tool.Name,
                "Append text to an existing text file or create it if needed. Approval may be required.",
                "Format: `<path> :: <content>`."),
            "replace in file" => new PlannerToolDefinition(
                tool.Name,
                tool.Name,
                "Replace one text fragment with another inside a text file. Approval may be required.",
                "Format: `<path> :: <find> :: <replace>`."),
            "move path" => new PlannerToolDefinition(
                tool.Name,
                tool.Name,
                "Move a file or folder. Approval may be required.",
                "Format: `<source> :: <destination>`."),
            "copy path" => new PlannerToolDefinition(
                tool.Name,
                tool.Name,
                "Copy a file or folder. Approval may be required.",
                "Format: `<source> :: <destination>`."),
            "delete path" => new PlannerToolDefinition(
                tool.Name,
                tool.Name,
                "Delete a file or folder. Approval may be required.",
                "Target path only."),
            "browse" => new PlannerToolDefinition(
                tool.Name,
                tool.Name,
                "Open a site in the user's browser. Site names work: teams, discord, linkedin, linkedin messages, notion, canvas, github, figma.",
                "URL or site name only, for example `discord` or `linkedin messages`."),
            "search web" => new PlannerToolDefinition(
                tool.Name,
                tool.Name,
                "Launch a browser search without collecting citations.",
                "Search query only."),
            "spotify" => new PlannerToolDefinition(
                tool.Name,
                tool.Name,
                "Spotify adapter for focus, media transport, and search.",
                "Spotify action only."),
            "code" => new PlannerToolDefinition(
                tool.Name,
                tool.Name,
                "VS Code adapter for opening files/folders, goto targets, and in-app search.",
                "Code action only."),
            "chrome" => new PlannerToolDefinition(
                tool.Name,
                tool.Name,
                "Chrome adapter for navigation, tabs, history/downloads/bookmarks, find-on-page, and form/login workflows specifically in Chrome.",
                "Chrome action only."),
            "explorer" => new PlannerToolDefinition(
                tool.Name,
                tool.Name,
                "Explorer adapter for opening folders and revealing files.",
                "Explorer action only."),
            "office" => new PlannerToolDefinition(
                tool.Name,
                tool.Name,
                "Office adapter for Word, Excel, PowerPoint, and Outlook launch/status actions plus opening files directly in Office apps.",
                "Office action only."),
            "discord" => new PlannerToolDefinition(
                tool.Name,
                tool.Name,
                "Discord adapter for focus, status, quick-jump, and in-app search.",
                "Discord action only."),
            "slack" => new PlannerToolDefinition(
                tool.Name,
                tool.Name,
                "Slack adapter for focus, status, and quick-jump routing.",
                "Slack action only."),
            "windows" => new PlannerToolDefinition(
                tool.Name,
                tool.Name,
                "List visible top-level desktop windows.",
                "Optional filter only."),
            "app state" => new PlannerToolDefinition(
                tool.Name,
                tool.Name,
                "Inspect richer state for a specific application, including visibility, process details, and launch metadata.",
                "Application name only."),
            "focus app" => new PlannerToolDefinition(
                tool.Name,
                tool.Name,
                "Bring a running app window to the foreground.",
                "Window target only."),
            "close app" => new PlannerToolDefinition(
                tool.Name,
                tool.Name,
                "Send a close request to a running window.",
                "Window target only."),
            "window state" => new PlannerToolDefinition(
                tool.Name,
                tool.Name,
                "Minimize, maximize, or restore a window.",
                "Target window plus state payload."),
            "move window" => new PlannerToolDefinition(
                tool.Name,
                tool.Name,
                "Move a visible window to explicit screen coordinates.",
                "Target window and X/Y coordinates."),
            "resize window" => new PlannerToolDefinition(
                tool.Name,
                tool.Name,
                "Resize a visible window to the requested dimensions.",
                "Target window and width/height payload."),
            "snap window" => new PlannerToolDefinition(
                tool.Name,
                tool.Name,
                "Snap a visible window into a desktop region such as left, right, or top-right.",
                "Target window and snap position."),
            "send hotkey" => new PlannerToolDefinition(
                tool.Name,
                tool.Name,
                "Send a keyboard shortcut to a target window. Approval may be required.",
                "Target window and hotkey payload."),
            "click element" => new PlannerToolDefinition(
                tool.Name,
                tool.Name,
                "Click a named or indexed UI element in the active app.",
                "UI target only."),
            "type into" => new PlannerToolDefinition(
                tool.Name,
                tool.Name,
                "Type text into an app window at its cursor, opening the app first if it is not running. Use this to write, note, or type anything into Notepad, Word, or another app.",
                "Format: `<app> <text>`, e.g. `notepad Hello World`. Quote an app name with spaces: `\"sticky notes\" Buy milk`."),
            "calendar" => new PlannerToolDefinition(
                tool.Name,
                tool.Name,
                "Read or add events in the user's Google Calendar. Use this for schedules, events, appointments, and what is on a given day.",
                "To read a day: `today`, `tomorrow`, `friday`, or a date. To add: `add <title> :: <date> :: <time> :: <color>`, e.g. `add Dentist :: tomorrow :: 3pm :: red` (time and color optional; colors: red, orange, yellow, green, blue, purple, pink, teal, gray, lavender, sage)."),
            "lecture notes" => new PlannerToolDefinition(
                tool.Name,
                tool.Name,
                "Record a lecture, class, or meeting (microphone and computer audio) and turn it into notes with a summary, key points, due dates, and a clickable transcript.",
                "`start <course or meeting name>` (name optional) or `stop`."),
            "vpn" => new PlannerToolDefinition(
                tool.Name,
                tool.Name,
                "Turn the user's NordVPN on or off, or say whether it is on.",
                "`on`, `off`, or `status`."),
            "page" => new PlannerToolDefinition(
                tool.Name,
                tool.Name,
                "Read aloud or summarize the web page open in the user's browser. Use this whenever the user refers to the page, article, site, or tab they are on.",
                "`read` to read its main text aloud (article only, no menus or ads), `summarize` for a summary, or `summarize <focus>` such as `summarize the reviews`."),
            "get element text" => new PlannerToolDefinition(
                tool.Name,
                tool.Name,
                "Read text from a named UI element.",
                "UI target only."),
            "scroll" => new PlannerToolDefinition(
                tool.Name,
                tool.Name,
                "Scroll the active window or target area.",
                "Direction or target payload only."),
            "media" => new PlannerToolDefinition(
                tool.Name,
                tool.Name,
                "Send system media controls such as play/pause, next track, or volume adjustments.",
                "Media action only."),
            "clipboard" => new PlannerToolDefinition(
                tool.Name,
                tool.Name,
                "Read, set or clear the clipboard, or paste it into a target window.",
                "Clipboard subcommand payload only."),
            "notify" => new PlannerToolDefinition(
                tool.Name,
                tool.Name,
                "Send a Windows toast notification.",
                "Format: `<title> :: <message>`."),
            "wait" => new PlannerToolDefinition(
                tool.Name,
                tool.Name,
                "Pause briefly to let UI state settle.",
                "Duration payload only when needed."),
            "run" => new PlannerToolDefinition(
                tool.Name,
                tool.Name,
                "Run a PowerShell command when needed. Read-only commands run directly; major changes may require approval.",
                "PowerShell command only."),
            _ => new PlannerToolDefinition(tool.Name, tool.Name, tool.Description)
        };
    }

    // Internal bundle of what a handling route produced: the result, route, session id, whether the reply is a question, tool results and sources.
    private sealed record PlanningOutcome(
        ToolResult Result,
        AgentIntentKind Intent,
        string SessionId,
        bool IsFollowUpQuestion,
        AssistantToolExecution[] ToolResults,
        AssistantCitation[] Citations);
}
