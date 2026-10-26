using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.CcSync.State;

public static class RunKinds
{
    public const string Sync = "sync";
    public const string Flush = "flush";
}

public static class RunOutcomes
{
    public const string Running = "running";
    public const string Applied = "applied";
    public const string NothingToDo = "nothing-to-do";
    public const string DryRun = "dry-run";
    public const string Guarded = "guarded";
    public const string Aborted = "aborted";
    public const string Waiting = "waiting";
}

public sealed record RunRecord(string Id, string Kind, DateTime StartedUtc, DateTime? FinishedUtc, string Outcome, string Summary);

/// <summary>Run summaries plus a small key/value table for the last match report and the stored preview plan.</summary>
public sealed class RunStore
{
    public const string MatchReportKey = "match-report";
    public const string PreviewPlanKey = "preview-plan";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly StateDb _db;

    public RunStore(StateDb db)
    {
        _db = db;
    }

    public RunRecord Start(string kind, DateTime nowUtc)
    {
        var id = kind + "-" + nowUtc.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..6];
        using var connection = _db.Open();
        connection.Execute(
            "INSERT INTO runs (id, kind, started_utc, outcome, summary) VALUES ($id, $kind, $started, 'running', '{}')",
            ("$id", id),
            ("$kind", kind),
            ("$started", StateDb.FormatTime(nowUtc)));
        return new RunRecord(id, kind, nowUtc, null, RunOutcomes.Running, "{}");
    }

    public void Finish(string id, string outcome, object summary, DateTime nowUtc)
    {
        using var connection = _db.Open();
        connection.Execute(
            "UPDATE runs SET outcome = $outcome, summary = $summary, finished_utc = $finished WHERE id = $id",
            ("$id", id),
            ("$outcome", outcome),
            ("$summary", summary as string ?? JsonSerializer.Serialize(summary, summary.GetType(), JsonOptions)),
            ("$finished", StateDb.FormatTime(nowUtc)));
    }

    public RunRecord? Last(string kind) => Recent(kind, 1).FirstOrDefault();

    public IReadOnlyList<RunRecord> Recent(string? kind, int limit)
    {
        using var connection = _db.Open();
        return connection.Query(
            "SELECT id, kind, started_utc, finished_utc, outcome, summary FROM runs WHERE ($kind IS NULL OR kind = $kind) ORDER BY started_utc DESC LIMIT $limit",
            Map,
            ("$kind", kind),
            ("$limit", limit));
    }

    public string? GetValue(string key)
    {
        using var connection = _db.Open();
        return connection.Scalar("SELECT value FROM kv WHERE key = $key", ("$key", key)) as string;
    }

    public void SetValue(string key, string value)
    {
        using var connection = _db.Open();
        connection.Execute(
            "INSERT INTO kv (key, value) VALUES ($key, $value) ON CONFLICT(key) DO UPDATE SET value = excluded.value",
            ("$key", key),
            ("$value", value));
    }

    public void RemoveValue(string key)
    {
        using var connection = _db.Open();
        connection.Execute("DELETE FROM kv WHERE key = $key", ("$key", key));
    }

    public T? GetJson<T>(string key)
        where T : class
        => GetValue(key) is { } json ? JsonSerializer.Deserialize<T>(json, JsonOptions) : null;

    public void SetJson<T>(string key, T value) => SetValue(key, JsonSerializer.Serialize(value, JsonOptions));

    public int DeleteFinishedBefore(DateTime cutoffUtc)
    {
        using var connection = _db.Open();
        return connection.Execute(
            "DELETE FROM runs WHERE outcome <> 'running' AND started_utc < $cutoff",
            ("$cutoff", StateDb.FormatTime(cutoffUtc)));
    }

    /// <summary>Marks runs left as running by a server stop as aborted.</summary>
    public int AbortDangling(DateTime nowUtc)
    {
        using var connection = _db.Open();
        return connection.Execute(
            "UPDATE runs SET outcome = 'aborted', finished_utc = $now, summary = '{\"AbortReason\":\"server stopped during the run\"}' WHERE outcome = 'running'",
            ("$now", StateDb.FormatTime(nowUtc)));
    }

    private static RunRecord Map(SqliteDataReader r) => new(
        r.GetString(0),
        r.GetString(1),
        StateDb.ParseTime(r.GetString(2)),
        StateDb.ParseTimeOrNull(r.StringOrNull(3)),
        r.GetString(4),
        r.GetString(5));
}
