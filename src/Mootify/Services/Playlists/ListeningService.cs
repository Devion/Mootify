using Microsoft.EntityFrameworkCore;
using Mootify.Data;
using Mootify.Services.Teams;

namespace Mootify.Services.Playlists;

/// <summary>
/// Somebody else, on this playlist, right now. Nothing here identifies a file or a path — it is
/// the same information the playlist page already shows, plus a name and a position.
/// </summary>
public sealed record Listener(
    Guid UserId,
    string DisplayName,
    Guid TrackId,
    string TrackTitle,
    string ArtistName,
    double PositionSeconds,
    bool IsPlaying,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// "Listening now": who else is playing this playlist, and what they're on.
///
/// Deliberately not called syncing. Nothing is synchronised — nobody's playback follows anybody
/// else's, and turning this on does not hand over control of your player. It publishes one fact
/// ("I am on track 7 of this list") to the people who can already see the list, and that is the
/// whole feature.
///
/// Three things decide whether a row exists or is visible, and they are checked in this order:
///
/// 1. <b>The broadcaster opted in.</b> <see cref="UserPreference.ShareListening"/> is off by
///    default and is per-account rather than per-device, so switching it off on the website also
///    silences the phone. Switching it off deletes the row rather than hiding it — there is no
///    reason to keep a copy of what somebody just asked us to stop telling people.
/// 2. <b>The broadcaster can still read the playlist.</b> Publishing goes through
///    <see cref="PlaylistService"/> like every other playlist read, so leaving a team stops the
///    broadcast at the next heartbeat rather than leaving a ghost in a list you can no longer see.
/// 3. <b>The reader can read it too</b>, and so can the people in the list they get back — a team
///    playlist only ever shows current members, which is the same rule
///    <see cref="PlaylistAccess.CanRead"/> applies to the playlist itself.
///
/// Liveness is a timestamp rather than a goodbye message. A closed tab, a phone in a tunnel and a
/// killed process all fail to say they've stopped, so clients re-stamp while they play and a row
/// older than <see cref="StaleAfter"/> is simply not shown. Nothing has to be cleaned up on a
/// schedule: a stale row costs one ignored row until that person plays something again.
/// </summary>
public sealed class ListeningService(
    IDbContextFactory<MootifyDbContext> dbFactory,
    PlaylistService playlists,
    ILogger<ListeningService> log)
{
    /// <summary>
    /// How long a heartbeat counts for. Comfortably more than twice the fastest client's
    /// interval (the Android reporter saves every 20 seconds, the website every
    /// <see cref="HeartbeatInterval"/>), so one dropped request doesn't make somebody vanish
    /// mid-song — and short enough that a laptop lid closed on a paused track stops claiming to
    /// be listening.
    /// </summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How often a playing client should re-stamp. Not enforced here — it is what the website's
    /// player throttles itself to, and what makes <see cref="StaleAfter"/> the right size.
    /// </summary>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(20);

    public async Task<bool> IsSharingAsync(Guid userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Preferences
            .AsNoTracking()
            .Where(p => p.UserId == userId)
            .Select(p => p.ShareListening)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Flips the account-wide switch. Turning it off takes the current broadcast down in the same
    /// call: "stop sharing" that leaves the last thing you played on somebody's screen for two
    /// minutes is not what anybody pressing it means.
    /// </summary>
    public async Task<bool> SetSharingAsync(Guid userId, bool enabled, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var preference = await db.Preferences.FirstOrDefaultAsync(p => p.UserId == userId, ct);
        if (preference is null)
        {
            preference = new UserPreference { UserId = userId };
            db.Preferences.Add(preference);
        }

        preference.ShareListening = enabled;

        if (!enabled)
        {
            var session = await db.ListeningSessions.FirstOrDefaultAsync(s => s.UserId == userId, ct);
            if (session is not null) db.ListeningSessions.Remove(session);
        }

        await db.SaveChangesAsync(ct);
        log.LogInformation("{User} turned listening-along {State}", userId, enabled ? "on" : "off");
        return enabled;
    }

    /// <summary>
    /// A heartbeat from whichever client is playing. Called often — every track change, and once
    /// every <see cref="HeartbeatInterval"/> in between — so it does the cheapest thing that can
    /// answer "should this be visible": one read of the preference, and nothing else if it's off.
    ///
    /// Returns whether anything is now being broadcast, so a caller can stop asking.
    /// </summary>
    public async Task<bool> PublishAsync(
        Guid userId,
        Guid? playlistId,
        Guid? trackId,
        double positionSeconds,
        bool isPlaying,
        CancellationToken ct = default)
    {
        // Not playing out of a playlist (an album, an artist, a search) is not a reason to keep
        // showing the last playlist they were on.
        if (playlistId is not { } playlist || trackId is not { } track)
        {
            await StopAsync(userId, ct);
            return false;
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var sharing = await db.Preferences
            .AsNoTracking()
            .Where(p => p.UserId == userId)
            .Select(p => p.ShareListening)
            .FirstOrDefaultAsync(ct);

        if (!sharing)
        {
            // Off since the last heartbeat — SetSharingAsync already removed the row, but a race
            // between the two would otherwise leave one behind for two minutes.
            await RemoveAsync(db, userId, ct);
            return false;
        }

        // The same check the playlist page makes, through the same service, so somebody who left
        // the team mid-song stops broadcasting into it.
        if (!await playlists.CanReadAsync(playlist, userId, ct))
        {
            await RemoveAsync(db, userId, ct);
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        var session = await db.ListeningSessions.FirstOrDefaultAsync(s => s.UserId == userId, ct);

        if (session is null)
        {
            session = new ListeningSession { UserId = userId, StartedAt = now };
            db.ListeningSessions.Add(session);
        }
        else if (session.PlaylistId != playlist)
        {
            // Moving to another playlist is a new sitting, not a continuation of the last one.
            session.StartedAt = now;
        }

        session.PlaylistId = playlist;
        session.TrackId = track;
        session.PositionSeconds = Math.Max(0, positionSeconds);
        session.IsPlaying = isPlaying;
        session.UpdatedAt = now;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // The track or the playlist went away between the read and the write — a scan
            // removing a file, somebody deleting the list. There is nothing to broadcast, and a
            // heartbeat is never worth failing the caller's request for.
            log.LogDebug(ex, "Could not record what {User} is listening to", userId);
            return false;
        }

        return true;
    }

    /// <summary>Stops broadcasting without changing the preference — the player went quiet.</summary>
    public async Task StopAsync(Guid userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await RemoveAsync(db, userId, ct);
    }

    private static async Task RemoveAsync(MootifyDbContext db, Guid userId, CancellationToken ct)
    {
        var session = await db.ListeningSessions.FirstOrDefaultAsync(s => s.UserId == userId, ct);
        if (session is null) return;

        db.ListeningSessions.Remove(session);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Who else is on this playlist. Empty for a playlist <paramref name="viewerId"/> can't read
    /// — the same answer as an empty playlist, so this can't be used to find out that somebody
    /// else's list exists.
    ///
    /// The viewer is left out of their own list. The toggle already tells them whether they are
    /// sharing, and a row saying "you are listening to the thing you are listening to" is the
    /// kind of true statement that makes a feature look broken.
    /// </summary>
    public async Task<List<Listener>> GetForPlaylistAsync(
        Guid playlistId, Guid viewerId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var playlist = await db.Playlists
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == playlistId, ct);

        if (playlist is null) return [];

        var viewerTeams = await TeamService.GetTeamIdsAsync(db, viewerId, ct);
        if (!PlaylistAccess.CanRead(playlist, viewerId, viewerTeams)) return [];

        // A personal playlist has exactly one reader, and they are the viewer — so there is
        // nobody else to list and no query worth running.
        if (playlist.TeamId is not { } teamId) return [];

        var live = DateTimeOffset.UtcNow - StaleAfter;

        // Membership is re-checked here rather than trusted from when the row was written: an
        // hour-old heartbeat from somebody who has since left the team must not still be visible
        // to the team they left.
        return await db.ListeningSessions
            .AsNoTracking()
            .Where(s => s.PlaylistId == playlistId
                     && s.UpdatedAt >= live
                     && s.UserId != viewerId
                     && db.TeamMembers.Any(m => m.TeamId == teamId && m.UserId == s.UserId))
            .OrderByDescending(s => s.UpdatedAt)
            .Select(s => new Listener(
                s.UserId,
                s.User!.DisplayName,
                s.TrackId,
                s.Track!.Title,
                s.Track!.Artist!.Name,
                s.PositionSeconds,
                s.IsPlaying,
                s.StartedAt,
                s.UpdatedAt))
            .ToListAsync(ct);
    }
}
