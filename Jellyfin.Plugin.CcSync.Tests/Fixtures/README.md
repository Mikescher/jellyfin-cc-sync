# Test fixtures

`v38/` is a jClipCorn database pair (main v38, user data v3, history enabled) written by
jClipCorn itself: `generator/JellyfinCcSyncFixture.java.txt`, run once as a throwaway JUnit test
in `../jClipCorn` (`src/test/de/jClipCorn/test/`, `./gradlew test --tests …`) at commit
`eae57a2a`, then the three `.db` files copied here. It imports `testres/example_data_full.jxmlbkp`,
enables the history triggers and adds the variations the plugin tests rely on:

| Element | Variation |
|---|---|
| Movie "Die Gefährten" | `TAGS` `[3]` (watch later), history `["UNSPECIFIED"]` |
| Movie "Super 8" | history `["2023-11-05 11:00:00"]`, score 3 |
| Movie "Forrest Gump" | refs tmdb `movie/13`, imdb `tt0109830`, imdb `tt0000001` with `d` |
| The six "SpencerHill" movies | no user row at all (unwatched, unrated) |
| Series "Soul Eater" | no user row |
| Group "Tarantino" | parent "Kino" |
| Series "Steins;Gate" | `TAGS` `[3]`, score 4 |
| Its season "01 - ONA" | score 4 |
| Its first episode | `TAGS` `[3]`, history `[]` (row kept for the tag) |
| Its second episode | score 5, history `["2016-06-12","2016-07-09 20:15:00"]` |

Tests copy the files to a temp directory before opening them.
