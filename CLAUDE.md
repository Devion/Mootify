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
dotnet test                             # 274 tests, ~3s
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
file. A **brand-new table** added to `Data/Entities.cs` can be created at boot by adding its DDL to
`Data/SchemaPatch.cs` — that's how `ApiTokens` reached installs that already had accounts and
playlists in them, without anybody losing a library. It is a stopgap, not a migration system: no
version table, no down path, and no support for changing an existing column. **Any other change to
`Data/Entities.cs` still means deleting `src/Mootify/data/mootify.db`.** Add real migrations before
the next one.

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

### The request pipeline is ordered, and the order is the design

```
Lidarr import → transcode non-MP3 → targeted rescan → match tracks → append to playlist → notify
```

`Services/Requests/RequestReconciler.cs`. Transcoding happens *before* the rescan, so a FLAC
never becomes a `Track` row and therefore can never reach a playlist or the player — the
MP3-only invariant is enforced by sequencing, not by checks in the UI. Appending before the
rescan finds nothing, intermittently, and only on a fast disk.

Polling is the source of truth; a webhook (not yet built) would only be the fast path. The
reconciler is idempotent — `PlaylistItem.RequestId` makes the append safe to run twice, which
it will be.

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
  `/library/artists/{id}/tracks` exist to make it cheap.
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
  on MBID.

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

`OnValidatePrincipal` re-checks the account on every request. That covers three things a
cookie can't know: the row is gone (wiped database), the account was **banned** since sign-in,
or admin rights changed — the last one patches the claim in place rather than forcing a
re-login. A ban has to bite immediately, not whenever the cookie expires.

**Admin operations all re-check `IsAdmin` against the database.** `[Authorize(Policy = ...)]`
on the page is for the UI; `AdminService` is the boundary. Three guards exist because each one
would otherwise create an unrecoverable state: you can't ban/delete yourself, you can't remove
the last active admin, and you can't delete a user who solely owns a team.

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

## Razor gotcha

HTML entities inside **attribute values** (`placeholder="Search&hellip;"`) render literally —
Razor escapes the ampersand. Use the character itself (`…`, `•`). In element content they're
fine.
