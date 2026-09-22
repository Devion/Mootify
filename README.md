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
until an admin closes registration from `/admin`.

Admins get `/admin`: ban and unban, promote, reset passwords, delete accounts, rename or delete
any team or playlist, add somebody to a team without an invite, and prune playlist entries whose
files have vanished.

### Configuration

Everything lives in `src/Mootify/mootify.json`, which is gitignored because it holds the slskd
API key. Environment variables override it (`Soulseek__ApiKey`, `Library__MusicRoot`), so Docker
secrets work without touching the file.

Two settings need real thought:

- **`Library:MusicRoot`** — the path *this app* can see. If it's a UNC share that needs
  a login, set `Library:Username` / `Password` / `Domain` — Mootify opens the SMB session itself
  and reconnects before every scan. Windows only; on Linux mount the share in the OS and point
  `MusicRoot` at the mount.
- **`Soulseek:LocalDownloadRoot`** — the path where Mootify sees slskd's configured downloads
  directory. Leave it blank when that is `Library:MusicRoot`. slskd writes each request beneath
  `Soulseek:DownloadDestination`, in its own folder, so Mootify can scan and reconcile it exactly.

FFmpeg must be on `PATH`. Startup logs whether it found it, along with the slskd version and
the music root, so a misconfiguration is visible in the first ten lines of output.

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
