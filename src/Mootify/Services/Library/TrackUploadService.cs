using Microsoft.Extensions.Options;
using Mootify.Configuration;
using Mootify.Services.Requests;

namespace Mootify.Services.Library;

/// <summary>
/// What happened to one uploaded file. Every value except <see cref="Filed"/> means the file did
/// not reach the library, and <c>Detail</c> says why in words the person who chose the file can
/// act on — "no album tag" is fixable, "failed" is not.
/// </summary>
public enum UploadOutcome
{
    /// <summary>Landed under <c>Artist/Album</c> and is being indexed.</summary>
    Filed,

    /// <summary>Not an extension the library indexes. Nothing was written.</summary>
    WrongFormat,

    /// <summary>Readable, but the artist or album tag is missing or says "unknown".</summary>
    MissingTags,

    /// <summary>Bigger than <c>Library:MaxUploadBytes</c>.</summary>
    TooBig,

    /// <summary>Couldn't be read as audio at all, or the move failed.</summary>
    Failed,
}

/// <summary>
/// One file's verdict. <paramref name="Artist"/> and <paramref name="Album"/> are what the tags
/// actually said, so a rejection can show the person what the file claims to be rather than
/// making them go and open it in a tagger to find out.
/// </summary>
public sealed record UploadedFile(
    string FileName,
    UploadOutcome Outcome,
    string? Detail = null,
    string? Artist = null,
    string? Album = null,
    string? RelativePath = null)
{
    public bool Ok => Outcome == UploadOutcome.Filed;
}

/// <summary>
/// The whole batch. <paramref name="Scan"/> and <paramref name="Matched"/> are the two things
/// that happen after the files land, reported rather than assumed: a file on disk that no scan
/// picked up is invisible, and that is not something the person uploading can see from a
/// success message.
/// </summary>
public sealed record UploadReport(
    List<UploadedFile> Files,
    ScanReport Scan,
    ImportMatchSummary Matched)
{
    public static readonly UploadReport Empty =
        new([], ScanReport.Empty, ImportMatchSummary.Nothing);

    public int Filed => Files.Count(f => f.Ok);
    public int Rejected => Files.Count(f => !f.Ok);
}

/// <summary>
/// Adding music from the browser: pick files, they go into the library under
/// <c>Artist/Album</c>, and anything that was waiting on them stops waiting.
///
/// This is the drop folder with the front door attached, and it deliberately runs the same
/// sequence for the same reasons (<c>LibraryFiler</c> → <c>LibraryScanner</c> →
/// <c>ImportRequestMatcher</c>): <b>file, then index, then match requests</b>. Indexing before
/// filing indexes the temp copy; matching before indexing has no <c>Track</c> rows to match
/// against. The naming rules — what a tag may become as a folder name, what happens when two
/// files are both <c>01 - Intro.mp3</c> — are <see cref="LibraryFiler"/>'s, called rather than
/// re-implemented, because two opinions about where a file belongs is how a library splits in half.
///
/// <b>The one rule that is not the drop folder's: an upload must be tagged.</b> The filer puts an
/// untagged file in the catch-all folder, which is right for a mailbox somebody is working
/// through and wrong here — the person is standing in front of the machine and can fix the tag
/// or pick a different file. So a missing artist or album is a refusal with a reason, and nothing
/// is written. The folders themselves are created on demand, so the first file by a new artist
/// makes the shelf it goes on.
///
/// Nothing here trusts the file name. It is a string from a browser: it decides nothing about
/// where the file goes (the tags do) and it is put through <see cref="LibraryFiler.SafeName"/>
/// before it becomes part of a path, which is what stops <c>../../</c> being a valid album.
/// </summary>
public sealed class TrackUploadService(
    LibraryScanner scanner,
    ImportRequestMatcher matcher,
    IOptionsMonitor<LibraryOptions> options,
    ILogger<TrackUploadService> log)
{
    /// <summary>
    /// What may be uploaded: what the library indexes, and nothing else.
    ///
    /// The drop folder also takes the formats the transcode sweep can rescue, because it is
    /// asynchronous and something will get to them. An upload answers immediately, and "it's in,
    /// but not yet, and only if ffmpeg is installed" is not an answer — so an OGG is refused here
    /// with a sentence saying to drop it in the import folder instead.
    /// </summary>
    public static IReadOnlyList<string> AcceptedExtensions => LibraryScanner.IndexedExtensions;

    /// <summary>For the file picker's <c>accept</c> attribute: ".mp3,.flac".</summary>
    public static string AcceptAttribute => string.Join(',', AcceptedExtensions);

    /// <summary>One file, opened as a stream, plus whatever the browser called it.</summary>
    public sealed record Incoming(string FileName, Stream Content);

    // Clamped rather than trusted: LibraryOptions isn't data-annotation validated at startup, and
    // a zero in the file would turn "upload" into "refuse everything" with no error anywhere.
    public long MaxBytes => Math.Clamp(options.CurrentValue.MaxUploadBytes, 1_000_000, 2_000_000_000);

    public int MaxFiles => Math.Clamp(options.CurrentValue.MaxUploadFiles, 1, 500);

    /// <summary>
    /// Files a batch and then does the two things that make it visible: a targeted rescan of the
    /// folders it wrote into, and a pass over open requests.
    ///
    /// The rescan is per destination folder rather than a full scan — an album is one or two
    /// folders, and walking 40,000 files to notice ten new ones is the wait this whole feature
    /// exists to avoid. It is also why the folders are collected as the files land rather than
    /// derived afterwards.
    /// </summary>
    public async Task<UploadReport> UploadAsync(
        Guid userId, IReadOnlyList<Incoming> files, CancellationToken ct = default)
    {
        if (files.Count == 0) return UploadReport.Empty;

        var root = options.CurrentValue.MusicRoot;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            log.LogWarning("Upload refused: Library:MusicRoot ({Root}) is not reachable", root);

            return new UploadReport(
                [.. files.Select(f => new UploadedFile(
                    f.FileName, UploadOutcome.Failed, "The music library isn't reachable right now."))],
                ScanReport.Empty,
                ImportMatchSummary.Nothing);
        }

        var results = new List<UploadedFile>(files.Count);
        var landed = new List<string>();
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files.Take(MaxFiles))
        {
            ct.ThrowIfCancellationRequested();

            var result = await FileOneAsync(file, root, ct);
            results.Add(result);

            if (result.Ok && result.RelativePath is { } relative)
            {
                var full = Path.GetFullPath(Path.Combine(root, relative));
                landed.Add(full);
                folders.Add(Path.GetDirectoryName(full)!);
            }
        }

        // Anything past the cap never opened a stream, so it is refused rather than silently
        // dropped — a batch that quietly files 50 of 80 files is the worst of both answers.
        foreach (var extra in files.Skip(MaxFiles))
        {
            results.Add(new UploadedFile(
                extra.FileName,
                UploadOutcome.Failed,
                $"Only {MaxFiles} files at a time — send this one in the next batch."));
        }

        if (landed.Count == 0) return new UploadReport(results, ScanReport.Empty, ImportMatchSummary.Nothing);

        var scan = await RescanAsync(folders, ct);
        var matched = await MatchRequestsAsync(landed, ct);

        log.LogInformation(
            "{User} uploaded {Filed} of {Total} file(s); scan +{Added} ~{Updated}, {Requests} request(s) completed",
            userId, landed.Count, files.Count, scan.Added, scan.Updated, matched.Requests);

        return new UploadReport(results, scan, matched);
    }

    // ---- one file --------------------------------------------------------

    private async Task<UploadedFile> FileOneAsync(Incoming file, string root, CancellationToken ct)
    {
        var extension = Path.GetExtension(file.FileName);

        if (!AcceptedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return new UploadedFile(
                file.FileName,
                UploadOutcome.WrongFormat,
                $"Only {string.Join(" and ", AcceptedExtensions.Select(e => e.TrimStart('.').ToUpperInvariant()))} "
                + "here. Anything else goes in the import folder, where it gets converted first.");
        }

        // Spooled to disk before anything is read from it: TagLib needs to seek, a browser upload
        // stream can't, and the size limit has to be enforced against bytes that have actually
        // arrived rather than against a Content-Length somebody chose.
        var temp = Path.Combine(Path.GetTempPath(), "mootify-upload", Guid.NewGuid().ToString("n") + extension);
        Directory.CreateDirectory(Path.GetDirectoryName(temp)!);

        try
        {
            if (!await SpoolAsync(file.Content, temp, MaxBytes, ct))
            {
                return new UploadedFile(
                    file.FileName,
                    UploadOutcome.TooBig,
                    $"Bigger than {MaxBytes / (1024 * 1024)}MB.");
            }

            var (artist, album, readable) = ReadTags(temp);

            if (!readable)
            {
                return new UploadedFile(
                    file.FileName, UploadOutcome.Failed, "That isn't a music file this can read.");
            }

            // SafeFolder is the arbiter, not a null check: it is also what turns "Unknown Artist"
            // into nothing, so a file the filer would have dumped in the catch-all is refused here
            // by the same rule rather than by a second opinion about what "untagged" means.
            var artistFolder = LibraryFiler.SafeFolder(artist);
            var albumFolder = LibraryFiler.SafeFolder(album);

            if (artistFolder is null || albumFolder is null)
            {
                return new UploadedFile(
                    file.FileName,
                    UploadOutcome.MissingTags,
                    Missing(artistFolder is null, albumFolder is null),
                    artist,
                    album);
            }

            var safeName = LibraryFiler.SafeName(file.FileName) ?? ("track" + extension);
            var relative = Path.Combine(artistFolder, albumFolder, safeName);
            var target = Path.Combine(root, relative);

            // The folders for a new artist or a new album are made here — that is the whole of
            // "create the directory if it doesn't exist", and it is one call because the names
            // above are already known to be safe.
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            var free = LibraryFiler.Unique(target);
            if (free is null)
            {
                return new UploadedFile(
                    file.FileName, UploadOutcome.Failed, "There are already too many files by that name.", artist, album);
            }

            File.Move(temp, free);

            return new UploadedFile(
                file.FileName,
                UploadOutcome.Filed,
                null,
                artist,
                album,
                Path.GetRelativePath(root, free));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not take the upload {File}", file.FileName);
            return new UploadedFile(file.FileName, UploadOutcome.Failed, "Something went wrong saving that one.");
        }
        finally
        {
            // Only still there if it never moved. A failed upload must not leave a copy of
            // somebody's music in the temp folder.
            TryDelete(temp);
        }
    }

    /// <summary>
    /// Copies at most <paramref name="maxBytes"/>, and answers false the moment there is more —
    /// so an oversized file costs one buffer past the limit rather than however much the client
    /// felt like sending.
    /// </summary>
    private static async Task<bool> SpoolAsync(Stream source, string path, long maxBytes, CancellationToken ct)
    {
        var buffer = new byte[81920];
        long written = 0;

        await using (var destination = File.Create(path))
        {
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                written += read;
                if (written > maxBytes) return false;

                await destination.WriteAsync(buffer.AsMemory(0, read), ct);
            }
        }

        return written > 0;
    }

    /// <summary>
    /// Artist and album as the file names them, plus whether it was audio at all. The artist
    /// comes from <see cref="LibraryScanner.ReadArtist"/> rather than <c>Tag.FirstPerformer</c>
    /// so that a band with a slash in its name survives — see that method for the ID3v2.3 story.
    /// </summary>
    private (string? Artist, string? Album, bool Readable) ReadTags(string path)
    {
        try
        {
            using var tag = TagLib.File.Create(path);
            return (LibraryScanner.ReadArtist(tag), tag.Tag.Album, true);
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "No usable tags on the uploaded file {Path}", path);
            return (null, null, false);
        }
    }

    private static string Missing(bool artist, bool album) => (artist, album) switch
    {
        (true, true) => "No artist or album tag — both are needed to know where it goes.",
        (true, false) => "No artist tag — that's the folder it would live in.",
        _ => "No album tag — that's the folder inside the artist's.",
    };

    // ---- afterwards ------------------------------------------------------

    /// <summary>
    /// One targeted scan per folder written into. A failure is logged and not thrown: the files
    /// are on disk either way, and the six-hourly full scan will find them — reporting the upload
    /// as failed when the music is safely filed would be the wrong lie.
    /// </summary>
    private async Task<ScanReport> RescanAsync(IEnumerable<string> folders, CancellationToken ct)
    {
        var total = ScanReport.Empty;

        foreach (var folder in folders)
        {
            try
            {
                var report = await scanner.ScanPathAsync(folder, ct);
                total = new ScanReport(
                    total.Added + report.Added,
                    total.Updated + report.Updated,
                    // A targeted scan only ever looks at one folder, so "removed" here means a
                    // file that genuinely went missing from it, not the rest of the library.
                    total.Removed + report.Removed,
                    total.Failed + report.Failed,
                    total.Elapsed + report.Elapsed);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Could not rescan {Folder} after an upload", folder);
            }
        }

        return total;
    }

    /// <summary>
    /// The same pass the drop folder gets, for the same reason: somebody who gave up waiting for
    /// Soulseek and fetched the file themselves has answered their own request, and the request
    /// should close, land in the playlist it was aimed at and ring the cowbell — not sit there
    /// saying "nothing found" next to music that is now in the library.
    /// </summary>
    private async Task<ImportMatchSummary> MatchRequestsAsync(List<string> landed, CancellationToken ct)
    {
        try
        {
            return await matcher.MatchAsync(landed, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Could not match uploaded files to open requests");
            return ImportMatchSummary.Nothing;
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Could not remove the temporary upload {Path}", path);
        }
    }
}
