// The rules for how long a memory lives, how important it is, and whether a reminder is still open.
// Used by JsonMemoryStore.cs whenever it saves, updates, filters or ranks notes (and by MemorySemanticSearch.cs for the
// importance and reminder bonuses). All numbers below are hand-picked tuning values, not derived from anything.
namespace Jarvis.Core;

// Stateless helper methods; nothing is stored here.
internal static class MemoryPolicies
{
    // Maps synonyms onto the canonical names (e.g. 'permanent' becomes 'forever'); unknown words are kept as typed, an empty value stays empty.
    public static string NormalizeRetentionPolicy(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "forever" or "permanent" => "forever",
            "long" or "long-term" => "long",
            "short" or "short-term" => "short",
            "standard" or "default" => "standard",
            "episodic" or "medium" => "episodic",
            "rolling" or "rolling-summary" => "rolling",
            "until-complete" or "until complete" => "until-complete",
            var normalized => normalized
        };
    }

    // Chooses a retention policy for a new note: the requested one if any, else by kind: reminders last until done, summaries
    // are 'rolling', preferences/entities/relationships (and routines, habits, favorites) are 'long', everything else is 'standard'.
    public static string ResolveRetentionPolicy(MemoryWriteRequest request)
    {
        var normalized = NormalizeRetentionPolicy(request.RetentionPolicy);

        if (!string.IsNullOrWhiteSpace(normalized))
        {
            return normalized;
        }

        var kind = NormalizeLabel(request.Kind, "note");
        var category = NormalizeLabel(request.Category, string.Empty);

        if (kind == "reminder" || request.ReminderAtUtc is not null)
        {
            return "until-complete";
        }

        if (kind == "summary")
        {
            return "rolling";
        }

        if (kind == "episodic")
        {
            return "episodic";
        }

        if (kind == "preference"
            || kind == "entity"
            || kind == "relationship"
            || category is "routines" or "habits" or "favorites")
        {
            return "long";
        }

        return "standard";
    }

    // Expiry for a new note: an explicit date if given, otherwise derived from its retention policy. Null means never expires.
    public static DateTimeOffset? ResolveExpiryUtc(MemoryWriteRequest request, DateTimeOffset createdAtUtc)
    {
        if (request.ExpiresAtUtc is not null)
        {
            return request.ExpiresAtUtc.Value.ToUniversalTime();
        }

        return ResolveExpiryUtc(ResolveRetentionPolicy(request), createdAtUtc, request.ReminderAtUtc, request.ReminderStatus);
    }

    // Turns a retention policy into an expiry date counted from creation: forever = never, long = 365 days, standard = 180, episodic = 90,
    // rolling = 45, short = 30. An unfinished reminder never expires; a finished one lingers 14 days past its due time. Unknown policies use 180.
    public static DateTimeOffset? ResolveExpiryUtc(
        string retentionPolicy,
        DateTimeOffset createdAtUtc,
        DateTimeOffset? reminderAtUtc = null,
        string? reminderStatus = null)
    {
        var normalized = NormalizeRetentionPolicy(retentionPolicy);

        return normalized switch
        {
            "forever" => null,
            "long" => createdAtUtc.AddDays(365),
            "standard" => createdAtUtc.AddDays(180),
            "episodic" => createdAtUtc.AddDays(90),
            "rolling" => createdAtUtc.AddDays(45),
            "short" => createdAtUtc.AddDays(30),
            "until-complete" when IsReminderInactive(reminderStatus) => (reminderAtUtc ?? createdAtUtc).AddDays(14),
            "until-complete" => null,
            _ => createdAtUtc.AddDays(180)
        };
    }

    // Importance from 0.05 to 1: starts from a base by kind (reminder 0.82 ... plain note 0.36), then adds small bonuses
    // for manual entry, user-approved retention, longer text, wording like 'always' or 'never', attached entities, a reminder
    // time and routine-style categories (preferences get a little extra by category, e.g. address +0.08). An explicit score wins.
    public static double ResolveImportanceScore(MemoryWriteRequest request)
    {
        if (request.ImportanceScore is double explicitScore)
        {
            return ClampImportance(explicitScore);
        }

        var kind = NormalizeLabel(request.Kind, "note");
        var category = NormalizeLabel(request.Category, string.Empty);
        var score = kind switch
        {
            "reminder" => 0.82d,
            "relationship" => 0.80d,
            "entity" => 0.72d,
            "preference" => 0.68d,
            "episodic" => 0.58d,
            "summary" => 0.45d,
            _ => 0.36d
        };

        if (request.Source.Equals("manual", StringComparison.OrdinalIgnoreCase))
        {
            score += 0.07d;
        }

        if (request.UserApprovedRetention)
        {
            score += 0.08d;
        }

        if (!string.IsNullOrWhiteSpace(request.Content))
        {
            var content = request.Content.Trim();

            if (content.Length >= 100)
            {
                score += 0.05d;
            }
            else if (content.Length <= 24)
            {
                score -= 0.04d;
            }

            if (ContainsImportanceCue(content))
            {
                score += 0.06d;
            }
        }

        if (request.Entities is { Length: > 0 })
        {
            score += Math.Min(0.16d, request.Entities.Length * 0.05d);
        }

        if (request.ReminderAtUtc is not null)
        {
            score += 0.10d;
        }

        if (category is "routines" or "habits" or "favorites")
        {
            score += 0.05d;
        }

        if (kind == "preference")
        {
            score += request.Category?.Trim().ToLowerInvariant() switch
            {
                "address" => 0.08d,
                "tone" or "response style" or "conversation" or "phrasing" => 0.06d,
                "apps" or "automations" or "locations" => 0.05d,
                _ => 0.02d
            };
        }

        return ClampImportance(score);
    }

    // Recomputes importance for an existing note: the same base rules, plus up to +0.14 for being seen repeatedly (0.025 per extra sighting)
    // and up to +0.06 for being seen recently, which fades to zero over a year.
    public static double ResolveImportanceScore(MemoryNote note)
    {
        var baseline = ResolveImportanceScore(new MemoryWriteRequest(
            note.Content,
            Kind: note.Kind,
            Category: note.Category,
            Source: note.Source,
            Privacy: note.Privacy,
            Tags: note.Tags,
            Context: note.Context,
            CreatedAtUtc: note.CreatedAtUtc,
            SummaryWindowStartUtc: note.SummaryWindowStartUtc,
            SummaryWindowEndUtc: note.SummaryWindowEndUtc,
            ImportanceScore: null,
            RetentionPolicy: note.RetentionPolicy,
            ExpiresAtUtc: note.ExpiresAtUtc,
            UserApprovedRetention: note.UserApprovedRetention,
            Entities: note.Entities,
            ReminderAtUtc: note.ReminderAtUtc,
            ReminderText: note.ReminderText,
            ReminderStatus: note.ReminderStatus));
        var observationBonus = Math.Min(0.14d, Math.Max(0, note.ObservationCount - 1) * 0.025d);
        var recencyBonus = note.LastObservedAtUtc is DateTimeOffset lastObservedAtUtc
            ? Math.Max(0d, 0.06d - Math.Min(0.06d, (DateTimeOffset.UtcNow - lastObservedAtUtc).TotalDays / 365d * 0.06d))
            : 0d;

        return ClampImportance(baseline + observationBonus + recencyBonus);
    }

    // Importance after a repeat sighting: a 70/30 mix of the existing note's recomputed score and the new request's score, plus 0.03.
    // It never goes below the importance the note already had, so repeats can only raise it.
    public static double BlendImportance(MemoryNote existing, MemoryWriteRequest incoming)
    {
        var incomingScore = incoming.ImportanceScore ?? ResolveImportanceScore(incoming);
        var observedScore = ResolveImportanceScore(existing);
        var blended = Math.Max(existing.ImportanceScore, (observedScore * 0.7d) + (incomingScore * 0.3d) + 0.03d);
        return ClampImportance(blended);
    }

    // A note is usable if it has not been forgotten and has not expired.
    public static bool IsActive(MemoryNote note, DateTimeOffset nowUtc)
    {
        if (note.IsForgotten)
        {
            return false;
        }

        return !IsExpired(note, nowUtc);
    }

    // True once the expiry time is reached; notes without an expiry never expire.
    public static bool IsExpired(MemoryNote note, DateTimeOffset nowUtc)
    {
        return note.ExpiresAtUtc is DateTimeOffset expiresAtUtc
            && expiresAtUtc <= nowUtc;
    }

    // A reminder with a time, not forgotten, whose status is not completed/dismissed/canceled.
    public static bool IsPendingReminder(MemoryNote note)
    {
        return note.ReminderAtUtc is not null
            && !note.IsForgotten
            && !IsReminderInactive(note.ReminderStatus);
    }

    // Standardizes reminder status words ('done' becomes 'completed', 'snooze' becomes 'snoozed'); blank means 'pending'.
    public static string NormalizeReminderStatus(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "pending";
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "done" => "completed",
            "complete" => "completed",
            "dismiss" => "dismissed",
            "snooze" => "snoozed",
            var normalized => normalized
        };
    }

    // Keeps importance within 0.05 to 1 so no note can drop to zero or exceed full importance.
    public static double ClampImportance(double value)
    {
        return Math.Clamp(value, 0.05d, 1d);
    }

    // True for finished reminders (completed, dismissed, canceled).
    private static bool IsReminderInactive(string? value)
    {
        var normalized = NormalizeReminderStatus(value);
        return normalized is "completed" or "dismissed" or "canceled" or "cancelled";
    }

    // True if the text has a word that suggests it matters long-term, such as 'always', 'never', 'favorite' or 'prefer'.
    private static bool ContainsImportanceCue(string content)
    {
        return content.Contains("always ", StringComparison.OrdinalIgnoreCase)
            || content.Contains("never ", StringComparison.OrdinalIgnoreCase)
            || content.Contains("important", StringComparison.OrdinalIgnoreCase)
            || content.Contains("favorite", StringComparison.OrdinalIgnoreCase)
            || content.Contains("prefer", StringComparison.OrdinalIgnoreCase)
            || content.Contains("every ", StringComparison.OrdinalIgnoreCase)
            || content.Contains("each ", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeLabel(string? value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim().ToLowerInvariant();
    }
}
