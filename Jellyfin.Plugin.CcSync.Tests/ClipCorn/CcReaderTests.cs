using Jellyfin.Plugin.CcSync.ClipCorn;
using Jellyfin.Plugin.CcSync.ClipCorn.Model;
using Jellyfin.Plugin.CcSync.Tests.Support;

namespace Jellyfin.Plugin.CcSync.Tests.ClipCorn;

public class CcReaderTests
{
    private static async Task<CcLibrary> ReadAsync(FixtureDirectory fixture)
    {
        using var snapshot = await CcSnapshot.TakeAsync(fixture.Files, fixture.SnapshotRoot, true, CancellationToken.None, TimeSpan.Zero);
        return CcReader.Read(snapshot.MainDb, snapshot.UserDb!, CancellationToken.None);
    }

    private static CcMovie Movie(CcLibrary library, string name) => library.Movies.Single(m => m.Name == name);

    [Fact]
    public async Task ReadsTheFixture()
    {
        using var fixture = new FixtureDirectory();
        var library = await ReadAsync(fixture);

        Assert.Equal(17, library.Movies.Count);
        Assert.Equal(3, library.Series.Count);
        Assert.Equal(7, library.Seasons.Count);
        Assert.Equal(86, library.Episodes.Count);
        Assert.Equal(3, library.Groups.Count);
        Assert.Empty(library.Problems);
        Assert.Equal(0, library.OrphanUserRows);

        Assert.Equal(38, library.Info.MainVersion);
        Assert.Equal(3, library.Info.UserVersion);
        Assert.Equal(library.Info.MainDuuid, library.Info.UserMainDuuid);
        Assert.True(library.Info.HistoryEnabled);
    }

    [Fact]
    public async Task ReadsMovieColumns()
    {
        using var fixture = new FixtureDirectory();
        var library = await ReadAsync(fixture);

        var gefaehrten = Movie(library, "Die Gefährten");
        Assert.Equal("Der Herr der Ringe", gefaehrten.Zyklus);
        Assert.Equal(1, gefaehrten.ZyklusNumber);
        Assert.Equal(2001, gefaehrten.Year);
        Assert.Equal("[\"UNSPECIFIED\"]", gefaehrten.History.ToJson());
        Assert.True(gefaehrten.Tags.Contains(CcTagList.WatchLater));
        Assert.Equal("120", gefaehrten.OnlineRefs.Tmdb("movie"));

        var basterds = Movie(library, "Inglourious Basterds");
        Assert.Equal(["Tarantino"], basterds.Groups);
        Assert.Equal(5, basterds.Score.Stored);
        Assert.Equal("[\"UNSPECIFIED\",\"2016-11-01\",\"2016-11-18 22:50:38\"]", basterds.History.ToJson());
        Assert.Equal(["Kino"], library.AncestorsOf("Tarantino"));

        var gump = Movie(library, "Forrest Gump");
        Assert.Equal("13", gump.OnlineRefs.Tmdb("movie"));
        Assert.Equal("tt0109830", gump.OnlineRefs.Imdb());

        Assert.Equal("[\"2023-11-05 11:00:00\"]", Movie(library, "Super 8").History.ToJson());
    }

    [Fact]
    public async Task MissingUserRowsMeanDefaults()
    {
        using var fixture = new FixtureDirectory();
        var library = await ReadAsync(fixture);

        var bomber = Movie(library, "Der Bomber");
        Assert.False(bomber.History.IsViewed);
        Assert.Equal(CcScore.NoRating, bomber.Score);
        Assert.Empty(bomber.Tags.Tags);
        Assert.Equal(["SpencerHill"], bomber.Groups);

        var soulEater = library.Series.Single(s => s.Name == "Soul Eater");
        Assert.Equal(CcScore.NoRating, soulEater.Score);
    }

    [Fact]
    public async Task ReadsTheSeriesTree()
    {
        using var fixture = new FixtureDirectory();
        var library = await ReadAsync(fixture);

        var steinsGate = library.Series.Single(s => s.Name == "Steins;Gate");
        Assert.Equal(4, steinsGate.Score.Stored);
        Assert.True(steinsGate.Tags.Contains(CcTagList.WatchLater));

        var ona = library.Seasons.Single(s => s.Name == "01 - ONA");
        Assert.Equal(steinsGate.Id, ona.SeriesId);
        Assert.Equal(4, ona.Score.Stored);
        Assert.Equal(2011, ona.Year);

        var episodes = library.Episodes.Where(e => e.SeasonId == ona.Id).OrderBy(e => e.EpisodeNumber).ToList();
        Assert.True(episodes[0].Tags.Contains(CcTagList.WatchLater));
        Assert.False(episodes[0].History.IsViewed);
        Assert.Equal(5, episodes[1].Score.Stored);
        Assert.Equal("[\"2016-06-12\",\"2016-07-09 20:15:00\"]", episodes[1].History.ToJson());

        Assert.Same(steinsGate, library.SeriesOf(episodes[1]));
        Assert.Same(library.Find(episodes[1].Id.ToUpperInvariant()), episodes[1]);
    }

    [Fact]
    public async Task UnparseableColumnsOnlyMarkTheirElement()
    {
        using var fixture = new FixtureDirectory();
        var id = (string)fixture.Scalar(CcFiles.MainDbName, "SELECT ID FROM MOVIES WHERE NAME = 'Super 8'")!;
        fixture.Execute(CcFiles.UserDbName, $"DROP TRIGGER IF EXISTS JCCTRIGGER_AUTOHISTORY_UPD_USERDATA_MOVIES_VIEWED_HISTORY; UPDATE MOVIES SET VIEWED_HISTORY = '[\"2023-11-05 11:00:00\",\"bad\"]', SCORE = 9 WHERE ID = '{id}'");
        fixture.Execute(CcFiles.MainDbName, "DROP TRIGGER IF EXISTS JCCTRIGGER_AUTOHISTORY_UPD_MAIN_MOVIES_GROUPS; UPDATE MOVIES SET GROUPS = '[\"Tarantino\"' WHERE NAME = 'Death Proof: Todsicher'");

        var library = await ReadAsync(fixture);

        var problems = library.Problems.ToList();
        Assert.Equal(2, problems.Count);

        var super8 = Movie(library, "Super 8");
        Assert.Contains("VIEWED_HISTORY", super8.Problem, StringComparison.Ordinal);
        Assert.Contains("SCORE 9", super8.Problem, StringComparison.Ordinal);
        Assert.Contains("GROUPS", Movie(library, "Death Proof: Todsicher").Problem, StringComparison.Ordinal);
        Assert.Null(Movie(library, "Die Gefährten").Problem);
    }

    [Fact]
    public async Task RefusesAForeignUserDatabase()
    {
        using var fixture = new FixtureDirectory();
        fixture.Execute(CcFiles.UserDbName, "DROP TRIGGER IF EXISTS JCCTRIGGER_AUTOHISTORY_UPD_USERDATA_INFO_IVALUE; UPDATE INFO SET IVALUE = 'other' WHERE IKEY = 'MAINDB_DUUID'");

        var ex = await Assert.ThrowsAsync<CcSchemaException>(() => ReadAsync(fixture));
        Assert.Contains("belongs to database other", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(CcFiles.MainDbName, "VERSION_DB", "39", "schema version 39")]
    [InlineData(CcFiles.UserDbName, "VERSION_DB", "4", "schema version 4")]
    [InlineData(CcFiles.UserDbName, "VERSION_MAINDB", "40", "main schema 40")]
    public async Task RefusesUnsupportedVersions(string file, string key, string value, string expected)
    {
        using var fixture = new FixtureDirectory();
        fixture.Execute(file, $"DROP TRIGGER IF EXISTS JCCTRIGGER_AUTOHISTORY_UPD_USERDATA_INFO_IVALUE; DROP TRIGGER IF EXISTS JCCTRIGGER_AUTOHISTORY_UPD_MAIN_INFO_IVALUE; UPDATE INFO SET IVALUE = '{value}' WHERE IKEY = '{key}'");

        var ex = await Assert.ThrowsAsync<CcSchemaException>(() => ReadAsync(fixture));
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefusesAFileThatIsNotADatabase()
    {
        using var fixture = new FixtureDirectory();
        File.WriteAllText(fixture.Files.UserDb, "this is not sqlite");

        await Assert.ThrowsAsync<CcSnapshotException>(() => ReadAsync(fixture));
    }
}
