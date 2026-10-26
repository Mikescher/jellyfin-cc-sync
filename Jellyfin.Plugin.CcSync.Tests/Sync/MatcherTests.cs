using Jellyfin.Plugin.CcSync.ClipCorn.Model;
using Jellyfin.Plugin.CcSync.Sync;
using Jellyfin.Plugin.CcSync.Tests.Support;

namespace Jellyfin.Plugin.CcSync.Tests.Sync;

public class MatcherTests
{
    private static readonly CcLibrary Library = FixtureLibrary.Load();

    private static string Id(string name) => Library.Elements.Single(e => e.Name == name).Id;

    private static JfItemInfo Item(CcKind kind, string name, string? clipcorn = null, int? year = null, string? tmdb = null, string? imdb = null, params string[] tags)
        => new(Guid.NewGuid(), kind, name, year, clipcorn, tmdb, imdb, tags);

    [Fact]
    public void MatchesByClipCornId()
    {
        var item = Item(CcKind.Movie, "anything", Id("Super 8"));
        var result = Matcher.Match(Library, [item]);

        var matched = Assert.Single(result.Matched);
        Assert.Equal("Super 8", matched.Element.Name);
        Assert.False(matched.ByFallback);
        Assert.Equal(Library.Elements.Count() - 1, result.UnmatchedCc.Count);
    }

    [Fact]
    public void ReportsKindMismatchesAndUnknownIds()
    {
        var mismatch = Item(CcKind.Movie, "Steins;Gate", Id("Steins;Gate"));
        var unknown = Item(CcKind.Episode, "x", "01a17a31-ffff-7fff-bfff-ffffffffffff");
        var noId = Item(CcKind.Season, "Season 1");

        var result = Matcher.Match(Library, [mismatch, unknown, noId]);

        Assert.Empty(result.Matched);
        Assert.Contains("is a Series", Assert.Single(result.KindMismatches).Reason, StringComparison.Ordinal);
        Assert.Equal(2, result.UnmatchedJf.Count);
        Assert.Contains(result.UnmatchedJf, i => i.Reason.Contains("does not exist", StringComparison.Ordinal));
        Assert.Contains(result.UnmatchedJf, i => i.Reason.Contains("no fallback", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("16869", null, "Whatever", 1900, "Inglourious Basterds", "tmdb 16869")]
    [InlineData(null, "tt0109830", "Whatever", 1900, "Forrest Gump", "imdb tt0109830")]
    [InlineData(null, null, "DIE  Rückkehr des Königs!", 2003, "Die Rückkehr des Königs", "name")]
    [InlineData(null, null, "Die Ruckkehr des Konigs", 2003, "Die Rückkehr des Königs", "name")]
    public void FallsBackForMoviesWithoutId(string? tmdb, string? imdb, string name, int year, string expected, string via)
    {
        var result = Matcher.Match(Library, [Item(CcKind.Movie, name, null, year, tmdb, imdb)]);

        var matched = Assert.Single(result.Matched);
        Assert.Equal(expected, matched.Element.Name);
        Assert.True(matched.ByFallback);
        Assert.StartsWith(via, matched.Via, StringComparison.Ordinal);
    }

    [Fact]
    public void NameWithoutYearDoesNotMatchAMovie()
    {
        var result = Matcher.Match(Library, [Item(CcKind.Movie, "Super 8")]);
        Assert.Empty(result.Matched);
        Assert.Single(result.UnmatchedJf);
    }

    [Fact]
    public void FallsBackForSeriesByName()
    {
        var result = Matcher.Match(Library, [Item(CcKind.Series, "soul eater")]);
        Assert.Equal("Soul Eater", Assert.Single(result.Matched).Element.Name);
    }

    [Fact]
    public void AnElementMatchedByIdIsNotOfferedToFallbacks()
    {
        var byId = Item(CcKind.Movie, "Inglourious Basterds", Id("Inglourious Basterds"));
        var byTmdb = Item(CcKind.Movie, "Inglourious Basterds", null, 2009, "16869");

        var result = Matcher.Match(Library, [byId, byTmdb]);

        Assert.Single(result.Matched);
        Assert.Equal(byTmdb.Id, Assert.Single(result.UnmatchedJf).Item.Id);
    }

    [Fact]
    public void AFallbackHitTwiceOnTheJellyfinSideIsAmbiguous()
    {
        var a = Item(CcKind.Movie, "Inglourious Basterds", null, 2009, "16869");
        var b = Item(CcKind.Movie, "Inglourious Basterds", null, 2009, "16869");

        var result = Matcher.Match(Library, [a, b]);

        Assert.Empty(result.Matched);
        Assert.Equal(2, result.Ambiguous.Count);
    }

    [Fact]
    public void AFallbackHittingSeveralElementsIsAmbiguous()
    {
        var library = FixtureLibrary.Load(f => f.Execute(
            "ClipCornDB.db",
            "UPDATE MOVIES SET ONLINEREF = '[{\"t\":\"tmdb\",\"id\":\"movie/16869\"}]' WHERE NAME = 'Death Proof: Todsicher'"));

        var result = Matcher.Match(library, [Item(CcKind.Movie, "Something", null, 2009, "16869")]);

        Assert.Contains("several", Assert.Single(result.Ambiguous).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void OneElementMayMatchSeveralItemsById()
    {
        var a = Item(CcKind.Movie, "Super 8", Id("Super 8"));
        var b = Item(CcKind.Movie, "Super 8", Id("Super 8"));

        var result = Matcher.Match(Library, [a, b]);

        Assert.Equal(2, result.Matched.Count);
        Assert.Equal("Super 8", Assert.Single(result.MatchedSeveralTimes).Name);
    }
}
