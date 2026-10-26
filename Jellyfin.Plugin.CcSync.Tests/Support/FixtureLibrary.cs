using Jellyfin.Plugin.CcSync.ClipCorn;
using Jellyfin.Plugin.CcSync.ClipCorn.Model;

namespace Jellyfin.Plugin.CcSync.Tests.Support;

public static class FixtureLibrary
{
    /// <summary>Reads the fixture pair, after an optional change to a temp copy.</summary>
    public static CcLibrary Load(Action<FixtureDirectory>? prepare = null)
    {
        using var fixture = new FixtureDirectory();
        prepare?.Invoke(fixture);
        using var snapshot = CcSnapshot.TakeAsync(fixture.Files, fixture.SnapshotRoot, true, CancellationToken.None, TimeSpan.Zero).GetAwaiter().GetResult();
        return CcReader.Read(snapshot.MainDb, snapshot.UserDb!, CancellationToken.None);
    }
}
