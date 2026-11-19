// Lecture / meeting note-taker. 'lecture notes start <name>' records the microphone and the PC's own audio; 'lecture notes stop' saves
// the recording, writes a Markdown note into the Lecture Notes Obsidian vault (JarvisOptions.LectureNotesFolder, default
// %USERPROFILE%\Lecture Notes), and asks the LOCAL language model for a summary, key points and due dates.
// The recording itself and speech-to-text are done by an ILectureRecorder supplied by the app (Jarvis.App); this file only controls it.
// LectureNoteText holds the text work: formatting the note, splitting the transcript for the model, and checking the model's output.
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Jarvis.Core;

// One transcribed sentence with its time from the start of the recording.
/// <param name="At">Time from the start of the recording.</param>
public sealed record TranscriptLine(TimeSpan At, string Text);

// A finished recording: transcript lines, total length and the saved audio file.
/// <param name="AudioPath">The saved recording: an MP3, or the WAV if it could not be converted.</param>
public sealed record LectureRecording(IReadOnlyList<TranscriptLine> Lines, TimeSpan Duration, string AudioPath);

// What the app must provide for recording: start, stop, and the finished result.
/// <summary>Records the microphone and the computer's own audio, transcribing as it goes.</summary>
public interface ILectureRecorder
{
    bool IsRecording { get; }

    /// <summary>Starts recording to <paramref name="wavPath"/>. Throws if there is no microphone.</summary>
    void Start(string wavPath);

    /// <summary>Stops, waits for the last of the transcription, and saves the audio as MP3.</summary>
    Task<LectureRecording> StopAsync(CancellationToken cancellationToken);
}

// The tool the planner calls. It remembers the recording in progress (title, file name, start time) in fields, so it handles one recording at a time.
public sealed class LectureNotesTool(ILectureRecorder? recorder, IModelGateway? modelGateway) : IAssistantTool
{
    // Cleaned name of the current recording.
    private string _title = string.Empty;
    // File name (without extension) shared by the note and its audio, e.g. '2026-09-29 IT Professions'.
    private string _fileName = string.Empty;
    // Local start time of the current recording.
    private DateTime _startedAt;

    // Name the planner uses to pick this tool.
    public string Name => "lecture notes";

    public string Description =>
        "Record a lecture or meeting (microphone and computer audio) and turn it into notes in the Lecture Notes vault. "
        + "`lecture notes start <course or meeting name>` begins; `lecture notes stop` ends it and writes the notes.";

    // True for input beginning with 'lecture notes'.
    public bool CanHandle(string input) => ToolInputHelper.StartsWithCommand(input, "lecture notes");

    // Routes 'stop' and 'start <name>'; anything else reports whether a recording is in progress.
    public async Task<ToolResult> ExecuteAsync(string input, ToolContext context, CancellationToken cancellationToken)
    {
        if (recorder is null)
        {
            return new ToolResult("Recording isn't available here.", Succeeded: false);
        }

        var tail = ToolInputHelper.GetCommandTail(input, "lecture notes").Trim();
        // Expands %USERPROFILE% in the configured folder.
        var vault = Environment.ExpandEnvironmentVariables(context.Options.LectureNotesFolder);

        if (tail.StartsWith("stop", StringComparison.OrdinalIgnoreCase))
        {
            return await StopAsync(vault, cancellationToken);
        }

        if (tail.StartsWith("start", StringComparison.OrdinalIgnoreCase))
        {
            return Start(vault, tail[5..].Trim());
        }

        return new ToolResult(
            recorder.IsRecording ? $"I'm recording {_title}, started at {_startedAt:h:mm tt}." : "I'm not recording anything.");
    }

    // Begins recording: picks unique file names, creates the Recordings folder and starts the recorder.
    private ToolResult Start(string vault, string name)
    {
        if (recorder!.IsRecording)
        {
            return new ToolResult($"I'm already recording {_title}. Say \"stop lecture notes\" to finish.");
        }

        // A name that is safe for file names and Obsidian links.
        _title = LectureNoteText.CleanTitle(name);
        _startedAt = DateTime.Now;
        var recordings = Path.Combine(vault, "Recordings");
        Directory.CreateDirectory(recordings);

        // "2026-09-29 IT Professions", then "... 2" for a second recording that day.
        _fileName = $"{_startedAt:yyyy-MM-dd} {_title}";

        for (var copy = 2; File.Exists(NotePath(vault)) || File.Exists(Path.Combine(recordings, _fileName + ".mp3")); copy++)
        {
            _fileName = $"{_startedAt:yyyy-MM-dd} {_title} {copy}";
        }

        try
        {
            recorder.Start(Path.Combine(recordings, _fileName + ".wav"));
        }
        catch (Exception exception)
        {
            // No microphone, a device error, or the vault folder can't be written.
            return new ToolResult($"I couldn't start recording: {exception.Message}", Succeeded: false);
        }

        // Kept short: the computer's audio is being recorded, so this reply ends up at the start of it.
        return new ToolResult($"Recording {_title}.");
    }

    // Ends the recording and produces the note in three steps: (1) save the transcript note immediately, (2) open it in Obsidian, (3) ask the
    // local model for notes and rewrite the note with them. Each step is separate so a failure later never loses what was saved earlier.
    private async Task<ToolResult> StopAsync(string vault, CancellationToken cancellationToken)
    {
        if (!recorder!.IsRecording)
        {
            return new ToolResult("I'm not recording anything.", Succeeded: false);
        }

        // CancellationToken.None on purpose: once stopped, the audio must be finished and saved even if the request is cancelled.
        var recording = await recorder.StopAsync(CancellationToken.None);
        recording = recording with { Lines = LectureNoteText.JoinIntoSentences(recording.Lines) };
        var notePath = NotePath(vault);
        Directory.CreateDirectory(Path.GetDirectoryName(notePath)!);

        // The transcript is saved before the model runs, so a crash or a cancelled request loses nothing.
        var audioFile = Path.GetFileName(recording.AudioPath);
        await File.WriteAllTextAsync(notePath, LectureNoteText.Render(_title, _startedAt, recording, audioFile, null), CancellationToken.None);

        // Opened now, not after the notes: the model can take minutes, and Obsidian reloads the file when it's rewritten.
        try
        {
            Process.Start(new ProcessStartInfo("obsidian://open?path=" + Uri.EscapeDataString(notePath)) { UseShellExecute = true });
        }
        catch
        {
            // Obsidian isn't installed: saving the notes must not fail over it.
        }

        if (recording.Lines.Count == 0)
        {
            return new ToolResult($"I saved the {_title} recording, but I couldn't make out any speech in it.");
        }

        // Step 3: model-written notes (null if the local model is not running).
        var notes = await LectureNoteText.WriteNotesAsync(modelGateway, _title, recording.Lines, cancellationToken);

        if (notes is null)
        {
            return new ToolResult(
                $"I saved the {_title} recording and transcript, but the local model is off, so there are no notes yet.",
                Succeeded: false);
        }

        await File.WriteAllTextAsync(notePath, LectureNoteText.Render(_title, _startedAt, recording, audioFile, notes), cancellationToken);
        // Wording for how many due dates / to-dos were found, appended to the spoken reply.
        var dueItems = notes.DueItems.Count switch
        {
            0 => string.Empty,
            1 => " There's one due date or to-do in them.",
            var count => $" There are {count} due dates or to-dos in them."
        };

        return new ToolResult(
            $"Your {_title} notes are saved. {notes.Summary}{dueItems}",
            SummaryText: $"Saved lecture notes to {notePath}.",
            SpeakInFull: true);
    }

    // Where the note goes: <vault>/<title>/<file name>.md.
    private string NotePath(string vault) => Path.Combine(vault, _title, _fileName + ".md");
}

// The finished notes: a short summary, due dates/to-dos, and key points for each time stretch.
/// <param name="Sections">Key points per stretch of the recording, with where each stretch starts and ends.</param>
public sealed record LectureNotes(string Summary, IReadOnlyList<string> DueItems, IReadOnlyList<(TimeSpan Start, TimeSpan End, IReadOnlyList<string> Points)> Sections);

// Text helpers. 'partial' because the [GeneratedRegex] methods at the bottom are completed by the compiler.
public static partial class LectureNoteText
{
    // Largest chunk of transcript sent to the model at once: about 3,000 tokens (roughly 15 minutes of speech),
    // inside the local model's 8K context with room to answer.
    private const int SectionCharacters = 12_000;

    // Prompt: turn one chunk of transcript into 3 to 8 bullet points, using only what was said.
    private const string KeyPointsInstructions =
        "You take notes from part of a lecture or meeting transcript. Write the key points as 3 to 8 short bullet points, "
        + "each on its own line starting with \"- \". Keep names, terms, numbers, and examples exactly as said. "
        + "Use only what the transcript says. Write nothing else: no heading, no introduction.";

    // Prompt: list assignments, deadlines, exams and tasks in one chunk; answer 'none' if there are none.
    private const string DueItemsInstructions =
        "From this part of a lecture or meeting transcript, list every assignment, due date, exam, quiz, deadline, and task "
        + "someone must do, and anything the speaker says will be on a test. One per line starting with \"- \", with dates "
        + "and times exactly as said. If there are none, reply with only the word: none";

    // Prompt: a short plain-text summary that can be read aloud.
    private const string SummaryInstructions =
        "These are notes from a lecture or meeting. Summarize it in 2 or 3 plain sentences, under 60 words, for someone "
        + "who missed it. The summary is read aloud: no lists, headings, markdown, or emoji.";

    /// <summary>"it professions" stays as said; characters that files or Obsidian links can't hold are dropped.</summary>
    public static string CleanTitle(string name)
    {
        // Replace characters that file names or Obsidian links cannot hold (\ / : * ? " < > | # ^ [ ]) with spaces.
        var cleaned = Regex.Replace(name, @"[\\/:*?""<>|#^\[\]]", " ");
        cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim(' ', '.');

        // Whisper sometimes writes a spoken name in lowercase ("demo day"); it becomes a title and a folder name.
        if (!cleaned.Any(char.IsUpper))
        {
            cleaned = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(cleaned);
        }

        return cleaned.Length == 0 ? "Lecture" : cleaned;
    }

    /// <summary>"05:07", or "01:05:07" past an hour: the formats the Audio Timestamp Player plugin reads.</summary>
    public static string FormatTime(TimeSpan time) =>
        time.TotalHours >= 1 ? time.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture) : time.ToString(@"mm\:ss", CultureInfo.InvariantCulture);

    /// <summary>A clickable time: with the plugin it plays the recording from there, without it it's a plain link.</summary>
    // Markdown link whose '#audio-time=' part tells the plugin where to start playing.
    private static string TimeLink(TimeSpan time) => $"[{FormatTime(time)}](#audio-time={FormatTime(time)})";

    /// <summary>The whole note. Without <paramref name="notes"/> it holds the recording and transcript only.</summary>
    // Builds the whole Markdown note: a front-matter header (date, time, duration, tags), the embedded audio, then either a 'no notes yet'
    // line or Summary / Due dates / Notes sections, and finally the timestamped transcript.
    public static string Render(string title, DateTime startedAt, LectureRecording recording, string audioFile, LectureNotes? notes)
    {
        // Tag as 'meeting' if the name says so, otherwise 'lecture'.
        var kind = title.Contains("meeting", StringComparison.OrdinalIgnoreCase) ? "meeting" : "lecture";
        // Turns the title into a tag: lower-case letters and digits with dashes ('IT Professions' becomes 'it-professions').
        var tag = Regex.Replace(title.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
        var note = new StringBuilder();

        note.AppendLine("---");
        note.AppendLine($"date: {startedAt:yyyy-MM-dd}");
        note.AppendLine($"time: \"{startedAt:HH:mm}\"");
        note.AppendLine($"duration: \"{FormatTime(recording.Duration)}\"");
        note.AppendLine("tags:");
        note.AppendLine($"  - {kind}");

        if (tag.Length > 0 && tag != kind)
        {
            note.AppendLine($"  - {tag}");
        }

        note.AppendLine("---");
        note.AppendLine($"# {title}, {startedAt.ToString("ddd MMM d", CultureInfo.InvariantCulture)}");
        note.AppendLine();
        note.AppendLine($"![[{audioFile}]]");
        note.AppendLine();

        if (notes is null)
        {
            note.AppendLine("*No notes yet: the local model was off or still working when this was saved. The transcript is below.*");
            note.AppendLine();
        }
        else
        {
            note.AppendLine("## Summary");
            note.AppendLine(notes.Summary);
            note.AppendLine();
            note.AppendLine("## Due dates and to-dos");
            note.AppendLine(notes.DueItems.Count == 0 ? "None mentioned." : string.Join(Environment.NewLine, notes.DueItems.Select(item => $"- [ ] {item}")));
            note.AppendLine();
            note.AppendLine("## Notes");

            foreach (var (start, end, points) in notes.Sections)
            {
                note.AppendLine($"### {TimeLink(start)} to {FormatTime(end)}");
                note.AppendLine(string.Join(Environment.NewLine, points.Select(point => $"- {point}")));
                note.AppendLine();
            }
        }

        note.AppendLine("## Transcript");

        foreach (var line in recording.Lines)
        {
            note.AppendLine($"{TimeLink(line.At)} {line.Text}");
        }

        return note.ToString();
    }

    /// <summary>Notes from the local model, or null when it is off or fails.</summary>
    // Asks the model for notes. Per transcript chunk: key points and due items; then one summary built from all the key points.
    // Returns null if the model is off or any step fails (unless the user cancelled, which propagates).
    public static async Task<LectureNotes?> WriteNotesAsync(
        IModelGateway? gateway,
        string title,
        IReadOnlyList<TranscriptLine> lines,
        CancellationToken cancellationToken)
    {
        if (gateway is not { IsAvailable: true })
        {
            return null;
        }

        try
        {
            var sections = new List<(TimeSpan Start, TimeSpan End, IReadOnlyList<string> Points)>();
            var dueItems = new List<string>();

            foreach (var (start, end, text) in SplitIntoSections(lines))
            {
                var input = $"Title: {title}\n\nTranscript:\n{text}";
                var points = Bullets(await CompleteAsync(gateway, KeyPointsInstructions, input, cancellationToken));
                sections.Add((start, end, points.Select(point => FlagUnheardNumbers(point, text)).ToList()));
                var due = Bullets(await CompleteAsync(gateway, DueItemsInstructions, input, cancellationToken));
                dueItems.AddRange(due.Select(item => FlagUnheardNumbers(item, text)));
            }

            var allPoints = string.Join("\n", sections.SelectMany(section => section.Points).Select(point => $"- {point}"));
            // Flatten the summary to plain text: drop markdown symbols and collapse whitespace so it reads well aloud.
            var summary = Regex.Replace(await CompleteAsync(gateway, SummaryInstructions, $"Title: {title}\n\nNotes:\n{allPoints}", cancellationToken), @"[*#_`]+|\s+", " ").Trim();
            return new LectureNotes(summary, dueItems.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), sections);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>
    /// Whisper's segments break mid-sentence ("...in seven" / "layers."), so a segment that doesn't end a
    /// sentence is joined to the next, keeping the first one's time. Capped so unpunctuated speech still breaks.
    /// </summary>
    // Rejoins transcript fragments (see the note above); the regex tests whether the text ends with . ? ! or ), and a fragment is only joined while under 250 characters.
    public static List<TranscriptLine> JoinIntoSentences(IReadOnlyList<TranscriptLine> lines)
    {
        var joined = new List<TranscriptLine>();

        foreach (var line in lines)
        {
            if (joined.Count > 0 && joined[^1] is var last && !Regex.IsMatch(last.Text, @"[.?!)]$") && last.Text.Length < 250)
            {
                joined[^1] = last with { Text = $"{last.Text} {line.Text}" };
            }
            else
            {
                joined.Add(line);
            }
        }

        return joined;
    }

    /// <summary>Consecutive transcript lines grouped into sections the model can take in one pass.</summary>
    // Groups lines into chunks of at most about 12,000 characters, recording each chunk's start and end time.
    public static List<(TimeSpan Start, TimeSpan End, string Text)> SplitIntoSections(IReadOnlyList<TranscriptLine> lines)
    {
        var sections = new List<(TimeSpan, TimeSpan, string)>();
        var text = new StringBuilder();
        var start = TimeSpan.Zero;

        for (var index = 0; index < lines.Count; index++)
        {
            if (text.Length == 0)
            {
                start = lines[index].At;
            }

            text.Append(lines[index].Text).Append(' ');

            if (index == lines.Count - 1 || text.Length + lines[index + 1].Text.Length > SectionCharacters)
            {
                var end = index == lines.Count - 1 ? lines[index].At : lines[index + 1].At;
                sections.Add((start, end, text.ToString().Trim()));
                text.Clear();
            }
        }

        return sections;
    }

    /// <summary>
    /// The small local model sometimes changes a number (it once wrote port 447 for 443). These notes are
    /// studied from, so a point with a number the transcript doesn't have is flagged rather than trusted.
    /// </summary>
    // Finds every run of digits in the model's point and checks it also appears as a whole number in the transcript (the lookarounds
    // (?<!\d) and (?!\d) stop '44' matching inside '443'). Numbers the transcript spells out as words would also be flagged.
    public static string FlagUnheardNumbers(string point, string transcript) =>
        Regex.Matches(point, @"\d+").Any(number => !Regex.IsMatch(transcript, $@"(?<!\d){number.Value}(?!\d)"))
            ? $"{point} ⚠️ *check the recording: a number here isn't in the transcript*"
            : point;

    /// <summary>The model's list items, without markers, headings, or "none".</summary>
    // Cleans the model's reply into plain items. The regex removes a leading '-', '*', a bullet or '1.' / '1)', and bold '**' marks;
    // lines ending in ':' (headings) and 'nothing found' answers are dropped.
    public static List<string> Bullets(string reply) =>
        reply.Split('\n')
            .Select(line => Regex.Replace(line, @"^\s*(?:[-*•]|\d+[.)])\s*|\*\*", string.Empty).Trim())
            .Where(line => line.Length > 0 && !line.EndsWith(':') && !NothingFoundRegex().IsMatch(line))
            .ToList();

    // Sends one prompt to the local model with a 90-second limit and removes any <think> block from the reply.
    private static async Task<string> CompleteAsync(IModelGateway gateway, string instructions, string input, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        var response = await gateway.CompleteAsync(
            new ChatCompletionRequest(
                [
                    new ChatMessage("system", [ChatMessageContentPart.FromText(instructions)]),
                    new ChatMessage("user", [ChatMessageContentPart.FromText(input)])
                ],
                // 0, not the usual 0.2: notes should repeat the facts, not vary them.
                Temperature: 0),
            // Reasoning allows 384 tokens on the local model; Fast's 256 could cut the notes off.
            ModelRoute.Reasoning,
            timeout.Token);

        return ThinkBlockRegex().Replace(response.Content ?? string.Empty, string.Empty);
    }

    // Matches a '<think>...</think>' block, the reasoning text some local models print before the answer.
    [GeneratedRegex(@"<think>[\s\S]*?</think>", RegexOptions.IgnoreCase)]
    private static partial Regex ThinkBlockRegex();

    // Matches a reply that means 'nothing found': 'none', 'n/a', 'there are/were/is no ...', or 'no <up to 3 words> mentioned/found/given/listed'.
    // "none", "There are no deadlines.", "No assignments were mentioned."
    [GeneratedRegex(@"^(?:none|n/a)\b|^there (?:are|were|is) no\b|^no\s+(?:\w+\s+){0,3}(?:mentioned|found|given|listed)\b", RegexOptions.IgnoreCase)]
    private static partial Regex NothingFoundRegex();
}
