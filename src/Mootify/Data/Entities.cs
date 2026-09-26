using System.ComponentModel.DataAnnotations;

namespace Mootify.Data;

public sealed class AppUser
{
    public Guid Id { get; set; }

    /// <summary>The username as typed at registration, preserved for display.</summary>
    [MaxLength(64)]
    public string DisplayName { get; set; } = "";

    /// <summary>Lowercased/trimmed username. Unique — this is what login matches on.</summary>
    [MaxLength(64)]
    public string NormalizedName { get; set; } = "";

    /// <summary>PBKDF2 via <c>PasswordHasher&lt;AppUser&gt;</c>. Never anything else, never logged.</summary>
    [MaxLength(256)]
    public string PasswordHash { get; set; } = "";

    /// <summary>Admins get the management panel. The first account created is one.</summary>
    public bool IsAdmin { get; set; }

    /// <summary>
    /// A banned user can't sign in, and any cookie they already hold is rejected on the next
    /// request. Their playlists and history stay put — banning isn't deleting.
    /// </summary>
    public bool IsBanned { get; set; }

    public bool ApprovalPending { get; set; }

    [MaxLength(512)]
    public string? BanReason { get; set; }

    public DateTimeOffset? BannedAt { get; set; }

    /// <summary>
    /// Set when an admin resets the password to a one-time one, cleared the moment the user
    /// picks their own. While it's set the whole site funnels to /password and the API refuses
    /// to issue a device token, so the only thing the account can do is finish the change.
    /// </summary>
    public bool MustChangePassword { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }

    public UserPreference? Preference { get; set; }
}

/// <summary>
/// Runtime settings an admin can change without editing a file or restarting. Config in
/// mootify.json stays for things that are true of the deployment (paths, API keys); this is
/// for things that are true of the moment.
/// </summary>
public sealed class AppSetting
{
    [MaxLength(64)]
    public string Key { get; set; } = "";

    [MaxLength(1024)]
    public string Value { get; set; } = "";
}

public sealed class UserPreference
{
    public Guid UserId { get; set; }
    public AppUser? User { get; set; }

    /// <summary>Cowbell on/off. Mirrored into localStorage so it applies before the circuit connects.</summary>
    public bool SoundEnabled { get; set; } = true;

    public bool DuckMusicWhilePlaying { get; set; } = true;

    public double Volume { get; set; } = 0.8;

    /// <summary>
    /// Keep playing when a playlist runs out, using suggestions from what this account has
    /// listened to before (<see cref="Services.Recommendations.TasteService"/>).
    ///
    /// On by default, and that is safe because the suggester refuses to guess: with too little
    /// history it returns nothing and the music simply stops the way it always did. So this can
    /// turn itself on usefully once there is something to go on, rather than needing a setting
    /// nobody knew to look for.
    /// </summary>
    public bool AutoContinue { get; set; } = true;
    public bool NormalizeVolume { get; set; }

    /// <summary>
    /// Whether playing from a playlist tells everybody else who can see that playlist what you're
    /// on. Off by default: what you listen to is nobody's business until you say it is.
    ///
    /// One switch for the account rather than one per device, because the alternative is a phone
    /// that quietly keeps broadcasting after the website was told to stop. Every client reports
    /// which playlist it is playing from and the server decides, in
    /// <see cref="Services.Playlists.ListeningService"/>, whether that becomes visible.
    /// </summary>
    public bool ShareListening { get; set; }
}

/// <summary>
/// A long-lived credential for a device that can't hold a browser cookie — the Android app.
/// One row per device, so losing a phone revokes that phone and nothing else.
///
/// The stored value is a plain SHA-256 of the secret, not PBKDF2. The secret is 32 random
/// bytes we generated, so there is no password to grind — a slow hash would only mean
/// stretching every streamed range request through PBKDF2 for no gain.
/// </summary>
public sealed class ApiToken
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }
    public AppUser? User { get; set; }

    /// <summary>Base64 SHA-256 of the secret. Indexed unique — this is the lookup key.</summary>
    [MaxLength(64)]
    public string TokenHash { get; set; } = "";

    /// <summary>Whatever the device called itself at sign-in, so the revoke list is readable.</summary>
    [MaxLength(128)]
    public string DeviceName { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// Stamped lazily — once per <see cref="Auth.ApiTokenService.LastUsedResolution"/>, not on
    /// every request. A car seeking through an album is hundreds of range requests, and none of
    /// them need to write to the database.
    /// </summary>
    public DateTimeOffset LastUsedAt { get; set; }

    /// <summary>Null means it doesn't expire on its own. See <c>Api:TokenLifetime</c>.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>Set rather than deleted, so a revoked device stays visible until it's cleared out.</summary>
    public DateTimeOffset? RevokedAt { get; set; }

    public bool IsActive(DateTimeOffset now) =>
        RevokedAt is null && (ExpiresAt is null || ExpiresAt > now);
}

public sealed class Artist
{
    public Guid Id { get; set; }

    [MaxLength(512)]
    public string Name { get; set; } = "";

    [MaxLength(512)]
    public string SortName { get; set; } = "";

    /// <summary>MusicBrainz artist ID, retained for metadata matching.</summary>
    [MaxLength(64)]
    public string? MusicBrainzId { get; set; }

    public List<Album> Albums { get; } = [];
    public List<Track> Tracks { get; } = [];
}

public sealed class Album
{
    public Guid Id { get; set; }

    [MaxLength(512)]
    public string Title { get; set; } = "";

    public int? Year { get; set; }

    /// <summary>MusicBrainz release-group ID, retained for metadata matching.</summary>
    [MaxLength(64)]
    public string? MusicBrainzId { get; set; }

    public Guid ArtistId { get; set; }
    public Artist? Artist { get; set; }

    /// <summary>Relative path under the music root of an embedded/adjacent cover, if one was found.</summary>
    [MaxLength(1024)]
    public string? CoverPath { get; set; }

    public List<Track> Tracks { get; } = [];
}

public sealed class Track
{
    public Guid Id { get; set; }

    /// <summary>Absolute path on disk. Never rendered to the browser.</summary>
    [MaxLength(1024)]
    public string Path { get; set; } = "";

    /// <summary>Cheap identity for change detection: size + mtime + path. Not a content hash.</summary>
    [MaxLength(64)]
    public string FingerPrint { get; set; } = "";

    [MaxLength(512)]
    public string Title { get; set; } = "";

    /// <summary>
    /// Stored as ticks, not as a TimeSpan. SQLite persists TimeSpan as TEXT, which means
    /// SUM() over durations can't be translated and every "how long is this playlist"
    /// query silently falls back to loading the whole table. A long aggregates in SQL.
    /// </summary>
    public long DurationTicks { get; set; }

    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public TimeSpan Duration
    {
        get => TimeSpan.FromTicks(DurationTicks);
        set => DurationTicks = value.Ticks;
    }

    public int Bitrate { get; set; }
    public int TrackNumber { get; set; }
    public int DiscNumber { get; set; }

    [MaxLength(64)]
    public string? RecordingMusicBrainzId { get; set; }

    /// <summary>
    /// The genre tag, as the file spells it. Free text and frequently absent or nonsense —
    /// "Rock", "rock", "Alt. Rock" and "(17)" are all things real files say — so it is a signal
    /// for <see cref="Services.Recommendations.TasteService"/> to weigh, never a category the
    /// library is organised by. Null means the file didn't have one.
    /// </summary>
    [MaxLength(128)]
    public string? Genre { get; set; }

    public Guid ArtistId { get; set; }
    public Artist? Artist { get; set; }

    public Guid AlbumId { get; set; }
    public Album? Album { get; set; }

    public long FileSize { get; set; }
    public DateTimeOffset AddedAt { get; set; }
    public DateTimeOffset FileModifiedAt { get; set; }

    /// <summary>Set false when a scan no longer finds the file, rather than deleting the row and orphaning playlists.</summary>
    public bool IsPresent { get; set; } = true;
}

public enum TeamRole
{
    Member,
    Owner,
}

public enum TeamJoinPolicy
{
    /// <summary>Anyone signed in joins instantly. No owner involvement.</summary>
    Open,

    /// <summary>Joining creates an application an owner has to accept.</summary>
    RequestToJoin,

    /// <summary>No way in except an invitation from an owner.</summary>
    InviteOnly,
}

public sealed class Team
{
    public Guid Id { get; set; }

    [MaxLength(128)]
    public string Name { get; set; } = "";

    [MaxLength(512)]
    public string? Description { get; set; }

    /// <summary>
    /// Defaults to RequestToJoin: an owner who bothers to make a team usually wants to know
    /// who's in it, and Open is one click away on the management page for those who don't.
    /// </summary>
    public TeamJoinPolicy JoinPolicy { get; set; } = TeamJoinPolicy.RequestToJoin;

    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public List<TeamMember> Members { get; } = [];
    public List<Playlist> Playlists { get; } = [];
    public List<TeamMembershipRequest> PendingRequests { get; } = [];
}

public enum MembershipRequestKind
{
    /// <summary>Team → user. The invited person accepts.</summary>
    Invite,

    /// <summary>User → team. An owner accepts.</summary>
    Application,
}

/// <summary>
/// A pending link between a user and a team, waiting on whichever side didn't create it.
/// One table for both directions because the shape and the accept path are identical —
/// only <see cref="Kind"/> decides who is allowed to say yes.
/// Resolved rows are deleted rather than kept: a declined invite that lingers would block
/// the owner from ever inviting that person again.
/// </summary>
public sealed class TeamMembershipRequest
{
    public Guid Id { get; set; }

    public Guid TeamId { get; set; }
    public Team? Team { get; set; }

    /// <summary>The person who would become a member.</summary>
    public Guid UserId { get; set; }
    public AppUser? User { get; set; }

    public MembershipRequestKind Kind { get; set; }

    /// <summary>Who sent it — the inviting owner, or the applicant themselves.</summary>
    public Guid CreatedByUserId { get; set; }

    [MaxLength(512)]
    public string? Message { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// Subscription to a team. Joining is open — anyone signed in can subscribe to any team.
/// This is a household install where everyone already trusts each other; an invite flow
/// would be ceremony protecting nothing.
/// </summary>
public sealed class TeamMember
{
    public Guid TeamId { get; set; }
    public Team? Team { get; set; }

    public Guid UserId { get; set; }
    public AppUser? User { get; set; }

    public TeamRole Role { get; set; } = TeamRole.Member;

    public DateTimeOffset JoinedAt { get; set; }
}

public enum PlaylistVisibility
{
    Private,
    Team,
}

public sealed class Playlist
{
    public Guid Id { get; set; }

    [MaxLength(256)]
    public string Name { get; set; } = "";

    [MaxLength(1024)]
    public string? Description { get; set; }

    /// <summary>Exactly one of OwnerUserId / TeamId is set. See PlaylistService for the rules.</summary>
    public Guid? OwnerUserId { get; set; }
    public AppUser? Owner { get; set; }

    public Guid? TeamId { get; set; }
    public Team? Team { get; set; }

    public PlaylistVisibility Visibility { get; set; } = PlaylistVisibility.Private;

    public bool IsTeamPlaylist => TeamId is not null;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public List<PlaylistItem> Items { get; } = [];
}

public sealed class PlaylistItem
{
    public Guid Id { get; set; }

    public Guid PlaylistId { get; set; }
    public Playlist? Playlist { get; set; }

    public Guid TrackId { get; set; }
    public Track? Track { get; set; }

    /// <summary>Insert = midpoint of neighbours, so reordering touches one row instead of renumbering the list.</summary>
    public double SortKey { get; set; }

    public Guid? AddedByUserId { get; set; }
    public DateTimeOffset AddedAt { get; set; }

    /// <summary>Set when auto-added by a completed request. Makes the append idempotent.</summary>
    public Guid? RequestId { get; set; }
}

public enum RequestKind
{
    Track,
    Album,
    Artist,
}

public enum RequestStatus
{
    Pending,
    Searching,
    Downloading,
    Imported,
    Transcoding,
    Available,
    NotFound,
    Failed,
}

public sealed class Request
{
    public Guid Id { get; set; }

    public Guid RequesterId { get; set; }
    public AppUser? Requester { get; set; }

    public RequestKind Kind { get; set; }
    public RequestStatus Status { get; set; } = RequestStatus.Pending;

    /// <summary>What the user typed, kept for diagnosing a bad match.</summary>
    [MaxLength(512)]
    public string Query { get; set; } = "";

    [MaxLength(512)]
    public string ArtistName { get; set; } = "";

    [MaxLength(512)]
    public string? AlbumTitle { get; set; }

    [MaxLength(512)]
    public string? TrackTitle { get; set; }

    [MaxLength(64)]
    public string? ArtistMusicBrainzId { get; set; }

    /// <summary>Release-group MBID when the request originated from metadata-backed import data.</summary>
    [MaxLength(64)]
    public string? AlbumMusicBrainzId { get; set; }

    /// <summary>Recording MBID. This is what picks the right track out of the album once it lands.</summary>
    [MaxLength(64)]
    public string? RecordingMusicBrainzId { get; set; }

    /// <summary>The slskd batch uses the request id for new rows; nullable for upgraded databases.</summary>
    public Guid? SoulseekBatchId { get; set; }

    [MaxLength(512)]
    public string? SoulseekUsername { get; set; }

    [MaxLength(2048)]
    public string? SoulseekFilename { get; set; }

    /// <summary>Attempts to find another peer after the selected peer went offline.</summary>
    public int OfflineRecoveryAttempts { get; set; }
    public DateTimeOffset? NextOfflineRecoveryAt { get; set; }

    /// <summary>Captured at request time so completion is silent and automatic. Null = don't add anywhere.</summary>
    public Guid? TargetPlaylistId { get; set; }
    public Playlist? TargetPlaylist { get; set; }

    // Bare ids survive deletion: completion must never recreate a removed entry.
    public Guid? ReplacementItemId { get; set; }
    public Guid? ReplacementTrackId { get; set; }

    [MaxLength(1024)]
    public string? FailureReason { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    public bool IsOpen => Status is not (RequestStatus.Available or RequestStatus.NotFound or RequestStatus.Failed);
}

public enum NotificationType
{
    RequestAvailable,
    RequestFailed,
    Info,
    TeamInvite,
    TeamJoinRequest,
    TeamAccepted,
}

public sealed class Notification
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }
    public AppUser? User { get; set; }

    public Guid? RequestId { get; set; }

    public NotificationType Type { get; set; }

    [MaxLength(256)]
    public string Title { get; set; } = "";

    [MaxLength(1024)]
    public string Body { get; set; } = "";

    /// <summary>Where the bell navigates on click.</summary>
    [MaxLength(512)]
    public string? Url { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
}

public enum RepeatMode
{
    Off,
    All,
    One,
}

public sealed class PlaybackState
{
    public Guid UserId { get; set; }
    public AppUser? User { get; set; }

    public Guid? CurrentTrackId { get; set; }
    public double PositionSeconds { get; set; }

    /// <summary>Ordered track ids, JSON. The queue is small enough that a column beats a table.</summary>
    public string QueueJson { get; set; } = "[]";

    public int QueueIndex { get; set; }

    /// <summary>Seeded Fisher-Yates: store the seed, walk the permutation, never reshuffle on next.</summary>
    public int ShuffleSeed { get; set; }
    public bool ShuffleEnabled { get; set; }
    public RepeatMode Repeat { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class PlayEvent
{
    public long Id { get; set; }
    public Guid UserId { get; set; }
    public Guid TrackId { get; set; }
    public DateTimeOffset PlayedAt { get; set; }

    /// <summary>Distinguishes a real listen from a skip.</summary>
    public double SecondsPlayed { get; set; }
}

/// <summary>
/// What one person is playing, out of one playlist, right now — so everybody else who can see
/// that playlist can see it too.
///
/// Keyed by user, not by playlist: you are only ever listening to one thing, and a row per
/// (user, playlist) would leave a trail of ghosts behind somebody who browsed four playlists in
/// a minute. Moving to another playlist rewrites this row.
///
/// <b>Liveness is a timestamp, not a teardown.</b> A closed browser tab, a phone that drove into
/// a tunnel and a process that was killed all fail to say goodbye, and a "stop" message that has
/// to arrive is a stop message that eventually doesn't — so a session counts as live only while
/// <see cref="UpdatedAt"/> is recent (<see cref="Services.Playlists.ListeningService.StaleAfter"/>),
/// and every client re-stamps it while it plays.
/// </summary>
public sealed class ListeningSession
{
    public Guid UserId { get; set; }
    public AppUser? User { get; set; }

    /// <summary>The playlist being played from. This is what decides who is allowed to see the row.</summary>
    public Guid PlaylistId { get; set; }
    public Playlist? Playlist { get; set; }

    public Guid TrackId { get; set; }
    public Track? Track { get; set; }

    public double PositionSeconds { get; set; }

    /// <summary>False is still worth broadcasting — "paused on track 4" is information.</summary>
    public bool IsPlaying { get; set; }

    /// <summary>When this listener joined <i>this</i> playlist, so "listening for 20 minutes" is answerable.</summary>
    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// One short note from a user to whoever runs the server. No threads, no replies, no categories —
/// the whole point is that leaving a suggestion costs one sentence and one click.
///
/// <b><see cref="Message"/> is plain ASCII and is enforced to be</b>, in
/// <see cref="Services.Ideas.IdeaText"/>, at the only door it can come in through. It is rendered
/// as text everywhere (Razor escapes, and nothing here ever becomes a <c>MarkupString</c>), so the
/// ASCII rule is a second wall rather than the only one — but it is the wall that also stops a
/// right-to-left override or a zero-width joiner making the admin's list say something other than
/// what was typed.
/// </summary>
public sealed class Idea
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }
    public AppUser? User { get; set; }

    [MaxLength(Services.Ideas.IdeaText.MaxLength)]
    public string Message { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// Set when an admin has dealt with it. Archived rather than deleted so the person who wrote
    /// it can still see that it was read — and deleting is a separate, deliberate button.
    /// </summary>
    public DateTimeOffset? ArchivedAt { get; set; }

    [MaxLength(280)]
    public string? AdminReply { get; set; }

    public bool IsArchived => ArchivedAt is not null;
}
