using Jellyfin.Plugin.CcSync.ClipCorn.Model;

namespace Jellyfin.Plugin.CcSync.Tests.ClipCorn;

public class CcColumnTests
{
    [Theory]
    [InlineData("[]", "[]")]
    [InlineData("", "[]")]
    [InlineData("[3]", "[3]")]
    [InlineData("[5,0,3]", "[0,3,5]")]
    [InlineData("[3,3]", "[3]")]
    public void ParsesTagLists(string json, string expected)
    {
        Assert.True(CcTagList.TryParse(json, out var tags));
        Assert.Equal(expected, tags.ToJson());
    }

    [Theory]
    [InlineData("[9]")]
    [InlineData("[-1]")]
    [InlineData("[\"3\"]")]
    [InlineData("[3.5]")]
    [InlineData("{}")]
    [InlineData("[3")]
    public void RejectsTagListsJClipCornWouldNotRead(string json)
    {
        Assert.False(CcTagList.TryParse(json, out _));
    }

    [Fact]
    public void RemovesWatchLater()
    {
        Assert.True(CcTagList.TryParse("[0,3,5]", out var tags));
        Assert.True(tags.Contains(CcTagList.WatchLater));

        var without = tags.Without(CcTagList.WatchLater);
        Assert.Equal("[0,5]", without.ToJson());
        Assert.Equal("[0,3,5]", tags.ToJson());
        Assert.Equal("[]", CcTagList.Empty.Without(CcTagList.WatchLater).ToJson());
        Assert.Same(without, without.Without(CcTagList.WatchLater));
    }

    [Theory]
    [InlineData(0, 1, "crap")]
    [InlineData(1, 2, "bad")]
    [InlineData(2, 3, "not-recommended")]
    [InlineData(7, 4, "average")]
    [InlineData(3, 5, "recommended")]
    [InlineData(4, 6, "favourite")]
    [InlineData(5, 7, "masterpiece")]
    [InlineData(6, 0, "")]
    public void MapsStoredScoresToRanks(int stored, int rank, string slug)
    {
        var score = CcScore.FromStored(stored);
        Assert.NotNull(score);
        Assert.Equal(rank, score.Rank);
        Assert.Equal(slug, score.Slug);
        Assert.Equal(stored != 6, score.IsRated);
    }

    [Fact]
    public void UnknownScoreIsNull()
    {
        Assert.Null(CcScore.FromStored(8));
        Assert.Null(CcScore.FromStored(-1));
    }

    [Fact]
    public void ReadsOnlineReferences()
    {
        Assert.True(CcOnlineRefs.TryParse("[{\"t\":\"tmdb\",\"id\":\"movie/16869\"},{\"t\":\"imdb\",\"id\":\"tt0361748\"}]", out var movie));
        Assert.Equal("16869", movie.Tmdb("movie"));
        Assert.Null(movie.Tmdb("tv"));
        Assert.Equal("tt0361748", movie.Imdb());

        Assert.True(CcOnlineRefs.TryParse("[{\"t\":\"tmdb\",\"id\":\"tv/1399\"},{\"t\":\"imdb\",\"id\":\"tt0944947\"}]", out var series));
        Assert.Equal("1399", series.Tmdb("tv"));
        Assert.Null(series.Tmdb("movie"));
    }

    [Fact]
    public void PrefersReferencesWithoutDescription()
    {
        Assert.True(CcOnlineRefs.TryParse(
            "[{\"t\":\"tmdb\",\"id\":\"movie/1\",\"d\":\"Part 2\"},{\"t\":\"tmdb\",\"id\":\"movie/2\"},{\"t\":\"imdb\",\"id\":\"tt1\",\"d\":\"x\"}]",
            out var refs));
        Assert.Equal("2", refs.Tmdb("movie"));
        Assert.Equal("tt1", refs.Imdb());
    }

    [Fact]
    public void IgnoresMalformedIdentifiers()
    {
        Assert.True(CcOnlineRefs.TryParse("[{\"t\":\"tmdb\",\"id\":\"movie/abc\"},{\"t\":\"imdb\",\"id\":\"0361748\"},{\"t\":\"amzn\",\"id\":\"B00X\"}]", out var refs));
        Assert.Null(refs.Tmdb("movie"));
        Assert.Null(refs.Imdb());
        Assert.Equal(3, refs.Refs.Count);
    }

    [Theory]
    [InlineData("[{\"t\":\"tmdb\"}]")]
    [InlineData("[\"tmdb\"]")]
    [InlineData("{\"t\":\"tmdb\",\"id\":\"movie/1\"}")]
    [InlineData("[{\"t\":\"tmdb\",\"id\":1}]")]
    public void RejectsMalformedReferenceLists(string json)
    {
        Assert.False(CcOnlineRefs.TryParse(json, out _));
    }

    [Fact]
    public void ReadsGroupsDroppingInvalidNames()
    {
        Assert.True(CcGroupList.TryParse("[\"MCU\",\"Star Wars\",\"Bad/Name\",\"MCU\",\"\"]", out var groups));
        Assert.Equal(["MCU", "Star Wars"], groups);

        Assert.True(CcGroupList.TryParse("[]", out var empty));
        Assert.Empty(empty);

        Assert.False(CcGroupList.TryParse("[1]", out _));
        Assert.False(CcGroupList.TryParse("[\"MCU\"", out _));
    }

    [Fact]
    public void WalksGroupAncestors()
    {
        var library = new CcLibrary(
            new CcDatabaseInfo(38, "d", 3, "d", 38, true),
            [],
            [],
            [],
            [],
            [new CcGroup("Root", string.Empty), new CcGroup("Mid", "Root"), new CcGroup("Leaf", "Mid"), new CcGroup("LoopA", "LoopB"), new CcGroup("LoopB", "LoopA")]);

        Assert.Equal(["Mid", "Root"], library.AncestorsOf("Leaf"));
        Assert.Empty(library.AncestorsOf("Root"));
        Assert.Empty(library.AncestorsOf("Unknown"));
        Assert.Equal(["LoopB"], library.AncestorsOf("LoopA"));
    }
}
