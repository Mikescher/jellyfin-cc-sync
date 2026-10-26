using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.CcSync.Sync;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CcSync.Jellyfin;

public sealed record JfApplyResult(bool UserDataApplied, bool TagsApplied, string? Problem);

/// <summary>
/// Writes planned changes (<c>docs/jellyfin-api.md</c> § User data, § Tags): always on the cached
/// <c>GetItemById</c> instance, user data with <see cref="UserDataSaveReason.Import"/>, tags with
/// <see cref="ItemUpdateType.MetadataImport"/> so no metadata saver rewrites jClipCorn's NFOs.
/// </summary>
public sealed class JfApplier
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserDataManager _userDataManager;
    private readonly ILogger<JfApplier> _logger;

    public JfApplier(ILibraryManager libraryManager, IUserDataManager userDataManager, ILogger<JfApplier> logger)
    {
        _libraryManager = libraryManager;
        _userDataManager = userDataManager;
        _logger = logger;
    }

    public async Task<JfApplyResult> ApplyAsync(PlannedChange change, User user, bool applyUserData, CancellationToken cancellationToken)
    {
        var item = _libraryManager.GetItemById(change.ItemId);
        if (item is null)
        {
            return new JfApplyResult(false, false, "the item no longer exists");
        }

        var userDataApplied = false;
        if (applyUserData && change.HasUserDataChange)
        {
            // Re-read right before the write: the whole row is saved, last writer wins.
            var data = _userDataManager.GetUserData(user, item);
            if (data is null)
            {
                return new JfApplyResult(false, false, "no user data row for the sync user");
            }

            if (change.Played is { } played)
            {
                data.Played = played.New;
            }

            if (change.PlayCount is { } count)
            {
                data.PlayCount = count.New;
            }

            if (change.LastPlayed is { } last)
            {
                data.LastPlayedDate = last.New;
            }

            if (change.Favorite is { } favorite)
            {
                data.IsFavorite = favorite.New;
            }

            try
            {
                _userDataManager.SaveUserData(user, item, data, UserDataSaveReason.Import, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // UserDataSaved handlers run without try/catch, after the row is saved; tell a
                // throwing handler of another plugin apart from a failed save by reading back.
                var check = _userDataManager.GetUserData(user, item);
                if (check is null || !Matches(check, change))
                {
                    throw;
                }

                _logger.LogWarning(ex, "A UserDataSaved handler threw after the user data of {Item} was saved", item.Name);
            }

            userDataApplied = true;
        }

        var tagsApplied = false;
        if (change.HasTagChange)
        {
            var tags = (item.Tags ?? [])
                .Where(t => !change.RemoveTags.Contains(t, StringComparer.OrdinalIgnoreCase))
                .ToList();
            foreach (var tag in change.AddTags)
            {
                if (!tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                {
                    tags.Add(tag);
                }
            }

            item.Tags = [.. tags];

            if (change.LockTags && !(item.LockedFields ?? []).Contains(MetadataField.Tags))
            {
                item.LockedFields = [.. item.LockedFields ?? [], MetadataField.Tags];
            }

            await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataImport, cancellationToken).ConfigureAwait(false);
            tagsApplied = true;
        }

        return new JfApplyResult(userDataApplied, tagsApplied, null);
    }

    private static bool Matches(MediaBrowser.Controller.Entities.UserItemData data, PlannedChange change)
        => (change.Played is null || data.Played == change.Played.New)
           && (change.PlayCount is null || data.PlayCount == change.PlayCount.New)
           && (change.LastPlayed is null || SyncPlanner.SameSecond(data.LastPlayedDate, change.LastPlayed.New))
           && (change.Favorite is null || data.IsFavorite == change.Favorite.New);
}
