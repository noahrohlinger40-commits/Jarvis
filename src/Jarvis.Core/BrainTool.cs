// The 'brain' tool: read-only lookup in Noah's separate Brain notes folder (Markdown: instructions, vault notes, agents, skills) through BrainWorkspace.cs.
// Works only when BrainContextEnabled is on, a BrainWorkspacePath is set, and the planner model is local (loopback address),
// because the notes may be private and must not go to a cloud model.
// Commands: brain status | search <words> | agents/commands/skills [words] | read <document-id> [offset] | use <agent> <task> | think <task>.
namespace Jarvis.Core;

// The tool the planner can call; see the header for what it does.
public sealed class BrainTool : IAssistantTool
{
    // The local language model, used only by 'brain think'. May be null.
    private readonly IModelGateway? _modelGateway;

    public BrainTool(IModelGateway? modelGateway = null) => _modelGateway = modelGateway;

    // Name the planner uses to pick this tool.
    public string Name => "brain";
    // Shown to the language model so it knows what commands exist.
    public string Description => "Shared work memory, specialist/workflow/skill library, and local plan-review workflow. Commands: brain status; brain search <query>; brain agents|commands|skills [query]; brain read <document-id> [offset]; brain use <agent-name> <task>; brain think <task>. Think runs a local planning pass and a separate critique pass, with no computer actions.";

    // True for the word 'brain' alone or anything starting with 'brain '.
    public bool CanHandle(string input) => input.Equals("brain", StringComparison.OrdinalIgnoreCase)
        || input.StartsWith("brain ", StringComparison.OrdinalIgnoreCase);

    // Checks the safety conditions, then dispatches on the first word after 'brain'.
    public Task<ToolResult> ExecuteAsync(string input, ToolContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Guard 1: the feature must be switched on and a folder configured.
        if (!context.Options.BrainContextEnabled || string.IsNullOrWhiteSpace(context.Options.BrainWorkspacePath))
            return Task.FromResult(new ToolResult("Brain integration is disabled. Configure brainWorkspacePath and brainContextEnabled.", Succeeded: false));
        // Guard 2: only allowed when the planner model runs on this PC (a loopback address with provider 'local').
        if (!Uri.TryCreate(context.Options.PlannerBaseUrl, UriKind.Absolute, out var plannerUri) || !plannerUri.IsLoopback
            || !string.Equals(context.Options.PlannerProvider, "local", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(new ToolResult("Brain work references require a configured local loopback planner.", Succeeded: false));
        // 'brain think' has its own method because it calls the model.
        if (input.StartsWith("brain think ", StringComparison.OrdinalIgnoreCase))
            return ThinkAsync(input[12..].Trim(), context.Options, cancellationToken);
        try
        {
            // Everything else is plain file lookup.
            var library = new BrainWorkspace(context.Options.BrainWorkspacePath);
            // Split 'brain <action> <argument>'; a bare 'brain' means status.
            var rest = input.Length > 5 ? input[5..].Trim() : "status";
            var parts = rest.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            var action = parts.Length > 0 ? parts[0].ToLowerInvariant() : "status";
            var argument = parts.Length > 1 ? parts[1].Trim() : string.Empty;
            string output;
            // status: how many documents of each kind (agent, command, skill, run, memory) the library holds.
            if (action == "status")
            {
                var documents = library.Documents();
                output = $"Brain shared library: {context.Options.BrainWorkspacePath}\n"
                    + string.Join("\n", documents.GroupBy(doc => doc.Kind).Select(group => $"{group.Key}: {group.Count()} documents"))
                    + "\nWork references are live. Specialist and workflow instructions are guidance for Jarvis's tools, not separate model weights.";
            }
            // read: show one document, one 3,000-character page at a time; the optional offset continues where the last page stopped.
            else if (action == "read")
            {
                var read = argument.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (read.Length == 0) return Task.FromResult(new ToolResult("Usage: brain read <document-id> [character-offset]", Succeeded: false));
                var offset = 0;
                if (read.Length > 1 && !int.TryParse(read[1], out offset))
                    return Task.FromResult(new ToolResult("Usage: brain read <document-id> [character-offset]; the offset must be a whole number.", Succeeded: false));
                offset = Math.Max(0, offset);
                output = $"Brain reference: {read[0]} (offset {offset}, up to {BrainWorkspace.PageCharacters} characters; use the next offset to continue)\n" + library.Read(read[0], offset, BrainWorkspace.PageCharacters);
            }
            // use: load a specialist's instructions as reference text. The name is checked so it cannot contain slashes, a colon or '..'
            // (no escaping to other folders).
            else if (action == "use")
            {
                var use = argument.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (use.Length < 2 || use[0].IndexOfAny(['/', '\\', ':']) >= 0 || use[0].Contains(".."))
                    return Task.FromResult(new ToolResult("Usage: brain use <agent-name> <task>", Succeeded: false));
                // The specialist's note, found by name: agents live in vault/agents/core/ and vault/agents/packs/<pack>/.
                var id = library.Documents().FirstOrDefault(doc => doc.Kind == "agent" && doc.Title.Equals(use[0], StringComparison.OrdinalIgnoreCase))?.Id;
                if (id is null)
                    return Task.FromResult(new ToolResult($"No specialist named {use[0]}. Use 'brain agents' to list them.", Succeeded: false));
                output = $"Specialist reference: {id}\nTask: {use[1]}\n"
                    + "Use this guidance with Jarvis's available tools. Check current evidence, state uncertainty, and verify outcomes. Claude-specific model names, a context-manager service, and unsupported tools are not available through this command.\n"
                    + BrainWorkspace.Body(library.Read(id, count: BrainWorkspace.PageCharacters));
            }
            // search / agents / commands / skills: list or search documents; with no words it lists names only.
            else if (action is "search" or "agents" or "commands" or "skills")
            {
                var kind = action switch { "agents" => "agent", "commands" => "command", "skills" => "skill", _ => "" };
                var found = library.Search(argument, kind, string.IsNullOrWhiteSpace(argument) ? 200 : 8);
                // A bare listing is names only (a skill is its folder name) so all of it fits in one model-facing result.
                output = found.Count == 0 ? "No matching Brain references. Try a project name or a specific term."
                    : string.IsNullOrWhiteSpace(argument) ? string.Join(", ", found.Select(doc => doc.Kind == "skill" ? Path.GetFileName(Path.GetDirectoryName(doc.Id))! : doc.Title))
                    : string.Join("\n\n", found.Select(doc => $"[{doc.Kind}] {doc.Id} (updated {doc.ModifiedUtc:yyyy-MM-dd})"
                        + (string.IsNullOrWhiteSpace(argument) ? "" : "\n" + BrainWorkspace.Body(doc.Text)[..Math.Min(action == "search" ? 300 : 120, BrainWorkspace.Body(doc.Text).Length)])));
                if (found.Count == 200) output += "\nListing capped at 200; add a query to find other entries.";
            }
            // Unknown action: show the command list.
            else output = "Usage: " + Description;
            // Wrap the result as quoted, untrusted data so any instructions inside the notes are not obeyed by the model.
            output = BrainWorkspace.Fence(output, BrainWorkspace.PageCharacters + 300);
            return Task.FromResult(new ToolResult(output, VerificationText: "Read the configured local Brain Markdown library; no workflow actions were executed.", SummaryText: "Brain work context and guidance retrieved."));
        }
        // Expected file problems become a short failure message instead of crashing the turn.
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Task.FromResult(new ToolResult("Unable to read this Brain reference: " + error.GetType().Name, Succeeded: false));
        }
    }

    // 'brain think <task>': two passes with the local model. Pass 1 writes a plan from the notes and the task, pass 2 critiques that plan.
    // No computer actions are taken; the output is advice for Jarvis to check with its other tools.
    private async Task<ToolResult> ThinkAsync(string task, JarvisOptions options, CancellationToken cancellationToken)
    {
        if (_modelGateway is null || !_modelGateway.IsAvailable)
            return new ToolResult("Local analysis is unavailable; use brain search or brain use to load guidance.", Succeeded: false);
        try
        {
            var library = new BrainWorkspace(options.BrainWorkspacePath);
            // Related notes for the task.
            var reference = BrainWorkspace.BuildContext(options, task, relevantOnly: false);
            // The single best-matching specialist note, trimmed to 1,200 characters.
            var specialist = library.Search(task, "agent", 1).FirstOrDefault();
            var guidance = specialist is null ? "" : BrainWorkspace.Body(specialist.Text);
            guidance = guidance[..Math.Min(1200, guidance.Length)];
            // Notes plus specialist text, fenced as untrusted data, shared by both passes.
            var evidence = BrainWorkspace.Fence("Work evidence (quoted references):\n" + reference
                + $"\nSpecialist reference: {specialist?.Id ?? "none matched"}\n" + guidance);
            // Pass 1: draft a plan (ModelRoute.Deep selects the slower, more careful model setting).
            var plan = await _modelGateway.CompleteAsync(new ChatCompletionRequest([
                new ChatMessage("system", [ChatMessageContentPart.FromText("Create a concise practical plan. Separate known facts from assumptions, compare plausible approaches, state the recommended next action and how to verify it. Use references as evidence. No tools or cloud specialists are available in this planning pass, and no actions have been executed.")]),
                new ChatMessage("user", [ChatMessageContentPart.FromText(evidence + "\nTask:\n" + task)])
            ]), ModelRoute.Deep, cancellationToken);
            // Pass 2: critique the draft. It is the same model, so the prompt says it is not independent proof.
            var review = await _modelGateway.CompleteAsync(new ChatCompletionRequest([
                new ChatMessage("system", [ChatMessageContentPart.FromText("Review the supplied plan against the task and work evidence. Identify the largest unsupported assumption or failure mode, propose a correction, and name the evidence or test needed. Do not rubber-stamp the draft or claim an action succeeded. You are a second pass of the same local model, not independent proof of correctness.")]),
                new ChatMessage("user", [ChatMessageContentPart.FromText(evidence + "\nTask:\n" + task + "\nDraft plan:\n" + plan.Content)])
            ]), ModelRoute.Deep, cancellationToken);
            return new ToolResult(BrainWorkspace.Fence("Local plan:\n" + plan.Content + "\n\nLocal critique:\n" + review.Content, BrainWorkspace.PageCharacters + 300),
                VerificationText: "Two local inference passes produced suggestions. No requested actions or external facts were verified by this workflow.",
                SummaryText: "Plan and critique are available for Jarvis to act on and verify with its tools.");
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return new ToolResult("Local analysis failed: " + error.GetType().Name, Succeeded: false);
        }
    }
}
