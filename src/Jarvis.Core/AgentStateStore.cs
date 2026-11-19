// Saves the state of Jarvis's multi-step 'agent' tasks to agent-state.json (default %LOCALAPPDATA%\Jarvis\data\agent-state.json,
// set by JarvisOptions.AgentStateFilePath), so a task survives a restart. A PersistedAgentSession (see AgentContracts.cs) is one task:
// the original request, its plan steps, tool results and a status. The assistant uses this to resume an unfinished task, to ask the user
// a clarifying question and continue from the answer, and to list tasks running in the background.
// Same pattern as JsonMemoryStore: one JSON array, a lock, load the whole file, change it, write it all back.
using System.Text.Json;

namespace Jarvis.Core;

// What the assistant needs from session storage. It is an interface so tests can use an in-memory fake.
public interface IAgentStateStore
{
    // The newest session waiting for the user to answer a question (status 'awaiting-clarification').
    Task<PersistedAgentSession?> GetPendingClarificationAsync(CancellationToken cancellationToken);

    // Background sessions that are not finished yet, oldest first.
    Task<IReadOnlyList<PersistedAgentSession>> GetBackgroundSessionsAsync(CancellationToken cancellationToken);

    // The most recently updated session that could be continued (active, queued, running or failed in the background).
    Task<PersistedAgentSession?> GetLatestResumableSessionAsync(CancellationToken cancellationToken);

    Task<PersistedAgentSession?> GetSessionAsync(string sessionId, CancellationToken cancellationToken);

    // Saves a session, replacing the stored one with the same id or adding it if new.
    Task UpsertSessionAsync(PersistedAgentSession session, CancellationToken cancellationToken);

    Task RemoveSessionAsync(string sessionId, CancellationToken cancellationToken);
}

// Implements IAgentStateStore with a single JSON file.
public sealed class JsonAgentStateStore : IAgentStateStore
{
    // Web defaults (camelCase names); WriteIndented keeps the file readable.
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _filePath;
    // One-at-a-time lock so overlapping calls cannot overwrite each other's changes.
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonAgentStateStore(string filePath)
    {
        _filePath = filePath;
    }

    public async Task<PersistedAgentSession?> GetPendingClarificationAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);

        try
        {
            var sessions = await LoadAsync(cancellationToken);
            return sessions
                .Where(session => string.Equals(session.Status, "awaiting-clarification", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(session => session.UpdatedAtUtc)
                .FirstOrDefault();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<PersistedAgentSession>> GetBackgroundSessionsAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);

        try
        {
            var sessions = await LoadAsync(cancellationToken);
            return sessions
                .Where(IsVisibleBackgroundSession)
                .OrderBy(session => session.CreatedAtUtc)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PersistedAgentSession?> GetLatestResumableSessionAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);

        try
        {
            var sessions = await LoadAsync(cancellationToken);
            return sessions
                .Where(IsResumableSession)
                .OrderByDescending(session => session.UpdatedAtUtc)
                .FirstOrDefault();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PersistedAgentSession?> GetSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        await _gate.WaitAsync(cancellationToken);

        try
        {
            var sessions = await LoadAsync(cancellationToken);
            return sessions.FirstOrDefault(session => string.Equals(session.SessionId, sessionId, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpsertSessionAsync(PersistedAgentSession session, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);

        try
        {
            var sessions = await LoadAsync(cancellationToken);
            var existingIndex = sessions.FindIndex(existing =>
                string.Equals(existing.SessionId, session.SessionId, StringComparison.OrdinalIgnoreCase));

            if (existingIndex >= 0)
            {
                sessions[existingIndex] = session;
            }
            else
            {
                sessions.Add(session);
            }

            await SaveAsync(sessions, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken);

        try
        {
            var sessions = await LoadAsync(cancellationToken);
            sessions.RemoveAll(session => string.Equals(session.SessionId, sessionId, StringComparison.OrdinalIgnoreCase));
            await SaveAsync(sessions, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    // Reads every saved session (an empty list if the file does not exist). Callers must already hold _gate.
    private async Task<List<PersistedAgentSession>> LoadAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);

        if (!File.Exists(_filePath))
        {
            return new List<PersistedAgentSession>();
        }

        await using var stream = File.OpenRead(_filePath);
        var sessions = await JsonSerializer.DeserializeAsync<List<PersistedAgentSession>>(stream, JsonOptions, cancellationToken);
        return sessions ?? new List<PersistedAgentSession>();
    }

    // Overwrites the file with the full list. Callers must already hold _gate.
    private async Task SaveAsync(List<PersistedAgentSession> sessions, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);

        // Write to a temporary file first and then swap it in, so a crash mid-write cannot wipe the saved sessions.
        var temp = _filePath + ".tmp";

        await using (var stream = File.Create(temp))
        {
            await JsonSerializer.SerializeAsync(stream, sessions, JsonOptions, cancellationToken);
        }

        File.Move(temp, _filePath, overwrite: true);
    }

    // A background session that has not finished (neither 'background-complete' nor 'completed').
    private static bool IsVisibleBackgroundSession(PersistedAgentSession session)
    {
        return session.RunInBackground
            && !MatchesStatus(session, "background-complete")
            && !MatchesStatus(session, "completed");
    }

    // Statuses a session can be picked up from: active, background-queued, background-running, background-failed.
    private static bool IsResumableSession(PersistedAgentSession session)
    {
        return MatchesStatus(session, "active")
            || MatchesStatus(session, "background-queued")
            || MatchesStatus(session, "background-running")
            || MatchesStatus(session, "background-failed");
    }

    private static bool MatchesStatus(PersistedAgentSession session, string expectedStatus)
    {
        return string.Equals(session.Status, expectedStatus, StringComparison.OrdinalIgnoreCase);
    }
}
