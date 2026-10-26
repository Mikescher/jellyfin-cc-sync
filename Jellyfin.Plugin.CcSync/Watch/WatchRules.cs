using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.CcSync.Jellyfin;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;

namespace Jellyfin.Plugin.CcSync.Watch;

/// <summary>
/// What identified the watched item when the pending update was created; the item may be
/// re-identified before the write.
/// </summary>
public sealed record PendingMatchKeys(
    Dictionary<string, string> ProviderIds,
    string Name,
    int? Year,
    string? SeriesName = null,
    Dictionary<string, string>? SeriesProviderIds = null,
    int? Season = null,
    int? Episode = null)
{
    private static readonly string[] Keys = [JfItems.ClipCornProvider, JfItems.TmdbProvider, JfItems.ImdbProvider];

    private static readonly JsonSerializerOptions JsonOptions = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static PendingMatchKeys From(BaseItem item)
    {
        if (item is Episode episode)
        {
            return new PendingMatchKeys(
                Ids(item),
                item.Name ?? string.Empty,
                item.ProductionYear,
                episode.SeriesName,
                episode.Series is { } series ? Ids(series) : null,
                episode.ParentIndexNumber,
                episode.IndexNumber);
        }

        return new PendingMatchKeys(Ids(item), item.Name ?? string.Empty, item.ProductionYear);
    }

    public static PendingMatchKeys? Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<PendingMatchKeys>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    private static Dictionary<string, string> Ids(BaseItem item)
    {
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in Keys)
        {
            if (JfItems.ProviderId(item, key) is { } value)
            {
                ids[key] = value;
            }
        }

        return ids;
    }
}

/// <summary>The watch rules shared with jellyfin-watchnotify.</summary>
public static class WatchRules
{
    public static double Fraction(long? positionTicks, long? runTimeTicks)
        => runTimeTicks is > 0 ? (double)(positionTicks ?? 0) / runTimeTicks.Value : 0;

    public static bool IsWatched(bool playedToCompletion, long? positionTicks, long? runTimeTicks, double threshold)
        => playedToCompletion || Fraction(positionTicks, runTimeTicks) >= threshold;

    /// <summary>The start of a watch whose playback start was not seen (e.g. after a server restart).</summary>
    public static DateTime EstimateStart(DateTime endUtc, long? positionTicks)
        => positionTicks is > 0 ? endUtc - TimeSpan.FromTicks(positionTicks.Value) : endUtc;

    /// <summary>Pending updates carry whole seconds, like the jClipCorn entry they become.</summary>
    public static DateTime TruncateToSeconds(DateTime utc)
        => new(utc.Ticks - (utc.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);
}
