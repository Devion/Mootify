using Mootify.Data;

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
    bool LidarrConfigured,
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
/// A playlist with its contents. <paramref name="Items"/> carry their own id because removing
/// one is a per-item operation — the same track can legitimately appear twice.
/// </summary>
public sealed record ApiPlaylistDetail(
    Guid Id,
    string Name,
    string? Description,
    Guid? TeamId,
    string? TeamName,
    bool CanEdit,
    bool CanDelete,
    List<ApiPlaylistItem> Items)
{
    public bool IsTeamPlaylist => TeamId is not null;
}

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

/// <summary>An album Lidarr could fetch — not in the library yet, so it has no track ids.</summary>
public sealed record ApiRemoteAlbum(
    string? MusicBrainzId,
    string Title,
    string ArtistName,
    string? ArtistMusicBrainzId,
    int? Year,
    string? AlbumType,
    string? CoverUrl,
    bool AlreadyInLibrary);

public sealed record ApiRemoteTrack(int Position, string Title, string? RecordingId, long? DurationMs);

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
/// <paramref name="SearchTerm"/> is the term that produced the album on the client's screen.
/// Lidarr has no "get by MBID" for something it hasn't adopted yet, so if the server's memory of
/// the search has expired it re-runs that term and matches on the MBID rather than failing.
/// </summary>
public sealed record CreateRequestBody(
    string? AlbumMusicBrainzId,
    RequestKind Kind,
    string? TrackTitle,
    string? RecordingMusicBrainzId,
    Guid? TargetPlaylistId,
    string? SearchTerm);

public sealed record SavePlaybackRequest(
    Guid? CurrentTrackId,
    double PositionSeconds,
    List<Guid>? Queue,
    int QueueIndex,
    bool ShuffleEnabled,
    RepeatMode Repeat);

public sealed record RecordPlayRequest(Guid TrackId, double SecondsPlayed);

// ---- projection helpers --------------------------------------------------

/// <summary>
/// One place that decides what a track looks like on the wire, including its URLs. Every
/// endpoint that returns tracks goes through here so the app never meets two spellings of the
/// same object.
/// </summary>
public static class ApiMap
{
    public const int Version = 1;

    public static string StreamUrl(Guid trackId) => $"/media/{trackId}";

    /// <summary>
    /// Art is a separate, unauthenticated path — see <c>ArtEndpoints</c> for why the car can't
    /// send a token. Always non-null: the client asks, and gets a 404 if there's nothing, which
    /// is one less branch than a nullable URL.
    /// </summary>
    public static string ArtUrl(Guid albumId) => $"/art/album/{albumId}";

    public static ApiUser User(AppUser user) => new(user.Id, user.DisplayName, user.IsAdmin);

    public static long Ms(long ticks) => (long)TimeSpan.FromTicks(ticks).TotalMilliseconds;
}
