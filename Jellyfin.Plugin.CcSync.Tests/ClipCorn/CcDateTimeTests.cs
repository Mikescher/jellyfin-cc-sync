using Jellyfin.Plugin.CcSync.ClipCorn.Model;

namespace Jellyfin.Plugin.CcSync.Tests.ClipCorn;

public class CcDateTimeTests
{
    private static readonly TimeZoneInfo Berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
    private static readonly TimeOnly Noon = new(12, 0);

    private static CcDateTime P(string raw)
    {
        Assert.True(CcDateTime.TryParse(raw, out var value), raw);
        return value;
    }

    [Theory]
    [InlineData("UNSPECIFIED", CcDateTimeKind.Unspecified)]
    [InlineData("2016-09-07", CcDateTimeKind.DateOnly)]
    [InlineData("2016-09-07 12:13:14", CcDateTimeKind.DateTime)]
    [InlineData("2016-09-07 12:13:14.123", CcDateTimeKind.DateTime)]
    public void ParsesEveryEncoding(string raw, CcDateTimeKind kind)
    {
        var value = P(raw);
        Assert.Equal(kind, value.Kind);
        Assert.Equal(raw, value.Raw);
    }

    [Fact]
    public void MillisecondVariantDropsTheFraction()
    {
        var value = P("2016-09-07 12:13:14.999");
        Assert.Equal(new TimeOnly(12, 13, 14), value.Time);
        Assert.True(value.IsEqual(P("2016-09-07 12:13:14")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("invalid")]
    [InlineData("unspecified")]
    [InlineData("2016-9-07")]
    [InlineData("2016-09-07T12:13:14")]
    [InlineData("2016-09-07 12:13")]
    [InlineData("2016-13-01")]
    [InlineData("2016-02-30")]
    [InlineData("2016-0a-01")]
    [InlineData("2016-09-07 24:00:00")]
    [InlineData("2016-09-07 12:60:00")]
    [InlineData("2016-09-07 12:13:14.12")]
    [InlineData("2016-09-07 12:13:14,123")]
    [InlineData("2016-09-07 12:13:14.12x")]
    [InlineData("2016-09-07  12:13:14")]
    public void RejectsAnythingElse(string raw)
    {
        Assert.False(CcDateTime.TryParse(raw, out _));
    }

    [Theory]
    [InlineData("2020-08-19 07:50:35", "2020-08-19 07:50:35", 0)]
    [InlineData("2020-08-19 07:50:34", "2020-08-19 07:50:35", -1)]
    [InlineData("2020-08-19 07:50:36", "2020-08-19 07:50:35", 1)]
    [InlineData("2020-08-19 07:49:35", "2020-08-19 07:50:35", -1)]
    [InlineData("2020-08-19 08:50:35", "2020-08-19 07:50:35", 1)]
    [InlineData("2019-08-19 07:50:35", "2020-08-19 07:50:35", -1)]
    [InlineData("2020-07-19 07:50:35", "2020-08-19 07:50:35", -1)]
    [InlineData("2020-08-21 07:50:35", "2020-08-19 07:50:35", 1)]
    [InlineData("2020-08-19 07:50:35", "2020-08-19", 0)]
    [InlineData("2020-08-10 07:50:35", "2020-08-19", -1)]
    [InlineData("2020-08-30 07:50:35", "2020-08-19", 1)]
    [InlineData("2020-08-19 07:50:35", "UNSPECIFIED", 1)]
    [InlineData("UNSPECIFIED", "2020-08-19", -1)]
    [InlineData("UNSPECIFIED", "UNSPECIFIED", 0)]
    public void ComparesLikeJClipCorn(string a, string b, int expected)
    {
        Assert.Equal(expected, CcDateTime.Compare(P(a), P(b)));
    }

    [Theory]
    [InlineData("UNSPECIFIED", "UNSPECIFIED", false)]
    [InlineData("2020-08-19 07:50:35", "UNSPECIFIED", false)]
    [InlineData("UNSPECIFIED", "2020-08-19", false)]
    [InlineData("2020-08-19 07:50:35", "2020-08-19", true)]
    [InlineData("2020-08-19 07:50:35", "2020-08-19 07:50:35", true)]
    [InlineData("2020-08-19 07:50:35", "2020-08-19 07:50:36", false)]
    public void EqualityFollowsJClipCorn(string a, string b, bool expected)
    {
        Assert.Equal(expected, P(a).IsEqual(P(b)));
    }

    [Theory]
    [InlineData("2026-07-01 20:00:00", "2026-07-01T18:00:00")]
    [InlineData("2026-01-15 20:00:00", "2026-01-15T19:00:00")]
    [InlineData("2026-07-01", "2026-07-01T10:00:00")]
    [InlineData("2026-01-15", "2026-01-15T11:00:00")]
    // DST gap: 02:30 does not exist and moves forward by the gap to 03:30 CEST.
    [InlineData("2026-03-29 02:30:00", "2026-03-29T01:30:00")]
    // DST end: 02:30 happens twice and is taken as standard time (CET).
    [InlineData("2026-10-25 02:30:00", "2026-10-25T01:30:00")]
    public void ConvertsLocalTimeToUtc(string raw, string expectedUtc)
    {
        var utc = P(raw).ToUtc(Berlin, Noon);
        Assert.Equal(DateTime.Parse(expectedUtc, System.Globalization.CultureInfo.InvariantCulture), utc);
        Assert.Equal(DateTimeKind.Utc, utc!.Value.Kind);
    }

    [Fact]
    public void DateOnlyUsesTheConfiguredTimeOfDay()
    {
        var utc = P("2026-07-01").ToUtc(Berlin, new TimeOnly(23, 30));
        Assert.Equal(new DateTime(2026, 7, 1, 21, 30, 0, DateTimeKind.Utc), utc);
    }

    [Fact]
    public void UnspecifiedHasNoInstant()
    {
        Assert.Null(CcDateTime.Unspecified.ToUtc(Berlin, Noon));
    }

    [Theory]
    [InlineData("2026-10-26T20:15:09Z", "2026-10-26 21:15:09")]
    [InlineData("2026-07-01T18:00:00Z", "2026-07-01 20:00:00")]
    [InlineData("2026-12-31T23:30:00Z", "2027-01-01 00:30:00")]
    public void RendersUtcAsLocalEntry(string utc, string expected)
    {
        var instant = DateTime.Parse(utc, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal);
        var entry = CcDateTime.FromUtc(instant, Berlin);
        Assert.Equal(expected, entry.Raw);
        Assert.Equal(CcDateTimeKind.DateTime, entry.Kind);
    }

    [Fact]
    public void RenderedEntryDropsSubSecondPrecision()
    {
        var entry = CcDateTime.FromUtc(new DateTime(2026, 7, 1, 18, 0, 0, 999, DateTimeKind.Utc), Berlin);
        Assert.Equal("2026-07-01 20:00:00", entry.Raw);
    }
}
