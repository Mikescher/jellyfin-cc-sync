using System.Globalization;

namespace Jellyfin.Plugin.CcSync.ClipCorn.Model;

public enum CcDateTimeKind
{
    Unspecified,
    DateOnly,
    DateTime,
}

/// <summary>
/// One viewed-history entry with jClipCorn's comparison semantics
/// (<c>util/datetime/CCDateTime.java</c>). The comparison is not a total order: a date-only
/// entry compares equal to every datetime on the same day.
/// </summary>
public sealed class CcDateTime
{
    public const string UnspecifiedText = "UNSPECIFIED";

    public const string WriteFormat = "yyyy-MM-dd HH:mm:ss";

    private CcDateTime(CcDateTimeKind kind, DateOnly date, TimeOnly time, string raw)
    {
        Kind = kind;
        Date = date;
        Time = time;
        Raw = raw;
    }

    public CcDateTimeKind Kind { get; }

    /// <summary>Gets the date; meaningless for <see cref="CcDateTimeKind.Unspecified"/>.</summary>
    public DateOnly Date { get; }

    /// <summary>Gets the time to the second; meaningful only for <see cref="CcDateTimeKind.DateTime"/>.</summary>
    public TimeOnly Time { get; }

    /// <summary>Gets the entry exactly as stored, kept so rewritten lists leave it byte-identical.</summary>
    public string Raw { get; }

    public bool IsUnspecified => Kind == CcDateTimeKind.Unspecified;

    public static CcDateTime Unspecified { get; } = new(CcDateTimeKind.Unspecified, default, default, UnspecifiedText);

    /// <summary>
    /// Parses an entry. Stricter than jClipCorn, which neither checks digits nor ranges: anything
    /// rejected here is reported instead of guessed at.
    /// </summary>
    public static bool TryParse(string raw, out CcDateTime value)
    {
        value = Unspecified;

        if (raw == UnspecifiedText)
        {
            return true;
        }

        if (raw.Length != 10 && raw.Length != 19 && raw.Length != 23)
        {
            return false;
        }

        if (!TryParseDate(raw, out var date))
        {
            return false;
        }

        if (raw.Length == 10)
        {
            value = new CcDateTime(CcDateTimeKind.DateOnly, date, default, raw);
            return true;
        }

        if (raw[10] != ' ' || !TryParseTime(raw.AsSpan(11), out var time))
        {
            return false;
        }

        // The millisecond variant is accepted on read; jClipCorn drops the fraction on its next save.
        if (raw.Length == 23 && (raw[19] != '.' || !AllDigits(raw.AsSpan(20, 3))))
        {
            return false;
        }

        value = new CcDateTime(CcDateTimeKind.DateTime, date, time, raw);
        return true;
    }

    public static CcDateTime FromLocal(DateTime local)
    {
        var raw = local.ToString(WriteFormat, CultureInfo.InvariantCulture);
        return new CcDateTime(
            CcDateTimeKind.DateTime,
            DateOnly.FromDateTime(local),
            new TimeOnly(local.Hour, local.Minute, local.Second),
            raw);
    }

    /// <summary>
    /// Renders a UTC instant as the entry jClipCorn would store for it in <paramref name="zone"/>.
    /// </summary>
    public static CcDateTime FromUtc(DateTime utc, TimeZoneInfo zone)
        => FromLocal(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone));

    /// <summary>
    /// jClipCorn's <c>compareTo</c>: unspecified entries first, then by date; the time only counts
    /// when both entries have one.
    /// </summary>
    public static int Compare(CcDateTime a, CcDateTime b)
    {
        if (a.IsUnspecified && b.IsUnspecified)
        {
            return 0;
        }

        if (a.IsUnspecified)
        {
            return -1;
        }

        if (b.IsUnspecified)
        {
            return 1;
        }

        var byDate = a.Date.CompareTo(b.Date);
        if (byDate != 0)
        {
            return Math.Sign(byDate);
        }

        if (a.Kind == CcDateTimeKind.DateOnly || b.Kind == CcDateTimeKind.DateOnly)
        {
            return 0;
        }

        return Math.Sign(a.Time.CompareTo(b.Time));
    }

    /// <summary>
    /// jClipCorn's <c>isEqual</c>, used by <c>add()</c>: unspecified entries are never equal to
    /// anything, not even to each other.
    /// </summary>
    public bool IsEqual(CcDateTime other)
        => !IsUnspecified && !other.IsUnspecified && Compare(this, other) == 0;

    /// <summary>
    /// The instant this entry stands for, or null for <see cref="CcDateTimeKind.Unspecified"/>.
    /// A local time inside a DST gap is moved forward by the gap; an ambiguous one is taken as
    /// standard time.
    /// </summary>
    public DateTime? ToUtc(TimeZoneInfo zone, TimeOnly dateOnlyTimeOfDay)
    {
        if (IsUnspecified)
        {
            return null;
        }

        var time = Kind == CcDateTimeKind.DateOnly ? dateOnlyTimeOfDay : Time;
        var local = DateTime.SpecifyKind(Date.ToDateTime(time), DateTimeKind.Unspecified);
        return LocalToUtc(local, zone);
    }

    public static DateTime LocalToUtc(DateTime local, TimeZoneInfo zone)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);

        if (zone.IsInvalidTime(local))
        {
            // The offset in force before the gap maps the local time to the instant one gap later.
            var before = local;
            do
            {
                before = before.AddMinutes(-15);
            }
            while (zone.IsInvalidTime(before));

            return DateTime.SpecifyKind(local - zone.GetUtcOffset(before), DateTimeKind.Utc);
        }

        // ConvertTimeToUtc takes an ambiguous time as standard time.
        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }

    public override string ToString() => Raw;

    private static bool TryParseDate(string raw, out DateOnly date)
    {
        date = default;
        if (raw[4] != '-' || raw[7] != '-'
            || !AllDigits(raw.AsSpan(0, 4)) || !AllDigits(raw.AsSpan(5, 2)) || !AllDigits(raw.AsSpan(8, 2)))
        {
            return false;
        }

        var year = int.Parse(raw.AsSpan(0, 4), CultureInfo.InvariantCulture);
        var month = int.Parse(raw.AsSpan(5, 2), CultureInfo.InvariantCulture);
        var day = int.Parse(raw.AsSpan(8, 2), CultureInfo.InvariantCulture);

        if (year < 1 || month < 1 || month > 12 || day < 1 || day > DateTime.DaysInMonth(year, month))
        {
            return false;
        }

        date = new DateOnly(year, month, day);
        return true;
    }

    private static bool TryParseTime(ReadOnlySpan<char> s, out TimeOnly time)
    {
        time = default;
        if (s.Length < 8 || s[2] != ':' || s[5] != ':'
            || !AllDigits(s[..2]) || !AllDigits(s.Slice(3, 2)) || !AllDigits(s.Slice(6, 2)))
        {
            return false;
        }

        var hour = int.Parse(s[..2], CultureInfo.InvariantCulture);
        var minute = int.Parse(s.Slice(3, 2), CultureInfo.InvariantCulture);
        var second = int.Parse(s.Slice(6, 2), CultureInfo.InvariantCulture);

        if (hour > 23 || minute > 59 || second > 59)
        {
            return false;
        }

        time = new TimeOnly(hour, minute, second);
        return true;
    }

    private static bool AllDigits(ReadOnlySpan<char> s)
    {
        foreach (var c in s)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }
}
