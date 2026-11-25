using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.CcSync.State;

public static class PendingStatus
{
    public const string Pending = "pending";
    public const string Written = "written";
    public const string Duplicate = "duplicate";
    public const string Failed = "failed";
    public const string Discarded = "discarded";
}

public static class PendingSource
{
    public const string Playback = "playback";
    public const string PlaybackFallback = "playback-fallback";
    public const string Manual = "manual";
}

/// <summary>
/// A Jellyfin watch waiting to be (or already) written into jClipCorn's viewed history.
/// <see cref="DuplicateOf"/> is set on a repeated signal of a watch: the original's id, or 0 when
/// the original is not in state.db.
/// </summary>
public sealed record PendingUpdate(
    long Id,
    DateTime CreatedUtc,
    string Source,
    Guid JfItemId,
    string ItemName,
    string ItemKind,
    string? CcId,
    string MatchKeys,
    DateTime WatchedAtUtc,
    string Status,
    int Attempts,
    DateTime? LastAttemptUtc,
    string? LastError,
    DateTime? FinishedUtc,
    string? CcEntry,
    long? DuplicateOf);

public sealed class PendingStore
{
    private const string Columns =
        "id, created_utc, source, jf_item_id, item_name, item_kind, cc_id, match_keys, watched_at_utc, status, attempts, last_attempt_utc, last_error, finished_utc, cc_entry, duplicate_of";

    private readonly StateDb _db;

    public PendingStore(StateDb db)
    {
        _db = db;
    }

    public PendingUpdate Add(DateTime createdUtc, string source, Guid jfItemId, string itemName, string itemKind, string? ccId, string matchKeys, DateTime watchedAtUtc)
        => Insert(createdUtc, source, jfItemId, itemName, itemKind, ccId, matchKeys, watchedAtUtc, null);

    /// <summary>
    /// Records a repeated watch signal as finished <c>duplicate</c>; it is never written to
    /// jClipCorn. <paramref name="duplicateOf"/> is null when the original is not in state.db.
    /// </summary>
    public PendingUpdate AddDuplicate(DateTime createdUtc, string source, Guid jfItemId, string itemName, string itemKind, string? ccId, string matchKeys, DateTime watchedAtUtc, long? duplicateOf)
        => Insert(createdUtc, source, jfItemId, itemName, itemKind, ccId, matchKeys, watchedAtUtc, duplicateOf ?? 0);

    /// <summary>
    /// The newest watch (not itself a repeated signal) created at or after <paramref name="sinceUtc"/>
    /// for the Jellyfin item or, when given, the jClipCorn element.
    /// </summary>
    public PendingUpdate? FindRecentWatch(Guid jfItemId, string? ccId, DateTime sinceUtc)
    {
        using var connection = _db.Open();
        return connection.Query(
            $"""
            SELECT {Columns} FROM pending
            WHERE duplicate_of IS NULL AND created_utc >= $since
              AND (jf_item_id = $item OR ($cc IS NOT NULL AND cc_id = $cc COLLATE NOCASE))
            ORDER BY id DESC LIMIT 1
            """,
            Map,
            ("$since", StateDb.FormatTime(sinceUtc)),
            ("$item", jfItemId.ToString("N")),
            ("$cc", ccId)).FirstOrDefault();
    }

    /// <param name="duplicateOf">null for a new watch; the original's id, or 0 when it is unknown, for a repeated signal.</param>
    private PendingUpdate Insert(DateTime createdUtc, string source, Guid jfItemId, string itemName, string itemKind, string? ccId, string matchKeys, DateTime watchedAtUtc, long? duplicateOf)
    {
        using var connection = _db.Open();
        var id = (long)connection.Scalar(
            """
            INSERT INTO pending (created_utc, source, jf_item_id, item_name, item_kind, cc_id, match_keys, watched_at_utc, status, finished_utc, duplicate_of)
            VALUES ($created, $source, $item, $name, $kind, $cc, $keys, $watched,
                    CASE WHEN $dup IS NULL THEN 'pending' ELSE 'duplicate' END,
                    CASE WHEN $dup IS NULL THEN NULL ELSE $created END,
                    $dup)
            RETURNING id
            """,
            ("$dup", duplicateOf),
            ("$created", StateDb.FormatTime(createdUtc)),
            ("$source", source),
            ("$item", jfItemId.ToString("N")),
            ("$name", itemName),
            ("$kind", itemKind),
            ("$cc", ccId),
            ("$keys", matchKeys),
            ("$watched", StateDb.FormatTime(watchedAtUtc)))!;
        return Get(connection, id)!;
    }

    public PendingUpdate? Get(long id)
    {
        using var connection = _db.Open();
        return Get(connection, id);
    }

    /// <summary>All updates still to be written, oldest first.</summary>
    public IReadOnlyList<PendingUpdate> GetPending()
    {
        using var connection = _db.Open();
        return connection.Query($"SELECT {Columns} FROM pending WHERE status = 'pending' ORDER BY id", Map);
    }

    public IReadOnlyList<PendingUpdate> List(string? status, int limit, int offset)
    {
        using var connection = _db.Open();
        return connection.Query(
            $"SELECT {Columns} FROM pending WHERE ($status IS NULL OR status = $status) ORDER BY id DESC LIMIT $limit OFFSET $offset",
            Map,
            ("$status", string.IsNullOrEmpty(status) ? null : status),
            ("$limit", limit),
            ("$offset", offset));
    }

    public int Count(string? status)
    {
        using var connection = _db.Open();
        return Convert.ToInt32(connection.Scalar(
            "SELECT COUNT(*) FROM pending WHERE ($status IS NULL OR status = $status)",
            ("$status", string.IsNullOrEmpty(status) ? null : status)));
    }

    public IReadOnlyDictionary<string, int> CountsByStatus()
    {
        using var connection = _db.Open();
        return connection
            .Query("SELECT status, COUNT(*) FROM pending GROUP BY status", r => (r.GetString(0), r.GetInt32(1)))
            .ToDictionary(x => x.Item1, x => x.Item2);
    }

    /// <summary>Whether a still-pending update for the item was created after <paramref name="sinceUtc"/>.</summary>
    public bool HasPendingCreatedAfter(Guid jfItemId, DateTime sinceUtc)
    {
        using var connection = _db.Open();
        return connection.Scalar(
            "SELECT 1 FROM pending WHERE jf_item_id = $item AND status = 'pending' AND created_utc > $since LIMIT 1",
            ("$item", jfItemId.ToString("N")),
            ("$since", StateDb.FormatTime(sinceUtc))) is not null;
    }

    /// <summary>Counts a failed attempt; the update stays pending. Returns the previous error.</summary>
    public string? RecordAttempt(long id, DateTime nowUtc, string error)
    {
        using var connection = _db.Open();
        var previous = connection.Scalar("SELECT last_error FROM pending WHERE id = $id", ("$id", id)) as string;
        connection.Execute(
            "UPDATE pending SET attempts = attempts + 1, last_attempt_utc = $now, last_error = $error WHERE id = $id AND status = 'pending'",
            ("$id", id),
            ("$now", StateDb.FormatTime(nowUtc)),
            ("$error", error));
        return previous;
    }

    /// <summary>Ends a pending update; false when it was no longer pending.</summary>
    public bool Finish(long id, string status, DateTime nowUtc, string? ccEntry, string? error)
    {
        using var connection = _db.Open();
        return connection.Execute(
            """
            UPDATE pending SET status = $status, attempts = attempts + 1, last_attempt_utc = $now, finished_utc = $now,
                cc_entry = COALESCE($entry, cc_entry), last_error = $error
            WHERE id = $id AND status = 'pending'
            """,
            ("$id", id),
            ("$status", status),
            ("$now", StateDb.FormatTime(nowUtc)),
            ("$entry", ccEntry),
            ("$error", error)) == 1;
    }

    /// <summary>Records a resolved jClipCorn id (fallback matching) without finishing the update.</summary>
    public void SetCcId(long id, string ccId)
    {
        using var connection = _db.Open();
        connection.Execute("UPDATE pending SET cc_id = $cc WHERE id = $id", ("$id", id), ("$cc", ccId));
    }

    /// <summary><c>failed</c> → <c>pending</c>.</summary>
    public bool Retry(long id)
    {
        using var connection = _db.Open();
        return connection.Execute(
            "UPDATE pending SET status = 'pending', finished_utc = NULL WHERE id = $id AND status = 'failed'",
            ("$id", id)) == 1;
    }

    /// <summary><c>pending</c> or <c>failed</c> → <c>discarded</c>.</summary>
    public bool Discard(long id, DateTime nowUtc)
    {
        using var connection = _db.Open();
        return connection.Execute(
            "UPDATE pending SET status = 'discarded', finished_utc = $now WHERE id = $id AND status IN ('pending', 'failed')",
            ("$id", id),
            ("$now", StateDb.FormatTime(nowUtc))) == 1;
    }

    private static PendingUpdate? Get(SqliteConnection connection, long id)
        => connection.Query($"SELECT {Columns} FROM pending WHERE id = $id", Map, ("$id", id)).FirstOrDefault();

    private static PendingUpdate Map(SqliteDataReader r) => new(
        r.GetInt64(0),
        StateDb.ParseTime(r.GetString(1)),
        r.GetString(2),
        Guid.Parse(r.GetString(3)),
        r.GetString(4),
        r.GetString(5),
        r.StringOrNull(6),
        r.GetString(7),
        StateDb.ParseTime(r.GetString(8)),
        r.GetString(9),
        r.GetInt32(10),
        StateDb.ParseTimeOrNull(r.StringOrNull(11)),
        r.StringOrNull(12),
        StateDb.ParseTimeOrNull(r.StringOrNull(13)),
        r.StringOrNull(14),
        r.IsDBNull(15) ? null : r.GetInt64(15));
}
