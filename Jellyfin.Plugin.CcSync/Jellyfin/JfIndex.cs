using Jellyfin.Data.Enums;
using Jellyfin.Plugin.CcSync.ClipCorn.Model;
using Jellyfin.Plugin.CcSync.Configuration;
using Jellyfin.Plugin.CcSync.Sync;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;

namespace Jellyfin.Plugin.CcSync.Jellyfin;

public sealed record JfIndexResult(IReadOnlyList<JfItemInfo> Items, IReadOnlyList<BaseItem> Instances);

/// <summary>
/// Movies, series, seasons and episodes of the selected libraries. The instances are read-only:
/// they come without user data, so saving one would wipe the user's row.
/// </summary>
public sealed class JfIndex
{
    private readonly ILibraryManager _libraryManager;

    public JfIndex(ILibraryManager libraryManager)
    {
        _libraryManager = libraryManager;
    }

    public JfIndexResult Build(PluginConfiguration config)
    {
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Season, BaseItemKind.Episode],
            IsVirtualItem = false,
            Recursive = true,
            // Without these fields the database query leaves ProviderIds (and with them the clipcorn id) empty.
            DtoOptions = new DtoOptions(false)
            {
                Fields = [ItemFields.ProviderIds, ItemFields.Tags, ItemFields.Path],
                EnableImages = false,
                EnableUserData = false,
            },
        };

        var libraries = JfItems.LibraryIds(config);
        if (libraries.Count > 0)
        {
            query.AncestorIds = [.. libraries];
        }

        var instances = _libraryManager.GetItemList(query)
            .Where(i => JfItems.Kind(i) is not null)
            .ToList();

        var items = instances.Select(i => new JfItemInfo(
                i.Id,
                Enum.Parse<CcKind>(JfItems.Kind(i)!),
                i.Name ?? string.Empty,
                i.ProductionYear,
                JfItems.ProviderId(i, JfItems.ClipCornProvider),
                JfItems.ProviderId(i, JfItems.TmdbProvider),
                JfItems.ProviderId(i, JfItems.ImdbProvider),
                i.Tags ?? [],
                i.Path,
                i.LockedFields?.Contains(MetadataField.Tags) == true))
            .ToList();

        return new JfIndexResult(items, instances);
    }
}
