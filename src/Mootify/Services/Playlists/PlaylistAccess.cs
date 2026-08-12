using Mootify.Data;

namespace Mootify.Services.Playlists;

/// <summary>
/// Who may do what to a playlist. Every read and write in the app funnels through these
/// three methods — scattering <c>if (playlist.OwnerId == userId)</c> through Razor
/// components is how you miss one, and with teams there are now four cases to miss.
///
/// Pure functions over a pre-loaded membership set rather than methods that query: the
/// caller loads the user's team ids once, and the rules stay trivially testable.
/// </summary>
public static class PlaylistAccess
{
    /// <summary>Owner of a personal playlist, or a member of the owning team.</summary>
    public static bool CanRead(Playlist playlist, Guid userId, IReadOnlySet<Guid> userTeamIds) =>
        playlist.TeamId is { } teamId
            ? userTeamIds.Contains(teamId)
            : playlist.OwnerUserId == userId;

    /// <summary>
    /// Same as read. A team playlist that members can see but not edit would defeat the
    /// point of sharing one; if that changes, TeamMember.Role is where the distinction goes.
    /// </summary>
    public static bool CanEdit(Playlist playlist, Guid userId, IReadOnlySet<Guid> userTeamIds) =>
        CanRead(playlist, userId, userTeamIds);

    /// <summary>
    /// Deleting is not editing. Anyone can add tracks to a team playlist; only a team owner
    /// can destroy one, because that throws away everybody else's work.
    /// </summary>
    public static bool CanDelete(
        Playlist playlist,
        Guid userId,
        IReadOnlySet<Guid> userTeamIds,
        IReadOnlySet<Guid> ownedTeamIds) =>
        playlist.TeamId is { } teamId
            ? ownedTeamIds.Contains(teamId)
            : playlist.OwnerUserId == userId;
}
