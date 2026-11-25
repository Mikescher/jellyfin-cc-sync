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

    private static CcViewedEntry N(string raw, string comment = "") => CcViewedEntry.Create(P(raw), comment);

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
    [InlineData("""[{"d":"UNSPECIFIED","c":""},{"d":"UNSPECIFIED","c":""},{"d":"2016-11-27 16:48:30","c":"Kino"},{"d":"2023-11-05 11:00:00","c":""}]""")]
    [InlineData("""[{"d":"2016-09-07","c":""},{"d":"2016-09-08 12:13:14","c":"Jellyfin"}]""")]
    [InlineData("""[{"d":"2016-09-08 12:13:14.123","c":""}]""")]
    [InlineData("""[{"d":"2016-09-07","c":"a \"b\" \\ <\/c> \u0001 ä"}]""")]
    [InlineData("""[{"c":"Kino","d":"2016-09-07"},{"d":"2016-09-08"},{ "d" : "2016-09-09" }]""")]
    [InlineData("""["UNSPECIFIED","UNSPECIFIED","2016-11-27 16:48:30","2023-11-05 11:00:00"]""")]
    [InlineData("""["2016-09-07",{"d":"2016-09-08 12:13:14","c":"Kino"},"2016-09-09"]""")]
    [InlineData("""["2016-09-08 12:13:14.123"]""")]
    public void RoundTripsByteIdentical(string json)
    {
        Assert.Equal(json, H(json).ToJson());
    }

    [Fact]
    public void ReadsOldStringElementsWithAnEmptyComment()
    {
        var history = H("""["UNSPECIFIED","2016-11-27 16:48:30"]""");
        Assert.Equal(2, history.Count);
        Assert.All(history.Entries, e => Assert.Equal(string.Empty, e.Comment));
        Assert.Equal("2016-11-27 16:48:30", history.Entries[1].Date.Raw);
        Assert.Equal("\"2016-11-27 16:48:30\"", history.Entries[1].Json);
    }

    [Fact]
    public void ReadsMixedArrays()
    {
        var history = H("""["2016-09-07",{"d":"2016-09-08 12:13:14","c":"Kino"},{"d":"UNSPECIFIED"}]""");
        Assert.Equal(["2016-09-07", "2016-09-08 12:13:14", "UNSPECIFIED"], history.Entries.Select(e => e.Date.Raw));
        Assert.Equal(["", "Kino", ""], history.Entries.Select(e => e.Comment));
    }

    [Fact]
    public void AnObjectWithoutCommentHasAnEmptyComment()
    {
        var entry = Assert.Single(H("""[{"d":"2016-09-08 12:13:14"}]""").Entries);
        Assert.Equal(string.Empty, entry.Comment);
        Assert.Equal(CcDateTimeKind.DateTime, entry.Date.Kind);
        Assert.Equal("""{"d":"2016-09-08 12:13:14"}""", entry.Json);
    }

    [Fact]
    public void ReadsEscapedComments()
    {
        var entry = Assert.Single(H("""[{"d":"2016-09-08","c":"a \"b\" \\ <\/c> \u0001 ä"}]""").Entries);
        Assert.Equal("a \"b\" \\ </c> \u0001 ä", entry.Comment);
    }

    [Theory]
    [InlineData("""["2016-09-07","invalid"]""")]
    [InlineData("""["2016-09-07" """)]
    [InlineData("""["2016-09-07",42]""")]
    [InlineData("[null]")]
    [InlineData("""[""]""")]
    [InlineData("""{"a":1}""")]
    [InlineData("\"2016-09-07\"")]
    [InlineData("""["2016-09-07",]""")]
    [InlineData("""[["2016-09-07"]]""")]
    [InlineData("[{}]")]
    [InlineData("""[{"c":"Kino"}]""")]
    [InlineData("""[{"d":null,"c":""}]""")]
    [InlineData("""[{"d":20160907,"c":""}]""")]
    [InlineData("""[{"d":"2016-09-07","c":null}]""")]
    [InlineData("""[{"d":"2016-09-07","c":1}]""")]
    [InlineData("""[{"d":"2016-09-07","c":["Kino"]}]""")]
    [InlineData("""[{"d":"2016-09-07","c":"","x":""}]""")]
    [InlineData("""[{"D":"2016-09-07","c":""}]""")]
    [InlineData("""[{"d":"2016-09-07","d":"2016-09-08","c":""}]""")]
    [InlineData("""[{"d":"2016-09-07","c":"a","c":"b"}]""")]
    [InlineData("""[{"d":"bad","c":""}]""")]
    [InlineData("""[{"d":"2016-09-07","c":""},"bad"]""")]
    public void RejectsListsThatDoNotParseCompletely(string json)
    {
        Assert.False(CcViewedHistory.TryParse(json, out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void RejectsTextThatCannotBeKeptVerbatim()
    {
        var loneSurrogate = "[{\"d\":\"2016-09-07\",\"c\":\"" + (char)0xD800 + "\"}]";
        Assert.False(CcViewedHistory.TryParse(loneSurrogate, out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void CountsUnspecifiedEntries()
    {
        var history = H("""[{"d":"UNSPECIFIED","c":""},{"d":"UNSPECIFIED","c":""},{"d":"2016-11-27 16:48:30","c":""}]""");
        Assert.Equal(3, history.Count);
        Assert.True(history.IsViewed);
    }

    [Fact]
    public void SkipsAnIdenticalDateTime()
    {
        var history = H("""[{"d":"2016-11-27 16:48:30","c":""}]""");
        var result = history.Add(N("2016-11-27 16:48:30"), out var added);
        Assert.False(added);
        Assert.Same(history, result);
    }

    [Fact]
    public void SkipsAnEqualDateWhateverTheComments()
    {
        var history = H("""[{"d":"2016-11-27 16:48:30","c":"Kino"}]""");
        history.Add(N("2016-11-27 16:48:30", "Jellyfin"), out var added);
        Assert.False(added);
    }

    [Fact]
    public void SkipsADateTimeOnADayWithADateOnlyEntry()
    {
        var history = H("""[{"d":"2016-11-27","c":""}]""");
        history.Add(N("2016-11-27 16:48:30"), out var added);
        Assert.False(added);
    }

    [Fact]
    public void SkipsADateOnlyEntryOnADayWithADateTime()
    {
        var history = H("""["2016-11-27 16:48:30"]""");
        history.Add(N("2016-11-27"), out var added);
        Assert.False(added);
    }

    [Fact]
    public void AllowsSeveralUnspecifiedEntries()
    {
        var history = H("""[{"d":"UNSPECIFIED","c":""}]""").Add(CcViewedEntry.Create(CcDateTime.Unspecified, ""), out var added);
        Assert.True(added);
        Assert.Equal("""[{"d":"UNSPECIFIED","c":""},{"d":"UNSPECIFIED","c":""}]""", history.ToJson());
    }

    [Fact]
    public void AddsAnotherTimeOnTheSameDay()
    {
        var history = H("""[{"d":"2016-11-27 16:48:30","c":""}]""").Add(N("2016-11-27 10:00:00", "Jellyfin"), out var added);
        Assert.True(added);
        Assert.Equal("""[{"d":"2016-11-27 10:00:00","c":"Jellyfin"},{"d":"2016-11-27 16:48:30","c":""}]""", history.ToJson());
    }

    [Theory]
    [InlineData("2015-01-01 00:00:00", """[{"d":"UNSPECIFIED","c":"x"},{"d":"2015-01-01 00:00:00","c":"Jellyfin"},"2016-11-27",{"d":"2023-11-05 11:00:00.500"}]""")]
    [InlineData("2020-02-02 20:20:20", """[{"d":"UNSPECIFIED","c":"x"},"2016-11-27",{"d":"2020-02-02 20:20:20","c":"Jellyfin"},{"d":"2023-11-05 11:00:00.500"}]""")]
    [InlineData("2026-10-26 21:15:00", """[{"d":"UNSPECIFIED","c":"x"},"2016-11-27",{"d":"2023-11-05 11:00:00.500"},{"d":"2026-10-26 21:15:00","c":"Jellyfin"}]""")]
    public void InsertsInJClipCornOrderKeepingExistingEntriesVerbatim(string entry, string expected)
    {
        var history = H("""[{"d":"UNSPECIFIED","c":"x"},"2016-11-27",{"d":"2023-11-05 11:00:00.500"}]""").Add(N(entry, "Jellyfin"), out var added);
        Assert.True(added);
        Assert.Equal(expected, history.ToJson());
    }

    [Fact]
    public void ANewEntryGoesBehindEqualDatesLikeAStableSort()
    {
        // Two UNSPECIFIED entries compare equal to each other; the new one lands behind the existing one.
        var history = H("""[{"d":"UNSPECIFIED","c":"first"}]""").Add(CcViewedEntry.Create(CcDateTime.Unspecified, "second"), out _);
        Assert.Equal(["first", "second"], history.Entries.Select(e => e.Comment));
    }

    [Fact]
    public void WritesCompactJsonForNonCompactInput()
    {
        var history = H("""[ {"d":"2016-11-27","c":""} ]""").Add(N("2017-01-01 10:00:00"), out _);
        Assert.Equal("""[{"d":"2016-11-27","c":""},{"d":"2017-01-01 10:00:00","c":""}]""", history.ToJson());
    }

    [Fact]
    public void SortsAnUnsortedListLikeJClipCornWhenAdding()
    {
        var history = H("""["2020-01-01",{"d":"UNSPECIFIED","c":""},"2010-01-01"]""").Add(N("2015-06-06 06:06:06"), out _);
        Assert.Equal("""[{"d":"UNSPECIFIED","c":""},"2010-01-01",{"d":"2015-06-06 06:06:06","c":""},"2020-01-01"]""", history.ToJson());
    }

    [Fact]
    public void LatestUtcIgnoresUnspecifiedAndUsesTheTimeOfDayForDates()
    {
        Assert.Null(H("[]").LatestUtc(Berlin, Noon));
        Assert.Null(H("""[{"d":"UNSPECIFIED","c":""},"UNSPECIFIED"]""").LatestUtc(Berlin, Noon));

        var history = H("""[{"d":"UNSPECIFIED","c":""},{"d":"2016-11-27 16:48:30","c":""},{"d":"2023-11-05","c":"Kino"}]""");
        Assert.Equal(new DateTime(2023, 11, 5, 11, 0, 0, DateTimeKind.Utc), history.LatestUtc(Berlin, Noon));
    }

    [Fact]
    public void LatestUtcComparesInstantsNotListPositions()
    {
        // Same day: the date-only entry at 12:00 local is later than the 09:00 entry.
        var history = H("""[{"d":"2023-11-05 09:00:00","c":""},{"d":"2023-11-05","c":""}]""");
        Assert.Equal(new DateTime(2023, 11, 5, 11, 0, 0, DateTimeKind.Utc), history.LatestUtc(Berlin, Noon));
    }

    [Fact]
    public void CreatesTheObjectFormWithKeysInJClipCornsOrder()
    {
        Assert.Equal("""{"d":"2026-10-26 21:15:00","c":"Jellyfin"}""", N("2026-10-26 21:15:00", "Jellyfin").Json);
        Assert.Equal("""{"d":"2026-10-26 21:15:00","c":""}""", N("2026-10-26 21:15:00").Json);
        Assert.Equal("""{"d":"UNSPECIFIED","c":""}""", CcViewedEntry.Create(CcDateTime.Unspecified, "").Json);
    }

    [Fact]
    public void EscapesTheCommentLikeOrgJson()
    {
        var entry = N("2026-10-26 21:15:00", "Er sagte \"hi\" \\ </b> a/b \u0001\tä");
        Assert.Equal("""{"d":"2026-10-26 21:15:00","c":"Er sagte \"hi\" \\ <\/b> a/b \u0001\tä"}""", entry.Json);

        var parsed = Assert.Single(H("[" + entry.Json + "]").Entries);
        Assert.Equal("Er sagte \"hi\" \\ </b> a/b \u0001\tä", parsed.Comment);
    }

    [Theory]
    [InlineData("", "\"\"")]
    [InlineData("Kino", "\"Kino\"")]
    [InlineData("\"", "\"\\\"\"")]
    [InlineData("\\", "\"\\\\\"")]
    [InlineData("</", "\"<\\/\"")]
    [InlineData("/", "\"/\"")]
    [InlineData("a/", "\"a/\"")]
    [InlineData("</</a", "\"<\\/<\\/a\"")]
    [InlineData("\b\t\n\f\r", "\"\\b\\t\\n\\f\\r\"")]
    [InlineData("\u0000\u001f", "\"\\u0000\\u001f\"")]
    [InlineData("\u007f", "\"\u007f\"")]
    [InlineData("\u0080\u009f", "\"\\u0080\\u009f\"")]
    [InlineData("\u00a0\u00e4\u00ff", "\"\u00a0\u00e4\u00ff\"")]
    [InlineData("\u1fff\u2000\u2028\u20ac\u20ff\u2100", "\"\u1fff\\u2000\\u2028\\u20ac\\u20ff\u2100\"")]
    [InlineData("\ud83d\ude00", "\"\ud83d\ude00\"")]
    public void QuotesLikeOrgJson(string value, string expected)
    {
        Assert.Equal(expected, CcJson.Quote(value));
    }
}
