# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Mootify is a self-hosted music player for a small group of people who share one music library
on disk. It plays what's already there, and when somebody wants something that isn't, it asks
Lidarr to fetch it and drops the result into the playlist they were looking at.

There are two clients: the Blazor website in `src/Mootify`, and an Android app in `android/` that
exists mainly to be an Android Auto media app. They share one server, one library and one set of
accounts — see **The Android app and its API** below.

`Plan/Plan.md` is the design document. It carries the reasoning behind most of the decisions
below — read it before changing anything architectural.

## Commands

```bash
dotnet run --project src/Mootify        # http://localhost:5199 (or the launchSettings port)
dotnet build                            # whole solution
dotnet test                             # 635 tests, ~4s
dotnet test --filter "FullyQualifiedName~PlaylistServiceTests"     # one class
dotnet test --filter "DisplayName~Adding_the_same_request_twice"   # one test

# The Android client. Builds with Android Studio's own JDK; from a terminal:
#   export JAVA_HOME="/c/Program Files/Android/Android Studio/jbr"
cd android && ./gradlew assembleDebug
```

The Android toolchain is pinned **current** rather than conservative, and that isn't a preference:
Studio ships a JDK 25 runtime, Gradle refuses to start on it before 9.x, AGP 9 follows from Gradle 9,
and `compileSdk 37` follows from the AndroidX versions. `android/README.md` lists the three AGP 9
gotchas (no `kotlin.android` plugin, `resValues` off by default, `kotlinOptions` removed).

Production server: **moo.lazy.kiwi**. That's the URL the Android build defaults to
(`mootifyServerUrl` in `android/gradle.properties`).

Config lives in `src/Mootify/mootify.json` (gitignored — it holds the Lidarr API key and any
share password). `mootify.example.json` is the committed template. Environment variables
override it (`Lidarr__ApiKey`, `Library__Password`, …) — but only because `Program.cs` re-adds
the environment provider *after* the JSON file. Configuration is last-wins, and the default
builder registers env vars before that file, so removing that line silently makes the file beat
your secrets.

A UNC share that needs a login is handled by `NetworkShareConnector` (`Library:Username` /
`Password` / `Domain`). It opens an SMB session with `WNetAddConnection2`, which Windows scopes
to the logon session — so every later file read on that path just works and the scanner needs
to know nothing about it. It reconnects before each scan, so a NAS reboot heals itself.
**Windows-only**: on Linux the mount belongs to the OS (fstab or a Docker volume), and the
connector says so rather than failing silently.

There are no EF migrations yet: startup calls `EnsureCreated()`, which only ever builds an empty
file. Two kinds of addition to `Data/Entities.cs` can reach an existing install at boot by being
listed in `Data/SchemaPatch.cs`: a **brand-new table** (`AddedTables` — that's how `ApiTokens`,
`ListeningSessions` and `Ideas` arrived) and a **new column appended to an existing one**
(`AddedColumns` — that's how `Users.MustChangePassword`, `Requests.LastSearchAt` and
`Preferences.ShareListening` did — note that `LastSearchAt`'s DDL says `INTEGER`, because the
`DateTimeOffset` convention below stores a converted long, not text). Both are guarded
by a `sqlite_master` / `pragma_table_info` check,
so they're idempotent and cost nothing on a current database. `SchemaPatchTests` is the only test
that runs against the *old* shape, because every other fixture starts from a schema that already
has everything.

It is still a stopgap, not a migration system: no version table, no down path, and nothing that
can **change** a column that already exists. **Any other change to `Data/Entities.cs` still means
deleting `src/Mootify/data/mootify.db`.** Add real migrations before the next one.

## Architecture

### Interactivity is global, and that is load-bearing

`App.razor` sets the render mode on `<Routes>` rather than per page. The `<audio>` element
lives in `MainLayout` and must never unmount — per-page render modes would make each page its
own island, re-rendering the layout and killing playback on every navigation. The login page
opts out with `[ExcludeFromInteractiveRouting]` so it can write the auth cookie during static
SSR, before the response starts.

### Two audio elements, never one

`#mootify-audio` (music, driven by `wwwroot/js/player.js`) and `#mootify-cowbell`
(notifications, driven by `wwwroot/js/notifications.js`). Playing the cowbell through the music
element stops the song. When a notification fires while music plays, `player.js duck()` ramps
the music down and back rather than pausing it.

`PlayerService` is the single owner of playback state; the play bar and every in-list play
button render from it. Do not let a component keep its own `isPlaying` flag.

That extends to what a control *does*, not just how it looks. `TrackList`'s row button showed a
pause icon on the current track and then restarted the song, because the icon asked
`Player.Current?.Id == row.TrackId` and the click handler didn't ask at all — two expressions of
one idea, and only one of them was maintained. Both now go through `PlayerService.IsCurrent` and
`PlayerService.PressRowAsync`: pressing the row you are listening to pauses it, pressing any other
starts the list from there. `PlayerController.tap` is the Android side of the same rule. A
double-click on a row always starts it — there is no icon under the cursor claiming otherwise —
which is why the button stops that event from reaching the row.

**Shuffle is a permutation, not a coin toss.** `_order` is a seeded Fisher-Yates shuffle of
indices into `_queue`, generated once and then walked — which is what gives no repeats until
the queue is exhausted, and what makes the queue drawer (`Components/Layout/QueuePanel.razor`)
able to show a truthful "up next" while shuffle is on. Everything that reads ahead must walk `_order`,
never `_queue`; the two are different lists exactly when somebody wants to look. The slice is
`PlayerService.UpNextPositions`, kept pure so `RepeatMode.All` wrapping back to the top and
stopping one short of the current track has tests rather than a drawer somebody eyeballs once.

### The request pipeline is ordered, and the order is the design

```
Lidarr import → transcode non-MP3 → targeted rescan → match tracks → append to playlist → notify
```

`Services/Requests/RequestReconciler.cs`. Transcoding happens *before* the rescan, so a file
in a format the library can't index never becomes a `Track` row and therefore can never reach
a playlist or the player — the invariant is enforced by sequencing, not by checks in the UI.
That set is `Transcoder.ConvertibleExtensions`, and **FLAC is deliberately not in it**: the
scanner indexes FLAC natively (`LibraryScanner.IndexedExtensions`) and a browser that can't
decode one is served a cached MP3 from `/media/{id}/mp3`, rather than the library losing the
original. Appending before the rescan finds nothing, intermittently, and only on a fast disk.

Polling is the source of truth; a webhook (not yet built) would only be the fast path. The
reconciler is idempotent — `PlaylistItem.RequestId` makes the append safe to run twice, which
it will be.

**Completing a request lives in `RequestFulfiller`, not in the reconciler**, because Lidarr is
no longer the only way music arrives — a file dropped into the import folder satisfies a
request just as completely (see **The import drop folder** below). Appending to the target
playlist, flipping the row to `Available` and notifying the requester and their team are one
operation, and two code paths that each decide separately what "done" means is how one of them
ends up not notifying anybody.

**A pass works albums, not requests, and re-searching is part of it.** Both halves of that came
from the same 504-request import:

- **Grouping.** 504 rows point at maybe 180 releases, and Lidarr fetches releases. One
  `GetAlbumAsync`, one track-file listing, one transcode and one rescan per release; only the
  track match, the playlist append and the cowbell are per request. Ungrouped it was 504 HTTP
  calls to learn 180 facts, and a pass took longer than the interval between passes.
- **Re-searching.** `AddAlbumAsync` searches once, when the request is made. A search that comes
  back empty — which is most of them when several hundred land on somebody's indexers at once —
  **leaves no trace anywhere in Lidarr**. The grabs that worked appear in the queue; the rest sat
  at "Searching" with nothing in the system that would ever ask again. `Request.LastSearchAt` and
  `SearchAttempts` are what make "never asked" distinguishable from "asked an hour ago", and
  `RequestReconciler.DueForSearch` backs off (0, 30m, 2h, 6h, then daily) to eight attempts before
  the seven-day timeout calls it. Searches are batched into one `AlbumSearch` command — Lidarr
  runs commands in series — and capped at `Lidarr:MaxSearchesPerPass`, oldest-asked first, so a
  low cap is slower rather than incomplete.

`GetQueueAsync` pages through the whole queue. It used to read page one of 200, and a request
whose queue record fell off that page is indistinguishable from one Lidarr never grabbed — which
would now get it re-searched while it is already downloading.

**Creating a request reuses the Lidarr ids of one already made for the same release.** Five songs
off one album are five `Request` rows but one album Lidarr needs to hear about; re-adding cost a
full artist list and another `AlbumSearch` each time. Rows that ride in on somebody else's add get
`LastSearchAt = null`, which is exactly what tells the reconciler to search them on its next pass.

**What counts as a duplicate lives in one function**, `RequestService.DuplicateKey`: recording MBID
where MusicBrainz gave us one and normalised title otherwise, scoped to the target playlist — or
to the requester when there is no playlist, since without a shared destination there is nothing to
collide with. `SearchPage` greys out what's already spoken for using the same function, because a
UI and a service that disagree about duplicates is worse than no check at all.

The list is paged with the counts for the whole thing (`RequestPage`, `RequestCounts`), not capped
at 50 — after an import the 450 rows a cap hides are the ones worth seeing. Rows can be cancelled
(`CancelAsync`, `DELETE /api/v1/requests/{id}`), which deletes rather than marking: nothing holds a
foreign key to a `Request`, so music that already landed keeps its playlist entry and its cowbell.
Cancelling unmonitors the album in Lidarr only once the last request for that release is gone.

Lidarr cannot fetch a single track, and has no song index at all: `album/lookup` returns a
track *count* with no titles, `/track` only answers for albums already in the library, and the
universal search returns artists and albums. So requests are always "this album, keep one
track" — `Components/Pages/SearchPage.razor` searches albums via Lidarr, expands one to its
tracklist via `MusicBrainzClient`, and stores the chosen recording MBID on the `Request`.

**Don't replace this with song-title search.** It was tried against the live API and the data
doesn't support it: MusicBrainz ranks by text match with no popularity signal, so
`bohemian rhapsody` returns seven tribute bands and no Queen. Scoping to a detected artist fixes
relevance but needs a further call per result at 1 req/s. The reasoning is in `Plan/Plan.md`.

### Importing playlists

`/import` reads an Exportify CSV: the file name becomes the playlist name, matches go straight
in, and the rest can be requested into it. The destination can instead be a playlist that
already exists — personal or any team playlist the user can see, which is the same thing as
being able to write to one. `AppendAsync` skips songs the target already has: adding a song
twice by hand is deliberate, but an import is a bulk action nobody reviews row by row, so
re-importing an export you already merged would otherwise double every song in it. Either way
the chosen playlist is what the missing-song requests then land in.

The matching is the whole job (`PlaylistImportService.Normalize`). Spotify titles carry
decoration the files don't — `(2011 Remaster)`, `- Radio Edit`, `feat. X` — so an exact
comparison finds almost nothing. Both sides are normalised (parentheticals and trailing
dash-suffixes stripped, accents folded, punctuation dropped), matched on title+artist first,
then on title with duration as the tiebreaker within 5s. Measured against a real 661-row export:
51 matched where only 58 rows had a title in the library at all, and the misses were
same-title-different-artist covers, correctly rejected.

Requesting the missing ones is capped per import (`MaxRequests`) and deliberately a second,
explicit click — every missing song means fetching a whole album, and 600 of them would fill
a disk.

### Library search is one rule in one place

`LibraryMatch` (in `Services/Library/LibrarySearch.cs`) owns what a search term matches, and
everything goes through it: the library page, the search box, and the API. There used to be three
answers to one question — the library page filtered **artist names only** (in memory, over every
artist), the search box matched title/artist/album, and `LibraryQueries` matched track titles
only. So "nevermind" found songs on one page, nothing on another, and nothing in the car.

The distinction worth keeping is `TrackTitle` vs `TrackAnywhere`. **Filtering** a list already
scoped to an album or artist matches the title only — matching the album title there would match
every track on it the moment somebody typed the album's name. **Searching**, with one box and no
scope, matches title, artist and album, because "play nevermind" has to reach the songs on it.

**On speed.** Every pattern is `%term%`, which no B-tree index can serve — SQLite scans, and an
index on `Title` would not change that. What keeps it cheap is the shape, not an index:

- the scan is over a *projection* with a `LIMIT`, never over materialised entities;
- the joins are primary-key lookups, which is what makes 40,000 rows milliseconds;
- callers debounce (250ms) and cancel in flight, so typing is one query rather than one per key;
- the total is only counted when the page didn't already answer it — `COUNT` over a `LIKE` is a
  second full scan with no early exit, and on the first page of a short result the answer is just
  how many came back.

The library page is paged and server-side for the same reason it needed to be: it used to load
every artist into the circuit before anybody typed anything, which is fine at 200 artists and
wrong at 2,000. It searches artists, albums and songs as three tabs — and a term that leaves the
open tab empty moves to the first tab that has results, because landing on "Artists (0)" next to
"Songs (12)" looks exactly like a search that failed.

**The ceiling is real and is worth knowing before optimising the wrong thing.** Somewhere in the
low hundreds of thousands of tracks a scan per keystroke stops being free, and the answer then is
SQLite's FTS5 — a virtual table kept in step by the scanner, queried with `MATCH`. That is
triggers and raw SQL, and it is not worth carrying at this size. If searching starts to feel slow,
that is the thing to build, and `LibraryMatch` is what it replaces.

### The import drop folder

`<MusicRoot>/import` (`Library:ImportFolder`) is a mailbox: drop anything in it and
`LibraryFiler` files it under `Artist/Album`, `Artist/` when only the artist is known, and
`Library:UnsortedFolder` (`generic/`) when neither is. It runs at the start of every
`ScanAllAsync`, which is every scan trigger there is — startup, the six-hourly timer, the
watcher debounce and the admin's "Rescan library".

**The order is load-bearing, twice over.**

- **File, then index.** Indexing first means the file is indexed where it landed *and* again
  where it went, and the first row goes absent on the following pass, taking any playlist entry
  made in between with it. The drop folder is therefore excluded from indexing outright
  (it is in `LibraryFiler.NotLibrary`) — what's left in it after a pass is a file still being
  copied, and a half-written MP3 has a plausible size and unreadable tags, which is exactly the
  shape of a `Track` row nobody wants. "Still being copied" is decided by opening it with
  `FileShare.None`, not by a timer.
- **Follow what was already indexed.** A drop folder that predates the filer has been indexed
  like anywhere else, so its tracks are in playlists. `RepointMovedTracksAsync` rewrites
  `Track.Path` for rows the filer moved instead of letting the scan mark the old path absent
  and add the new one as a stranger — same row, same id, so every playlist entry, play count
  and playback position on it survives. `Track.Path` is unique, so a destination that somehow
  already has a row is left for the scan to sort out the ordinary way.
- **Index, then match requests.** `ImportRequestMatcher` completes any open request the new
  files satisfy, and it can only do that after the `Track` rows exist. It runs through
  `RequestFulfiller`, so a hand-fetched file appends to the same playlist and fires the same
  cowbell as one Lidarr found — `NotFound` and `Failed` rows are matched too, since those are
  precisely the ones somebody gives up on Lidarr for and fetches themselves.

**A match must be seeded by a file that just arrived.** The candidate query is by destination
folder (`StartsWith`, which SQLite matches case-insensitively — the exact-path filter then
happens in memory, where the comparison is ours), so dropping one file into an existing artist
folder pulls that artist's whole catalogue back. Without that rule an album that has been
sitting there for a year would close a request made yesterday. Matching itself reuses
`PlaylistImportService.Normalize` — same problem, same normaliser, because two opinions about
what counts as the same song is worse than one imperfect one — and requires the **artist** to
agree, since a title alone matches covers and closing somebody's request with the wrong
recording is worse than leaving it open.

**A successful pass over an already-indexed drop folder reports `+0 ~0 -0`, and that is correct.**
Repointing happens before the scan, so the scan finds each file at its new path with an unchanged
fingerprint and does nothing — the files moved, the library didn't change. Reading the scan report
as "the filer didn't run" is the obvious mistake, which is why `FilingOutcome` distinguishes
`Disabled` / `NotFound` / `NothingToDo` / `Filed`, why every pass logs the folder and file counts
it walked, and why `/admin` prints all four differently. Four situations that produce zero filed
files and one word for all of them is a feature nobody can tell is working.

Three things the filer will not do, each because the failure is unrecoverable: it never
overwrites (a collision becomes `song (2).mp3` — two files can honestly both be
`01 - Intro.mp3`), it never deletes, and it **never guesses an artist from a folder name**. A
folder called "New stuff" would become an artist in the library; the album falls back to the
folder name because the scanner already makes exactly that inference, but the artist comes from
tags or not at all. Cover art travels with a folder only when every music file in it filed to
the same place — `AlbumArtService` reads an adjacent `cover.jpg` off disk, so a cover left
behind is a cover lost, and one moved to the wrong album is worse.

### Organizing what's already there

`/admin/organize` (`LibraryOrganizer`) repairs names the scanner has already stored and collapses
the same song stored twice. It exists because a real library arrives looking like this:

```
09. Elton John     — 1 album, 1 song
12. Shocking Blue  — 1 album, 1 song
The Black Eyed Peas / Black Eyed Peas — the same band, two rows, a screen apart
```

**The first of those is not the folder layout leaking in.** `Track.ArtistId` comes from the tag
and from nowhere else — `LibraryFiler` deliberately never guesses an artist from a folder name,
and the scanner's only folder inference is the *album* (see `AlbumFromFolder`). A numbered artist
is a numbered tag, which is what a compilation ripped with each track's position in the artist
field looks like. Every one of those is its own `Artist` row with one album and one song under it,
which is what turns the library page into a wall.

**`LibraryNaming` owns the rule and the scanner applies it too**, which is what makes this a repair
rather than a chore somebody redoes weekly: the organizer fixes the rows, and every file that
arrives afterwards is read the same way, so nothing puts the mess back. `LibraryScanner`'s artist
cache is keyed by `LibraryNaming.ArtistKey`, not by the raw tag, so a file tagged "Black Eyed Peas"
joins the existing "The Black Eyed Peas" instead of founding a second row beside it.

**Almost all of `StripIndexPrefix` is a rule about what *not* to strip**, because the failure is
silent and permanent — "3 Doors Down" filed under "Doors Down" is an artist nobody finds again.
So: at most three digits; a separator is required and a bare space only counts when the number is
zero-padded ("09 Elton John" yes, "10 Years" no); whitespace has to appear in the separator unless
it's a dot or underscore, which keeps "5-Star" and "3-11 Porter" whole; and what's left has to
start with a letter, which is what saves "10.000 Maniacs" from becoming "000 Maniacs". It is
one pass, deliberately: catching "01 - 09. Elton John" means letting the strip recurse past a
remainder starting with a digit, and that guard is worth more than the doubled prefix is.

`ArtistKey` folds further than the display name does — `PlaylistImportService.Normalize` (case,
accents, punctuation) plus a leading "The". Only "The": "A Perfect Circle" is a name, and there is
no habit of dropping that the way there is with the article. A name that normalises to nothing
falls back to itself, because `!!!` is a band and otherwise every all-punctuation name would share
one key.

**Albums are folded far more conservatively than artists** — the track number and case, nothing
else. There is no album-shaped bloat to justify the risk of merging "Live" into "Live at Leeds".
That pass still has to run whenever artists merged, because two artists that became one can each
own a *Greatest Hits* and `(ArtistId, Title)` is unique.

**Duplicates are `Normalize(title)` + artist + running time within five seconds**, the same
normaliser the import matcher uses. The tolerance is the whole safeguard: a live and a studio
version share a title and an artist, and almost never share a length. The survivor is present over
absent, then bitrate (which carries FLAC above MP3 without knowing about formats), then file size,
then `AddedAt`.

**Four things about the pass are load-bearing.**

- **It is previewed first.** Merging artists isn't undoable and the interesting failure — two bands
  that were never the same band — is only ever visible as a pair of names somebody reads. So
  `PreviewAsync` and the apply build the *same* plan from the same read-only snapshot, and the page
  shows merges first.
- **The duplicate file is moved to `<MusicRoot>/duplicates`, and that move is what makes the merge
  stick.** Delete only the row and the next scan finds the file where it always was and indexes it
  straight back in as a brand-new duplicate. It is never a delete, because the pass can be wrong.
  **If the move fails the row is kept**, so the database still describes what is on the disk.
- **Everything pointing at a merged-away track is repointed, not dropped** — playlist entries, play
  history, listening broadcasts, and the ids inside `PlaybackState.QueueJson`. A tidy-up that
  empties a playlist is worse than the mess it tidied. The queue is remapped in place rather than
  deduplicated, so `QueueIndex` still means what it meant. Playlist entries that would become the
  same song twice in one list are dropped, keeping the earlier `SortKey`.
- **Merge before rename, albums before artists.** The apply order is one long argument with two
  unique indexes and two cascading foreign keys. `(Albums.ArtistId, Title)` is unique, so moving
  Cowbells' *Greatest Hits* onto The Cowbells before the two *Greatest Hits* rows have been merged
  fails; collapsing albums first leaves at most one album per title under each artist, which is
  what makes the move safe. `Artists.Name` is unique for the reason renaming comes last. And
  nothing is deleted before what hangs off it has moved.

Empty artists and albums are cleared at the end, recomputed against the database rather than taken
from the plan — by then the plan is several saves old, and the debris that was already there is
worth clearing too.

**Which folders are not the library is one list in one place**, `LibraryFiler.NotLibrary`: the drop
folder (still being copied into) and the duplicates folder (deliberately taken out). Three separate
things walk that tree — `LibraryScanner.EnumerateAudioFiles`, the `FileSystemWatcher` in
`LibraryScanService`, and `LibraryTranscodeService.FindConvertible` — and a quarantined file comes
straight back the moment one of them disagrees. The watcher skips only the *duplicates* folder: a
drop has to wake it, and an organize pass moving 400 files must not.

**And nothing may scan while a pass runs.** `LibraryScanner.Suspend()` is held for the length of
one, and a suspended scan is **refused rather than queued** — every caller is a timer, a watcher or
a button, and all three would rather come back in a minute than block. The check is at the top of
`ScanAllAsync`, before the filer, because filing writes `Track.Path` on rows the pass may be about
to merge away. Without it, a scan caught mid-merge saves tracks pointing at an artist that stopped
existing halfway through.

### Playlists are read a page at a time

`PlaylistService.GetPageAsync` is how a playlist is read; `GetAsync` (the whole entity graph) is
for editing one row, not for showing a long list. The difference stopped being academic at 200
tracks: `GetAsync` loads every item joined to its track, artist and album as tracked objects, and
the API then re-queried the same tracks to serialise them — so browsing a playlist on the phone
was slow and browsing one **in the car was slow once per page**, because `MootifyLibraryService`
fetched the whole thing and sliced it locally for every twenty rows the head unit drew.

Three things came out of that and all three matter:

- **`PlaylistTrackRow` carries the union of what both clients need** — the website wants a title,
  an artist and a duration; the API additionally wants the ids, numbers and bitrate that make an
  `ApiTrack`. Projecting the union once is what removes the second query.
- **Totals describe the playlist, not the page.** `PlaylistTrackPage.Total` and `TotalDuration`
  are counted over the whole list, because a pager that can only count what it fetched can't say
  "page 2 of 9" and a header computed from a page says "10 tracks" about a list of 200.
- **"Play" doesn't page.** `GetTrackIdsAsync` returns every present track id in order, in one
  column of GUIDs — a queue needs all of the list and none of the metadata.

On the wire, `ApiPlaylistDetail.Items` is an `ApiPage<ApiPlaylistItem>` rather than an array, and
`/playlists/{id}/items` and `/playlists/{id}/trackids` exist for the pages after the first and for
building a queue. That shape change is why **`ApiMap.Version` is 2**: a client built against the
old array fails to parse rather than silently showing the first hundred as though they were all
of them. The Android app pages the browse tree from the server and fills the phone screen's list
in behind the first page (`MootifyViewModel.PlaylistUi.complete` gates Play and Shuffle, so
neither can queue a prefix).

### Listening along

A playlist page shows who else is playing that playlist and what they're on. It is deliberately
**not** called syncing: nothing is synchronised, nobody's playback follows anybody else's, and
turning it on hands over no control. It publishes one fact — "I am on track 7 of this list" — to
people who can already see the list.

`Services/Playlists/ListeningService.cs`. Three things decide whether a row exists or is visible:

- **The broadcaster opted in.** `UserPreference.ShareListening` is off by default and is
  **per account, not per device** — a phone that kept broadcasting after the website was told to
  stop is the bug that shape prevents. Every client reports which playlist it is playing from and
  the server decides; the client never interprets the switch. Turning it off *deletes* the row
  rather than hiding it.
- **The broadcaster can still read the playlist**, checked through `PlaylistService` like every
  other playlist read, so leaving a team stops the broadcast at the next heartbeat.
- **The reader can read it too**, and so can everyone in the list they get back — a team playlist
  only ever lists current members, re-checked on read rather than trusted from when the row was
  written. A playlist the reader can't see answers *empty*, the same as one nobody is on, so this
  can't be used to discover that somebody else's list exists. The viewer is left out of their own
  list; the toggle already tells them whether they're sharing.

**Liveness is a timestamp, not a teardown.** A closed tab, a phone in a tunnel and a killed
process all fail to say goodbye, so a session counts as live only while `UpdatedAt` is inside
`ListeningService.StaleAfter` (2 minutes) and clients re-stamp while they play. Nothing has to be
cleaned up on a schedule.

**One call, one timer.** The heartbeat rides on the playback save that already happens every 20
seconds — `SavePlaybackRequest` gained `SourcePlaylistId` and `IsPlaying`, and the Android media
id already encodes which list a track was browsed from. On the website `PlayerService.BroadcastAsync`
throttles itself to the same interval, because `OnTimeUpdate` fires several times a second and a
write per tick is thousands of updates an hour against the file that also serves the website.
`PlaylistPage` polls for other people's rows every 10 seconds: their playback happens in other
processes, so there is nothing in this circuit to subscribe to.

### Uploading music from the browser

It lives inside `/import` rather than beside it: "get music in" is one job with two shapes — a
Spotify CSV or the files themselves — and two menu entries for it was clutter. `/upload` is kept
as a second route on the same page that opens on that tab, so the half is still linkable.

**Both tabs stay mounted and the inactive one is hidden**, rather than the usual `@if`. Either
side can have work in flight — a CSV being read, an album being uploaded — and a tab click that
unmounted the component doing it would throw the result away while the work carried on regardless.


The **Music files** tab on `/import` (`TrackUpload` + `TrackUploadService`) is the drop folder with
a front door on it, and it runs the same
sequence for the same reasons: **file, then index, then match requests**. Indexing before filing
indexes the temp copy; matching before indexing has no `Track` rows to match against. The naming
rules are `LibraryFiler`'s — called, not re-implemented, because two opinions about where a file
belongs is how a library splits in half.

**The one rule that is not the drop folder's: an upload must be tagged.** The filer puts an
untagged file in the catch-all folder, which is right for a mailbox somebody works through and
wrong here — the person is standing in front of the machine and can fix the tag or pick another
file. So a missing artist or album is a refusal with a reason and *nothing is written*; the
arbiter is `LibraryFiler.SafeFolder` returning null, which is also what makes a file tagged
literally "Unknown Artist" count as untagged. The `Artist/Album` folders are created on demand, so
the first file by a new artist makes the shelf it goes on.

**Nothing trusts the file name.** It is a string from a browser: the tags decide the folder, and
the name goes through `LibraryFiler.SafeName` before it becomes part of a path — which is what
stops `../../` being a valid album. Collisions become `song (2).mp3` via the same
`LibraryFiler.Unique` the filer uses; nothing is ever overwritten.

Only what the library indexes is accepted (`.mp3`, `.flac`). The drop folder also takes the
formats the transcode sweep can rescue because it is asynchronous and something will get to them;
an upload answers immediately, and "it's in, but not yet, and only if ffmpeg is installed" is not
an answer — so an OGG is refused with a sentence pointing at the import folder. Size and batch
caps are `Library:MaxUploadBytes` / `Library:MaxUploadFiles`, counted against bytes that actually
arrive rather than a declared length, and **anything past the batch cap is reported as refused by
name** rather than silently dropped.

The rescan afterwards is per destination folder, not a full scan — an album is one or two folders,
and walking 40,000 files to notice ten new ones is the wait this feature exists to avoid.

### The Ideabox

`/ideas` takes a short note from anybody with an account; `/admin/ideas` is where they land.
Posting rings the same cowbell everything else does, to every admin except the author — a
suggestion box that has to be checked on purpose is a suggestion box nobody checks. Archiving is
not deleting, because the person who wrote it seeing that it was read is the only feedback this
box gives.

**The interesting part is the input rule, and it is not the escaping.** Razor escapes interpolated
text and nothing here ever becomes a `MarkupString` — line breaks are `white-space: pre-wrap` in
CSS rather than `<br>` for exactly that reason. `IdeaText` is the layer *under* that, and it
exists because "we escape on output" is a promise about every future rendering site, including the
ones nobody has written yet: a log line, a notification body, a CSV export, a terminal. (The
notification body is already one of them.)

So the rule is blunt — **printable ASCII and newlines, nothing else** — and it is aimed at the
characters that change what a correctly-escaped string *means*: `U+202E` reverses the text after
it, zero-width characters are invisible in every list they appear in, homoglyphs make a Cyrillic
"а" read as a Latin "a", and C0 control characters mean things to terminals. `<script>` is not the
threat model; escaping already handles that, and `IdeaTextTests` asserts that angle brackets and
ampersands are *stored as typed* rather than mangled. The cost is that "café" and emoji are
refused, with a message naming the position and the code point — never echoing the character back,
which is how a sanitiser becomes the injection.

Posting is throttled (20 seconds apart, 10 unarchived per person) against the accident rather than
an attacker — a stuck key, a double-submitted form — because an admin inbox with 400 rows in it is
one nobody opens. Archiving is admin-only and deleting is author-or-admin, both **re-checked
against the database** in `IdeaService`: the `[Authorize]` attribute on the page decides what is
drawn, the service decides what happens.

### Knowing what somebody listens to

`Services/Recommendations/TasteService.cs` turns play history into a taste profile, and a profile
into things to play next. It backs two things: **Surprise me** (`/surprise`), and carrying on when
a playlist runs out.

**Two foundations had to be laid first, and both were silently missing.**

- **The website never recorded a play.** `PlayEvent` existed from the start and only the Android
  app wrote it, so anybody who listens on the website had no history at all and everything built
  on history would have had nothing to work from. `PlayerService.FlushPlayAsync` now does what the
  app's `PlaybackReporter` does: one event per track, written when the track is left, carrying the
  seconds played — which is what tells a listen from a skip.
- **There was no genre anywhere.** `Track.Genre` is read from tags now, and that exposed a trap:
  the scanner skips files whose fingerprint is unchanged, so a new column stays null on every row
  of every library that already existed, for ever. `LibraryScanner.FingerprintVersion` is the fix —
  bumping it invalidates every stored fingerprint, so the next scan re-reads every file's tags
  once and then goes back to being cheap. **Bump it whenever the scanner starts reading a tag it
  didn't read before**, or the feature that needs it will quietly have no data.

  The same change also showed that `SchemaPatch` could add a column but not its index, so an
  upgraded database had `Tracks.Genre` and no index on it while a fresh one had both — invisible
  until the install that has been running for a year is the slow one. Hence `AddedIndexes`.

**The whole design is built around refusing to guess.** A household server has libraries where
most of the music has never been played by the person asking, so a recommender that always answers
mostly answers with music they own and actively don't listen to — which is worse than not having
the feature, because it teaches people the button is bad. So nothing is suggested until
`TasteService.MinimumTracksHeard` (15) **distinct** songs have been heard, and every entry point
says so in words rather than padding the list with randoms. Distinct, not plays: one track on
repeat is not a taste.

Three weightings do the work, and each exists because of a specific way this goes wrong:

- **Completion.** A play counts in proportion to how much of the track was heard, so skipping
  through an album doesn't teach the profile that you love it.
- **Recency.** Plays decay on a 45-day half-life, so this month outranks last year without erasing
  it.
- **Variety.** Suggestions are drawn by weighted random sampling, capped at
  `MaxPerArtist` (2) per batch, with anything played in the last two weeks held out entirely.
  Strict top-N returns one artist's discography — technically the best answer and useless as a
  playlist — and a button called "Surprise me" that returns the same list twice is misnamed.

An `ExplorationFloor` gives every track a non-zero chance, which is the only way somebody hears an
artist they have never played; the ratios keep it rare (a same-genre track is roughly eight times
likelier than an unrelated one). Every suggestion carries the reason it was picked — "You play a
lot of Grunge" — because a suggestion nobody can account for is one nobody trusts, and because it
makes a bad batch diagnosable instead of mysterious.

**Running out.** `PlayerService` tops the queue up when it reaches the end. `UserPreference.
AutoContinue` defaults to **on**, which is only safe because the suggester refuses to run without
history: before there is anything to go on it does nothing and the music stops exactly as it did
before. `RepeatMode.All` never reaches for suggestions — somebody looping a playlist has said what
they want to hear. A queue started from Surprise me passes `alwaysGrow`, so it keeps going whatever
the preference says: endlessness is the thing being asked for there, not a side effect of a setting.

`FakeJsRuntime` in the tests is what makes any of this testable. The player's interesting behaviour
is bookkeeping that happens to sit next to an `<audio>` element, and without a stand-in for the
browser the only way to exercise it is a real one with a working sound device.

### Teams and authorization

A playlist has an owner **or** a team, never both. Everyone can see that a team exists — that's
how you find one — but only members see its playlists.

**Getting in** depends on the team's `TeamJoinPolicy`: `Open` (join instantly),
`RequestToJoin` (an owner accepts — the default) or `InviteOnly`. Both directions live in one
table, `TeamMembershipRequest`, distinguished by `Kind`: an `Invite` is the invitee's to accept,
an `Application` is an owner's. Resolved rows are **deleted**, not marked — a lingering declined
row would block that person from ever being invited again.

Three cases that look like edge cases but happen constantly, all handled in `TeamService`:

- An invite and an application crossing → both sides already said yes, so it resolves to
  membership rather than sitting there.
- Pressing join when you already have an invite → accepts the invite.
- Switching a team to `Open` → auto-accepts everyone queued, instead of stranding applications
  nobody will look at again.

Owners manage everything at `/team/{id}/manage`: name, description, join policy, accept and
decline applications, send and revoke invites, promote, demote, remove, delete.

The rules live in `Services/Playlists/PlaylistAccess.cs` as pure functions over a pre-loaded
set of the user's team ids, and every read and write funnels through `PlaylistService`. Don't
add ownership checks in components — there are four cases to get wrong now, not two.

The one asymmetry: any member can add, remove and rename, but only a **team owner** can delete
a team playlist, because deleting throws away everybody's work. Creating a team makes you its
owner, and the last owner can't leave, be demoted, or be removed — an ownerless team could
never be renamed or deleted again, and its playlists would be undeletable.

The auto-append on request completion runs as the requester through the same path. That's
deliberate: it can't write into playlists the requester can't see, and it correctly refuses if
they left the team while the download was in flight. When the target is a team playlist, the
reconciler notifies the rest of the team as well as the requester — with different wording, and
skipping the requester so they don't get two cowbells.

### The Android app and its API

`android/` is a Kotlin/Media3 app whose reason to exist is Android Auto. `android/README.md` covers
building it and testing it against the Desktop Head Unit; what follows is the part that constrains
the server.

**Everything it needs is `/api/v1`, and nothing there is shared with the website's rendering path.**
The endpoints are thin: `Endpoints/Api/*Endpoints.cs` parse the request and hand off to the same
services the Razor components use. `PlaylistService` is still the only thing that decides who may
read or write a playlist — the API is explicitly not allowed its own ownership checks.

**Two authentication schemes, one set of claims.** The website keeps its cookie; the app gets a
bearer token (`ApiToken`, `ApiTokenService`, `ApiTokenAuthenticationHandler`). Both produce the same
`ClaimsPrincipal` shape, so nothing below the endpoint layer knows which door was used. Three
consequences that were each deliberate:

- **The API policy excludes cookies** (`MootifyAuth.ApiPolicy`). That is what makes it safe for the
  API to turn antiforgery off — a token client can't mint an antiforgery token, and with cookies
  refused there is no CSRF left to prevent. If you ever add the cookie scheme back to that policy,
  antiforgery has to come back with it.
- **Streaming accepts either** (`MootifyAuth.MediaPolicy` on `/media`), because the website plays
  through a cookie and the app through a token, and it's a GET of the user's own library either way.
- **The token scheme 401s instead of redirecting.** A phone handed the HTML login page reads a 200
  full of markup and has nothing to tell its user, which is the failure that makes token clients
  hang rather than re-authenticate. For the same reason `SetupMiddleware` answers `/api` with a 503
  and a JSON sentence instead of bouncing it to `/setup`, and `UseStatusCodePagesWithReExecute` is
  wrapped in a `UseWhen` that skips `/api`, `/media` and `/art`.

Tokens are stored as a plain SHA-256, not PBKDF2: the secret is 32 bytes from a CSPRNG, so there is
no dictionary to run, and it is verified on every request including every range request of every
stream. `LastUsedAt` is stamped at most once every 15 minutes for the same reason — a car seeking
through an album is hundreds of requests and none of them need a write. Signing in goes through the
same `AccountService` and `LoginThrottle` as the web form, so the API isn't a second unlimited door
to the same accounts. Devices are listed and revoked on `/account`.

**`/art/album/{albumId}` is anonymous, and that is the one deliberate hole.** Android Auto renders
browse items from a `MediaMetadata` carrying an `artworkUri`, and the head unit fetches that URI from
its own process — our OkHttp client, and therefore our token, is not involved. The alternatives were
pushing every cover through a browse parcel with a kilobyte-scale size limit, or a car full of grey
squares. So the album GUID is the capability, and what leaks if one escapes is a picture that is also
on the front of the record. **Audio is not treated this way.** `AlbumArtService` finds art the way
the library actually stores it: an adjacent `cover.jpg` is served straight off disk, embedded ID3 art
is extracted once into a cache, and albums with no art get an empty marker file so the miss is as
cheap as the hit (a car scrolling 800 albums asks 800 times).

**Durations are milliseconds and URLs are relative** across the whole API. A `TimeSpan` serializes as
`"00:03:41.2340000"`, and a proxied server doesn't reliably know its own public name, so the client
resolves `/media/…` and `/art/…` against the URL it signed in against. Enums go over the wire as
names (`ConfigureHttpJsonOptions` + `JsonStringEnumConverter`) so a client built before a new enum
member fails to match rather than silently picking the wrong state.

`LibraryQueries` holds every library read the API does, out of the endpoint lambdas, because the two
persistence gotchas below are *translation* failures that only appear when a real provider compiles
the query — and now they have tests.

Three things on the app side worth knowing before changing the server:

- **A track's media id carries the list it came from** (`track:<id>@album:<id>`). Android Auto sends
  only the item that was tapped, so the parent is what lets the app rebuild the album around it.
  Nothing on the server depends on this, but `/library/albums/{id}`, `/playlists/{id}` and
  `/library/artists/{id}/tracks` exist to make it cheap. It is also what makes the listening
  heartbeat free: the parent already says which playlist the current track came out of, so
  `PlaybackReporter` can put it in the save it was making anyway.
- **Any list that can be long is paged by the server, not sliced by the client.** Playlists were
  the exception and were fetched whole for every browse page — see **Playlists are read a page at
  a time**. Whole-list fetches survive only where the list has an inherent ceiling (an album's
  tracks).
- **The app reads ahead, so `/media` sees requests for tracks nobody played.** A rolling three tracks
  are pulled into the phone's cache while the current one plays (`MediaPrefetcher`), bounded to 24MB
  a pass. Play counts are unaffected — those come from `POST /api/v1/plays`, which only the player
  writes, never a media GET. It does mean `/media` throughput is not a listening statistic.
- **Playback resumption reads `PlaybackState`.** `PlaybackState` and `PlayEvent` were in the schema
  from the start and unused; the app writes them (`PUT /api/v1/playback`, `POST /api/v1/plays`), so
  getting in the car continues what was playing in the kitchen. The website still doesn't.
- **Requests work exactly as they do on the website**, and for the same reason: Lidarr fetches albums
  and has no song index, so `/requests/search` is a Lidarr album lookup and `/requests/albums/{mbid}/tracks`
  is MusicBrainz. Lidarr can't hand back an album it hasn't adopted, so the search result is cached
  for 30 minutes and the create call falls back to re-running the client's search term and matching
  on MBID. `GET /requests` is an `ApiPage` like every other list — it was a bare array until an
  import made that several hundred rows — and `DELETE /requests/{id}` cancels one, answering 404
  for both a missing row and somebody else's so the API doesn't leak whose requests exist.

### Persistence gotchas (SQLite)

Both of these were bugs, both are fixed centrally:

- **`DateTimeOffset`** has no native SQLite type and can't be ordered as TEXT. A convention in
  `MootifyDbContext.ConfigureConventions` converts every one to a sortable binary form.
- **`TimeSpan`** can't be aggregated. `Track.DurationTicks` is the mapped column; `Duration` is
  `[NotMapped]`. Project `DurationTicks` in queries and convert after materializing — projecting
  `Duration` directly will fail to translate.

Components use `IDbContextFactory`, not an injected `DbContext`: a circuit outlives any scope,
and two components sharing one context will eventually overlap queries on it.

### Auth and admin

Usernames and passwords, hashed with `PasswordHasher<AppUser>` (PBKDF2). No Identity stack —
just the hasher — so there's no parallel user schema to keep in step.

**First run**: with no accounts, `SetupMiddleware` sends every request to `/setup`, which
creates the one account named by `Auth:AdminUsername` (`mooadmin`) and makes it an admin.
`CompleteSetupAsync` refuses once anybody exists, so it can't be used later to mint a second
admin. After that, self-registration is on unless an admin closes it from `/admin`.

**Two settings are the admin's rather than the file's**, and both work the same way:
`SettingsService` reads a row out of `AppSetting` and falls back to `mootify.json` when there
isn't one. Registration is one; the request quota (`Requests:MaxOpenPerUser`) is the other, so
whoever is watching the disk fill can change the number without a redeploy. **The fallback is the
point** — a missing row means the file is still in charge, which is why the quota is cleared by
*deleting* the row rather than by writing the configured number into it: writing it would freeze
today's file value into the database and silently ignore the file from then on. `RequestService`
therefore asks `SettingsService`, never `IOptionsMonitor<RequestOptions>`, and imports still skip
the check entirely (`enforceQuota: false`).

`OnValidatePrincipal` re-checks the account on every request. That covers four things a
cookie can't know: the row is gone (wiped database), the account was **banned** since sign-in,
admin rights changed, or an admin **reset the password** — the last two patch the claim in place
rather than forcing a re-login. A ban has to bite immediately, not whenever the cookie expires.

**An admin reset is a one-time password.** `AccountService.AdminSetPasswordAsync` sets
`AppUser.MustChangePassword` and revokes the user's device tokens, because the secret is now
known to two people and a phone holding a live token would sail past everything below. The flag
rides in the cookie as a claim, so the gate costs no database round trip:
`PasswordChangeMiddleware` sends every request to `/password` until it clears, and `/auth/login`
redirects there directly so the reason is obvious rather than looking like a bounced navigation.
`ChangePasswordAsync` refuses a new password equal to the current one — otherwise the forced
change is a form to click through and the password the admin knows stays live.

Three consequences of the flag living in a claim. **`/password` is static SSR** (`[ExcludeFrom
InteractiveRouting]`, a form post to `/auth/password`) because the interactive app is exactly
what's gated. **Nothing re-issues the cookie after the change** — `OnValidatePrincipal` drops the
claim on the next request, which is the redirect, and that's also what makes the interactive
`/account` page work: a circuit can't write a cookie. And **the API refuses to issue a token to a
flagged account** with a 403 and a sentence, since a phone has nowhere to choose a new password;
`MootifyAuth.BuildApiPrincipal` therefore never carries the claim.

**`UseAntiforgery()` goes after `UseAuthentication()`/`UseAuthorization()`**, and that ordering is
load-bearing rather than stylistic: antiforgery tokens are identity-bound, so a middleware that
can't see who is asking validates every authenticated form post against an anonymous user and
answers a raw 400. It was in the wrong place until `/auth/password` became the first
antiforgery-protected form an authenticated user posts.

**Admin operations all re-check `IsAdmin` against the database.** `[Authorize(Policy = ...)]`
on the page is for the UI; `AdminService` is the boundary. Three guards exist because each one
would otherwise create an unrecoverable state: you can't ban/delete yourself, you can't remove
the last active admin, and you can't delete a user who solely owns a team. Resetting your own
password is refused for a softer reason — it would lock you into the change screen to solve a
problem you don't have; `/account` is the door.

**Login and register pages redirect away if you're already signed in.** Antiforgery tokens are
bound to the requesting identity, so a form rendered for one user and posted as another fails
with a raw 400. Authorization failures go to `/denied`, not to the login page, for the same
reason.

`LoginThrottle` is two layers. **Delay**: one second per prior failure, applied *before* the
password check and to correct passwords too — delaying only failures would let an attacker read
the answer off the response time. **Lockout**: at `Auth:MaxFailedAttempts` (10) the door shuts
for `Auth:LockoutDuration` (15 min), no password is checked, and attempts during it aren't
counted, so "try again in 12 minutes" stays true. Tripping the lockout clears the counter, so
serving it earns a clean ten rather than a hair trigger.

Both are counted per username **and** per IP, whichever is worse: per-username alone does
nothing against spraying one password across many accounts, per-IP alone does nothing against a
botnet grinding one account. Counters live in a size-limited `IMemoryCache`, so cycling random
usernames can't grow them without bound.

Two consequences worth knowing before changing the numbers. Anyone who can reach the login page
can lock a *known* username out for 15 minutes on purpose — that's inherent to account lockout,
and the 15-minute expiry is the whole mitigation, so there is no unlock button. And behind a
reverse proxy every request carries the proxy's address, so the per-IP half would treat all
users as one; wire up forwarded headers before putting this behind one.

## Testing

`tests/Mootify.Tests` uses real SQLite in memory, not the EF InMemory provider — the bugs worth
catching here are translation failures that InMemory would happily let through.

`ApiIntegrationTests` boots the whole app in-process (`WebApplicationFactory`) and drives it over
HTTP the way the phone will. That exists because the risky part of the API is the wiring, not the
logic: that `/api` refuses a request with no token, that a bearer token gets through both the API and
the audio stream, that turning antiforgery off didn't also turn authentication off, and that a
revoked device stops working at the door. Each of those is one line in `Program.cs` that would fail
silently in the direction of "let it through".

**The isolation in that fixture is load-bearing.** `mootify.json` is copied into the test output, so
a test host that doesn't override it picks up the real connection string, the real Lidarr key and a
music root pointing at the NAS — and then scans it. `UseSetting` is *not* enough: it lands in host
configuration, which `Program.cs` layers `mootify.json` on top of. Worse, `Program.cs` reads the
connection string **eagerly** while composing the container, so even a late in-memory source changes
`IConfiguration` without changing the database the `DbContext` was registered with. The connection
string is therefore overridden with an environment variable, and `AssertIsolated()` checks the
context's *actual* connection string rather than what configuration claims.

## Not built yet

Drag-reorder in the UI (`PlaylistService.MoveAsync` is ready), the Lidarr webhook receiver, playback
state on the *website* (the Android app writes it — see `/api/v1/playback`), offline downloads in the
app, and EF migrations.

Listening-along has no Android UI: the app reports its source playlist and the server decides, but
the switch itself lives on the website (`/account` and any team playlist page) and there is no
`ApiListener` on the client. The Ideabox is website-only for the same reason — the endpoints would
have no screen to live on.

Surprise me and auto-continue are website-only too, though the phone feeds them: the Android app
has always written `PlayEvent`, so it is the main source of the history both run on. Bringing them
to the car means an `/api/v1/suggestions` endpoint and growing the queue in `MootifyLibraryService`
when it empties.

## Razor gotcha

HTML entities inside **attribute values** (`placeholder="Search&hellip;"`) render literally —
Razor escapes the ampersand. Use the character itself (`…`, `•`). In element content they're
fine.
