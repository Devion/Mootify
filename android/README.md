# Mootify for Android

A client for a self-hosted Mootify, built to replace Spotify or YouTube Music in the car: it
appears in Android Auto, browses the same playlists and library the website does, and can ask
Lidarr for something that isn't there yet.

Default server: **https://moo.lazy.kiwi**. It's only the value the login screen starts with — the
app stores whatever it signs in against, so a LAN address works too (type the `http://` yourself if
it's plain HTTP).

## Building

Android Studio is all you need — its bundled JDK (JBR 25) builds this, so there's no separate JDK to
install and `JAVA_HOME` doesn't have to be set for the IDE. Nothing on the Mootify server side is
needed to build.

```bash
cd android
./gradlew assembleDebug                  # app/build/outputs/apk/debug/
./gradlew installDebug                   # onto a connected phone
./gradlew assembleDebug -PmootifyServerUrl=http://192.168.1.50:5199   # different default server
```

From a terminal, point `JAVA_HOME` at Studio's runtime:
`export JAVA_HOME="/c/Program Files/Android/Android Studio/jbr"` (or the Windows equivalent).

`assembleRelease` works and R8 shrinks the APK to about 3MB, but the result is **unsigned** — there
is no signing config in this project, so installing a release build means signing it yourself.
`assembleDebug` is the one to use.

The debug build installs as `kiwi.lazy.mootify.debug` and calls itself "Mootify (debug)", so it can
sit beside a release build without Android Auto showing two identical media apps.

### The toolchain is pinned current, not pinned old

Versions live in `gradle/libs.versions.toml`, and the floor is set from the top: **Studio ships a
JDK 25 runtime, and Gradle releases before 9.x refuse to start on it.** That forces Gradle 9.7 and
AGP 9.3.1, which in turn forces `compileSdk 37` (AndroidX `core-ktx` 1.19 won't be compiled against
less). Downgrading any one of those means downgrading the lot, or installing an older JDK.

Three AGP 9 details that are easy to trip over if you edit `app/build.gradle.kts`:

- **`org.jetbrains.kotlin.android` must not be applied.** AGP 9 brings its own Kotlin support and
  fails the build if the plugin is applied on top. The Compose and serialization compiler plugins are
  still declared normally.
- **`buildFeatures { resValues = true }` is required**, because AGP 9 turns resource values off by
  default and the debug build renames itself with one.
- **`kotlinOptions {}` is gone**; Kotlin settings live in the top-level `kotlin { compilerOptions {} }`
  block.

Media3 is the dependency to be careful with when bumping: `media3-exoplayer`, `media3-session` and
`media3-datasource-okhttp` have to move together, or the browse tree fails at runtime rather than at
compile time. OkHttp stays on 4.x because that's what `media3-datasource-okhttp` is built against —
moving to OkHttp 5 means moving Retrofit to 3.x at the same time.

## Signing in

Server URL, username, password — the same account as the website. That registers this phone as a
device on the server and stores a token (a year by default, `Api:TokenLifetime`). The car never asks
for a password, which is the point.

Revoke a device from the website's **Account** page, or from the app's sign-out button. A revoked
token stops working on the next request, including mid-stream.

## Testing the Android Auto integration

Without a car:

1. Install **Android Auto** on the phone (pre-installed on most), then enable developer mode in it:
   Settings → about → tap the version ten times.
2. In Android Auto's developer settings, turn on **Unknown sources** — a debug-signed app is an
   unknown source and is invisible in the car until this is on. This is the single most common
   reason "my app doesn't appear".
3. Install the [Desktop Head Unit](https://developer.android.com/training/cars/testing/dhu)
   (`$ANDROID_HOME/extras/google/auto/desktop-head-unit`), connect the phone by USB, and run it.
4. Mootify appears in the head unit's media app list. Browse → Playlists / Recently added / Albums /
   Artists.

Voice search ("play Nirvana on Mootify") goes through the same `onSearch` path the browse tree uses;
the server answers artists, albums and songs in one call so the car can decide what to play without
three round trips.

What to check when something looks wrong:

| Symptom | Usually |
| --- | --- |
| App missing from the car | "Unknown sources" off, or the `android.media.browse.MediaBrowserService` intent filter got dropped from the manifest |
| Browse shows "Sign in on your phone" | No token — sign in on the phone first |
| Browse is empty | Server unreachable from the phone's network |
| Grey squares instead of covers | The library has no embedded art or `cover.jpg` next to the files, or `/art/album/{id}` isn't reachable — the car fetches art itself, unauthenticated, by design |
| Tapping a song plays one song and stops | The queue wasn't expanded — see `onSetMediaItems` in `MootifyLibraryService` |
| Music stops for good after a signal drop | `NetworkRecovery` only retries transient errors; a 401 (revoked device) is not one, and the app will be back at the login screen |
| More data used than expected | The read-ahead, capped at 24MB a pass — see **Playing on a bad connection** |

## How it's put together

```
ui/          Compose screens and a MediaController onto the session
playback/    MediaLibraryService — the browse tree Android Auto reads, and the player
data/        Retrofit against /api/v1, one OkHttp client, the session store
```

Three decisions worth knowing before changing anything:

**One player, one session.** `MootifyLibraryService` owns the only `ExoPlayer`. The phone UI drives
it through a `MediaController`, exactly as the car does. Nothing keeps its own `isPlaying`.

**One HTTP client for JSON and audio.** The bearer token is attached by an interceptor, so
ExoPlayer's range requests are authenticated without anybody remembering to set a header. A 401
anywhere clears the session and the UI drops to the login screen.

**A track's media id carries the list it came from** (`track:<id>@album:<id>`). Android Auto sends
the single item that was tapped and nothing else; the parent is what lets the service rebuild the
album around it so "next" works.

## Playing on a bad connection

A car is the worst network this app will ever see, and three pieces in `playback/` exist for it. They
are worth understanding together, because each one is what stops the next from being needed.

**The buffer holds whole songs.** `bufferForPatchySignal()` in `MootifyLibraryService` asks for five
minutes ahead rather than Media3's default fifty seconds, and sets
`prioritizeTimeOverSizeThresholds` — without that the byte threshold stops the load long before the
duration is reached and the numbers mean nothing. Losing signal mid-song is then inaudible, because
the song is already in memory. The 24MB target is RAM, not disk.

**`MediaPrefetcher` reads ahead onto disk**, into the same cache the player reads through, so a
prefetched track is indistinguishable from one played yesterday. It is bounded three ways on purpose,
because the failure mode is a phone quietly pulling a gigabyte off a data plan when someone opens a
600-song playlist: **three tracks ahead** (re-aimed on every song change and every shuffle toggle),
**12MB per track and 24MB per pass**, counted against bytes actually fetched — a track already on
disk costs nothing. A long track is cached as a prefix and the tail streams normally. Nothing starts
until the current song is 85% buffered, since prefetching competes for exactly the bandwidth the
thing playing right now is short of.

**`NetworkRecovery` restarts what finally failed.** ExoPlayer retries a load internally (8 times here,
up from 3) and then drops to `STATE_IDLE`, which in a car is silence until somebody picks up the
phone. So: four re-prepares on a doubling backoff, then stop and register for the network coming back
instead — a ten-minute dead zone resumes on the first bar of signal rather than having spent its
attempts in the first thirty seconds. Only transient errors qualify. A 401 means the device was
revoked, and re-preparing into that is a loop that spends battery to be told no four more times.

None of it is offline playback: the read-ahead is a rolling window, not a download, and it is
evicted LRU like everything else in the 512MB cache.

## Not built yet

Offline downloads — the cache reads through and reads ahead, but nothing is pinned, so a drive with
no signal from the start is still silence. Also playlist reordering, notifications for a landed
request, and Wear OS.
