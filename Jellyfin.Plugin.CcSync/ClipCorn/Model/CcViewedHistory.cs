using System.Text;
using System.Text.Json;

namespace Jellyfin.Plugin.CcSync.ClipCorn.Model;

/// <summary>
/// A <c>VIEWED_HISTORY</c> list (<c>columnTypes/CCDateTimeList.java</c>). Kept in the stored
/// order; <see cref="Add"/> re-sorts the way jClipCorn's constructor does.
/// </summary>
public sealed class CcViewedHistory
{
    private CcViewedHistory(IReadOnlyList<CcDateTime> entries)
    {
        Entries = entries;
    }

    public static CcViewedHistory Empty { get; } = new([]);

    public IReadOnlyList<CcDateTime> Entries { get; }

    /// <summary>Gets the number of viewings; unspecified entries count.</summary>
    public int Count => Entries.Count;

    public bool IsViewed => Entries.Count > 0;

    /// <summary>
    /// Parses a stored list. A single bad entry makes the whole list unreadable for jClipCorn, so a
    /// list that fails here must never be rewritten.
    /// </summary>
    public static bool TryParse(string? json, out CcViewedHistory history, out string? error)
    {
        history = Empty;
        error = null;

        if (string.IsNullOrWhiteSpace(json) || json == "[]")
        {
            return true;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                error = "not a JSON array";
                return false;
            }

            var entries = new List<CcDateTime>();
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.String)
                {
                    error = $"entry {element.GetRawText()} is not a string";
                    return false;
                }

                var text = element.GetString()!;
                if (!CcDateTime.TryParse(text, out var entry))
                {
                    error = $"entry \"{text}\" is not a jClipCorn date";
                    return false;
                }

                entries.Add(entry);
            }

            history = new CcViewedHistory(entries);
            return true;
        }
        catch (JsonException ex)
        {
            error = "invalid JSON: " + ex.Message;
            return false;
        }
    }

    public static CcViewedHistory Create(IEnumerable<CcDateTime> entries) => new(StableSort(entries.ToList()));

    /// <summary>jClipCorn's <c>contains</c>: any entry <see cref="CcDateTime.IsEqual"/> to the value.</summary>
    public bool Contains(CcDateTime value) => Entries.Any(e => e.IsEqual(value));

    /// <summary>
    /// jClipCorn's <c>add()</c>: an entry equal to an existing one is skipped, otherwise it is added
    /// and the list sorted again.
    /// </summary>
    public CcViewedHistory Add(CcDateTime value, out bool added)
    {
        added = !Contains(value);
        if (!added)
        {
            return this;
        }

        var list = Entries.ToList();
        list.Add(value);
        return new CcViewedHistory(StableSort(list));
    }

    /// <summary>
    /// Gets the latest dated entry as UTC, or null when the list has none.
    /// </summary>
    public DateTime? LatestUtc(TimeZoneInfo zone, TimeOnly dateOnlyTimeOfDay)
    {
        DateTime? latest = null;
        foreach (var entry in Entries)
        {
            var instant = entry.ToUtc(zone, dateOnlyTimeOfDay);
            if (instant is not null && (latest is null || instant > latest))
            {
                latest = instant;
            }
        }

        return latest;
    }

    /// <summary>
    /// Serialises compactly like jClipCorn's <c>JSONArray.toString()</c>, every entry in its stored text.
    /// </summary>
    public string ToJson()
    {
        var builder = new StringBuilder("[");
        for (var i = 0; i < Entries.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            // Parsed entries consist of digits, '-', ':', '.', ' ' or UNSPECIFIED: nothing to escape.
            builder.Append('"').Append(Entries[i].Raw).Append('"');
        }

        return builder.Append(']').ToString();
    }

    public override string ToString() => ToJson();

    /// <summary>
    /// Stable binary insertion sort with jClipCorn's comparator. For a list jClipCorn wrote (already
    /// sorted) it equals Java's TimSort: existing entries keep their order, a new one goes behind
    /// every entry that does not compare greater.
    /// </summary>
    private static List<CcDateTime> StableSort(List<CcDateTime> list)
    {
        for (var i = 1; i < list.Count; i++)
        {
            var pivot = list[i];
            int left = 0, right = i;
            while (left < right)
            {
                var mid = (left + right) >>> 1;
                if (CcDateTime.Compare(pivot, list[mid]) < 0)
                {
                    right = mid;
                }
                else
                {
                    left = mid + 1;
                }
            }

            list.RemoveAt(i);
            list.Insert(left, pivot);
        }

        return list;
    }
}
