// JsonMemoryStore: Jarvis's long-term memory. Everything it remembers lives in ONE JSON array of MemoryNote records (AssistantTurn.cs)
// in memory.json (default %LOCALAPPDATA%\Jarvis\data\, path from JarvisOptions.MemoryFilePath via JarvisDataPaths): facts, preferences,
// reminders, conversation summaries and 'entities' (people, devices, projects, places).
// SearchAsync drops forgotten and expired notes, then MemorySemanticSearch ranks the rest by TF-IDF cosine similarity plus bonuses
// for phrase matches, similar situation, importance and recency; there is no database or embedding model.
// Related: MemoryModels.cs, MemoryPolicies.cs (retention, expiry, importance), MemoryAutomation.cs (what to remember),
// MemoryManagementTools.cs (the 'memory' tool), JsonLineTranscriptStore.cs, AgentStateStore.cs, pending-approval.json.
// Every public method takes _gate, loads the whole file, edits it in memory and writes it all back: fine for a few thousand notes, no more.
using System.Text.Json;

namespace Jarvis.Core;

// Reads and writes memory.json. Safe for concurrent async calls inside one process; it does not protect against
// two Jarvis processes writing the same file.
public sealed class JsonMemoryStore
{
    // Web defaults give camelCase property names in the file; WriteIndented keeps it readable by hand.
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    // Full path of memory.json.
    private readonly string _filePath;
    // One-at-a-time lock, so two overlapping calls cannot both read, change and save the file and lose each other's changes.
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonMemoryStore(string filePath)
    {
        _filePath = filePath;
    }

    // Shortcut: remember a piece of plain text using all default settings.
    public Task AddAsync(string content, CancellationToken cancellationToken)
    {
        return AddAsync(new MemoryWriteRequest(content), cancellationToken);
    }

    // Saves a note. If an equivalent note already exists it is updated (counted as seen once more) instead of duplicated; see UpsertNote.
    public async Task AddAsync(MemoryWriteRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Content))
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken);

        try
        {
            var notes = await LoadAsync(cancellationToken);
            UpsertNote(notes, request);
            await SaveAsync(notes, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    // How many notes are currently usable (not forgotten, not expired).
    public async Task<int> CountAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);

        try
        {
            var notes = await LoadAsync(cancellationToken);
            return GetActiveNotes(notes, DateTimeOffset.UtcNow).Count;
        }
        finally
        {
            _gate.Release();
        }
    }

    // Every usable note, oldest first.
    public async Task<IReadOnlyList<MemoryNote>> GetAllAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);

        try
        {
            var notes = await LoadAsync(cancellationToken);
            return GetActiveNotes(notes, DateTimeOffset.UtcNow)
                .OrderBy(note => note.CreatedAtUtc)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    // The most recently seen usable notes; ties are broken by importance, then by creation time.
    public async Task<IReadOnlyList<MemoryNote>> GetRecentAsync(int maxResults, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);

        try
        {
            var notes = await LoadAsync(cancellationToken);
            return GetActiveNotes(notes, DateTimeOffset.UtcNow)
                .OrderByDescending(note => note.LastObservedAtUtc ?? note.CreatedAtUtc)
                .ThenByDescending(note => note.ImportanceScore)
                .ThenByDescending(note => note.CreatedAtUtc)
                .Take(Math.Max(0, maxResults))
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    // Search with no extra desktop context (the 'context' is just the query text).
    public Task<IReadOnlyList<MemoryNote>> SearchAsync(string query, int maxResults, CancellationToken cancellationToken)
    {
        return SearchAsync(query, new MemoryQueryContext(query), maxResults, cancellationToken);
    }

    // Search that also uses the current situation (time of day, active app, ...) so notes learned in a similar situation rank higher.
    public async Task<IReadOnlyList<MemoryNote>> SearchAsync(
        string query,
        MemoryQueryContext queryContext,
        int maxResults,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);

        try
        {
            var notes = await LoadAsync(cancellationToken);
            return MemorySemanticSearch.Search(
                GetActiveNotes(notes, DateTimeOffset.UtcNow),
                query,
                maxResults,
                queryContext);
        }
        finally
        {
            _gate.Release();
        }
    }

    // Builds the short 'what Jarvis knows about you' summary: learned preferences, structured memories and pending reminders.
    // The same fact noted several times is folded into one entry with a count of how often it was seen.
    public async Task<MemoryProfileSnapshot> GetProfileAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);

        try
        {
            var nowUtc = DateTimeOffset.UtcNow;
            var notes = await LoadAsync(cancellationToken);
            var activeNotes = GetActiveNotes(notes, nowUtc);
            // Step 1, preferences: group identical (category + text) preferences, add up sightings, keep the newest as the sample, most-seen first.
            var preferenceGroups = activeNotes
                .Where(note => string.Equals(note.Kind, "preference", StringComparison.OrdinalIgnoreCase))
                .GroupBy(
                    note => $"{NormalizeLabel(note.Category, "other")}|{note.Content.Trim()}",
                    note => note,
                    StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var sample = group
                        .OrderByDescending(note => note.LastObservedAtUtc ?? note.CreatedAtUtc)
                        .ThenByDescending(note => note.ImportanceScore)
                        .First();
                    var observationCount = group.Sum(note => Math.Max(1, note.ObservationCount));
                    var lastObservedAtUtc = group.Max(note => note.LastObservedAtUtc ?? note.CreatedAtUtc);
                    return new LearnedPreference(
                        NormalizeLabel(sample.Category, "other"),
                        sample.Content.Trim(),
                        observationCount,
                        lastObservedAtUtc);
                })
                .OrderByDescending(preference => preference.ObservationCount)
                .ThenByDescending(preference => preference.LastObservedAtUtc)
                .ThenBy(preference => preference.Value, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            // Step 2, structured memories (people, devices, projects...): same idea, but the grouping key is built from the
            // entity's names, aliases and distinguishing attributes, so 'Mom' and 'mother' fold into one entry.
            var structuredMemories = activeNotes
                .SelectMany(BuildStructuredEntries)
                .GroupBy(
                    item => BuildStructuredIdentityKey(item.Kind, item.Name, item.Aliases, item.Attributes),
                    item => item,
                    StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var sample = group
                        .OrderByDescending(item => item.Note.LastObservedAtUtc ?? item.Note.CreatedAtUtc)
                        .ThenByDescending(item => item.Note.ImportanceScore)
                        .First();
                    var observationCount = group.Sum(item => Math.Max(1, item.Note.ObservationCount));
                    var lastObservedAtUtc = group.Max(item => item.Note.LastObservedAtUtc ?? item.Note.CreatedAtUtc);
                    return new StructuredMemorySummary(
                        NormalizeLabel(sample.Kind, "other"),
                        sample.Name.Trim(),
                        sample.Summary.Trim(),
                        observationCount,
                        lastObservedAtUtc);
                })
                .OrderByDescending(item => item.ObservationCount)
                .ThenByDescending(item => item.LastObservedAtUtc)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            // Step 3: the next 6 reminders that are not finished, soonest first.
            var pendingReminders = activeNotes
                .Where(MemoryPolicies.IsPendingReminder)
                .OrderBy(note => note.ReminderAtUtc)
                .ThenByDescending(note => note.ImportanceScore)
                .Select(ToReminderSnapshot)
                .Take(6)
                .ToArray();

            var summaryCount = activeNotes.Count(note => string.Equals(note.Kind, "summary", StringComparison.OrdinalIgnoreCase));
            return new MemoryProfileSnapshot(
                activeNotes.Count,
                preferenceGroups.Length,
                summaryCount,
                structuredMemories.Length,
                activeNotes.Count(MemoryPolicies.IsPendingReminder),
                preferenceGroups,
                structuredMemories,
                pendingReminders);
        }
        finally
        {
            _gate.Release();
        }
    }

    // Reminders that are not completed or dismissed, soonest first.
    public async Task<IReadOnlyList<MemoryNote>> GetPendingRemindersAsync(int maxResults, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);

        try
        {
            var notes = await LoadAsync(cancellationToken);
            return GetActiveNotes(notes, DateTimeOffset.UtcNow)
                .Where(MemoryPolicies.IsPendingReminder)
                .OrderBy(note => note.ReminderAtUtc)
                .ThenByDescending(note => note.ImportanceScore)
                .Take(Math.Max(0, maxResults))
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    // Pending reminders due now or within the next 18 hours, so Jarvis can mention tomorrow-morning items the evening before.
    public async Task<IReadOnlyList<MemoryNote>> GetDueRemindersAsync(DateTimeOffset nowUtc, int maxResults, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);

        try
        {
            var notes = await LoadAsync(cancellationToken);
            return GetActiveNotes(notes, nowUtc)
                .Where(note => MemoryPolicies.IsDueReminder(note, nowUtc, TimeSpan.FromHours(18)))
                .OrderBy(note => note.ReminderAtUtc)
                .ThenByDescending(note => note.ImportanceScore)
                .Take(Math.Max(0, maxResults))
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    // Finds one note by id, including forgotten or expired ones (so a note can still be inspected).
    public async Task<MemoryNote?> GetByIdAsync(string id, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        await _gate.WaitAsync(cancellationToken);

        try
        {
            var notes = await LoadAsync(cancellationToken);
            return notes.FirstOrDefault(note => string.Equals(note.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _gate.Release();
        }
    }

    // Replaces the editable fields of an existing note (used by the memory tool). Returns null if the id is unknown.
    public async Task<MemoryNote?> UpdateAsync(MemoryUpdateRequest request, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);

        try
        {
            var notes = await LoadAsync(cancellationToken);
            var index = notes.FindIndex(note => string.Equals(note.Id, request.Id.Trim(), StringComparison.OrdinalIgnoreCase));

            if (index < 0)
            {
                return null;
            }

            var existing = notes[index];
            var createdAtUtc = existing.CreatedAtUtc == default ? DateTimeOffset.UtcNow : existing.CreatedAtUtc;
            var reminderStatus = MemoryPolicies.NormalizeReminderStatus(request.ReminderStatus);
            var retentionPolicy = MemoryPolicies.NormalizeRetentionPolicy(request.RetentionPolicy);
            // Use the explicit expiry if one was given, otherwise work it out from the retention policy.
            var expiresAtUtc = request.ExpiresAtUtc?.ToUniversalTime()
                ?? MemoryPolicies.ResolveExpiryUtc(
                    retentionPolicy,
                    createdAtUtc,
                    request.ReminderAtUtc?.ToUniversalTime(),
                    reminderStatus);
            // A plain 'note' that is given a reminder time is promoted to kind 'reminder'.
            var normalizedKind = NormalizeLabel(
                request.ReminderAtUtc is not null && string.Equals(request.Kind, "note", StringComparison.OrdinalIgnoreCase)
                    ? "reminder"
                    : request.Kind,
                existing.Kind);

            var updated = NormalizeNote(existing with
            {
                Content = request.Content.Trim(),
                Kind = normalizedKind,
                Category = NormalizeLabel(request.Category, string.Empty),
                Privacy = NormalizeLabel(request.Privacy, "persistent"),
                Tags = NormalizeTags(request.Tags),
                ImportanceScore = MemoryPolicies.ClampImportance(request.ImportanceScore),
                RetentionPolicy = retentionPolicy,
                ExpiresAtUtc = expiresAtUtc,
                UserApprovedRetention = request.UserApprovedRetention,
                Entities = request.Entities is null ? existing.Entities : NormalizeEntities(request.Entities),
                ReminderAtUtc = request.ReminderAtUtc?.ToUniversalTime(),
                ReminderText = string.IsNullOrWhiteSpace(request.ReminderText) ? string.Empty : request.ReminderText.Trim(),
                ReminderStatus = reminderStatus
            });

            notes[index] = updated;
            await SaveAsync(notes, cancellationToken);
            return updated;
        }
        finally
        {
            _gate.Release();
        }
    }

    // Soft delete: the note stays in the file but is flagged forgotten, so it vanishes from searches and lists.
    public async Task<bool> ForgetAsync(string id, string reason, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        await _gate.WaitAsync(cancellationToken);

        try
        {
            var notes = await LoadAsync(cancellationToken);
            var index = notes.FindIndex(note => string.Equals(note.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));

            if (index < 0)
            {
                return false;
            }

            notes[index] = notes[index] with
            {
                IsForgotten = true,
                ForgottenAtUtc = DateTimeOffset.UtcNow,
                ForgottenReason = string.IsNullOrWhiteSpace(reason) ? "forgotten" : reason.Trim()
            };

            await SaveAsync(notes, cancellationToken);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    // Reads the whole file (an empty list if it does not exist yet). Callers must already hold _gate.
    private async Task<List<MemoryNote>> LoadAsync(CancellationToken cancellationToken)
    {
        EnsureParentDirectoryExists();

        if (!File.Exists(_filePath))
        {
            return new List<MemoryNote>();
        }

        return await ReadNotesFromJsonAsync(_filePath, cancellationToken);
    }

    // Overwrites the whole file with the cleaned-up notes, oldest first. Callers must already hold _gate.
    private async Task SaveAsync(List<MemoryNote> notes, CancellationToken cancellationToken)
    {
        EnsureParentDirectoryExists();

        var normalized = notes
            .Select(NormalizeNote)
            .OrderBy(note => note.CreatedAtUtc)
            .ToArray();

        // Write to a temporary file first and then swap it in, so a crash mid-write cannot wipe memory.json.
        var temp = _filePath + ".tmp";

        await using (var stream = File.Create(temp))
        {
            await JsonSerializer.SerializeAsync(stream, normalized, JsonOptions, cancellationToken);
        }

        File.Move(temp, _filePath, overwrite: true);
    }

    // Insert-or-merge: if an equivalent note exists, bump its sighting count and merge the new details into it; otherwise add a new note.
    private void UpsertNote(List<MemoryNote> notes, MemoryWriteRequest request)
    {
        var prepared = PrepareWriteRequest(request);
        var duplicateIndex = FindDuplicateIndex(
            notes,
            prepared.Content.Trim(),
            prepared.Kind,
            prepared.Category,
            prepared.SummaryWindowStartUtc,
            prepared.SummaryWindowEndUtc,
            prepared.ReminderAtUtc);

        // Merge branch: an equivalent note already exists.
        if (duplicateIndex >= 0)
        {
            var existing = notes[duplicateIndex];
            var retentionPolicy = ResolveDuplicateRetentionPolicy(existing, prepared);
            // Keep the old expiry when the user had approved retention and this repeat sighting did not, so an approved note is not shortened.
            var expiresAtUtc = prepared.ExpiresAtUtc
                ?? (existing.UserApprovedRetention && !prepared.UserApprovedRetention
                    ? existing.ExpiresAtUtc
                    : MemoryPolicies.ResolveExpiryUtc(
                        retentionPolicy,
                        existing.CreatedAtUtc,
                        prepared.ReminderAtUtc ?? existing.ReminderAtUtc,
                        string.IsNullOrWhiteSpace(prepared.ReminderStatus) ? existing.ReminderStatus : prepared.ReminderStatus));

            notes[duplicateIndex] = NormalizeNote(existing with
            {
                Source = prepared.Source,
                Privacy = prepared.Privacy,
                Tags = MergeTags(existing.Tags, prepared.Tags),
                Context = prepared.Context ?? existing.Context,
                ObservationCount = existing.ObservationCount + 1,
                LastObservedAtUtc = prepared.CreatedAtUtc,
                SummaryWindowStartUtc = prepared.SummaryWindowStartUtc ?? existing.SummaryWindowStartUtc,
                SummaryWindowEndUtc = prepared.SummaryWindowEndUtc ?? existing.SummaryWindowEndUtc,
                ImportanceScore = MemoryPolicies.BlendImportance(existing, prepared),
                RetentionPolicy = retentionPolicy,
                ExpiresAtUtc = expiresAtUtc,
                UserApprovedRetention = existing.UserApprovedRetention || prepared.UserApprovedRetention,
                Entities = MergeEntities(existing.Entities, prepared.Entities),
                ReminderAtUtc = prepared.ReminderAtUtc ?? existing.ReminderAtUtc,
                ReminderText = string.IsNullOrWhiteSpace(prepared.ReminderText) ? existing.ReminderText : prepared.ReminderText.Trim(),
                ReminderStatus = string.IsNullOrWhiteSpace(prepared.ReminderStatus)
                    ? existing.ReminderStatus
                    : MemoryPolicies.NormalizeReminderStatus(prepared.ReminderStatus),
                // Seeing a forgotten fact again brings it back.
                IsForgotten = false,
                ForgottenAtUtc = null,
                ForgottenReason = string.Empty
            });

            return;
        }

        // New-note branch: nothing equivalent exists, so create a fresh note with a random id.
        var createdAtUtc = prepared.CreatedAtUtc ?? DateTimeOffset.UtcNow;
        var retention = MemoryPolicies.ResolveRetentionPolicy(prepared);
        var reminderStatus = MemoryPolicies.NormalizeReminderStatus(prepared.ReminderStatus);

        notes.Add(new MemoryNote(
            Guid.NewGuid().ToString("n"),
            prepared.Content.Trim(),
            createdAtUtc,
            prepared.Kind,
            prepared.Category,
            prepared.Source,
            prepared.Privacy,
            prepared.Tags,
            prepared.Context ?? InteractionContextSnapshot.Empty,
            1,
            createdAtUtc,
            prepared.SummaryWindowStartUtc,
            prepared.SummaryWindowEndUtc,
            prepared.ImportanceScore ?? MemoryPolicies.ResolveImportanceScore(prepared),
            retention,
            prepared.ExpiresAtUtc ?? MemoryPolicies.ResolveExpiryUtc(prepared, createdAtUtc),
            prepared.UserApprovedRetention,
            NormalizeEntities(prepared.Entities),
            prepared.ReminderAtUtc?.ToUniversalTime(),
            string.IsNullOrWhiteSpace(prepared.ReminderText) ? string.Empty : prepared.ReminderText.Trim(),
            reminderStatus));
    }

    // Cleans a write request: trims text, lower-cases labels, and fills in default importance and retention.
    private static MemoryWriteRequest PrepareWriteRequest(MemoryWriteRequest request)
    {
        var content = request.Content?.Trim() ?? string.Empty;
        var kind = NormalizeLabel(request.ReminderAtUtc is not null && string.Equals(request.Kind, "note", StringComparison.OrdinalIgnoreCase)
            ? "reminder"
            : request.Kind, "note");

        return request with
        {
            Content = content,
            Kind = kind,
            Category = NormalizeLabel(request.Category, kind == "preference" ? "other" : string.Empty),
            Source = NormalizeLabel(request.Source, "manual"),
            Privacy = NormalizeLabel(request.Privacy, "persistent"),
            Tags = NormalizeTags(request.Tags),
            Context = request.Context ?? InteractionContextSnapshot.Empty,
            ImportanceScore = request.ImportanceScore is double score ? MemoryPolicies.ClampImportance(score) : MemoryPolicies.ResolveImportanceScore(request),
            RetentionPolicy = MemoryPolicies.ResolveRetentionPolicy(request),
            ExpiresAtUtc = request.ExpiresAtUtc?.ToUniversalTime(),
            Entities = NormalizeEntities(request.Entities),
            ReminderAtUtc = request.ReminderAtUtc?.ToUniversalTime(),
            ReminderText = string.IsNullOrWhiteSpace(request.ReminderText) ? string.Empty : request.ReminderText.Trim(),
            ReminderStatus = MemoryPolicies.NormalizeReminderStatus(request.ReminderStatus)
        };
    }

    // Drops notes that were forgotten or have passed their expiry time.
    private static IReadOnlyList<MemoryNote> GetActiveNotes(IReadOnlyList<MemoryNote> notes, DateTimeOffset nowUtc)
    {
        return notes
            .Where(note => MemoryPolicies.IsActive(note, nowUtc))
            .ToArray();
    }

    // Turns one note into 'entity' entries for the profile: one per attached entity or, for routine/habit/favorite
    // notes that have no entities, the note text itself.
    private static IEnumerable<(MemoryNote Note, string Kind, string Name, string Summary, string[] Aliases, MemoryAttribute[] Attributes)> BuildStructuredEntries(MemoryNote note)
    {
        if (note.Entities is { Length: > 0 })
        {
            foreach (var entity in note.Entities)
            {
                if (string.IsNullOrWhiteSpace(entity.Name))
                {
                    continue;
                }

                yield return (
                    note,
                    NormalizeLabel(entity.Kind, "other"),
                    entity.Name.Trim(),
                    string.IsNullOrWhiteSpace(entity.Summary) ? note.Content.Trim() : entity.Summary.Trim(),
                    entity.SafeAliases,
                    entity.SafeAttributes);
            }
        }

        if (note.Entities is not { Length: > 0 }
            && note.Category is "routines" or "habits" or "favorites")
        {
            yield return (
                note,
                note.Category,
                note.Content.Trim(),
                note.Content.Trim(),
                Array.Empty<string>(),
                Array.Empty<MemoryAttribute>());
        }
    }

    // Converts a reminder note into the small read-only shape used by the profile.
    private static ReminderSnapshot ToReminderSnapshot(MemoryNote note)
    {
        return new ReminderSnapshot(
            note.Id,
            string.IsNullOrWhiteSpace(note.ReminderText) ? note.Content.Trim() : note.ReminderText.Trim(),
            note.ReminderAtUtc ?? note.CreatedAtUtc,
            MemoryPolicies.NormalizeReminderStatus(note.ReminderStatus),
            note.Id);
    }

    // Picks the retention policy for a repeated note without downgrading one the user explicitly approved.
    private static string ResolveDuplicateRetentionPolicy(MemoryNote existing, MemoryWriteRequest incoming)
    {
        if (existing.UserApprovedRetention && !incoming.UserApprovedRetention && string.IsNullOrWhiteSpace(incoming.RetentionPolicy))
        {
            return NormalizeLabel(existing.RetentionPolicy, "standard");
        }

        return MemoryPolicies.ResolveRetentionPolicy(incoming);
    }

    // Looks for an existing note that means the same thing: same kind, category and text (ignoring case). Summaries must also
    // cover the same time window and reminders the same reminder time. Returns its index, or -1.
    private static int FindDuplicateIndex(
        IReadOnlyList<MemoryNote> notes,
        string content,
        string kind,
        string category,
        DateTimeOffset? summaryWindowStartUtc,
        DateTimeOffset? summaryWindowEndUtc,
        DateTimeOffset? reminderAtUtc)
    {
        for (var index = 0; index < notes.Count; index++)
        {
            var note = notes[index];

            if (note.IsForgotten
                || !string.Equals(note.Kind, kind, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(note.Category, category, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(note.Content.Trim(), content, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (kind == "summary")
            {
                if (note.SummaryWindowStartUtc != summaryWindowStartUtc
                    || note.SummaryWindowEndUtc != summaryWindowEndUtc)
                {
                    continue;
                }
            }

            if ((kind == "reminder" || reminderAtUtc is not null)
                && note.ReminderAtUtc != reminderAtUtc)
            {
                continue;
            }

            return index;
        }

        return -1;
    }

    // Reads memory.json tolerantly: accepts a JSON array, an object with an 'items' array, or a single note object (older file shapes).
    private static async Task<List<MemoryNote>> ReadNotesFromJsonAsync(string path, CancellationToken cancellationToken)
    {
        var text = await File.ReadAllTextAsync(path, cancellationToken);

        if (string.IsNullOrWhiteSpace(text))
        {
            return new List<MemoryNote>();
        }

        using var document = JsonDocument.Parse(text);

        return document.RootElement.ValueKind switch
        {
            JsonValueKind.Array => DeserializeArray(document.RootElement),
            JsonValueKind.Object when TryGetProperty(document.RootElement, "items", out var items) && items.ValueKind == JsonValueKind.Array => DeserializeArray(items),
            JsonValueKind.Object => DeserializeSingle(document.RootElement),
            _ => new List<MemoryNote>()
        };
    }

    // Reads each element as a note, skipping nulls and cleaning each one.
    private static List<MemoryNote> DeserializeArray(JsonElement element)
    {
        var notes = new List<MemoryNote>();

        foreach (var item in element.EnumerateArray())
        {
            var note = item.Deserialize<MemoryNote>(JsonOptions);

            if (note is not null)
            {
                notes.Add(NormalizeNote(note));
            }
        }

        return notes;
    }

    private static List<MemoryNote> DeserializeSingle(JsonElement element)
    {
        var note = element.Deserialize<MemoryNote>(JsonOptions);
        return note is null
            ? new List<MemoryNote>()
            : new List<MemoryNote> { NormalizeNote(note) };
    }

    // Cleans a note loaded from disk or about to be saved: trims, converts times to UTC, fills a missing id and labels, and works out
    // retention and expiry for older notes that lack them. A stored importance of 0 or less is treated as the default 0.35.
    private static MemoryNote NormalizeNote(MemoryNote note)
    {
        var content = note.Content?.Trim() ?? string.Empty;
        var createdAtUtc = note.CreatedAtUtc == default ? DateTimeOffset.UtcNow : note.CreatedAtUtc.ToUniversalTime();
        var lastObservedAtUtc = note.LastObservedAtUtc?.ToUniversalTime() ?? createdAtUtc;
        var retentionPolicy = string.IsNullOrWhiteSpace(note.RetentionPolicy)
            ? MemoryPolicies.ResolveRetentionPolicy(new MemoryWriteRequest(
                content,
                Kind: note.Kind,
                Category: note.Category,
                Source: note.Source,
                Privacy: note.Privacy,
                Tags: note.Tags,
                Context: note.Context,
                CreatedAtUtc: createdAtUtc,
                SummaryWindowStartUtc: note.SummaryWindowStartUtc,
                SummaryWindowEndUtc: note.SummaryWindowEndUtc,
                ImportanceScore: note.ImportanceScore,
                Entities: note.Entities,
                ReminderAtUtc: note.ReminderAtUtc,
                ReminderText: note.ReminderText,
                ReminderStatus: note.ReminderStatus))
            : MemoryPolicies.NormalizeRetentionPolicy(note.RetentionPolicy);
        var reminderAtUtc = note.ReminderAtUtc?.ToUniversalTime();
        var reminderStatus = MemoryPolicies.NormalizeReminderStatus(note.ReminderStatus);
        var expiresAtUtc = note.ExpiresAtUtc?.ToUniversalTime()
            ?? MemoryPolicies.ResolveExpiryUtc(retentionPolicy, createdAtUtc, reminderAtUtc, reminderStatus);

        return note with
        {
            Id = string.IsNullOrWhiteSpace(note.Id) ? Guid.NewGuid().ToString("n") : note.Id.Trim(),
            Content = content,
            CreatedAtUtc = createdAtUtc,
            Kind = NormalizeLabel(note.Kind, "note"),
            Category = NormalizeLabel(note.Category, string.Empty),
            Source = NormalizeLabel(note.Source, "manual"),
            Privacy = NormalizeLabel(note.Privacy, "persistent"),
            Tags = NormalizeTags(note.Tags),
            Context = note.Context ?? InteractionContextSnapshot.Empty,
            ObservationCount = Math.Max(1, note.ObservationCount),
            LastObservedAtUtc = lastObservedAtUtc,
            SummaryWindowStartUtc = note.SummaryWindowStartUtc?.ToUniversalTime(),
            SummaryWindowEndUtc = note.SummaryWindowEndUtc?.ToUniversalTime(),
            ImportanceScore = note.ImportanceScore <= 0 ? 0.35d : MemoryPolicies.ClampImportance(note.ImportanceScore),
            RetentionPolicy = retentionPolicy,
            ExpiresAtUtc = expiresAtUtc,
            Entities = NormalizeEntities(note.Entities),
            ReminderAtUtc = reminderAtUtc,
            ReminderText = string.IsNullOrWhiteSpace(note.ReminderText) ? string.Empty : note.ReminderText.Trim(),
            ReminderStatus = reminderStatus,
            ForgottenAtUtc = note.ForgottenAtUtc?.ToUniversalTime(),
            ForgottenReason = string.IsNullOrWhiteSpace(note.ForgottenReason) ? string.Empty : note.ForgottenReason.Trim()
        };
    }

    // Cleans the entity list and merges entries that are really the same thing (same kind, a shared name or alias, no conflicting attributes).
    private static StructuredMemoryReference[] NormalizeEntities(StructuredMemoryReference[]? entities)
    {
        var normalized = (entities ?? Array.Empty<StructuredMemoryReference>())
            .Where(entity => !string.IsNullOrWhiteSpace(entity.Name))
            .Select(entity => new StructuredMemoryReference(
                NormalizeLabel(entity.Kind, "other"),
                entity.Name.Trim(),
                string.IsNullOrWhiteSpace(entity.Summary) ? string.Empty : entity.Summary.Trim(),
                NormalizeAliases(entity.Aliases),
                NormalizeAttributes(entity.Attributes)))
            .ToList();
        var merged = new List<StructuredMemoryReference>();

        foreach (var candidate in normalized)
        {
            var existingIndex = merged.FindIndex(existing => ShouldMergeEntity(existing, candidate));

            if (existingIndex < 0)
            {
                merged.Add(candidate);
                continue;
            }

            var existing = merged[existingIndex];
            var summary = SelectEntitySummary(existing, candidate);
            var aliases = NormalizeAliases(
                existing.SafeAliases
                    .Append(existing.Name)
                    .Concat(candidate.SafeAliases)
                    .Append(candidate.Name)
                    .ToArray());
            var attributes = NormalizeAttributes(existing.SafeAttributes.Concat(candidate.SafeAttributes));
            var canonicalName = SelectCanonicalEntityName(existing.Name, candidate.Name, aliases, attributes);
            var canonicalAliases = NormalizeAliases(aliases.Where(alias => !string.Equals(alias, canonicalName, StringComparison.OrdinalIgnoreCase)).ToArray());

            merged[existingIndex] = existing with
            {
                Name = canonicalName,
                Summary = summary,
                Aliases = canonicalAliases,
                Attributes = attributes
            };
        }

        return merged
            .OrderBy(entity => entity.Kind, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entity => entity.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    // Trims, removes duplicates (ignoring case) and sorts alternative names.
    private static string[] NormalizeAliases(string[]? aliases)
    {
        return (aliases ?? Array.Empty<string>())
            .Where(alias => !string.IsNullOrWhiteSpace(alias))
            .Select(alias => alias.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(alias => alias, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    // Drops empty key/value pairs and keeps one value per key; the last one wins, so newer information overrides older.
    private static MemoryAttribute[] NormalizeAttributes(IEnumerable<MemoryAttribute>? attributes)
    {
        return (attributes ?? Array.Empty<MemoryAttribute>())
            .Where(attribute => !string.IsNullOrWhiteSpace(attribute.Key) && !string.IsNullOrWhiteSpace(attribute.Value))
            .Select(attribute => new MemoryAttribute(attribute.Key.Trim(), attribute.Value.Trim()))
            .GroupBy(attribute => attribute.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .OrderBy(attribute => attribute.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static StructuredMemoryReference[] MergeEntities(StructuredMemoryReference[]? existing, StructuredMemoryReference[]? incoming)
    {
        return NormalizeEntities((existing ?? Array.Empty<StructuredMemoryReference>())
            .Concat(incoming ?? Array.Empty<StructuredMemoryReference>())
            .ToArray());
    }

    // Trims, removes duplicates and sorts tags.
    private static string[] NormalizeTags(IEnumerable<string>? tags)
    {
        return (tags ?? Array.Empty<string>())
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string[] MergeTags(string[]? existing, string[]? incoming)
    {
        return NormalizeTags((existing ?? Array.Empty<string>()).Concat(incoming ?? Array.Empty<string>()));
    }

    // Lower-cases and trims a label such as a kind or category, using the fallback when it is empty.
    private static string NormalizeLabel(string? value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim().ToLowerInvariant();
    }

    // Two entities are the same if they have the same kind, share at least one name or alias, and do not contradict each other on a disambiguating attribute.
    private static bool ShouldMergeEntity(StructuredMemoryReference existing, StructuredMemoryReference candidate)
    {
        if (!string.Equals(existing.Kind, candidate.Kind, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (HasConflictingEntityDisambiguators(existing.SafeAttributes, candidate.SafeAttributes))
        {
            return false;
        }

        var existingNames = BuildEntityNameSet(existing);
        var candidateNames = BuildEntityNameSet(candidate);
        return existingNames.Overlaps(candidateNames);
    }

    // Every name an entity goes by (main name plus aliases), normalized for comparison.
    private static HashSet<string> BuildEntityNameSet(StructuredMemoryReference entity)
    {
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddNormalizedEntityName(values, entity.Name);

        foreach (var alias in entity.SafeAliases)
        {
            AddNormalizedEntityName(values, alias);
        }

        return values;
    }

    private static void AddNormalizedEntityName(ISet<string> values, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var normalized = NormalizeEntityName(name);

        if (!string.IsNullOrWhiteSpace(normalized))
        {
            values.Add(normalized);
        }
    }

    // Lower-cases, replaces punctuation with spaces and collapses repeated spaces, so 'Mom!' and 'mom' compare equal.
    private static string NormalizeEntityName(string value)
    {
        var characters = value
            .Trim()
            .ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) || char.IsWhiteSpace(character) ? character : ' ')
            .ToArray();
        return string.Join(' ', new string(characters).Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    // True when both entities give a different value for an attribute that tells similar things apart (label, role, ...), meaning they are different things that happen to share a name.
    private static bool HasConflictingEntityDisambiguators(
        IReadOnlyList<MemoryAttribute> existing,
        IReadOnlyList<MemoryAttribute> candidate)
    {
        foreach (var key in new[] { "label", "devicetype", "role", "relationship", "anchor", "cadence" })
        {
            var left = existing.FirstOrDefault(attribute => string.Equals(attribute.Key, key, StringComparison.OrdinalIgnoreCase))?.Value;
            var right = candidate.FirstOrDefault(attribute => string.Equals(attribute.Key, key, StringComparison.OrdinalIgnoreCase))?.Value;

            if (!string.IsNullOrWhiteSpace(left)
                && !string.IsNullOrWhiteSpace(right)
                && !string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    // When merging, keep the longer summary (assumed to be the more informative one).
    private static string SelectEntitySummary(StructuredMemoryReference existing, StructuredMemoryReference candidate)
    {
        if (string.IsNullOrWhiteSpace(existing.Summary))
        {
            return candidate.Summary;
        }

        if (string.IsNullOrWhiteSpace(candidate.Summary))
        {
            return existing.Summary;
        }

        return candidate.Summary.Length > existing.Summary.Length
            ? candidate.Summary
            : existing.Summary;
    }

    // The shortest name becomes the main name; the others are kept as aliases. (The aliases/attributes parameters are only a fallback.)
    private static string SelectCanonicalEntityName(
        string existingName,
        string candidateName,
        IReadOnlyList<string> aliases,
        IReadOnlyList<MemoryAttribute> attributes)
    {
        return new[] { existingName, candidateName }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .OrderBy(value => value.Length)
            .ThenBy(value => value, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault()
            ?? aliases.OrderBy(value => value.Length).ThenBy(value => value, StringComparer.OrdinalIgnoreCase).FirstOrDefault()
            ?? existingName;
    }

    // Builds a text key meaning 'the same entity' across notes: its normalized names plus its disambiguating attributes.
    private static string BuildStructuredIdentityKey(
        string kind,
        string name,
        IReadOnlyList<string> aliases,
        IReadOnlyList<MemoryAttribute> attributes)
    {
        var normalizedNames = aliases
            .Append(name)
            .Select(NormalizeEntityName)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var discriminator = string.Join(
            "|",
            attributes
                .Where(attribute => attribute.Key is not null)
                .Select(attribute => (Key: NormalizeLabel(attribute.Key, string.Empty), Value: NormalizeEntityName(attribute.Value)))
                .Where(attribute => attribute.Key is "label" or "devicetype" or "role" or "relationship" or "anchor" or "cadence")
                .OrderBy(attribute => attribute.Key, StringComparer.OrdinalIgnoreCase)
                .ThenBy(attribute => attribute.Value, StringComparer.OrdinalIgnoreCase)
                .Select(attribute => $"{attribute.Key}:{attribute.Value}"));

        return $"{NormalizeLabel(kind, "other")}|{string.Join("/", normalizedNames)}|{discriminator}";
    }

    private void EnsureParentDirectoryExists()
    {
        EnsureDirectoryExists(Path.GetDirectoryName(_filePath));
    }

    private static void EnsureDirectoryExists(string? directory)
    {
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    // Case-insensitive JSON property lookup (JsonElement.TryGetProperty is case-sensitive).
    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }
}
