// The 'memory' tool: lets the user (or the model on their behalf) inspect a stored memory and change how long it is kept or how
// important it is. Written commands look like 'memory review <id or words>' or 'memory set retention <id or words> :: forever'.
// It is one of the tools the planner can call (see Tools.cs / IAssistantTool) and works through context.MemoryStore (JsonMemoryStore).
// It cannot create or delete memories.
namespace Jarvis.Core;

// The tool object the planner registers. CanHandle says whether a typed command is a memory command; ExecuteAsync runs it.
public sealed class MemoryTool : IAssistantTool
{
    public string Name => "memory";

    public string Description => "Review a memory or change its retention or importance. Use `memory review`, `memory set retention`, or `memory set importance`.";

    // True for anything starting with the word 'memory'.
    public bool CanHandle(string input) => MemoryCommandParsing.TryParse(input, out _);

    // Parses the command, then dispatches to review / set retention / set importance (anything else shows the help text).
    public async Task<ToolResult> ExecuteAsync(string input, ToolContext context, CancellationToken cancellationToken)
    {
        if (!MemoryCommandParsing.TryParse(input, out var command))
        {
            return BuildUsageResult();
        }

        switch (command.Action)
        {
            case "help":
                return BuildUsageResult();
            case "review":
                return await ReviewMemoryAsync(command.Target, context, cancellationToken);
            case "set-retention":
                return await SetRetentionAsync(command.Target, command.Payload, context, cancellationToken);
            case "set-importance":
                return await SetImportanceAsync(command.Target, command.Payload, context, cancellationToken);
            default:
                return BuildUsageResult();
        }
    }

    // The help text listing the commands.
    private static ToolResult BuildUsageResult()
    {
        return new ToolResult(
            string.Join(
                Environment.NewLine,
                "Memory commands:",
                "- memory review <id-or-phrase>",
                "- memory set retention <id-or-phrase> :: <short|standard|episodic|rolling|long|forever|until-complete>",
                "- memory set importance <id-or-phrase> :: <5-100>"),
            VerificationText: "Displayed memory command usage.",
            SummaryText: "Showed memory command usage.");
    }

    // Shows every field of one memory (kind, importance, expiry, reminder, tags, entities, text).
    private static async Task<ToolResult> ReviewMemoryAsync(
        string target,
        ToolContext context,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveSingleMemoryAsync(target, context, cancellationToken);

        if (resolved.ErrorResult is not null)
        {
            return resolved.ErrorResult;
        }

        var note = resolved.Note!;
        var lines = new List<string>
        {
            $"Memory {note.Id}:",
            $"- Kind: {note.Kind}",
            $"- Category: {OrNone(note.Category)}",
            $"- Source: {OrNone(note.Source)}",
            $"- Privacy: {OrNone(note.Privacy)}",
            $"- Created: {note.CreatedAtUtc:yyyy-MM-dd HH:mm}Z",
            $"- Last observed: {(note.LastObservedAtUtc ?? note.CreatedAtUtc):yyyy-MM-dd HH:mm}Z",
            $"- Observations: {note.ObservationCount}",
            $"- Importance: {Math.Round(note.ImportanceScore * 100d)}",
            $"- Retention: {OrNone(note.RetentionPolicy)}",
            $"- Expires: {(note.ExpiresAtUtc is null ? "none" : note.ExpiresAtUtc.Value.ToString("yyyy-MM-dd HH:mm") + "Z")}",
            $"- Reminder: {(note.ReminderAtUtc is null ? "none" : $"{note.ReminderAtUtc.Value.ToLocalTime():yyyy-MM-dd h:mm tt} | {MemoryPolicies.NormalizeReminderStatus(note.ReminderStatus)}")}",
            $"- Tags: {(note.Tags is { Length: > 0 } ? string.Join(", ", note.Tags) : "none")}",
            $"- Context: {(note.Context is null ? "none" : note.Context.ToInlineText())}",
            "- Content:",
            note.Content
        };

        if (note.Entities is { Length: > 0 })
        {
            lines.Add("- Entities:");
            lines.AddRange(note.Entities.Select(entity => $"  {entity.ToInlineText()}"));
        }

        return new ToolResult(
            string.Join(Environment.NewLine, lines),
            VerificationText: $"Reviewed memory `{ShortId(note.Id)}`.",
            SummaryText: "Reviewed one memory.");
    }

    // Changes how long a memory is kept. Counts as the user approving retention, which protects the note from being shortened by later repeats.
    private static async Task<ToolResult> SetRetentionAsync(
        string target,
        string payload,
        ToolContext context,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveSingleMemoryAsync(target, context, cancellationToken);

        if (resolved.ErrorResult is not null)
        {
            return resolved.ErrorResult;
        }

        var normalizedRetention = MemoryPolicies.NormalizeRetentionPolicy(payload);

        if (string.IsNullOrWhiteSpace(normalizedRetention))
        {
            return new ToolResult(
                "Usage: memory set retention <id-or-phrase> :: <short|standard|episodic|rolling|long|forever|until-complete>",
                Succeeded: false,
                SummaryText: "Missing retention value.");
        }

        var note = resolved.Note!;
        var updated = await context.MemoryStore.UpdateAsync(
            BuildUpdateRequest(
                note,
                retentionPolicy: normalizedRetention,
                expiresAtUtc: null,
                userApprovedRetention: true),
            cancellationToken);

        return updated is null
            ? new ToolResult(
                $"Unable to update retention for {ShortId(note.Id)}.",
                Succeeded: false,
                VerificationText: "The memory could not be updated.",
                SummaryText: "Memory retention update failed.")
            : new ToolResult(
                $"Updated retention for {ShortId(updated.Id)} to {normalizedRetention}.",
                VerificationText: $"Retention for `{ShortId(updated.Id)}` is now `{normalizedRetention}`.",
                SummaryText: "Updated memory retention.");
    }

    // Changes a memory's importance score (see TryParseImportance for accepted values).
    private static async Task<ToolResult> SetImportanceAsync(
        string target,
        string payload,
        ToolContext context,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveSingleMemoryAsync(target, context, cancellationToken);

        if (resolved.ErrorResult is not null)
        {
            return resolved.ErrorResult;
        }

        if (!TryParseImportance(payload, out var importance))
        {
            return new ToolResult(
                "Usage: memory set importance <id-or-phrase> :: <5-100>",
                Succeeded: false,
                SummaryText: "Missing importance value.");
        }

        var note = resolved.Note!;
        var updated = await context.MemoryStore.UpdateAsync(
            BuildUpdateRequest(note, importanceScore: importance),
            cancellationToken);

        return updated is null
            ? new ToolResult(
                $"Unable to update importance for {ShortId(note.Id)}.",
                Succeeded: false,
                VerificationText: "The memory could not be updated.",
                SummaryText: "Memory importance update failed.")
            : new ToolResult(
                $"Updated importance for {ShortId(updated.Id)} to {Math.Round(updated.ImportanceScore * 100d)}.",
                VerificationText: $"Importance for `{ShortId(updated.Id)}` is now `{Math.Round(updated.ImportanceScore * 100d)}`.",
                SummaryText: "Updated memory importance.");
    }

    // Finds the one memory the user means. First tries an exact id or id prefix; otherwise searches by words and takes the top hit,
    // but refuses (listing the candidates) when several match and the best one is not an exact text match.
    private static async Task<ResolvedMemory> ResolveSingleMemoryAsync(
        string target,
        ToolContext context,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return new ResolvedMemory(null, new ToolResult("A memory id or search phrase is required.", Succeeded: false, SummaryText: "Missing memory target."));
        }

        // Step 1: id or id-prefix match against all active memories.
        var notes = await context.MemoryStore.GetAllAsync(cancellationToken);
        var directMatch = notes.FirstOrDefault(note =>
            string.Equals(note.Id, target, StringComparison.OrdinalIgnoreCase)
            || note.Id.StartsWith(target, StringComparison.OrdinalIgnoreCase));

        if (directMatch is not null)
        {
            return new ResolvedMemory(directMatch, null);
        }

        // Step 2: no id match, so treat the target as search words (top 5 results).
        var searchContext = await ToolInputHelper.BuildMemoryQueryContextAsync(context, target, cancellationToken);
        var searched = await context.MemoryStore.SearchAsync(target, searchContext, 5, cancellationToken);

        if (searched.Count == 0)
        {
            return new ResolvedMemory(
                null,
                new ToolResult(
                    $"No stored memory matched \"{target}\".",
                    Succeeded: false,
                    VerificationText: "The requested memory target did not match any active memory.",
                    SummaryText: "No matching memory."));
        }

        if (searched.Count > 1 && !string.Equals(searched[0].Content, target, StringComparison.OrdinalIgnoreCase))
        {
            return new ResolvedMemory(
                null,
                new ToolResult(
                    "More than one memory matched. Use a longer phrase or an id prefix:" + Environment.NewLine +
                    string.Join(Environment.NewLine, searched.Select(ToolInputHelper.RenderMemoryLine)),
                    Succeeded: false,
                    VerificationText: "The memory target was ambiguous.",
                    SummaryText: "Ambiguous memory target."));
        }

        return new ResolvedMemory(searched[0], null);
    }

    // Copies a note's current values into an update request, replacing only the fields passed in (UpdateAsync needs all fields).
    private static MemoryUpdateRequest BuildUpdateRequest(
        MemoryNote note,
        double? importanceScore = null,
        string? retentionPolicy = null,
        DateTimeOffset? expiresAtUtc = null,
        bool? userApprovedRetention = null,
        StructuredMemoryReference[]? entities = null)
    {
        return new MemoryUpdateRequest(
            note.Id,
            note.Content,
            note.Kind,
            note.Category,
            note.Privacy,
            note.Tags ?? Array.Empty<string>(),
            importanceScore ?? note.ImportanceScore,
            retentionPolicy ?? note.RetentionPolicy,
            expiresAtUtc,
            userApprovedRetention ?? note.UserApprovedRetention,
            note.ReminderAtUtc,
            note.ReminderText,
            note.ReminderStatus,
            entities ?? note.Entities);
    }

    // Accepts '5' to '100', optionally with a % sign, or a fraction like 0.7, and converts to the 0-1 scale. A value above 1 is read as a
    // percent, so '1' means 100% (not 1%). The result is clamped to 0.05-1.
    private static bool TryParseImportance(string payload, out double importance)
    {
        importance = 0;

        if (string.IsNullOrWhiteSpace(payload))
        {
            return false;
        }

        var trimmed = payload.Trim().TrimEnd('%');

        if (!double.TryParse(trimmed, out var raw))
        {
            return false;
        }

        importance = raw > 1d ? raw / 100d : raw;
        importance = MemoryPolicies.ClampImportance(importance);
        return true;
    }

    private static string OrNone(string value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim();

    // First 8 characters of the id, which is enough for people to read.
    private static string ShortId(string id) => string.IsNullOrWhiteSpace(id) ? "n/a" : id.Length <= 8 ? id : id[..8];

    // Either the memory that was found, or the error message to show instead.
    private sealed record ResolvedMemory(MemoryNote? Note, ToolResult? ErrorResult);
}

// A parsed command: the action name, the memory it targets, and the value after '::' (if any).
internal sealed record MemoryCommand(
    string Action,
    string Target = "",
    string Payload = "");

// Turns typed text into a MemoryCommand.
internal static class MemoryCommandParsing
{
    // Returns true for anything that starts with 'memory'. Unrecognized sub-commands become 'help' rather than an error.
    public static bool TryParse(string input, out MemoryCommand command)
    {
        if (ToolInputHelper.MatchesExact(input, "memory", "memory help", "memory commands"))
        {
            command = new MemoryCommand("help");
            return true;
        }

        if (!ToolInputHelper.StartsWithCommand(input, "memory"))
        {
            command = new MemoryCommand(string.Empty);
            return false;
        }

        var tail = ToolInputHelper.GetCommandTail(input, "memory");

        if (string.IsNullOrWhiteSpace(tail))
        {
            command = new MemoryCommand("help");
            return true;
        }

        if (ToolInputHelper.StartsWithCommand(tail, "review"))
        {
            command = new MemoryCommand("review", ToolInputHelper.GetCommandTail(tail, "review"));
            return true;
        }

        if (TryParseTargetAndPayload(tail, "set retention", out command, "set-retention"))
        {
            return true;
        }

        if (TryParseTargetAndPayload(tail, "set importance", out command, "set-importance"))
        {
            return true;
        }

        command = new MemoryCommand("help");
        return true;
    }

    // Splits '<target> :: <value>' on the first '::' (for set retention / set importance).
    private static bool TryParseTargetAndPayload(
        string tail,
        string prefix,
        out MemoryCommand command,
        string action)
    {
        if (!ToolInputHelper.StartsWithCommand(tail, prefix))
        {
            command = new MemoryCommand(string.Empty);
            return false;
        }

        var remainder = ToolInputHelper.GetCommandTail(tail, prefix);
        var parts = remainder.Split(["::"], 2, StringSplitOptions.None | StringSplitOptions.TrimEntries);
        command = parts.Length < 2
            ? new MemoryCommand(action, remainder.Trim())
            : new MemoryCommand(action, parts[0].Trim(), parts[1].Trim());
        return true;
    }
}
