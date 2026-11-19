using System.Text.Json;
using System.Text.RegularExpressions;

namespace Jarvis.Core;

// "Teach me" tutor: points at real controls in the app Noah is using, one step at a time, and remembers
// the lesson. This file is the pure half (steps, prompt, parsing, matching, store, driver); the screen
// half (UI Automation, the pointer window, the click poll) lives in Jarvis.App behind ITutorHost.

// In short: you say "teach me how to insert a table"; Jarvis asks the model for the next step, draws a pointer on the
// real button (found through Windows UI Automation), waits for the click, and repeats. Finished lessons are saved and
// replayed later without the model. Flow: TeachTool (chat command) -> TutorService (runs a lesson in the background)
// -> TutorDriver (the step loop) -> TutorLogic (prompt, parsing, matching) and TutorLessonStore (file), with ITutorHost for the screen.
// A rectangle on the screen in pixels (the screen position of a control or window).
public readonly record struct TutorRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;

    public int Height => Bottom - Top;

    public bool IsEmpty => Width <= 0 || Height <= 0;

    // True if the point is inside the rectangle, optionally widened by margin pixels on every side.
    public bool Contains(int x, int y, int margin = 0) =>
        x >= Left - margin && x < Right + margin && y >= Top - margin && y < Bottom + margin;
}

// The app window being taught: its Windows handle, program name (such as WINWORD.EXE) and title.
public sealed record TutorWindow(long Handle, string Exe, string Title);

// One clickable control currently visible in the window, as reported by UI Automation (name, kind such as Button, id, position).
public sealed record TutorElement(string Name, string ControlType, string AutomationId, TutorRect Rect);

// How a saved step identifies its control, without the position (positions change when the window moves).
public sealed record TutorStepElement(string Name, string ControlType, string AutomationId);

/// <param name="Kind">"click" or "type" (nothing to click: type or press keys, then say "next").</param>
/// <param name="Dx">The click point relative to the window's top-left; a fallback only.</param>
// One step of a lesson: the sentence to say, a short label by the pointer, the kind of step and the control to click.
public sealed record TutorStep(string Say, string Label, string Kind, TutorStepElement? Element, int Dx, int Dy);

// A saved lesson for one app and goal, with its steps in order.
public sealed record TutorLesson(string App, string Goal, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, TutorStep[] Steps);

// The model's decision for the next step: Pick is the number of the chosen control in the list (null when nothing is
// to be clicked), and Done means the goal is already reached.
public sealed record TutorPick(string Say, string Label, int? Pick, bool Done);

// The model's reply could not be used (bad JSON, missing text, a control number that is not in the list).
public sealed class TutorStepException(string message) : Exception(message);

/// <summary>The screen side failed (the window closed, no pointer window): the lesson cannot go on.</summary>
public sealed class TutorUnavailableException(string message) : Exception(message);

// What the screen side should show: where to point (null when the step has nothing to click), the label and the sentence.
public sealed record TutorPointer(TutorRect? Rect, string Label, string Say);

// How a step ended.
public enum TutorOutcomeKind
{
    /// <summary>Noah clicked the control that was pointed at.</summary>
    Confirmed,
    /// <summary>Noah clicked something else; Clicked says what (null when it could not be identified).</summary>
    Corrected,
    /// <summary>A step with nothing to click: Noah said "next".</summary>
    Next
}

// The result of one step: how it ended and where the click happened on screen.
public sealed record TutorOutcome(TutorOutcomeKind Kind, int X, int Y, TutorElement? Clicked = null);

/// <summary>The screen side of the tutor. Jarvis.App implements it; the checks script it.</summary>
public interface ITutorHost
{
    /// <summary>The window Noah was using: the foreground one, or the top one behind Jarvis if Jarvis has focus.</summary>
    TutorWindow? FindTargetWindow();

    /// <summary>Called once before a lesson: gets Jarvis's own window out of the way of the target.</summary>
    void Begin(TutorWindow window);

    void End();

    TutorRect GetWindowRect(TutorWindow window);

    /// <summary>On-screen, named, interactable elements, at most <see cref="TutorLogic.MaxElements"/>.</summary>
    IReadOnlyList<TutorElement> ListElements(TutorWindow window);

    /// <summary>Draws the pointer and waits for the click (or "next" when Rect is null). Cancelled = lesson stopped.</summary>
    Task<TutorOutcome> PointAsync(TutorWindow window, TutorPointer pointer, CancellationToken cancellationToken);

    void Next();
}

// The testable "brain" of the tutor: limits, the model prompt, reply parsing and lesson matching. No screen access.
public static class TutorLogic
{
    // Most steps in one lesson; a longer one is stopped and not saved.
    public const int MaxSteps = 15;
    // Most controls listed to the model per step, which keeps the prompt small enough for a small local model.
    public const int MaxElements = 150;
    // Pixels of slack around a control when deciding whether the click landed on it.
    public const int ClickMargin = 6;
    // Minimum word overlap (0 to 1) for a saved lesson to count as the same goal; see FindLesson.
    private const double MatchAt = 0.8; // "insert a row in table" (2/3) and "insert a table and merge cells" (1/2) must not replay "insert a table"

    // Filler and app-name words ignored when comparing goals, so "how do I insert a table in Word" equals "insert table".
    private static readonly HashSet<string> Stopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "and", "are", "can", "do", "does", "how", "i", "in", "is", "it", "me", "my", "of", "on", "or",
        "the", "to", "teach", "this", "that", "use", "using", "with", "you", "your",
        // App names: lessons are already scoped per exe.
        "app", "word", "excel", "powerpoint", "outlook", "teams", "onenote", "edge", "chrome", "browser", "sharepoint", "notepad", "windows"
    };

    // Matches the start of a request such as "teach me how to " or "teach me how can I ", so only the goal is left.
    // All parts after "teach" are optional: "me", "how", "do/can/would I", "to".
    private static readonly Regex GoalPrefix = new(
        @"^\s*teach(\s+me)?(\s+how)?(\s+(do|can|would)\s+i)?(\s+to)?\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // The model's instructions. It must answer with a single JSON object {say, label, pick, done}, picking a control by
    // its number from the list so it cannot invent buttons that do not exist.
    public const string SystemPrompt =
        "You are an on-screen tutor that teaches Windows software one step at a time. Each turn you get the user's goal, "
        + "the steps already done and a numbered list of the controls on screen right now. Reply with ONLY one JSON object, "
        + "no prose, no code fence: {\"say\": str, \"label\": str, \"pick\": int|null, \"done\": bool}. "
        + "\"say\" is one short sentence telling them what to do next, like \"Click Table to open the grid picker\". "
        + "\"label\" is 1-4 words shown beside the control. \"pick\" is the number of the control to click, taken from the "
        + "list only; never invent a control. If the next step is typing or pressing keys with nothing to click, set "
        + "\"pick\" to null and say exactly what to type or press. If the goal is already reached, reply {\"done\": true}. "
        + "Give only the single next step, never a list.";

    // True if the message starts with "teach me" (case-insensitive, whole word).
    public static bool IsTeachRequest(string input) =>
        Regex.IsMatch(input, @"^\s*teach\s+me\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>"teach me how to insert a table" -> "insert a table".</summary>
    public static string ExtractGoal(string input) => GoalPrefix.Replace(input, string.Empty).Trim().TrimEnd('.', '?', '!');

    // Builds the per-step message for the model: the goal, the app, the steps done so far, and a numbered list of the
    // controls on screen. When replaying a saved step whose control vanished, it also shows the old step so the model can find its replacement.
    public static string BuildPrompt(string goal, string exe, IReadOnlyList<string> done, IReadOnlyList<TutorElement> elements, TutorStep? old = null)
    {
        var lines = new List<string> { $"Goal: {goal}", $"App: {exe}", "Steps already done: " + (done.Count == 0 ? "none yet" : string.Empty) };
        lines.AddRange(done.Select((say, index) => $"{index + 1}. {say}"));

        if (old is not null)
        {
            lines.Add($"This lesson used to say: \"{old.Say}\" (label \"{old.Label}\"), but that control is not on screen any more. Find it in the list.");
        }

        lines.Add("Controls on screen:");
        lines.AddRange(elements.Select((element, index) => $"{index + 1}. {element.ControlType} \"{Shorten(element.Name)}\""));
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>The first {...} object in the reply. Tolerates prose and a code fence around it; anything unusable,
    /// or a pick outside the list, throws so the lesson ends with a message instead of pointing at nothing.</summary>
    public static TutorPick ParseStep(string text, int elementCount)
    {
        // Scan for each "{" and try to read a complete JSON object starting there. This tolerates text or a code fence
        // around the object, which small models often add.
        JsonElement? found = null;

        for (var start = text.IndexOf('{'); start >= 0 && found is null; start = text.IndexOf('{', start + 1))
        {
            try
            {
                var reader = new Utf8JsonReader(System.Text.Encoding.UTF8.GetBytes(text[start..]));

                if (JsonDocument.TryParseValue(ref reader, out var document) && document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    found = document.RootElement;
                }
            }
            catch (JsonException)
            {
            }
        }

        if (found is not { } root)
        {
            throw new TutorStepException("no JSON object in the reply");
        }

        if (root.TryGetProperty("done", out var doneElement) && doneElement.ValueKind == JsonValueKind.True)
        {
            return new TutorPick(string.Empty, string.Empty, null, true);
        }

        var say = root.TryGetProperty("say", out var sayElement) && sayElement.ValueKind == JsonValueKind.String ? sayElement.GetString()!.Trim() : string.Empty;

        if (say.Length == 0)
        {
            throw new TutorStepException("missing \"say\"");
        }

        var label = root.TryGetProperty("label", out var labelElement) && labelElement.ValueKind == JsonValueKind.String ? labelElement.GetString()!.Trim() : string.Empty;
        int? pick = null;

        if (root.TryGetProperty("pick", out var pickElement) && pickElement.ValueKind != JsonValueKind.Null)
        {
            // A small model may answer "3" or 3.0 for 3.
            var number = 0;
            var isNumber = pickElement.ValueKind == JsonValueKind.Number
                ? pickElement.TryGetDouble(out var real) && real == Math.Floor(real) && Math.Abs(real) < int.MaxValue && (number = (int)real) == real
                : pickElement.ValueKind == JsonValueKind.String && int.TryParse(pickElement.GetString(), out number);

            if (!isNumber || number < 1 || number > elementCount)
            {
                throw new TutorStepException("\"pick\" is not a number from the list");
            }

            pick = number;
        }

        return new TutorPick(say, label, pick, false);
    }

    /// <summary>Confirmed when the release lands on the pointed control (a small margin allowed), else Corrected.</summary>
    public static TutorOutcomeKind Classify(TutorRect rect, int x, int y) =>
        rect.Contains(x, y, ClickMargin) ? TutorOutcomeKind.Confirmed : TutorOutcomeKind.Corrected;

    /// <summary>By AutomationId when the saved step has one, else by name and control type.</summary>
    // Finds the saved step's control again on the current screen; null if it is not there (the app layout changed).
    public static TutorElement? FindElement(IReadOnlyList<TutorElement> elements, TutorStep step)
    {
        if (step.Element is not { } wanted)
        {
            return null;
        }

        if (wanted.AutomationId.Length > 0)
        {
            return elements.FirstOrDefault(e => e.AutomationId == wanted.AutomationId);
        }

        return elements.FirstOrDefault(e => wanted.Name.Length > 0
            && e.ControlType == wanted.ControlType
            && string.Equals(e.Name, wanted.Name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The saved lesson for this app whose goal shares enough words with the new one.
    /// ponytail: word overlap (Jaccard) misses synonyms ("add" vs "insert"), and MemorySemanticSearch scores memory
    /// notes, not goals; if that bites, hand the model the saved goals for this app and let it pick one.</summary>
    public static TutorLesson? FindLesson(IEnumerable<TutorLesson> lessons, string exe, string goal)
    {
        var want = GoalWords(goal);
        TutorLesson? best = null;
        var bestScore = MatchAt;

        foreach (var lesson in lessons.Where(l => l.Steps.Length > 0 && string.Equals(l.App, exe, StringComparison.OrdinalIgnoreCase)))
        {
            var have = GoalWords(lesson.Goal);
            // Score = shared words / all words (Jaccard similarity); 1.0 means identical word sets.
            var union = want.Union(have).Count();
            var score = union == 0 ? 0 : (double)want.Intersect(have).Count() / union;

            if (score >= bestScore)
            {
                best = lesson;
                bestScore = score;
            }
        }

        return best;
    }

    /// <summary>Lowercase content words with a trailing plural s stripped ("tables" equals "table").</summary>
    public static HashSet<string> GoalWords(string goal) =>
        Regex.Matches(goal.ToLowerInvariant(), "[a-z0-9]+")
            .Select(m => m.Value)
            .Where(word => !Stopwords.Contains(word))
            .Select(word => word.Length > 3 && word.EndsWith('s') && !word.EndsWith("ss") ? word[..^1] : word)
            .ToHashSet();

    // Long control names are cut to 60 characters in the prompt.
    private static string Shorten(string name) => name.Length <= 60 ? name : name[..60];
}

/// <summary>Saved lessons, one JSON list.</summary>
public sealed class TutorLessonStore(string path)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    // Returns all saved lessons (an empty list if there is no file or it cannot be read).
    public async Task<IReadOnlyList<TutorLesson>> LoadAsync(CancellationToken cancellationToken) =>
        (await TryLoadAsync(cancellationToken)).Lessons;

    /// <summary>Readable is false when the file exists but cannot be read or parsed.</summary>
    private async Task<(List<TutorLesson> Lessons, bool Readable)> TryLoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(path))
            {
                return ([], true);
            }

            await using var stream = File.OpenRead(path);
            return (await JsonSerializer.DeserializeAsync<List<TutorLesson>>(stream, JsonOptions, cancellationToken) ?? [], true);
        }
        // Cancellation is not a read failure; pass it on.
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return ([], false);
        }
    }

    /// <summary>Adds the lesson, or replaces the saved one for the same app and goal.</summary>
    public async Task SaveAsync(TutorLesson lesson, CancellationToken cancellationToken)
    {
        var (lessons, readable) = await TryLoadAsync(cancellationToken);

        if (!readable)
        {
            // Never silently drop saved lessons: keep the unreadable file beside the new one.
            File.Copy(path, $"{path}.bad-{DateTime.Now:yyyyMMdd-HHmmss}", overwrite: true);
        }

        lessons.RemoveAll(l => string.Equals(l.App, lesson.App, StringComparison.OrdinalIgnoreCase)
            && string.Equals(l.Goal, lesson.Goal, StringComparison.OrdinalIgnoreCase));
        lessons.Add(lesson);

        var directory = Path.GetDirectoryName(path);

        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Write to a temporary file first and then swap it in, so a crash mid-write cannot corrupt the saved lessons.
        var temp = path + ".tmp";

        await using (var stream = File.Create(temp))
        {
            await JsonSerializer.SerializeAsync(stream, lessons, JsonOptions, cancellationToken);
        }

        File.Move(temp, path, overwrite: true);
    }
}

/// <summary>One lesson, live or replayed. Live: one model call per step. Replay: none, except for a step whose
/// control is gone, which asks the model for just that step and rewrites the lesson at the end.</summary>
public sealed class TutorDriver(IModelGateway? model, ITutorHost host, TutorLessonStore store, Action<string> say, int settleMs = 800)
{
    // Runs one whole lesson and returns the final message to say. Each pass of the loop handles one step: it takes the
    // saved step if there is one, otherwise (or if the saved control is gone) asks the model, points at the control, waits for
    // the user's click, and records what actually happened. The lesson is saved at the end if it is new or changed.
    public async Task<string> RunAsync(string goal, CancellationToken cancellationToken)
    {
        var window = host.FindTargetWindow();

        if (window is null)
        {
            return "I couldn't find an app window to teach in. Click into the app first, then ask again.";
        }

        // A matching saved lesson means this is a replay: no model calls unless a control has moved.
        var saved = TutorLogic.FindLesson(await store.LoadAsync(cancellationToken), window.Exe, goal);
        var steps = new List<TutorStep>();
        var changed = false;
        var reached = false;
        host.Begin(window);

        try
        {
            say(saved is null ? $"Okay, let's {goal}. I'll point at each control." : $"I remember this one: {saved.Goal}.");

            for (var n = 0; n < TutorLogic.MaxSteps && !reached; n++)
            {
                var windowRect = host.GetWindowRect(window);
                var elements = host.ListElements(window);
                var old = saved is not null && n < saved.Steps.Length ? saved.Steps[n] : null;

                if (saved is not null && old is null)
                {
                    reached = true; // every saved step was played
                    break;
                }

                var plan = old;
                TutorElement? target = null;

                // Ask the model when there is no saved step, or when the saved click target cannot be found on screen any more.
                if (old is null || (old.Kind == "click" && (target = TutorLogic.FindElement(elements, old)) is null))
                {
                    var pick = await AskAsync(goal, window.Exe, steps, elements, old, cancellationToken);

                    if (pick.Done)
                    {
                        changed |= old is not null;
                        reached = true;
                        break;
                    }

                    changed |= old is not null;
                    target = pick.Pick is { } number ? elements[number - 1] : null;
                    plan = new TutorStep(pick.Say, pick.Label, target is null ? "type" : "click", null, 0, 0);
                }

                say(plan!.Say);
                var outcome = await host.PointAsync(window, new TutorPointer(target?.Rect, plan.Label, plan.Say), cancellationToken);

                if (plan.Kind == "type")
                {
                    steps.Add(plan);
                }
                else if (outcome.Kind == TutorOutcomeKind.Corrected)
                {
                    // Noah clicked something else: his click is the step.
                    changed = true;
                    var clicked = outcome.Clicked;
                    steps.Add(new TutorStep(
                        clicked is null ? "Click here" : $"Click {clicked.Name}",
                        clicked?.Name ?? string.Empty,
                        "click",
                        clicked is null ? null : new TutorStepElement(clicked.Name, clicked.ControlType, clicked.AutomationId),
                        outcome.X - windowRect.Left,
                        outcome.Y - windowRect.Top));
                }
                else
                {
                    steps.Add(plan with
                    {
                        Element = new TutorStepElement(target!.Name, target.ControlType, target.AutomationId),
                        Dx = outcome.X - windowRect.Left,
                        Dy = outcome.Y - windowRect.Top
                    });
                }

                await Task.Delay(settleMs, cancellationToken); // let the app redraw before the next look
            }

            if (!reached)
            {
                return $"That took more than {TutorLogic.MaxSteps} steps, so I stopped. I didn't save it.";
            }

            if (steps.Count > 0 && (saved is null || changed))
            {
                var now = DateTimeOffset.UtcNow;
                await store.SaveAsync(new TutorLesson(window.Exe, saved?.Goal ?? goal, saved?.CreatedAtUtc ?? now, now, steps.ToArray()), cancellationToken);
                return saved is null ? $"That's it. I saved the lesson, so next time I can replay it: {goal}." : $"That's it. I updated the saved lesson: {saved.Goal}.";
            }

            return "That's it. You've got it.";
        }
        // The user stopped the lesson.
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return "Lesson stopped. I didn't save it.";
        }
        catch (Exception exception) when (exception is OperationCanceledException or TimeoutException)
        {
            // Not Noah's stop: the model did not answer in time.
            return "The model took too long to answer, so I stopped the lesson. I didn't save it.";
        }
        catch (TutorUnavailableException exception)
        {
            return $"I had to stop the lesson: {exception.Message}. I didn't save it.";
        }
        catch (TutorStepException exception)
        {
            return $"I couldn't work out the next step ({exception.Message}). I stopped the lesson and didn't save it.";
        }
        finally
        {
            // Always give the screen back (remove the pointer, restore Jarvis's window), whatever happened.
            host.End();
        }
    }

    // Asks the model for the next step using the Fast route and temperature 0.1 (nearly deterministic).
    private async Task<TutorPick> AskAsync(
        string goal, string exe, IReadOnlyList<TutorStep> steps, IReadOnlyList<TutorElement> elements, TutorStep? old, CancellationToken cancellationToken)
    {
        if (model is null || !model.IsAvailable)
        {
            throw new TutorStepException("the local model is not available");
        }

        var prompt = TutorLogic.BuildPrompt(goal, exe, steps.Select(s => s.Say).ToList(), elements, old);
        var request = new ChatCompletionRequest(
            [
                new ChatMessage("system", [ChatMessageContentPart.FromText(TutorLogic.SystemPrompt)]),
                new ChatMessage("user", [ChatMessageContentPart.FromText(prompt)])
            ],
            Temperature: 0.1);

        // One retry when the reply does not parse; a second bad reply ends the lesson.
        var response = await model.CompleteAsync(request, ModelRoute.Fast, cancellationToken);

        try
        {
            return TutorLogic.ParseStep(response.Content, elements.Count);
        }
        catch (TutorStepException)
        {
            response = await model.CompleteAsync(request, ModelRoute.Fast, cancellationToken);
            return TutorLogic.ParseStep(response.Content, elements.Count);
        }
    }
}

// What the rest of Jarvis uses to run lessons: start one, stop it, or say "next" for a step with nothing to click.
public interface ITutorService
{
    bool IsActive { get; }

    /// <summary>Each step's spoken line, and the lesson's final message. Raised on a background thread.</summary>
    event Action<string>? Said;

    bool Start(string goal);

    void Stop();

    void Next();
}

// Runs a TutorDriver lesson on a background task. Only one lesson can run at a time.
public sealed class TutorService : ITutorService
{
    private readonly TutorDriver _driver;
    private readonly ITutorHost _host;
    private CancellationTokenSource? _lesson;

    public TutorService(IModelGateway? model, ITutorHost host, string lessonsPath)
    {
        _host = host;
        _driver = new TutorDriver(model, host, new TutorLessonStore(lessonsPath), message => Said?.Invoke(message));
    }

    public bool IsActive => _lesson is not null;

    public event Action<string>? Said;

    public bool Start(string goal)
    {
        var source = new CancellationTokenSource();

        // Atomically claim the "a lesson is running" slot; if it was already taken, refuse to start a second lesson.
        if (Interlocked.CompareExchange(ref _lesson, source, null) is not null)
        {
            source.Dispose();
            return false;
        }

        // Fire and forget: the lesson runs in the background and reports back by raising the Said event.
        _ = Task.Run(async () =>
        {
            string message;

            try
            {
                message = await _driver.RunAsync(goal, source.Token);
            }
            catch (Exception exception)
            {
                message = $"The lesson failed: {exception.Message}";
            }

            _lesson = null;
            source.Dispose();
            Said?.Invoke(message);
        });
        return true;
    }

    public void Stop()
    {
        try
        {
            _lesson?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The lesson just ended.
        }
    }

    public void Next() => _host.Next();
}

// The chat tool for lessons: "teach me how to ...", plus "next" and "stop lesson" while one is running.
public sealed class TeachTool(ITutorService? tutor) : IAssistantTool
{
    private static readonly string[] StopPhrases = ["stop lesson", "stop teaching", "end lesson", "cancel lesson"];
    private static readonly string[] NextPhrases = ["next", "next step", "done", "i did it"];

    // The language model may call the tool with a looser form such as "teach insert a table"; this accepts any "teach <something>".
    private static readonly Regex PlannerForm = new(@"^\s*teach\s+\S", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public string Name => "teach";

    public string Description =>
        "Teach the user a task in the app they are using by pointing at the real controls on screen, one step at a time, and remember the lesson. "
        + "Input: `teach me how to <goal>`. While a lesson runs: `next` or `stop lesson`.";

    // Handles "teach me" requests, and "next"/"stop" phrases only while a lesson is running (so a plain "next" or "done" is not taken over when no lesson is running).
    public bool CanHandle(string input) =>
        tutor is not null
        && (TutorLogic.IsTeachRequest(input)
            || (tutor.IsActive && (IsOneOf(input, StopPhrases) || IsOneOf(input, NextPhrases))));

    public Task<ToolResult> ExecuteAsync(string input, ToolContext context, CancellationToken cancellationToken)
    {
        if (tutor is null)
        {
            return Task.FromResult(new ToolResult("The tutor is not available.", Succeeded: false, SummaryText: "Tutor unavailable."));
        }

        var isControl = IsOneOf(input, StopPhrases) || IsOneOf(input, NextPhrases);

        if (isControl && !tutor.IsActive)
        {
            return Task.FromResult(new ToolResult("No lesson is running.", Succeeded: false, SummaryText: "No lesson running."));
        }

        if (!isControl && !TutorLogic.IsTeachRequest(input) && !PlannerForm.IsMatch(input))
        {
            return Task.FromResult(new ToolResult("Say `teach me how to <goal>` to start a lesson.", Succeeded: false, SummaryText: "Not a lesson request."));
        }

        if (tutor.IsActive && IsOneOf(input, StopPhrases))
        {
            tutor.Stop();
            return Task.FromResult(new ToolResult("Okay, stopping the lesson.", SummaryText: "Stopped the lesson."));
        }

        if (tutor.IsActive && IsOneOf(input, NextPhrases))
        {
            tutor.Next();
            return Task.FromResult(new ToolResult("Okay.", SummaryText: "Next step."));
        }

        var goal = TutorLogic.ExtractGoal(input);

        if (goal.Length == 0)
        {
            return Task.FromResult(new ToolResult("Teach you what? Say, for example, `teach me how to insert a table`.", Succeeded: false, SummaryText: "No lesson goal."));
        }

        return Task.FromResult(tutor.Start(goal)
            ? new ToolResult(
                $"Okay. Let's {goal}. I'll point at each control; click it and I'll move on. Say `stop lesson` to end it.",
                VerificationText: "The lesson started in the background.",
                SummaryText: "Started a lesson.")
            : new ToolResult("A lesson is already running. Say `stop lesson` first.", Succeeded: false, SummaryText: "Lesson already running."));
    }

    // Exact phrase match, ignoring case and a trailing "." or "!".
    private static bool IsOneOf(string input, string[] phrases) =>
        phrases.Any(phrase => string.Equals(input.Trim().TrimEnd('.', '!'), phrase, StringComparison.OrdinalIgnoreCase));
}
