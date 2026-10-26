using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.CcSync.State;

/// <summary>History kinds, see <c>docs/sync-semantics.md</c> § History.</summary>
public static class HistoryKinds
{
    public const string JfPlayed = "jf-played";
    public const string JfFavorite = "jf-favorite";
    public const string JfTagAdded = "jf-tag-added";
    public const string JfTagRemoved = "jf-tag-removed";
    public const string PendingCreated = "pending-created";
    public const string PendingWritten = "pending-written";
    public const string PendingDuplicate = "pending-duplicate";
    public const string PendingFailed = "pending-failed";
    public const string PendingDiscarded = "pending-discarded";
    public const string PendingRetry = "pending-retry";
    public const string CcLocked = "cc-locked";
    public const string RunSync = "run-sync";
    public const string RunFlush = "run-flush";

    /// <summary>Dry-run and guard previews record the would-be change under <c>planned-</c> + its kind.</summary>
    public const string PlannedPrefix = "planned-";

    public static IReadOnlyList<string> All { get; } =
    [
        JfPlayed, JfFavorite, JfTagAdded, JfTagRemoved,
        PendingCreated, PendingWritten, PendingDuplicate, PendingFailed, PendingDiscarded, PendingRetry,
        CcLocked, RunSync, RunFlush,
        PlannedPrefix + JfPlayed, PlannedPrefix + JfFavorite, PlannedPrefix + JfTagAdded, PlannedPrefix + JfTagRemoved,
    ];
}

public sealed record HistoryEntry
{
    public long Id { get; init; }

    public DateTime TsUtc { get; init; }

    public string? RunId { get; init; }

    public required string Kind { get; init; }

    public string? JfItemId { get; init; }

    public string? CcId { get; init; }

    public string? ItemName { get; init; }

    public string? ItemKind { get; init; }

    public string? Field { get; init; }

    public string? OldValue { get; init; }

    public string? NewValue { get; init; }

    public string? Detail { get; init; }

    public bool Success { get; init; } = true;
}

public sealed record HistoryQuery(
    string? Kind = null,
    string? Search = null,
    string? RunId = null,
    DateTime? FromUtc = null,
    DateTime? ToUtc = null,
    int Limit = 100,
    int Offset = 0);

public sealed record HistoryPage(int Total, IReadOnlyList<HistoryEntry> Entries);

public sealed class HistoryStore
{
    public const int MaxPageSize = 1000;

    private readonly StateDb _db;

    public HistoryStore(StateDb db)
    {
        _db = db;
    }

    public void Add(HistoryEntry entry) => AddRange([entry]);

    public void AddRange(IEnumerable<HistoryEntry> entries)
    {
        using var connection = _db.Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.Command(
            """
            INSERT INTO history (ts_utc, run_id, kind, jf_item_id, cc_id, item_name, item_kind, field, old_value, new_value, detail, success)
            VALUES ($ts, $run, $kind, $item, $cc, $name, $itemKind, $field, $old, $new, $detail, $success)
            """);
        command.Transaction = transaction;
        var p = new[] { "$ts", "$run", "$kind", "$item", "$cc", "$name", "$itemKind", "$field", "$old", "$new", "$detail", "$success" }
            .Select(name => command.Parameters.Add(name, SqliteType.Text))
            .ToArray();

        foreach (var e in entries)
        {
            object?[] values =
            [
                StateDb.FormatTime(e.TsUtc == default ? DateTime.UtcNow : e.TsUtc), e.RunId, e.Kind, e.JfItemId, e.CcId,
                e.ItemName, e.ItemKind, e.Field, e.OldValue, e.NewValue, e.Detail, e.Success ? 1 : 0,
            ];
            for (var i = 0; i < p.Length; i++)
            {
                p[i].Value = values[i] ?? DBNull.Value;
            }

            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>Newest first. <see cref="HistoryQuery.Kind"/> ending in <c>*</c> matches a prefix.</summary>
    public HistoryPage Query(HistoryQuery query)
    {
        var where = new List<string>();
        var args = new List<(string, object?)>();

        if (!string.IsNullOrEmpty(query.Kind))
        {
            if (query.Kind.EndsWith('*'))
            {
                where.Add("kind LIKE $kind ESCAPE '\\'");
                args.Add(("$kind", EscapeLike(query.Kind[..^1]) + "%"));
            }
            else
            {
                where.Add("kind = $kind");
                args.Add(("$kind", query.Kind));
            }
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            where.Add("(item_name LIKE $search ESCAPE '\\' OR detail LIKE $search ESCAPE '\\' OR old_value LIKE $search ESCAPE '\\'"
                + " OR new_value LIKE $search ESCAPE '\\' OR cc_id = $term COLLATE NOCASE OR jf_item_id = $guid)");
            args.Add(("$search", "%" + EscapeLike(term) + "%"));
            args.Add(("$term", term));
            args.Add(("$guid", Guid.TryParse(term, out var guid) ? guid.ToString("N") : term));
        }

        if (!string.IsNullOrEmpty(query.RunId))
        {
            where.Add("run_id = $run");
            args.Add(("$run", query.RunId));
        }

        if (query.FromUtc is { } from)
        {
            where.Add("ts_utc >= $from");
            args.Add(("$from", StateDb.FormatTime(from)));
        }

        if (query.ToUtc is { } to)
        {
            where.Add("ts_utc < $to");
            args.Add(("$to", StateDb.FormatTime(to)));
        }

        var filter = where.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", where);
        var limit = Math.Clamp(query.Limit, 1, MaxPageSize);
        var offset = Math.Max(0, query.Offset);

        using var connection = _db.Open();
        var total = Convert.ToInt32(connection.Scalar($"SELECT COUNT(*) FROM history {filter}", [.. args]));
        var entries = connection.Query(
            $"""
            SELECT id, ts_utc, run_id, kind, jf_item_id, cc_id, item_name, item_kind, field, old_value, new_value, detail, success
            FROM history {filter} ORDER BY ts_utc DESC, id DESC LIMIT $limit OFFSET $offset
            """,
            Map,
            [.. args, ("$limit", limit), ("$offset", offset)]);

        return new HistoryPage(total, entries);
    }

    public int DeleteOlderThan(DateTime cutoffUtc)
    {
        using var connection = _db.Open();
        return connection.Execute("DELETE FROM history WHERE ts_utc < $cutoff", ("$cutoff", StateDb.FormatTime(cutoffUtc)));
    }

    private static string EscapeLike(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);

    private static HistoryEntry Map(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        TsUtc = StateDb.ParseTime(r.GetString(1)),
        RunId = r.StringOrNull(2),
        Kind = r.GetString(3),
        JfItemId = r.StringOrNull(4),
        CcId = r.StringOrNull(5),
        ItemName = r.StringOrNull(6),
        ItemKind = r.StringOrNull(7),
        Field = r.StringOrNull(8),
        OldValue = r.StringOrNull(9),
        NewValue = r.StringOrNull(10),
        Detail = r.StringOrNull(11),
        Success = r.GetInt64(12) != 0,
    };
}
