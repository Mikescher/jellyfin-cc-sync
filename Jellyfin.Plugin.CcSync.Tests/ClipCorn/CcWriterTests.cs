using Jellyfin.Plugin.CcSync.ClipCorn;
using Jellyfin.Plugin.CcSync.ClipCorn.Model;
using Jellyfin.Plugin.CcSync.Tests.Support;

namespace Jellyfin.Plugin.CcSync.Tests.ClipCorn;

public class CcWriterTests
{
    private const string Super8 = "01a17a31-0270-7899-ba94-8e72f8bc14da";
    private const string Gefaehrten = "01a17a31-01a6-77b5-b4f4-f3d22e44aab7";
    private const string Basterds = "01a17a31-0212-78ef-b0e4-ea56e510a39c";
    private const string Bomber = "01a17a31-0692-792d-8e0d-46f8eca06166";
    private const string SteinsGate = "01a17a31-0313-7518-af58-e4b38339ee79";
    private const string SteinsGateEp1 = "01a17a31-033a-72a6-81ea-c45f4585d62c";

    private static CcDateTime E(string raw)
    {
        Assert.True(CcDateTime.TryParse(raw, out var value));
        return value;
    }

    private static string Duuid(FixtureDirectory f) => (string)f.Scalar(CcFiles.MainDbName, "SELECT IVALUE FROM INFO WHERE IKEY = 'DATABASE_UNIVERSALLY_UNIQUE_IDENTIFIER'")!;

    private static IReadOnlyList<CcWriteOutcome> Apply(FixtureDirectory f, bool resetWatchLater, params CcWriteRequest[] requests)
        => CcWriter.Apply(f.Files.UserDb, Duuid(f), requests, resetWatchLater, () => null);

    private static string? Col(FixtureDirectory f, string table, string column, string id)
        => f.Scalar(CcFiles.UserDbName, $"SELECT {column} FROM {table} WHERE ID = '{id}'") as string;

    private static long Count(FixtureDirectory f, string sql) => (long)f.Scalar(CcFiles.UserDbName, sql)!;

    private static string Dump(FixtureDirectory f, string exceptId)
    {
        var parts = new List<string>();
        foreach (var table in new[] { "MOVIES", "SERIES", "SEASONS", "EPISODES", "INFO" })
        {
            var key = table == "INFO" ? "IKEY" : "ID";
            parts.AddRange(f.Rows(CcFiles.UserDbName, $"SELECT * FROM {table} WHERE {key} <> '{exceptId}' ORDER BY {key}"));
            parts.Add("--");
        }

        return string.Join("\n", parts);
    }

    [Fact]
    public void AddsAnEntryToAnExistingRow()
    {
        using var f = new FixtureDirectory();
        var others = Dump(f, Super8);

        var outcome = Apply(f, true, new CcWriteRequest(1, Super8, CcKind.Movie, E("2026-10-26 21:15:00"), null)).Single();

        Assert.Equal(CcWriteResult.Written, outcome.Result);
        Assert.False(outcome.WatchLaterRemoved);
        Assert.Equal("[\"2023-11-05 11:00:00\",\"2026-10-26 21:15:00\"]", Col(f, "MOVIES", "VIEWED_HISTORY", Super8));
        Assert.Equal("[]", Col(f, "MOVIES", "TAGS", Super8));
        Assert.Equal(others, Dump(f, Super8));

        // The history trigger logged exactly the one changed column.
        Assert.Equal(1, Count(f, $"SELECT COUNT(*) FROM HISTORY WHERE ID = '{Super8}'"));
        Assert.Equal(
            "MOVIES|UPDATE|VIEWED_HISTORY|[\"2023-11-05 11:00:00\"]|[\"2023-11-05 11:00:00\",\"2026-10-26 21:15:00\"]",
            f.Scalar(CcFiles.UserDbName, $"SELECT [TABLE] || '|' || [ACTION] || '|' || FIELD || '|' || OLD || '|' || [NEW] FROM HISTORY WHERE ID = '{Super8}'"));

        Assert.Equal("delete", f.Scalar(CcFiles.UserDbName, "PRAGMA journal_mode"));
        Assert.Empty(f.Files.ExistingJournals());
    }

    [Fact]
    public void CreatesASparseRowWithDefaults()
    {
        using var f = new FixtureDirectory();
        Assert.Equal(0, Count(f, $"SELECT COUNT(*) FROM MOVIES WHERE ID = '{Bomber}'"));

        Apply(f, true, new CcWriteRequest(1, Bomber, CcKind.Movie, E("2026-10-26 21:15:00"), null));

        Assert.Equal(
            $"{Bomber}|[\"2026-10-26 21:15:00\"]|[]|6|",
            f.Scalar(CcFiles.UserDbName, $"SELECT ID || '|' || VIEWED_HISTORY || '|' || TAGS || '|' || SCORE || '|' || SCORECOMMENT FROM MOVIES WHERE ID = '{Bomber}'"));

        // A new user row is logged as four UPDATEs from the defaults, never as an INSERT.
        Assert.Equal(4, Count(f, $"SELECT COUNT(*) FROM HISTORY WHERE ID = '{Bomber}' AND [ACTION] = 'UPDATE'"));
        Assert.Equal(0, Count(f, $"SELECT COUNT(*) FROM HISTORY WHERE ID = '{Bomber}' AND [ACTION] <> 'UPDATE'"));
    }

    [Fact]
    public void RemovesWatchLaterFromAMovie()
    {
        using var f = new FixtureDirectory();

        var outcome = Apply(f, true, new CcWriteRequest(1, Gefaehrten, CcKind.Movie, E("2026-10-26 21:15:00"), null)).Single();

        Assert.True(outcome.WatchLaterRemoved);
        Assert.Equal("[\"UNSPECIFIED\",\"2026-10-26 21:15:00\"]", Col(f, "MOVIES", "VIEWED_HISTORY", Gefaehrten));
        Assert.Equal("[]", Col(f, "MOVIES", "TAGS", Gefaehrten));
        Assert.Equal(2, Count(f, $"SELECT COUNT(*) FROM HISTORY WHERE ID = '{Gefaehrten}'"));
    }

    [Fact]
    public void KeepsWatchLaterWhenTheSettingIsOff()
    {
        using var f = new FixtureDirectory();

        var outcome = Apply(f, false, new CcWriteRequest(1, Gefaehrten, CcKind.Movie, E("2026-10-26 21:15:00"), null)).Single();

        Assert.False(outcome.WatchLaterRemoved);
        Assert.Equal("[3]", Col(f, "MOVIES", "TAGS", Gefaehrten));
    }

    [Theory]
    [InlineData(Super8, "2023-11-05 11:00:00")]
    [InlineData(Basterds, "2016-11-01 20:00:00")]
    public void AnEqualEntryIsADuplicateAndChangesNothing(string id, string entry)
    {
        using var f = new FixtureDirectory();
        var before = Dump(f, string.Empty);

        var outcome = Apply(f, true, new CcWriteRequest(1, id, CcKind.Movie, E(entry), null)).Single();

        Assert.Equal(CcWriteResult.Duplicate, outcome.Result);
        Assert.Equal(before, Dump(f, string.Empty));
        Assert.Equal(0, Count(f, "SELECT COUNT(*) FROM HISTORY"));
    }

    [Fact]
    public void ADuplicateStillRemovesWatchLaterLikeJClipCorn()
    {
        using var f = new FixtureDirectory();
        f.Execute(CcFiles.UserDbName, $"UPDATE MOVIES SET TAGS = '[3]' WHERE ID = '{Super8}'");
        f.Execute(CcFiles.UserDbName, "DELETE FROM HISTORY");

        var outcome = Apply(f, true, new CcWriteRequest(1, Super8, CcKind.Movie, E("2023-11-05 11:00:00"), null)).Single();

        Assert.Equal(CcWriteResult.Duplicate, outcome.Result);
        Assert.True(outcome.WatchLaterRemoved);
        Assert.Equal("[\"2023-11-05 11:00:00\"]", Col(f, "MOVIES", "VIEWED_HISTORY", Super8));
        Assert.Equal("[]", Col(f, "MOVIES", "TAGS", Super8));
        Assert.Equal("TAGS", f.Scalar(CcFiles.UserDbName, $"SELECT FIELD FROM HISTORY WHERE ID = '{Super8}'"));
    }

    [Fact]
    public void AnEpisodeWatchClearsWatchLaterOnTheEpisodeAndItsSeries()
    {
        using var f = new FixtureDirectory();

        var outcome = Apply(f, true, new CcWriteRequest(1, SteinsGateEp1, CcKind.Episode, E("2026-10-26 21:15:00"), SteinsGate)).Single();

        Assert.Equal(CcWriteResult.Written, outcome.Result);
        Assert.True(outcome.WatchLaterRemoved);
        Assert.True(outcome.SeriesWatchLaterRemoved);
        Assert.Equal("[\"2026-10-26 21:15:00\"]", Col(f, "EPISODES", "VIEWED_HISTORY", SteinsGateEp1));
        Assert.Equal("[]", Col(f, "EPISODES", "TAGS", SteinsGateEp1));
        Assert.Equal("[]", Col(f, "SERIES", "TAGS", SteinsGate));
        Assert.Equal(4L, f.Scalar(CcFiles.UserDbName, $"SELECT SCORE FROM SERIES WHERE ID = '{SteinsGate}'"));
        Assert.Equal(1, Count(f, $"SELECT COUNT(*) FROM HISTORY WHERE [TABLE] = 'SERIES' AND FIELD = 'TAGS'"));
    }

    [Fact]
    public void ASeriesRowBackToDefaultsIsDeleted()
    {
        using var f = new FixtureDirectory();
        var konoSuba = (string)f.Scalar(CcFiles.MainDbName, "SELECT ID FROM SERIES WHERE NAME = 'KonoSuba'")!;
        var episode = (string)f.Scalar(CcFiles.MainDbName, $"SELECT e.ID FROM EPISODES e JOIN SEASONS s ON s.ID = e.SEASONID WHERE s.SERIESID = '{konoSuba}' ORDER BY e.EPISODE LIMIT 1")!;
        f.Execute(CcFiles.UserDbName, $"UPDATE SERIES SET TAGS = '[3]', SCORE = 6, SCORECOMMENT = '' WHERE ID = '{konoSuba}'; DELETE FROM HISTORY");

        var outcome = Apply(f, true, new CcWriteRequest(1, episode, CcKind.Episode, E("2026-10-26 21:15:00"), konoSuba)).Single();

        Assert.True(outcome.SeriesWatchLaterRemoved);
        Assert.Equal(0, Count(f, $"SELECT COUNT(*) FROM SERIES WHERE ID = '{konoSuba}'"));
        Assert.Equal(0, Count(f, $"SELECT COUNT(*) FROM HISTORY WHERE ID = '{konoSuba}' AND [ACTION] <> 'UPDATE'"));
        Assert.Equal("[]", f.Scalar(CcFiles.UserDbName, $"SELECT [NEW] FROM HISTORY WHERE ID = '{konoSuba}' AND FIELD = 'TAGS'"));
    }

    [Fact]
    public void AnUnparseableHistoryFailsAndStaysByteIdentical()
    {
        using var f = new FixtureDirectory();
        const string broken = "[\"2023-11-05 11:00:00\",\"bad\"]";
        f.Execute(CcFiles.UserDbName, $"UPDATE MOVIES SET VIEWED_HISTORY = '{broken}', TAGS = '[3]' WHERE ID = '{Super8}'; DELETE FROM HISTORY");

        var outcome = Apply(f, true, new CcWriteRequest(1, Super8, CcKind.Movie, E("2026-10-26 21:15:00"), null)).Single();

        Assert.Equal(CcWriteResult.Failed, outcome.Result);
        Assert.Contains("does not parse", outcome.Note, StringComparison.Ordinal);
        Assert.Equal(broken, Col(f, "MOVIES", "VIEWED_HISTORY", Super8));
        Assert.Equal("[3]", Col(f, "MOVIES", "TAGS", Super8));
        Assert.Equal(0, Count(f, "SELECT COUNT(*) FROM HISTORY"));
    }

    [Fact]
    public void KeepsMillisecondEntriesVerbatim()
    {
        using var f = new FixtureDirectory();
        f.Execute(CcFiles.UserDbName, $"UPDATE MOVIES SET VIEWED_HISTORY = '[\"2023-11-05 11:00:00.500\"]' WHERE ID = '{Super8}'");

        Apply(f, true, new CcWriteRequest(1, Super8, CcKind.Movie, E("2020-01-01 10:00:00"), null));

        Assert.Equal("[\"2020-01-01 10:00:00\",\"2023-11-05 11:00:00.500\"]", Col(f, "MOVIES", "VIEWED_HISTORY", Super8));
    }

    [Fact]
    public void AppliesSeveralViewingsOfOneElementInOrder()
    {
        using var f = new FixtureDirectory();

        var outcomes = Apply(
            f,
            true,
            new CcWriteRequest(1, Bomber, CcKind.Movie, E("2026-10-26 21:15:00"), null),
            new CcWriteRequest(2, Bomber, CcKind.Movie, E("2026-10-20 10:00:00"), null),
            new CcWriteRequest(3, Bomber, CcKind.Movie, E("2026-10-26 21:15:00"), null));

        Assert.Equal([CcWriteResult.Written, CcWriteResult.Written, CcWriteResult.Duplicate], outcomes.Select(o => o.Result));
        Assert.Equal("[\"2026-10-20 10:00:00\",\"2026-10-26 21:15:00\"]", Col(f, "MOVIES", "VIEWED_HISTORY", Bomber));
    }

    [Fact]
    public void RefusesAUserDatabaseOfAnotherMainDatabase()
    {
        using var f = new FixtureDirectory();
        var before = Dump(f, string.Empty);

        var ex = Assert.Throws<CcWriteAbortedException>(() => CcWriter.Apply(
            f.Files.UserDb, "someone-else", [new CcWriteRequest(1, Super8, CcKind.Movie, E("2026-10-26 21:15:00"), null)], true, () => null));

        Assert.Contains("belongs to database", ex.Message, StringComparison.Ordinal);
        Assert.Equal(before, Dump(f, string.Empty));
        Assert.Empty(f.Files.ExistingJournals());
    }

    [Fact]
    public void RollsBackWhenALockAppearsBeforeTheCommit()
    {
        using var f = new FixtureDirectory();
        var before = Dump(f, string.Empty);

        var ex = Assert.Throws<CcWriteAbortedException>(() => CcWriter.Apply(
            f.Files.UserDb,
            Duuid(f),
            [new CcWriteRequest(1, Bomber, CcKind.Movie, E("2026-10-26 21:15:00"), null), new CcWriteRequest(2, Gefaehrten, CcKind.Movie, E("2026-10-26 21:15:00"), null)],
            true,
            () => "ClipCornDB.db.~lock appeared"));

        Assert.Equal("ClipCornDB.db.~lock appeared", ex.Message);
        Assert.Equal(before, Dump(f, string.Empty));
        Assert.Equal(0, Count(f, "SELECT COUNT(*) FROM HISTORY"));
        Assert.Empty(f.Files.ExistingJournals());
    }

    [Fact]
    public void ReportsABusyDatabaseAsAnAbortedSession()
    {
        using var f = new FixtureDirectory();
        using var blocker = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={f.Files.UserDb};Pooling=False");
        blocker.Open();
        using (var begin = blocker.CreateCommand())
        {
            begin.CommandText = "BEGIN IMMEDIATE";
            begin.ExecuteNonQuery();
        }

        var start = DateTime.UtcNow;
        var ex = Assert.Throws<CcWriteAbortedException>(() => Apply(f, true, new CcWriteRequest(1, Bomber, CcKind.Movie, E("2026-10-26 21:15:00"), null)));
        Assert.Contains("locked", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(DateTime.UtcNow - start < TimeSpan.FromSeconds(30));
    }
}
