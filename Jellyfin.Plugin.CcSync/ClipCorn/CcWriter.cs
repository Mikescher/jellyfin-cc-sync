using System.Globalization;
using Jellyfin.Plugin.CcSync.ClipCorn.Model;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.CcSync.ClipCorn;

/// <summary>One viewing to add; <paramref name="SeriesId"/> only for episodes.</summary>
public sealed record CcWriteRequest(long PendingId, string ElementId, CcKind Kind, CcDateTime Entry, string? SeriesId);

public enum CcWriteResult
{
    Written,
    Duplicate,
    Failed,
}

public sealed record CcWriteOutcome(
    long PendingId,
    CcWriteResult Result,
    string Entry,
    bool WatchLaterRemoved,
    bool SeriesWatchLaterRemoved,
    string? Note);

/// <summary>The whole session was rolled back; every update stays pending.</summary>
public sealed class CcWriteAbortedException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Adds viewings to <c>ClipCornUserData.db</c> — the only code that opens a live jClipCorn file for
/// writing (recipe: <c>docs/clipcorn-db.md</c> § Write recipe). Callers guarantee that no
/// jClipCorn lock is held and that a backup exists.
/// </summary>
public static class CcWriter
{
    private const string DefaultList = "[]";
    private const int DefaultScore = CcScore.NoRatingValue;

    /// <param name="userDbPath">The live user-data file.</param>
    /// <param name="mainDuuid">DUUID of the main DB the requests were resolved against.</param>
    /// <param name="requests">The viewings, applied in order.</param>
    /// <param name="resetWatchLater">Remove tag 3 from the element (and an episode's series).</param>
    /// <param name="checkBeforeCommit">Returns a reason to roll back, e.g. a lock that appeared.</param>
    public static IReadOnlyList<CcWriteOutcome> Apply(
        string userDbPath,
        string mainDuuid,
        IReadOnlyList<CcWriteRequest> requests,
        bool resetWatchLater,
        Func<string?> checkBeforeCommit)
    {
        // No immutable flag here and no change to journal_mode: this is jClipCorn's file.
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = userDbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
            DefaultTimeout = 5,
        };

        try
        {
            using var connection = new SqliteConnection(builder.ToString());
            connection.Open();

            // The JCCTRIGGER_AUTOHISTORY_* triggers rely on it, as in jClipCorn.
            Execute(connection, "PRAGMA recursive_triggers = true");
            Execute(connection, "BEGIN IMMEDIATE");

            var committed = false;
            try
            {
                CheckInfo(connection, mainDuuid);

                var outcomes = requests.Select(r => ApplyOne(connection, r, resetWatchLater)).ToList();

                if (checkBeforeCommit() is { } reason)
                {
                    throw new CcWriteAbortedException(reason);
                }

                Execute(connection, "COMMIT");
                committed = true;
                return outcomes;
            }
            finally
            {
                if (!committed)
                {
                    TryRollback(connection);
                }
            }
        }
        catch (SqliteException ex)
        {
            throw new CcWriteAbortedException("Writing ClipCornUserData.db failed: " + ex.Message, ex);
        }
    }

    private static void CheckInfo(SqliteConnection connection, string mainDuuid)
    {
        string? Info(string key) => Convert.ToString(Scalar(connection, "SELECT IVALUE FROM INFO WHERE IKEY = $k", ("$k", key)), CultureInfo.InvariantCulture);

        if (Info("VERSION_DB") != CcSchema.SupportedUserVersion.ToString(CultureInfo.InvariantCulture))
        {
            throw new CcWriteAbortedException($"ClipCornUserData.db has schema version {Info("VERSION_DB")}; this plugin writes version {CcSchema.SupportedUserVersion} only.");
        }

        if (!string.Equals(Info("MAINDB_DUUID"), mainDuuid, StringComparison.OrdinalIgnoreCase))
        {
            throw new CcWriteAbortedException($"ClipCornUserData.db belongs to database {Info("MAINDB_DUUID")}, not to {mainDuuid}.");
        }
    }

    private static CcWriteOutcome ApplyOne(SqliteConnection connection, CcWriteRequest request, bool resetWatchLater)
    {
        var table = request.Kind switch
        {
            CcKind.Movie => "MOVIES",
            CcKind.Episode => "EPISODES",
            _ => throw new ArgumentException($"Only movies and episodes have a viewed history, not {request.Kind}."),
        };

        var row = Row(connection, $"SELECT VIEWED_HISTORY, TAGS FROM {table} WHERE ID = $id", request.ElementId);
        var historyText = row?[0] ?? DefaultList;
        var tagsText = row?[1] ?? DefaultList;

        if (!CcViewedHistory.TryParse(historyText, out var history, out var error))
        {
            return new CcWriteOutcome(request.PendingId, CcWriteResult.Failed, request.Entry.Raw, false, false,
                $"the existing VIEWED_HISTORY {historyText} does not parse ({error}); jClipCorn reads it as broken, so it is left alone");
        }

        var updated = history.Add(request.Entry, out var added);
        var newHistory = added ? updated.ToJson() : historyText;

        var notes = new List<string>();
        var newTags = tagsText;
        var watchLaterRemoved = false;
        if (resetWatchLater)
        {
            if (CcTagList.TryParse(tagsText, out var tags))
            {
                if (tags.Contains(CcTagList.WatchLater))
                {
                    newTags = tags.Without(CcTagList.WatchLater).ToJson();
                    watchLaterRemoved = true;
                }
            }
            else
            {
                notes.Add($"TAGS {tagsText} does not parse and was left unchanged");
            }
        }

        if (added || watchLaterRemoved)
        {
            Execute(
                connection,
                $"""
                INSERT INTO {table} (ID, VIEWED_HISTORY, TAGS, SCORE, SCORECOMMENT) VALUES ($id, $history, $tags, {DefaultScore}, '')
                ON CONFLICT(ID) DO UPDATE SET VIEWED_HISTORY = excluded.VIEWED_HISTORY, TAGS = excluded.TAGS
                """,
                ("$id", request.ElementId),
                ("$history", newHistory),
                ("$tags", newTags));
        }

        var seriesWatchLaterRemoved = false;
        if (resetWatchLater && request.Kind == CcKind.Episode && request.SeriesId is { } seriesId)
        {
            seriesWatchLaterRemoved = ResetSeriesWatchLater(connection, seriesId, notes);
        }

        return new CcWriteOutcome(
            request.PendingId,
            added ? CcWriteResult.Written : CcWriteResult.Duplicate,
            request.Entry.Raw,
            watchLaterRemoved,
            seriesWatchLaterRemoved,
            notes.Count == 0 ? null : string.Join("; ", notes));
    }

    /// <summary>
    /// Removes "Watch later" from the series' user row; a row that is back to all defaults is
    /// deleted, as jClipCorn keeps user rows sparse.
    /// </summary>
    private static bool ResetSeriesWatchLater(SqliteConnection connection, string seriesId, List<string> notes)
    {
        var row = Row(connection, "SELECT TAGS, SCORE, SCORECOMMENT FROM SERIES WHERE ID = $id", seriesId);
        if (row is null)
        {
            return false;
        }

        if (!CcTagList.TryParse(row[0], out var tags))
        {
            notes.Add($"series TAGS {row[0]} does not parse and was left unchanged");
            return false;
        }

        if (!tags.Contains(CcTagList.WatchLater))
        {
            return false;
        }

        var newTags = tags.Without(CcTagList.WatchLater).ToJson();
        var isDefault = newTags == DefaultList
            && row[1] == DefaultScore.ToString(CultureInfo.InvariantCulture)
            && string.IsNullOrEmpty(row[2]);

        if (isDefault)
        {
            Execute(connection, "DELETE FROM SERIES WHERE ID = $id", ("$id", seriesId));
        }
        else
        {
            Execute(connection, "UPDATE SERIES SET TAGS = $tags WHERE ID = $id", ("$id", seriesId), ("$tags", newTags));
        }

        return true;
    }

    private static string?[]? Row(SqliteConnection connection, string sql, string id)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var values = new string?[reader.FieldCount];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = reader.IsDBNull(i) ? null : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture);
        }

        return values;
    }

    private static void Execute(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        command.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command.ExecuteScalar();
    }

    private static void TryRollback(SqliteConnection connection)
    {
        try
        {
            Execute(connection, "ROLLBACK");
        }
        catch (SqliteException)
        {
            // No transaction left to roll back (BEGIN failed or SQLite already rolled back).
        }
    }
}
