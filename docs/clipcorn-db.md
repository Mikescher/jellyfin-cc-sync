# jClipCorn database reference

Everything the plugin needs to know about the jClipCorn database, verified against the
jClipCorn source in `../jClipCorn` (main DB v38, user-data DB v3). Source paths below are
relative to `../jClipCorn/src/main/de/jClipCorn/`.

## Files

All files live in one folder, `<jcc working dir>/ClipCornDB/`:

| File | Content | Plugin access |
|---|---|---|
| `ClipCornDB.db` | Shared, objective data: MOVIES, SERIES, SEASONS, EPISODES, GROUPS, COVERS, INFO, HISTORY (staging), TEMP, STATSNAPSHOTS | read only (snapshot) |
| `ClipCornUserData.db` | Per-user data: viewed history, score, score comment, tags (+ INFO, PROPERTIES, FILTERS, HISTORY staging) | read (snapshot), **write** |
| `ClipCornHistory.db` | jClipCorn's change archive | never |
| `ClipCornDB.db.~lock` | Lock of a running jClipCorn | read only |
| `ClipCornHistory.db.~lock` | Lock of the history DB | read only |
| `cover/` | Cover images | never |

- jClipCorn opens `ClipCornDB.db` as `main` and ATTACHes `ClipCornUserData.db` as `userdata`
  (`database/driver/SQLiteDatabase.java:241-252`). Table names exist in both files.
- There is exactly one user-data file per DB folder, with a fixed name. "Per user" means: in a
  publisher/subscriber setup (`jcc-mirror`) the main DB is replaced wholesale by the mirror,
  while each person keeps their own `ClipCornUserData.db` (`migration/Migration_35_36.java:20-27`).
  **The plugin therefore only ever writes `ClipCornUserData.db`.**
- The pair is bound together: `userdata.INFO.MAINDB_DUUID` must equal
  `main.INFO.DATABASE_UNIVERSALLY_UNIQUE_IDENTIFIER`, otherwise jClipCorn refuses to start
  (`database/CCDatabase.java:1664-1673`). The plugin checks the same before reading or writing.

## Versions

| Key | Supported value |
|---|---|
| `main.INFO.VERSION_DB` | `38` |
| `userdata.INFO.VERSION_DB` | `3` |
| `userdata.INFO.VERSION_MAINDB` | must be `<=` main `VERSION_DB` |

Any other value: reading and writing are refused with a clear status message. A new jClipCorn
schema version needs a plugin update, which is checked against
`../jClipCorn/src/main/de/jClipCorn/database/migration/`.

## Lock file

`util/filesystem/FileLockManager.java`

- Name: DB file path + `.~lock` (`ClipCornDB.db.~lock`, `ClipCornHistory.db.~lock`). There is
  no lock for `ClipCornUserData.db`; the main lock covers it.
- No OS-level locking: existence plus content.
- Content (UTF-8, no BOM), `c` = acquired, `u` = last heartbeat, both `Instant.toString()` of a
  value truncated to seconds (`2026-10-26T18:00:00Z`), `pid` a JSON number:
  ```json
  {"pid": 1234, "c": "2026-10-26T18:00:00Z", "u": "2026-10-26T19:00:00Z", "host": "DESKTOP-X"}
  ```
  Old jClipCorn versions write a bare PID (`219994`).
- jClipCorn rewrites the file every 60 min while running (heartbeat).
- jClipCorn's rule for a lock from another host: held while `u` is younger than 180 min.
- Created at DB open (also in read-only mode), deleted at clean shutdown. A crash leaves it
  behind; jClipCorn then overwrites it once it counts as stale.
- jClipCorn refuses to start while a valid foreign lock is held ("Database already in use").

**Plugin rule** (`CcLockProbe`), evaluated for both lock files:

| Lock content | Held? |
|---|---|
| file absent (only "not found" counts as absent; any other IO error = held) | no |
| JSON with `u` parseable | held while `now - u < LockStaleMinutes` (default 180) |
| bare PID, malformed JSON, unreadable | held while file mtime is younger than `LockStaleMinutes` |

A lock that is present but stale is reported as a warning on the Status page ("jClipCorn was
not shut down cleanly on host X"). The plugin never creates, rewrites or deletes lock files.

## SQLite settings

- Journal mode is the default `DELETE` (rollback journal). WAL is deliberately avoided because
  atomic commits across attached DBs need a rollback journal (`SQLiteDatabase.java:196-198`).
  **Never change `journal_mode`**; it is persisted in the file.
- jClipCorn sets `PRAGMA recursive_triggers = true` (the history triggers rely on it). The
  plugin sets the same on its write connection.
- `foreign_keys` is never enabled; FKs are declared but not enforced.
- jClipCorn keeps one connection for its whole run and writes with explicit BEGIN/COMMIT per
  element. Between writes it holds no SQLite lock — only the `.~lock` file protects the DB.
- jClipCorn never re-reads the DB while running: it rewrites a whole element (main row + user
  row) from memory when that element changes. **A write by the plugin while jClipCorn runs is a
  lost update.** Hence: write only when no lock is held.

## IDs

- Primary key column `ID` (TEXT) in MOVIES, SERIES, SEASONS, EPISODES: a lowercase canonical
  UUID (v7 for new rows; migration-created ids hash in the table name), unique across tables
  in practice (`util/datatypes/CCUUID.java`).
- User rows join on the same `ID`.
- The NFO `<uniqueid type="clipcorn">` carries exactly this value.

## Tables the plugin reads

Read columns **by name**; migrated databases keep older declared types and column order.

Main DB (`database/driver/DatabaseStructure.java`):

| Table | Columns used |
|---|---|
| `MOVIES` | `ID`, `NAME`, `ZYKLUS`, `ZYKLUSNUMBER`, `MOVIEYEAR` (nullable), `ONLINEREF`, `GROUPS`, `ADDDATE` |
| `SERIES` | `ID`, `NAME`, `ONLINEREF`, `GROUPS` |
| `SEASONS` | `ID`, `SERIESID`, `NAME`, `SEASONYEAR` (nullable), `ONLINEREF` |
| `EPISODES` | `ID`, `SEASONID`, `EPISODE`, `NAME` |
| `GROUPS` | `NAME` (PK), `PARENTGROUP` (`''` = none) |
| `INFO` | `IKEY`/`IVALUE`: `VERSION_DB`, `DATABASE_UNIVERSALLY_UNIQUE_IDENTIFIER` |

User-data DB (exact DDL, `migration/Migration_35_36.java:68-79`):

```sql
CREATE TABLE MOVIES   ([ID] TEXT NOT NULL PRIMARY KEY, [VIEWED_HISTORY] TEXT NOT NULL, [TAGS] TEXT NOT NULL, [SCORE] TINYINT NOT NULL, [SCORECOMMENT] TEXT NOT NULL)
CREATE TABLE SERIES   ([ID] TEXT NOT NULL PRIMARY KEY, [TAGS] TEXT NOT NULL, [SCORE] TINYINT NOT NULL, [SCORECOMMENT] TEXT NOT NULL)
CREATE TABLE SEASONS  ([ID] TEXT NOT NULL PRIMARY KEY, [SCORE] TINYINT NOT NULL, [SCORECOMMENT] TEXT NOT NULL)
CREATE TABLE EPISODES ([ID] TEXT NOT NULL PRIMARY KEY, [VIEWED_HISTORY] TEXT NOT NULL, [TAGS] TEXT NOT NULL, [SCORE] TINYINT NOT NULL, [SCORECOMMENT] TEXT NOT NULL)
CREATE TABLE INFO     ([IKEY] TEXT NOT NULL PRIMARY KEY, [IVALUE] TEXT NOT NULL)
```

- **Rows are sparse**: a user row exists only while at least one value differs from its default.
  Defaults: `VIEWED_HISTORY='[]'`, `TAGS='[]'`, `SCORE=6`, `SCORECOMMENT=''`. jClipCorn deletes
  a row that returns to all defaults (`database/CCDatabase.java:976-1063`).
- Reading: `LEFT JOIN` user rows onto main rows; a missing row means all defaults.
- `userdata.INFO` keys: `VERSION_DB`, `MAINDB_DUUID`, `VERSION_MAINDB`, `HISTORY_ENABLED`.

## Viewed history

There is no viewed flag: an element is viewed iff `VIEWED_HISTORY` is non-empty
(`database/databaseElement/CCMovie.java:208-211`, `CCEpisode.java:351-354`). Only movies and
episodes have a history; series/season state is derived in memory.

Format: a compact JSON array of strings (no spaces), sorted (`columnTypes/CCDateTimeList.java`):

| Variant | Encoding | Meaning |
|---|---|---|
| unknown date | `"UNSPECIFIED"` | a real viewing, date unknown; counts in `count()` |
| date only | `"2018-10-21"` | viewed that day, time unknown |
| date + time | `"2018-10-21 16:06:45"` | viewed at that local time |
| read-only variant | `"2018-10-21 16:06:45.123"` | accepted on read; milliseconds dropped on save |

Example: `["UNSPECIFIED","UNSPECIFIED","2016-11-27 16:48:30","2023-11-05 11:00:00"]`.

- Times are local wall-clock time of the jClipCorn machine, no zone stored
  (`util/datetime/CCDateTime.java`). The plugin converts with the configured `ClipCornTimeZone`.
- Sort order: `UNSPECIFIED` first, then chronological; a date-only entry compares **equal** to
  any datetime on the same day (`CCDateTime.java:150-160`).
- `add()` skips an entry that compares equal to an existing one (`CCDateTimeList.java:90-96`):
  an identical datetime, or any datetime on a day that already has a date-only entry (and vice
  versa). Multiple `UNSPECIFIED` entries are allowed.
- **One unparseable entry makes jClipCorn read the whole list as empty**
  (`CCDateTimeList.fromJSONArray`, committed state `826b2511`), and its next save writes `[]`.
  An uncommitted change in the jClipCorn working copy (2026-10-26) makes `fromJSONArray` of every
  JSON list column (history, tags, groups including invalid group names, online refs, …) throw
  instead; either way a bad list is fatal to the history. The plugin's writer must produce only
  `yyyy-MM-dd HH:mm:ss` entries, keep existing entries verbatim, and refuse to touch a row whose
  existing list it cannot parse completely.
- jClipCorn's entry parser (`CCDateTime.createFromSQL`) checks only the length and the
  separators, not digits or ranges (`2016-13-45` passes). The plugin's parser
  (`CcDateTime.TryParse`) is stricter: digits, real calendar dates, `00:00:00`–`23:59:59`, and
  `UNSPECIFIED` in exactly that spelling. Anything it rejects counts as unparseable; jClipCorn
  never writes such entries itself.
- `add()` re-sorts the whole list with Java's stable `Collections.sort`. For a list jClipCorn
  wrote (already sorted) that keeps every existing entry in place and puts the new one behind all
  entries that do not compare greater; the plugin reproduces this with a stable binary insertion
  sort.
- jClipCorn's own entries for "play" are the *start* of playback (`addToViewedHistoryFromUI`
  adds "now" when the player is launched).

## Tags (userdata `TAGS`)

Compact JSON array of ints, sorted ascending, e.g. `[3]` (`columnTypes/CCTagList.java`).
Relevant here: tag **3 = "Watch later"**. jClipCorn removes it when an element is viewed from the
UI: from the movie; for an episode from the episode **and** its series
(`CCMovie.java:281-297`, `CCEpisode.java:281-299`). The plugin mirrors this behind its own
setting `ResetWatchLaterOnWatch` (default on); it does not read jClipCorn's
`PROP_MAINFRAME_AUTOMATICRESETWATCHLATER`.

Full list (not synced to Jellyfin): 0 Low Quality, 1 Missing Time, 2 Broken File, 3 Watch later,
4 Wrong Language, 5 Watch never, 6 CamRip, 7 MicDubbed, 8 Cancelled (series).

## User score (userdata `SCORE`)

On MOVIES, SERIES, SEASONS, EPISODES (`columnTypes/CCUserScore.java:13-20`). The stored value is
**not** the ranking:

| Stored | Enum | Rank | English | German | Tag slug |
|---|---|---|---|---|---|
| 0 | RATING_0 | 1 | Crap | Grauenhaft | `crap` |
| 1 | RATING_I | 2 | Bad | Schlecht | `bad` |
| 2 | RATING_II | 3 | Not recommended | Nicht empfehlenswert | `not-recommended` |
| 7 | RATING_MID | 4 | Average | Mittelmäßig | `average` |
| 3 | RATING_III | 5 | Recommended | Empfehlenswert | `recommended` |
| 4 | RATING_IV | 6 | Favourite | Favorit | `favourite` |
| 5 | RATING_V | 7 | Masterpiece | Kunstwerk | `masterpiece` |
| 6 | RATING_NO | 0 | No Rating | Nicht angegeben | — (no tag) |

## Groups

- Only on movies and series: main `MOVIES.GROUPS` / `SERIES.GROUPS`, a JSON array of names,
  e.g. `["MCU"]` (`columnTypes/CCGroupList.java:136-140`).
- Allowed names: `^[A-Za-z0-9\-_ ]+$`, case-sensitive (`columnTypes/CCGroup.java:9-10`). The
  reader silently drops names that do not validate; the plugin does the same.
- `GROUPS` table: `NAME`, `ORDERING`, `COLOR`, `SERIALIZE`, `PARENTGROUP` (`''` = root, max
  depth 6), `VISIBLE` (cover drawing only).

## Online references

- Main `ONLINEREF` on MOVIES, SERIES, SEASONS; episodes have none.
- JSON array `[{"t":"<type>","id":"<id>"[, "d":"<description>"]}]`; first element is primary
  (`columnTypes/CCOnlineReferenceList.java:144-177`).
- TMDB: `t="tmdb"`, `id` = `movie/<n>` or `tv/<n>`. IMDB: `t="imdb"`, `id` = `tt<n>`.
- Examples: `[{"t":"tmdb","id":"movie/16869"},{"t":"imdb","id":"tt0361748"}]`,
  `[{"t":"tmdb","id":"tv/1399"},{"t":"imdb","id":"tt0944947"}]`.
- For matching, prefer a reference without `d` (description marks secondary references, as the
  web panel's `getFirstRef` does: `../ClipCornWebPanel/src/lib/utils/onlineref.ts:43-81`).
- The NFO writer (`features/nfo/NFOUniqueIdWriter.java`) writes only the **first** reference per
  provider type, in list order and regardless of `d`; Jellyfin keeps the last `<uniqueid>` per
  type, so writing one per type is what makes the NFO's `tmdb`/`imdb` deterministic.
- jClipCorn reads a list with an unknown type (`CCOnlineRefType.parse`) as empty; the plugin
  keeps unknown types and only looks at `tmdb`/`imdb`.

## Change-history triggers

When `userdata.INFO.HISTORY_ENABLED = '1'`, both files contain `JCCTRIGGER_AUTOHISTORY_*`
triggers (`history/CCDatabaseHistory.java`). They are plain SQL stored in the files and fire
for any client, so the plugin's writes to `ClipCornUserData.db` are logged into
`userdata.HISTORY` automatically. jClipCorn drains the staging rows into `ClipCornHistory.db`
15 s after it started, whenever its history view is opened, and at shutdown
(`CCDatabase.syncHistoryToHistoryDb`), so rows the plugin causes wait until the next jClipCorn
start. Verified with the fixture: after a clean shutdown `userdata.HISTORY` is empty.
Inserts/deletes of user rows are logged as `UPDATE` with the default as old/new value. Never
modify or drop the triggers; jClipCorn compares their SQL text.

## Write recipe (one viewed entry)

Executed on `ClipCornUserData.db` alone (no ATTACH needed), inside one `BEGIN IMMEDIATE`
transaction, after `PRAGMA recursive_triggers = true`:

1. `SELECT VIEWED_HISTORY, TAGS FROM MOVIES|EPISODES WHERE ID = ?`.
2. Parse with jClipCorn semantics. Unparseable → fail this update, leave the row untouched.
3. Add the entry (`yyyy-MM-dd HH:mm:ss`, local time) with jClipCorn's `add()` semantics. Skipped
   as equal → the update is a no-op ("duplicate").
4. If `ResetWatchLaterOnWatch`: remove `3` from TAGS of the element; for an episode also from
   its series' user row (`SERIES`, id via main `EPISODES.SEASONID → SEASONS.SERIESID`).
5. Upsert, never `INSERT OR REPLACE` (that would log delete + add):
   ```sql
   INSERT INTO MOVIES (ID, VIEWED_HISTORY, TAGS, SCORE, SCORECOMMENT) VALUES (?, ?, '[]', 6, '')
   ON CONFLICT(ID) DO UPDATE SET VIEWED_HISTORY = excluded.VIEWED_HISTORY
   ```
   (`TAGS` likewise when step 4 changed it.)
6. A series row whose values all returned to defaults after step 4 is deleted, as jClipCorn
   would.

Serialisation: compact JSON (`["a","b"]`), sorted with jClipCorn's comparator, existing entries
byte-for-byte unchanged.

## Side effects the plugin does not reproduce

- NFO files: jClipCorn regenerates an element's NFO only when it changes while jClipCorn runs.
  After a plugin write, `<playcount>`/`<lastplayed>` in the NFO stay stale until jClipCorn
  touches the element. Jellyfin must therefore not import user data from NFOs (see
  `deployment.md`).
- `main.STATSNAPSHOTS` (derived statistics) — recomputed by jClipCorn on its next start.
- jClipCorn's transaction log (`../ClipCornTransactions/*.jsonl`).

## NFO contents (for matching)

jClipCorn writes NFOs (`features/nfo/`), off by default, enabled per installation:

| File | Relevant content |
|---|---|
| movie `<video>.nfo` | `title` = `NAME` verbatim, `originaltitle` = zyklus + roman number (when set), `set/name` = zyklus, `year` = `MOVIEYEAR`, `uniqueid` (imdb, tmdb without `movie/` prefix, …, then `clipcorn`) |
| `tvshow.nfo` | `title`, `year`, `uniqueid` (tmdb/imdb omitted when seasons have differing refs), `clipcorn` |
| `season.nfo` | `seasonnumber`, `uniqueid` incl. `clipcorn` |
| episode `<video>.nfo` | `season`, `episode`, only the `clipcorn` uniqueid |

No `<tag>` element is ever written, so `jcc-*` tags cannot collide with NFO tags. Known jClipCorn
bugs, not handled here: `<userrating>` is computed from the stored score value instead of the
rank, and `<lastplayed>` uses the UI date format.
