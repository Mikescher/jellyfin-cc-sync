using Jellyfin.Plugin.CcSync.ClipCorn;
using Jellyfin.Plugin.CcSync.Tests.Support;

namespace Jellyfin.Plugin.CcSync.Tests.ClipCorn;

public class CcSnapshotTests
{
    [Fact]
    public async Task CopiesBothFilesAndDeletesThemOnDispose()
    {
        using var fixture = new FixtureDirectory();

        string directory;
        using (var snapshot = await CcSnapshot.TakeAsync(fixture.Files, fixture.SnapshotRoot, true, CancellationToken.None, TimeSpan.Zero))
        {
            directory = snapshot.Directory;
            Assert.True(File.Exists(snapshot.MainDb));
            Assert.True(File.Exists(snapshot.UserDb));
            Assert.Equal(File.ReadAllBytes(fixture.Files.MainDb), File.ReadAllBytes(snapshot.MainDb));
            Assert.Equal(File.ReadAllBytes(fixture.Files.UserDb), File.ReadAllBytes(snapshot.UserDb!));
        }

        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public async Task MainOnlySnapshotHasNoUserDb()
    {
        using var fixture = new FixtureDirectory();
        using var snapshot = await CcSnapshot.TakeAsync(fixture.Files, fixture.SnapshotRoot, false, CancellationToken.None, TimeSpan.Zero);
        Assert.Null(snapshot.UserDb);
        Assert.False(File.Exists(Path.Combine(snapshot.Directory, CcFiles.UserDbName)));
    }

    [Fact]
    public async Task RefusesWhileAJournalExists()
    {
        using var fixture = new FixtureDirectory();
        File.WriteAllText(fixture.Files.MainDb + "-journal", string.Empty);

        var ex = await Assert.ThrowsAsync<CcSnapshotException>(
            () => CcSnapshot.TakeAsync(fixture.Files, fixture.SnapshotRoot, true, CancellationToken.None, TimeSpan.Zero));
        Assert.Contains("ClipCornDB.db-journal", ex.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateDirectories(fixture.SnapshotRoot));
    }

    [Fact]
    public void ReadsTheHeaderChangeCounter()
    {
        using var fixture = new FixtureDirectory();
        var before = CcSqlite.ReadChangeCounter(fixture.Files.UserDb);
        Assert.NotNull(before);

        fixture.Execute(CcFiles.UserDbName, "UPDATE INFO SET IVALUE = 'someone' WHERE IKEY = 'CREATION_USERNAME'");
        Assert.Equal(before + 1, CcSqlite.ReadChangeCounter(fixture.Files.UserDb));

        File.WriteAllText(fixture.Files.UserDb, "not a database");
        Assert.Null(CcSqlite.ReadChangeCounter(fixture.Files.UserDb));
    }

    [Fact]
    public async Task RemovesOldLeftoverSnapshots()
    {
        using var fixture = new FixtureDirectory();
        var leftover = Directory.CreateDirectory(Path.Combine(fixture.SnapshotRoot, "old"));
        var recent = Directory.CreateDirectory(Path.Combine(fixture.SnapshotRoot, "recent"));
        Directory.SetLastWriteTimeUtc(leftover.FullName, DateTime.UtcNow.AddDays(-1));

        using var snapshot = await CcSnapshot.TakeAsync(fixture.Files, fixture.SnapshotRoot, false, CancellationToken.None, TimeSpan.Zero);

        Assert.False(leftover.Exists && Directory.Exists(leftover.FullName));
        Assert.True(Directory.Exists(recent.FullName));
    }
}
