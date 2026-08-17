using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mootify.Configuration;
using Mootify.Data;
using Mootify.Services.Requests;

namespace Mootify.Services.Library;

public sealed record ScanReport(int Added, int Updated, int Removed, int Failed, TimeSpan Elapsed)
{
    public int TouchedCount => Added + Updated + Removed;
    public static readonly ScanReport Empty = new(0, 0, 0, 0, TimeSpan.Zero);
}

/// <summary>
/// Walks the music root, reads tags, upserts. MP3 and FLAC — anything else is transcoded
/// before it gets here, so a file the browser can't play never becomes a Track row.
/// </summary>
public sealed class LibraryScanner(
    IServiceScopeFactory scopeFactory,
    LibraryFiler filer,
    NetworkShareConnector shares,
    IOptionsMonitor<LibraryOptions> options,
    ILogger<LibraryScanner> log)
{
    /// <summary>One scan at a time. A full scan and a post-import scan racing would deadlock on upserts.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    public bool IsScanning { get; private set; }
    public ScanReport? LastReport { get; private set; }
    public DateTimeOffset? LastScanAt { get; private set; }

    public event Action? Changed;

    public async Task<ScanReport> ScanAllAsync(CancellationToken ct = default)
    {
        var root = options.CurrentValue.MusicRoot;

        // Here rather than only in LibraryScanService: the admin's "Rescan library" and the
        // home page's "Scan now" call this directly, so on a credentialled share they were the
        // two entry points that could run against a dropped SMB session — and a scan that can't
        // see the library doesn't fail, it marks all 40,000 tracks absent.
        await shares.EnsureConnectedAsync(ct);

        // Empty the drop folder first, so this scan indexes each dropped file once, at the path
        // it is going to keep. The other order indexes it twice — where it landed, then where
        // it went — and the first row goes absent on the next pass, taking any playlist entry
        // made in between down with it.
        var filed = filer.FileDropFolder(ct);
        await RepointMovedTracksAsync(filed, ct);

        var report = await ScanAsync(root, isFullScan: true, ct);

        // And only now, because a request is completed by appending Track rows to a playlist
        // and those rows don't exist until the scan above has run.
        await MatchImportsToRequestsAsync(filed, ct);

        return report;
    }

    /// <summary>
    /// Follows a file that already had a <see cref="Track"/> row through the move, rather than
    /// letting the scan mark the old path absent and add the new one as a stranger.
    ///
    /// This only matters once, but it matters a lot: a drop folder that predates the filer has
    /// been indexed like anywhere else, so its tracks are in playlists, and absent-plus-new
    /// would empty those playlists on the first scan after the upgrade. Keeping the row keeps
    /// its id, and therefore every playlist entry, play count and playback position on it.
    /// </summary>
    private async Task RepointMovedTracksAsync(FilingReport filed, CancellationToken ct)
    {
        if (filed.Paths.Count == 0 || filer.DropFolder is not { } drop) return;

        try
        {
            using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<MootifyDbContext>();

            // Everything still indexed under the drop folder, which bounds the set to what was
            // just there. StartsWith translates to LIKE, so the case-sensitive comparison that
            // actually decides happens below, in memory.
            var stale = await db.Tracks.Where(t => t.Path.StartsWith(drop)).ToListAsync(ct);
            if (stale.Count == 0) return;

            var moves = filed.Paths.ToDictionary(f => f.From, f => f.To, StringComparer.OrdinalIgnoreCase);

            // Track.Path is unique, so a destination that somehow already has a row is left for
            // the scan to sort out the ordinary way rather than failing the whole SaveChanges.
            var taken = await db.Tracks
                .Where(t => moves.Values.Contains(t.Path))
                .Select(t => t.Path)
                .ToListAsync(ct);

            var occupied = taken.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var moved = 0;

            foreach (var track in stale)
            {
                if (!moves.TryGetValue(track.Path, out var destination)) continue;
                if (!occupied.Add(destination)) continue;

                track.Path = destination;
                track.IsPresent = true;
                moved++;
            }

            if (moved == 0) return;

            await db.SaveChangesAsync(ct);
            log.LogInformation("Followed {Count} already-indexed track(s) out of the import folder", moved);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The scan below still indexes the files at their new homes; the cost of failing
            // here is orphaned playlist entries, not lost music.
            log.LogError(ex, "Could not follow moved tracks out of the import folder");
        }
    }

    /// <summary>
    /// A file somebody fetched by hand answers a request just as well as one Lidarr found. The
    /// matcher is resolved per call rather than injected: it reaches PlaylistService and the
    /// notifier, both scoped, and this scanner is a singleton that outlives all of them.
    /// </summary>
    private async Task MatchImportsToRequestsAsync(FilingReport filed, CancellationToken ct)
    {
        if (filed.Paths.Count == 0) return;

        try
        {
            using var scope = scopeFactory.CreateAsyncScope();
            var matcher = scope.ServiceProvider.GetRequiredService<ImportRequestMatcher>();

            var summary = await matcher.MatchAsync([.. filed.Paths.Select(f => f.To)], ct);

            if (summary.Requests > 0)
            {
                log.LogInformation(
                    "{Requests} request(s) completed by {Tracks} track(s) from the import folder",
                    summary.Requests, summary.Tracks);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The library is scanned either way; failing to close a request is not a reason to
            // report a failed scan.
            log.LogError(ex, "Could not match imported files to open requests");
        }
    }

    /// <summary>
    /// Targeted rescan of one folder, used after a Lidarr import. The request pipeline must not
    /// wait out a full scan of 40,000 files to add one song to a playlist.
    /// </summary>
    public Task<ScanReport> ScanPathAsync(string path, CancellationToken ct = default) =>
        ScanAsync(path, isFullScan: false, ct);

    private async Task<ScanReport> ScanAsync(string path, bool isFullScan, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            log.LogWarning("Scan skipped: Library:MusicRoot is not configured.");
            return ScanReport.Empty;
        }

        if (!Directory.Exists(path))
        {
            log.LogWarning("Scan skipped: {Path} does not exist or is not reachable.", path);
            return ScanReport.Empty;
        }

        await _gate.WaitAsync(ct);
        IsScanning = true;
        Changed?.Invoke();
        var startedAt = DateTimeOffset.UtcNow;
        int added = 0, updated = 0, removed = 0, failed = 0;

        try
        {
            using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<MootifyDbContext>();

            // The drop folder is a mailbox, not part of the library: what's left in it after
            // the filer has run is a file still being copied, and a half-written MP3 has a
            // plausible size and unreadable tags — exactly the shape of a Track row nobody wants.
            var files = EnumerateAudioFiles(path, exclude: filer.DropFolder).ToList();
            log.LogInformation("Scanning {Count} file(s) under {Path}", files.Count, path);

            // Cache lookups per scan; a 5,000-track library would otherwise issue 15,000 queries.
            var artists = await db.Artists.ToDictionaryAsync(a => a.Name, StringComparer.OrdinalIgnoreCase, ct);
            var albums = await db.Albums.ToDictionaryAsync(a => (a.ArtistId, a.Title), ct);
            var existing = await db.Tracks
                .Where(t => t.Path.StartsWith(path))
                .ToDictionaryAsync(t => t.Path, StringComparer.OrdinalIgnoreCase, ct);

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                seen.Add(file.FullName);

                try
                {
                    var fingerprint = Fingerprint(file);

                    if (existing.TryGetValue(file.FullName, out var track))
                    {
                        if (track.FingerPrint == fingerprint && track.IsPresent)
                        {
                            continue; // unchanged
                        }

                        ApplyTags(track, file, db, artists, albums);
                        track.FingerPrint = fingerprint;
                        track.IsPresent = true;
                        updated++;
                    }
                    else
                    {
                        track = new Track
                        {
                            Id = Guid.NewGuid(),
                            Path = file.FullName,
                            AddedAt = DateTimeOffset.UtcNow,
                            FingerPrint = fingerprint,
                        };
                        ApplyTags(track, file, db, artists, albums);
                        db.Tracks.Add(track);
                        existing[file.FullName] = track;
                        added++;
                    }
                }
                catch (Exception ex)
                {
                    failed++;
                    log.LogWarning(ex, "Could not read {File}", file.FullName);
                }
            }

            // Mark vanished files absent rather than deleting them — deleting orphans playlist entries.
            foreach (var (trackPath, track) in existing)
            {
                if (!seen.Contains(trackPath) && track.IsPresent)
                {
                    track.IsPresent = false;
                    removed++;
                }
            }

            await db.SaveChangesAsync(ct);
        }
        finally
        {
            IsScanning = false;
            _gate.Release();
        }

        var report = new ScanReport(added, updated, removed, failed, DateTimeOffset.UtcNow - startedAt);
        LastReport = report;
        LastScanAt = DateTimeOffset.UtcNow;

        log.LogInformation(
            "Scan of {Path} finished in {Elapsed:0.0}s: +{Added} ~{Updated} -{Removed} !{Failed}",
            path, report.Elapsed.TotalSeconds, added, updated, removed, failed);

        Changed?.Invoke();
        return report;
    }

    private void ApplyTags(
        Track track,
        FileInfo file,
        MootifyDbContext db,
        Dictionary<string, Artist> artists,
        Dictionary<(Guid, string), Album> albums)
    {
        string artistName = "Unknown Artist";
        string? albumTitle = null;
        string title = Path.GetFileNameWithoutExtension(file.Name);
        int year = 0, trackNo = 0, disc = 0, bitrate = 0;
        var duration = TimeSpan.Zero;
        string? recordingMbid = null, artistMbid = null, albumMbid = null;

        try
        {
            using var tag = TagLib.File.Create(file.FullName);

            artistName = ReadArtist(tag) ?? artistName;
            albumTitle = Blank(tag.Tag.Album) ? null : tag.Tag.Album;
            title = Blank(tag.Tag.Title) ? title : tag.Tag.Title!;
            year = (int)tag.Tag.Year;
            trackNo = (int)tag.Tag.Track;
            disc = (int)tag.Tag.Disc;
            duration = tag.Properties?.Duration ?? TimeSpan.Zero;
            bitrate = tag.Properties?.AudioBitrate ?? 0;

            recordingMbid = Blank(tag.Tag.MusicBrainzTrackId) ? null : tag.Tag.MusicBrainzTrackId;
            artistMbid = Blank(tag.Tag.MusicBrainzArtistId) ? null : tag.Tag.MusicBrainzArtistId;
            albumMbid = Blank(tag.Tag.MusicBrainzReleaseGroupId) ? null : tag.Tag.MusicBrainzReleaseGroupId;
        }
        catch (Exception ex)
        {
            // A broken tag shouldn't cost us the file — fall back to the filename.
            log.LogDebug(ex, "Tag read failed for {File}, falling back to filename", file.FullName);
        }

        // No album tag is the common case in a real library, not the exception. Falling back
        // to "Unknown Album" would collapse every untagged track by an artist into one bucket;
        // the folder layout Lidarr already writes (Artist/Album/track.mp3) says what the album
        // is, so use it.
        albumTitle ??= AlbumFromFolder(file, artistName);

        if (!artists.TryGetValue(artistName, out var artist))
        {
            artist = new Artist
            {
                Id = Guid.NewGuid(),
                Name = artistName,
                SortName = SortName(artistName),
                MusicBrainzId = artistMbid,
            };
            db.Artists.Add(artist);
            artists[artistName] = artist;
        }
        else if (artist.MusicBrainzId is null && artistMbid is not null)
        {
            artist.MusicBrainzId = artistMbid;
        }

        var albumKey = (artist.Id, albumTitle);
        if (!albums.TryGetValue(albumKey, out var album))
        {
            album = new Album
            {
                Id = Guid.NewGuid(),
                Title = albumTitle,
                ArtistId = artist.Id,
                Year = year > 0 ? year : null,
                MusicBrainzId = albumMbid,
            };
            db.Albums.Add(album);
            albums[albumKey] = album;
        }
        else
        {
            album.Year ??= year > 0 ? year : null;
            album.MusicBrainzId ??= albumMbid;
        }

        track.Title = title;
        track.ArtistId = artist.Id;
        track.AlbumId = album.Id;
        track.Duration = duration;
        track.Bitrate = bitrate;
        track.TrackNumber = trackNo;
        track.DiscNumber = disc;
        track.RecordingMusicBrainzId = recordingMbid;
        track.FileSize = file.Length;
        track.FileModifiedAt = file.LastWriteTimeUtc;
    }

    /// <summary>
    /// Infers an album from the containing folder when the file has no album tag.
    /// Returns "Unknown Album" when the folder can't mean an album: the file sits directly
    /// in the music root, or its folder is the artist's own folder (Artist/track.mp3), where
    /// the folder name would just repeat the artist.
    /// </summary>
    private string AlbumFromFolder(FileInfo file, string artistName)
    {
        var folder = file.Directory;
        if (folder is null) return "Unknown Album";

        var root = options.CurrentValue.MusicRoot;
        if (!string.IsNullOrWhiteSpace(root) && SamePath(folder.FullName, root))
        {
            return "Unknown Album";
        }

        if (string.Equals(folder.Name, artistName, StringComparison.OrdinalIgnoreCase))
        {
            return "Unknown Album";
        }

        return folder.Name;
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(
            a.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            b.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// What the library indexes. MP3 plays everywhere; FLAC plays in every current browser
    /// and is what Lidarr actually fetches, so refusing it left most of the music invisible.
    /// Anything else still needs converting before it can be a library entry.
    /// </summary>
    public static readonly string[] IndexedExtensions = [".mp3", ".flac"];

    public static bool IsIndexable(string path) =>
        IndexedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static IEnumerable<FileInfo> EnumerateAudioFiles(string root, string? exclude = null)
    {
        var opts = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System,
        };

        var excluded = string.IsNullOrWhiteSpace(exclude)
            ? null
            : exclude.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

        // One walk with a filter rather than a walk per pattern — over SMB the enumeration
        // is the expensive part, not the comparison.
        return new DirectoryInfo(root)
            .EnumerateFiles("*", opts)
            .Where(f => IsIndexable(f.Name))
            .Where(f => excluded is null || !f.FullName.StartsWith(excluded, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Size + mtime, not a content hash. Hashing 40,000 files on every scan is not worth it.</summary>
    private static string Fingerprint(FileInfo f) =>
        string.Create(CultureInfo.InvariantCulture, $"{f.Length:x}-{f.LastWriteTimeUtc.Ticks:x}");

    private static string SortName(string name) =>
        name.StartsWith("The ", StringComparison.OrdinalIgnoreCase) ? name[4..] : name;

    private static bool Blank(string? s) => string.IsNullOrWhiteSpace(s);

    /// <summary>
    /// The artist as the file actually names them.
    ///
    /// TagLib splits ID3v2.3 TPE1 on "/" — a leftover from ID3v1, where that was the
    /// multi-artist convention. So "AC/DC" arrives as ["AC", "DC"], and taking the first
    /// element files the whole discography under "AC". Rejoining with the same separator
    /// gives back exactly what the tag said, whether that was one band with a slash in its
    /// name or two artists.
    ///
    /// ID3v2.4 and Vorbis comments have real multi-value fields, so there the parts are
    /// genuinely separate names and a slash would be a lie.
    /// </summary>
    internal static string? ReadArtist(TagLib.File file)
    {
        var separator = file.GetTag(TagLib.TagTypes.Id3v2) is TagLib.Id3v2.Tag { Version: < 4 }
            ? "/"
            : "; ";

        return Join(file.Tag.AlbumArtists, separator) ?? Join(file.Tag.Performers, separator);
    }

    private static string? Join(string[]? values, string separator)
    {
        var parts = values?.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).ToArray();
        return parts is { Length: > 0 } ? string.Join(separator, parts) : null;
    }
}
