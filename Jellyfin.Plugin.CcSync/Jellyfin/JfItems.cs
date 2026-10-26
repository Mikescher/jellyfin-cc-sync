using System.Globalization;
using Jellyfin.Plugin.CcSync.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.CcSync.Jellyfin;

/// <summary>Small helpers around Jellyfin items and the configured scope.</summary>
public static class JfItems
{
    // ProviderIds of items loaded from the database is a case-sensitive dictionary.
    public const string ClipCornProvider = "clipcorn";
    public const string TmdbProvider = "Tmdb";
    public const string ImdbProvider = "Imdb";

    public static string? ProviderId(IHasProviderIds item, string key)
    {
        if (item.ProviderIds is null)
        {
            return null;
        }

        if (item.ProviderIds.TryGetValue(key, out var exact) && !string.IsNullOrWhiteSpace(exact))
        {
            return exact.Trim();
        }

        foreach (var (name, value) in item.ProviderIds)
        {
            if (string.Equals(name, key, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    public static string? Kind(BaseItem item) => item switch
    {
        Movie => "Movie",
        Series => "Series",
        Season => "Season",
        Episode => "Episode",
        _ => null,
    };

    public static string DisplayName(BaseItem item) => item switch
    {
        Episode episode => string.Format(
            CultureInfo.InvariantCulture,
            "{0} S{1:00}E{2:00} {3}",
            episode.SeriesName,
            episode.ParentIndexNumber ?? 0,
            episode.IndexNumber ?? 0,
            episode.Name).Trim(),
        Season season => $"{season.SeriesName} – {season.Name}".Trim(' ', '–'),
        _ when item.ProductionYear is > 0 => $"{item.Name} ({item.ProductionYear})",
        _ => item.Name ?? string.Empty,
    };

    public static Guid? SyncUserId(PluginConfiguration config)
        => Guid.TryParse(config.SyncUserId, out var id) && id != Guid.Empty ? id : null;

    /// <summary>The selected library (collection folder) ids; empty means every library.</summary>
    public static HashSet<Guid> LibraryIds(PluginConfiguration config)
        => config.LibraryIds.Select(s => Guid.TryParse(s, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).ToHashSet();

    public static bool InLibraries(ILibraryManager libraryManager, BaseItem item, HashSet<Guid> libraryIds)
        => libraryIds.Count == 0 || libraryManager.GetCollectionFolders(item).Any(f => libraryIds.Contains(f.Id));
}
