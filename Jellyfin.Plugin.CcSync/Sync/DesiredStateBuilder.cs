using Jellyfin.Plugin.CcSync.ClipCorn.Model;
using Jellyfin.Plugin.CcSync.Configuration;

namespace Jellyfin.Plugin.CcSync.Sync;

public sealed record SyncOptions(
    bool SyncPlayed,
    bool SyncPlayCount,
    bool SyncLastPlayed,
    bool SyncFavorites,
    IReadOnlySet<int> FavoriteScores,
    bool RatingTags,
    bool GroupTags,
    bool GroupTagsIncludeParents,
    IReadOnlySet<CcKind> RatingKinds,
    TimeZoneInfo Zone,
    TimeOnly DateOnlyTime)
{
    public static SyncOptions From(PluginConfiguration config, TimeZoneInfo zone, TimeOnly dateOnlyTime)
    {
        var kinds = new HashSet<CcKind>();
        if (config.ApplyRatingsToMovies)
        {
            kinds.Add(CcKind.Movie);
        }

        if (config.ApplyRatingsToSeries)
        {
            kinds.Add(CcKind.Series);
        }

        if (config.ApplyRatingsToSeasons)
        {
            kinds.Add(CcKind.Season);
        }

        if (config.ApplyRatingsToEpisodes)
        {
            kinds.Add(CcKind.Episode);
        }

        return new SyncOptions(
            config.SyncPlayed,
            config.SyncPlayCount,
            config.SyncLastPlayed,
            config.SyncFavorites,
            config.FavoriteScores.ToHashSet(),
            config.RatingTagsEnabled,
            config.GroupTagsEnabled,
            config.GroupTagsIncludeParents,
            kinds,
            zone,
            dateOnlyTime);
    }
}

/// <summary>A not-yet-written Jellyfin watch, overlaid on jClipCorn's history.</summary>
public sealed record PendingOverlay(Guid JfItemId, string? CcId, DateTime WatchedAtUtc);

/// <summary>
/// What one Jellyfin item should look like. A null field is not owned by this run and stays as it
/// is; <see cref="Tags"/> null leaves the item's <c>jcc-*</c> tags alone.
/// </summary>
public sealed record DesiredItem(
    JfItemInfo Item,
    CcElement? Element,
    bool? Played,
    int? PlayCount,
    bool OwnsLastPlayed,
    DateTime? LastPlayed,
    bool? Favorite,
    IReadOnlySet<string>? Tags);

/// <summary>
/// Desired Jellyfin state from jClipCorn plus pending updates (<c>docs/sync-semantics.md</c> §
/// Viewed state, § Favourites and tags).
/// </summary>
public static class DesiredStateBuilder
{
    public static IReadOnlyList<DesiredItem> Build(
        CcLibrary library,
        MatchResult match,
        IReadOnlyList<JfItemInfo> items,
        IReadOnlyList<PendingOverlay> pending,
        SyncOptions options)
    {
        var byItem = match.Matched.ToDictionary(m => m.Item.Id);
        var overlay = BuildOverlay(library, byItem, pending, options.Zone);
        var empty = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var result = new List<DesiredItem>(items.Count);
        foreach (var item in items)
        {
            if (!byItem.TryGetValue(item.Id, out var matched))
            {
                result.Add(new DesiredItem(item, null, null, null, false, null, null, empty));
                continue;
            }

            var element = matched.Element;
            if (element.Problem is not null)
            {
                result.Add(new DesiredItem(item, element, null, null, false, null, null, null));
                continue;
            }

            bool? played = null;
            int? playCount = null;
            DateTime? lastPlayed = null;
            var ownsLastPlayed = false;

            var history = element switch
            {
                CcMovie movie => movie.History,
                CcEpisode episode => episode.History,
                _ => null,
            };

            if (history is not null)
            {
                foreach (var entry in overlay.GetValueOrDefault(element.Id) ?? [])
                {
                    history = history.Add(entry, out _);
                }

                played = options.SyncPlayed ? history.IsViewed : null;
                playCount = options.SyncPlayCount ? history.Count : null;
                ownsLastPlayed = options.SyncLastPlayed;
                lastPlayed = options.SyncLastPlayed ? history.LatestUtc(options.Zone, options.DateOnlyTime) : null;
            }

            var rated = options.RatingKinds.Contains(element.Kind);
            bool? favorite = options.SyncFavorites && rated ? options.FavoriteScores.Contains(element.Score.Stored) : null;

            result.Add(new DesiredItem(item, element, played, playCount, ownsLastPlayed, lastPlayed, favorite, Tags(library, element, rated, options)));
        }

        return result;
    }

    private static HashSet<string> Tags(CcLibrary library, CcElement element, bool rated, SyncOptions options)
    {
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (options.RatingTags && rated && TagSlug.RatingTag(element.Score) is { } rating)
        {
            tags.Add(rating);
        }

        var groups = element switch
        {
            CcMovie movie => movie.Groups,
            CcSeries series => series.Groups,
            _ => [],
        };

        if (options.GroupTags)
        {
            foreach (var group in groups)
            {
                var names = options.GroupTagsIncludeParents ? library.AncestorsOf(group).Prepend(group) : [group];
                foreach (var name in names)
                {
                    if (TagSlug.GroupTag(name) is { } tag)
                    {
                        tags.Add(tag);
                    }
                }
            }
        }

        return tags;
    }

    /// <summary>
    /// Pending entries per element. The element is found through the Jellyfin item's match, or the
    /// clipcorn id stored on the pending update when the item is not in the index.
    /// </summary>
    private static Dictionary<string, List<CcDateTime>> BuildOverlay(
        CcLibrary library,
        Dictionary<Guid, MatchedItem> byItem,
        IReadOnlyList<PendingOverlay> pending,
        TimeZoneInfo zone)
    {
        var overlay = new Dictionary<string, List<CcDateTime>>(StringComparer.OrdinalIgnoreCase);
        foreach (var update in pending)
        {
            var element = byItem.TryGetValue(update.JfItemId, out var matched)
                ? matched.Element
                : update.CcId is { } ccId ? library.Find(ccId) : null;

            if (element is not (CcMovie or CcEpisode))
            {
                continue;
            }

            if (!overlay.TryGetValue(element.Id, out var entries))
            {
                overlay[element.Id] = entries = [];
            }

            entries.Add(CcDateTime.FromUtc(update.WatchedAtUtc, zone));
        }

        return overlay;
    }
}
