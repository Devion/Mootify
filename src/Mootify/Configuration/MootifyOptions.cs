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

public sealed class LidarrOptions
{
    public const string Section = "Lidarr";

    /// <summary>Blank disables every Lidarr-backed feature instead of failing at startup.</summary>
    public string BaseUrl { get; set; } = "";

    public string ApiKey { get; set; } = "";

    public int QualityProfileId { get; set; } = 1;
    public int MetadataProfileId { get; set; } = 1;
    public string RootFolderPath { get; set; } = "";
    public bool SearchOnAdd { get; set; } = true;

    /// <summary>Reconciliation interval. Webhooks are the fast path; this poll is the source of truth.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(10);

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

    /// <summary>Root folder that Lidarr writes into and the scanner reads from.</summary>
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

    /// <summary>Keep the FLAC by default; disk is cheaper than re-downloading.</summary>
    public bool DeleteSourceAfterTranscode { get; set; }

    /// <summary>Transcoding competes with playback for CPU. Keep this small.</summary>
    [Range(1, 8)]
    public int MaxConcurrent { get; set; } = 1;
}
