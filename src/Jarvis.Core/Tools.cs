// Tools.cs: the built-in tools Jarvis can run, plus the small types every tool shares.
// A tool is a class implementing IAssistantTool: a Name (e.g. "weather"), a Description, CanHandle(input) (is this text meant for it?)
// and ExecuteAsync (does the work, returns a ToolResult). Tools are put in one list in the JarvisAssistant constructor; ORDER MATTERS:
// the first tool whose CanHandle returns true wins.
// The planner (AssistantPlanner) turns each Name and Description into a tool definition for the local model with one text argument,
// input, so the Description is the model's instruction manual. A call is the text "<name> <input>"; ToolSafetyService (ActionSafety.cs)
// decides allow / ask / block before ExecuteAsync runs. Tools reach the desktop, memory, web and screen through ToolContext.
// This file holds the core tools and ToolInputHelper; the rest live in siblings such as AppIntegrationTools.cs and AutomationWorkflowTools.cs.
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace Jarvis.Core;

// Everything a tool may need while it runs: settings, memory and transcript stores, desktop automation, weather, web research and screen
// understanding services, the workspace folder, and whether the planner (the LLM) is available. One is built per assistant and passed to every tool.
public sealed class ToolContext
{
    public ToolContext(
        JarvisOptions options,
        JsonMemoryStore memoryStore,
        JsonLineTranscriptStore transcriptStore,
        IDesktopAutomationService desktopAutomation,
        IWeatherService weatherService,
        IWebResearchService webResearchService,
        IVisualContextService visualContextService,
        string workspaceRoot,
        bool plannerAvailable,
        string plannerStatus)
    {
        Options = options;
        MemoryStore = memoryStore;
        TranscriptStore = transcriptStore;
        DesktopAutomation = desktopAutomation;
        WeatherService = weatherService;
        WebResearchService = webResearchService;
        VisualContextService = visualContextService;
        WorkspaceRoot = workspaceRoot;
        PlannerAvailable = plannerAvailable;
        PlannerStatus = plannerStatus;
    }

    public JarvisOptions Options { get; }

    public JsonMemoryStore MemoryStore { get; }

    public JsonLineTranscriptStore TranscriptStore { get; }

    public IDesktopAutomationService DesktopAutomation { get; }

    public IWeatherService WeatherService { get; }

    public IWebResearchService WebResearchService { get; }

    public IVisualContextService VisualContextService { get; }

    public string WorkspaceRoot { get; }

    public bool PlannerAvailable { get; }

    public string PlannerStatus { get; }

    // The last N finished turns of this session, oldest first; the request being handled is not in it yet.
    public Func<int, IReadOnlyList<ConversationStateEntry>> RecentTurns { get; init; } = _ => [];
}

// The outcome of running a tool. ResponseText is what the user sees and hears; the other fields help the planner and the safety layer.
/// <param name="FollowUpCommand">
/// Set when the reply asks for the rest of a command ("What should I write down?"). The user's next
/// input is appended to this command instead of being handled on its own.
/// </param>
/// <param name="SpeakInFull">
/// The reply is meant to be heard however long it is (an article, a day's schedule). Otherwise a long
/// reply is cut to its opening sentences (see <see cref="SpokenReply"/>), so listings stay on screen.
/// </param>
public sealed record ToolResult(
    string ResponseText,
    bool ShouldExit = false,
    bool Succeeded = true,
    string VerificationText = "",
    string SummaryText = "",
    string FollowUpCommand = "",
    bool SpeakInFull = false,
    // A failure the planner cannot fix (the tool was the right one and got as far as acting). Skips the cheap-route fallback.
    bool SkipReplan = false);

// The contract every tool follows. See the header of this file for how tools are registered, offered to the model and run.
public interface IAssistantTool
{
    string Name { get; }

    string Description { get; }

    bool CanHandle(string input);

    Task<ToolResult> ExecuteAsync(string input, ToolContext context, CancellationToken cancellationToken);
}

// Lists every registered tool with its Description. Gets the tool list through a function because the list is still being built when this tool is created.
public sealed class HelpTool : IAssistantTool
{
    private readonly Func<IReadOnlyList<IAssistantTool>> _getTools;

    public HelpTool(Func<IReadOnlyList<IAssistantTool>> getTools)
    {
        _getTools = getTools;
    }

    public string Name => "help";

    public string Description => "Show the available directives.";

    public bool CanHandle(string input) =>
        input.Equals("help", StringComparison.OrdinalIgnoreCase) ||
        input.Equals("commands", StringComparison.OrdinalIgnoreCase);

    public Task<ToolResult> ExecuteAsync(string input, ToolContext context, CancellationToken cancellationToken)
    {
        var lines = _getTools()
            .Select(tool => $"- {tool.Name}: {tool.Description}")
            .ToList();

        lines.Insert(0, "Available directives:");
        lines.Add("- voice corrections: Show learned speech-correction pairs plus recent rejected transcripts.");
        lines.Add("- I said <corrected>, not <heard>: Teach Jarvis how to correct a recurring transcription mistake.");

        return Task.FromResult(new ToolResult(string.Join(Environment.NewLine, lines)));
    }
}

// Prints a summary of the current settings and capabilities (voice, speech, safety, planner, memory counts).
public sealed class StatusTool : IAssistantTool
{
    public string Name => "status";

    public string Description => "Report the current runtime mode and enabled capabilities.";

    public bool CanHandle(string input) =>
        input.Equals("status", StringComparison.OrdinalIgnoreCase) ||
        input.Equals("capabilities", StringComparison.OrdinalIgnoreCase) ||
        input.Equals("what can you do", StringComparison.OrdinalIgnoreCase);

    public async Task<ToolResult> ExecuteAsync(string input, ToolContext context, CancellationToken cancellationToken)
    {
        var memoryCount = await context.MemoryStore.CountAsync(cancellationToken);
        var transcriptCount = await context.TranscriptStore.CountAsync(cancellationToken);
        var profile = await context.MemoryStore.GetProfileAsync(cancellationToken);
        var preferredAddress = AssistantPersonaConfiguration.ResolvePreferredUserAddress(context.Options, profile);
        var pronunciationHints = AssistantPersonaConfiguration.ResolvePronunciationHints(context.Options);

        var response = string.Join(
            Environment.NewLine,
            $"Mode: {(context.PlannerAvailable ? "agent runtime" : "bootstrap shell")}",
            $"Assistant: {context.Options.AssistantName}",
            $"Assistant persona: {AssistantPersonaConfiguration.BuildStatusSummary(context.Options)}",
            $"Presence preset: {AssistantPersonaConfiguration.ResolvePresencePreset(context.Options)}",
            $"Response style: {AssistantPersonaConfiguration.ResolveResponseStyle(context.Options)}",
            $"Small talk: {(context.Options.AssistantSmallTalkEnabled ? "brief and allowed when invited" : "minimal by default")}",
            $"Preferred user address: {(string.IsNullOrWhiteSpace(preferredAddress) ? "not set" : preferredAddress)}",
            $"Pronunciation hints: {(pronunciationHints.Count == 0 ? "none" : string.Join(" | ", pronunciationHints.Take(3)))}",
            $"Wake phrase: {context.Options.WakePhrase}",
            $"Voice output: {(context.Options.VoiceEnabled ? "enabled" : "disabled")}",
            $"Speech recognition: {(context.Options.SpeechRecognitionEnabled ? "enabled" : "disabled")}",
            $"Speech provider: {context.Options.SpeechRecognitionProvider}",
            $"Wake word gate: {(context.Options.WakeWordEnabled ? "enabled" : "disabled")}",
            "Desktop automation: enabled for app discovery, visible-window discovery, window management, browser and form workflows, file create/edit/move/copy/delete actions, clipboard read and set, notifications, media controls, Win32 UI control, local network discovery, live computer context, system state, and app adapters for Spotify, VS Code, Office, Chrome, Explorer, Discord, and Slack",
            $"Visual grounding: screen and window capture, monitor inventory, OCR-style text extraction, structured UI analysis, and document parsing for text/image inputs ({(string.IsNullOrWhiteSpace(context.Options.PlannerVisionModel) ? "planner vision route inherits the default model" : context.Options.PlannerVisionModel)})",
            $"Shell execution: {(context.Options.ShellExecutionEnabled ? "enabled" : "disabled")}",
            $"Action safety: {(context.Options.SafetyRequireApprovalForMajorActions ? "approval required for shell changes" : "approval disabled")} | {(context.Options.SafetyBlockProtectedSystemPaths ? "protected system paths blocked" : "protected system path blocking disabled")}",
            $"Local AI mode: {(context.Options.LocalAiOnly ? "this computer only; hosted model, voice, and transcription routes blocked" : "remote AI providers permitted")}",
            $"Background workflow limit: {Math.Clamp(context.Options.PlannerMaxConcurrentBackgroundTasks, 1, 4)}",
            $"Planner: {context.PlannerStatus}",
            $"Natural language understanding: {(context.PlannerAvailable ? "multi-step, structured tool-calling, verification-aware, and context-aware" : "command-driven only")}",
            $"Web research: {(context.Options.PlannerWebResearchEnabled ? "enabled" : "disabled")}",
            "Additional context: short-term session state, recent turns, recent tool results, learned preferences, local user state, reminders, and optional visual understanding",
            "Privacy: `remember private <note>` keeps a note off disk, `off the record` skips logging and memory capture for that turn, and passwords, keys and card numbers are never saved automatically",
            $"Default weather location: {(string.IsNullOrWhiteSpace(context.Options.DefaultWeatherLocation) ? "not set" : context.Options.DefaultWeatherLocation)}",
            $"Stored memories: {memoryCount} | learned preferences: {profile.PreferenceCount} | summaries: {profile.SummaryCount} | structured: {profile.StructuredMemoryCount} | pending reminders: {profile.PendingReminderCount}",
            $"Transcript turns: {transcriptCount}",
            context.PlannerAvailable
                ? "Remaining gaps for movie-style behavior: stronger email/task integrations, deeper app-state automation, and a richer HUD."
                : "Missing layers for movie-style behavior: a stronger streaming voice pipeline, LLM planning, deeper desktop automation, and a richer HUD.");

        return new ToolResult(response);
    }
}

// Says the current local date and time.
public sealed class TimeTool : IAssistantTool
{
    public string Name => "time";

    public string Description => "Report the current local date and time.";

    public bool CanHandle(string input) =>
        input.Equals("time", StringComparison.OrdinalIgnoreCase) ||
        input.Equals("date", StringComparison.OrdinalIgnoreCase) ||
        input.Equals("what time is it", StringComparison.OrdinalIgnoreCase);

    public Task<ToolResult> ExecuteAsync(string input, ToolContext context, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.Now;
        var response = $"Local time is {now:dddd, MMMM d, yyyy h:mm tt zzz}.";
        return Task.FromResult(new ToolResult(response));
    }
}

// Saves a note to memory. A "private" note is acknowledged but never written to disk. Text that looks like a password or key is refused.
public sealed class RememberTool : IAssistantTool
{
    public string Name => "remember";

    public string Description => "Store a note with `remember <note>`. Use `remember private <note>` to keep it off disk.";

    public bool CanHandle(string input) =>
        input.StartsWith("remember ", StringComparison.OrdinalIgnoreCase) ||
        input.StartsWith("note ", StringComparison.OrdinalIgnoreCase);

    public async Task<ToolResult> ExecuteAsync(string input, ToolContext context, CancellationToken cancellationToken)
    {
        var content = input.StartsWith("note ", StringComparison.OrdinalIgnoreCase)
            ? input["note ".Length..].Trim()
            : input["remember ".Length..].Trim();

        if (string.IsNullOrWhiteSpace(content))
        {
            return new ToolResult("Usage: remember <note>");
        }

        // Notes starting with private, privately or ephemeral are acknowledged but not stored.
        var isPrivate = content.StartsWith("private ", StringComparison.OrdinalIgnoreCase)
            || content.StartsWith("privately ", StringComparison.OrdinalIgnoreCase)
            || content.StartsWith("ephemeral ", StringComparison.OrdinalIgnoreCase);

        if (isPrivate)
        {
            var privateContent = content
                .Replace("private ", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("privately ", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("ephemeral ", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Trim();

            return string.IsNullOrWhiteSpace(privateContent)
                ? new ToolResult("Usage: remember private <note>")
                : new ToolResult(
                    "Noted privately. I did not persist that memory.",
                    VerificationText: "The remember command was explicitly marked private.",
                    SummaryText: "Private memory was not persisted.");
        }

        // An optional prefix (forever, long term, short term) sets how long the note is kept; saying it counts as the user approving that retention.
        var retentionPolicy = string.Empty;
        var userApprovedRetention = false;

        foreach (var prefix in new[] { "forever ", "long-term ", "long term ", "short-term ", "short term " })
        {
            if (!content.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            retentionPolicy = prefix.StartsWith("short", StringComparison.OrdinalIgnoreCase)
                ? "short"
                : prefix.StartsWith("forever", StringComparison.OrdinalIgnoreCase)
                    ? "forever"
                    : "long";
            content = content[prefix.Length..].Trim();
            userApprovedRetention = true;
            break;
        }

        // Refuse to store anything that looks like a password, key or card number.
        if (MemoryAutomation.LooksSensitive(content))
        {
            return new ToolResult(
                "That looks sensitive, so I did not persist it. Use an off-the-record request instead if you still want help with it.",
                Succeeded: false,
                VerificationText: "Sensitive content was detected and blocked from persistence.",
                SummaryText: "Blocked persistence for sensitive memory content.");
        }

        var memoryContext = await ToolInputHelper.BuildInteractionContextAsync(context, "manual remember", cancellationToken);
        await context.MemoryStore.AddAsync(
            new MemoryWriteRequest(
                content,
                Kind: "note",
                Category: string.Empty,
                Source: "manual",
                Tags: ["manual", "memory"],
                Context: memoryContext,
                RetentionPolicy: retentionPolicy,
                UserApprovedRetention: userApprovedRetention),
            cancellationToken);

        var retentionSummary = string.IsNullOrWhiteSpace(retentionPolicy)
            ? string.Empty
            : $" with {retentionPolicy} retention";
        return new ToolResult(
            $"Stored memory{retentionSummary}: {content}",
            VerificationText: "The memory note was written to the local store.",
            SummaryText: "Stored one memory note.");
    }
}

// Searches saved memory for a phrase and shows the top five matches.
public sealed class RecallTool : IAssistantTool
{
    public string Name => "recall";

    public string Description => "Search memory semantically with `recall <term>` or `what do you remember about <term>`.";

    public bool CanHandle(string input) =>
        input.StartsWith("recall", StringComparison.OrdinalIgnoreCase) ||
        input.StartsWith("what do you remember about", StringComparison.OrdinalIgnoreCase);

    public async Task<ToolResult> ExecuteAsync(string input, ToolContext context, CancellationToken cancellationToken)
    {
        var query = input.StartsWith("what do you remember about", StringComparison.OrdinalIgnoreCase)
            ? input["what do you remember about".Length..].Trim()
            : input["recall".Length..].Trim();

        if (string.IsNullOrWhiteSpace(query))
        {
            return new ToolResult("Usage: recall <term>");
        }

        var queryContext = await ToolInputHelper.BuildMemoryQueryContextAsync(context, query, cancellationToken);
        var notes = await context.MemoryStore.SearchAsync(query, queryContext, 5, cancellationToken);

        if (notes.Count == 0)
        {
            return new ToolResult($"No relevant memory matches found for \"{query}\".");
        }

        var lines = notes.Select(ToolInputHelper.RenderMemoryLine);
        return new ToolResult("Relevant memories:" + Environment.NewLine + string.Join(Environment.NewLine, lines));
    }
}

// Shows the most recent memories and upcoming reminders.
public sealed class ListMemoryTool : IAssistantTool
{
    public string Name => "notes";

    public string Description => "List recent memories plus upcoming reminders.";

    public bool CanHandle(string input) =>
        input.Equals("notes", StringComparison.OrdinalIgnoreCase) ||
        input.Equals("memories", StringComparison.OrdinalIgnoreCase);

    public async Task<ToolResult> ExecuteAsync(string input, ToolContext context, CancellationToken cancellationToken)
    {
        var notes = await context.MemoryStore.GetRecentAsync(8, cancellationToken);
        var reminders = await context.MemoryStore.GetPendingRemindersAsync(4, cancellationToken);

        if (notes.Count == 0 && reminders.Count == 0)
        {
            return new ToolResult("No memories stored yet.");
        }

        var sections = new List<string>();

        if (reminders.Count > 0)
        {
            sections.Add("Upcoming reminders:");
            sections.AddRange(reminders.Select(ToolInputHelper.RenderReminderLine));
        }

        if (notes.Count > 0)
        {
            if (sections.Count > 0)
            {
                sections.Add(string.Empty);
            }

            sections.Add("Recent memories:");
            sections.AddRange(notes.Select(ToolInputHelper.RenderMemoryLine));
        }

        return new ToolResult(string.Join(Environment.NewLine, sections));
    }
}

// Creates a reminder or lists pending ones. If the request lacks a time or a task, it asks for only the missing part.
public sealed class ReminderTool : IAssistantTool
{
    public string Name => "reminders";

    public string Description => "Create or list reminder memories with `remind me to ... at ...` or `reminders`.";

    public bool CanHandle(string input) =>
        input.Equals("reminders", StringComparison.OrdinalIgnoreCase)
        || input.Equals("show reminders", StringComparison.OrdinalIgnoreCase)
        || input.Equals("list reminders", StringComparison.OrdinalIgnoreCase)
        || input.StartsWith("reminders ", StringComparison.OrdinalIgnoreCase)
        || MemoryAutomation.IsReminderRequest(input);

    public async Task<ToolResult> ExecuteAsync(string input, ToolContext context, CancellationToken cancellationToken)
    {
        if (input.Equals("reminders", StringComparison.OrdinalIgnoreCase)
            || input.Equals("show reminders", StringComparison.OrdinalIgnoreCase)
            || input.Equals("list reminders", StringComparison.OrdinalIgnoreCase))
        {
            var reminders = await context.MemoryStore.GetPendingRemindersAsync(8, cancellationToken);

            if (reminders.Count == 0)
            {
                return new ToolResult("No pending reminders.");
            }

            return new ToolResult("Pending reminders:" + Environment.NewLine + string.Join(Environment.NewLine, reminders.Select(ToolInputHelper.RenderReminderLine)));
        }

        var reminderInput = input.StartsWith("reminders ", StringComparison.OrdinalIgnoreCase)
            ? "remind me to " + input["reminders ".Length..].Trim()
            : input;
        if (!MemoryAutomation.TryParseReminder(reminderInput, DateTimeOffset.Now, out var reminderText, out var reminderAt))
        {
            // Asks only for the missing part. The answer is appended to the follow-up command, so a short
            // reply ("in 10 minutes", "take my medication") completes the reminder.
            var (question, followUp) = (reminderText.Length > 0, reminderAt != default) switch
            {
                (true, false) => ("When should I remind you? For example, in 10 minutes or at 5.", $"remind me to {reminderText}"),
                (true, true) => ("That time has already passed. When should I remind you?", $"remind me to {reminderText}"),
                (false, true) => ("What should I remind you about?", reminderInput),
                _ => ("What should I remind you about, and when?", "remind me")
            };

            return new ToolResult(
                question,
                Succeeded: false,
                VerificationText: "The reminder request is missing a task or a time.",
                SummaryText: "Asked for the rest of the reminder.",
                FollowUpCommand: followUp);
        }

        var memoryContext = await ToolInputHelper.BuildInteractionContextAsync(context, "manual reminder", cancellationToken);
        var reminder = MemoryAutomation.BuildReminderMemory(reminderText, reminderAt, memoryContext, DateTimeOffset.Now);
        await context.MemoryStore.AddAsync(reminder, cancellationToken);
        return new ToolResult(
            $"Okay, I'll remind you {CalendarText.DescribeMoment(reminderAt.LocalDateTime, DateOnly.FromDateTime(DateTime.Now))}: {reminder.ReminderText}.",
            VerificationText: "The reminder memory was written to the local store.",
            SummaryText: "Stored one reminder.");
    }
}

// Forgets a memory chosen by id prefix or by search. If several memories match, it lists them instead of guessing.
public sealed class ForgetMemoryTool : IAssistantTool
{
    public string Name => "forget";

    public string Description => "Forget a stored memory with `forget <id-or-phrase>`.";

    public bool CanHandle(string input) =>
        input.StartsWith("forget ", StringComparison.OrdinalIgnoreCase)
        || input.StartsWith("delete memory ", StringComparison.OrdinalIgnoreCase);

    public async Task<ToolResult> ExecuteAsync(string input, ToolContext context, CancellationToken cancellationToken)
    {
        var target = ToolInputHelper.GetCommandTail(input, "forget", "delete memory");

        if (string.IsNullOrWhiteSpace(target))
        {
            return new ToolResult("Usage: forget <id-or-phrase>");
        }

        // First try an id match (full id or a prefix of it); only if that fails, search by phrase.
        var notes = await context.MemoryStore.GetAllAsync(cancellationToken);
        var directMatch = notes.FirstOrDefault(note =>
            string.Equals(note.Id, target, StringComparison.OrdinalIgnoreCase)
            || note.Id.StartsWith(target, StringComparison.OrdinalIgnoreCase));

        var match = directMatch;

        if (match is null)
        {
            var searchContext = await ToolInputHelper.BuildMemoryQueryContextAsync(context, target, cancellationToken);
            var searched = await context.MemoryStore.SearchAsync(target, searchContext, 3, cancellationToken);

            if (searched.Count == 0)
            {
                return new ToolResult($"No stored memory matched \"{target}\".");
            }

            if (searched.Count > 1
                && !string.Equals(searched[0].Content, target, StringComparison.OrdinalIgnoreCase))
            {
                var lines = searched.Select(ToolInputHelper.RenderMemoryLine);
                return new ToolResult(
                    "More than one memory matched. Use a longer phrase or the memory id prefix:" + Environment.NewLine + string.Join(Environment.NewLine, lines),
                    Succeeded: false,
                    VerificationText: "The forget request was ambiguous.",
                    SummaryText: "Memory forget request was ambiguous.");
            }

            match = searched[0];
        }

        var forgot = await context.MemoryStore.ForgetAsync(match.Id, "user request", cancellationToken);

        return forgot
            ? new ToolResult(
                $"Forgot memory {match.Id[..8]}: {match.Content}",
                VerificationText: "The selected memory was marked forgotten.",
                SummaryText: "Forgot one memory.")
            : new ToolResult(
                $"Unable to forget memory {match.Id[..8]}.",
                Succeeded: false,
                VerificationText: "The selected memory could not be updated.",
                SummaryText: "Memory forget failed.");
    }
}

// Shows what Jarvis has learned about the user: preferences, routines, structured memories and pending reminders.
public sealed class MemoryProfileTool : IAssistantTool
{
    public string Name => "preferences";

    public string Description => "Show learned preferences, habits, routines, structured memories, and pending reminders.";

    public bool CanHandle(string input) =>
        input.Equals("preferences", StringComparison.OrdinalIgnoreCase)
        || input.Equals("profile", StringComparison.OrdinalIgnoreCase)
        || input.Equals("memory profile", StringComparison.OrdinalIgnoreCase)
        || input.Equals("user profile", StringComparison.OrdinalIgnoreCase);

    public async Task<ToolResult> ExecuteAsync(string input, ToolContext context, CancellationToken cancellationToken)
    {
        var profile = await context.MemoryStore.GetProfileAsync(cancellationToken);
        return new ToolResult(profile.ToDisplayText());
    }
}

// Reports current weather for a place, or for the default location from settings.
public sealed class WeatherTool : IAssistantTool
{
    public string Name => "weather";

    public string Description => "Check current weather with `weather <location>` or use `defaultWeatherLocation` in settings.";

    public bool CanHandle(string input) =>
        input.Equals("weather", StringComparison.OrdinalIgnoreCase) ||
        input.StartsWith("weather ", StringComparison.OrdinalIgnoreCase);

    public async Task<ToolResult> ExecuteAsync(string input, ToolContext context, CancellationToken cancellationToken)
    {
        var target = ToolInputHelper.GetCommandTail(input, "weather");

        if (string.IsNullOrWhiteSpace(target))
        {
            target = context.Options.DefaultWeatherLocation;
        }

        return new ToolResult(await context.WeatherService.GetCurrentWeatherAsync(target, cancellationToken));
    }
}

// Makes a Sticky Notes note from dictated words. It can also save "that" (the last reply) or "our conversation" as a note.
/// <param name="modelGateway">Condenses rambling dictation into a short note; without it the words are only tidied.</param>
public sealed class WriteDownTool(IModelGateway? modelGateway = null) : IAssistantTool
{
    public string Name => "write down";

    public string Description =>
        "Make a note: put text on a new Sticky Note. Use this for any request to make, take, or write a note, or to jot or write something down. "
        + "`write down <what the note should say>`; long dictation is shortened automatically.";

    // Sticky Notes (the OneNote one) has no API. Its "New note" button only responds to keyboard focus, so
    // the script focuses it, presses Enter, and types only once the new note's editor holds the focus.
    // Jarvis.Core targets plain net8.0, which has no UI Automation, hence Windows PowerShell.
    private const string StickyNoteScript = """
        $ProgressPreference = 'SilentlyContinue'
        Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
        $A = [System.Windows.Automation.AutomationElement]
        $Scope = [System.Windows.Automation.TreeScope]
        function Find($parent, $scope, $name) { $parent.FindFirst($scope, (New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, $name))) }
        function Press($element, $name) {
            $element.SetFocus()
            Start-Sleep -Milliseconds 200
            if ($A::FocusedElement.Current.Name -ne $name) { "Could not focus the $name button."; exit 1 }
            $shell.SendKeys('{ENTER}')
        }
        $shell = New-Object -ComObject WScript.Shell
        Start-Process 'shell:AppsFolder\Microsoft.Office.OneNote.MemoryPreview'
        $button = $null
        $closedOpenNote = $false
        for ($i = 0; $i -lt 40 -and -not $button; $i++) {
            Start-Sleep -Milliseconds 250
            $window = Find ($A::RootElement) ($Scope::Children) 'Sticky Notes (new)'
            if (-not $window) { continue }
            $button = Find $window ($Scope::Descendants) 'New note'
            # With a note open for editing the panel hides "New note"; its Checkmark button closes the note.
            $done = if (-not $button -and -not $closedOpenNote) { Find $window ($Scope::Descendants) 'Checkmark' }
            if ($done) {
                $null = $shell.AppActivate('Sticky Notes (new)')
                Start-Sleep -Milliseconds 300
                Press $done 'Checkmark'
                $closedOpenNote = $true
            }
        }
        if (-not $button) { 'Sticky Notes did not open.'; exit 1 }
        $null = $shell.AppActivate('Sticky Notes (new)')
        Start-Sleep -Milliseconds 300
        Press $button 'New note'
        for ($i = 0; $i -lt 20 -and $A::FocusedElement.Current.Name -notlike 'Note editor*'; $i++) { Start-Sleep -Milliseconds 150 }
        if ($A::FocusedElement.Current.Name -notlike 'Note editor*') { 'The new note did not open for typing.'; exit 1 }
        # A .NET exception does not stop a script, so a failed typing step would still print ok. The loops above rely on that to retry; only this step must not.
        try { $shell.SendKeys(($env:JARVIS_NOTE_TEXT -replace '([+^%~(){}\[\]])', '{$1}')) } catch { "Could not type the note: $($_.Exception.Message)"; exit 1 }
        'ok'
        """;

    public bool CanHandle(string input) =>
        input.Equals("write down", StringComparison.OrdinalIgnoreCase) ||
        input.StartsWith("write down ", StringComparison.OrdinalIgnoreCase);

    public async Task<ToolResult> ExecuteAsync(string input, ToolContext context, CancellationToken cancellationToken)
    {
        // Transcription often ends the command with a full stop: "Write this down. Buy milk."
        var text = ToolInputHelper.GetCommandTail(input, "write down").Trim(' ', '.', ',', ':', ';', '-');

        if (string.IsNullOrWhiteSpace(text))
        {
            return new ToolResult("What should I write down?", FollowUpCommand: "write down");
        }

        var (note, reply) = await ResolveNoteAsync(text, context, cancellationToken);

        if (reply is not null)
        {
            return reply;
        }

        text = note;

        // Launch a hidden Windows PowerShell with the script above. -EncodedCommand takes the script as Base64 text in UTF-16.
        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-EncodedCommand");
        startInfo.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(StickyNoteScript)));
        // Passed through the environment so no quoting or escaping of the dictated text is needed.
        startInfo.Environment["JARVIS_NOTE_TEXT"] = text;

        // Exit code 0 means the script printed ok; otherwise its printed text is the reason shown to the user.
        try
        {
            using var process = Process.Start(startInfo)!;
            var output = (await process.StandardOutput.ReadToEndAsync(cancellationToken)).Trim();
            await process.WaitForExitAsync(cancellationToken);

            if (process.ExitCode == 0)
            {
                return new ToolResult($"Added a sticky note: \"{text}\"");
            }

            return NoteFailure(output.Length > 0 ? output : $"PowerShell exited with code {process.ExitCode}.", text);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return NoteFailure(exception.Message, text);
        }
    }

    /// <summary>The note's words, or a reply when there is nothing to save. Split from ExecuteAsync so tests never reach the desktop script.</summary>
    public async Task<(string Note, ToolResult? Reply)> ResolveNoteAsync(string text, ToolContext context, CancellationToken cancellationToken)
    {
        var reference = ReferenceKind(text);

        if (reference == NoteReference.None)
        {
            text = await StickyNoteText.CondenseAsync(modelGateway, text, DateTime.Now, cancellationToken);
        }
        else
        {
            var turns = context.RecentTurns(reference == NoteReference.Conversation ? ConversationTurns : 1);

            if (turns.Count == 0)
            {
                return (string.Empty, new ToolResult("I don't have anything from this conversation to write down yet. What should I write down?", FollowUpCommand: "write down"));
            }

            // "Add that" after a failed note retries the same note, already condensed.
            if (reference == NoteReference.LastExchange && FailedNoteText(turns[^1].ResponseText) is { } failedNote)
            {
                text = failedNote;
            }
            else
            {
                text = await StickyNoteText.CondenseAsync(modelGateway, text, DateTime.Now, cancellationToken, SourceText(turns));

                if (text.Length == 0)
                {
                    return (string.Empty, new ToolResult("I couldn't work out a note from that, so I didn't save one. What should I write down?", FollowUpCommand: "write down"));
                }
            }
        }

        return (text, null);
    }

    /// <summary>
    /// The note step failed after the text was condensed. The words go back to the user and the planner is skipped,
    /// because the planner would turn the dictation into chat steps, write no note and show no failure.
    /// </summary>
    public static ToolResult NoteFailure(string reason, string text) =>
        new($"I couldn't save the sticky note: {reason.Trim()} Here is the note so you can copy it: \"{text}\"",
            Succeeded: false, SkipReplan: true);

    // What a "write down" request points at: its own words (None), the previous exchange ("that", "what you just said"), or the recent conversation.
    public enum NoteReference { None, LastExchange, Conversation }

    // How many recent turns count as "the conversation", and the most characters of conversation text sent to the model (the newest are kept).
    private const int ConversationTurns = 6;
    private const int SourceCap = 4000;

    // "add that to the sticky note" and "write down our last conversation" name something instead of saying it, so the tool
    // would only see those words. A word like "that" is a reference only when nothing else follows; the conversation phrases
    // need our/this/last/previous, so "the conversation with Sam" is text. Examples: "our last conversation", "everything we
    // talked about so far", "this conversation we just had". They must be the whole request, so "our conversation tomorrow
    // is cancelled" stays plain text; "we had" may run on.
    private static readonly Regex ConversationRegex = new(
        @"^(?:(?:all|everything|anything)\s+(?:of\s+|from\s+|in\s+)?)?(?:(?:our|this|(?:the\s+)?(?:last|previous|recent))\s+(?:(?:last|previous|recent|whole|entire)\s+)*conversation(?:\s+we\s+(?:just\s+)?had\b(?!\s+with\b).*)?|what\s+we\s+(?:just\s+)?(?:talked|spoke|chatted)\s+about|what\s+we\s+(?:just\s+)?(?:discussed|said))(?:\s+(?:so\s+far|today|tonight|earlier|just\s+now))?$",
        RegexOptions.IgnoreCase);

    // Matches phrases that name the previous exchange, e.g. "that", "it down", "what you just said", "your last answer". Whole request only.
    private static readonly Regex LastExchangeRegex = new(
        @"^(?:(?:that|this|it)(?:\s+down)?|(?:what|everything)\s+(?:you|i)\s+(?:just\s+)?(?:said|told\s+me|answered|replied)|the\s+(?:last|previous)\s+(?:thing|answer|reply|response)(?:\s+you\s+(?:said|told\s+me))?|your\s+(?:last\s+|previous\s+)?(?:answer|reply|response))$",
        RegexOptions.IgnoreCase);

    // Classifies the dictated text as a reference to earlier conversation or as plain text to save. Matched on the exact whole phrase.
    public static NoteReference ReferenceKind(string text)
    {
        var trimmed = text.Trim(' ', '.', ',', ':', ';', '!', '?', '-');
        return ConversationRegex.IsMatch(trimmed) ? NoteReference.Conversation
            : LastExchangeRegex.IsMatch(trimmed) ? NoteReference.LastExchange
            : NoteReference.None;
    }

    /// <summary>The words of a note that failed to save, from the reply <see cref="NoteFailure"/> gave.</summary>
    public static string? FailedNoteText(string response)
    {
        var match = Regex.Match(response ?? string.Empty, "Here is the note so you can copy it: \"(?<note>.+)\"\\s*$", RegexOptions.Singleline);
        return match.Success ? match.Groups["note"].Value : null;
    }

    /// <summary>The turns as labelled text for the model, newest kept when the cap trims.</summary>
    public static string SourceText(IReadOnlyList<ConversationStateEntry> turns)
    {
        var text = string.Join("\n\n", turns.Select(turn => $"User: {turn.UserInput.Trim()}\nJarvis: {turn.ResponseText.Trim()}"));
        return text.Length <= SourceCap ? text : text[^SourceCap..];
    }
}

// Cleans up dictated text for a sticky note, using the model when it is available and a simple filler-word clean-up when it is not.
/// <summary>Turns natural dictation ("so um tomorrow I need to grab milk and eggs after class") into a sticky note.</summary>
public static partial class StickyNoteText
{
    // Prompt text sent to the model to condense dictation. Wording matters: the rules and examples steer it away from inventing details.
    private const string Instructions =
        "You turn dictated speech into a short sticky note. Write the note itself, never a reply to the user.\n"
        + "- Keep every concrete detail: times, places, amounts, and names or titles exactly as said (\"IT Professions exam\", not \"IT exam\").\n"
        + "- Drop filler, hesitations, and anything said to the assistant (\"can you\", \"make a note\").\n"
        + "- At most 15 words on one line, as a terse fragment or command; separate list items with commas.\n"
        + "- Keep day words exactly as said (today, tonight, tomorrow, Friday) and never add a day or date that was not said.\n"
        + "- A number is a time only when said as one (\"at 3\", \"3pm\", \"3 o'clock\"); \"homework three\" is \"Homework 3\".\n"
        + "- Plain text only: no quotes, emoji, or markdown. Reply with the note only.\n"
        + "Examples:\n"
        + "Dictated: so um I need to grab paper towels and uh dish soap -> Grab paper towels, dish soap\n"
        + "Dictated: can you write down that the wifi password for the lab is blue falcon 42 -> Lab wifi password: blue falcon 42";

    // Without this the model, given only the request sentence, invents a note about the conversation.
    private const string SourceInstructions =
        "\nThe input is not dictation but part of the conversation between User and Jarvis. Write the note from what is said there and nothing else; "
        + "add no fact, name, time or day that does not appear in it.\n"
        // At 15 words a sample with a due date and a meeting kept only the meeting. 30 words still fits Accept's 200 characters.
        + "For this note the limit is 30 words: keep every separate fact, task, date and time, separated by commas, and leave out greetings and small talk. "
        + "If there is nothing but greetings and small talk, reply with the single word none.";

    // Asks the model to condense the dictation (12 second limit). With source text the model sees only the earlier conversation, never the request itself.
    // If the model is missing, slow, errors, or gives an unrelated answer, the fallback is used instead. Day words are turned into dates at the end.
    /// <param name="source">Earlier turns the note is about ("add that", "our conversation"). The model sees only these, never the request;
    /// with no usable note the result is empty, since tidying the request sentence would save the request itself as the note.</param>
    public static async Task<string> CondenseAsync(IModelGateway? gateway, string dictated, DateTime now, CancellationToken cancellationToken, string? source = null)
    {
        var fallback = source is null ? Tidy(dictated) : string.Empty;

        if (gateway is not { IsAvailable: true })
        {
            return fallback;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            var response = await gateway.CompleteAsync(
                new ChatCompletionRequest(
                    [
                        new ChatMessage("system", [ChatMessageContentPart.FromText(source is null ? Instructions : Instructions + SourceInstructions)]),
                        new ChatMessage("user", [ChatMessageContentPart.FromText(source is null ? $"Dictated: {dictated}" : $"Conversation:\n{source}")])
                    ],
                    Temperature: 0.1),
                ModelRoute.Fast,
                timeout.Token);

            return ResolveDays(Accept(response.Content, source ?? dictated) ?? fallback, now);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Model offline, slow, or erroring: the note still gets written, just not condensed.
            return ResolveDays(fallback, now);
        }
    }

    /// <summary>
    /// "tomorrow" means nothing on a note read next week, so day words become dates: "Mon 9/28".
    /// Done here rather than by the model, which added dates nobody said.
    /// </summary>
    public static string ResolveDays(string note, DateTime now)
    {
        string Label(DateTime day) => day.ToString("ddd M/d", CultureInfo.InvariantCulture);

        return DayWordRegex().Replace(note, match =>
        {
            var word = match.Groups["word"].Value.ToLowerInvariant();

            if (word is "today" or "tonight")
            {
                return word == "tonight" ? $"tonight ({Label(now)})" : Label(now);
            }

            if (word == "tomorrow")
            {
                return Label(now.AddDays(1));
            }

            var target = (int)Enum.GetValues<DayOfWeek>().First(day => day.ToString().StartsWith(word[..3], StringComparison.OrdinalIgnoreCase));
            var ahead = (target - (int)now.DayOfWeek + 7) % 7;
            return Label(now.AddDays(ahead == 0 && match.Groups["next"].Success ? 7 : ahead));
        });
    }

    /// <summary>The model's note, or null when it is empty, overlong, or unrelated to what was said.</summary>
    public static string? Accept(string modelOutput, string dictated)
    {
        var note = ThinkBlockRegex().Replace(modelOutput ?? string.Empty, string.Empty);
        note = Regex.Replace(note, @"^\s*(?:sticky\s+)?note\s*:\s*", string.Empty, RegexOptions.IgnoreCase);
        note = Regex.Replace(note, @"\s+", " ").Trim().Trim('"', '\'', '`', '*').Trim();

        if (note.Length == 0 || note.Length > 200)
        {
            return null;
        }

        // A note sharing no words with the dictation is the model answering instead of condensing.
        var said = ContentWords(dictated);
        return ContentWords(note).Any(said.Contains) ? char.ToUpperInvariant(note[0]) + note[1..] : null;
    }

    // Fallback when the model is unavailable: just removes filler words (um, uh, erm, you know) and tidies spaces and capitals.
    public static string Tidy(string dictated)
    {
        var note = Regex.Replace(dictated, @"\b(?:um+|uh+|erm|you know)\b,?\s*", string.Empty, RegexOptions.IgnoreCase);
        note = Regex.Replace(note, @"\s+", " ").Trim(' ', ',', '.');
        return note.Length == 0 ? dictated.Trim() : char.ToUpperInvariant(note[0]) + note[1..];
    }

    // Full day names only: abbreviations are ordinary words too ("sun screen", "SAT prep").
    // "next Friday" said on a Friday is a week out; "this Friday" is today.
    [GeneratedRegex(@"\b(?:(?<next>next)\s+|(?:this|coming)\s+)?(?<word>today|tonight|tomorrow|monday|tuesday|wednesday|thursday|friday|saturday|sunday)\b(?!\s*\d{1,2}/\d{1,2})", RegexOptions.IgnoreCase)]
    private static partial Regex DayWordRegex();

    // The lowercase words of 3+ letters or digits, used to check that a model-written note shares words with what was said.
    private static HashSet<string> ContentWords(string text) =>
        Regex.Matches(text.ToLowerInvariant(), @"[a-z0-9]{3,}").Select(match => match.Value).ToHashSet();

    // Matches a reasoning block (think tags) that some models print before the answer, so it can be removed.
    [GeneratedRegex(@"<think>[\s\S]*?</think>", RegexOptions.IgnoreCase)]
    private static partial Regex ThinkBlockRegex();
}

// Puts the computer to sleep a few seconds after replying, so the reply can be shown and spoken first.
public sealed class SleepTool : IAssistantTool
{
    public string Name => "sleep";

    public string Description => "Put the computer to sleep with `sleep`.";

    public bool CanHandle(string input) => input.Equals("sleep", StringComparison.OrdinalIgnoreCase);

    public Task<ToolResult> ExecuteAsync(string input, ToolContext context, CancellationToken cancellationToken)
    {
        // Wait so the reply is shown and spoken before the machine goes down.
        _ = Task.Delay(TimeSpan.FromSeconds(3)).ContinueWith(_ =>
        {
            if (IsPwrSuspendAllowed())
            {
                SetSuspendState(false, false, false);
            }
            else
            {
                // Modern Standby laptops have no S3 sleep, and SetSuspendState would hibernate them.
                // Turning the display off is what the power button does, and it enters standby.
                PostMessage(new IntPtr(0xFFFF), 0x0112, new IntPtr(0xF170), new IntPtr(2));
            }
        }, TaskScheduler.Default);

        return Task.FromResult(new ToolResult("Going to sleep. Goodnight!"));
    }

    // Windows power-management calls, loaded straight from the system DLLs (P/Invoke).
    [System.Runtime.InteropServices.DllImport("powrprof.dll")]
    private static extern bool IsPwrSuspendAllowed();

    [System.Runtime.InteropServices.DllImport("powrprof.dll")]
    private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}

// Shows computer status: drives, process count and similar.
public sealed class SystemStateTool : IAssistantTool
{
    public string Name => "system";

    public string Description => "Show computer status, drives, and running process count.";

    public bool CanHandle(string input) =>
        input.Equals("system", StringComparison.OrdinalIgnoreCase) ||
        input.Equals("system state", StringComparison.OrdinalIgnoreCase) ||
        input.Equals("computer status", StringComparison.OrdinalIgnoreCase) ||
        input.Equals("machine status", StringComparison.OrdinalIgnoreCase);

    public async Task<ToolResult> ExecuteAsync(string input, ToolContext context, CancellationToken cancellationToken)
    {
        return new ToolResult(await context.DesktopAutomation.GetSystemStatusAsync(cancellationToken));
    }
}

// Shows live desktop context: active window, visible apps, workspace files and system state. Many phrasings are accepted.
public sealed class ComputerContextTool : IAssistantTool
{
    public string Name => "computer";

    public string Description => "Show live desktop context including the active window, visible apps, workspace entries, and system state.";

    public bool CanHandle(string input) =>
        input.Equals("computer", StringComparison.OrdinalIgnoreCase) ||
        input.Equals("computer context", StringComparison.OrdinalIgnoreCase) ||
        input.Equals("desktop context", StringComparison.OrdinalIgnoreCase) ||
        input.Equals("active window", StringComparison.OrdinalIgnoreCase) ||
        input.Equals("what app am i in", StringComparison.OrdinalIgnoreCase) ||
        input.Equals("what is happening on my computer", StringComparison.OrdinalIgnoreCase) ||
        input.Equals("whats happening on my computer", StringComparison.OrdinalIgnoreCase) ||
        input.Equals("what is going on on my computer", StringComparison.OrdinalIgnoreCase) ||
        input.Equals("whats going on on my computer", StringComparison.OrdinalIgnoreCase);

    public async Task<ToolResult> ExecuteAsync(string input, ToolContext context, CancellationToken cancellationToken)
    {
        return new ToolResult(await context.DesktopAutomation.GetComputerContextAsync(cancellationToken));
    }
}

// Network overview, device scan or health check, or details for one IP or host. The desktop service does the real work.
public sealed class NetworkTool : IAssistantTool
{
    public string Name => "network";

    public string Description => "Inspect the local network. Use `network`, `network scan`, `network health`, or `network <ip-or-host>`.";

    public bool CanHandle(string input)
    {
        var normalized = input.Trim();

        return LocalNetworkMonitoring.ModePhrases.ContainsKey(normalized)
            || normalized.StartsWith("network ", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<ToolResult> ExecuteAsync(string input, ToolContext context, CancellationToken cancellationToken)
    {
        var target = ParseNetworkTarget(input);
        var response = await context.DesktopAutomation.GetLocalNetworkStatusAsync(target, cancellationToken);

        return new ToolResult(
            response,
            VerificationText: "The local network monitor completed an on-demand discovery or health check.",
            SummaryText: response.ReplaceLineEndings(" ").Trim());
    }

    // Maps the many ways of asking onto "scan", "health", empty (overview) or a specific host.
    private static string ParseNetworkTarget(string input)
    {
        var normalized = input.Trim();

        return LocalNetworkMonitoring.ModePhrases.TryGetValue(normalized, out var mode)
            ? mode switch
            {
                LocalNetworkMonitoring.NetworkRequestMode.Scan => "scan",
                LocalNetworkMonitoring.NetworkRequestMode.Health => "health",
                _ => string.Empty
            }
            : ToolInputHelper.GetCommandTail(normalized, "network");
    }
}

// Turns NordVPN on or off, or reports its state, by running NordVPN's own command line and polling until it takes effect.
public sealed class VpnTool : IAssistantTool
{
    public string Name => "vpn";

    public string Description => "Turn NordVPN on or off, or check it: `vpn on`, `vpn off`, `vpn status`.";

    public bool CanHandle(string input) => ToolInputHelper.StartsWithCommand(input, "vpn");

    public async Task<ToolResult> ExecuteAsync(string input, ToolContext context, CancellationToken cancellationToken)
    {
        // Read the state first: status needs no change, an unknown word is refused, and asking for the state it already has does nothing.
        var action = ToolInputHelper.GetCommandTail(input, "vpn").Trim().ToLowerInvariant();
        var isOn = IsConnected();

        if (action is "" or "status")
        {
            return new ToolResult(isOn ? "Your VPN is on." : "Your VPN is off.");
        }

        if (action is not ("on" or "off"))
        {
            return new ToolResult("Usage: vpn on, vpn off, or vpn status.", Succeeded: false);
        }

        var turnOn = action == "on";

        if (isOn == turnOn)
        {
            return new ToolResult($"Your VPN is already {action}.");
        }

        var nordVpn = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NordVPN", "NordVPN.exe");

        if (!File.Exists(nordVpn))
        {
            return new ToolResult($"I couldn't find NordVPN at {nordVpn}.", Succeeded: false);
        }

        // NordVPN's own command line: it hands -c / -d to the running app and exits.
        using (Process.Start(nordVpn, turnOn ? "-c" : "-d"))
        {
        }

        // Connecting took 1.7 s to 13 s in testing, depending on the server NordVPN picks.
        for (var waited = 0; waited < 30_000; waited += 250)
        {
            await Task.Delay(250, cancellationToken);

            if (IsConnected() == turnOn)
            {
                return new ToolResult($"VPN is {action}.");
            }
        }

        return new ToolResult(
            $"I asked NordVPN to turn {action}, but it hasn't yet. Check the NordVPN app.",
            Succeeded: false);
    }

    /// <summary>
    /// Whether internet traffic goes out through a NordVPN adapter. The adapters stay "Up" with an address
    /// even while disconnected, so their status says nothing; the route Windows picks does.
    /// </summary>
    private static bool IsConnected()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect("1.1.1.1", 53); // a UDP connect sends nothing; it only chooses the route
            var source = ((IPEndPoint)socket.LocalEndPoint!).Address;

            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(adapter => adapter.Description.Contains("Nord", StringComparison.OrdinalIgnoreCase))
                .Any(adapter => adapter.GetIPProperties().UnicastAddresses.Any(address => address.Address.Equals(source)));
        }
        catch (SocketException)
        {
            return false; // no network at all
        }
    }
}

// Lists installed or running apps, optionally filtered by name.
public sealed class ApplicationsTool : IAssistantTool
{
    public string Name => "apps";

    public string Description => "List discovered applications or search them with `apps <name>`.";

    public bool CanHandle(string input) =>
        input.Equals("apps", StringComparison.OrdinalIgnoreCase) ||
        input.Equals("running apps", StringComparison.OrdinalIgnoreCase) ||
        input.Equals("what apps are open", StringComparison.OrdinalIgnoreCase) ||
        input.Equals("whats open", StringComparison.OrdinalIgnoreCase) ||
        input.StartsWith("apps ", StringComparison.OrdinalIgnoreCase) ||
        input.StartsWith("find app", StringComparison.OrdinalIgnoreCase) ||
        input.StartsWith("search apps", StringComparison.OrdinalIgnoreCase) ||
        input.StartsWith("list apps", StringComparison.OrdinalIgnoreCase);

    public async Task<ToolResult> ExecuteAsync(string input, ToolContext context, CancellationToken cancellationToken)
    {
        var target = ToolInputHelper.GetCommandTail(input, "apps", "find app", "search apps", "list apps", "running apps");
        return new ToolResult(await context.DesktopAutomation.ListApplicationsAsync(target, cancellationToken));
    }
}

// Focuses an app that is already running, or launches it.
public sealed class OpenApplicationTool : IAssistantTool
{
    public string Name => "open app";

    public string Description => "Focus a running app or launch a discovered application with `open app <name>` or `launch <name>`.";

    public bool CanHandle(string input) =>
        input.StartsWith("open app", StringComparison.OrdinalIgnoreCase) ||
        input.StartsWith("launch", StringComparison.OrdinalIgnoreCase) ||
        input.StartsWith("start app", StringComparison.OrdinalIgnoreCase);

    public async Task<ToolResult> ExecuteAsync(string input, ToolContext context, CancellationToken cancellationToken)
    {
        var target = ToolInputHelper.GetCommandTail(input, "open app", "launch app", "launch", "start app");
        return new ToolResult(await context.DesktopAutomation.OpenApplicationAsync(target, cancellationToken));
    }
}

// Opens a file or folder with its default program. Opening a program or script asks for approval first (see ActionSafety.cs).
public sealed class OpenPathTool : IAssistantTool
{
    public string Name => "open path";

    public string Description => "Open a file or folder with `open path <target>` or `open file <target>`.";

    public bool CanHandle(string input) =>
        input.StartsWith("open path", StringComparison.OrdinalIgnoreCase) ||
        input.StartsWith("open file", StringComparison.OrdinalIgnoreCase) ||
        input.StartsWith("open folder", StringComparison.OrdinalIgnoreCase);

    public async Task<ToolResult> ExecuteAsync(string input, ToolContext context, CancellationToken cancellationToken)
    {
        var target = ToolInputHelper.GetCommandTail(input, "open path", "open file", "open folder");
        return new ToolResult(await context.DesktopAutomation.OpenPathAsync(target, cancellationToken));
    }
}

// Lists the files in a folder.
public sealed class ListFilesTool : IAssistantTool
{
    public string Name => "list files";

    public string Description => "List files in a directory with `list files <path>`.";

    public bool CanHandle(string input) =>
        input.StartsWith("list files", StringComparison.OrdinalIgnoreCase) ||
        input.StartsWith("show files", StringComparison.OrdinalIgnoreCase) ||
        input.StartsWith("list folder", StringComparison.OrdinalIgnoreCase);

    public async Task<ToolResult> ExecuteAsync(string input, ToolContext context, CancellationToken cancellationToken)
    {
        var target = ToolInputHelper.GetCommandTail(input, "list files", "show files", "list folder");
        return new ToolResult(await context.DesktopAutomation.ListFilesAsync(target, cancellationToken));
    }
}

// Shows a preview of a text file.
public sealed class ReadFileTool : IAssistantTool
{
    public string Name => "read file";

    public string Description => "Preview a text file with `read file <path>`.";

    public bool CanHandle(string input) =>
        input.StartsWith("read file", StringComparison.OrdinalIgnoreCase) ||
        input.StartsWith("show file", StringComparison.OrdinalIgnoreCase) ||
        input.StartsWith("preview file", StringComparison.OrdinalIgnoreCase);

    public async Task<ToolResult> ExecuteAsync(string input, ToolContext context, CancellationToken cancellationToken)
    {
        var target = ToolInputHelper.GetCommandTail(input, "read file", "show file", "preview file");
        return new ToolResult(await context.DesktopAutomation.ReadFilePreviewAsync(target, cancellationToken));
    }
}

// Searches common user folders (Desktop, OneDrive Desktop, Documents, Downloads) for files by name.
public sealed class FindFilesTool : IAssistantTool
{
    public string Name => "find files";

    public string Description => "Search your Desktop, OneDrive Desktop, Documents and Downloads by name with `find files <term>`.";

    public bool CanHandle(string input) =>
        input.StartsWith("find files", StringComparison.OrdinalIgnoreCase) ||
        input.StartsWith("find file", StringComparison.OrdinalIgnoreCase) ||
        input.StartsWith("search files", StringComparison.OrdinalIgnoreCase);

    public async Task<ToolResult> ExecuteAsync(string input, ToolContext context, CancellationToken cancellationToken)
    {
        var target = ToolInputHelper.GetCommandTail(input, "find files", "find file", "search files");
        return new ToolResult(await context.DesktopAutomation.FindFilesAsync(target, cancellationToken));
    }
}

// Opens a website or a known site name in the browser.
public sealed class BrowseTool : IAssistantTool
{
    public string Name => "browse";

    public string Description => "Open a website in the browser with `browse <url>` or `go to <site>`; site names work too: teams, discord, linkedin, linkedin messages, notion, canvas, github, figma.";

    public bool CanHandle(string input) =>
        input.StartsWith("browse", StringComparison.OrdinalIgnoreCase) ||
        input.StartsWith("open website", StringComparison.OrdinalIgnoreCase) ||
        input.StartsWith("go to", StringComparison.OrdinalIgnoreCase) ||
        input.StartsWith("visit", StringComparison.OrdinalIgnoreCase);

    public async Task<ToolResult> ExecuteAsync(string input, ToolContext context, CancellationToken cancellationToken)
    {
        var target = ToolInputHelper.GetCommandTail(input, "browse", "open website", "go to", "visit");
        return new ToolResult(await context.DesktopAutomation.BrowseAsync(target, cancellationToken));
    }
}

// Runs a web search in the browser.
public sealed class SearchWebTool : IAssistantTool
{
    public string Name => "search web";

    public string Description => "Run a browser search with `search web <query>`.";

    public bool CanHandle(string input) =>
        input.StartsWith("search web", StringComparison.OrdinalIgnoreCase) ||
        input.StartsWith("web search", StringComparison.OrdinalIgnoreCase);

    public async Task<ToolResult> ExecuteAsync(string input, ToolContext context, CancellationToken cancellationToken)
    {
        var target = ToolInputHelper.GetCommandTail(input, "search web", "web search");
        return new ToolResult(await context.DesktopAutomation.SearchWebAsync(target, cancellationToken));
    }
}

// Web research that returns cited sources, using the research service. Reports offline status separately from "no results".
public sealed class ResearchTool : IAssistantTool
{
    public string Name => "research";

    public string Description => "Run web research with citations using `research <query>`.";

    public bool CanHandle(string input) =>
        input.StartsWith("research ", StringComparison.OrdinalIgnoreCase) ||
        input.StartsWith("web research ", StringComparison.OrdinalIgnoreCase);

    public async Task<ToolResult> ExecuteAsync(string input, ToolContext context, CancellationToken cancellationToken)
    {
        var query = ToolInputHelper.GetCommandTail(input, "research", "web research");

        if (string.IsNullOrWhiteSpace(query))
        {
            return new ToolResult(
                "Usage: research <topic>",
                Succeeded: false,
                VerificationText: "No research query was provided.",
                SummaryText: "Missing research query.");
        }

        var result = await context.WebResearchService.ResearchAsync(
            query,
            context.Options.PlannerWebResearchMaxResults,
            cancellationToken);

        if (result.Citations.Length == 0)
        {
            return new ToolResult(
                result.SummaryText,
                Succeeded: !result.IsOffline,
                VerificationText: result.IsOffline ? "The research service could not reach the public web." : "The research service returned no sources.",
                SummaryText: result.SummaryText);
        }

        var lines = result.Citations.Select(citation =>
            $"- {citation.Title} | {citation.Url} | {citation.Snippet}");

        return new ToolResult(
            $"Web research for \"{query}\":{Environment.NewLine}{string.Join(Environment.NewLine, lines)}",
            VerificationText: $"Collected {result.Citations.Length} citation(s) for the query.",
            SummaryText: $"Collected {result.Citations.Length} web citation(s).");
    }
}

// Screen tools: list monitors, capture, read text (OCR), find UI elements, or answer a question about what is on screen.
public sealed class ScreenTool : IAssistantTool
{
    public string Name => "screen";

    public string Description => "Monitor- and window-aware screen tooling. Use `screen`, `screen monitors`, `screen capture [target]`, `screen ocr [target]`, or `screen ui [target]`.";

    public bool CanHandle(string input) =>
        ToolInputHelper.StartsWithCommand(input, "screen") ||
        ToolInputHelper.StartsWithCommand(input, "analyze screen") ||
        ToolInputHelper.StartsWithCommand(input, "inspect screen") ||
        ToolInputHelper.MatchesExact(input, "monitors", "show monitors", "list monitors", "displays", "show displays", "list displays") ||
        ToolInputHelper.StartsWithCommand(input, "screenshot") ||
        ToolInputHelper.StartsWithCommand(input, "take screenshot") ||
        ToolInputHelper.StartsWithCommand(input, "capture screen") ||
        ToolInputHelper.StartsWithCommand(input, "screen capture") ||
        ToolInputHelper.StartsWithCommand(input, "screen ocr") ||
        ToolInputHelper.StartsWithCommand(input, "screen text") ||
        ToolInputHelper.StartsWithCommand(input, "ocr") ||
        ToolInputHelper.StartsWithCommand(input, "extract text from screen") ||
        ToolInputHelper.StartsWithCommand(input, "detect ui") ||
        ToolInputHelper.StartsWithCommand(input, "screen ui") ||
        ToolInputHelper.StartsWithCommand(input, "screen elements");

    // The first matching branch wins; with no sub-command the text after "screen" is treated as a question about the current screen.
    public Task<ToolResult> ExecuteAsync(string input, ToolContext context, CancellationToken cancellationToken)
    {
        if (ToolInputHelper.MatchesExact(input, "monitors", "show monitors", "list monitors", "displays", "show displays", "list displays"))
        {
            return context.VisualContextService.ListDisplaysAsync(cancellationToken);
        }

        if (ToolInputHelper.StartsWithCommand(input, "screenshot")
            || ToolInputHelper.StartsWithCommand(input, "take screenshot")
            || ToolInputHelper.StartsWithCommand(input, "capture screen")
            || ToolInputHelper.StartsWithCommand(input, "screen capture"))
        {
            var target = ToolInputHelper.GetCommandTail(input, "screenshot", "take screenshot", "capture screen", "screen capture");
            return context.VisualContextService.CaptureScreenAsync(target, cancellationToken);
        }

        if (ToolInputHelper.StartsWithCommand(input, "screen ocr")
            || ToolInputHelper.StartsWithCommand(input, "screen text")
            || ToolInputHelper.StartsWithCommand(input, "ocr")
            || ToolInputHelper.StartsWithCommand(input, "extract text from screen"))
        {
            var target = ToolInputHelper.GetCommandTail(input, "screen ocr", "screen text", "ocr", "extract text from screen");
            return context.VisualContextService.ExtractScreenTextAsync(target, cancellationToken);
        }

        if (ToolInputHelper.StartsWithCommand(input, "detect ui")
            || ToolInputHelper.StartsWithCommand(input, "screen ui")
            || ToolInputHelper.StartsWithCommand(input, "screen elements"))
        {
            var target = ToolInputHelper.GetCommandTail(input, "detect ui", "screen ui", "screen elements");
            return context.VisualContextService.DetectScreenUiAsync(target, cancellationToken);
        }

        var question = ToolInputHelper.GetCommandTail(input, "screen", "analyze screen", "inspect screen");
        return context.VisualContextService.AnalyzeCurrentScreenAsync(question, cancellationToken);
    }
}

// Describes an image file, optionally answering a question about it (path :: question).
public sealed class AnalyzeImageTool : IAssistantTool
{
    public string Name => "analyze image";

    public string Description => "Analyze an image file. Format: `analyze image <path>` or `analyze image <path> :: <question>`.";

    public bool CanHandle(string input) =>
        input.StartsWith("analyze image", StringComparison.OrdinalIgnoreCase) ||
        input.StartsWith("inspect image", StringComparison.OrdinalIgnoreCase);

    public Task<ToolResult> ExecuteAsync(string input, ToolContext context, CancellationToken cancellationToken)
    {
        var payload = ToolInputHelper.GetCommandTail(input, "analyze image", "inspect image");
        return context.VisualContextService.AnalyzeImageAsync(payload, cancellationToken);
    }
}

// Reads a text or image-based document, optionally answering a question about it (path :: question).
public sealed class AnalyzeDocumentTool : IAssistantTool
{
    public string Name => "analyze document";

    public string Description => "Analyze a text or image-based document. Format: `analyze document <path>` or `analyze document <path> :: <question>`.";

    public bool CanHandle(string input) =>
        input.StartsWith("analyze document", StringComparison.OrdinalIgnoreCase) ||
        input.StartsWith("inspect document", StringComparison.OrdinalIgnoreCase);

    public Task<ToolResult> ExecuteAsync(string input, ToolContext context, CancellationToken cancellationToken)
    {
        var payload = ToolInputHelper.GetCommandTail(input, "analyze document", "inspect document");
        return context.VisualContextService.AnalyzeDocumentAsync(payload, cancellationToken);
    }
}

// Runs a PowerShell command. Off unless shellExecutionEnabled is set. Whether it runs freely, asks first or is blocked is decided in ActionSafety.cs.
public sealed class ShellTool : IAssistantTool
{
    public string Name => "run";

    public string Description => "Run a PowerShell command with `run <command>` when enabled. Read-only commands run directly; major changes require approval.";

    public bool CanHandle(string input) =>
        input.StartsWith("run ", StringComparison.OrdinalIgnoreCase) ||
        input.StartsWith("shell ", StringComparison.OrdinalIgnoreCase);

    public async Task<ToolResult> ExecuteAsync(string input, ToolContext context, CancellationToken cancellationToken)
    {
        if (!context.Options.ShellExecutionEnabled)
        {
            return new ToolResult(
                "Shell execution is disabled. Set `shellExecutionEnabled` to `true` in jarvis.settings.json to allow it.",
                Succeeded: false,
                VerificationText: "The runtime blocked shell execution because it is disabled in settings.",
                SummaryText: "Shell execution is disabled.");
        }

        var command = input.StartsWith("shell ", StringComparison.OrdinalIgnoreCase)
            ? input["shell ".Length..].Trim()
            : input["run ".Length..].Trim();

        if (string.IsNullOrWhiteSpace(command))
        {
            return new ToolResult(
                "Usage: run <PowerShell command>",
                Succeeded: false,
                VerificationText: "No PowerShell command was provided.",
                SummaryText: "Missing PowerShell command.");
        }

        // A result starting with "Command failed" or "Shell process failed" is how failure is recognised, because the helper below returns text, not an error.
        var output = await ExecutePowerShellAsync(command, context.WorkspaceRoot, cancellationToken);
        var failed = output.StartsWith("Command failed", StringComparison.OrdinalIgnoreCase)
            || output.StartsWith("Shell process failed", StringComparison.OrdinalIgnoreCase);

        return new ToolResult(
            output,
            Succeeded: !failed,
            VerificationText: failed
                ? "PowerShell returned a failure signal."
                : "PowerShell returned a success signal.",
            SummaryText: output.ReplaceLineEndings(" ").Trim());
    }

    // ponytail: one fixed deadline for every command; make it a setting if long installs ever need more.
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(120);

    // Runs a command in a hidden PowerShell and returns its output as text, with a 120 second time limit. The command is passed Base64-encoded
    // (UTF-16, as PowerShell's -EncodedCommand expects) so quotes and special characters in it cannot break the command line.
    private static async Task<string> ExecutePowerShellAsync(string command, string workspaceRoot, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        var encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell",
            Arguments = $"-NoProfile -NonInteractive -EncodedCommand {encodedCommand}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workspaceRoot
        };

        using var process = Process.Start(startInfo);

        if (process is null)
        {
            return "Shell process failed to start.";
        }

        // Output is read while the process runs, so a full output buffer cannot make the process hang.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var deadline = timeout ?? CommandTimeout;
        using var deadlineSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadlineSource.CancelAfter(deadline);
        var timedOut = false;

        try
        {
            await process.WaitForExitAsync(deadlineSource.Token);
        }
        catch (OperationCanceledException)
        {
            // Cancelling the wait leaves the child running, so stop the whole tree ourselves.
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            cancellationToken.ThrowIfCancellationRequested();
            timedOut = true;
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        var combined = string.Join(
            Environment.NewLine,
            new[] { stdout.Trim(), stderr.Trim() }.Where(text => !string.IsNullOrWhiteSpace(text)));

        var trimmedOutput = TrimOutput(combined);

        if (timedOut)
        {
            return $"Command was stopped after {deadline.TotalSeconds:0} seconds without finishing."
                + (string.IsNullOrWhiteSpace(trimmedOutput) ? string.Empty : Environment.NewLine + trimmedOutput);
        }

        if (process.ExitCode == 0)
        {
            return string.IsNullOrWhiteSpace(trimmedOutput)
                ? "Command completed successfully."
                : "Command output:" + Environment.NewLine + trimmedOutput;
        }

        return string.IsNullOrWhiteSpace(trimmedOutput)
            ? $"Command failed with exit code {process.ExitCode}."
            : $"Command failed with exit code {process.ExitCode}:{Environment.NewLine}{trimmedOutput}";
    }

    // Cuts long output to 2000 characters so it does not flood the screen or the model.
    private static string TrimOutput(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        const int maxChars = 2000;
        var value = text.Trim();

        if (value.Length <= maxChars)
        {
            return value;
        }

        return value[..maxChars] + Environment.NewLine + "[output truncated]";
    }
}

// Ends the assistant.
public sealed class ExitTool : IAssistantTool
{
    public string Name => "exit";

    public string Description => "Shutdown the assistant shell.";

    public bool CanHandle(string input) =>
        input.Equals("exit", StringComparison.OrdinalIgnoreCase) ||
        input.Equals("quit", StringComparison.OrdinalIgnoreCase) ||
        input.Equals("shutdown", StringComparison.OrdinalIgnoreCase);

    public Task<ToolResult> ExecuteAsync(string input, ToolContext context, CancellationToken cancellationToken)
    {
        return Task.FromResult(new ToolResult("Shutting down.", true));
    }
}

// Helper functions shared by many tools (also used by AppIntegrationTools.cs and other tool files).
internal static class ToolInputHelper
{
    // Shortens text to one line of at most max characters, adding "..." when cut.
    internal static string Clip(this string text, int max)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var flattened = text.ReplaceLineEndings(" ").Trim();
        return flattened.Length <= max ? flattened : flattened[..max] + "...";
    }

    // Returns what follows the first matching command prefix, minus leading spaces and separators. For prefixes "open app" and "launch",
    // "open app, chrome" gives "chrome". Put longer prefixes first, because the first match wins. Empty if none match.
    public static string GetCommandTail(string input, params string[] prefixes)
    {
        foreach (var prefix in prefixes)
        {
            if (!input.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return input[prefix.Length..].TrimStart(' ', ',', ':', ';', '-');
        }

        return string.Empty;
    }

    // True if the input equals any of the given phrases (ignoring case).
    public static bool MatchesExact(string input, params string[] values)
    {
        return values.Any(value => input.Equals(value, StringComparison.OrdinalIgnoreCase));
    }

    // True if the input is exactly the command word or starts with it followed by a space, so "screen" matches but "screenshot" does not.
    public static bool StartsWithCommand(string input, string value)
    {
        return input.Equals(value, StringComparison.OrdinalIgnoreCase)
            || input.StartsWith(value + " ", StringComparison.OrdinalIgnoreCase);
    }

    // Formats one memory as a single line: date, text, then extras like id, app, location, reminder time and retention.
    public static string RenderMemoryLine(MemoryNote note)
    {
        var details = new List<string>();

        details.Add("id " + ShortId(note.Id));

        if (!string.IsNullOrWhiteSpace(note.Kind) && !string.Equals(note.Kind, "note", StringComparison.OrdinalIgnoreCase))
        {
            details.Add(note.Kind);
        }

        if (!string.IsNullOrWhiteSpace(note.Category))
        {
            details.Add(note.Category);
        }

        if (note.Context is not null)
        {
            if (!string.IsNullOrWhiteSpace(note.Context.ActiveApp))
            {
                details.Add("app " + note.Context.ActiveApp.Trim());
            }

            if (!string.IsNullOrWhiteSpace(note.Context.Location))
            {
                details.Add("location " + note.Context.Location.Trim());
            }

            if (!string.IsNullOrWhiteSpace(note.Context.TimeOfDay))
            {
                details.Add(note.Context.TimeOfDay.Trim());
            }
        }

        if (note.ReminderAtUtc is DateTimeOffset reminderAtUtc)
        {
            details.Add("due " + reminderAtUtc.ToLocalTime().ToString("MM-dd h:mm tt"));
        }

        if (!string.IsNullOrWhiteSpace(note.RetentionPolicy))
        {
            details.Add("retention " + note.RetentionPolicy.Trim());
        }

        var suffix = details.Count == 0 ? string.Empty : " | " + string.Join(" | ", details);
        return $"- {note.CreatedAtUtc:yyyy-MM-dd HH:mm}Z | {note.Content}{suffix}";
    }

    // Formats one reminder as a single line with id, text, due time and status.
    public static string RenderReminderLine(MemoryNote note)
    {
        var due = note.ReminderAtUtc?.ToLocalTime().ToString("yyyy-MM-dd h:mm tt") ?? "unscheduled";
        var text = string.IsNullOrWhiteSpace(note.ReminderText) ? note.Content : note.ReminderText;
        return $"- {ShortId(note.Id)} | {text} | due {due} | status {MemoryPolicies.NormalizeReminderStatus(note.ReminderStatus)}";
    }

    // Collects the current context (active app and window, time of day, location) to store with a memory. If the desktop cannot be read,
    // it still returns a basic snapshot so saving a note never fails because of it.
    public static async Task<InteractionContextSnapshot> BuildInteractionContextAsync(
        ToolContext context,
        string recentActivity,
        CancellationToken cancellationToken)
    {
        try
        {
            var desktopContext = await context.DesktopAutomation.GetContextSnapshotAsync(cancellationToken);
            return MemoryAutomation.BuildInteractionContext(
                desktopContext,
                ResolveLocation(context),
                [recentActivity]);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new InteractionContextSnapshot(
                DateTimeOffset.UtcNow,
                string.Empty,
                ResolveLocation(context),
                string.Empty,
                string.Empty,
                [],
                [recentActivity]);
        }
    }

    // Builds the context used to rank memory search results, from the query plus the current desktop context.
    public static async Task<MemoryQueryContext> BuildMemoryQueryContextAsync(
        ToolContext context,
        string query,
        CancellationToken cancellationToken)
    {
        var interactionContext = await BuildInteractionContextAsync(context, query, cancellationToken);
        return new MemoryQueryContext(
            query,
            interactionContext.TimeOfDay,
            interactionContext.Location,
            interactionContext.ActiveApp,
            interactionContext.ActiveWindow,
            interactionContext.SafeRecentActivity);
    }

    // The default weather location from settings, used as the user's rough location.
    private static string ResolveLocation(ToolContext context)
    {
        return string.IsNullOrWhiteSpace(context.Options.DefaultWeatherLocation)
            ? string.Empty
            : context.Options.DefaultWeatherLocation.Trim();
    }

    // The first 8 characters of an id, which is enough to refer to a memory.
    private static string ShortId(string id)
    {
        return string.IsNullOrWhiteSpace(id)
            ? "n/a"
            : id.Length <= 8
                ? id
                : id[..8];
    }
}
