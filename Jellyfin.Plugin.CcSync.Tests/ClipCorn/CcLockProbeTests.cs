using Jellyfin.Plugin.CcSync.ClipCorn;
using Jellyfin.Plugin.CcSync.Tests.Support;

namespace Jellyfin.Plugin.CcSync.Tests.ClipCorn;

public class CcLockProbeTests
{
    private static readonly DateTime Now = new(2026, 10, 26, 19, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Stale = TimeSpan.FromMinutes(180);

    private static CcLockStatus Eval(string? content, DateTime? modified = null)
        => CcLockProbe.Evaluate("/x/ClipCornDB.db.~lock", content, modified, Stale, Now);

    [Fact]
    public void AbsentIsNotHeld()
    {
        var status = Eval(null);
        Assert.Equal(CcLockFormat.Absent, status.Format);
        Assert.False(status.Held);
        Assert.False(status.Stale);
    }

    [Fact]
    public void FreshJsonHeartbeatIsHeld()
    {
        var status = Eval("{\"pid\": 1234, \"c\": \"2026-10-26T12:00:00Z\", \"u\": \"2026-10-26T18:00:00Z\", \"host\": \"DESKTOP-X\"}", Now.AddDays(-3));
        Assert.Equal(CcLockFormat.Json, status.Format);
        Assert.True(status.Held);
        Assert.Equal("DESKTOP-X", status.Host);
        Assert.Equal(1234, status.Pid);
        Assert.Equal(new DateTime(2026, 10, 26, 18, 0, 0, DateTimeKind.Utc), status.HeartbeatUtc);
        Assert.Equal(new DateTime(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc), status.AcquiredUtc);
        Assert.Contains("DESKTOP-X", status.Description, StringComparison.Ordinal);
        Assert.Contains("heartbeat 1 h 0 min ago", status.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void JsonHeartbeatDecidesOverAFreshMtime()
    {
        var status = Eval("{\"pid\": 1234, \"c\": \"2026-10-26T12:00:00Z\", \"u\": \"2026-10-26T15:59:00Z\", \"host\": \"DESKTOP-X\"}", Now);
        Assert.False(status.Held);
        Assert.True(status.Stale);
        Assert.Contains("not shut down cleanly", status.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void HeartbeatJustInsideTheWindowIsHeld()
    {
        Assert.True(Eval("{\"u\": \"2026-10-26T16:00:01Z\"}").Held);
        Assert.False(Eval("{\"u\": \"2026-10-26T16:00:00Z\"}").Held);
    }

    [Fact]
    public void FutureHeartbeatIsHeld()
    {
        Assert.True(Eval("{\"u\": \"2026-10-27T19:00:00Z\"}").Held);
    }

    [Theory]
    [InlineData("219994", CcLockFormat.BarePid)]
    [InlineData("{\"pid\": 1234, \"c\": \"2026-10-26T", CcLockFormat.Malformed)]
    [InlineData("garbage", CcLockFormat.Malformed)]
    [InlineData("{\"pid\": 1234, \"u\": \"yesterday\"}", CcLockFormat.Json)]
    [InlineData("", CcLockFormat.Malformed)]
    public void WithoutAHeartbeatTheMtimeDecides(string content, CcLockFormat format)
    {
        var fresh = Eval(content, Now.AddMinutes(-10));
        Assert.Equal(format, fresh.Format);
        Assert.True(fresh.Held);

        var old = Eval(content, Now.AddMinutes(-181));
        Assert.False(old.Held);
        Assert.True(old.Stale);
    }

    [Fact]
    public void BarePidIsReported()
    {
        Assert.Equal(219994, Eval("219994\n", Now).Pid);
    }

    [Fact]
    public void ByteOrderMarkIsIgnored()
    {
        Assert.Equal(CcLockFormat.Json, Eval("﻿{\"u\": \"2026-10-26T18:00:00Z\"}").Format);
    }

    [Fact]
    public void ReadsFilesFromDisk()
    {
        using var fixture = new FixtureDirectory();
        var stale = TimeSpan.FromMinutes(180);

        Assert.Equal(CcLockFormat.Absent, CcLockProbe.Probe(fixture.Files.MainLock, stale, DateTime.UtcNow).Format);

        File.WriteAllText(fixture.Files.MainLock, "219994");
        var bare = CcLockProbe.Probe(fixture.Files.MainLock, stale, DateTime.UtcNow);
        Assert.Equal(CcLockFormat.BarePid, bare.Format);
        Assert.True(bare.Held);

        File.SetLastWriteTimeUtc(fixture.Files.MainLock, DateTime.UtcNow.AddHours(-4));
        Assert.False(CcLockProbe.Probe(fixture.Files.MainLock, stale, DateTime.UtcNow).Held);
    }

    [Fact]
    public void UnreadableLockCountsAsHeld()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
        {
            return;
        }

        using var fixture = new FixtureDirectory();
        File.WriteAllText(fixture.Files.MainLock, "{\"u\": \"2020-01-01T00:00:00Z\"}");
        File.SetUnixFileMode(fixture.Files.MainLock, UnixFileMode.None);

        var status = CcLockProbe.Probe(fixture.Files.MainLock, TimeSpan.FromMinutes(180), DateTime.UtcNow);
        Assert.Equal(CcLockFormat.Unreadable, status.Format);
        Assert.True(status.Held);

        File.SetUnixFileMode(fixture.Files.MainLock, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [Fact]
    public void DirectoryIsBlockedByEitherLockOrAJournal()
    {
        using var fixture = new FixtureDirectory();
        var stale = TimeSpan.FromMinutes(180);

        Assert.False(CcLockProbe.ProbeDirectory(fixture.Files, stale, DateTime.UtcNow).Blocked);

        File.WriteAllText(fixture.Files.HistoryLock, "{\"u\": \"" + DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture) + "\", \"host\": \"nas\"}");
        var locked = CcLockProbe.ProbeDirectory(fixture.Files, stale, DateTime.UtcNow);
        Assert.True(locked.Blocked);
        Assert.Contains("ClipCornHistory.db.~lock held by jClipCorn on nas", locked.Describe(), StringComparison.Ordinal);
        File.Delete(fixture.Files.HistoryLock);

        File.WriteAllText(fixture.Files.UserDb + "-journal", string.Empty);
        var journal = CcLockProbe.ProbeDirectory(fixture.Files, stale, DateTime.UtcNow);
        Assert.True(journal.Blocked);
        Assert.Contains("ClipCornUserData.db-journal", journal.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheCallersOwnJournalDoesNotBlock()
    {
        using var fixture = new FixtureDirectory();
        File.WriteAllText(fixture.Files.UserDb + "-journal", string.Empty);
        Assert.False(CcLockProbe.ProbeDirectory(fixture.Files, Stale, DateTime.UtcNow, ownTransactionOn: fixture.Files.UserDb).Blocked);

        File.WriteAllText(fixture.Files.MainDb + "-journal", string.Empty);
        Assert.True(CcLockProbe.ProbeDirectory(fixture.Files, Stale, DateTime.UtcNow, ownTransactionOn: fixture.Files.UserDb).Blocked);
    }
}
