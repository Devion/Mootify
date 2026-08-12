# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Mootify is a self-hosted music player for a small group of people who share one music library
on disk. It plays what's already there, and when somebody wants something that isn't, it asks
Lidarr to fetch it and drops the result into the playlist they were looking at.

`Plan/Plan.md` is the design document. It carries the reasoning behind most of the decisions
below — read it before changing anything architectural.

## Commands

```bash
dotnet run --project src/Mootify        # http://localhost:5199 (or the launchSettings port)
dotnet build                            # whole solution
dotnet test                             # 26 tests, ~1s
dotnet test --filter "FullyQualifiedName~PlaylistServiceTests"     # one class
dotnet test --filter "DisplayName~Adding_the_same_request_twice"   # one test
```

Config lives in `src/Mootify/mootify.json` (gitignored — it holds the Lidarr API key).
`mootify.example.json` is the committed template. Environment variables override it
(`Lidarr__ApiKey`, `Library__MusicRoot`).

There are no EF migrations yet: startup calls `EnsureCreated()`. **Any change to
`Data/Entities.cs` means deleting `src/Mootify/data/mootify.db` before the app will start.**
Add migrations before this reaches real data.

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

Lidarr cannot fetch a single track. A song request resolves to the album it appears on. See
the lookup flow in `Components/Pages/SearchPage.razor`.

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

## Testing

`tests/Mootify.Tests` uses real SQLite in memory, not the EF InMemory provider — the bugs worth
catching here are translation failures that InMemory would happily let through.

## Not built yet

Drag-reorder in the UI (`PlaylistService.MoveAsync` is ready), the Lidarr webhook receiver,
playback state persistence across sessions, and EF migrations.

## Razor gotcha

HTML entities inside **attribute values** (`placeholder="Search&hellip;"`) render literally —
Razor escapes the ampersand. Use the character itself (`…`, `•`). In element content they're
fine.
