# Mootify — Build Plan

*Mooving along.*

Self-hosted setup: your MP3s already live on disk in a folder Lidarr also manages, multiple users, and "teams" meaning small shared groups (household, office, band).

## Stack

- **ASP.NET Core 10 Blazor Web App**, Interactive Server render mode. Playback itself is client-side HTML5 audio driven through JS interop, so the server circuit only carries UI and queue state.
- **EF Core 10** on PostgreSQL (SQLite for local dev).
- **Auth: pluggable.** Dev runs on a shared passphrase (`moo`); production swaps in ASP.NET Core Identity with cookie auth. Same claims either way — see [Authentication](#authentication).
- **TagLibSharp** for ID3 metadata during library scans.
- **SignalR hub** for cross-user events: team playlist edits, request status changes, notifications.
- **Serilog + OpenTelemetry**, Docker Compose for deployment (app, Postgres, bind-mount the music volume read-only).

## Authentication

The login must stay trivial during development but must not paint you into a corner, so put one interface in front of it:

```csharp
public interface IUserAuthenticator
{
    Task<ClaimsPrincipal?> AuthenticateAsync(LoginRequest request, CancellationToken ct);
}
```

Two implementations, chosen by `Auth:Mode` in config: `PassphraseAuthenticator` and `IdentityAuthenticator`. Everything downstream — `PlaylistAuthorizationHandler`, request ownership, notifications — reads `ClaimTypes.NameIdentifier` and never knows which one signed the cookie.

> **Superseded.** Mootify now uses real usernames and passwords with a first-run admin account
> (`mooadmin`) and self-registration. The two sections below are kept for the reasoning that
> shaped the current design — in particular why a per-user identity was always needed. See
> `CLAUDE.md` for how auth actually works.

### Passphrase mode (development)

The login page has exactly two fields:

1. **Display name** — free text, remembered in `localStorage` so returning users hit Enter twice.
2. **Passphrase** — compared against `Auth:Passphrase`, default `moo`.

On success, upsert an `AppUser` by normalised display name and issue a cookie with `NameIdentifier` (a deterministic GUIDv5 from the normalised name, so the same name always maps to the same user across DB resets), `Name`, and `role: user`.

Why bother with the name field when the passphrase is shared? Because half the features you asked for are per-user — *"notify **the user who added it**"*, per-user playback state, per-user request quota. A single anonymous session would make requests and notifications unattributable and you would have to rework them later. The name field costs one `<input>` and keeps the whole ownership model honest.

Guard rails, so this never ships by accident:

- Refuse to start in `Production` with `Auth:Mode=Passphrase` unless `Auth:AllowPassphraseInProduction=true` is set explicitly. Throw at startup, don't warn — a warning in a log nobody reads is not a guard rail.
- Log a loud banner on every boot in passphrase mode.
- Compare the passphrase with a fixed-time comparison anyway (`CryptographicOperations.FixedTimeEquals`), and rate-limit the login endpoint. It is five lines and means the dev path isn't a worked example of how not to do it.
- No registration, no password reset, no email confirmation in this mode. `/Account/*` Identity pages are not mapped at all.

### Identity mode (later)

Standard Identity tables are in the EF model from day one, so switching modes is a config change plus a migration, not a rewrite. `AppUser` rows created in passphrase mode survive — you attach credentials to them rather than re-creating users, which keeps their playlists and history.

## Configuration

One file, `mootify.json`, layered over `appsettings.json` and overridable by environment variables (`Lidarr__ApiKey`) so Docker secrets work without editing files.

```jsonc
{
  "Auth": {
    "Mode": "Passphrase",          // Passphrase | Identity
    "Passphrase": "moo",
    "AllowPassphraseInProduction": false
  },
  "Lidarr": {
    "BaseUrl": "http://lidarr:8686",
    "ApiKey": "",                   // required; also LIDARR__APIKEY or /run/secrets/lidarr_api_key
    "QualityProfileId": 1,
    "MetadataProfileId": 1,
    "RootFolderPath": "/music",
    "SearchOnAdd": true,
    "PollInterval": "00:10:00"
  },
  "Library": {
    "MusicRoot": "/music",
    "FullScanInterval": "06:00:00",
    "WatchFileSystem": true
  },
  "Requests": {
    "MaxOpenPerUser": 5
  },
  "Transcode": {
    "FfmpegPath": "ffmpeg",
    "Bitrate": "320k",
    "DeleteSourceAfterTranscode": false
  },
  "Notifications": {
    "SoundFile": "/audio/cowbell.mp3",
    "DefaultSoundEnabled": true,
    "DuckMusicWhilePlaying": true
  }
}
```

Implementation notes:

- Bind with the options pattern and `.ValidateDataAnnotations().ValidateOnStart()`. A missing or malformed `Lidarr:BaseUrl` should stop the app at boot with a readable message, not surface as a `NullReferenceException` the first time somebody clicks Request.
- `IOptionsMonitor<LidarrOptions>` with `reloadOnChange`, so editing the API key doesn't need a restart. The typed `HttpClient` reads the key per request from the monitor rather than capturing it at construction — otherwise "hot reload" silently keeps using the old key.
- Commit `mootify.example.json`; gitignore `mootify.json`.
- Startup health check calls `GET /api/v1/system/status`. Surface the result on an admin/status page as green/red with the actual error text. A wrong API key returns `401` and is the single most common setup failure.
- Never render the API key back into the UI, even masked — show "configured" / "not configured".

## Branding and assets

| Asset | Use |
|---|---|
| `Mootify_Logo_Big.png` (portrait, **black background**) | Login page and splash. Make the login page a full-bleed dark panel and the black background stops being a problem — no alpha-channel surgery needed. Do not drop it on a light surface. |
| `Mootify_Logo_Small.png` (transparent, horizontal lockup) | Top bar, favicon source, PWA icons, `og:image`. The wordmark is white-filled with a dark outline, so it survives on both light and dark chrome — but check it against the exact top-bar colour before committing to a light theme. |
| `pietjecow.png` | Empty states, 404, "nothing playing" placeholder. Too big to ship as-is: resize and compress. |
| `cowbell.mp3` | Notification sound. Sourced. Check it's normalised against typical track loudness — a notification louder than the music is a mute button people will actually press. |

Dark theme first; the artwork is drawn for it. Generate favicon/PWA sizes from the small logo at build time rather than hand-exporting a dozen PNGs.

## Data model

| Entity | Notes |
|---|---|
| `Track` | Path, hash, duration, bitrate, title, `ArtistId`, `AlbumId`, track no., MBIDs from tags |
| `Artist` / `Album` | MusicBrainz IDs are the join key to Lidarr |
| `AppUser` | Identity user; auto-provisioned in passphrase mode |
| `Team` / `TeamMember` | Role: owner, member |
| `Playlist` | `OwnerUserId` nullable, `TeamId` nullable, `Visibility` enum. Exactly one of owner/team set |
| `PlaylistItem` | `SortKey` as a `double` (insert = midpoint of neighbours, renumber job when gaps collapse) |
| `Request` | Requester, `Kind` (Track/Album/Artist), target MBIDs, free-text query, status enum, `LidarrArtistId`, `LidarrAlbumId`, **`TargetPlaylistId`** (nullable), `FailureReason`, timestamps |
| `Notification` | `UserId`, `RequestId`, `Type`, `Title`, `Body`, `CreatedAt`, `ReadAt` (nullable) |
| `UserPreference` | Sound on/off, duck-while-playing, theme, last volume |
| `PlaybackState` | Per user: current track, position, queue snapshot, shuffle seed, repeat mode |
| `PlayEvent` | For history, "recently played", and later recommendations |

## Playback

The single hardest bug in this kind of app is the `<audio>` element getting destroyed on navigation. Put the player in `MainLayout`, not in a routed page — one persistent element, one JS module wrapping it, components talk to it through an injected `PlayerService`.

- Stream via a minimal API endpoint: `GET /media/{trackId}` returning `Results.File(path, "audio/mpeg", enableRangeProcessing: true)`. Range support is non-negotiable — without it seeking silently breaks and Safari won't play at all.
- Authorize per-request against the track, then serve. Add ETags so browsers cache.
- Persist position every few seconds so a reload resumes where you left off. In .NET 10 you can lean on `[PersistentState]` for the transient bits of component state across prerender.

### Shuffle

Don't reshuffle on every `next`. Generate a permutation once with seeded Fisher–Yates, store the seed and cursor in `PlaybackState`, and walk it. This gives you: no repeats until the queue is exhausted, a stable "up next" list, and reproducible order across reloads and devices. Toggling shuffle off restores the natural order at the current track rather than jumping.

Optional refinement: artist-aware spreading, so a 200-track playlist with 40 tracks by one artist doesn't clump them.

## UI

Three fixed regions, one routed region:

```
┌─────────────────────────────────────────────────────┐
│ [logo]   search…                    [🔔 3] [mute] [user] │  top bar
├──────────┬──────────────────────────────────────────┤
│ Library  │                                          │
│ Playlists│            routed content                │
│  + New   │                                          │
│ Requests │                                          │
├──────────┴──────────────────────────────────────────┤
│ ▸ art  Title — Artist   ⏮ ⏯ ⏭  ──●───── 1:23/3:45  🔊 ⇄ ↻ ≡ │  play bar
└─────────────────────────────────────────────────────┘
```

- **Play bar** lives in `MainLayout` next to the audio element and never unmounts. Prev / play-pause / next, scrubber with buffered range, elapsed & total, volume, shuffle, repeat, queue drawer toggle. Clicking the track title navigates to its album; clicking the art expands a now-playing view.
- **Play/pause** must be driven from one place. `PlayerService` owns the state, the bar and any in-list play buttons render from it, and the JS module raises `timeupdate`/`ended`/`play`/`pause` back into it. Two components each holding their own "is playing" flag is how you get a pause button that lies.
- **Keyboard**: space toggles play unless focus is in a text input — check `document.activeElement` in JS, not in Blazor, or you will fight the circuit's latency.
- **Playlist create/select**: `+ New playlist` in the sidebar opens an inline row, not a modal — name it, hit Enter, done. Adding a track uses a "+" on the row that opens a searchable playlist picker with **Create new…** pinned at the top, so "add this to a playlist that doesn't exist yet" is one interaction rather than three.
- **Drag-reorder** with the `SortKey` midpoint trick, optimistic in the UI, reconciled over SignalR.
- **Media Session API** so lock-screen and headset controls work. Cheap to add, and it's the difference between a web player and something you actually use on a phone.

## Search, requests and lookup

One search box, escalating in three steps. This is the "find the right song" flow and it deserves the care.

1. **Local library** — debounced ~300 ms, searches track/album/artist with trigram or `ILIKE` matching, grouped results. Instant, no network.
2. **"Not in the library? Search for it →"** — always visible under local results, even when there are hits, because the local match may be the wrong version.
3. **Remote lookup** — hits Lidarr's `/api/v1/album/lookup` and `/artist/lookup`, renders cover art, release year, type (album/EP/single) and track count. Disambiguation matters: three results named *Greatest Hits* with no year is a coin flip, and the user picking wrong means a wasted download and a confused "where's my song".

**Lidarr cannot fetch a single track**, and it has no song index either. Measured against a real instance: `album/lookup` returns a track *count* and no titles, `/track` only answers for albums already in the library, and the universal search returns artists and albums only. So a song request has to be expressed as "this album, but keep one track".

**Search by song title is a trap, and it was worth proving before building it.** MusicBrainz is the only free song index, and its search ranks by text match with no popularity signal. Against the live API:

- `bohemian rhapsody` → the top seven hits are tribute bands, a string quartet and a karaoke album. **No Queen at all.**
- `one more time daft punk` → a piano cover, an NFT, and a Chemical Brothers mashup outrank Daft Punk, because the covers put "Daft Punk" in their *title* while the real recording only has it in the artist field.

Scoping the query to a detected artist (`arid:`) fixes relevance completely, but then mapping each recording back to its studio album needs a further call per result at one request per second — too slow to type against. A song-first search would either be wrong or be slow.

So the flow is **album search, then tracklist**:

- Album search stays with Lidarr, which is accurate — `Discovery / Daft Punk` comes back correctly.
- Expanding an album fetches its tracklist from MusicBrainz in one call (`release?release-group=<mbid>&inc=recordings`), which returns every pressing with full tracks and recording MBIDs. Pick the earliest official pressing so you get the album as released rather than a remaster with bonus discs.
- Requesting a song stores the recording MBID on the `Request`. That's what picks the right track out of the album when it lands, instead of guessing by title.
- Lidarr is handed the album by MBID (`album/lookup?term=lidarr:<release-group-mbid>` resolves it exactly), downloads the lot, and only the chosen track joins the playlist.

Respect the MusicBrainz rate limit (1 req/s, `User-Agent` with contact info) and cache tracklists — they're historical facts and won't change.

The request row confirms in one place: what you're getting, and **which playlist to add it to when it arrives** (or "don't add anywhere"). Capturing the target playlist at request time — not asking later — is what makes the completion flow silent and automatic.

### Dispatch and reconciliation

- Typed `HttpClient` with `X-Api-Key` header, Polly retry + circuit breaker. Lidarr's v1 API gives you `/artist/lookup` and `/album/lookup` for search, `POST /artist` to add (with `monitored`, `qualityProfileId`, `metadataProfileId`, `rootFolderPath`, and `addOptions.searchForMissingAlbums`), and `POST /command` with `AlbumSearch` to force a search for an artist you already track.
- Never trust the webhook alone. Run a reconciliation `BackgroundService` every 10–15 minutes that polls `/queue` and `/wanted/missing` for open requests and reconciles status. Webhooks get missed; polling makes it self-healing. Webhook = fast path, poll = source of truth.
- Cache the Lidarr profile IDs and root folders at startup so the request screen shows real dropdowns rather than raw numbers.
- **No approval step.** This is a local install for people who already trust each other; a request dispatches to Lidarr immediately. The quota below is the only brake, and it exists to protect the disk, not to police anyone.
- Per-user quota (`Requests:MaxOpenPerUser`, default 5) saves you from someone queueing a discography and filling the disk.
- Status enum: `Pending → Searching → Downloading → Imported → Transcoding → Available`, plus `NotFound` and `Failed`. Store a failure reason; "it didn't work" with no detail is the thing that generates support messages.

### Completion: import → scan → playlist → notify

The ordering here is the part that breaks if you improvise it. Lidarr says "imported" before your database knows the files exist, so appending to the playlist on the import event alone will append nothing at all, intermittently, and only on a fast disk.

```
Lidarr import (webhook or poll)
   └─ status → Imported
      └─ any non-MP3 files?  ── yes ─→ status → Transcoding
      │                                  └─ FFmpeg → MP3, replace in place
      └─ targeted rescan of the import path (not a full library scan)
         └─ Track rows now exist (MP3 only)
            └─ match request → tracks
               └─ append to Request.TargetPlaylistId (dedupe, preserve track order)
                  └─ status → Available
                     └─ Notification row + SignalR push to Request.RequesterId
```

**MP3 only.** The library holds nothing else. Transcoding happens *before* the rescan, so a FLAC never becomes a `Track` row and therefore can never reach a playlist or the player — the invariant is enforced by ordering, not by checks scattered through the UI. A request is not `Available` until every file backing it is playable; that is the whole point of the extra status.

Matching, in order of preference: recording MBID → track MBID from tags → normalised title within the imported album. For a `Kind=Album` request, append the whole album in track order; for `Kind=Track`, append the single best match and log the runners-up so a mismatch is diagnosable.

Make the append idempotent (`RequestId` recorded on the created `PlaylistItem`s) — the webhook and the poller *will* both fire for the same request eventually. If the target playlist was deleted meanwhile, skip the append and say so in the notification rather than failing the request.

Long Lidarr calls never happen in a component: they block the circuit. Background service, then SignalR callback.

## Notifications

A bell in the top bar with an unread count, a dropdown listing recent notifications with relative timestamps, click to jump to the playlist or the request, and mark-all-read.

- Persist to the `Notification` table first, *then* push over SignalR to `Clients.User(userId)`. Push-only means anyone who was offline when their album landed never finds out — and that's the exact user who's been waiting three days for it.
- Requeue on connect: the bell loads unread rows on init, so a reconnect after a dropped circuit shows the same badge.

### The cowbell

- Play it through a **second, dedicated `<audio>` element**. Reusing the music element to play the notification will stop the song. This is the one implementation detail here that is not negotiable.
- **Duck, don't stop.** If music is playing and `DuckMusicWhilePlaying` is on, ramp the music volume to ~30% for the length of the cowbell and ramp it back. Ramp over ~150 ms; an instant volume jump sounds like a glitch.
- **Mute button** sits next to the bell — one click, obvious state, persisted to `UserPreference` and mirrored into `localStorage` so it applies before the circuit connects. Muting silences the sound only; the bell and badge still update.
- **Autoplay policy**: browsers block audio until a user gesture. Login and the first play click are both gestures — prime the notification element there (`load()`, or a muted zero-volume `play()`), otherwise the first cowbell of the session is swallowed and it looks like the feature is broken.
- Ship `.mp3` and `.ogg` sources on the same element.
- Coalesce: if five albums import at once, one cowbell and five list entries. A user who requested a discography should not be assaulted.

## Playlists and teams

A playlist has an owner **or** a team, never both.

Everyone can see that a team exists — that's how you find one — but only members see its playlists.

**Getting in** is the team's own choice, set by its owner: *open* (join instantly), *ask to join* (an owner accepts) or *invite only*. Default is ask-to-join, on the grounds that somebody who bothers to make a team usually wants to know who's in it, and open is one dropdown away.

Both directions — the owner's invite and the applicant's request — are the same shape, so they're one table with a `Kind`. Only who's allowed to accept differs: an invite is the invitee's to accept, an application is an owner's. Neither can be waved through by the other party, and resolved rows are deleted rather than kept, so declining somebody doesn't permanently block them.

Three cases that sound like edge cases and aren't:

- **An invite and an application crossing.** Both sides have said yes; making either click again would be silly, so it resolves straight to membership.
- **Pressing join when you already have an invite.** Accept it rather than filing an application the owner has to approve twice.
- **Switching a team to open.** Auto-accept everyone queued, or you leave a stack of applications nobody will ever look at again.

Invites can only target people who already have an account, because accounts are created on first login and there's no email to send to. Say so in the UI rather than letting an owner wonder why their housemate isn't in the list.

Authorization is the part that bites. The rules live in one place (`PlaylistAccess`) as pure functions over the user's team ids, and every read and write funnels through `PlaylistService` — `Read` and `Edit` (owner, or member of the team), `Delete` (owner, or **team owner**). Don't scatter `if (playlist.OwnerId == userId)` checks through Razor components; with teams there are four cases to miss rather than two.

That last asymmetry is deliberate: any member can add, remove and rename, because a shared playlist nobody may touch is just a private one in an awkward place. Deleting is different — it throws away everybody's work, so it stays with the team owner.

Two invariants worth enforcing rather than hoping for:

- **A team is never ownerless.** Whoever creates it is its first owner, and the last owner can't leave, be demoted, or be removed — an ownerless team could never be renamed or deleted, and its playlists would be undeletable. Promote somebody first, or delete the team.
- **The auto-append runs as the requester**, through the same authorization path as a manual add. So a request into a team playlist correctly refuses if the requester left the team while the download was in flight, and the completion notification goes to the whole team — different wording, skipping the requester so they don't get two cowbells.

For team playlists, concurrent edits are real. Give `PlaylistItem` a row version, and broadcast changes over SignalR so a second user's open list reorders live instead of overwriting.

## Library scanning

A `BackgroundService` that walks the music root, hashes files, reads tags with TagLib, and upserts. Add `FileSystemWatcher` with a debounce for near-real-time pickup after Lidarr imports, but keep the periodic full scan as the source of truth — watchers drop events on network shares. Expose a `ScanPathAsync(path)` entry point for the targeted post-import rescan above; the request pipeline shouldn't have to wait out a full scan of 40,000 files to add one song to a playlist.

## Gotchas worth planning for now

- **Lidarr downloads FLAC by default.** Handled two ways, belt and braces: set the Lidarr quality profile to MP3 so it mostly doesn't happen, *and* run the transcode step above so it doesn't matter when it does. Don't rely on the profile alone — Lidarr will fall back to whatever it can find when the preferred quality isn't available, and the failure would land directly in the auto-add-to-playlist feature.
- **FFmpeg is a hard dependency**, not an optional extra. Add it to the container image and fail the startup health check if it isn't on `PATH` — discovering it's missing at 2 a.m. when the first FLAC lands is worse than discovering it at boot.
- **Transcoding is CPU-heavy and concurrent with playback.** Run it through a bounded queue (one or two at a time), not a `Task.Run` per file, or importing a discography will make the app stutter for everyone listening.
- **Mobile browsers require a user gesture** before audio will play, and iOS Safari won't autoplay the next track unless playback started from a gesture in the same audio element. Reuse one element for the whole session.
- **Blazor Server + flaky wifi** means dropped circuits mid-song. Keep playback state in the browser as the authoritative copy, syncing to the server, not the other way round.
- **Long-running Lidarr calls in a component** will block the circuit. Background services or fire-and-forget with SignalR callbacks.
- **The shared passphrase is a shared account in disguise.** Anyone on the network who can reach the site is any user they choose to type. Fine on a LAN during development; put it behind a VPN or reverse-proxy auth before it touches anything public, and switch to Identity mode before real users exist.

## Phases

1. ~~**Foundation** — project, EF model, passphrase login, config binding + validation, scanner, browse by artist/album.~~ **Done.**
2. ~~**Player** — persistent audio element, range streaming, play bar, queue, shuffle, repeat.~~ **Done**, except resume-across-sessions (`PlaybackState` is modelled but not written to).
3. ~~**Playlists** — personal CRUD, create/select flow, teams with join policies, invites, join requests, a management page, shared authorization.~~ **Done**, except drag-reorder (`MoveAsync` exists, no UI) and live SignalR reordering for concurrent team edits.
4. ~~**Requests** — request entity with target playlist, local search, three-step lookup.~~ **Done.** No approval queue by decision.
5. **Lidarr** — ~~dispatch, reconciliation service, import→transcode→scan→playlist→notify pipeline, bell + cowbell + mute~~ **done**; webhook receiver still to do (polling covers it, just more slowly).
6. **Polish** — Media Session wiring (the JS is there, nothing calls `setMetadata` yet), PWA manifest, listening history, Identity mode, EF migrations, Docker Compose.

Remaining before this is more than a dev toy, roughly in order: **EF migrations** (startup uses
`EnsureCreated`, so any entity change means deleting the database), the Lidarr **webhook** for
fast completion, **playback resume**, and **drag-reorder**.
