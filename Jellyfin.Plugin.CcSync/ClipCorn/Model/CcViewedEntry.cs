using System.Text.Json;

namespace Jellyfin.Plugin.CcSync.ClipCorn.Model;

/// <summary>
/// One <c>VIEWED_HISTORY</c> element: <c>{"d":"…","c":"…"}</c>, or a plain date string as older
/// databases store it (comment empty). Only <see cref="Date"/> takes part in order and equality.
/// </summary>
public sealed class CcViewedEntry
{
    private CcViewedEntry(CcDateTime date, string comment, string json)
    {
        Date = date;
        Comment = comment;
        Json = json;
    }

    public CcDateTime Date { get; }

    public string Comment { get; }

    /// <summary>Gets the element's JSON text exactly as stored, kept so rewritten lists leave it byte-identical.</summary>
    public string Json { get; }

    /// <summary>A new element in the form jClipCorn writes: <c>{"d":…,"c":…}</c>, keys in that order.</summary>
    public static CcViewedEntry Create(CcDateTime date, string comment)
        => new(date, comment, "{\"d\":" + CcJson.Quote(date.Raw) + ",\"c\":" + CcJson.Quote(comment) + "}");

    /// <summary>
    /// Parses one array element: a date string, or an object with a string <c>d</c> and an optional
    /// string <c>c</c> and nothing else.
    /// </summary>
    public static bool TryParse(JsonElement element, out CcViewedEntry entry, out string? error)
    {
        entry = null!;
        error = null;
        string? dateText = null;
        var comment = string.Empty;

        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                dateText = element.GetString()!;
                break;

            case JsonValueKind.Object:
                var hasComment = false;
                foreach (var property in element.EnumerateObject())
                {
                    var isDate = property.NameEquals("d");
                    if (!isDate && !property.NameEquals("c"))
                    {
                        error = $"entry {element.GetRawText()} has an unknown key \"{property.Name}\"";
                        return false;
                    }

                    if ((isDate && dateText is not null) || (!isDate && hasComment))
                    {
                        error = $"entry {element.GetRawText()} has the key \"{property.Name}\" twice";
                        return false;
                    }

                    if (property.Value.ValueKind != JsonValueKind.String)
                    {
                        error = $"entry {element.GetRawText()} has a non-string \"{property.Name}\"";
                        return false;
                    }

                    if (isDate)
                    {
                        dateText = property.Value.GetString()!;
                    }
                    else
                    {
                        comment = property.Value.GetString()!;
                        hasComment = true;
                    }
                }

                if (dateText is null)
                {
                    error = $"entry {element.GetRawText()} has no \"d\"";
                    return false;
                }

                break;

            default:
                error = $"entry {element.GetRawText()} is neither a string nor an object";
                return false;
        }

        if (!CcDateTime.TryParse(dateText, out var date))
        {
            error = $"entry \"{dateText}\" is not a jClipCorn date";
            return false;
        }

        entry = new CcViewedEntry(date, comment, element.GetRawText());
        return true;
    }

    public override string ToString() => Json;
}
