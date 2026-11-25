# Test fixtures

`v38/` is a jClipCorn database pair (main v38, user data v4, history enabled) written by
jClipCorn itself: `generator/JellyfinCcSyncFixture.java.txt`, run once as a throwaway JUnit test
in `../jClipCorn` (`src/test/de/jClipCorn/test/`, `./gradlew test --tests …`) at commit
`eae57a2a`, then the three `.db` files copied here. It imports `testres/example_data_full.jxmlbkp`,
enables the history triggers and adds the variations the plugin tests rely on.

That jClipCorn commit wrote user data v3. `ClipCornUserData.db` was brought to v4 the way
jClipCorn's migration 3 → 4 does it: every `VIEWED_HISTORY` of `MOVIES` and `EPISODES` rewritten
to the object form with `"c":""` (entry order and `d` strings verbatim), `INFO.VERSION_DB` set to
`4`, and the `HISTORY` staging rows the `JCCTRIGGER_*` triggers wrote for that removed, so the
file looks freshly migrated and cleanly shut down (`HISTORY` empty, triggers unchanged).
`ClipCornDB.db` and `ClipCornHistory.db` are as jClipCorn wrote them. The generator's history
inputs are plain strings, which every jClipCorn version reads; run against a jClipCorn with
user data v4 it writes the v4 form directly.

| Element | Variation |
|---|---|
| Movie "Die Gefährten" | `TAGS` `[3]` (watch later), history `[{"d":"UNSPECIFIED","c":""}]` |
| Movie "Super 8" | history `[{"d":"2023-11-05 11:00:00","c":""}]`, score 3 |
| Movie "Forrest Gump" | refs tmdb `movie/13`, imdb `tt0109830`, imdb `tt0000001` with `d` |
| The six "SpencerHill" movies | no user row at all (unwatched, unrated) |
| Series "Soul Eater" | no user row |
| Group "Tarantino" | parent "Kino" |
| Series "Steins;Gate" | `TAGS` `[3]`, score 4 |
| Its season "01 - ONA" | score 4 |
| Its first episode | `TAGS` `[3]`, history `[]` (row kept for the tag) |
| Its second episode | score 5, history `[{"d":"2016-06-12","c":""},{"d":"2016-07-09 20:15:00","c":""}]` |

No entry in the fixture has a comment; tests that need one (or the old plain-string form) set it
on their temp copy.

Tests copy the files to a temp directory before opening them.
