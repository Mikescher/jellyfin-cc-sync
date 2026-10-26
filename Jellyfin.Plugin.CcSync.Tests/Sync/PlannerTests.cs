using Jellyfin.Plugin.CcSync.ClipCorn.Model;
using Jellyfin.Plugin.CcSync.Configuration;
using Jellyfin.Plugin.CcSync.Sync;
using Jellyfin.Plugin.CcSync.Tests.Support;

namespace Jellyfin.Plugin.CcSync.Tests.Sync;

public class PlannerTests
{
    private static readonly CcLibrary Library = FixtureLibrary.Load();
    private static readonly TimeZoneInfo Berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");

    private static string Id(string name) => Library.Elements.Single(e => e.Name == name).Id;

    private static JfItemInfo Item(CcKind kind, string name, string? clipcorn, params string[] tags)
        => new(Guid.NewGuid(), kind, name, null, clipcorn, null, null, tags);

    private static SyncOptions Options(Action<PluginConfiguration>? change = null)
    {
        var config = new PluginConfiguration();
        change?.Invoke(config);
        return SyncOptions.From(config, Berlin, new TimeOnly(12, 0));
    }

    private static SyncPlan Plan(IReadOnlyList<JfItemInfo> items, Dictionary<Guid, JfUserData>? actual = null, IReadOnlyList<PendingOverlay>? pending = null, SyncOptions? options = null, CcLibrary? library = null)
    {
        library ??= Library;
        var match = Matcher.Match(library, items);
        var desired = DesiredStateBuilder.Build(library, match, items, pending ?? [], options ?? Options());
        return SyncPlanner.Plan(desired, actual ?? []);
    }

    [Fact]
    public void ViewedHistoryBecomesPlayedCountAndLastPlayed()
    {
        // ["UNSPECIFIED","2016-11-01","2016-11-18 22:50:38"], score 5 (Masterpiece), group Tarantino.
        var item = Item(CcKind.Movie, "Inglourious Basterds", Id("Inglourious Basterds"));

        var change = Assert.Single(Plan([item]).Changes);

        Assert.Equal(new FieldChange<bool>(false, true), change.Played);
        Assert.Equal(new FieldChange<int>(0, 3), change.PlayCount);
        Assert.Equal(new DateTime(2016, 11, 18, 21, 50, 38, DateTimeKind.Utc), change.LastPlayed!.New);
        Assert.Equal(new FieldChange<bool>(false, true), change.Favorite);
        Assert.Equal(["jcc-rating-masterpiece", "jcc-tarantino"], change.AddTags);
        Assert.Empty(change.RemoveTags);
    }

    [Fact]
    public void UnspecifiedOnlyHistoryHasNoLastPlayed()
    {
        var item = Item(CcKind.Movie, "Die Gefährten", Id("Die Gefährten"));
        var actual = new Dictionary<Guid, JfUserData> { [item.Id] = new(true, 1, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), false) };

        var change = Assert.Single(Plan([item], actual).Changes);

        Assert.Null(change.Played);
        Assert.Null(change.PlayCount);
        Assert.Equal(new FieldChange<DateTime?>(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), null), change.LastPlayed);
    }

    [Fact]
    public void EqualStateProducesNoChangeAndComparesDatesToTheSecond()
    {
        var item = Item(CcKind.Movie, "Super 8", Id("Super 8"), "jcc-rating-recommended", "other");
        var actual = new Dictionary<Guid, JfUserData>
        {
            [item.Id] = new(true, 1, new DateTime(2023, 11, 5, 10, 0, 0, 999, DateTimeKind.Unspecified), false),
        };

        Assert.Empty(Plan([item], actual).Changes);
    }

    [Fact]
    public void AnEmptyHistoryUnwatches()
    {
        var item = Item(CcKind.Movie, "Der Bomber", Id("Der Bomber"), "jcc-spencerhill");
        var actual = new Dictionary<Guid, JfUserData> { [item.Id] = new(true, 4, DateTime.UtcNow, true) };

        var plan = Plan([item], actual);

        var change = Assert.Single(plan.Changes);
        Assert.True(change.Unwatches);
        Assert.Equal(new FieldChange<int>(4, 0), change.PlayCount);
        Assert.Equal(new FieldChange<bool>(true, false), change.Favorite);
        Assert.Equal(1, plan.Unwatches);
    }

    [Fact]
    public void PendingUpdatesAreOverlaid()
    {
        var item = Item(CcKind.Movie, "Der Bomber", Id("Der Bomber"));
        var watched = new DateTime(2026, 10, 26, 20, 15, 0, DateTimeKind.Utc);

        var change = Assert.Single(Plan([item], pending: [new PendingOverlay(item.Id, null, watched)]).Changes);

        Assert.Equal(new FieldChange<bool>(false, true), change.Played);
        Assert.Equal(new FieldChange<int>(0, 1), change.PlayCount);
        Assert.Equal(watched, change.LastPlayed!.New);
    }

    [Fact]
    public void AnOverlayEqualToAnExistingEntryIsSkippedLikeTheWriterWould()
    {
        // Super 8 already has 2023-11-05 11:00:00 local = 10:00:00 UTC.
        var item = Item(CcKind.Movie, "Super 8", Id("Super 8"));
        var actual = new Dictionary<Guid, JfUserData> { [item.Id] = new(true, 1, new DateTime(2023, 11, 5, 10, 0, 0, DateTimeKind.Utc), false) };

        var plan = Plan([item], actual, [new PendingOverlay(Guid.NewGuid(), Id("Super 8"), new DateTime(2023, 11, 5, 10, 0, 0, DateTimeKind.Utc))]);

        Assert.DoesNotContain(plan.Changes, c => c.HasPlayedChange);
    }

    [Fact]
    public void UnmatchedItemsLoseOnlyJccTags()
    {
        var item = Item(CcKind.Movie, "Unknown", null, "jcc-rating-bad", "JCC-old-group", "favourite-of-mine");
        var actual = new Dictionary<Guid, JfUserData> { [item.Id] = new(true, 3, DateTime.UtcNow, true) };

        var change = Assert.Single(Plan([item], actual).Changes);

        Assert.False(change.HasUserDataChange);
        Assert.Empty(change.AddTags);
        Assert.Equal(["JCC-old-group", "jcc-rating-bad"], change.RemoveTags);
    }

    [Fact]
    public void TagComparisonIgnoresCase()
    {
        var item = Item(CcKind.Series, "Steins;Gate", Id("Steins;Gate"), "JCC-RATING-FAVOURITE");
        var actual = new Dictionary<Guid, JfUserData> { [item.Id] = new(false, 0, null, true) };

        Assert.Empty(Plan([item], actual).Changes);
    }

    [Fact]
    public void ElementsWithProblemsAreLeftAlone()
    {
        var library = FixtureLibrary.Load(f => f.Execute(
            "ClipCornUserData.db",
            "UPDATE MOVIES SET VIEWED_HISTORY = '[\"bad\"]' WHERE ID = '01a17a31-0270-7899-ba94-8e72f8bc14da'"));
        var item = Item(CcKind.Movie, "Super 8", "01a17a31-0270-7899-ba94-8e72f8bc14da", "jcc-stale");

        Assert.Empty(Plan([item], library: library).Changes);
    }

    [Fact]
    public void SettingsLimitWhatIsOwned()
    {
        var item = Item(CcKind.Movie, "Inglourious Basterds", Id("Inglourious Basterds"));

        var change = Assert.Single(Plan([item], options: Options(c =>
        {
            c.SyncPlayCount = false;
            c.SyncLastPlayed = false;
            c.SyncFavorites = false;
            c.GroupTagsEnabled = false;
        })).Changes);

        Assert.NotNull(change.Played);
        Assert.Null(change.PlayCount);
        Assert.Null(change.LastPlayed);
        Assert.Null(change.Favorite);
        Assert.Equal(["jcc-rating-masterpiece"], change.AddTags);

        Assert.Equal(["jcc-tarantino"], Assert.Single(Plan([item], options: Options(c => c.ApplyRatingsToMovies = false)).Changes).AddTags);
    }

    [Fact]
    public void GroupTagsCanIncludeParents()
    {
        var item = Item(CcKind.Movie, "Death Proof", Id("Death Proof: Todsicher"));
        var change = Assert.Single(Plan([item], options: Options(c => c.GroupTagsIncludeParents = true)).Changes);
        Assert.Equal(["jcc-kino", "jcc-tarantino"], change.AddTags);
    }

    [Fact]
    public void FavouriteScoresAreConfigurable()
    {
        // Super 8 is stored score 3 (Recommended).
        var item = Item(CcKind.Movie, "Super 8", Id("Super 8"));
        Assert.Null(Assert.Single(Plan([item]).Changes).Favorite);
        Assert.Equal(new FieldChange<bool>(false, true), Assert.Single(Plan([item], options: Options(c => c.FavoriteScores = [3])).Changes).Favorite);
    }

    [Fact]
    public void SeasonsAndEpisodesGetScoresButNoGroups()
    {
        var season = Item(CcKind.Season, "Season 1", Id("01 - ONA"));
        var episode = Item(CcKind.Episode, "Parania", Library.Episodes.Single(e => e.Score.Stored == 5).Id);

        var plan = Plan([season, episode]);

        Assert.Equal(["jcc-rating-favourite"], plan.Changes.Single(c => c.ItemId == season.Id).AddTags);
        var ep = plan.Changes.Single(c => c.ItemId == episode.Id);
        Assert.Equal(["jcc-rating-masterpiece"], ep.AddTags);
        Assert.Equal(new FieldChange<int>(0, 2), ep.PlayCount);
    }

    [Fact]
    public void SameSecondComparison()
    {
        var a = new DateTime(2026, 1, 1, 10, 0, 0, 100, DateTimeKind.Utc);
        Assert.True(SyncPlanner.SameSecond(a, a.AddMilliseconds(800)));
        Assert.False(SyncPlanner.SameSecond(a, a.AddSeconds(1)));
        Assert.True(SyncPlanner.SameSecond(null, null));
        Assert.False(SyncPlanner.SameSecond(a, null));
    }
}
