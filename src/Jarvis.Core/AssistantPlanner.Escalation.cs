// One slice of the StructuredAssistantPlanner class (see AssistantPlanner.cs): the narrow hand-off to the bigger model.
// Jarvis's own model always answers first. When the planner route ends stuck (the honesty gate replaced the reply, the model gave up,
// every iteration was used, or it gave no usable reply) and nothing that changes the computer has succeeded yet, the same request runs
// once more on the escalation gateway (LM Studio's gpt-oss-20b by default) through the same tools, approval gates and honesty gate.
// Every hand-off, and every stuck turn that was not handed off, is appended to data/escalations.jsonl: the list of what Jarvis cannot do yet.
using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Jarvis.Core;

/// <summary>Filled in by the planner loop: why the turn ended stuck. The first reason noted wins.</summary>
internal sealed class StuckNotes
{
    public string? Reason { get; private set; }

    public void Note(string reason) => Reason ??= reason;
}

internal sealed partial class StructuredAssistantPlanner
{
    // Seams for the selftest and the eval harness, which swap in a temp log file, a fake server start and a short budget.
    // Normal code never changes them.
    internal static string EscalationLogPath = JarvisDataPaths.Resolve("data/escalations.jsonl");
    internal static Func<string, bool, CancellationToken, Task<bool>> EnsureEscalationServerAsync = EscalationServer.EnsureAsync;
    // The whole hand-off (server start plus every model and tool step) gets this long; then the own reply stays.
    internal static TimeSpan EscalationBudget = TimeSpan.FromSeconds(150);

    private const string Redacted = "(sensitive, not saved)";
    // A plain "I can't do that yet" is listed in the log but not handed off: in testing, those hand-offs mostly
    // cost a minute for the same answer, and one opened an app on a question.
    internal const string GaveUpReason = "the model said it cannot do this yet";
    private static readonly object EscalationLogLock = new();
    private static readonly JsonSerializerOptions EscalationLogJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // Called after a stuck planner turn. Returns the own model's result unless the escalation produced a usable one.
    // Does nothing when escalation is off. Never escalates a turn that already changed something, is waiting for approval, or ran brain think.
    private async Task<AssistantPlanningResult> EscalateIfStuckAsync(
        AssistantPlanningRequest request,
        string sessionId,
        IntentDecision intentDecision,
        Func<PlannerToolCall, CancellationToken, Task<PlannerToolResult>> executeToolAsync,
        IProgress<AssistantActivityEvent> activity,
        AssistantPlanningResult own,
        string reason,
        CancellationToken cancellationToken)
    {
        if (_escalation is null)
        {
            return own;
        }

        var sensitive = MemoryAutomation.LooksSensitive(request.UserInput);
        var entry = new Dictionary<string, object?>
        {
            ["time"] = DateTimeOffset.Now.ToString("o"),
            ["request"] = request.UserInput,
            ["stuck"] = reason,
            ["ownReply"] = own.Response,
            ["ownTools"] = DescribeTools(own.ToolResults)
        };

        var blocked = reason == GaveUpReason ? "listed only: Jarvis said it can't do this yet" : WhyNotToEscalate(own.ToolResults);

        if (blocked is not null)
        {
            entry["escalated"] = false;
            entry["notEscalatedReason"] = blocked;
            WriteEscalationLog(entry, sensitive);
            return own;
        }

        entry["escalated"] = true;
        entry["model"] = _options.PlannerEscalationModel;
        activity.Report(new AssistantActivityEvent(
            AssistantActivityKind.Status,
            "I'm stuck on this, so I'm handing it to the bigger model. That can take a minute or two.",
            sessionId,
            Detail: reason));

        var clock = Stopwatch.StartNew();
        // One time limit for the whole hand-off; the caller's own cancellation still wins.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(EscalationBudget);
        // What the bigger model's tools returned, kept here because a timeout throws the planner's own list away.
        var ran = new List<PlannerToolResult>();
        Func<PlannerToolCall, CancellationToken, Task<PlannerToolResult>> recordingTool = async (call, token) =>
        {
            var result = await executeToolAsync(call, token);
            lock (ran) ran.Add(result);
            return result;
        };

        try
        {
            if (!await EnsureEscalationServerAsync(_options.PlannerEscalationBaseUrl, _options.LocalAiOnly, budget.Token))
            {
                entry["succeeded"] = false;
                entry["error"] = "the escalation server did not start or answer";
                return own;
            }

            var notes = new StuckNotes();
            var bigger = await _escalation.ExecutePlannedSessionAsync(
                request, sessionId, intentDecision, recordingTool, activity, null, false, budget.Token, notes);
            entry["escalationReply"] = bigger.Response;
            entry["escalationTools"] = DescribeTools(bigger.ToolResults);
            entry["succeeded"] = notes.Reason is null;

            if (notes.Reason is not null)
            {
                entry["error"] = "the bigger model was stuck too: " + notes.Reason;
            }

            // If it changed something and then got stuck, its reply says so and is the honest one.
            return notes.Reason is null || bigger.ToolResults.Any(ToolSafetyService.ActedSuccessfully) ? bigger : OwnWithActedNote(own, ran, entry);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            entry["succeeded"] = false;
            entry["error"] = "cancelled";
            throw;
        }
        catch (OperationCanceledException)
        {
            entry["succeeded"] = false;
            entry["error"] = $"out of time ({EscalationBudget.TotalSeconds:0} s)";
            return OwnWithActedNote(own, ran, entry);
        }
        catch (Exception exception)
        {
            entry["succeeded"] = false;
            entry["error"] = TrimError(exception.Message);
            return OwnWithActedNote(own, ran, entry);
        }
        finally
        {
            entry["seconds"] = Math.Round(clock.Elapsed.TotalSeconds, 1);
            WriteEscalationLog(entry, sensitive);
        }
    }

    // The reason a stuck turn is not handed off, or null when it may be: something already changed (repeating it would do it twice),
    // an approval is waiting (the user answers that), or brain think ran (that tool is meant to run on the own model).
    private static string? WhyNotToEscalate(IEnumerable<PlannerToolResult> results)
    {
        var list = results.ToArray();
        var acted = list.Where(ToolSafetyService.ActedSuccessfully).Select(result => result.ApiName).ToArray();

        if (acted.Length > 0)
        {
            return "an acting tool already succeeded (" + string.Join(", ", acted) + "), so the request is not run again";
        }

        if (list.Any(result => string.Equals(ResolveVerificationStatus(result), "awaiting_approval", StringComparison.OrdinalIgnoreCase)))
        {
            return "an approval is waiting for the user";
        }

        if (list.Any(result => (string.Equals(result.Command, "brain", StringComparison.OrdinalIgnoreCase) || string.Equals(result.ApiName, "brain", StringComparison.OrdinalIgnoreCase))
            && result.Input.TrimStart().StartsWith("think", StringComparison.OrdinalIgnoreCase)))
        {
            return "brain think stays on the own model";
        }

        return null;
    }

    // The own reply, plus one short line when the bigger model changed something before it was cut off, so nothing it did is hidden.
    private static AssistantPlanningResult OwnWithActedNote(AssistantPlanningResult own, List<PlannerToolResult> ran, Dictionary<string, object?> entry)
    {
        PlannerToolResult[] seen;
        lock (ran) seen = ran.ToArray();
        entry["escalationTools"] = DescribeTools(seen);
        var acted = seen.Where(ToolSafetyService.ActedSuccessfully).Select(result => $"{result.ApiName} {TruncateForContext(result.Input, 60)}".Trim()).ToArray();

        return acted.Length == 0
            ? own
            : own with { Response = own.Response + Environment.NewLine + "Note: the bigger model had already done this before it stopped: " + string.Join("; ", acted) + "." };
    }

    private static string[] DescribeTools(IEnumerable<PlannerToolResult> results) =>
        results.Select(result => $"{result.ApiName}({TruncateForContext(result.Input, 120)}){(result.IsError ? " failed" : string.Empty)}").ToArray();

    // One JSON line per entry; logging is best effort and never breaks a turn. A turn Jarvis would not save to memory
    // (a password, a key) has its words and every reply and tool input left out.
    private static void WriteEscalationLog(Dictionary<string, object?> entry, bool sensitive)
    {
        try
        {
            if (sensitive)
            {
                foreach (var key in new[] { "request", "ownReply", "escalationReply" })
                {
                    if (entry.ContainsKey(key)) entry[key] = Redacted;
                }

                foreach (var key in new[] { "ownTools", "escalationTools" })
                {
                    if (entry.ContainsKey(key)) entry[key] = new[] { Redacted };
                }
            }

            lock (EscalationLogLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(EscalationLogPath)!);
                File.AppendAllText(EscalationLogPath, JsonSerializer.Serialize(entry, EscalationLogJson) + Environment.NewLine);
            }
        }
        catch
        {
            // Best effort.
        }
    }
}

/// <summary>Makes sure LM Studio's local server answers, starting it with the lms command line tool at most once per call.</summary>
internal static class EscalationServer
{
    public static async Task<bool> EnsureAsync(string baseUrl, bool localAiOnly, CancellationToken cancellationToken)
    {
        if (await ReachableAsync(baseUrl, localAiOnly, cancellationToken))
        {
            return true;
        }

        var lms = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".lmstudio", "bin", "lms.exe");

        if (!File.Exists(lms) || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        {
            return false;
        }

        try
        {
            // No pipes: lms starts a background service, which could keep redirected output open long after lms itself is done.
            var startInfo = new ProcessStartInfo { FileName = lms, UseShellExecute = false, CreateNoWindow = true };

            foreach (var argument in new[] { "server", "start", "--port", uri.Port.ToString() })
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo);

            if (process is null)
            {
                return false;
            }

            try
            {
                using var startTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                startTimeout.CancelAfter(TimeSpan.FromSeconds(20));
                await process.WaitForExitAsync(startTimeout.Token);

                // A broken or half-installed LM Studio says so by its exit code; no reason to wait out the poll below.
                if (process.ExitCode != 0)
                {
                    return false;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The 20 s start limit passed; the finally below stops the leftover process and the poll still gets its chance.
            }
            finally
            {
                // Still running here means the start timed out or Jarvis was cancelled mid-wait.
                try { if (!process.HasExited) process.Kill(); } catch { }
            }
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        // Up to 20 s for the server to answer after the start command.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await ReachableAsync(baseUrl, localAiOnly, cancellationToken))
            {
                return true;
            }

            await Task.Delay(1000, cancellationToken);
        }

        return false;
    }

    private static async Task<bool> ReachableAsync(string baseUrl, bool localAiOnly, CancellationToken cancellationToken)
    {
        try
        {
            using var client = LocalAiPolicy.CreateHttpClient(localAiOnly, TimeSpan.FromSeconds(3));
            using var response = await client.GetAsync(new Uri(baseUrl).GetLeftPart(UriPartial.Authority) + "/v1/models", cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }
}
