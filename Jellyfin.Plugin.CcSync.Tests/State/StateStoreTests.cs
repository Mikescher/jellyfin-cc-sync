using Jellyfin.Plugin.CcSync.State;
using Jellyfin.Plugin.CcSync.Tests.Support;

namespace Jellyfin.Plugin.CcSync.Tests.State;

public class StateStoreTests
{
    private static readonly DateTime T0 = new(2026, 10, 26, 18, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void MigratesToTheCurrentSchemaInWalMode()
    {
        using var state = new TempStateDb();
        using var connection = state.Db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        Assert.Equal((long)StateDb.SchemaVersion, command.ExecuteScalar());
        command.CommandText = "PRAGMA journal_mode";
        Assert.Equal("wal", command.ExecuteScalar());

        // A second instance on the same file finds it migrated.
        using var again = new StateDb(state.Path).Open();
    }

    [Fact]
    public void PendingLifecycle()
    {
        using var state = new TempStateDb();
        var item = Guid.NewGuid();

        var added = state.Pending.Add(T0, PendingSource.Playback, item, "Super 8 (2011)", "Movie", "cc-1", "{}", T0.AddMinutes(-110));
        Assert.Equal(PendingStatus.Pending, added.Status);
        Assert.Equal(item, added.JfItemId);
        Assert.Equal(T0.AddMinutes(-110), added.WatchedAtUtc);
        Assert.Single(state.Pending.GetPending());

        Assert.Null(state.Pending.RecordAttempt(added.Id, T0.AddMinutes(1), "jClipCorn is running"));
        Assert.Equal("jClipCorn is running", state.Pending.RecordAttempt(added.Id, T0.AddMinutes(2), "jClipCorn is running"));
        var waiting = state.Pending.Get(added.Id)!;
        Assert.Equal(2, waiting.Attempts);
        Assert.Equal(PendingStatus.Pending, waiting.Status);

        state.Pending.Finish(added.Id, PendingStatus.Written, T0.AddMinutes(3), "2026-10-26 18:10:00", null);
        var written = state.Pending.Get(added.Id)!;
        Assert.Equal(PendingStatus.Written, written.Status);
        Assert.Equal(3, written.Attempts);
        Assert.Null(written.LastError);
        Assert.Equal("2026-10-26 18:10:00", written.CcEntry);
        Assert.Empty(state.Pending.GetPending());

        Assert.False(state.Pending.Retry(added.Id));
        Assert.False(state.Pending.Discard(added.Id, T0));
    }

    [Fact]
    public void FailedUpdatesCanBeRetriedOrDiscarded()
    {
        using var state = new TempStateDb();
        var a = state.Pending.Add(T0, PendingSource.Manual, Guid.NewGuid(), "A", "Episode", null, "{}", T0);
        var b = state.Pending.Add(T0, PendingSource.Manual, Guid.NewGuid(), "B", "Episode", null, "{}", T0);
        state.Pending.Finish(a.Id, PendingStatus.Failed, T0, null, "unmatched episode");

        Assert.True(state.Pending.Retry(a.Id));
        Assert.Equal(PendingStatus.Pending, state.Pending.Get(a.Id)!.Status);
        Assert.Equal("unmatched episode", state.Pending.Get(a.Id)!.LastError);

        Assert.True(state.Pending.Discard(b.Id, T0));
        Assert.Equal(PendingStatus.Discarded, state.Pending.Get(b.Id)!.Status);

        var counts = state.Pending.CountsByStatus();
        Assert.Equal(1, counts[PendingStatus.Pending]);
        Assert.Equal(1, counts[PendingStatus.Discarded]);
        Assert.Equal(2, state.Pending.Count(null));
        Assert.Single(state.Pending.List(PendingStatus.Discarded, 10, 0));
        Assert.Equal(b.Id, state.Pending.List(null, 1, 0).Single().Id);
    }

    [Fact]
    public void FindsPendingUpdatesCreatedAfterASnapshot()
    {
        using var state = new TempStateDb();
        var item = Guid.NewGuid();
        state.Pending.Add(T0, PendingSource.Playback, item, "A", "Movie", null, "{}", T0);

        Assert.True(state.Pending.HasPendingCreatedAfter(item, T0.AddSeconds(-1)));
        Assert.False(state.Pending.HasPendingCreatedAfter(item, T0));
        Assert.False(state.Pending.HasPendingCreatedAfter(Guid.NewGuid(), T0.AddDays(-1)));
    }

    [Fact]
    public void HistoryFiltersAndPages()
    {
        using var state = new TempStateDb();
        var item = Guid.NewGuid().ToString("N");
        state.History.AddRange(Enumerable.Range(0, 250).Select(i => new HistoryEntry
        {
            TsUtc = T0.AddMinutes(i),
            Kind = i % 2 == 0 ? HistoryKinds.JfPlayed : HistoryKinds.PlannedPrefix + HistoryKinds.JfTagAdded,
            RunId = i < 10 ? "run-a" : "run-b",
            ItemName = i == 7 ? "Forrest Gump" : "Item " + i,
            JfItemId = i == 8 ? item : null,
            CcId = i == 9 ? "01a17a31-06c5-78f6-b841-09a525e56001" : null,
            Success = i != 3,
        }));

        var first = state.History.Query(new HistoryQuery(Limit: 100));
        Assert.Equal(250, first.Total);
        Assert.Equal(100, first.Entries.Count);
        Assert.Equal(T0.AddMinutes(249), first.Entries[0].TsUtc);

        var last = state.History.Query(new HistoryQuery(Limit: 100, Offset: 200));
        Assert.Equal(50, last.Entries.Count);
        Assert.Equal(T0, last.Entries[^1].TsUtc);

        Assert.Equal(125, state.History.Query(new HistoryQuery(Kind: HistoryKinds.JfPlayed)).Total);
        Assert.Equal(125, state.History.Query(new HistoryQuery(Kind: "planned-*")).Total);
        Assert.Equal(10, state.History.Query(new HistoryQuery(RunId: "run-a")).Total);
        Assert.Equal("Forrest Gump", state.History.Query(new HistoryQuery(Search: "gump")).Entries.Single().ItemName);
        Assert.Equal(8, state.History.Query(new HistoryQuery(Search: Guid.Parse(item).ToString())).Entries.Single().TsUtc.Minute);
        Assert.Single(state.History.Query(new HistoryQuery(Search: "01A17A31-06C5-78F6-B841-09A525E56001")).Entries);
        Assert.Empty(state.History.Query(new HistoryQuery(Search: "%")).Entries);
        Assert.False(state.History.Query(new HistoryQuery(RunId: "run-a")).Entries.Single(e => e.TsUtc == T0.AddMinutes(3)).Success);

        var window = state.History.Query(new HistoryQuery(FromUtc: T0.AddMinutes(10), ToUtc: T0.AddMinutes(20)));
        Assert.Equal(10, window.Total);

        Assert.Equal(100, state.History.DeleteOlderThan(T0.AddMinutes(100)));
        Assert.Equal(150, state.History.Query(new HistoryQuery()).Total);
    }

    [Fact]
    public void RunsAndValues()
    {
        using var state = new TempStateDb();
        var run = state.Runs.Start(RunKinds.Sync, T0);
        Assert.Equal(RunOutcomes.Running, state.Runs.Last(RunKinds.Sync)!.Outcome);

        state.Runs.Finish(run.Id, RunOutcomes.Applied, new { Changed = 3 }, T0.AddSeconds(5));
        var finished = state.Runs.Last(RunKinds.Sync)!;
        Assert.Equal(RunOutcomes.Applied, finished.Outcome);
        Assert.Equal("{\"Changed\":3}", finished.Summary);
        Assert.Equal(T0.AddSeconds(5), finished.FinishedUtc);
        Assert.Null(state.Runs.Last(RunKinds.Flush));

        var dangling = state.Runs.Start(RunKinds.Flush, T0.AddMinutes(1));
        Assert.Equal(1, state.Runs.AbortDangling(T0.AddMinutes(2)));
        Assert.Equal(RunOutcomes.Aborted, state.Runs.Last(RunKinds.Flush)!.Outcome);
        Assert.Equal(dangling.Id, state.Runs.Recent(null, 1).Single().Id);

        state.Runs.SetJson(RunStore.MatchReportKey, new[] { "a", "b" });
        Assert.Equal(["a", "b"], state.Runs.GetJson<string[]>(RunStore.MatchReportKey)!);
        state.Runs.SetValue(RunStore.MatchReportKey, "[]");
        Assert.Equal("[]", state.Runs.GetValue(RunStore.MatchReportKey));
        state.Runs.RemoveValue(RunStore.MatchReportKey);
        Assert.Null(state.Runs.GetValue(RunStore.MatchReportKey));
    }
}
