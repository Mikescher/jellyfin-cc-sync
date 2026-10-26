using Jellyfin.Plugin.CcSync.ClipCorn;
using Jellyfin.Plugin.CcSync.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.CcSync.Tests.ClipCorn;

public class CcProbeTests
{
    private static CcProbe Probe(FixtureDirectory fixture)
        => new(new PluginPaths(Path.Combine(fixture.SnapshotRoot, "data")), NullLogger<CcProbe>.Instance);

    [Fact]
    public async Task PassesOnTheFixture()
    {
        using var fixture = new FixtureDirectory();
        var result = await Probe(fixture).RunAsync(fixture.Root, 180, "Europe/Berlin", CancellationToken.None);

        Assert.True(result.Ok, string.Join("\n", result.Checks.Select(c => $"{c.Name} {c.Level} {c.Message}")));
        Assert.Contains(result.Checks, c => c.Name == "Content" && c.Message.StartsWith("17 movies, 3 series, 7 seasons, 86 episodes, 3 groups", StringComparison.Ordinal));
        Assert.Contains(result.Checks, c => c.Name == "Write access" && c.Level == "ok");
        Assert.False(File.Exists(Path.Combine(fixture.Root, CcFiles.ProbeFileName)));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(fixture.SnapshotRoot, "data", "snapshots")));
    }

    [Fact]
    public async Task ReportsAnEmptyDirectory()
    {
        using var fixture = new FixtureDirectory();
        var empty = Directory.CreateDirectory(Path.Combine(fixture.SnapshotRoot, "empty")).FullName;

        var result = await Probe(fixture).RunAsync(empty, 180, "Europe/Berlin", CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains(result.Checks, c => c.Name == "Directory" && c.Message.Contains("is empty", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReportsAHeldLockAndABadTimeZone()
    {
        using var fixture = new FixtureDirectory();
        File.WriteAllText(fixture.Files.MainLock, "{\"u\": \"" + DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture) + "\", \"host\": \"desk\"}");

        var result = await Probe(fixture).RunAsync(fixture.Root, 180, "Mars/Olympus", CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains(result.Checks, c => c.Name == "Time zone" && c.Level == "error");
        Assert.Contains(result.Checks, c => c.Name == "Lock" && c.Level == "warn" && c.Message.Contains("desk", StringComparison.Ordinal));
        Assert.Contains(result.Checks, c => c.Name == "Schema" && c.Level == "ok");
    }
}
