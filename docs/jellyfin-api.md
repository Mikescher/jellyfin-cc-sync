# Jellyfin 12.3 API notes

The server APIs the plugin uses, with the traps that matter. Verified against the server source
at tag `v12.3` and the `Jellyfin.Controller`/`Jellyfin.Model` 12.3.0 packages (the 12.1 → 12.3
public API diff only removes `InternalItemsQuery.UseRawName`). Paths are relative to
`https://github.com/jellyfin/jellyfin/blob/v12.3/`.

## Packages and ABI

- `Jellyfin.Controller` and `Jellyfin.Model` **12.3.0**, `ExcludeAssets=runtime`, `net10.0`.
- `targetAbi` **12.3.0.0** in `meta.json`/`manifest.json`; it is a floor (server version >= ABI).
- The jellyfin-watchnotify sources build unchanged against 12.3.0; its patterns (hosted
  service, config pages, controller without `ILogger<T>`) carry over.

## User data — `IUserDataManager`

```csharp
event EventHandler<UserDataSaveEventArgs>? UserDataSaved;
UserItemData? GetUserData(User user, BaseItem item);
Dictionary<Guid, UserItemData> GetUserDataBatch(IReadOnlyList<BaseItem> items, User user);
void SaveUserData(User user, BaseItem item, UserItemData userData, UserDataSaveReason reason, CancellationToken ct);
void SaveUserData(User user, BaseItem item, UpdateUserItemDataDto dto, UserDataSaveReason reason);
```

`UserItemData`: `Played`, `PlayCount`, `LastPlayedDate` (`DateTime?`), `IsFavorite`,
`PlaybackPositionTicks`, `Rating` (`double?`, 0–10), stream indexes, `Key`.

Rules for the plugin:

- **Save with `UserDataSaveReason.Import`.** Only the NFO parser uses it otherwise. It does not
  trigger the NFO user-data saver (which reacts to PlaybackFinished/TogglePlayed/
  UpdateUserRating), clients still get a live `UserDataChanged` push, and
  jellyfin-watchnotify ignores it. The plugin's own `UserDataSaved` handler ignores `Import`, so
  its writes never create pending updates.
- **Use the `UserItemData` overload.** The DTO overload only applies fields that have a value and
  cannot set `LastPlayedDate` back to `null`.
- **Get the item with `ILibraryManager.GetItemById(id)`** (the cached shared instance, always with
  user data), read with `GetUserData` on that instance, change only the owned fields
  (`Played`, `PlayCount`, `LastPlayedDate`, `IsFavorite`), save. Never save user data on an item
  from `GetItemList` with `EnableUserData=false`: `GetUserData` then returns a blank row and
  saving it wipes favourites, resume points and counts.
- The whole row is written (last writer wins), so re-read right before each write.
- `UserDataSaved` is invoked synchronously **without try/catch**; another plugin's throwing
  handler surfaces in our call after the data is already saved. Wrap every `SaveUserData`.
- `Played=true` with `LastPlayedDate=null` is allowed.
- No batch write API; one `SaveUserData` per item (own transaction). Reads: `GetUserDataBatch`.
- Server behaviour to keep in mind: `PlayCount` is incremented at playback **start**;
  `LastPlayedDate` is set at stop (and at start for not-yet-played items).
- No per-play history exists in core; only `PlayCount` + `LastPlayedDate`.

`UserDataSaveReason`: `PlaybackStart=1, PlaybackProgress=2, PlaybackFinished=3, TogglePlayed=4,
UpdateUserRating=5, Import=6, UpdateUserData=7`. Favourite toggles from clients arrive as
`UpdateUserRating`; mark played/unplayed as `TogglePlayed`.

`IsFavorite` is per user and per item: movies, series, seasons and episodes each have their own
row.

## Tags

- `BaseItem.Tags` (`string[]`), library-global metadata (not per user). Stored `|`-joined and
  de-duplicated case-insensitively; a tag containing `|` would be split — slugs never contain it.
- Persist: set `item.Tags` on the `GetItemById` instance and call
  `item.UpdateToRepositoryAsync(ItemUpdateType.MetadataImport, ct)`.
  - **Not `MetadataEdit`**: that runs the metadata savers and may rewrite jClipCorn's NFO files.
  - Saving persists the whole in-memory item (provider ids, images, locked fields), so only ever
    save a fully loaded `GetItemById` instance.
- Metadata refresh (`MediaBrowser.Providers/Manager/MetadataService.cs`):
  - normal scan/refresh: existing tags ∪ NFO tags ∪ TMDb keyword tags → `jcc-*` tags survive;
  - "Replace all metadata" and Identify drop existing tags → the next sync re-adds them;
  - Identify also replaces the provider ids, dropping `clipcorn` until the next normal refresh;
    the item is then matched by the TMDB/IMDB/name fallback or reported as unmatched.
  - `MetadataField.Tags` in `item.LockedFields` protects tags in every mode but also freezes
    TMDb keyword tags; offered as the opt-in setting `LockTagsField`.
- Clients filter by tags (`/Items?tags=…`, web client Tags filter). Series tags are not
  inherited by episodes for filtering.

## Provider ids and the item index

- `<uniqueid type="clipcorn">` lands in `ProviderIds["clipcorn"]` (`BaseNfoParser.cs:607` →
  `ProviderIdsExtensions.TrySetProviderId`: unknown names are stored verbatim). TMDB/IMDB map to
  `"Tmdb"`/`"Imdb"`.
- **Items loaded from the DB have a case-sensitive `ProviderIds` dictionary.** Always use the
  exact keys `"clipcorn"`, `"Tmdb"`, `"Imdb"`.
- Index query (read-only use; never save these instances):
  ```csharp
  _libraryManager.GetItemList(new InternalItemsQuery
  {
      IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Season, BaseItemKind.Episode],
      IsVirtualItem = false,
      DtoOptions = new DtoOptions(false) { Fields = [ItemFields.ProviderIds], EnableImages = false, EnableUserData = false },
  });
  ```
  Restrict to selected libraries via `AncestorIds` or `TopParentIds` (not verified which one
  matches library root folders in 12.3; test against a multi-library server). `HasAnyProviderId`
  (`["clipcorn"] = ""` = any value) narrows to NFO-tagged items, but the index needs untagged
  items too (fallback matching, removal of stale `jcc-*` tags).
- Useful members: `ProductionYear`, `Name`, `Episode.SeriesId`, `Episode.SeasonId`,
  `ParentIndexNumber`, `IndexNumber`, `Season.SeriesId`.
- Alternate versions are excluded by default.

## Playback events (watch detection)

Unchanged from 12.1, as used by jellyfin-watchnotify:
`ISessionManager.PlaybackStart`/`PlaybackProgress` (`PlaybackProgressEventArgs`),
`PlaybackStopped` (`PlaybackStopEventArgs` + `PlayedToCompletion`), `IUserDataManager.UserDataSaved`
(`UserDataSaveEventArgs`: `UserId`, `Item`, `UserData`, `SaveReason`).

## Scheduled tasks

```csharp
interface IScheduledTask { string Name; string Key; string Description; string Category;
    Task ExecuteAsync(IProgress<double> progress, CancellationToken ct);
    IEnumerable<TaskTriggerInfo> GetDefaultTriggers(); }
interface IConfigurableScheduledTask { bool IsHidden; bool IsEnabled; bool IsLogged; }
new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromMinutes(5).Ticks }
```

- Discovered automatically from the plugin assembly; created with
  `ActivatorUtilities.CreateInstance` (constructor injection of our DI singletons works; the task
  instance itself is not a singleton). A throwing constructor fails the plugin.
- Triggers are user-editable in Dashboard → Scheduled Tasks and persisted by the server.
- `ITaskManager.QueueScheduledTask<T>()` runs one on demand (used by the "Run now" buttons).
- `IsLogged = false` keeps 5-minute runs out of the activity log.

## SQLite (`Microsoft.Data.Sqlite`)

- Jellyfin 12.3 ships `Microsoft.Data.Sqlite` 10.0.12 and `SQLitePCLRaw` 2.1.12 (SQLite 3.53.3)
  and initialises the provider.
- Plugin assemblies fall back to the server's copy when they are not in the plugin folder.
  Reference the package compile-only (`IncludeAssets=compile`, version <= 10.0.12) and verify the
  publish output contains only `Jellyfin.Plugin.CcSync.dll`. Never ship SQLitePCLRaw or a native
  `libe_sqlite3`: a second copy has its own static state.
- `Mode=ReadOnly` → `SQLITE_OPEN_READONLY`; a `Data Source=file:…?immutable=1` URI is honoured.
  `immutable=1` only on private snapshots, never on the live share.
- Pooling is on by default and keeps file handles open after `Close()`. Use `Pooling=False` for
  every connection to jClipCorn files and snapshot files.

## Plugin data and pages

- State: `Path.Combine(IApplicationPaths.DataPath, "jellyfin-cc-sync")`. Not
  `Plugin.DataFolderPath` (inside the plugins folder) and not covered by Jellyfin's own backup.
- `PluginPageInfo.EnableInMainMenu = true` lists the page under the dashboard's Plugins section;
  `MenuIcon` takes a Material icon name (`"sync"`); `MenuSection` is ignored. Only **one** page
  per plugin may be in the menu (the drawer keys entries by plugin id); further pages are tabs.
- Controllers must not inject `ILogger<T>` (jellyfin#11488).
