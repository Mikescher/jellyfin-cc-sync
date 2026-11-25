using System.Text;
using System.Text.Json;

namespace Jellyfin.Plugin.CcSync.ClipCorn.Model;

/// <summary>
/// A <c>VIEWED_HISTORY</c> list (<c>columnTypes/CCDateTimeList.java</c>). Kept in the stored
/// order; <see cref="Add"/> re-sorts the way jClipCorn's constructor does, by date only.
/// </summary>
public sealed class CcViewedHistory
{
    private CcViewedHistory(IReadOnlyList<CcViewedEntry> entries)
    {
        Entries = entries;
    }

    public static CcViewedHistory Empty { get; } = new([]);

    public IReadOnlyList<CcViewedEntry> Entries { get; }

    /// <summary>Gets the number of viewings; unspecified entries count.</summary>
    public int Count => Entries.Count;

    public bool IsViewed => Entries.Count > 0;

    /// <summary>
    /// Parses a stored list; elements may be in object or plain string form, mixed. A single bad
    /// entry makes the whole list unreadable for jClipCorn, so a list that fails here must never be
    /// rewritten.
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

            var entries = new List<CcViewedEntry>();
            var position = 0;
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (!CcViewedEntry.TryParse(element, out var entry, out error))
                {
                    return false;
                }

                // GetRawText is decoded again from the UTF-8 copy JsonDocument parses; ToJson relies
                // on it being the exact stored text.
                position = json.IndexOf(entry.Json, position, StringComparison.Ordinal);
                if (position < 0)
                {
                    error = $"entry {entry.Json} cannot be kept verbatim";
                    return false;
                }

                position += entry.Json.Length;
                entries.Add(entry);
            }

            history = new CcViewedHistory(entries);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            error = "invalid JSON: " + ex.Message;
            return false;
        }
    }

    public static CcViewedHistory Create(IEnumerable<CcViewedEntry> entries) => new(StableSort(entries.ToList()));

    /// <summary>jClipCorn's <c>contains</c>: any entry whose date <see cref="CcDateTime.IsEqual"/> the value.</summary>
    public bool Contains(CcDateTime value) => Entries.Any(e => e.Date.IsEqual(value));

    /// <summary>
    /// jClipCorn's <c>add()</c>: an entry whose date equals an existing one is skipped, whatever the
    /// comments; otherwise it is added and the list sorted again.
    /// </summary>
    public CcViewedHistory Add(CcViewedEntry value, out bool added)
    {
        added = !Contains(value.Date);
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
            var instant = entry.Date.ToUtc(zone, dateOnlyTimeOfDay);
            if (instant is not null && (latest is null || instant > latest))
            {
                latest = instant;
            }
        }

        return latest;
    }

    /// <summary>
    /// Serialises compactly like jClipCorn's <c>JSONStringer</c>, every entry in its stored text.
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

            builder.Append(Entries[i].Json);
        }

        return builder.Append(']').ToString();
    }

    public override string ToString() => ToJson();

    /// <summary>
    /// Stable binary insertion sort with jClipCorn's comparator on the dates. For a list jClipCorn
    /// wrote (already sorted) it equals Java's TimSort: existing entries keep their order, a new one
    /// goes behind every entry that does not compare greater.
    /// </summary>
    private static List<CcViewedEntry> StableSort(List<CcViewedEntry> list)
    {
        for (var i = 1; i < list.Count; i++)
        {
            var pivot = list[i];
            int left = 0, right = i;
            while (left < right)
            {
                var mid = (left + right) >>> 1;
                if (CcDateTime.Compare(pivot.Date, list[mid].Date) < 0)
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
