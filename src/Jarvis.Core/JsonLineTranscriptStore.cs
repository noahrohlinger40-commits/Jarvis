// The conversation log: every turn (what the user said, what Jarvis answered, tool results) is appended to transcript.jsonl
// (default %LOCALAPPDATA%\Jarvis\data\transcript.jsonl, from JarvisOptions.TranscriptFilePath).
// 'JSON lines' means one complete JSON object per line, so adding a turn is just appending a line, never rewriting the file.
// MemoryAutomation.PrepareTurn decides whether a turn is written at all (off-the-record and sensitive turns are not).
// The assistant reads recent turns back for conversation context and for BuildConversationSummary.
using System.Text.Json;

namespace Jarvis.Core;

// Append-only transcript file with a lock so concurrent writers do not interleave their lines.
public sealed class JsonLineTranscriptStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _filePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonLineTranscriptStore(string filePath)
    {
        _filePath = filePath;
    }

    // Adds one turn as a new line at the end of the file.
    public async Task AppendAsync(AssistantTurn turn, CancellationToken cancellationToken)
    {
        EnsureParentDirectoryExists();

        await _gate.WaitAsync(cancellationToken);

        try
        {
            var line = JsonSerializer.Serialize(turn, JsonOptions) + Environment.NewLine;
            await File.AppendAllTextAsync(_filePath, line, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    // Number of turns in the log (reads the whole file).
    public async Task<int> CountAsync(CancellationToken cancellationToken)
    {
        var turns = await GetAllAsync(cancellationToken);
        return turns.Count;
    }

    // Every logged turn, oldest first.
    public async Task<IReadOnlyList<AssistantTurn>> GetAllAsync(CancellationToken cancellationToken)
    {
        EnsureParentDirectoryExists();

        await _gate.WaitAsync(cancellationToken);

        try
        {
            return await LoadTurnsUnsafeAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    // The newest N turns, still in oldest-to-newest order.
    public async Task<IReadOnlyList<AssistantTurn>> GetRecentAsync(int maxResults, CancellationToken cancellationToken)
    {
        if (maxResults <= 0)
        {
            return Array.Empty<AssistantTurn>();
        }

        EnsureParentDirectoryExists();

        await _gate.WaitAsync(cancellationToken);

        try
        {
            var turns = await LoadTurnsUnsafeAsync(cancellationToken);
            return turns
                .TakeLast(maxResults)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    // Reads and parses every line, skipping blank ones, and sorts by time. 'Unsafe' because the caller must already hold _gate.
    private async Task<List<AssistantTurn>> LoadTurnsUnsafeAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath))
        {
            return new List<AssistantTurn>();
        }

        var lines = await File.ReadAllLinesAsync(_filePath, cancellationToken);
        var turns = new List<AssistantTurn>(capacity: lines.Length);

        foreach (var line in lines)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var turn = JsonSerializer.Deserialize<AssistantTurn>(line, JsonOptions);

            if (turn is not null)
            {
                turns.Add(turn);
            }
        }

        return turns
            .OrderBy(turn => turn.TimestampUtc)
            .ToList();
    }

    private void EnsureParentDirectoryExists()
    {
        EnsureDirectory(Path.GetDirectoryName(_filePath));
    }

    private static void EnsureDirectory(string? directory)
    {
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }
}
