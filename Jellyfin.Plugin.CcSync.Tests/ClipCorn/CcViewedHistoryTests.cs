using Jellyfin.Plugin.CcSync.ClipCorn.Model;

namespace Jellyfin.Plugin.CcSync.Tests.ClipCorn;

public class CcViewedHistoryTests
{
    private static readonly TimeZoneInfo Berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
    private static readonly TimeOnly Noon = new(12, 0);

    private static CcViewedHistory H(string json)
    {
        Assert.True(CcViewedHistory.TryParse(json, out var history, out var error), error);
        return history;
    }

    private static CcDateTime P(string raw)
    {
        Assert.True(CcDateTime.TryParse(raw, out var value), raw);
        return value;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("[]")]
    [InlineData("[ ]")]
    public void ReadsEmptyForms(string? json)
    {
        var history = H(json!);
        Assert.Equal(0, history.Count);
        Assert.False(history.IsViewed);
        Assert.Equal("[]", history.ToJson());
    }

    [Theory]
    [InlineData("[\"UNSPECIFIED\",\"UNSPECIFIED\",\"2016-11-27 16:48:30\",\"2023-11-05 11:00:00\"]")]
    [InlineData("[\"2016-09-07\",\"2016-09-08 12:13:14\"]")]
    [InlineData("[\"2016-09-08 12:13:14.123\"]")]
    public void RoundTripsByteIdentical(string json)
    {
        Assert.Equal(json, H(json).ToJson());
    }

    [Theory]
    [InlineData("[\"2016-09-07\",\"invalid\"]")]
    [InlineData("[\"2016-09-07\"")]
    [InlineData("[\"2016-09-07\",42]")]
    [InlineData("[null]")]
    [InlineData("[\"\"]")]
    [InlineData("{\"a\":1}")]
    [InlineData("\"2016-09-07\"")]
    [InlineData("[\"2016-09-07\",]")]
    public void RejectsListsThatDoNotParseCompletely(string json)
    {
        Assert.False(CcViewedHistory.TryParse(json, out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void CountsUnspecifiedEntries()
    {
        var history = H("[\"UNSPECIFIED\",\"UNSPECIFIED\",\"2016-11-27 16:48:30\"]");
        Assert.Equal(3, history.Count);
        Assert.True(history.IsViewed);
    }

    [Fact]
    public void SkipsAnIdenticalDateTime()
    {
        var history = H("[\"2016-11-27 16:48:30\"]");
        var result = history.Add(P("2016-11-27 16:48:30"), out var added);
        Assert.False(added);
        Assert.Same(history, result);
    }

    [Fact]
    public void SkipsADateTimeOnADayWithADateOnlyEntry()
    {
        var history = H("[\"2016-11-27\"]");
        history.Add(P("2016-11-27 16:48:30"), out var added);
        Assert.False(added);
    }

    [Fact]
    public void SkipsADateOnlyEntryOnADayWithADateTime()
    {
        var history = H("[\"2016-11-27 16:48:30\"]");
        history.Add(P("2016-11-27"), out var added);
        Assert.False(added);
    }

    [Fact]
    public void AllowsSeveralUnspecifiedEntries()
    {
        var history = H("[\"UNSPECIFIED\"]").Add(CcDateTime.Unspecified, out var added);
        Assert.True(added);
        Assert.Equal("[\"UNSPECIFIED\",\"UNSPECIFIED\"]", history.ToJson());
    }

    [Fact]
    public void AddsAnotherTimeOnTheSameDay()
    {
        var history = H("[\"2016-11-27 16:48:30\"]").Add(P("2016-11-27 10:00:00"), out var added);
        Assert.True(added);
        Assert.Equal("[\"2016-11-27 10:00:00\",\"2016-11-27 16:48:30\"]", history.ToJson());
    }

    [Theory]
    [InlineData("2015-01-01 00:00:00", "[\"UNSPECIFIED\",\"2015-01-01 00:00:00\",\"2016-11-27\",\"2023-11-05 11:00:00.500\"]")]
    [InlineData("2020-02-02 20:20:20", "[\"UNSPECIFIED\",\"2016-11-27\",\"2020-02-02 20:20:20\",\"2023-11-05 11:00:00.500\"]")]
    [InlineData("2026-10-26 21:15:00", "[\"UNSPECIFIED\",\"2016-11-27\",\"2023-11-05 11:00:00.500\",\"2026-10-26 21:15:00\"]")]
    public void InsertsInJClipCornOrderKeepingExistingEntriesVerbatim(string entry, string expected)
    {
        var history = H("[\"UNSPECIFIED\",\"2016-11-27\",\"2023-11-05 11:00:00.500\"]").Add(P(entry), out var added);
        Assert.True(added);
        Assert.Equal(expected, history.ToJson());
    }

    [Fact]
    public void WritesCompactJsonForNonCompactInput()
    {
        var history = H("[ \"2016-11-27\" ]").Add(P("2017-01-01 10:00:00"), out _);
        Assert.Equal("[\"2016-11-27\",\"2017-01-01 10:00:00\"]", history.ToJson());
    }

    [Fact]
    public void SortsAnUnsortedListLikeJClipCornWhenAdding()
    {
        var history = H("[\"2020-01-01\",\"UNSPECIFIED\",\"2010-01-01\"]").Add(P("2015-06-06 06:06:06"), out _);
        Assert.Equal("[\"UNSPECIFIED\",\"2010-01-01\",\"2015-06-06 06:06:06\",\"2020-01-01\"]", history.ToJson());
    }

    [Fact]
    public void LatestUtcIgnoresUnspecifiedAndUsesTheTimeOfDayForDates()
    {
        Assert.Null(H("[]").LatestUtc(Berlin, Noon));
        Assert.Null(H("[\"UNSPECIFIED\",\"UNSPECIFIED\"]").LatestUtc(Berlin, Noon));

        var history = H("[\"UNSPECIFIED\",\"2016-11-27 16:48:30\",\"2023-11-05\"]");
        Assert.Equal(new DateTime(2023, 11, 5, 11, 0, 0, DateTimeKind.Utc), history.LatestUtc(Berlin, Noon));
    }

    [Fact]
    public void LatestUtcComparesInstantsNotListPositions()
    {
        // Same day: the date-only entry at 12:00 local is later than the 09:00 entry.
        var history = H("[\"2023-11-05 09:00:00\",\"2023-11-05\"]");
        Assert.Equal(new DateTime(2023, 11, 5, 11, 0, 0, DateTimeKind.Utc), history.LatestUtc(Berlin, Noon));
    }
}
