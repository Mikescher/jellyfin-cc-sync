# jellyfin-cc-sync

A Jellyfin server plugin (`net10.0`, Jellyfin 12.3.0 packages, ABI `12.3.0.0`) that syncs one
Jellyfin user's watch state, favourites and `jcc-*` tags from a jClipCorn database and writes
Jellyfin watches back into jClipCorn's viewed history.

Status: planned, not implemented. `PLAN.md` is the build plan (decisions, layout, phases);
`docs/` holds the verified reference material. Read the matching doc before touching a component.

## Identity
- Display name `jellyfin-cc-sync`, assembly/namespace `Jellyfin.Plugin.CcSync`, GUID
  `7c59bc79-ad24-4106-94b9-d13477e9a518`, API route `/CcSync`, data folder
  `<DataPath>/jellyfin-cc-sync/`.
- `../jellyfin-watchnotify` is the template for project files, packaging, release flow, config
  pages and watch detection. `../jClipCorn` is the ground truth for the database format.

## Rules that protect jClipCorn's data
- Write only `ClipCornUserData.db`, never `ClipCornDB.db` or `ClipCornHistory.db`.
- Write only while neither `ClipCornDB.db.~lock` nor `ClipCornHistory.db.~lock` is held and no
  `-journal` file exists; probe again before and after the transaction. Never create, edit or
  delete lock files.
- Never change `journal_mode`, never drop or edit the `JCCTRIGGER_*` triggers, set
  `PRAGMA recursive_triggers = true` on the write connection.
- Viewed history entries are written as `yyyy-MM-dd HH:mm:ss` local time; existing entries stay
  byte-identical; a list that does not parse completely is never rewritten (one bad entry makes
  jClipCorn drop the whole list).
- User rows are sparse: upsert with `ON CONFLICT(ID) DO UPDATE`, never `INSERT OR REPLACE`;
  delete a row that returns to all defaults.
- Sync reads go through a local snapshot copy (`immutable=1`); the writer is the only code that
  opens a live jClipCorn file, and only `ClipCornUserData.db`.
- `Pooling=False` on every connection to jClipCorn files.

## Jellyfin traps
- Save user data with `UserDataSaveReason.Import` and the `UserItemData` overload, on the
  `GetItemById` instance; the watch handler ignores `Import`.
- Persist tags with `UpdateToRepositoryAsync(ItemUpdateType.MetadataImport)`, never
  `MetadataEdit` (it can rewrite jClipCorn's NFOs).
- `ProviderIds` of DB-loaded items is case-sensitive: `"clipcorn"`, `"Tmdb"`, `"Imdb"`.
- Every tag starting with `jcc-` (case-insensitive) is owned by the plugin; never touch others.
- `UserDataSaved` handlers of other plugins can throw into `SaveUserData`; wrap the call.
- Controllers must not inject `ILogger<T>` (jellyfin#11488).
- Jellyfin packages carry `ExcludeAssets=runtime`; `Microsoft.Data.Sqlite` is referenced
  compile-only and comes from the server. The publish output must be only our dll.
- Only one page per plugin may have `EnableInMainMenu`; the rest are tabs.

## Build / test
- `make build`, `make test`, `make deploy JELLYFIN_CONFIG=…`, `make package`.
- `make release VERSION=x.y.z.w`, then commit the zip together with `manifest.json`.
- Logo: `scripts/make_logo.py` (fonttools + uharfbuzz, Adwaita Sans) writes `assets/logo.svg`;
  render with `rsvg-convert -w 1600 -h 900 assets/logo.svg -o assets/logo.png`.
