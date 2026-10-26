using System.Globalization;
using Jellyfin.Plugin.CcSync.ClipCorn.Model;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.CcSync.ClipCorn;

public sealed class CcReadException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Reads a snapshot pair into a <see cref="CcLibrary"/>, all or nothing: an integrity, version or
/// pairing problem, or a table that cannot be read to the end, throws. A JSON column that does not
/// parse only marks its element (<see cref="CcElement.Problem"/>).
/// </summary>
public static class CcReader
{
    public static CcLibrary Read(string mainDbPath, string userDbPath, CancellationToken cancellationToken)
    {
        try
        {
            using var main = CcSqlite.OpenSnapshot(mainDbPath);
            using var user = CcSqlite.OpenSnapshot(userDbPath);

            QuickCheck(main, CcFiles.MainDbName);
            QuickCheck(user, CcFiles.UserDbName);

            var info = CcSchema.ReadInfo(main, user);
            var problems = CcSchema.Check(info);
            if (problems.Count > 0)
            {
                throw new CcSchemaException(string.Join(" ", problems));
            }

            cancellationToken.ThrowIfCancellationRequested();

            var userMovies = ReadUserRows(user, "SELECT ID, VIEWED_HISTORY, TAGS, SCORE FROM MOVIES", true, true);
            var userSeries = ReadUserRows(user, "SELECT ID, NULL, TAGS, SCORE FROM SERIES", false, true);
            var userSeasons = ReadUserRows(user, "SELECT ID, NULL, NULL, SCORE FROM SEASONS", false, false);
            var userEpisodes = ReadUserRows(user, "SELECT ID, VIEWED_HISTORY, TAGS, SCORE FROM EPISODES", true, true);
            cancellationToken.ThrowIfCancellationRequested();

            var groups = Query(main, "SELECT NAME, PARENTGROUP FROM GROUPS", r => new CcGroup(Text(r, 0), Text(r, 1)));

            var movies = Query(main, "SELECT ID, NAME, ZYKLUS, ZYKLUSNUMBER, MOVIEYEAR, ONLINEREF, GROUPS FROM MOVIES", r =>
            {
                var id = Text(r, 0);
                var row = Take(userMovies, id);
                var issues = new List<string>();
                var refs = ParseRefs(Text(r, 5), issues);
                var groupList = ParseGroups(Text(r, 6), issues);
                var history = ParseHistory(row, issues);
                var score = ParseScore(row, issues);
                return new CcMovie(id, Text(r, 1), Text(r, 2), Int(r, 3) ?? -1, Year(r, 4), refs, groupList, history, Tags(row), score, Join(issues));
            });
            cancellationToken.ThrowIfCancellationRequested();

            var series = Query(main, "SELECT ID, NAME, ONLINEREF, GROUPS FROM SERIES", r =>
            {
                var id = Text(r, 0);
                var row = Take(userSeries, id);
                var issues = new List<string>();
                var refs = ParseRefs(Text(r, 2), issues);
                var groupList = ParseGroups(Text(r, 3), issues);
                var score = ParseScore(row, issues);
                return new CcSeries(id, Text(r, 1), refs, groupList, Tags(row), score, Join(issues));
            });

            var seasons = Query(main, "SELECT ID, SERIESID, NAME, SEASONYEAR, ONLINEREF FROM SEASONS", r =>
            {
                var id = Text(r, 0);
                var row = Take(userSeasons, id);
                var issues = new List<string>();
                var refs = ParseRefs(Text(r, 4), issues);
                var score = ParseScore(row, issues);
                return new CcSeason(id, Text(r, 1), Text(r, 2), Year(r, 3), refs, score, Join(issues));
            });
            cancellationToken.ThrowIfCancellationRequested();

            var episodes = Query(main, "SELECT ID, SEASONID, EPISODE, NAME FROM EPISODES", r =>
            {
                var id = Text(r, 0);
                var row = Take(userEpisodes, id);
                var issues = new List<string>();
                var history = ParseHistory(row, issues);
                var score = ParseScore(row, issues);
                return new CcEpisode(id, Text(r, 1), Int(r, 2) ?? 0, Text(r, 3), history, Tags(row), score, Join(issues));
            });

            return new CcLibrary(info, movies, series, seasons, episodes, groups)
            {
                OrphanUserRows = userMovies.Count + userSeries.Count + userSeasons.Count + userEpisodes.Count,
            };
        }
        catch (SqliteException ex)
        {
            throw new CcReadException("Reading the jClipCorn snapshot failed: " + ex.Message, ex);
        }
    }

    private static void QuickCheck(SqliteConnection connection, string name)
    {
        var results = Query(connection, "PRAGMA quick_check", r => Text(r, 0));
        if (results.Count != 1 || results[0] != "ok")
        {
            throw new CcReadException($"{name} failed the integrity check: {string.Join("; ", results.Take(5))}");
        }
    }

    private static Dictionary<string, UserRow> ReadUserRows(SqliteConnection user, string sql, bool hasHistory, bool hasTags)
    {
        var rows = new Dictionary<string, UserRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in Query(user, sql, r => new UserRow(
                     Text(r, 0),
                     hasHistory ? Text(r, 1) : null,
                     hasTags ? Text(r, 2) : null,
                     Int(r, 3))))
        {
            rows[row.Id] = row;
        }

        return rows;
    }

    private static UserRow? Take(Dictionary<string, UserRow> rows, string id)
        => rows.Remove(id, out var row) ? row : null;

    private static CcViewedHistory ParseHistory(UserRow? row, List<string> issues)
    {
        if (row?.History is null)
        {
            return CcViewedHistory.Empty;
        }

        if (!CcViewedHistory.TryParse(row.History, out var history, out var error))
        {
            issues.Add($"VIEWED_HISTORY {row.History} does not parse ({error})");
        }

        return history;
    }

    private static CcScore ParseScore(UserRow? row, List<string> issues)
    {
        if (row?.Score is not { } stored)
        {
            return CcScore.NoRating;
        }

        var score = CcScore.FromStored(stored);
        if (score is null)
        {
            issues.Add($"SCORE {stored} is not a jClipCorn score");
        }

        return score ?? CcScore.NoRating;
    }

    private static CcTagList Tags(UserRow? row)
        => row?.Tags is not null && CcTagList.TryParse(row.Tags, out var tags) ? tags : CcTagList.Empty;

    private static CcOnlineRefs ParseRefs(string json, List<string> issues)
    {
        if (!CcOnlineRefs.TryParse(json, out var refs))
        {
            issues.Add($"ONLINEREF {json} does not parse");
        }

        return refs;
    }

    private static IReadOnlyList<string> ParseGroups(string json, List<string> issues)
    {
        if (!CcGroupList.TryParse(json, out var groups))
        {
            issues.Add($"GROUPS {json} does not parse");
        }

        return groups;
    }

    private static string? Join(List<string> issues) => issues.Count == 0 ? null : string.Join("; ", issues);

    private static List<T> Query<T>(SqliteConnection connection, string sql, Func<SqliteDataReader, T> map)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var result = new List<T>();
        while (reader.Read())
        {
            result.Add(map(reader));
        }

        return result;
    }

    private static string Text(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? string.Empty : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture) ?? string.Empty;

    // Migrated databases keep older declared types, so numbers may come back as text.
    private static int? Int(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        return reader.GetValue(ordinal) switch
        {
            long l => (int)l,
            double d => (int)d,
            string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) => i,
            _ => null,
        };
    }

    private static int? Year(SqliteDataReader reader, int ordinal)
        => Int(reader, ordinal) is { } year && year > 0 ? year : null;

    private sealed record UserRow(string Id, string? History, string? Tags, int? Score);
}
