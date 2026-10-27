using Jellyfin.Plugin.CcSync.Jellyfin;
using Jellyfin.Plugin.CcSync.Watch;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;

namespace Jellyfin.Plugin.CcSync.Tests.Watch;

public class WatchTests
{
    private static readonly long Hour = TimeSpan.FromHours(1).Ticks;

    [Theory]
    [InlineData(true, 0L, 1L, true)]
    [InlineData(false, 90L, 100L, true)]
    [InlineData(false, 89L, 100L, false)]
    [InlineData(false, 100L, 0L, false)]
    [InlineData(false, null, 100L, false)]
    public void AppliesTheWatchedThreshold(bool completed, long? position, long? runtime, bool expected)
    {
        Assert.Equal(expected, WatchRules.IsWatched(completed, position, runtime, 0.90));
    }

    [Fact]
    public void EstimatesTheStartFromThePosition()
    {
        var end = new DateTime(2026, 10, 26, 20, 0, 0, DateTimeKind.Utc);
        Assert.Equal(end.AddHours(-1.5), WatchRules.EstimateStart(end, Hour * 3 / 2));
        Assert.Equal(end, WatchRules.EstimateStart(end, null));
        Assert.Equal(new DateTime(2026, 10, 26, 20, 0, 1, DateTimeKind.Utc), WatchRules.TruncateToSeconds(end.AddMilliseconds(1999)));
    }

    [Fact]
    public void TrackerKeepsTheEarliestStartWithinTheCoalesceWindow()
    {
        var tracker = new SessionTracker();
        var user = Guid.NewGuid();
        var item = Guid.NewGuid();
        var start = new DateTime(2026, 10, 26, 18, 0, 0, DateTimeKind.Utc);

        tracker.Record("s|i", user, item, start, 0, TimeSpan.FromMinutes(10));
        tracker.Record("s|i", user, item, start.AddMinutes(30), Hour, TimeSpan.FromMinutes(10));

        Assert.Equal(start, tracker.EndWatch(user, item));
        Assert.Null(tracker.EndWatch(user, item, "s|i"));
    }

    [Fact]
    public void TrackerReportsTheFurthestPositionOfTheLatestSession()
    {
        var tracker = new SessionTracker();
        var user = Guid.NewGuid();
        var item = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var window = TimeSpan.FromMinutes(10);

        Assert.Null(tracker.FurthestPosition(user, item));

        tracker.Record("old|i", user, item, now, Hour * 2, window);
        Thread.Sleep(10);
        tracker.Record("new|i", user, item, now, 0, window);
        tracker.Record("new|i", user, item, now, Hour, window);
        tracker.Record("new|i", user, item, now, Hour / 2, window);
        tracker.Record("new|i", user, item, now, null, window);

        Assert.Equal(Hour, tracker.FurthestPosition(user, item));
        Assert.Null(tracker.FurthestPosition(Guid.NewGuid(), item));
    }

    [Fact]
    public void ABriefReplayOfAWatchedItemDoesNotReachTheThreshold()
    {
        var tracker = new SessionTracker();
        var user = Guid.NewGuid();
        var item = Guid.NewGuid();
        var runtime = Hour;
        var window = TimeSpan.FromMinutes(10);

        tracker.Record("a|i", user, item, DateTime.UtcNow, 0, window);
        tracker.Record("a|i", user, item, DateTime.UtcNow, TimeSpan.FromSeconds(30).Ticks, window);

        Assert.False(WatchRules.IsWatched(false, tracker.FurthestPosition(user, item), runtime, 0.90));
    }

    [Fact]
    public void AWatchResumedInANewSessionReachesTheThreshold()
    {
        var tracker = new SessionTracker();
        var user = Guid.NewGuid();
        var item = Guid.NewGuid();
        var runtime = Hour;
        var window = TimeSpan.FromMinutes(10);
        var firstStart = new DateTime(2026, 10, 26, 18, 0, 0, DateTimeKind.Utc);

        tracker.Record("before-restart|i", user, item, firstStart, 0, window);
        tracker.Record("before-restart|i", user, item, firstStart, runtime / 2, window);
        Assert.False(WatchRules.IsWatched(false, tracker.FurthestPosition(user, item), runtime, 0.90));

        Thread.Sleep(10);
        tracker.Record("after-restart|i", user, item, firstStart.AddHours(2), runtime / 2, window);
        tracker.Record("after-restart|i", user, item, firstStart.AddHours(2), runtime - TimeSpan.FromSeconds(5).Ticks, window);

        Assert.True(WatchRules.IsWatched(false, tracker.FurthestPosition(user, item), runtime, 0.90));
        Assert.Equal(firstStart, tracker.EndWatch(user, item));
    }

    [Fact]
    public void EndingAWatchDropsEverySessionOfTheItem()
    {
        var tracker = new SessionTracker();
        var user = Guid.NewGuid();
        var item = Guid.NewGuid();
        var other = Guid.NewGuid();
        var start = new DateTime(2026, 10, 26, 18, 0, 0, DateTimeKind.Utc);
        var window = TimeSpan.FromMinutes(10);

        tracker.Record("a|i", user, item, start, Hour, window);
        tracker.Record("b|i", user, item, start.AddHours(1), 0, window);
        tracker.Record("a|o", user, other, start, 0, window);

        Assert.Equal(start.AddHours(1), tracker.EndWatch(user, item, "b|i"));
        Assert.Null(tracker.FurthestPosition(user, item));
        Assert.Equal(0, tracker.FurthestPosition(user, other));
    }

    [Fact]
    public void TrackerClaimsOncePerWindow()
    {
        var tracker = new SessionTracker();
        var key = SessionTracker.DedupKey(Guid.NewGuid(), Guid.NewGuid());

        Assert.False(tracker.IsClaimed(key, TimeSpan.FromMinutes(60)));
        Assert.True(tracker.TryClaim(key, TimeSpan.FromMinutes(60)));
        Assert.True(tracker.IsClaimed(key, TimeSpan.FromMinutes(60)));
        Assert.False(tracker.TryClaim(key, TimeSpan.FromMinutes(60)));
        Assert.True(tracker.TryClaim(key, TimeSpan.Zero));
    }

    [Fact]
    public void CapturesMovieMatchKeys()
    {
        var movie = new Movie
        {
            Name = "Super 8",
            ProductionYear = 2011,
            ProviderIds = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["clipcorn"] = "01a17a31-0270-7899-ba94-8e72f8bc14da",
                ["Tmdb"] = "37686",
                ["Imdb"] = "tt1650062",
                ["Tvdb"] = "ignored",
            },
        };

        var keys = PendingMatchKeys.Parse(PendingMatchKeys.From(movie).ToJson())!;
        Assert.Equal("Super 8", keys.Name);
        Assert.Equal(2011, keys.Year);
        Assert.Equal(3, keys.ProviderIds.Count);
        Assert.Equal("37686", keys.ProviderIds["Tmdb"]);
        Assert.Null(keys.SeriesName);
        Assert.Equal("01a17a31-0270-7899-ba94-8e72f8bc14da", JfItems.ProviderId(movie, JfItems.ClipCornProvider));
        Assert.Equal("Super 8 (2011)", JfItems.DisplayName(movie));
    }

    [Fact]
    public void CapturesEpisodeMatchKeys()
    {
        var episode = new Episode
        {
            Name = "Prologue",
            SeriesName = "Steins;Gate",
            ParentIndexNumber = 1,
            IndexNumber = 2,
            ProviderIds = new Dictionary<string, string>(StringComparer.Ordinal) { ["clipcorn"] = "ep-1" },
        };

        var keys = PendingMatchKeys.From(episode);
        Assert.Equal("Steins;Gate", keys.SeriesName);
        Assert.Equal(1, keys.Season);
        Assert.Equal(2, keys.Episode);
        Assert.Equal("Steins;Gate S01E02 Prologue", JfItems.DisplayName(episode));
        Assert.Equal("Episode", JfItems.Kind(episode));
    }

    [Fact]
    public void ProviderIdsFallBackToACaseInsensitiveLookup()
    {
        var movie = new Movie { ProviderIds = new Dictionary<string, string>(StringComparer.Ordinal) { ["ClipCorn"] = " x " } };
        Assert.Equal("x", JfItems.ProviderId(movie, JfItems.ClipCornProvider));
        Assert.Null(JfItems.ProviderId(movie, JfItems.TmdbProvider));
    }
}
