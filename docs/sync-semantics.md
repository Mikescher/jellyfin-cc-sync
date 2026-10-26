# Sync semantics

The exact rules of both directions. `docs/clipcorn-db.md` describes the data formats this
builds on; `PLAN.md` the components that implement it.

Scope: one Jellyfin user (`SyncUserId`) and one jClipCorn database. Items outside the selected
libraries are ignored entirely.

## Source of truth

**jClipCorn + pending updates.** For every matched item, the Jellyfin state of the sync user is
overwritten with what jClipCorn says, after the not-yet-written pending updates are overlaid on
jClipCorn's viewed history. Changes made in Jellyfin to synced fields (unmark played, favourite
toggle, `jcc-*` tag edits) are reverted by the next sync, except for watches that become pending
updates.

| Data | Direction | Owner |
|---|---|---|
| Viewed (Played, PlayCount, LastPlayedDate) | CC → JF, and JF → CC for new watches | CC + pending |
| Favourite | CC → JF | CC score |
| `jcc-rating-*` tags | CC → JF | CC score |
| `jcc-<group>` tags | CC → JF | CC groups |
| Resume position, audio/subtitle choice, JF user rating, non-`jcc-` tags | untouched | Jellyfin |

## Matching

Key: `ProviderIds["clipcorn"]` (exact key casing) = jClipCorn `ID`. Kinds must agree
(JF Movie ↔ CC movie, Series ↔ series, Season ↔ season, Episode ↔ episode); a kind mismatch is
reported and not synced.

Fallbacks, only for items without a `clipcorn` id:

| Kind | Fallback chain |
|---|---|
| Movie | TMDB (`movie/<n>` ↔ `Tmdb=<n>`) → IMDB (`tt…`) → normalised name + year |
| Series | TMDB (`tv/<n>`) → IMDB → normalised name (+ year of the earliest season, when JF has one) |
| Season | none |
| Episode | none |

- A fallback match counts only if it is unique on both sides; otherwise the item is reported as
  ambiguous and skipped.
- A CC element matched by its `clipcorn` id is not offered to fallback matching again.
- One CC element may match several JF items (same file in two libraries): each JF item is synced
  and the duplication is reported as info.
- Name normalisation: case-folded, diacritics removed, punctuation and whitespace collapsed. The
  movie name compared is `NAME` alone: `MovieNFOWriter` writes it verbatim as `<title>` and puts
  the zyklus only into `<originaltitle>`/`<set>`.
- The Status page lists: unmatched CC elements, JF items without a match, ambiguous fallbacks,
  kind mismatches, fallback-matched items (a hint that their NFO is missing or outdated).

## Viewed state: CC → JF

For each matched movie/episode:

1. `H` = parsed `VIEWED_HISTORY`. An unparseable list skips the item for this run and leaves
   its JF state untouched (jClipCorn itself refuses to load such a database since `16777b66`).
2. Overlay: every pending update for this element with status `pending` is added to `H` with
   jClipCorn's `add()` semantics (an entry equal to an existing one is skipped, exactly as the
   writer will skip it later).
3. Desired Jellyfin values:

| Field | Value |
|---|---|
| `Played` | `H.Count > 0` (`UNSPECIFIED` entries count) |
| `PlayCount` | `H.Count` |
| `LastPlayedDate` | latest dated entry as UTC; `null` if `H` has no dated entry |

Entry → instant:

| Entry | Instant |
|---|---|
| `yyyy-MM-dd HH:mm:ss` | that local time in `ClipCornTimeZone`, converted to UTC |
| `yyyy-MM-dd` | that date at `DateOnlyTimeOfDay` (default `12:00`) local, converted to UTC |
| `UNSPECIFIED` | none (counts for `Played`/`PlayCount` only) |

Local → UTC conversion: a time inside a DST gap is moved forward by the gap; an ambiguous time
(DST end) is taken as standard time. Both are deterministic, so re-syncs are stable.

Comparison with the current Jellyfin row (only differences are written):
`Played` and `PlayCount` exactly; `LastPlayedDate` equal when both are null or both truncate to
the same second. `PlayCount` and `LastPlayedDate` are only compared/written when
`SyncPlayCount` / `SyncLastPlayed` are enabled (default both on).

Effects worth knowing:

- An empty CC history sets `Played=false`, `PlayCount=0`, `LastPlayedDate=null`.
- Partial plays raise Jellyfin's `PlayCount` at playback start; the next sync resets it to the
  CC count.
- After a Jellyfin watch the pending entry carries the playback *start* time, so the next sync
  moves Jellyfin's `LastPlayedDate` from the stop time to the start time once.

## Favourites and tags: CC → JF

Applies to the kinds enabled in `ApplyRatingsTo` (default: movies, series, seasons, episodes).

- `IsFavorite` = CC score ∈ `FavoriteScores` (default `{4 Favourite, 5 Masterpiece}`).
- Rating tag: `jcc-rating-<slug>` for the CC score (table in `clipcorn-db.md`); no tag for
  "No Rating".
- Group tags (movies and series): `jcc-<slug(group)>` for every group of the element; with
  `GroupTagsIncludeParents` also for every ancestor group.
- Slug: lower-case invariant, whitespace runs → `-`, `-` runs collapsed, leading/trailing `-`
  trimmed (`Star Wars` → `star-wars`, `MCU` → `mcu`). Group names are restricted to
  `[A-Za-z0-9\-_ ]` by jClipCorn, so nothing else needs escaping.
- Collisions (two groups with the same slug, or a group slug starting with `rating-`) are merged
  into one tag and reported as a warning.

Tag ownership: **every tag starting with `jcc-` (case-insensitive) belongs to the plugin**, on
every Movie/Series/Season/Episode in the selected libraries. Per item, desired set `D` vs actual
`jcc-*` set `A` (case-insensitive): add `D \ A`, remove `A \ D`. Other tags are never touched.
JF items without a CC match get `D = ∅`, so stale `jcc-*` tags disappear when an element is
deleted from jClipCorn or loses its group/score.

Unmatched items keep their user data (played, favourite) unchanged.

## Watches: JF → CC

### Detection

Same rules as jellyfin-watchnotify, restricted to the sync user and to movies/episodes:

- `PlaybackStopped` with `PlayedToCompletion` or `position / runtime >= WatchedThreshold` (0.90).
  Jellyfin sets `PlayedToCompletion` on every stop of an item shorter than the server's minimum
  resume duration (Dashboard → Playback → Resume, default 300 s), so such items count as watched
  whenever they are stopped (seen with 60 s test clips).
- `UserDataSaved` with `PlaybackFinished` and `Played=true`, delayed 10 s, as fallback for
  clients that under-report the stop.
- `UserDataSaved` with `TogglePlayed` and `Played=true` only if `ManualMarkCreatesPending`
  (default **off**); watched-at = now.
- Duplicate suppression per item within `DedupMinutes` (default 60), shared by all paths.
- `UserDataSaved` with `Import` (the plugin's own writes, NFO imports) is ignored.

Watched-at for playback = **start** of the watch (first `PlaybackStart` of the session, kept
across seeks/pauses within `StartCoalesceMinutes`); if no start was seen (server restart),
`stop - position`.

### Pending update

Created for every detected watch, before any attempt to write:

| Field | Content |
|---|---|
| `JfItemId`, `ItemName`, `ItemKind` | the Jellyfin item |
| `ClipCornId` | `ProviderIds["clipcorn"]` if present, else resolved at write time via the fallbacks (movies only — episodes without id fail as unmatched) |
| `MatchKeys` | provider ids, name, year captured at creation (the item may be re-identified later) |
| `WatchedAtUtc` | see above, truncated to seconds |
| `Source` | `playback`, `playback-fallback`, `manual` |
| `Status` | `pending` → `written` / `duplicate` / `failed`; `discarded` by hand |

The CC entry is rendered at write time as `WatchedAtUtc` in `ClipCornTimeZone`,
`yyyy-MM-dd HH:mm:ss`.

### Writing

Attempted 15 s after a watch (debounce) and by the 5-minute task while pending updates exist:

1. jClipCorn lock held (`ClipCornDB.db.~lock` or `ClipCornHistory.db.~lock`, rule in
   `clipcorn-db.md`) or a `-journal`/`-wal` file next to any of the three DBs → wait; Status
   shows who holds it. Waiting counts no attempt; `cc-locked` is recorded once per holder (and a
   configuration problem once per problem), not on every 5-minute retry.
2. Snapshot-read the main DB for versions, DUUID, element existence, episode → series ids and
   fallback resolution.
3. Back up `ClipCornUserData.db` into the plugin's backup folder (rotating, `BackupCount`).
4. Probe the locks again, then open `ClipCornUserData.db` read-write and apply all pending
   updates in one transaction (recipe in `clipcorn-db.md`); probe once more right before
   `COMMIT` and roll back if a lock appeared.
5. Probe the locks again; a lock that appeared during the write is logged as a warning.

Movies without a `clipcorn` id are resolved at write time with the movie fallback chain on the
jClipCorn side (unique match required); the resolved id is stored on the pending update.

Outcomes per update:

| Outcome | Status | Retry |
|---|---|---|
| entry added | `written` | — |
| equal entry already present | `duplicate` | — |
| element missing in CC, kind mismatch, unmatched episode, unparseable existing history | `failed` | manual retry from the Pending page |
| IO error, SQLite busy, version/DUUID mismatch, lock appeared before commit | stays `pending`, attempt counted | next run |

`WriteBackEnabled=false` stops creating pending updates; Jellyfin watches are then reverted by
the next CC → JF sync.

## Concurrency

- One gate (`SemaphoreSlim`) serialises CC → JF runs and pending writes. Watch detection never
  waits for it.
- The CC → JF plan is computed from a pending snapshot taken inside the gate. Before applying an
  item's user data, the run checks whether a pending update for that item was created after the
  snapshot; if so the item is skipped this run (the next run includes it).
- Because writes also run inside the gate, a pending update cannot move from `pending` to
  `written` between the snapshot and the apply.

## Safety rails

- The CC read is all-or-nothing: snapshot stability checks, versions, DUUID and
  `PRAGMA quick_check` must pass and every table must be read to the end, otherwise the run
  aborts without touching Jellyfin. A copy counts as stable when no `-journal`/`-wal` file
  existed before or after it and every source kept its size, mtime and SQLite header "file
  change counter" (offset 24, incremented by every commit in rollback-journal mode) while it was
  copied; three attempts 2 s apart, then the run aborts. A single unparseable JSON column (history, groups, refs)
  only skips that element and is reported.
- A CC library without any movie and episode aborts the run.
- Change guard: a run that would change more than `MaxChangesPerRun` items (default 100) or
  un-watch more than `MaxUnwatchPerRun` items (default 20) stops before applying and stores the
  plan as a preview. The Status page shows it; "Apply" runs it once with the guard lifted. The
  first run after setup always hits the guard.
- `DryRun` computes and stores the plan, writes history entries of kind `planned-*`, applies
  nothing.

## History

Every applied change and every pending lifecycle step is recorded (`State/HistoryStore`), kept
for `HistoryRetentionDays` (default 0 = forever):

| Kind | Recorded for |
|---|---|
| `jf-played` | `Played`/`PlayCount`/`LastPlayedDate` changed (old → new) |
| `jf-favorite` | `IsFavorite` changed |
| `jf-tag-added`, `jf-tag-removed` | one entry per tag |
| `pending-created` | detected watch (source, watched-at) |
| `pending-written`, `pending-duplicate` | write outcome, with the CC entry text and whether "watch later" was removed |
| `pending-failed`, `pending-discarded`, `pending-retry` | failures and manual actions |
| `cc-locked` | first time a write had to wait for a given lock holder |
| `run-sync`, `run-flush` | one summary per run (counts, duration, outcome, abort reason) |
| `planned-*` | dry-run and guard previews |

Consecutive identical failures of the same pending update are recorded once.
