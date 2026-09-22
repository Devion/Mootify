using System.ComponentModel.DataAnnotations;

namespace Mootify.Configuration;

public sealed class AuthOptions
{
    public const string Section = "Auth";

    /// <summary>
    /// The username the very first account must use. On an empty database Mootify refuses to
    /// do anything else until this account exists and has a password.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    [MaxLength(64)]
    public string AdminUsername { get; set; } = "mooadmin";

    /// <summary>
    /// Length only. Composition rules ("one capital, one symbol") push people towards
    /// Password1! and are worse than a longer minimum.
    /// </summary>
    [Range(6, 128)]
    public int MinPasswordLength { get; set; } = 8;

    /// <summary>
    /// Whether new people can sign themselves up. Seeded from here on first run, then owned
    /// by the admin panel — <see cref="Data.AppSetting"/> is the live value.
    /// </summary>
    public bool AllowRegistration { get; set; } = true;

    /// <summary>
    /// Wrong passwords before the account (and the address) is locked out. Each failure
    /// before that adds a second of delay; this is the wall at the end of the corridor.
    /// </summary>
    [Range(3, 100)]
    public int MaxFailedAttempts { get; set; } = 10;

    /// <summary>How long a lockout lasts. Fixed from the moment it trips — retrying during
    /// the lockout doesn't extend it, so the wait is predictable.</summary>
    public TimeSpan LockoutDuration { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>How long a login cookie stays valid.</summary>
    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromDays(30);
}

/// <summary>
/// The token-authenticated JSON API the Android app talks to. Nothing here turns the API off:
/// it authenticates the same accounts the website does, so an install that has no phones simply
/// never issues a token.
/// </summary>
public sealed class ApiOptions
{
    public const string Section = "Api";

    /// <summary>
    /// How long a device token lives. Long by default — a car stereo asking for a password is
    /// worse than useless, and revoking a device is one click on the account page.
    /// Zero or less means it never expires on its own.
    /// </summary>
    public TimeSpan TokenLifetime { get; set; } = TimeSpan.FromDays(365);

    /// <summary>
    /// Tokens kept per account. Signing in again from the same phone mints a new row, so
    /// without a cap the table would grow for the lifetime of the install; the oldest is
    /// dropped instead.
    /// </summary>
    [Range(1, 100)]
    public int MaxTokensPerUser { get; set; } = 10;

    /// <summary>
    /// Ceiling on any paged list. A car client asking for the whole library in one response is
    /// how you find out the head unit has 200MB of RAM.
    /// </summary>
    [Range(10, 2000)]
    public int MaxPageSize { get; set; } = 500;

    /// <summary>
    /// Where extracted cover art is cached. Empty puts it next to the transcode cache, under
    /// the app's data folder — never inside the music root, which the scanner walks.
    /// </summary>
    /// <summary>
    /// Where extracted cover art is cached. Empty puts it under the app's data folder — never
    /// inside the music root, which the scanner walks.
    ///
    /// Art is served at whatever size the file embeds it. Resizing would mean an image library
    /// on the server, and the clients that ask for art (Coil on Android, the browser) already
    /// downsample to the slot they're drawing into.
    /// </summary>
    public string ArtCacheDirectory { get; set; } = "";
}

public sealed class SoulseekOptions
{
    public const string Section = "Soulseek";

    /// <summary>Blank disables remote search and download without disabling the music library.</summary>
    public string BaseUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";
    [Range(5, 60)] public int SearchTimeoutSeconds { get; set; } = 60;
    [Range(1, 10000)] public int FileLimit { get; set; } = 1000;
    [Range(1, 1000)] public int ResponseLimit { get; set; } = 100;
    [Range(0, 1000000)] public int MaximumPeerQueueLength { get; set; } = 100;
    [Range(1, 500)] public int MaxResults { get; set; } = 100;
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Relative to slskd's configured downloads directory.</summary>
    public string DownloadDestination { get; set; } = "Mootify";

    /// <summary>The same slskd downloads directory as Mootify sees it. Blank uses Library:MusicRoot.</summary>
    public string LocalDownloadRoot { get; set; } = "";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(ApiKey);
}

public sealed class MusicBrainzOptions
{
    public const string Section = "MusicBrainz";

    public string BaseUrl { get; set; } = "https://musicbrainz.org/ws/2/";

    /// <summary>
    /// MusicBrainz requires an identifying User-Agent with a way to contact you, and throttles
    /// or blocks requests without one. Put a real address or repository here.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string Contact { get; set; } = "https://github.com/mootify";

    /// <summary>
    /// Their published limit is one request per second, averaged. Going faster gets an IP
    /// banned, and there is no appeal worth the afternoon.
    /// </summary>
    public TimeSpan MinRequestInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Tracklists don't change. Cache them and stop asking.</summary>
    public TimeSpan CacheDuration { get; set; } = TimeSpan.FromHours(24);
}

public sealed class LibraryOptions
{
    public const string Section = "Library";

    /// <summary>Root folder containing the library and, normally, slskd's downloads directory.</summary>
    [Required(AllowEmptyStrings = false)]
    public string MusicRoot { get; set; } = "";

    /// <summary>
    /// Credentials for a UNC share that needs them. Leave blank when the share is open, or
    /// when the account Mootify runs under already has access.
    ///
    /// Only used on Windows — it opens an SMB session for the process, after which the normal
    /// file APIs work against the path unchanged. On Linux the mount belongs to the OS
    /// (fstab, or a Docker volume), not to us.
    /// </summary>
    public string? Username { get; set; }

    /// <summary>
    /// Also readable from the <c>Library__Password</c> environment variable or a Docker
    /// secret, so it needn't be written into the file at all.
    /// </summary>
    public string? Password { get; set; }

    /// <summary>Optional. <c>DOMAIN\user</c> in <see cref="Username"/> works just as well.</summary>
    public string? Domain { get; set; }

    public bool HasCredentials => !string.IsNullOrWhiteSpace(Username);

    /// <summary>Full rescan cadence. The watcher handles the fast path; this catches what it drops.</summary>
    public TimeSpan FullScanInterval { get; set; } = TimeSpan.FromHours(6);

    public bool WatchFileSystem { get; set; } = true;

    /// <summary>Wait for writes to settle before scanning a changed file.</summary>
    public TimeSpan WatchDebounce { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Run a scan as soon as the app starts.</summary>
    public bool ScanOnStartup { get; set; } = true;

    /// <summary>
    /// A drop folder inside the music root. Everything in it is filed under
    /// <c>Artist/Album</c> at the start of each scan, and it is never itself indexed — see
    /// <see cref="Services.Library.LibraryFiler"/>. Blank turns the whole thing off.
    /// </summary>
    public string ImportFolder { get; set; } = "import";

    /// <summary>
    /// Where a file whose artist can't be worked out ends up, relative to the music root. It
    /// stays visible in the library rather than being hidden — the point is that somebody can
    /// find it and tag it, not that it disappears tidily.
    /// </summary>
    public string UnsortedFolder { get; set; } = "generic";

    /// <summary>
    /// File the drop folder before each scan. Off leaves it alone *and* still excludes it from
    /// indexing, which is what you want while sorting a batch by hand.
    /// </summary>
    public bool FileImportsOnScan { get; set; } = true;

    /// <summary>
    /// Where <see cref="Services.Library.LibraryOrganizer"/> puts the copy of a song it merged
    /// away, relative to the music root. Like the drop folder it is never indexed — a quarantine
    /// the scanner walks is not a quarantine, and the file would come straight back as a new
    /// duplicate on the next pass.
    ///
    /// It is a move rather than a delete because the pass can be wrong: two versions of a song
    /// that really were different are recoverable from a folder and not from a delete. Blank
    /// leaves duplicate files where they are, which also means Organize can't merge them.
    /// </summary>
    public string DuplicatesFolder { get; set; } = "duplicates";

    /// <summary>
    /// Ceiling on one uploaded file (<see cref="Services.Library.TrackUploadService"/>). 100MB
    /// takes a long FLAC without complaint and still refuses somebody's holiday video, and the
    /// limit is counted against bytes that have arrived rather than a declared length.
    /// </summary>
    [Range(1_000_000, 2_000_000_000)]
    public long MaxUploadBytes { get; set; } = 100 * 1024 * 1024;

    /// <summary>
    /// Files in one upload. An album is a dozen; the number is here so a whole discography
    /// arrives as a handful of batches somebody watches rather than one that times out. Anything
    /// past it is reported as refused, never silently dropped.
    /// </summary>
    [Range(1, 500)]
    public int MaxUploadFiles { get; set; } = 50;
}

public sealed class RequestOptions
{
    public const string Section = "Requests";

    /// <summary>
    /// No approval workflow by design — this is a local install. The quota exists to protect
    /// the disk from a queued discography, not to police anyone.
    /// </summary>
    [Range(1, 1000)]
    public int MaxOpenPerUser { get; set; } = 5;
}

public sealed class NotificationOptions
{
    public const string Section = "Notifications";

    public string SoundFile { get; set; } = "/audio/cowbell.mp3";

    public bool DefaultSoundEnabled { get; set; } = true;

    /// <summary>Duck the music instead of talking over it. See <c>player.js</c>.</summary>
    public bool DuckMusicWhilePlaying { get; set; } = true;

    [Range(0.0, 1.0)]
    public double DuckToVolume { get; set; } = 0.3;
}

public sealed class TranscodeOptions
{
    public const string Section = "Transcode";

    public string FfmpegPath { get; set; } = "ffmpeg";

    public string Bitrate { get; set; } = "320k";

    /// <summary>Keep the original by default; disk is cheaper than re-downloading.</summary>
    public bool DeleteSourceAfterTranscode { get; set; }

    /// <summary>
    /// Where on-demand MP3s live for browsers that can't decode the original. Kept out of the
    /// music root so the scanner never indexes a copy of a track it already has.
    /// </summary>
    public string CacheDirectory { get; set; } = "";

    /// <summary>Transcoding competes with playback for CPU. Keep this small.</summary>
    [Range(1, 16)]
    public int MaxConcurrent { get; set; } = 1;
}
