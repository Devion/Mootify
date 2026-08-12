<img src="Plan/Mootify_Logo_Small.png" alt="Mootify" width="320">

Self-hosted music player for a small group sharing one library on disk. Plays what's already
there; when somebody wants something that isn't, Lidarr fetches it and it lands in the playlist
they were looking at — with a cowbell.

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

Everything lives in `src/Mootify/mootify.json`, which is gitignored because it holds the Lidarr
API key. Environment variables override it (`Lidarr__ApiKey`, `Library__MusicRoot`), so Docker
secrets work without touching the file.

Two settings need real thought:

- **`Library:MusicRoot`** — the path *this app* can see. Lidarr's root folder is a path inside
  Lidarr's own container; they are usually not the same string.
- **`Lidarr:QualityProfileId`** — pick a profile without FLAC in it (Lidarr's stock "Standard"
  is usually id 3). Anything non-MP3 that slips through gets transcoded, but not downloading it
  in the first place is cheaper.

FFmpeg must be on `PATH`. Startup logs whether it found it, along with the Lidarr version and
the music root, so a misconfiguration is visible in the first ten lines of output.

## Requirements

.NET 10 SDK, FFmpeg, and optionally a reachable Lidarr (without one, everything except
requesting new music still works).

## Layout

```
src/Mootify/          the app
  Components/         Blazor pages, layout, shared UI
  Services/           auth, library scanning, playback, playlists, Lidarr, requests, transcoding
  Data/               EF Core entities and DbContext
  wwwroot/js/         player.js (music) and notifications.js (cowbell)
tests/Mootify.Tests/  dotnet test
Plan/Plan.md          the design document and the reasoning behind it
```

## Tests

```bash
dotnet test
```
