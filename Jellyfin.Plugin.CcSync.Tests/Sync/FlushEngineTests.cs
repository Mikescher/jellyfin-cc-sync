using System.Globalization;
using Jellyfin.Plugin.CcSync.ClipCorn;
using Jellyfin.Plugin.CcSync.Configuration;
using Jellyfin.Plugin.CcSync.State;
using Jellyfin.Plugin.CcSync.Sync;
using Jellyfin.Plugin.CcSync.Tests.Support;
using Jellyfin.Plugin.CcSync.Watch;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.CcSync.Tests.Sync;

public sealed class FlushEngineTests : IDisposable
{
    private const string Super8 = "01a17a31-0270-7899-ba94-8e72f8bc14da";
    private const string SteinsGate = "01a17a31-0313-7518-af58-e4b38339ee79";
    private const string SteinsGateEp1 = "01a17a31-033a-72a6-81ea-c45f4585d62c";

    // 2026-10-26 20:15:00 UTC is 21:15:00 in Berlin (CET).
    private static readonly DateTime WatchedAt = new(2026, 10, 26, 20, 15, 0, DateTimeKind.Utc);

    private readonly FixtureDirectory _fixture = new();
    private readonly TempStateDb _state = new();
    private readonly SyncGate _gate = new();
    private readonly FlushEngine _engine;
    private readonly PluginConfiguration _config;

    public FlushEngineTests()
    {
        _engine = new FlushEngine(_state.Pending, _state.History, _state.Runs, _gate, new PluginPaths(Path.Combine(_fixture.SnapshotRoot, "data")), NullLogger<FlushEngine>.Instance);
        _config = new PluginConfiguration { ClipCornDirectory = _fixture.Root };
    }

    private string BackupDirectory => Path.Combine(_fixture.SnapshotRoot, "data", "backups");

    public void Dispose()
    {
        _engine.Dispose();
        _gate.Dispose();
        _state.Dispose();
        _fixture.Dispose();
    }

    private PendingUpdate AddMovie(string? ccId, string name = "Super 8", int? year = 2011, string? tmdb = null, DateTime? watchedAt = null)
    {
        var ids = new Dictionary<string, string>();
        if (ccId is not null)
        {
            ids["clipcorn"] = ccId;
        }

        if (tmdb is not null)
        {
            ids["Tmdb"] = tmdb;
        }

        return _state.Pending.Add(DateTime.UtcNow, PendingSource.Playback, Guid.NewGuid(), name, "Movie", ccId, new PendingMatchKeys(ids, name, year).ToJson(), watchedAt ?? WatchedAt);
    }

    private string? History(string id) => _fixture.Scalar(CcFiles.UserDbName, $"SELECT VIEWED_HISTORY FROM MOVIES WHERE ID = '{id}'") as string;

    private static string Heartbeat(DateTime utc) => "{\"pid\": 42, \"c\": \"2026-10-26T08:00:00Z\", \"u\": \"" + utc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) + "\", \"host\": \"desk\"}";

    [Fact]
    public async Task NothingPendingIsANoOp()
    {
        var result = await _engine.RunAsync(_config, CancellationToken.None);
        Assert.Equal(RunOutcomes.NothingToDo, result.Outcome);
        Assert.Empty(_state.Runs.Recent(null, 10));
        Assert.False(Directory.Exists(BackupDirectory));
    }

    [Fact]
    public async Task WritesAPendingMovieAndRecordsEverything()
    {
        var update = AddMovie(Super8);

        var result = await _engine.RunAsync(_config, CancellationToken.None);

        Assert.True(result.Outcome == RunOutcomes.Applied, result.Message);
        Assert.Equal(1, result.Written);
        Assert.Equal("[\"2023-11-05 11:00:00\",\"2026-10-26 21:15:00\"]", History(Super8));

        var done = _state.Pending.Get(update.Id)!;
        Assert.Equal(PendingStatus.Written, done.Status);
        Assert.Equal("2026-10-26 21:15:00", done.CcEntry);

        var run = _state.Runs.Last(RunKinds.Flush)!;
        Assert.Equal(RunOutcomes.Applied, run.Outcome);
        var entries = _state.History.Query(new HistoryQuery(RunId: run.Id)).Entries;
        Assert.Contains(entries, e => e.Kind == HistoryKinds.PendingWritten && e.NewValue == "2026-10-26 21:15:00" && e.CcId == Super8);
        Assert.Contains(entries, e => e.Kind == HistoryKinds.RunFlush && e.Success);

        var backup = Assert.Single(CcBackups.List(BackupDirectory));
        Assert.Contains("[\"2023-11-05 11:00:00\"]", Rows(backup), StringComparer.Ordinal);
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(_fixture.SnapshotRoot, "data", "snapshots")));
    }

    [Fact]
    public async Task WaitsWhileJClipCornHoldsTheLockAndRecordsTheHolderOnce()
    {
        File.WriteAllText(_fixture.Files.MainLock, Heartbeat(DateTime.UtcNow.AddMinutes(-5)));
        var update = AddMovie(Super8);

        Assert.Equal(RunOutcomes.Waiting, (await _engine.RunAsync(_config, CancellationToken.None)).Outcome);
        Assert.Equal(RunOutcomes.Waiting, (await _engine.RunAsync(_config, CancellationToken.None)).Outcome);

        Assert.Equal("[\"2023-11-05 11:00:00\"]", History(Super8));
        Assert.Equal(0, _state.Pending.Get(update.Id)!.Attempts);
        Assert.Empty(_state.Runs.Recent(null, 10));
        var locked = Assert.Single(_state.History.Query(new HistoryQuery(Kind: HistoryKinds.CcLocked)).Entries);
        Assert.Contains("desk", locked.Detail, StringComparison.Ordinal);
        Assert.False(Directory.Exists(BackupDirectory));
        Assert.Contains("desk", _state.Runs.GetJson<FlushWait>(FlushEngine.WaitKey)!.Reason, StringComparison.Ordinal);

        File.Delete(_fixture.Files.MainLock);
        Assert.Equal(RunOutcomes.Applied, (await _engine.RunAsync(_config, CancellationToken.None)).Outcome);
        Assert.Equal(PendingStatus.Written, _state.Pending.Get(update.Id)!.Status);
        Assert.Null(_state.Runs.GetValue(FlushEngine.WaitKey));
    }

    [Fact]
    public async Task AStaleLockDoesNotBlock()
    {
        File.WriteAllText(_fixture.Files.MainLock, Heartbeat(DateTime.UtcNow.AddHours(-4)));
        AddMovie(Super8);

        Assert.Equal(RunOutcomes.Applied, (await _engine.RunAsync(_config, CancellationToken.None)).Outcome);
        Assert.True(File.Exists(_fixture.Files.MainLock), "the plugin never removes lock files");
    }

    [Fact]
    public async Task WaitsWhileAJournalExists()
    {
        File.WriteAllText(_fixture.Files.UserDb + "-journal", string.Empty);
        AddMovie(Super8);

        var result = await _engine.RunAsync(_config, CancellationToken.None);

        Assert.Equal(RunOutcomes.Waiting, result.Outcome);
        Assert.Contains("ClipCornUserData.db-journal", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WritesAnEpisodeAndClearsWatchLaterOnItsSeries()
    {
        var update = _state.Pending.Add(DateTime.UtcNow, PendingSource.Playback, Guid.NewGuid(), "Steins;Gate S01E01", "Episode", SteinsGateEp1, "{}", WatchedAt);

        await _engine.RunAsync(_config, CancellationToken.None);

        Assert.Equal(PendingStatus.Written, _state.Pending.Get(update.Id)!.Status);
        Assert.Equal("[\"2026-10-26 21:15:00\"]", _fixture.Scalar(CcFiles.UserDbName, $"SELECT VIEWED_HISTORY FROM EPISODES WHERE ID = '{SteinsGateEp1}'"));
        Assert.Equal("[]", _fixture.Scalar(CcFiles.UserDbName, $"SELECT TAGS FROM SERIES WHERE ID = '{SteinsGate}'"));
        var written = Assert.Single(_state.History.Query(new HistoryQuery(Kind: HistoryKinds.PendingWritten)).Entries);
        Assert.Contains("removed from the series", written.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolvesAMovieWithoutClipCornIdByFallback()
    {
        var update = AddMovie(null, "Inglourious Basterds", 2009, tmdb: "16869");

        await _engine.RunAsync(_config, CancellationToken.None);

        var done = _state.Pending.Get(update.Id)!;
        Assert.Equal(PendingStatus.Written, done.Status);
        Assert.Equal("01a17a31-0212-78ef-b0e4-ea56e510a39c", done.CcId);
    }

    [Fact]
    public async Task PermanentProblemsFailTheirUpdateOnly()
    {
        var episodeWithoutId = _state.Pending.Add(DateTime.UtcNow, PendingSource.Playback, Guid.NewGuid(), "Some Show S01E01", "Episode", null, "{}", WatchedAt);
        var missing = AddMovie("01a17a31-ffff-7fff-bfff-ffffffffffff");
        var wrongKind = AddMovie(SteinsGate);
        var unknown = AddMovie(null, "Unknown Thing", 1999);
        var good = AddMovie(Super8);

        var result = await _engine.RunAsync(_config, CancellationToken.None);

        Assert.Equal(1, result.Written);
        Assert.Equal(4, result.Failed);
        Assert.Contains("episodes are matched by id only", _state.Pending.Get(episodeWithoutId.Id)!.LastError, StringComparison.Ordinal);
        Assert.Contains("has no element", _state.Pending.Get(missing.Id)!.LastError, StringComparison.Ordinal);
        Assert.Contains("is a Series in jClipCorn", _state.Pending.Get(wrongKind.Id)!.LastError, StringComparison.Ordinal);
        Assert.Contains("no unique fallback", _state.Pending.Get(unknown.Id)!.LastError, StringComparison.Ordinal);
        Assert.Equal(PendingStatus.Written, _state.Pending.Get(good.Id)!.Status);
        Assert.Equal(4, _state.History.Query(new HistoryQuery(Kind: HistoryKinds.PendingFailed)).Total);
    }

    [Fact]
    public async Task AForeignUserDatabaseKeepsEverythingPendingAndIsRecordedOnce()
    {
        _fixture.Execute(CcFiles.UserDbName, "UPDATE INFO SET IVALUE = 'other' WHERE IKEY = 'MAINDB_DUUID'");
        var update = AddMovie(Super8);

        Assert.Equal(RunOutcomes.Aborted, (await _engine.RunAsync(_config, CancellationToken.None)).Outcome);
        Assert.Equal(RunOutcomes.Aborted, (await _engine.RunAsync(_config, CancellationToken.None)).Outcome);

        var stillPending = _state.Pending.Get(update.Id)!;
        Assert.Equal(PendingStatus.Pending, stillPending.Status);
        Assert.Equal(2, stillPending.Attempts);
        Assert.Contains("belongs to database other", stillPending.LastError, StringComparison.Ordinal);
        Assert.Single(_state.History.Query(new HistoryQuery(Kind: HistoryKinds.PendingFailed)).Entries);
        Assert.Equal(2, _state.History.Query(new HistoryQuery(Kind: HistoryKinds.RunFlush)).Total);
    }

    [Fact]
    public async Task KeepsTheConfiguredNumberOfBackups()
    {
        _config.BackupCount = 2;
        for (var i = 0; i < 3; i++)
        {
            AddMovie(Super8, watchedAt: WatchedAt.AddDays(i));
            await _engine.RunAsync(_config, CancellationToken.None);
            await Task.Delay(1100, TestContext.Current.CancellationToken);
        }

        Assert.Equal(2, CcBackups.List(BackupDirectory).Count);
    }

    [Fact]
    public async Task AMissingDirectoryWaits()
    {
        _config.ClipCornDirectory = Path.Combine(_fixture.Root, "nope");
        AddMovie(Super8);

        var result = await _engine.RunAsync(_config, CancellationToken.None);

        Assert.Equal(RunOutcomes.Waiting, result.Outcome);
        Assert.Single(_state.History.Query(new HistoryQuery(Kind: HistoryKinds.RunFlush)).Entries);
    }

    private static IReadOnlyList<string> Rows(string db)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT VIEWED_HISTORY FROM MOVIES";
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }
}
