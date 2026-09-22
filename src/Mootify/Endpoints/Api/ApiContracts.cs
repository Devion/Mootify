using Mootify.Data;
using Mootify.Services.Playlists;

namespace Mootify.Endpoints.Api;

/// <summary>
/// The wire shapes for <c>/api/v1</c>. Records rather than entities, for three reasons that
/// each bit at least once while this was written:
///
/// - <see cref="Track.Path"/> must never leave the server. Serializing the entity would send
///   the absolute path of every file on the share to a phone.
/// - Durations go over the wire as milliseconds. A <see cref="TimeSpan"/> serializes as
///   <c>"00:03:41.2340000"</c>, which every client then has to parse by hand.
/// - URLs are relative. The client already knows which host it authenticated against, and a
///   server behind a reverse proxy does not reliably know its own public name.
/// </summary>
public sealed record ApiUser(Guid Id, string DisplayName, bool IsAdmin);

/// <summary>
/// Handed out at sign-in so the client can light up features rather than discovering them
/// through 404s. <paramref name="ApiVersion"/> is the number to check before assuming a
/// newer endpoint exists.
/// </summary>
public sealed record ApiServerInfo(
    string Name,
    int ApiVersion,
    bool SoulseekConfigured,
    bool RegistrationOpen,
    int MaxPageSize);

public sealed record ApiTokenResponse(
    string Token,
    DateTimeOffset? ExpiresAt,
    ApiUser User,
    ApiServerInfo Server);

public sealed record ApiDeviceToken(
    Guid Id,
    string DeviceName,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastUsedAt,
    DateTimeOffset? ExpiresAt,
    bool IsCurrent);

public sealed record ApiArtist(
    Guid Id,
    string Name,
    string SortName,
    int AlbumCount,
    int TrackCount);

public sealed record ApiAlbum(
    Guid Id,
    string Title,
    Guid ArtistId,
    string ArtistName,
    int? Year,
    int TrackCount,
    long DurationMs,
    string ArtUrl);

public sealed record ApiTrack(
    Guid Id,
    string Title,
    Guid ArtistId,
    string ArtistName,
    Guid AlbumId,
    string AlbumTitle,
    int? Year,
    int TrackNumber,
    int DiscNumber,
    long DurationMs,
    int Bitrate,
    string StreamUrl,
    string ArtUrl);

public sealed record ApiPlaylist(
    Guid Id,
    string Name,
    string? Description,
    int TrackCount,
    long DurationMs,
    Guid? TeamId,
    string? TeamName)
{
    public bool IsTeamPlaylist => TeamId is not null;
}

/// <summary>
/// A playlist with one page of its contents. <paramref name="Items"/> carry their own id because
/// removing one is a per-item operation — the same track can legitimately appear twice.
///
/// <b><paramref name="Items"/> is a page, not the playlist.</b> It used to be the whole thing, and
/// a 200-track playlist was then fetched whole every time a head unit asked for the next twenty
/// rows of it — once per browse page, on a phone, over a mobile connection. <paramref name="Items"/>
/// carries its own total so a client can page without a second call, and
/// <paramref name="TrackCount"/> and <paramref name="DurationMs"/> describe the playlist itself so
/// the header doesn't have to be computed from a page. This is why <see cref="ApiMap.Version"/> is
/// 2: a client built against the old shape fails to parse rather than silently showing the first
/// hundred as though they were all of them.
/// </summary>
public sealed record ApiPlaylistDetail(
    Guid Id,
    string Name,
    string? Description,
    Guid? TeamId,
    string? TeamName,
    bool CanEdit,
    bool CanDelete,
    int TrackCount,
    long DurationMs,
    ApiPage<ApiPlaylistItem> Items)
{
    public bool IsTeamPlaylist => TeamId is not null;
}

/// <summary>
/// Somebody else playing this playlist right now. See
/// <see cref="Mootify.Services.Playlists.ListeningService"/> for who is allowed to appear here —
/// the short version is: they opted in, and they can read the same playlist you can.
/// </summary>
public sealed record ApiListener(
    Guid UserId,
    string DisplayName,
    Guid TrackId,
    string TrackTitle,
    string ArtistName,
    double PositionSeconds,
    bool IsPlaying,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt);

public sealed record ApiPlaylistItem(Guid Id, ApiTrack Track, DateTimeOffset AddedAt);

/// <summary>Paged so a car client never has to hold the whole library in memory.</summary>
public sealed record ApiPage<T>(int Total, int Skip, int Take, List<T> Items);

public sealed record ApiSearchResults(
    List<ApiArtist> Artists,
    List<ApiAlbum> Albums,
    List<ApiTrack> Tracks);

public sealed record ApiLibraryStats(
    int Artists,
    int Albums,
    int Tracks,
    long DurationMs,
    DateTimeOffset? LastAddedAt);

public sealed record ApiSoulseekFile(
    Guid ResultId,
    string Name,
    string Folder,
    string Extension,
    long Size,
    int? BitRate,
    int? BitDepth,
    int? LengthSeconds,
    bool HasFreeUploadSlot,
    int QueueLength);

public sealed record ApiRequest(
    Guid Id,
    RequestKind Kind,
    RequestStatus Status,
    string Query,
    string ArtistName,
    string? AlbumTitle,
    string? TrackTitle,
    Guid? TargetPlaylistId,
    string? TargetPlaylistName,
    string? FailureReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);

public sealed record ApiNotification(
    Guid Id,
    NotificationType Type,
    string Title,
    string Body,
    string? Url,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ReadAt);

/// <summary>
/// Where the user was, so the app can pick up mid-song and the website can too. The queue is
/// track ids rather than resolved tracks: it exists to be handed straight back to a player, and
/// resolving 300 tracks on save would make every skip a write of the whole library.
/// </summary>
public sealed record ApiPlaybackState(
    Guid? CurrentTrackId,
    double PositionSeconds,
    List<Guid> Queue,
    int QueueIndex,
    bool ShuffleEnabled,
    RepeatMode Repeat,
    DateTimeOffset UpdatedAt);

// ---- request bodies ------------------------------------------------------

public sealed record TokenRequest(string Username, string Password, string? DeviceName);

public sealed record CreatePlaylistRequest(string Name, Guid? TeamId);

public sealed record RenamePlaylistRequest(string Name);

public sealed record AddTracksRequest(List<Guid> TrackIds);

/// <summary>
/// <paramref name="ResultId"/> identifies a short-lived, server-cached Soulseek result. The raw
/// peer username and path never need to be trusted from a client.
/// </summary>
public sealed record CreateRequestBody(Guid ResultId, Guid? TargetPlaylistId, string? SearchTerm);

/// <summary>
/// A heartbeat from whichever client is playing.
///
/// <paramref name="SourcePlaylistId"/> and <paramref name="IsPlaying"/> are what let this one call
/// also serve "listening along". The alternative was a second endpoint on its own timer, which is a
/// second thing to keep in step with playback and a second write per tick; the client already
/// reports where it is every twenty seconds, and it already knows which list the current track was
/// picked out of. The server decides whether that becomes visible — the account's
/// <see cref="Data.UserPreference.ShareListening"/> switch is not the client's to interpret.
///
/// Both are optional on the wire: an older client that sends neither saves its playback state
/// exactly as before and broadcasts nothing.
/// </summary>
public sealed record SavePlaybackRequest(
    Guid? CurrentTrackId,
    double PositionSeconds,
    List<Guid>? Queue,
    int QueueIndex,
    bool ShuffleEnabled,
    RepeatMode Repeat,
    Guid? SourcePlaylistId = null,
    bool IsPlaying = false);

public sealed record RecordPlayRequest(Guid TrackId, double SecondsPlayed);

/// <summary>The listening-along switch, which is per account rather than per device.</summary>
public sealed record SetListeningRequest(bool Sharing);

// ---- projection helpers --------------------------------------------------

/// <summary>
/// One place that decides what a track looks like on the wire, including its URLs. Every
/// endpoint that returns tracks goes through here so the app never meets two spellings of the
/// same object.
/// </summary>
public static class ApiMap
{
    /// <summary>
    /// Bumped to 2 when playlist contents became a page — see <see cref="ApiPlaylistDetail"/>.
    /// Clients check this before assuming an endpoint or a shape exists.
    /// </summary>
    public const int Version = 3;

    public static string StreamUrl(Guid trackId) => $"/media/{trackId}";

    /// <summary>
    /// Art is a separate, unauthenticated path — see <c>ArtEndpoints</c> for why the car can't
    /// send a token. Always non-null: the client asks, and gets a 404 if there's nothing, which
    /// is one less branch than a nullable URL.
    /// </summary>
    public static string ArtUrl(Guid albumId) => $"/art/album/{albumId}";

    public static ApiUser User(AppUser user) => new(user.Id, user.DisplayName, user.IsAdmin);

    public static long Ms(long ticks) => (long)TimeSpan.FromTicks(ticks).TotalMilliseconds;

    public static long Ms(TimeSpan span) => (long)span.TotalMilliseconds;

    /// <summary>
    /// A playlist row as the wire sees it. The paging query already projected everything an
    /// <see cref="ApiTrack"/> needs, so this is a rename rather than a second trip to the
    /// database — which is the entire point of <see cref="PlaylistTrackRow"/> carrying the
    /// union of what the website and the API want.
    /// </summary>
    public static ApiTrack Track(PlaylistTrackRow row) => new(
        row.TrackId,
        row.Title,
        row.ArtistId,
        row.ArtistName,
        row.AlbumId,
        row.AlbumTitle,
        row.Year,
        row.TrackNumber,
        row.DiscNumber,
        Ms(row.DurationTicks),
        row.Bitrate,
        StreamUrl(row.TrackId),
        ArtUrl(row.AlbumId));

    public static ApiPlaylistItem PlaylistItem(PlaylistTrackRow row) =>
        new(row.ItemId, Track(row), row.AddedAt);

    public static ApiListener Listener(Mootify.Services.Playlists.Listener listener) => new(
        listener.UserId,
        listener.DisplayName,
        listener.TrackId,
        listener.TrackTitle,
        listener.ArtistName,
        listener.PositionSeconds,
        listener.IsPlaying,
        listener.StartedAt,
        listener.UpdatedAt);
}
