<img src="Plan/Mootify_Logo_Small.png" alt="Mootify" width="320">

Self-hosted music player for a small group sharing one library on disk. Plays what's already
there; when somebody wants something that isn't, Mootify searches Soulseek through slskd, downloads the exact file, and lands it in the playlist
they were looking at — with a cowbell.

There's a website and an Android app. The app is an Android Auto media app, so the same library and
the same playlists show up in the car — see [`android/README.md`](android/README.md).

## Running it

```bash
cp src/Mootify/mootify.example.json src/Mootify/mootify.json   # then edit it
dotnet run --project src/Mootify
```

On an empty database everything redirects to `/setup`, where you create the admin account
(`mooadmin`) and pick its password. After that anyone can sign themselves up at `/register`,
until an admin closes registration from `/admin`. New accounts wait for approval in **Admin → Users**
before signing in on the website or Android. Existing accounts and the first setup admin keep access.

Admins get `/admin`: ban and unban, promote, reset passwords, delete accounts, rename or delete
any team or playlist, add somebody to a team without an invite, and prune playlist entries whose
files have vanished.

### Playback and playlists

- Open **Queue** in the play bar for the full right-hand panel, including the current track.
  Drag songs or use the up/down buttons to change playback order, including while shuffled.
- Playlists load more songs as you scroll, with continuous numbering. Drag rows or use their
  arrows to reorder the saved playlist. A yellow insertion line marks the drop position.
  Clear a playlist search before reordering.
- For an office queue, play a team playlist, enable **Share what I'm playing**, then **Share queue**.
  Teammates choose **Join queue** beside your listening status. Their Queue panel follows your
  playback order; song options offer **Suggest next in shared queue** and **Add to shared queue**.
  Joining never starts audio on their device. **Leave queue** restores their local queue controls.
  Sharing ends when the host stops sharing, replaces the queue, or closes the session. These live
  website queues are held in server memory and end on a server restart.
- The volume slider applies changes continuously while dragging.
- Right-click a song in the library or a playlist for a menu beside the pointer, or open its **⋯**
  menu, to **Play next** or **Add to queue**. These actions keep the current song playing.
- **Normalize volume** in **Account → Playback** is saved per account for website playback and takes
  effect from the next song. It uses FFmpeg loudness normalization (-16 LUFS, -1.5 dB true-peak
  target) in separate cached MP3 copies; the originals stay intact. Preparing a song's first
  normalized copy can delay playback. Admins can use **Admin → Normalized playback → Prepare all MP3s**
  to prepare the scanned MP3 library in the background, with progress and cancellation. Reruns skip
  up-to-date copies, so cancelled jobs can be resumed by starting again. Run it again after adding
  songs or clearing the playback cache. Preparation uses extra disk space and keeps originals intact.
- Adding to playlists skips library tracks already present, including repeated tracks in a batch.
  **Remove duplicates** on a playlist removes repeated occurrences of the same library track,
  retaining the first occurrence and the remaining order. Existing duplicates stay until removed.
- Soulseek search results show duration when the peer supplies it, or **Length unknown**.
- In a playlist, **Request & replace…** opens an editable search for an alternative recording.
  Choose a Soulseek file; the original stays until that download is ready, then the selected entry
  is replaced in place. Other playlists and library files are untouched. If the new version already
  appears elsewhere in this playlist, that duplicate is removed. A cowbell reports the outcome;
  deleted or changed entries and revoked access prevent replacement.
- **Admin → Requests → Retry** searches Soulseek again for failed or not-found requests, including
  older Lidarr requests. It retains the requester and playlist target; album retries download the
  matching files returned from one peer's album folder. Searches run in the background and retry
  later when no matching peer is available.

When filing an Ideabox item, admins can optionally send a reply of up to 280 characters. The author
sees it on the idea and receives a notification in the bell. **Last seen** updates on authenticated
visits, navigation, and API activity, with a five-minute write throttle for persistent sessions.

### Configuration

Everything lives in `src/Mootify/mootify.json`, which is gitignored because it holds the slskd
API key. Environment variables override it (`Soulseek__ApiKey`, `Library__MusicRoot`), so Docker
secrets work without touching the file.

Artwork and normalized playback copies are stored on the music storage device:
`<Library:MusicRoot>/Cache/Art` and `<Library:MusicRoot>/Cache/Normalized`.
`Library:CacheFolder` defaults to `Cache` and must stay inside `MusicRoot`. The entire cache folder
is excluded from scanning and organizing. If storage is unavailable, these caches never fall back
to the webserver's `data` directory. The former `Api:ArtCacheDirectory` setting no longer controls
where artwork is written. Existing normalized copies in the old transcode cache are moved to
storage when playback or bulk preparation needs them, avoiding another conversion.

Two settings need real thought:

- **`Library:MusicRoot`** — the path *this app* can see. If it's a UNC share that needs
  a login, set `Library:Username` / `Password` / `Domain` — Mootify opens the SMB session itself
  and reconnects before every scan. Windows only; on Linux mount the share in the OS and point
  `MusicRoot` at the mount.
- **`Soulseek:LocalDownloadRoot`** — the path where Mootify sees slskd's configured downloads
  directory. Leave it blank when that is `Library:MusicRoot`. slskd writes each request beneath
  `Soulseek:DownloadDestination`, in its own folder, so Mootify can scan and reconcile it exactly.

FFmpeg must be on `PATH`. Startup logs whether it found it, along with the slskd version and
the music root, so a misconfiguration is visible in the first ten lines of output. Persistent
daily logs are written to the site's `logs/` directory as `mootify-YYYYMMDD.log` and retained for
14 days. Soulseek searches log their query, response and filtering counts, errors, and slskd search
cleanup there.

## In the car

The Android app (`android/`) signs in once against your server — `https://moo.lazy.kiwi` by default —
and then appears in Android Auto as a media app: playlists, recently added, albums, artists, and
voice search. It streams from the same `/media` endpoint the website plays through, and it can ask
Soulseek for something new without getting your phone out.

Signing in registers the phone as a device, listed on **Account** with a Revoke button beside it. No
password is ever stored on the phone; the token is, and revoking it takes effect on the next request.

Build it with `cd android && ./gradlew assembleDebug` (Android Studio supplies the JDK and the
SDK). [`android/README.md`](android/README.md) has the Desktop Head Unit steps for testing the car
integration without a car.

## Requirements

.NET 10 SDK, FFmpeg, and optionally a reachable slskd (without one, everything except
requesting new music still works). For the Android app: Android Studio, which brings its own JDK and
the Android SDK.

## Layout

```
src/Mootify/          the server and website
  Components/         Blazor pages, layout, shared UI
  Endpoints/          /auth, /media, and Api/ — the JSON API the Android app uses
  Services/           auth, library scanning, playback, playlists, Soulseek, requests, transcoding
  Data/               EF Core entities and DbContext
  wwwroot/js/         player.js (music) and notifications.js (cowbell)
android/              the Android Auto client (Kotlin, Media3, Compose)
tests/Mootify.Tests/  dotnet test
Plan/Plan.md          the design document and the reasoning behind it
```

## Tests

```bash
dotnet test
```
