# Plan: jellyfin-cc-sync

A Jellyfin server plugin that keeps one Jellyfin user's watch state, favourites and `jcc-*` tags
in line with a jClipCorn database on the NAS, and writes watches made in Jellyfin back into
jClipCorn.

- **jClipCorn → Jellyfin** (every 30 min): viewed state + dates, favourites (from the user
  score), `jcc-rating-<label>` and `jcc-<group>` tags. jClipCorn plus not-yet-written watches is
  the source of truth; Jellyfin data of the sync user is overwritten.
- **Jellyfin → jClipCorn** (on every watch, retried every 5 min): a watch becomes a *pending
  update*; it is written into jClipCorn's viewed history as soon as jClipCorn is not running.
- Every applied change and every pending step lands in an exhaustive history, shown on the
  plugin's pages.

Reference documents (read these before implementing a component):

| File | Content |
|---|---|
| `docs/clipcorn-db.md` | jClipCorn files, lock, schema, data formats, write recipe |
| `docs/jellyfin-api.md` | Jellyfin 12.3 APIs used and their traps |
| `docs/sync-semantics.md` | Exact mapping, matching, overlay, concurrency and safety rules |
| `docs/deployment.md` | Mounting the share, Jellyfin settings, coexistence |
| `../jellyfin-watchnotify/` | Template: project layout, packaging, releases, watch detection, config pages |
| `../jClipCorn/` | jClipCorn source (ground truth for the DB format) |
| `../ClipCornWebPanel/src/lib/server/queries.ts` | Working read queries against the split DB |

## 1. Decisions

| Topic | Decision | Why |
|---|---|---|
| Name | Display name `jellyfin-cc-sync`; assembly/namespace `Jellyfin.Plugin.CcSync`; Gitea repo stays `jellyfin-sync`, the GitHub mirror is `jellyfin-cc-sync` | C# identifiers cannot contain `-` |
| DB access | A directory path (bind mount / Docker cifs or nfs volume / same NAS). No SMB/NFS client in the plugin | A container cannot mount; SQLite's own journal + locks only work on a real file system; least code |
| Reading | Copy `ClipCornDB.db` + `ClipCornUserData.db` to a local snapshot, verify stability, open the snapshot `immutable=1` | No SQLite locks on the live files, so a running jClipCorn never sees `SQLITE_BUSY` from us; a torn copy is detected and the run aborts |
| Writing | Only `ClipCornUserData.db`, only while no jClipCorn lock is held, in one transaction, after a rotating backup | jClipCorn keeps everything in memory and would overwrite concurrent writes; the main DB may be replaced wholesale by jcc-mirror |
| Own lock file | **None**; locks are probed before and after the write | A plugin lock left by a crash would block jClipCorn for 3 h; the race window is a few hundred ms |
| Unknown date (`UNSPECIFIED`) | Counts for Played/PlayCount; `LastPlayedDate` = latest *dated* entry, `null` if none | No invented dates |
| Date-only entries | Configurable time of day, default `12:00` local | Stays on the same calendar day in any display timezone |
| Watch timestamp written to jClipCorn | Start of playback | jClipCorn's own convention ("play" adds the entry at launch) |
| Manual "mark played" in Jellyfin | Creates a pending update only if `ManualMarkCreatesPending` (default off) | Same opt-in as watchnotify |
| "Mark unplayed" in Jellyfin | Never written to jClipCorn; reverted by the next sync | jClipCorn is the source of truth; the plugin never deletes history entries |
| "Watch later" tag | Removed on write like jClipCorn does, controlled by the plugin setting `ResetWatchLaterOnWatch` (default on), independent of jClipCorn's property | Explicit, visible in Jellyfin |
| Ratings/favourites scope | Movies, series, seasons, episodes (each toggleable) | All four carry a jClipCorn score; seasons match via `season.nfo` |
| Not synced | jClipCorn tags (watch later/never …) as Jellyfin tags; jClipCorn score → Jellyfin `Rating` | Out of scope for now |
| Plugin state | SQLite `state.db` in `<data>/jellyfin-cc-sync/` | Unbounded history needs paging and filtering; SQLite is already available in the server |
| Jobs | Two `IScheduledTask`s (5 min / 30 min default triggers) + an immediate debounced write attempt after each watch | Visible and editable in Dashboard → Scheduled Tasks, "Run now" for free |
| Safety | Change guard with preview/confirm, dry-run mode, all-or-nothing reads | The first sync rewrites a whole library; a wrong path or user DB must not un-watch everything |
| Target | Jellyfin 12.3.0 packages, `targetAbi 12.3.0.0`, `net10.0` | Requested minimum |
| Releases | Same scheme as watchnotify: zip committed in `releases/`, `manifest.json` served raw from the public mirror | Proven; see §12 for the open hosting detail |

## 2. Identity

| Item | Value |
|---|---|
| Display name | `jellyfin-cc-sync` |
| Assembly / namespace | `Jellyfin.Plugin.CcSync` |
| Plugin GUID | `7c59bc79-ad24-4106-94b9-d13477e9a518` |
| API route prefix | `/CcSync` |
| Config pages | `CcSync` (menu entry, icon `sync`), `CcSyncStatus`, `CcSyncPending`, `CcSyncHistory` |
| Data folder | `<DataPath>/jellyfin-cc-sync/` |
| Package | `jellyfin-cc-sync_<version>.zip` |
| Scheduled task category | `jellyfin-cc-sync` |
| Category (catalogue) | `General` |

## 3. Repository layout

```
jellyfin-cc-sync/
├── PLAN.md  README.md  CLAUDE.md  .gitignore
├── docs/                         clipcorn-db.md, jellyfin-api.md, sync-semantics.md, deployment.md
├── assets/                       logo.svg (source, outlined wordmark), logo.png (1600×900 render)
├── scripts/                      make_logo.py, package.py (from watchnotify)
├── manifest.json                 plugin repository
├── releases/                     committed release zips
├── Directory.Build.props         version, net10.0, nullable, warnings as errors
├── global.json                   `dotnet test` on Microsoft.Testing.Platform (xunit.v3)
├── Jellyfin.Plugin.CcSync.slnx
├── Makefile                      build / test / deploy / package / release
├── .github/workflows/release.yml
├── Jellyfin.Plugin.CcSync/
│   ├── Jellyfin.Plugin.CcSync.csproj
│   ├── Plugin.cs                         BasePlugin<PluginConfiguration>, IHasWebPages
│   ├── PluginServiceRegistrator.cs
│   ├── PluginPaths.cs                    <DataPath>/jellyfin-cc-sync/{state.db,snapshots,backups}
│   ├── Configuration/                    PluginConfiguration, ConfigValues, 4 pages + common.js (embedded)
│   ├── ClipCorn/                         everything that knows the jClipCorn format
│   │   ├── CcFiles.cs                    file and lock names below the configured directory
│   │   ├── CcLockProbe.cs                lock parsing and held/stale rule
│   │   ├── CcSnapshot.cs                 stable local copy of both DB files
│   │   ├── CcReader.cs                   snapshot → CcLibrary
│   │   ├── CcSqlite.cs                   snapshot connections, header change counter
│   │   ├── CcProbe.cs                    "Test ClipCorn access"
│   │   ├── CcSchema.cs                   supported versions, DUUID pairing
│   │   ├── CcWriter.cs                   pending updates → ClipCornUserData.db
│   │   ├── CcBackups.cs                  rotating user-data backups
│   │   └── Model/                        CcLibrary, CcElements (movie/series/season/episode records),
│   │                                     CcDateTime, CcViewedHistory, CcOnlineRefs, CcScore, CcTagList,
│   │                                     CcGroupList
│   ├── Jellyfin/
│   │   ├── JfIndex.cs                    library query → JfItemInfo records
│   │   └── JfApplier.cs                  user data + tag writes on GetItemById instances
│   ├── Sync/
│   │   ├── Matcher.cs                    CcLibrary × JfItemInfo → MatchResult (pure)
│   │   ├── DesiredStateBuilder.cs        overlay + mapping rules (pure)
│   │   ├── SyncPlanner.cs                desired vs actual → SyncPlan (pure)
│   │   ├── TagSlug.cs
│   │   ├── SyncGate.cs                   one SemaphoreSlim for both directions
│   │   ├── SyncEngine.cs                 CC → JF run
│   │   └── FlushEngine.cs                JF → CC write session
│   ├── Watch/
│   │   ├── WatchEntryPoint.cs            IHostedService: events → pending, debounced flush
│   │   └── SessionTracker.cs             start times + dedup (port from watchnotify)
│   ├── Tasks/
│   │   ├── FlushPendingTask.cs           IScheduledTask, interval 5 min
│   │   └── SyncFromClipCornTask.cs       IScheduledTask, interval 30 min
│   ├── State/
│   │   ├── StateDb.cs                    connection factory + schema migrations
│   │   ├── PendingStore.cs
│   │   ├── HistoryStore.cs
│   │   └── RunStore.cs                   run summaries, last match report, stored preview plan
│   └── Api/
│       ├── CcSyncController.cs
│       └── Responses.cs
└── Jellyfin.Plugin.CcSync.Tests/         xUnit v3; pure logic + fixture databases (Fixtures/v38, see its README)
```

The pure layer (`ClipCorn/Model`, `Sync/Matcher|DesiredStateBuilder|SyncPlanner|TagSlug`) uses
only plugin-defined records, no Jellyfin types, so it is unit-testable without a server.

## 4. Architecture

```
                 ┌──────────────── Jellyfin process ───────────────────────────────────┐
 playback events │ WatchEntryPoint ──► PendingStore ──(debounce 15 s)──┐               │
 (sync user only)│        ▲                                            ▼               │
                 │  SessionTracker                FlushPendingTask ► FlushEngine ──────┼──► ClipCornUserData.db
                 │                                     (5 min)          │  ▲           │    (write, no lock held)
                 │                                                      │  │ CcLockProbe
                 │                      SyncGate (one at a time) ◄──────┤  │ CcSnapshot ◄┼─── ClipCornDB.db
                 │                                                      │  │             │    ClipCornUserData.db
                 │  SyncFromClipCornTask ► SyncEngine ──────────────────┘  │             │    (read via snapshot)
                 │       (30 min)            │ CcReader → Matcher → DesiredStateBuilder │
                 │                           │   → SyncPlanner → JfApplier             │
                 │                           ▼                                         │
                 │                     IUserDataManager / ILibraryManager (sync user)  │
                 │  HistoryStore / RunStore ◄── every step                              │
                 │  CcSyncController ◄── dashboard pages                                │
                 └─────────────────────────────────────────────────────────────────────┘
```

Singletons: `StateDb`, `PendingStore`, `HistoryStore`, `RunStore`, `SessionTracker`,
`SyncGate`, `CcSnapshot`, `CcReader`, `CcWriter`, `JfIndex`, `JfApplier`, `SyncEngine`,
`FlushEngine`; hosted service `WatchEntryPoint`. Scheduled tasks get the engines injected.

### 4.1 Jellyfin → jClipCorn

1. `WatchEntryPoint` (watchnotify logic, `docs/sync-semantics.md` § Watches) detects a watch of
   the sync user → `PendingStore.Add` → history `pending-created` → schedules a flush in 15 s.
2. `FlushEngine.RunAsync` (also called by `FlushPendingTask`): no pending → return; enter
   `SyncGate`; `CcLockProbe` held or journal present → record `cc-locked` once per holder, return;
   snapshot main DB → validate; `CcBackups.Create`; probe again; `CcWriter.Apply` in one
   transaction; probe again; record outcomes; leave the gate.

### 4.2 jClipCorn → Jellyfin

`SyncEngine.RunAsync` (`SyncFromClipCornTask`, "Run now", "Apply preview"):

1. Enter `SyncGate`. Validate configuration (directory, sync user exists).
2. `CcSnapshot.Take` both files → `CcReader.Read` → `CcLibrary`. Any failure → `run-sync`
   aborted, Jellyfin untouched.
3. `PendingStore` snapshot of `pending` updates (overlay), timestamp `T0`.
4. `JfIndex.Build` for the selected libraries; `Matcher.Match`.
5. `DesiredStateBuilder` → desired user data and `jcc-*` tag sets; `GetUserDataBatch` for the
   actual user data; `SyncPlanner.Plan` → list of changes.
6. Guards: dry-run or over the limits → store the plan as preview, record `planned-*`, stop.
7. `JfApplier`: per change re-check "pending created after `T0` for this item" → skip; otherwise
   `GetItemById` → write → history entry. Tag changes via `UpdateToRepositoryAsync(MetadataImport)`.
8. Store run summary + match report; leave the gate.

## 5. Configuration

`PluginConfiguration` (XML, managed by the server). Defaults in brackets.

| Group | Setting | Default | Meaning |
|---|---|---|---|
| jClipCorn | `ClipCornDirectory` | `""` | Path of the `ClipCornDB` folder inside the Jellyfin process |
| | `ClipCornTimeZone` | `Europe/Berlin` | IANA zone of jClipCorn's local timestamps |
| | `DateOnlyTimeOfDay` | `12:00` | Time used for date-only history entries |
| | `LockStaleMinutes` | `180` | Lock older than this (heartbeat or mtime) counts as stale; minimum 180 |
| | `BackupCount` | `20` | User-data backups kept |
| Jellyfin | `SyncUserId` | none | The one Jellyfin user that is synced (dropdown) |
| | `LibraryIds` | empty = all | Libraries taken into account |
| Write-back | `WriteBackEnabled` | on | Create pending updates for watches |
| | `WatchedThreshold` | `0.90` | Played fraction that counts as watched |
| | `DedupMinutes` | `60` | Duplicate suppression per item |
| | `StartCoalesceMinutes` | `10` | Pauses/seeks shorter than this keep the original start time |
| | `ManualMarkCreatesPending` | off | "Mark played" in Jellyfin also creates a pending update |
| | `ResetWatchLaterOnWatch` | on | Remove jClipCorn tag 3 (movie; episode + series) on write |
| Sync | `SyncEnabled` | on | CC → JF runs do anything at all |
| | `SyncPlayed` / `SyncPlayCount` / `SyncLastPlayed` | on | Fields owned by the sync |
| | `SyncFavorites` | on | Score → `IsFavorite` |
| | `FavoriteScores` | `[4, 5]` | Stored score values that mean favourite (checkbox list with labels) |
| | `RatingTagsEnabled` | on | `jcc-rating-<label>` |
| | `GroupTagsEnabled` | on | `jcc-<group>` |
| | `GroupTagsIncludeParents` | off | Also tag ancestor groups |
| | `ApplyRatingsTo` | movies, series, seasons, episodes | Kinds that get favourites and rating tags |
| | `LockTagsField` | off | Add `MetadataField.Tags` to locked fields of tagged items |
| Safety | `DryRun` | off | Plan and record, apply nothing |
| | `MaxChangesPerRun` | `100` | Items changed per run without confirmation |
| | `MaxUnwatchPerRun` | `20` | Played → unplayed flips per run without confirmation |
| History | `HistoryRetentionDays` | `0` | 0 = keep forever |
| | `WriteToActivityLog` | on | Mirror failures, aborts and guard stops into Jellyfin's activity log |

The task intervals are not plugin settings; they are the scheduled tasks' triggers.

## 6. State store (`state.db`)

Local SQLite file, WAL mode, `Microsoft.Data.Sqlite` from the server. Schema version in
`PRAGMA user_version`, migrations in `StateDb`.

```sql
CREATE TABLE pending (
  id INTEGER PRIMARY KEY,
  created_utc TEXT NOT NULL, source TEXT NOT NULL,
  jf_item_id TEXT NOT NULL, item_name TEXT NOT NULL, item_kind TEXT NOT NULL,
  cc_id TEXT NULL, match_keys TEXT NOT NULL,          -- JSON: provider ids, name, year
  watched_at_utc TEXT NOT NULL,
  status TEXT NOT NULL,                                -- pending|written|duplicate|failed|discarded
  attempts INTEGER NOT NULL DEFAULT 0, last_attempt_utc TEXT NULL, last_error TEXT NULL,
  finished_utc TEXT NULL, cc_entry TEXT NULL);
CREATE INDEX pending_status ON pending(status);
CREATE INDEX pending_item ON pending(jf_item_id);

CREATE TABLE history (
  id INTEGER PRIMARY KEY, ts_utc TEXT NOT NULL, run_id TEXT NULL, kind TEXT NOT NULL,
  jf_item_id TEXT NULL, cc_id TEXT NULL, item_name TEXT NULL, item_kind TEXT NULL,
  field TEXT NULL, old_value TEXT NULL, new_value TEXT NULL,
  detail TEXT NULL, success INTEGER NOT NULL);
CREATE INDEX history_ts ON history(ts_utc);
CREATE INDEX history_kind ON history(kind);
CREATE INDEX history_run ON history(run_id);

CREATE TABLE runs (
  id TEXT PRIMARY KEY, kind TEXT NOT NULL,             -- sync|flush
  started_utc TEXT NOT NULL, finished_utc TEXT NULL,
  outcome TEXT NOT NULL,                               -- running|applied|nothing-to-do|dry-run|guarded|aborted|waiting
  summary TEXT NOT NULL);                              -- JSON counts, abort reason
CREATE INDEX runs_kind ON runs(kind, started_utc);

CREATE TABLE kv (key TEXT PRIMARY KEY, value TEXT NOT NULL);  -- last match report, stored preview plan
```

History kinds: `docs/sync-semantics.md` § History.

Times are stored as `yyyy-MM-ddTHH:mm:ss.fffZ` text (UTC, sortable), Jellyfin item ids as `Guid`
`N` format. Every connection is opened with `Pooling=False`; WAL lets the pages read while an
engine writes. A run that is still `running` at startup was cut off by a server stop and is
marked `aborted`.

## 7. HTTP API

`[Authorize(Policy = Policies.RequiresElevation)]`, route `/CcSync`, no `ILogger<T>` in the
controller.

| Method | Path | Purpose |
|---|---|---|
| GET | `Status` | Config warnings, directory/lock/journal state, versions, DUUID, last runs, pending counts, match summary |
| POST | `Probe` | "Test ClipCorn access" (deployment.md § Checking the setup) |
| GET | `Matching` | Last match report lists (unmatched, ambiguous, kind mismatch, fallback-matched) |
| GET | `Pending?status=&limit=&offset=` | Pending updates |
| POST | `Pending/{id}/Retry` | `failed` → `pending` |
| DELETE | `Pending/{id}` | → `discarded` |
| GET | `History?kind=&search=&runId=&from=&to=&limit=&offset=` | Paged history, newest first |
| DELETE | `History?olderThanDays=` | Manual pruning |
| POST | `Sync/Run` | Queue `SyncFromClipCornTask` |
| POST | `Sync/Preview` | Run with forced dry-run, returns the plan |
| GET | `Sync/Preview` | Last stored plan (guard stop or dry-run) |
| POST | `Sync/Apply?planId=` | Run once with the guard lifted; refused if the plan is stale (CC files or pending changed since) |
| POST | `Flush/Run` | Queue `FlushPendingTask` |

## 8. Dashboard pages

One menu entry (`CcSync`, icon `sync`); the others are tabs set with `LibraryMenu.setTabs`, with
the same fallback tab bar as watchnotify.

- **Settings** — the groups of §5; sync user dropdown and library checkboxes fed from
  `/Users` and `/Library/VirtualFolders`; favourite scores as labelled checkboxes; "Test ClipCorn
  access" button with the probe result inline.
- **Status** — health (directory, lock holder + heartbeat age, versions, DUUID), last sync and
  flush with outcome, pending count, buttons *Run sync now*, *Preview*, *Write pending now*;
  when a plan is stored: change table (item, field, old → new) with *Apply*; match report
  summary with expandable lists.
- **Pending** — table (created, item, watched-at, status, attempts, last error) with
  *Retry*/*Discard*.
- **History** — filterable (kind, text, run, date range), paged table; run rows link to their
  entries.

## 9. Scheduled tasks

| Class | Name | Key | Default trigger | `IsLogged` |
|---|---|---|---|---|
| `FlushPendingTask` | Write pending watches to jClipCorn | `CcSyncFlush` | interval 5 min | false |
| `SyncFromClipCornTask` | Sync jClipCorn → Jellyfin | `CcSyncFromClipCorn` | interval 30 min | false |

Both report progress, honour cancellation and delegate to the engine singletons. The engines
are safe to call concurrently (gate) and return quickly when there is nothing to do.

## 10. Testing

Unit tests (xUnit, no server):

- `CcDateTime` / `CcViewedHistory`: every encoding variant, jClipCorn's sort order, `add()`
  equality (date-only vs datetime same day, identical datetimes, multiple `UNSPECIFIED`),
  compact serialisation, byte-identical round trip of existing lists, rejection of unparseable
  lists. Take expectations from `../jClipCorn/src/main/de/jClipCorn/util/datetime/` and
  `columnTypes/CCDateTimeList.java`.
- Time zone conversion incl. DST gap/overlap; date-only time of day.
- `CcLockProbe`: JSON fresh/stale, bare PID, malformed, missing file, unreadable file.
- `TagSlug`, rating labels, collision detection.
- `Matcher`: id match, kind mismatch, each fallback, ambiguity, duplicates.
- `DesiredStateBuilder` + `SyncPlanner`: overlay, comparisons at second precision, guard counts,
  tag add/remove sets, unmatched items lose `jcc-*` tags only.

Fixture databases:

- Generate a v38/v3 pair with jClipCorn itself: a throwaway test in `../jClipCorn` based on
  `ClipCornBaseTest.createExampleDB()` (`src/test/de/jClipCorn/test/ClipCornBaseTest.java`,
  data from `testres/example_data_full.jxmlbkp`) with history enabled, written to a folder and
  copied into `Jellyfin.Plugin.CcSync.Tests/Fixtures/`. This gives real DDL and real
  `JCCTRIGGER_AUTOHISTORY_*` triggers.
- Reader tests against it; writer tests on a temp copy: apply pending updates, re-read, assert
  the exact `VIEWED_HISTORY`/`TAGS` text, sparse-row handling, and that the trigger wrote
  `userdata.HISTORY` rows.
- Final check: open a written fixture copy in jClipCorn (`--validate-db` or GUI) and confirm it
  loads with the new entries. Done in phase 6 with a throwaway JUnit test
  (`Fixtures/generator/JellyfinCcSyncReopen.java.txt`): loads, entries present, validator output
  unchanged.

Manual end-to-end (Jellyfin 12.3 in Docker, a copy of the real `ClipCornDB` folder, a small
library with jClipCorn NFOs): first run hits the guard → preview → apply; watch an episode →
pending → written while jClipCorn is "closed"; fake a fresh lock file → write waits; Identify an
item → fallback match; replace-all refresh → tags restored on next run.

**Done with the fixture copy (phases 5–7)** in a `jellyfin/jellyfin:12.3` container with an
NFO-only library of 60 s test clips (5 movies, 1 series, 1 season, 2 episodes): first run →
`guarded` → preview → apply (8 items) → next run `nothing-to-do`; watches of a movie and an
episode → pending → written 15 s later (start time, local zone) → next sync moves
`LastPlayedDate` to the start time once; fresh fake lock → write waits, `cc-locked` once →
lock removed → written, "Watch later" cleared; replace-all refresh drops `jcc-*` tags → next
sync restores them; a movie without `clipcorn` id matched via TMDB. Identify itself was not run
(no internet metadata providers in the test setup); its effect — the `clipcorn` id gone — is the
fallback path above. **Still open:** the same run against a copy of the real `ClipCornDB` folder
and one write over the real share (needs the user).

## 11. Implementation phases

Each phase ends with `make build` + `make test` green and a commit.

1. **Scaffold** — copy watchnotify's skeleton: props (version `0.1.0.0`), slnx, csproj with
   12.3.0 packages and compile-only `Microsoft.Data.Sqlite`, Makefile (+ `test` target),
   `scripts/package.py` with this plugin's constants and zip name, empty `manifest.json`,
   release workflow, `Plugin.cs` with the four pages, empty settings page, logo in the zip.
   *Done when* the plugin loads in Jellyfin 12.3, shows the menu entry, and the publish folder
   contains only `Jellyfin.Plugin.CcSync.dll`.
2. **jClipCorn model** — `ClipCorn/Model/*`, `TagSlug`, unit tests.
3. **jClipCorn access** — `CcFiles`, `CcLockProbe`, `CcSnapshot`, `CcSchema`, `CcReader`;
   fixture DBs; `Probe` endpoint; Settings page jClipCorn section with the test button.
4. **State + history** — `StateDb`, `PendingStore`, `HistoryStore`, `RunStore`; History page and
   endpoints.
5. **Watch detection** — `WatchEntryPoint`, `SessionTracker` (ported), sync-user filter,
   pending creation; Pending page.
6. **Write-back** — `CcBackups`, `CcWriter`, `FlushEngine`, `FlushPendingTask`, debounce;
   writer tests on fixtures incl. trigger rows and jClipCorn re-open check.
7. **jClipCorn → Jellyfin** — `JfIndex`, `Matcher`, `DesiredStateBuilder`, `SyncPlanner`,
   `JfApplier`, `SyncEngine`, `SyncFromClipCornTask`, guards, preview/apply, Status page.
8. **Hardening + release** — activity log mirroring, history retention, README final pass,
   CLAUDE.md update, manual E2E list from §10, `make release VERSION=1.0.0.0`.

## 12. To verify during implementation

- Jellyfin's season NFO parser stores `<uniqueid type="clipcorn">` on Season items (expected,
  `SeasonNfoParser` derives from `BaseNfoParser`; confirm on a real library). **Verified
  (phase 5)** in the 12.3 container with an NFO-only test library: Series, Season and Episode
  items carry `ProviderIds["clipcorn"]` from `tvshow.nfo`, `season.nfo` and the episode NFO.
- How jClipCorn composes the movie NFO `<title>` from `NAME`/`ZYKLUS`/`ZYKLUSNUMBER` (needed for
  the name fallback). **Verified (phase 2):** `<title>` is `NAME` verbatim
  (`MovieNFOWriter.java:39`); `ZYKLUS` + roman `ZYKLUSNUMBER` go to `<originaltitle>`, `ZYKLUS` to
  `<set><name>`. The name fallback compares `NAME`.
- `Microsoft.Data.Sqlite` compile-only reference: confirm no SQLite assemblies or native libs in
  the publish output and that the server's 10.0.12 copy is used at runtime.
  **Publish output verified (phase 1):** the 12.3 server ships `Microsoft.Data.Sqlite.Core`
  10.0.12 (not the `Microsoft.Data.Sqlite` meta package); the plugin references exactly that,
  compile-only, and the publish output holds only `Jellyfin.Plugin.CcSync.dll` (+ pdb/deps.json,
  not packaged). `scripts/package.py` refuses a publish folder with any other dll or native lib.
  **Runtime verified (phase 3):** in the 12.3 container the probe opens snapshot copies
  (`file:…?immutable=1`, `Pooling=False`) through the server's SQLite; nothing else is loaded.
- SQLite writes over the actual mount (cifs without `nobrl`, or NFSv4): run the writer once
  against the real share with jClipCorn closed and re-open in jClipCorn. **Partly verified
  (phase 6):** written through a local bind mount in the 12.3 container and re-opened with
  jClipCorn (see `docs/clipcorn-db.md` § Write recipe). The run against the real share is still
  open and needs the user.
- Jellyfin's NFO parser only imports `<playcount>`/`<lastplayed>` when an NFO user is configured
  (deployment.md recommends none); confirm in `BaseNfoParser`. **Verified (phase 7):** in 12.3 it
  never imports during a refresh, not even with a user set (the provider parses into an item
  without id; `docs/jellyfin-api.md` § NFO user data). Keeping the setting empty still matters:
  the NFO saver writes watch data into jClipCorn's NFOs.
- `SaveUserData` with `Import` on an item the user is currently playing: confirm the session's
  own save afterwards does not resurrect stale values in a way that loops. **Verified (phase 7):**
  start (Jellyfin raises PlayCount 2 → 3), sync during playback (Import resets it to 2), progress
  and stop: the session reads fresh data and keeps 2; the watch becomes a pending update, is
  written after the 15 s debounce, the next sync sets 3 and the start time, the one after has
  nothing to do.
- Release hosting: watchnotify publishes `manifest.json` and zips via its GitHub mirror
  (`raw.githubusercontent.com/Mikescher/jellyfin-watchnotify`). This repo's `origin` is
  `git.blackforestbytes.com/Mikescher/jellyfin-sync`; the plan assumes a GitHub mirror
  with the same scheme. If there is none, the manifest URLs move to
  the Gitea raw URLs and the release workflow to `.gitea/workflows/`.
  **Settled:** `git.blackforestbytes.com` redirects anonymous requests to the login page, so
  Gitea raw URLs cannot serve a Jellyfin repository. The mirror is
  `github.com/Mikescher/jellyfin-cc-sync` (remote `github`); `scripts/package.py` and
  `manifest.json` point at its raw files.

## 13. Later, not in this plan

- jClipCorn tags as `jcc-tag-*` Jellyfin tags.
- jClipCorn score → Jellyfin `UserItemData.Rating`.
- Multiple Jellyfin users ↔ multiple user-data files.
- A built-in SMB backend (only behind the `CcFiles` abstraction; the snapshot/write split would
  stay, with download/upload instead of copy/in-place write).
